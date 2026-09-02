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
/// "latest" GPU texture. While frozen, "latest" is left alone so a snapshot can be taken from it.
/// Tracks the hardware pointer position and shape so the snapshot can include the cursor.
/// </summary>
public sealed class CaptureLoop : IDisposable
{
    private static readonly Format[] PreferredFormats = { Format.R16G16B16A16_Float, Format.R10G10B10A2_UNorm, Format.B8G8R8A8_UNorm };
    private static readonly FeatureLevel[] FeatureLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

    public OutputHandle Output { get; }
    private readonly HelperLog _log;
    private readonly object _gpu = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _frozen;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _latest;
    private Texture2DDescription _latestDesc;
    private bool _hasLatest;
    private DateTime _latestUtc;

    private OutduplPointerPosition _pointer;
    private CursorShape? _cursor;

    public bool Frozen { get => _frozen; set => _frozen = value; }
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

    private void Run()
    {
        while (_running)
        {
            try
            {
                if (_dup == null) Open();
                Result hr = _dup!.AcquireNextFrame(200, out OutduplFrameInfo info, out IDXGIResource? resource);
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                if (hr.Failure)
                {
                    Status = $"duplication lost ({hr}), reopening";
                    _log.Warn($"{Output.DeviceName}: AcquireNextFrame {hr}; reopening");
                    Close();
                    Thread.Sleep(500);
                    continue;
                }
                try
                {
                    if (info.LastMouseUpdateTime != 0) _pointer = info.PointerPosition;
                    if (info.PointerShapeBufferSize > 0) ReadPointerShape(info.PointerShapeBufferSize);
                    if (!_frozen && (info.LastPresentTime != 0 || info.AccumulatedFrames > 0))
                    {
                        using ID3D11Texture2D tex = resource!.QueryInterface<ID3D11Texture2D>();
                        lock (_gpu)
                        {
                            EnsureLatest(tex.Description);
                            _context!.CopyResource(_latest!, tex);
                            _hasLatest = true;
                            _latestUtc = DateTime.UtcNow;
                        }
                    }
                }
                finally
                {
                    resource?.Dispose();
                    _dup.ReleaseFrame();
                }
                Status = "ok";
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                _log.Error($"{Output.DeviceName}: {e.Message}");
                Close();
                Thread.Sleep(1000);
            }
        }
        Close();
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
        lock (_gpu)
        {
            _hasLatest = false;
            _latest?.Dispose(); _latest = null;
            _dup?.Dispose(); _dup = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
        }
    }

    private void EnsureLatest(Texture2DDescription frameDesc)
    {
        if (_latest != null && _latestDesc.Width == frameDesc.Width && _latestDesc.Height == frameDesc.Height && _latestDesc.Format == frameDesc.Format) return;
        _latest?.Dispose();
        _latestDesc = new Texture2DDescription(frameDesc.Format, frameDesc.Width, frameDesc.Height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
        _latest = _device!.CreateTexture2D(_latestDesc);
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

    /// <summary>Copies the latest frame to the CPU, converts it to scRGB and draws the cursor when asked.</summary>
    public (FloatImage Image, Core.Snapshot.CursorRect? Cursor)? Snapshot(bool includeCursor)
    {
        lock (_gpu)
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
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
        Close();
    }
}
