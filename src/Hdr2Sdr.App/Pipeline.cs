using Hdr2Sdr.Windows.Display;
using Hdr2Sdr.Windows.Imaging;
using Hdr2Sdr.Windows.Helper;
using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Match;
using Hdr2Sdr.Core.Config;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.App;

internal static class Pipeline
{
    private sealed class CapturedOutput
    {
        public required OutputHandle Output { get; init; }
        public required FloatImage Capture { get; init; }
        public required byte[] Preview { get; init; }
        public bool FromHelper { get; init; }
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

    public static int Process(CliOptions opts, Settings settings, DisplaySet set, Log log)
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
        HelperClient? helper = null;
        if (settings.UseHelper)
        {
            helper = new HelperClient(log.Info);
            HelperSnapshot? snap = helper.TryGetSnapshot(TimeSpan.FromMilliseconds(200));
            if (snap != null)
            {
                DateTime fileUtc = File.GetLastWriteTimeUtc(input);
                double ageBeforeFile = (fileUtc - snap.Header.TakenUtc).TotalSeconds;
                if (ageBeforeFile < -2 || ageBeforeFile > 120)
                {
                    log.Info($"helper snapshot ignored: taken {ageBeforeFile:F1} s before the file was written");
                }
                else
                {
                    for (int i = 0; i < snap.Header.Outputs.Count; i++)
                    {
                        Core.Snapshot.SnapshotOutput so = snap.Header.Outputs[i];
                        OutputHandle? o = set.Outputs.FirstOrDefault(x => x.DeviceName.Equals(so.DeviceName, StringComparison.OrdinalIgnoreCase) && x.Width == so.Width && x.Height == so.Height);
                        if (o == null) { log.Warn($"helper snapshot output {so.DeviceName} does not match a current output; skipped"); continue; }
                        FloatImage img = snap.Images[i];
                        byte[] preview = PixelConvert.ToRgba8(img, PreviewTonemapper(o));
                        if (opts.DumpDir != null) Dump(opts.DumpDir, $"helper-{i}", img, preview, o);
                        captured[o.DeviceName] = new CapturedOutput { Output = o, Capture = img, Preview = preview, FromHelper = true };
                    }
                    log.Info($"using helper snapshot taken {ageBeforeFile:F1} s before the file for {captured.Count} outputs");
                }
            }
        }
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
                CapturedOutput? cap = captured.TryGetValue(o.DeviceName, out CapturedOutput? held) ? held : TryCapture(o, index++, opts, log);
                if (cap == null) continue;
                captured[o.DeviceName] = cap;
                GrayImage outputGray = PixelConvert.ToGray(cap.Preview, o.Width, o.Height);
                if (IsUniform(outputGray)) log.Warn($"{o.DeviceName} captured as a uniform frame: an exclusive-fullscreen app cannot be captured by Desktop Duplication; use borderless windowed mode");
                MatchResult m = RegionMatcher.FindRobust(template, outputGray);
                log.Info($"match on {o.DeviceName}: ({m.X},{m.Y}) score={m.Score:F4} coverage={m.Coverage:P0}");
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
                    MatchResult m = RegionMatcher.FindRobust(template, PixelConvert.ToGray(canvas.Rgba, canvas.Width, canvas.Height));
                    log.Info($"match on virtual desktop {canvas.Width}x{canvas.Height} at ({canvas.Left},{canvas.Top}): ({m.X},{m.Y}) score={m.Score:F4} coverage={m.Coverage:P0}");
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
        bool customTonemap = settings.IsCustomTonemap;
        var tiles = new List<RgbaImage.Tile>(captured.Count);
        foreach (CapturedOutput c in captured.Values)
        {
            byte[] rgba = c.Preview;
            if (c.Output.Hdr && customTonemap && region.Intersects(c.Output))
            {
                TonemapParams p = settings.ToTonemapParams(c.Output.SdrWhiteNits, c.Output.MaxLuminance);
                log.Info($"tonemap {settings.Tonemap} on {c.Output.DeviceName}: sdrWhite={p.SdrWhiteNits:F0} peak={p.PeakNits:F0} exposure={p.Exposure} knee={p.Knee}");
                rgba = PixelConvert.ToRgba8(c.Capture, TonemapperFactory.Create(settings.Tonemap, p));
            }
            tiles.Add(Tile(c.Output, rgba));
        }

