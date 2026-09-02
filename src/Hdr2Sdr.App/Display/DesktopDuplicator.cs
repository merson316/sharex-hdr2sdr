using System.Diagnostics;
using Hdr2Sdr.Core.Imaging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Hdr2Sdr.App.Display;

public static class DesktopDuplicator
{
    private static readonly Format[] PreferredFormats = { Format.R16G16B16A16_Float, Format.R10G10B10A2_UNorm, Format.B8G8R8A8_UNorm };
    private static readonly FeatureLevel[] FeatureLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

    /// <summary>
    /// Captures one output with DXGI Desktop Duplication. The duplicated surface is only filled by the OS while
    /// no frame is held, so the first acquire can be blank: acquire/release until the compositor reports a real
    /// present, then keep that frame. Falls back to whatever the next frame is after a short wait (idle desktop).
    /// </summary>
    public static FloatImage Capture(OutputHandle output, Action<string> log, int presentBudgetMs = 600)
    {
        D3D11.D3D11CreateDevice(output.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels,
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        {
            using IDXGIOutputDuplication dup = output.Output.DuplicateOutput1(device, PreferredFormats);
            IDXGIResource? resource = null;
            OutduplFrameInfo info = default;
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < presentBudgetMs)
            {
                Result hr = dup.AcquireNextFrame(100, out info, out resource);
                if (hr.Success)
                {
                    if (info.LastPresentTime != 0 || info.AccumulatedFrames > 0) break;
                    resource.Dispose();
                    resource = null;
                    dup.ReleaseFrame();
                    continue;
                }
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                throw new InvalidOperationException($"AcquireNextFrame failed on {output.DeviceName}: {hr}");
            }

            if (resource == null)
            {
                log($"{output.DeviceName}: no present within {presentBudgetMs} ms, taking the next frame");
                Thread.Sleep(120);
                for (int i = 0; i < 5 && resource == null; i++)
                {
                    Result hr = dup.AcquireNextFrame(200, out info, out resource);
                    if (hr.Failure) resource = null;
                }
            }
            if (resource == null) throw new InvalidOperationException($"Desktop Duplication produced no frame for {output.DeviceName}.");

            try
            {
                using ID3D11Texture2D tex = resource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription desc = tex.Description;
                log($"{output.DeviceName}: frame {desc.Width}x{desc.Height} format={desc.Format} after {sw.ElapsedMilliseconds} ms");
                var stagingDesc = new Texture2DDescription(desc.Format, desc.Width, desc.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
                using ID3D11Texture2D staging = device.CreateTexture2D(stagingDesc);
                context.CopyResource(staging, tex);
                MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    return FrameConverter.ToScRgb(map.DataPointer, (int)map.RowPitch, (int)desc.Width, (int)desc.Height, desc.Format, output.Rotation);
                }
                finally
                {
                    context.Unmap(staging, 0);
                }
            }
            finally
            {
                resource.Dispose();
                dup.ReleaseFrame();
            }
        }
    }
}
