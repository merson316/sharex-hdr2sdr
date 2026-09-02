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
    public static AnnotationResult Apply(byte[] sharex, byte[] reference, byte[] target, FloatImage region, float sdrWhiteScale, int width, int height)
    {
        int n = width * height;
        if (sharex.Length != n * 4 || reference.Length != n * 4 || target.Length != n * 4 || region.Width != width || region.Height != height)
            throw new ArgumentException("Buffer sizes do not match the dimensions.");

        var sdr = new bool[n];
        var raw = new bool[n];
        float inv = 1f / sdrWhiteScale;
        for (int i = 0; i < n; i++)
        {
            float r = region.Data[i * 3] * inv, g = region.Data[i * 3 + 1] * inv, b = region.Data[i * 3 + 2] * inv;
            sdr[i] = r >= SdrMin && r <= SdrMax && g >= SdrMin && g <= SdrMax && b >= SdrMin && b <= SdrMax;
            if (!sdr[i]) continue;
            int p = i * 4;
            int d = Math.Max(Math.Abs(sharex[p] - reference[p]), Math.Max(Math.Abs(sharex[p + 1] - reference[p + 1]), Math.Abs(sharex[p + 2] - reference[p + 2])));
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

        var result = (byte[])target.Clone();
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            int p = i * 4;
            result[p] = sharex[p]; result[p + 1] = sharex[p + 1]; result[p + 2] = sharex[p + 2];
        }
        return new AnnotationResult(result, count);
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
