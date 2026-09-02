using System.Collections.Concurrent;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Windows.Display;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Hdr2Sdr.Helper;

/// <summary>
/// Keeps a Desktop Duplication session open for one output and mirrors every presented frame into a "latest"
/// GPU texture. All Direct3D work happens on this loop's own thread; other threads post work items.
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

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _latest;
    private Texture2DDescription _latestDesc;
    private volatile bool _hasLatest;
    private DateTime _latestUtc;
    private bool _frozen;

    private readonly ConcurrentQueue<Action> _work = new();
    private volatile bool _pendingFreeze, _pendingUnfreeze;

    public bool HasFrame => _hasLatest;
    public DateTime LatestUtc => _latestUtc;
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

    /// <summary>Stop updating "latest" so the frame at this instant can be shown and captured.</summary>
    public void Freeze() => _pendingFreeze = true;

    public void Unfreeze() => _pendingUnfreeze = true;

    /// <summary>Copies the latest (frozen) frame to the CPU as linear scRGB in desktop orientation. Null on timeout or no frame.</summary>
    public FloatImage? Snapshot(int timeoutMs = 2000)
    {
        FloatImage? value = null;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            try { value = SnapshotOnLoop(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs)) { _log.Warn($"{Output.DeviceName}: snapshot timed out (loop status: {Status})"); return null; }
        if (error != null) { _log.Warn($"{Output.DeviceName}: snapshot failed: {error.Message}"); return null; }
        return value;
    }

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
                    try
                    {
                        ApplyPendingFlags();
                        if (!_frozen && (info.LastPresentTime != 0 || info.AccumulatedFrames > 0))
                        {
                            using ID3D11Texture2D tex = resource!.QueryInterface<ID3D11Texture2D>();
                            EnsureLatest(tex.Description);
                            _context!.CopyResource(_latest!, tex);
                            _hasLatest = true;
                            _latestUtc = DateTime.UtcNow;
                        }
                    }
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
        if (_pendingFreeze) { _pendingFreeze = false; _frozen = true; }
        if (_pendingUnfreeze) { _pendingUnfreeze = false; _frozen = false; }
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

    private FloatImage? SnapshotOnLoop()
    {
        if (!_hasLatest || _latest == null || _device == null || _context == null) return null;
        var stagingDesc = new Texture2DDescription(_latestDesc.Format, _latestDesc.Width, _latestDesc.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using ID3D11Texture2D staging = _device.CreateTexture2D(stagingDesc);
        _context.CopyResource(staging, _latest);
        MappedSubresource map = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            return FrameConverter.ToScRgb(map.DataPointer, (int)map.RowPitch, (int)_latestDesc.Width, (int)_latestDesc.Height, _latestDesc.Format, Output.Rotation);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
    }
}
