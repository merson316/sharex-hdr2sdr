using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class CompositeTests
{
    private static byte[] Px(RgbaImage.Canvas v, int x, int y) => v.Rgba[((y * v.Width + x) * 4)..((y * v.Width + x) * 4 + 4)];

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var d = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { d[i * 4] = r; d[i * 4 + 1] = g; d[i * 4 + 2] = b; d[i * 4 + 3] = 255; }
        return d;
    }

    [Fact]
    public void Crop_takes_the_right_rgba_pixels()
    {
        var src = new byte[4 * 3 * 4];
        for (int i = 0; i < 12; i++) { src[i * 4] = (byte)i; src[i * 4 + 3] = 255; }
        byte[] c = RgbaImage.Crop(src, 4, 3, 1, 1, 2, 2);
        Assert.Equal(16, c.Length);
        Assert.Equal(5, c[0]);      // (1,1) -> index 5
        Assert.Equal(6, c[4]);      // (2,1)
        Assert.Equal(9, c[8]);      // (1,2)
        Assert.Equal(10, c[12]);    // (2,2)
        Assert.Throws<ArgumentOutOfRangeException>(() => RgbaImage.Crop(src, 4, 3, 3, 0, 2, 1));
    }

    [Fact]
    public void Composite_places_tiles_and_fills_gaps_black()
    {
        // Two "monitors": A 3x2 at (0,0), B 2x3 at (3,-1). Virtual desktop = x 0..5, y -1..2 => 5x3.
        var tiles = new[]
        {
            new RgbaImage.Tile(Solid(3, 2, 10, 20, 30), 3, 2, 0, 0),
            new RgbaImage.Tile(Solid(2, 3, 40, 50, 60), 2, 3, 3, -1),
        };
        var v = RgbaImage.Composite(tiles);
        Assert.Equal((0, -1, 5, 3), (v.Left, v.Top, v.Width, v.Height));
        // (0,0) in virtual coords is desktop (0,-1): gap => black, alpha 255
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(v, 0, 0));
        // desktop (0,0) => virtual (0,1)
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, Px(v, 0, 1));
        // desktop (4,-1) => virtual (4,0)
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, Px(v, 4, 0));
        // desktop (1,1) => virtual (1,2); desktop (2,2) is outside the virtual desktop (B ends at y=2, A at y=2)
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, Px(v, 1, 2));
        // desktop (4,1) => virtual (4,2): still inside tile B (rows -1..1)
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, Px(v, 4, 2));
    }
}
