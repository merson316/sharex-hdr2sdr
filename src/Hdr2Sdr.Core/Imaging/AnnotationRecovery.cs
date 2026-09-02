namespace Hdr2Sdr.Core.Imaging;

/// <summary>MeanDiff: mean per-pixel difference between ShareX's SDR pixels and our tone-fitted render (0 = identical frame).</summary>
public sealed record AnnotationResult(byte[] Rgba, int Pixels, int HardPixels = 0, int SoftPixels = 0, float MeanDiff = 0f);

/// <summary>
/// Recovers what ShareX's image editor added on top of its own capture (arrows, text, blur, pixelate ...).
/// On SDR content ShareX's GDI capture and our default render of the same frame are pixel-identical, so a
/// clear difference means an edit. Differences over HDR pixels are expected (GDI clips them) and ignored.
/// </summary>
public static class AnnotationRecovery
{
    /// <summary>Per-channel difference above which a pixel counts as edited (JPEG noise stays below this).</summary>
    public const int Threshold = 20;
    private const float SdrMin = -0.02f, SdrMax = 1.02f;

    /// <param name="sharex">ShareX's saved pixels (RGBA8).</param>
    /// <param name="reference">Our default render of the same region (RGBA8), what GDI would have produced on SDR content.</param>
    /// <param name="target">The image to paste the edits onto (RGBA8), usually the final tonemapped result.</param>
    /// <param name="region">Linear scRGB of the region, to tell SDR pixels from HDR ones.</param>
    /// <param name="sdrWhiteScale">SDR white in scRGB units (SDR white nits / 80).</param>
    /// <summary>If more than this share of the SDR pixels differs, the two images are not the same frame and nothing is carried over.</summary>
    public const float MaxMaskShare = 0.35f;

