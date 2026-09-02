using System.Collections.Concurrent;
using System.Diagnostics;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Windows.Display;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Keeps a Desktop Duplication session open for one output and mirrors every presented frame into a
/// "latest" GPU texture. All Direct3D work happens on this loop's own thread: other threads post work
/// items (snapshot, ring crops) that the loop runs between frames, so the device is never used from two
/// threads and a slow request can never wedge the loop.
/// </summary>
public sealed class CaptureLoop : IDisposable
{
    private static readonly Format[] PreferredFormats = { Format.R16G16B16A16_Float, Format.R10G10B10A2_UNorm, Format.B8G8R8A8_UNorm };
    private static readonly FeatureLevel[] FeatureLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
    private const uint AcquireTimeoutMs = 50;

    public OutputHandle Output { get; }
    private readonly HelperLog _log;
    private Thread? _thread;
    private volatile bool _running;

    // Loop-thread state
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _latest;
    private Texture2DDescription _latestDesc;
    private volatile bool _hasLatest;
    private DateTime _latestUtc;
    private OutduplPointerPosition _pointer;
    private CursorShape? _cursor;
    private bool _frozen;
    private readonly List<(ID3D11Texture2D Tex, DateTime Utc)> _ring = new();
    private bool _recording;
    private DateTime _recordUntil, _recordStart;
    private int _ringMax;
    private readonly List<(ID3D11Texture2D Tex, DateTime Utc)> _history = new();

    // Cross-thread requests
    private readonly ConcurrentQueue<Action> _work = new();
    private volatile int _pendingRecordMs = -1, _pendingRecordFrames;
    private volatile bool _pendingUnfreeze;
    private volatile int _historyMs;
    private volatile int _ringCount, _historyCount;

    public long FrameBudgetBytes { get; set; } = 1L << 30;
    public int HistoryMs { get => _historyMs; set => _historyMs = Math.Max(0, value); }
    public bool HasFrame => _hasLatest;
    public DateTime LatestUtc => _latestUtc;
    public int RingCount => _ringCount;
    public int HistoryCount => _historyCount;
    public string Status { get; private set; } = "starting";