        byte[] result;
        FloatImage? hdrRegionOverride = null;
        CapturedOutput? container = hits.FirstOrDefault(c => region.IsInside(c.Output));
        if (container != null && container.FromHelper && helper != null && container.Output.Hdr)
        {
            // The frozen frame predates ShareX's own capture slightly; pick the recorded frame that matches best.
            int lx = region.Left - container.Output.Left, ly = region.Top - container.Output.Top;
            List<(int OffsetMs, FloatImage Crop)> ring = helper.GetRingCrops(container.Output.DeviceName, lx, ly, tw, th);
            if (ring.Count > 0)
            {
                DesktopTonemapper previewTm = PreviewTonemapper(container.Output);
                int ds = Math.Max(1, Math.Min(tw, th) / 64);   // score at reduced resolution; ranking frames does not need every pixel
                GrayImage templateSmall = template.Downsample(ds);
                float Score(FloatImage crop) => RegionMatcher.Find(templateSmall, PixelConvert.ToGray(PixelConvert.ToRgba8(crop, previewTm), tw, th).Downsample(ds)).Score;
                float bestScore = Score(container.Capture.Crop(lx, ly, tw, th));
                int bestOffset = 0;
                FloatImage? bestCrop = null;
                var report = new List<string> { $"hotkey:{bestScore:F4}" };
                foreach (var (offset, crop) in ring)
                {
                    float sc = Score(crop);
                    report.Add($"+{offset}ms:{sc:F4}");
                    if (sc > bestScore + 0.0005f) { bestScore = sc; bestOffset = offset; bestCrop = crop; }
                }
                log.Info($"frame alignment ({string.Join(" ", report)}) -> {(bestCrop == null ? "hotkey frame" : $"+{bestOffset} ms")}");
                if (bestCrop != null) hdrRegionOverride = bestCrop;
            }
        }
        if (hdrRegionOverride != null && container != null)
        {
            ITonemapper tm = customTonemap
                ? TonemapperFactory.Create(settings.Tonemap, settings.ToTonemapParams(container.Output.SdrWhiteNits, container.Output.MaxLuminance))
                : PreviewTonemapper(container.Output);
            result = PixelConvert.ToRgba8(hdrRegionOverride, tm);
        }
        else if (container != null)
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

        FloatImage? hdrRegion = null;
        if (settings.CarryAnnotations || settings.HdrSidecar == "jxr")
        {
            if (hdrRegionOverride != null) hdrRegion = hdrRegionOverride;
            else if (container != null) hdrRegion = container.Capture.Crop(region.Left - container.Output.Left, region.Top - container.Output.Top, tw, th);
            else
            {
                var (canvas, cLeft, cTop) = FloatImage.Composite(captured.Values.Select(c => new FloatImage.Tile(c.Capture, c.Output.Left, c.Output.Top)).ToList());
                hdrRegion = canvas.Crop(region.Left - cLeft, region.Top - cTop, tw, th);
            }
        }

        if (settings.CarryAnnotations && container != null && hdrRegion != null)
        {
            // What GDI produced for SDR pixels equals our default render; anything else ShareX's editor added.
            byte[] reference = customTonemap ? PixelConvert.ToRgba8(hdrRegion, PreviewTonemapper(container.Output)) : result;
            float scale = container.Output.Hdr ? container.Output.SdrWhiteNits / 80f : 1f;
            AnnotationResult ann = AnnotationRecovery.Apply(rgbaIn, reference, result, hdrRegion, scale, tw, th);
            log.Info($"annotations: gdi-vs-render mean diff {ann.MeanDiff:F1} levels; hard {ann.HardPixels} px, soft (blur/pixelate) {ann.SoftPixels} px, applied {ann.Pixels} px");
            if (ann.Pixels > 0) result = ann.Rgba;
            if (opts.DumpDir != null)
            {
                File.WriteAllBytes(Path.Combine(opts.DumpDir, "annot-sharex.png"), Png.EncodeRgba8(rgbaIn, tw, th));
                File.WriteAllBytes(Path.Combine(opts.DumpDir, "annot-render.png"), Png.EncodeRgba8(reference, tw, th));
                File.WriteAllBytes(Path.Combine(opts.DumpDir, "annot-result.png"), Png.EncodeRgba8(result, tw, th));
            }
        }

        string target = opts.OutputPath ?? input;
        string extension = Path.GetExtension(target);
        byte[] png = Png.EncodeRgba8(result, tw, th);
        byte[]? fileBytes = null;
        try
        {
            fileBytes = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? png : ImageIO.Encode(result, tw, th, extension, settings.JpegQuality, settings.WebpQuality);
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

        if (settings.HdrSidecar == "jxr" && hdrRegion != null)
        {
            try
            {
                string sidecar = Path.ChangeExtension(target, ".jxr");
                File.WriteAllBytes(sidecar, JxrEncoder.EncodeHalf(hdrRegion));
                log.Info($"wrote HDR sidecar {sidecar}");
            }
            catch (Exception e)
            {
                log.Warn($"HDR sidecar failed: {e.Message}");
            }
        }

        if (!opts.NoClipboard)
        {
            try
            {
                Hdr2Sdr.Windows.Clipboard.Win32Clipboard.SetImage(result, tw, th, png);
                log.Info("clipboard updated");
            }
            catch (Exception e)
            {
                log.Error($"clipboard write failed: {e.Message}");
                return ExitCodes.WriteFailed;
            }
        }
        if (helper != null)
        {
            helper.ReportRegion(input, region.Left, region.Top, tw, th);
            helper.Consume();
        }
        return ExitCodes.Ok;
    }

    private static RgbaImage.Tile Tile(OutputHandle o, byte[] rgba) => new(rgba, o.Width, o.Height, o.Left, o.Top);

    /// <summary>True when the whole frame is one flat colour (black for exclusive fullscreen or protected content).</summary>
    private static bool IsUniform(GrayImage g)
    {
        double sum = 0, sq = 0;
        int step = Math.Max(1, g.Data.Length / 200_000);   // sample
        int n = 0;
        for (int i = 0; i < g.Data.Length; i += step) { sum += g.Data[i]; sq += g.Data[i] * g.Data[i]; n++; }
        double mean = sum / n;
        return sq / n - mean * mean < 1e-6;
    }

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
