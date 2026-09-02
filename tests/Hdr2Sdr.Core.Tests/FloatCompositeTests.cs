using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class FloatCompositeTests
{
    private static FloatImage Solid(int w, int h, float v)
    {
        var img = new FloatImage(w, h);
        Array.Fill(img.Data, v);
        return img;
    }

    [Fact]
    public void Composite_places_float_tiles_and_fills_gaps_with_zero()
    {
        var tiles = new[]
        {
            new FloatImage.Tile(Solid(3, 2, 0.5f), 0, 0),
            new FloatImage.Tile(Solid(2, 3, 2.0f), 3, -1),
        };
        var (canvas, left, top) = FloatImage.Composite(tiles);
        Assert.Equal((0, -1, 5, 3), (left, top, canvas.Width, canvas.Height));
        Assert.Equal(0f, canvas.Data[0]);                       // desktop (0,-1): gap
        Assert.Equal(0.5f, canvas.Data[(1 * 5 + 0) * 3]);       // desktop (0,0)
        Assert.Equal(2.0f, canvas.Data[(0 * 5 + 4) * 3]);       // desktop (4,-1)
        Assert.Equal(2.0f, canvas.Data[(2 * 5 + 3) * 3 + 2]);   // desktop (3,1), blue channel
    }
}
