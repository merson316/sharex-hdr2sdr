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
        string input = opts.InputPath!;
        byte[] rgbaIn;
        int tw, th;
        try
        {
            (rgbaIn, tw, th) = Png.DecodeRgba8(File.ReadAllBytes(input));
        }
        catch (Exception e)
        {
            log.Error($"cannot read '{input}': {e.Message}");
            return ExitCodes.BadArguments;
        }
        GrayImage template = PixelConvert.ToGray(rgbaIn, tw, th);
        log.Info($"template {tw}x{th} from {input}");

        // Exact-size outputs first (fullscreen captures), then HDR outputs, then the rest.
        List<OutputHandle> ordered = set.Outputs
            .Where(o => o.Width >= tw && o.Height >= th)
            .OrderByDescending(o => o.Width == tw && o.Height == th)
            .ThenByDescending(o => o.Hdr)
            .ToList();
        if (ordered.Count == 0)
        {
            log.Error($"no output is large enough to contain a {tw}x{th} image");
            return ExitCodes.RegionNotFound;
        }

        OutputHandle? bestOutput = null;
        FloatImage? bestCapture = null;
        var best = new Core.Match.MatchResult(0, 0, -1f);
        int captured = 0, index = 0;
        foreach (OutputHandle o in ordered)
        {
            FloatImage capture;
            try
            {
                capture = DesktopDuplicator.Capture(o, log.Info);
                captured++;
            }
            catch (Exception e)
            {
                log.Warn($"capture of {o.DeviceName} failed: {e.Message}");
                index++;
                continue;
            }
            byte[] preview = PixelConvert.ToRgba8(capture, PreviewTonemapper(o));
            if (opts.DumpDir != null) Dump(opts.DumpDir, $"output{index}", capture, preview, o);
            Core.Match.MatchResult m = Core.Match.RegionMatcher.Find(template, PixelConvert.ToGray(preview, o.Width, o.Height));
            log.Info($"match on {o.DeviceName}: ({m.X},{m.Y}) score={m.Score:F4}");
            if (m.Score > best.Score)
            {
                best = m;
                bestOutput = o;
                bestCapture = capture;
            }
            index++;
            if (best.Score >= 0.98f) break;
        }

        if (captured == 0)
        {
            log.Error("no output could be captured");
            return ExitCodes.CaptureFailed;
        }
        if (bestOutput == null || bestCapture == null || best.Score < Core.Match.RegionMatcher.AcceptThreshold)
        {
            log.Error($"region not found (best score {best.Score:F4}, threshold {Core.Match.RegionMatcher.AcceptThreshold})");
            return ExitCodes.RegionNotFound;
        }
        if (!bestOutput.Hdr)
        {
            log.Info($"region is on SDR output {bestOutput.DeviceName}; nothing to do");
            return ExitCodes.Ok;
        }

        var p = new TonemapParams
        {
            SdrWhiteNits = opts.SdrWhiteNits ?? bestOutput.SdrWhiteNits,
            PeakNits = opts.PeakNits ?? bestOutput.MaxLuminance,
            Exposure = opts.Exposure,
            Knee = opts.Knee,
        };
        ITonemapper tm = TonemapperFactory.Create(opts.Tonemap, p);
        log.Info($"tonemap {opts.Tonemap} sdrWhite={p.SdrWhiteNits:F0} peak={p.PeakNits:F0} exposure={p.Exposure} knee={p.Knee} region=({best.X},{best.Y},{tw}x{th}) on {bestOutput.DeviceName}");
        byte[] rgba = PixelConvert.ToRgba8(bestCapture.Crop(best.X, best.Y, tw, th), tm);
        byte[] png = Png.EncodeRgba8(rgba, tw, th);

        string target = opts.OutputPath ?? input;
        try
        {
            string tmp = target + ".hdr2sdr.tmp";
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, target, overwrite: true);
            log.Info($"wrote {target} ({png.Length} bytes)");
        }
        catch (Exception e)
        {
            log.Error($"cannot write '{target}': {e.Message}");
            return ExitCodes.WriteFailed;
        }

        if (!opts.NoClipboard)
        {
            try
            {
                Clipboard.Win32Clipboard.SetImage(rgba, tw, th, png);
                log.Info("clipboard updated");
            }
            catch (Exception e)
            {
                log.Error($"clipboard write failed: {e.Message}");
                return ExitCodes.WriteFailed;
            }
        }
        return ExitCodes.Ok;
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
