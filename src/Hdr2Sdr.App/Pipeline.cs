using Hdr2Sdr.App.Display;
using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.App;

internal static class Pipeline
{
    public static int CaptureAll(CliOptions opts, DisplaySet set, Log log)
    {
        int captured = 0, index = 0;
        foreach (OutputHandle o in set.Outputs)
        {
            try
            {
                FloatImage capture = DesktopDuplicator.Capture(o, log.Info);
                byte[] preview = PixelConvert.ToRgba8(capture, PreviewTonemapper(o));
                Dump(opts.DumpDir!, $"output{index}", capture, preview, o);
                log.Info($"dumped {o.DeviceName} as output{index}");
                captured++;
            }
            catch (Exception e)
            {
                log.Warn($"capture of {o.DeviceName} failed: {e.Message}");
            }
            index++;
        }
        return captured > 0 ? ExitCodes.Ok : ExitCodes.CaptureFailed;
    }

    public static int Process(CliOptions opts, DisplaySet set, Log log)
    {
        log.Error("processing is not implemented yet");
        return ExitCodes.CaptureFailed;
    }

    /// <summary>The tonemapper used to make a capture comparable with ShareX's SDR image: exact SDR, clip above.</summary>
    internal static DesktopTonemapper PreviewTonemapper(OutputHandle o)
        => new(new TonemapParams { SdrWhiteNits = o.SdrWhiteNits, PeakNits = o.MaxLuminance });

    internal static void Dump(string dir, string name, FloatImage capture, byte[] previewRgba, OutputHandle o)
    {
        Directory.CreateDirectory(dir);
        // 16-bit linear PNG where 65535 = monitor peak luminance.
        float scale = 80f / MathF.Max(o.MaxLuminance, 80f);
        File.WriteAllBytes(Path.Combine(dir, name + "-linear16.png"), Png.EncodeRgb16(capture.ToRgb16BigEndian(scale), capture.Width, capture.Height));
        File.WriteAllBytes(Path.Combine(dir, name + "-preview.png"), Png.EncodeRgba8(previewRgba, capture.Width, capture.Height));
        File.WriteAllText(Path.Combine(dir, name + ".txt"), o.ToString() + Environment.NewLine);
    }
}