    public static AnnotationResult Apply(byte[] sharex, byte[] reference, byte[] target, FloatImage region, float sdrWhiteScale, int width, int height)
    {
        int n = width * height;
        if (sharex.Length != n * 4 || reference.Length != n * 4 || target.Length != n * 4 || region.Width != width || region.Height != height)
            throw new ArgumentException("Buffer sizes do not match the dimensions.");

        var sdr = new bool[n];
        float inv = 1f / sdrWhiteScale;
        int sdrCount = 0;
        for (int i = 0; i < n; i++)
        {
            float r = region.Data[i * 3] * inv, g = region.Data[i * 3 + 1] * inv, b = region.Data[i * 3 + 2] * inv;
            sdr[i] = r >= SdrMin && r <= SdrMax && g >= SdrMin && g <= SdrMax && b >= SdrMin && b <= SdrMax;
            if (sdr[i]) sdrCount++;
        }
        if (sdrCount == 0) return new AnnotationResult((byte[])target.Clone(), 0, 0, 0, 0f);

        // GDI renders HDR surfaces (video, games) through its own tone curve that is not a function of our pixels,
        // so differences inside and right around HDR content mean nothing. Trust only blocks with no HDR pixel in
        // themselves or their neighbours; edits over HDR content are not carried over.
        bool[] trusted = TrustedBlocks(sdr, width, height);
        for (int i = 0; i < n; i++) if (!trusted[i]) sdr[i] = false;
        sdrCount = sdr.Count(v => v);
        if (sdrCount == 0) return new AnnotationResult((byte[])target.Clone(), 0, 0, 0, 0f);

        // GDI's rendering of the SDR desktop need not equal ours level for level (different transfer curve, dithering),
        // but the relationship is a global monotonic one. Fit it per channel as the median ShareX value for each of
        // our values, then compare against that instead of against our raw pixels.
        byte[][] lut = FitLuts(sharex, reference, sdr, n);

        var raw = new bool[n];
        var diff = new int[n];
        double diffSum = 0;
        for (int i = 0; i < n; i++)
        {
            if (!sdr[i]) continue;
            int p = i * 4;
            int d = Math.Max(Math.Abs(sharex[p] - lut[0][reference[p]]), Math.Max(Math.Abs(sharex[p + 1] - lut[1][reference[p + 1]]), Math.Abs(sharex[p + 2] - lut[2][reference[p + 2]])));
            diff[i] = d;
            diffSum += d;
            raw[i] = d > Threshold;
        }
        float meanDiff = (float)(diffSum / sdrCount);

        // Opening-like cleanup: keep a pixel only if most of its 3x3 neighbourhood also differs (kills speckles,
        // keeps 2 px lines), then grow by one pixel onto SDR pixels to cover anti-aliased edges.
        var kept = new bool[n];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!raw[y * width + x]) continue;
                if (Neighbourhood(raw, width, height, x, y) >= 5) kept[y * width + x] = true;
            }
        var mask = new bool[n];
        int count = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!sdr[i]) continue;
                if (kept[i] || Neighbourhood(kept, width, height, x, y) > 0)
                {
                    mask[i] = true;
                    count++;
                }
            }

        int hardCount = count;
        if (count > sdrCount * MaxMaskShare) return new AnnotationResult((byte[])target.Clone(), 0, hardCount, 0, meanDiff);   // not the same frame

        // Soft edits (blur, pixelate) barely move flat pixels and are skipped over HDR highlights by the rules above,
        // so detect them by their signature instead: a block whose fine detail collapsed while its mean stayed put.
        bool[] soft = SoftEditBlocks(sharex, reference, lut, width, height);
        int softCount = 0;
        for (int i = 0; i < n; i++) if (soft[i]) { softCount++; if (!mask[i]) count++; mask[i] = true; }

        // Blend by how strongly each pixel differs: solid annotation pixels are copied outright, anti-aliased edges and
        // the grown border mix into our render instead of dragging ShareX's background (and its compression noise) along.
        var result = (byte[])target.Clone();
        int applied = 0;
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            float a = soft[i] ? 1f : Math.Clamp((diff[i] - Threshold * 0.5f) / (Threshold * 1.5f), 0f, 1f);
            if (a <= 0f) continue;
            applied++;
            int p = i * 4;
            for (int c = 0; c < 3; c++) result[p + c] = (byte)Math.Round(result[p + c] * (1f - a) + sharex[p + c] * a);
        }
        return new AnnotationResult(result, applied, hardCount, softCount, meanDiff);
    }

    /// <summary>Per-pixel: the pixel's 8x8 block and all 8 neighbouring blocks contain only SDR pixels.</summary>
    private static bool[] TrustedBlocks(bool[] sdr, int width, int height)
    {
        int bw = (width + Block - 1) / Block, bh = (height + Block - 1) / Block;
        var hdrBlock = new bool[bw * bh];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (!sdr[y * width + x]) hdrBlock[(y / Block) * bw + x / Block] = true;
        var trustedBlock = new bool[bw * bh];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                bool ok = true;
                for (int j = Math.Max(0, by - 1); ok && j <= Math.Min(bh - 1, by + 1); j++)
                    for (int i = Math.Max(0, bx - 1); i <= Math.Min(bw - 1, bx + 1); i++)
                        if (hdrBlock[j * bw + i]) { ok = false; break; }
                trustedBlock[by * bw + bx] = ok;
            }
        var result = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                result[y * width + x] = trustedBlock[(y / Block) * bw + x / Block];
        return result;
    }

    private const int Block = 8;
    private const float SoftEnergyDrop = 0.55f;     // ShareX's block keeps at most this share of our detail energy
    private const int SoftMinEnergyPerPixel = 3;    // our block must have had detail worth blurring
    private const int SoftMaxMeanShift = 14;        // blur/pixelate keep the block mean; a changed frame does not
    private const int SoftMinBlocks = 3;            // connected blocks needed (kills isolated false positives)

    /// <summary>Per-pixel mask of 8x8 blocks that look blurred or pixelated in ShareX's image.</summary>
    private static bool[] SoftEditBlocks(byte[] sharex, byte[] reference, byte[][] lut, int width, int height)
    {
        int bw = (width + Block - 1) / Block, bh = (height + Block - 1) / Block;
        var flagged = new bool[bw * bh];
        // gray of ShareX's pixels and of our pixels mapped through the tone fit
        var gs = new float[width * height];
        var gr = new float[width * height];
        for (int i = 0; i < width * height; i++)
        {
            int p = i * 4;
            gs[i] = 0.299f * sharex[p] + 0.587f * sharex[p + 1] + 0.114f * sharex[p + 2];
            gr[i] = 0.299f * lut[0][reference[p]] + 0.587f * lut[1][reference[p + 1]] + 0.114f * lut[2][reference[p + 2]];
        }
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int x0 = bx * Block, y0 = by * Block, x1 = Math.Min(width, x0 + Block), y1 = Math.Min(height, y0 + Block);
                double es = 0, er = 0, ms = 0, mr = 0;
                int cnt = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * width + x;
                        ms += gs[i]; mr += gr[i]; cnt++;
                        if (x + 1 < x1) { es += Math.Abs(gs[i + 1] - gs[i]); er += Math.Abs(gr[i + 1] - gr[i]); }
                        if (y + 1 < y1) { es += Math.Abs(gs[i + width] - gs[i]); er += Math.Abs(gr[i + width] - gr[i]); }
                    }
                if (cnt == 0) continue;
                bool hadDetail = er >= SoftMinEnergyPerPixel * cnt;
                bool lostDetail = es <= SoftEnergyDrop * er;
                bool meanKept = Math.Abs(ms / cnt - mr / cnt) <= SoftMaxMeanShift;
                flagged[by * bw + bx] = hadDetail && lostDetail && meanKept;
            }

        // keep connected components of at least SoftMinBlocks blocks
        var keep = new bool[bw * bh];
        var seen = new bool[bw * bh];
        var stack = new Stack<int>();
        for (int b = 0; b < flagged.Length; b++)
        {
            if (!flagged[b] || seen[b]) continue;
            var comp = new List<int>();
            stack.Push(b); seen[b] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                comp.Add(c);
                int cx = c % bw, cy = c / bw;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                    int nb = ny * bw + nx;
                    if (flagged[nb] && !seen[nb]) { seen[nb] = true; stack.Push(nb); }
                }
            }
            if (comp.Count >= SoftMinBlocks) foreach (int c in comp) keep[c] = true;
        }

        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                mask[y * width + x] = keep[(y / Block) * bw + x / Block];
        return mask;
    }

    /// <summary>Per-channel 256-entry maps from our value to ShareX's median value at that level; gaps are interpolated.</summary>
    private static byte[][] FitLuts(byte[] sharex, byte[] reference, bool[] sdr, int n)
    {
        var luts = new byte[3][];
        var hist = new int[256, 256];
        for (int c = 0; c < 3; c++)
        {
            Array.Clear(hist);
            for (int i = 0; i < n; i++)
                if (sdr[i]) hist[reference[i * 4 + c], sharex[i * 4 + c]]++;

            var known = new int[256];
            Array.Fill(known, -1);
            for (int v = 0; v < 256; v++)
            {
                int total = 0;
                for (int t = 0; t < 256; t++) total += hist[v, t];
                if (total < 8) continue;
                int acc = 0;
                for (int t = 0; t < 256; t++) { acc += hist[v, t]; if (acc * 2 >= total) { known[v] = t; break; } }
            }

            var lut = new byte[256];
            int prev = -1;
            for (int v = 0; v < 256; v++)
            {
                if (known[v] >= 0) { lut[v] = (byte)known[v]; prev = v; continue; }
                int next = -1;
                for (int k = v + 1; k < 256; k++) if (known[k] >= 0) { next = k; break; }
                if (prev < 0 && next < 0) lut[v] = (byte)v;                                             // no data at all: identity
                else if (prev < 0) lut[v] = (byte)Math.Clamp(known[next] - (next - v), 0, 255);          // below the first sample: same slope 1
                else if (next < 0) lut[v] = (byte)Math.Clamp(known[prev] + (v - prev), 0, 255);          // above the last sample
                else lut[v] = (byte)Math.Round(known[prev] + (known[next] - known[prev]) * (double)(v - prev) / (next - prev));
            }
            luts[c] = lut;
        }
        return luts;
    }

    private static int Neighbourhood(bool[] m, int width, int height, int x, int y)
    {
        int c = 0;
        for (int j = Math.Max(0, y - 1); j <= Math.Min(height - 1, y + 1); j++)
            for (int i = Math.Max(0, x - 1); i <= Math.Min(width - 1, x + 1); i++)
                if (m[j * width + i]) c++;
        return c;
    }
}
