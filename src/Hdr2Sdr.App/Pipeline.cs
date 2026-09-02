using Hdr2Sdr.App.Display;
using Hdr2Sdr.App.Imaging;
using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Match;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.App;

internal static class Pipeline
{
    private sealed class CapturedOutput
    {
        public required OutputHandle Output { get; init; }
        public required FloatImage Capture { get; init; }
        public required byte[] Preview { get; init; }
    }

    /// <summary>A rectangle in desktop (virtual screen) coordinates.</summary>
    private readonly record struct Region(int Left, int Top, int Width, int Height)
    {
        public bool Intersects(OutputHandle o)
            => Left < o.Left + o.Width && Left + Width > o.Left && Top < o.Top + o.Height && Top + Height > o.Top;

        public bool IsInside(OutputHandle o)
            => Left >= o.Left && Top >= o.Top && Left + Width <= o.Left + o.Width && Top + Height <= o.Top + o.Height;
    }

    public static int CaptureAll(CliOptions opts, DisplaySet set, Log log)
    {
        int captured = 0, index = 0;
        foreach (OutputHandle o in set.Outputs)
        {
            if (TryCapture(o, index, opts, log) != null) captured++;
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
            (rgbaIn, tw, th) = ImageIO.Load(input);
        }
        catch (Exception e)
        {
            log.Error($"cannot read '{input}': {e.Message}");
            return ExitCodes.BadArguments;
        }
        GrayImage template = PixelConvert.ToGray(rgbaIn, tw, th);
        log.Info($"template {tw}x{th} from {input}");

        if (set.Outputs.Count == 0)
        {
            log.Error("no DXGI outputs found");
            return ExitCodes.CaptureFailed;
        }
        int vLeft = set.Outputs.Min(o => o.Left), vTop = set.Outputs.Min(o => o.Top);
        int vWidth = set.Outputs.Max(o => o.Left + o.Width) - vLeft, vHeight = set.Outputs.Max(o => o.Top + o.Height) - vTop;
        if (tw > vWidth || th > vHeight)
        {
            log.Error($"image {tw}x{th} is larger than the virtual desktop {vWidth}x{vHeight}");
            return ExitCodes.RegionNotFound;
        }

        var captured = new Dictionary<string, CapturedOutput>(StringComparer.OrdinalIgnoreCase);
        var best = new MatchResult(0, 0, -1f);
        Region? bestRegion = null;
        int index = 0;

        // Single-output search first: exact-size outputs (monitor captures), then HDR outputs, then the rest.
        bool virtualSized = set.Outputs.Count > 1 && tw == vWidth && th == vHeight;
        if (!virtualSized)
        {
            List<OutputHandle> ordered = set.Outputs
                .Where(o => o.Width >= tw && o.Height >= th)
                .OrderByDescending(o => o.Width == tw && o.Height == th)
                .ThenByDescending(o => o.Hdr)
                .ToList();
            foreach (OutputHandle o in ordered)
            {
                CapturedOutput? cap = TryCapture(o, index++, opts, log);
                if (cap == null) continue;
                captured[o.DeviceName] = cap;
                MatchResult m = RegionMatcher.Find(template, PixelConvert.ToGray(cap.Preview, o.Width, o.Height));
                log.Info($"match on {o.DeviceName}: ({m.X},{m.Y}) score={m.Score:F4}");
                if (m.Score > best.Score)
                {
                    best = m;
                    bestRegion = new Region(o.Left + m.X, o.Top + m.Y, tw, th);
                }
                if (best.Score >= 0.98f) break;
            }
        }

        // Virtual-desktop search: fullscreen captures across monitors and regions spanning them.
        if (best.Score < RegionMatcher.AcceptThreshold && set.Outputs.Count > 1)
        {
            foreach (OutputHandle o in set.Outputs)
            {
                if (captured.ContainsKey(o.DeviceName)) continue;
                CapturedOutput? cap = TryCapture(o, index++, opts, log);
                if (cap != null) captured[o.DeviceName] = cap;
            }
            if (captured.Count > 0)
            {
                RgbaImage.Canvas canvas = RgbaImage.Composite(captured.Values.Select(c => Tile(c.Output, c.Preview)).ToList());
                if (opts.DumpDir != null)
                    File.WriteAllBytes(Path.Combine(opts.DumpDir, "virtual-preview.png"), Png.EncodeRgba8(canvas.Rgba, canvas.Width, canvas.Height));
                if (tw <= canvas.Width && th <= canvas.Height)
                {
                    MatchResult m = RegionMatcher.Find(template, PixelConvert.ToGray(canvas.Rgba, canvas.Width, canvas.Height));
                    log.Info($"match on virtual desktop {canvas.Width}x{canvas.Height} at ({canvas.Left},{canvas.Top}): ({m.X},{m.Y}) score={m.Score:F4}");
                    if (m.Score > best.Score)
                    {
                        best = m;
                        bestRegion = new Region(canvas.Left + m.X, canvas.Top + m.Y, tw, th);
                    }
                }
            }
        }

        if (captured.Count == 0)
        {
            log.Error("no output could be captured");
            return ExitCodes.CaptureFailed;
        }
        if (bestRegion == null || best.Score < RegionMatcher.AcceptThreshold)
        {
            log.Error($"region not found (best score {best.Score:F4}, threshold {RegionMatcher.AcceptThreshold})");
            return ExitCodes.RegionNotFound;
        }

        Region region = bestRegion.Value;
        List<CapturedOutput> hits = captured.Values.Where(c => region.Intersects(c.Output)).ToList();
        if (!hits.Any(c => c.Output.Hdr))
        {
            log.Info($"region ({region.Left},{region.Top},{region.Width}x{region.Height}) only covers SDR outputs; nothing to do");
            return ExitCodes.Ok;
        }

        // Final pixels: the preview already is the default tonemap; recompute HDR outputs only for custom settings.
        bool customTonemap = !(opts.Tonemap == "desktop" && opts.Knee >= 1f && opts.Exposure == 1f && opts.SdrWhiteNits == null && opts.PeakNits == null);
        var tiles = new List<RgbaImage.Tile>(captured.Count);
        foreach (CapturedOutput c in captured.Values)
        {
            byte[] rgba = c.Preview;
            if (c.Output.Hdr && customTonemap && region.Intersects(c.Output))
            {
                var p = new TonemapParams
                {
                    SdrWhiteNits = opts.SdrWhiteNits ?? c.Output.SdrWhiteNits,
                    PeakNits = opts.PeakNits ?? c.Output.MaxLuminance,
                    Exposure = opts.Exposure,
                    Knee = opts.Knee,
                };
                log.Info($"tonemap {opts.Tonemap} on {c.Output.DeviceName}: sdrWhite={p.SdrWhiteNits:F0} peak={p.PeakNits:F0} exposure={p.Exposure} knee={p.Knee}");
                rgba = PixelConvert.ToRgba8(c.Capture, TonemapperFactory.Create(opts.Tonemap, p));
            }
            tiles.Add(Tile(c.Output, rgba));
        }

        byte[] result;
        CapturedOutput? container = hits.FirstOrDefault(c => region.IsInside(c.Output));
        if (container != null)
        {
            RgbaImage.Tile t = tiles.First(t => t.Left == container.Output.Left && t.Top == container.Output.Top);
            result = RgbaImage.Crop(t.Rgba, t.Width, t.Height, region.Left - t.Left, region.Top - t.Top, tw, th);
        }
        else
        {
            RgbaImage.Canvas canvas = RgbaImage.Composite(tiles);
            result = RgbaImage.Crop(canvas.Rgba, canvas.Width, canvas.Height, region.Left - canvas.Left, region.Top - canvas.Top, tw, th);
        }
        log.Info($"region ({region.Left},{region.Top},{tw}x{th}) covers {string.Join(", ", hits.Select(h => h.Output.DeviceName + (h.Output.Hdr ? " (HDR)" : " (SDR)")))}");

        string target = opts.OutputPath ?? input;
        string extension = Path.GetExtension(target);
        byte[] png = Png.EncodeRgba8(result, tw, th);
        byte[]? fileBytes = null;
        try
        {
            fileBytes = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? png : ImageIO.Encode(result, tw, th, extension);
        }
        catch (Exception e)
        {
            log.Warn($"cannot encode '{extension}' ({e.Message}); file left unchanged, clipboard still updated");
        }
        if (fileBytes != null)
        {
            try
            {
                string tmp = target + ".hdr2sdr.tmp";
                File.WriteAllBytes(tmp, fileBytes);
                File.Move(tmp, target, overwrite: true);
                log.Info($"wrote {target} ({fileBytes.Length} bytes)");
            }
            catch (Exception e)
            {
                log.Error($"cannot write '{target}': {e.Message}");
                return ExitCodes.WriteFailed;
            }
        }

        if (!opts.NoClipboard)
        {
            try
            {
                Clipboard.Win32Clipboard.SetImage(result, tw, th, png);
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

    private static RgbaImage.Tile Tile(OutputHandle o, byte[] rgba) => new(rgba, o.Width, o.Height, o.Left, o.Top);

    private static CapturedOutput? TryCapture(OutputHandle o, int index, CliOptions opts, Log log)
    {
        try
        {
            FloatImage capture = DesktopDuplicator.Capture(o, log.Info);
            byte[] preview = PixelConvert.ToRgba8(capture, PreviewTonemapper(o));
            if (opts.DumpDir != null) Dump(opts.DumpDir, $"output{index}", capture, preview, o);
            return new CapturedOutput { Output = o, Capture = capture, Preview = preview };
        }
        catch (Exception e)
        {
            log.Warn($"capture of {o.DeviceName} failed: {e.Message}");
            return null;
        }
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
