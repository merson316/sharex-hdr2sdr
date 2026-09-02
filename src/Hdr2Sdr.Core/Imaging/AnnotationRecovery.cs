namespace Hdr2Sdr.Core.Imaging;

public sealed record AnnotationResult(byte[] Rgba, int Pixels);

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
        if (sdrCount == 0) return new AnnotationResult((byte[])target.Clone(), 0);

        // GDI's rendering of the SDR desktop need not equal ours level for level (different transfer curve, dithering),
        // but the relationship is a global monotonic one. Fit it per channel as the median ShareX value for each of
        // our values, then compare against that instead of against our raw pixels.
        byte[][] lut = FitLuts(sharex, reference, sdr, n);

        var raw = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!sdr[i]) continue;
            int p = i * 4;
            int d = Math.Max(Math.Abs(sharex[p] - lut[0][reference[p]]), Math.Max(Math.Abs(sharex[p + 1] - lut[1][reference[p + 1]]), Math.Abs(sharex[p + 2] - lut[2][reference[p + 2]])));
            raw[i] = d > Threshold;
        }

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

        if (count > sdrCount * MaxMaskShare) return new AnnotationResult((byte[])target.Clone(), 0);   // not the same frame

        var result = (byte[])target.Clone();
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            int p = i * 4;
            result[p] = sharex[p]; result[p + 1] = sharex[p + 1]; result[p + 2] = sharex[p + 2];
        }
        return new AnnotationResult(result, count);
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
