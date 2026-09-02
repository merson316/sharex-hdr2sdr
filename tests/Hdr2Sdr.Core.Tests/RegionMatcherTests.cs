using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Match;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class RegionMatcherTests
{
    /// <summary>Deterministic structured image: smooth waves plus hashed noise, values in [0,1].</summary>
    private static GrayImage Synthetic(int w, int h, int seed)
    {
        var img = new GrayImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint hsh = (uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791);
                hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
                float noise = (hsh & 0xFFFF) / 65535f;
                float wave = 0.5f + 0.25f * MathF.Sin(x * 0.11f + seed) * MathF.Cos(y * 0.07f);
                img.Data[y * w + x] = Math.Clamp(wave * 0.7f + noise * 0.3f, 0f, 1f);
            }
        return img;
    }

    private static GrayImage Crop(GrayImage src, int x, int y, int w, int h, Func<float, int, float> distort)
    {
        var t = new GrayImage(w, h);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                t.Data[j * w + i] = distort(src.Data[(y + j) * src.Width + x + i], j * w + i);
        return t;
    }

    private static float GammaAndNoise(float v, int i) => Math.Clamp(MathF.Pow(v, 1.3f) * 0.9f + ((i * 7919) % 13 - 6) * 0.003f, 0f, 1f);

    [Fact]
    public void Finds_large_distorted_crop_exactly()
    {
        var scene = Synthetic(640, 480, seed: 1);
        var template = Crop(scene, 137, 61, 200, 120, GammaAndNoise);
        var m = RegionMatcher.Find(template, scene);
        Assert.Equal(137, m.X);
        Assert.Equal(61, m.Y);
        Assert.True(m.Score > RegionMatcher.AcceptThreshold, $"score {m.Score}");
    }

    [Fact]
    public void Finds_small_crop_using_the_two_level_path()
    {
        var scene = Synthetic(300, 200, seed: 2);
        var template = Crop(scene, 211, 33, 24, 20, GammaAndNoise);
        var m = RegionMatcher.Find(template, scene);
        Assert.Equal(211, m.X);
        Assert.Equal(33, m.Y);
        Assert.True(m.Score > RegionMatcher.AcceptThreshold, $"score {m.Score}");
    }

    [Fact]
    public void Finds_tiny_crop_using_full_resolution_only()
    {
        var scene = Synthetic(120, 90, seed: 3);
        var template = Crop(scene, 50, 40, 10, 8, (v, _) => v);
        var m = RegionMatcher.Find(template, scene);
        Assert.Equal(50, m.X);
        Assert.Equal(40, m.Y);
    }

    [Fact]
    public void Same_size_returns_origin_with_high_score()
    {
        var scene = Synthetic(200, 100, seed: 4);
        var template = Crop(scene, 0, 0, 200, 100, GammaAndNoise);
        var m = RegionMatcher.Find(template, scene);
        Assert.Equal((0, 0), (m.X, m.Y));
        Assert.True(m.Score > 0.95f, $"score {m.Score}");
    }

    [Fact]
    public void Template_larger_than_candidate_is_impossible()
    {
        var m = RegionMatcher.Find(Synthetic(100, 100, 5), Synthetic(50, 100, 5));
        Assert.Equal(-1f, m.Score);
    }

    [Fact]
    public void Unrelated_template_scores_below_threshold()
    {
        var scene = Synthetic(640, 480, seed: 6);
        var other = Synthetic(640, 480, seed: 99);
        var template = Crop(other, 100, 100, 200, 120, (v, _) => v);
        var m = RegionMatcher.Find(template, scene);
        Assert.True(m.Score < RegionMatcher.AcceptThreshold, $"score {m.Score}");
    }

    [Fact]
    public void Flat_template_matches_the_flat_patch()
    {
        // Candidate: horizontal gradient in [0.6, 1.0] with a flat 0.5 patch at (80, 30) of size 40x40.
        var scene = new GrayImage(200, 120);
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 200; x++)
                scene.Data[y * 200 + x] = 0.6f + 0.4f * x / 199f;
        for (int y = 30; y < 70; y++)
            for (int x = 80; x < 120; x++)
                scene.Data[y * 200 + x] = 0.5f;
        var template = new GrayImage(40, 40);
        Array.Fill(template.Data, 0.5f);
        var m = RegionMatcher.Find(template, scene);
        Assert.Equal((80, 30), (m.X, m.Y));
        Assert.True(m.Score > 0.99f, $"score {m.Score}");
    }
}
