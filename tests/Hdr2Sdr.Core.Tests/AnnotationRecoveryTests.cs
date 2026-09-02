using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class AnnotationRecoveryTests
{
    private const int W = 80, H = 60;

    /// <summary>A smooth SDR gradient as our render, and the matching linear scRGB region (SDR white = 2.5 scRGB units).</summary>
    private static (byte[] Render, FloatImage Region) Scene()
    {
        var render = new byte[W * H * 4];
        var region = new FloatImage(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                byte v = (byte)(40 + x * 2);
                render[i] = v; render[i + 1] = (byte)(60 + y * 2); render[i + 2] = 90; render[i + 3] = 255;
                int f = (y * W + x) * 3;
                region.Data[f] = 0.5f; region.Data[f + 1] = 0.5f; region.Data[f + 2] = 0.5f;   // well inside SDR range
            }
        return (render, region);
    }

    private static byte[] WithRect(byte[] src, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        var copy = (byte[])src.Clone();
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * W + x) * 4;
                copy[i] = r; copy[i + 1] = g; copy[i + 2] = b;
            }
        return copy;
    }

    [Fact]
    public void Solid_annotation_on_sdr_content_is_carried_over()
    {
        var (render, region) = Scene();
        byte[] sharex = WithRect(render, 20, 15, 24, 10, 255, 0, 0);
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, sdrWhiteScale: 2.5f, W, H);
        Assert.InRange(res.Pixels, 24 * 10, (24 + 4) * (10 + 4));          // rect, at most grown by the dilation
        int inside = ((20 * W) + 30) * 4;
        Assert.Equal((255, 0, 0), (res.Rgba[inside], res.Rgba[inside + 1], res.Rgba[inside + 2]));
        int outside = ((5 * W) + 5) * 4;
        Assert.Equal(render[outside], res.Rgba[outside]);
    }

    [Fact]
    public void Differences_over_hdr_pixels_are_not_treated_as_annotations()
    {
        var (render, region) = Scene();
        for (int y = 15; y < 25; y++)
            for (int x = 20; x < 44; x++)
            {
                int f = (y * W + x) * 3;
                region.Data[f] = 6f; region.Data[f + 1] = 6f; region.Data[f + 2] = 6f;   // 480 nits-ish highlight
            }
        byte[] sharex = WithRect(render, 20, 15, 24, 10, 255, 255, 255);   // GDI clipped the highlight to white
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.Equal(0, res.Pixels);
        Assert.Equal(render, res.Rgba);
    }

    [Fact]
    public void Isolated_speckles_and_compression_noise_are_ignored()
    {
        var (render, region) = Scene();
        var sharex = (byte[])render.Clone();
        var rng = new Random(3);
        for (int i = 0; i < sharex.Length; i += 4)
        {
            int n = rng.Next(-14, 15);
            for (int c = 0; c < 3; c++) sharex[i + c] = (byte)Math.Clamp(sharex[i + c] + n, 0, 255);
        }
        // a few single-pixel outliers
        foreach (int p in new[] { 100, 1500, 3000 })
        {
            sharex[p * 4] = 255; sharex[p * 4 + 1] = 255; sharex[p * 4 + 2] = 255;
        }
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.Equal(0, res.Pixels);
    }

    [Fact]
    public void Thin_lines_survive_when_they_are_at_least_two_pixels_wide()
    {
        var (render, region) = Scene();
        byte[] sharex = WithRect(render, 10, 30, 50, 2, 0, 255, 0);   // a 2 px arrow shaft
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.True(res.Pixels >= 50 * 2, $"pixels {res.Pixels}");
        int onLine = ((30 * W) + 30) * 4;
        Assert.Equal((0, 255, 0), (res.Rgba[onLine], res.Rgba[onLine + 1], res.Rgba[onLine + 2]));
    }
}

public class AnnotationRecoveryToneTests
{
    private const int W = 120, H = 80;

    private static (byte[] Render, FloatImage Region) Scene()
    {
        var render = new byte[W * H * 4];
        var region = new FloatImage(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                render[i] = (byte)(x * 2); render[i + 1] = (byte)(y * 3); render[i + 2] = (byte)((x + y) & 255); render[i + 3] = 255;
                int f = (y * W + x) * 3;
                region.Data[f] = region.Data[f + 1] = region.Data[f + 2] = 0.4f;
            }
        return (render, region);
    }

    /// <summary>GDI rendered the SDR desktop with a different curve than we do: a global, monotonic tone shift.</summary>
    private static byte[] ToneShift(byte[] src)
    {
        var o = (byte[])src.Clone();
        for (int i = 0; i < o.Length; i += 4)
            for (int c = 0; c < 3; c++) o[i + c] = (byte)Math.Round(255.0 * Math.Pow(o[i + c] / 255.0, 1.25));
        return o;
    }

    [Fact]
    public void Global_tone_difference_alone_carries_nothing_over()
    {
        var (render, region) = Scene();
        byte[] sharex = ToneShift(render);
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.Equal(0, res.Pixels);
        Assert.Equal(render, res.Rgba);
    }

    [Fact]
    public void Annotation_is_still_found_under_a_global_tone_difference()
    {
        var (render, region) = Scene();
        byte[] sharex = ToneShift(render);
        for (int y = 20; y < 30; y++)
            for (int x = 30; x < 70; x++)
            {
                int i = (y * W + x) * 4;
                sharex[i] = 255; sharex[i + 1] = 0; sharex[i + 2] = 0;
            }
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.InRange(res.Pixels, 40 * 10, 44 * 14);
        int inside = ((25 * W) + 50) * 4;
        Assert.Equal((255, 0, 0), (res.Rgba[inside], res.Rgba[inside + 1], res.Rgba[inside + 2]));
    }

    [Fact]
    public void Implausibly_large_masks_are_rejected()
    {
        var (render, region) = Scene();
        var sharex = new byte[render.Length];
        for (int i = 0; i < sharex.Length; i += 4) { sharex[i] = 200; sharex[i + 1] = 10; sharex[i + 2] = 10; sharex[i + 3] = 255; }   // unrelated image
        AnnotationResult res = AnnotationRecovery.Apply(sharex, render, render, region, 2.5f, W, H);
        Assert.Equal(0, res.Pixels);
    }
}