    public CaptureLoop(OutputHandle output, HelperLog log)
    {
        Output = output;
        _log = log;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "capture " + Output.DeviceName };
        _thread.Start();
    }

    // ---------------- requests from other threads ----------------

    /// <summary>Freeze "latest" now and record the following window into the ring (history frames come first).</summary>
    public void BeginRecording(int windowMs, int maxFrames)
    {
        _pendingRecordFrames = maxFrames;
        _pendingRecordMs = Math.Max(0, windowMs);   // picked up before the next frame is copied
    }

    /// <summary>Let "latest" follow the desktop again (the ring keeps recording until its window ends).</summary>
    public void Unfreeze() => _pendingUnfreeze = true;

    public void ClearRing() => Post(() => ClearRingLocked());

    /// <summary>Copies the latest frame to the CPU, converts it to scRGB and draws the cursor when asked. Null on timeout.</summary>
    public (FloatImage Image, Core.Snapshot.CursorRect? Cursor)? Snapshot(bool includeCursor, int timeoutMs = 5000)
        => Run(() => SnapshotOnLoop(includeCursor), timeoutMs, out var r) ? r : null;

    /// <summary>Region crops (output-local rectangle) from every ring frame; offsets are ms after the trigger. Empty on timeout.</summary>
    public List<(int OffsetMs, FloatImage Crop)> RingCrops(int x, int y, int w, int h, int timeoutMs = 8000)
        => Run(() => RingCropsOnLoop(x, y, w, h), timeoutMs, out var r) && r != null ? r : new List<(int, FloatImage)>();

    private void Post(Action a) => _work.Enqueue(a);

    private bool Run<T>(Func<T> f, int timeoutMs, out T? result)
    {
        T? value = default;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            try { value = f(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs))
        {
            _log.Warn($"{Output.DeviceName}: request timed out after {timeoutMs} ms (loop status: {Status})");
            result = default;
            return false;
        }
        if (error != null)
        {
            _log.Warn($"{Output.DeviceName}: request failed: {error.Message}");
            result = default;
            return false;
        }
        result = value;
        return true;
    }

    // ---------------- loop thread ----------------

    private void Run()
    {
        while (_running)
        {
            try
            {
                if (_dup == null) Open();
                ApplyPendingFlags();
                Result hr = _dup!.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo info, out IDXGIResource? resource);
                if (hr.Success)
                {
                    try { HandleFrame(info, resource!); }
                    finally { resource?.Dispose(); _dup.ReleaseFrame(); }
                    Status = "ok";
                }
                else if (hr != Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    Status = $"duplication lost ({hr}), reopening";
                    _log.Warn($"{Output.DeviceName}: AcquireNextFrame {hr}; reopening");
                    Close();
                    Thread.Sleep(500);
                }
                if (_recording && DateTime.UtcNow > _recordUntil) _recording = false;
                DrainWork();
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                _log.Error($"{Output.DeviceName}: {e.Message}");
                Close();
                DrainWork();
                Thread.Sleep(1000);
            }
        }
        Close();
        DrainWork();
    }

    private void ApplyPendingFlags()
    {
        int ms = _pendingRecordMs;
        if (ms >= 0)
        {
            _pendingRecordMs = -1;
            ClearRingLocked();
            _frozen = true;
            _recordStart = DateTime.UtcNow;
            _recordUntil = _recordStart.AddMilliseconds(ms);
            _ringMax = Math.Max(0, _pendingRecordFrames) + _history.Count;
            _ring.AddRange(_history);
            _history.Clear();
            _historyCount = 0;
            _ringCount = _ring.Count;
            _recording = ms > 0 && _pendingRecordFrames > 0;
        }
        if (_pendingUnfreeze) { _pendingUnfreeze = false; _frozen = false; }
        if (_historyMs == 0 && _history.Count > 0) { foreach (var h in _history) h.Tex.Dispose(); _history.Clear(); _historyCount = 0; }
    }

    private void HandleFrame(OutduplFrameInfo info, IDXGIResource resource)
    {
        if (info.LastMouseUpdateTime != 0) _pointer = info.PointerPosition;
        if (info.PointerShapeBufferSize > 0) ReadPointerShape(info.PointerShapeBufferSize);
        ApplyPendingFlags();   // a hotkey during the acquire must freeze before this frame is copied
        bool presented = info.LastPresentTime != 0 || info.AccumulatedFrames > 0;
        if (!presented) return;
        using ID3D11Texture2D tex = resource.QueryInterface<ID3D11Texture2D>();
        DateTime now = DateTime.UtcNow;
        long bytes = FrameBytes(tex.Description);
        if (!_frozen)
        {
            EnsureLatest(tex.Description);
            _context!.CopyResource(_latest!, tex);
            _hasLatest = true;
            _latestUtc = now;
        }
        if (_recording)
        {
            if (now <= _recordUntil && _ring.Count < _ringMax && (_ring.Count + 1) * bytes <= FrameBudgetBytes)
            {
                _ring.Add((CopyOf(tex), now));
                _ringCount = _ring.Count;
            }
            else _recording = false;
        }
        else if (_historyMs > 0 && !_frozen)
        {
            DateTime cutoff = now.AddMilliseconds(-_historyMs);
            while (_history.Count > 0 && (_history[0].Utc < cutoff || (_history.Count + 1) * bytes > FrameBudgetBytes))
            {
                _history[0].Tex.Dispose();
                _history.RemoveAt(0);
            }
            _history.Add((CopyOf(tex), now));
            _historyCount = _history.Count;
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out Action? a))
        {
            try { a(); }
            catch (Exception e) { _log.Warn($"{Output.DeviceName}: work item failed: {e.Message}"); }
        }
    }

    private void Open()
    {
        D3D11.D3D11CreateDevice(Output.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels, out _device, out _context).CheckError();
        _dup = Output.Output.DuplicateOutput1(_device, PreferredFormats);
        _hasLatest = false;
        _log.Info($"{Output.DeviceName}: duplication open");
    }

    private void Close()
    {
        ClearRingLocked();
        foreach (var h in _history) h.Tex.Dispose();
        _history.Clear();
        _historyCount = 0;
        _hasLatest = false;
        _latest?.Dispose(); _latest = null;
        _dup?.Dispose(); _dup = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    private void EnsureLatest(Texture2DDescription frameDesc)
    {
        if (_latest != null && _latestDesc.Width == frameDesc.Width && _latestDesc.Height == frameDesc.Height && _latestDesc.Format == frameDesc.Format) return;
        _latest?.Dispose();
        _latestDesc = new Texture2DDescription(frameDesc.Format, frameDesc.Width, frameDesc.Height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
        _latest = _device!.CreateTexture2D(_latestDesc);
    }

    private static long FrameBytes(Texture2DDescription d) => (long)d.Width * d.Height * (d.Format == Format.R16G16B16A16_Float ? 8 : 4);

    private ID3D11Texture2D CopyOf(ID3D11Texture2D tex)
    {
        Texture2DDescription d = tex.Description;
        var copy = _device!.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        _context!.CopyResource(copy, tex);
        return copy;
    }

    private void ClearRingLocked()
    {
        _recording = false;
        foreach (var r in _ring) r.Tex.Dispose();
        _ring.Clear();
        _ringCount = 0;
    }

    private unsafe void ReadPointerShape(uint size)
    {
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            Result hr = _dup!.GetFramePointerShape(size, (IntPtr)p, out uint required, out OutduplPointerShapeInfo info);
            if (hr.Failure) return;
            var type = (CursorShapeType)info.Type;
            if (type is not (CursorShapeType.Monochrome or CursorShapeType.Color or CursorShapeType.MaskedColor)) return;
            _cursor = new CursorShape(type, (int)info.Width, (int)info.Height, (int)info.Pitch, buffer[..(int)required], info.HotSpot.X, info.HotSpot.Y);
        }
    }

    private (FloatImage Image, Core.Snapshot.CursorRect? Cursor)? SnapshotOnLoop(bool includeCursor)
    {
        if (!_hasLatest || _latest == null || _device == null || _context == null) return null;
        var stagingDesc = new Texture2DDescription(_latestDesc.Format, _latestDesc.Width, _latestDesc.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using ID3D11Texture2D staging = _device.CreateTexture2D(stagingDesc);
        _context.CopyResource(staging, _latest);
        MappedSubresource map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        FloatImage img;
        try
        {
            img = FrameConverter.ToScRgb(map.DataPointer, (int)map.RowPitch, (int)_latestDesc.Width, (int)_latestDesc.Height, _latestDesc.Format, Output.Rotation);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
        Core.Snapshot.CursorRect? rect = null;
        if (includeCursor && _cursor != null && _pointer.Visible)
        {
            float white = Output.Hdr ? Output.SdrWhiteNits / 80f : 1f;
            CursorCompositor.Draw(img, _cursor, _pointer.Position.X, _pointer.Position.Y, white);
            rect = new Core.Snapshot.CursorRect(_pointer.Position.X - _cursor.HotspotX, _pointer.Position.Y - _cursor.HotspotY, _cursor.Width, _cursor.VisibleHeight);
        }
        return (img, rect);
    }

    private List<(int OffsetMs, FloatImage Crop)> RingCropsOnLoop(int x, int y, int w, int h)
    {
        var result = new List<(int, FloatImage)>();
        if (_device == null || _context == null || _ring.Count == 0 || _latest == null) return result;
        int texW = (int)_latestDesc.Width, texH = (int)_latestDesc.Height;
        int turns = Output.Rotation switch { ModeRotation.Rotate90 => 1, ModeRotation.Rotate180 => 2, ModeRotation.Rotate270 => 3, _ => 0 };
        if (!RotationMath.DesktopRectToTexture(turns, texW, texH, x, y, w, h, out int tx, out int ty, out int tw, out int th)) return result;
        if (tx < 0 || ty < 0 || tw <= 0 || th <= 0 || tx + tw > texW || ty + th > texH) return result;
        var sw = Stopwatch.StartNew();
        var stagingDesc = new Texture2DDescription(_latestDesc.Format, (uint)tw, (uint)th, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using ID3D11Texture2D staging = _device.CreateTexture2D(stagingDesc);
        foreach (var (tex, utc) in _ring)
        {
            _context.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(tx, ty, 0, tx + tw, ty + th, 1));
            MappedSubresource map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                FloatImage crop = FrameConverter.ToScRgb(map.DataPointer, (int)map.RowPitch, tw, th, _latestDesc.Format, Output.Rotation);
                result.Add(((int)(utc - _recordStart).TotalMilliseconds, crop));
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        _log.Info($"{Output.DeviceName}: {result.Count} ring crops {w}x{h} in {sw.ElapsedMilliseconds} ms");
        return result;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
    }
}
