using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Match;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class TileVoteTests
{
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

    /// <summary>Crops (x,y,w,h) from scene, then overwrites the right `changedFraction` of it with pixels from `other`.</summary>
    private static GrayImage CropWithChangedRight(GrayImage scene, GrayImage other, int x, int y, int w, int h, float changedFraction)
    {
        var t = new GrayImage(w, h);
        int changedFrom = (int)(w * (1f - changedFraction));
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                t.Data[j * w + i] = i >= changedFrom
                    ? other.Data[(y + j) * other.Width + x + i]
                    : MathF.Pow(scene.Data[(y + j) * scene.Width + x + i], 1.2f);
        return t;
    }

    [Fact]
    public void Whole_template_ncc_fails_when_half_changed_but_tile_vote_succeeds()
    {
        var scene = Synthetic(640, 480, seed: 11);
        var other = Synthetic(640, 480, seed: 12);
        var template = CropWithChangedRight(scene, other, 100, 50, 300, 200, changedFraction: 0.5f);

        var plain = RegionMatcher.Find(template, scene);
        Assert.True(plain.Score < RegionMatcher.AcceptThreshold, $"whole-template score unexpectedly high: {plain.Score}");

        var voted = RegionMatcher.FindRobust(template, scene);
        Assert.Equal((100, 50), (voted.X, voted.Y));
        Assert.True(voted.Score > RegionMatcher.AcceptThreshold, $"score {voted.Score}");
        Assert.InRange(voted.Coverage, 0.3f, 0.7f);
    }

    [Fact]
    public void Robust_find_uses_the_fast_path_when_the_whole_template_matches()
    {
        var scene = Synthetic(640, 480, seed: 13);
        var template = CropWithChangedRight(scene, scene, 220, 140, 200, 120, changedFraction: 0f);
        var m = RegionMatcher.FindRobust(template, scene);
        Assert.Equal((220, 140), (m.X, m.Y));
        Assert.Equal(1f, m.Coverage);
    }

    [Fact]
    public void Same_size_capture_with_a_changed_block_still_matches_at_origin()
    {
        var scene = Synthetic(400, 300, seed: 14);
        var other = Synthetic(400, 300, seed: 15);
        var template = CropWithChangedRight(scene, other, 0, 0, 400, 300, changedFraction: 0.4f);
        var m = RegionMatcher.FindRobust(template, scene);
        Assert.Equal((0, 0), (m.X, m.Y));
        Assert.True(m.Score > RegionMatcher.AcceptThreshold, $"score {m.Score}");
    }

    [Fact]
    public void Unrelated_template_is_still_rejected()
    {
        var scene = Synthetic(640, 480, seed: 16);
        var other = Synthetic(640, 480, seed: 17);
        var template = CropWithChangedRight(other, other, 100, 100, 300, 200, changedFraction: 0f);
        var m = RegionMatcher.FindRobust(template, scene);
        Assert.True(m.Score < RegionMatcher.AcceptThreshold, $"score {m.Score} coverage {m.Coverage}");
    }

    [Fact]
    public void Small_template_falls_back_to_plain_matching()
    {
        var scene = Synthetic(300, 200, seed: 18);
        var template = CropWithChangedRight(scene, scene, 40, 30, 24, 20, changedFraction: 0f);
        var m = RegionMatcher.FindRobust(template, scene);
        Assert.Equal((40, 30), (m.X, m.Y));
    }
}
