using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class RotationMathTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Texture_rect_rotated_equals_desktop_crop(int turns)
    {
        // Texture 7x5 with unique values; desktop = texture rotated `turns` quarter turns clockwise.
        var tex = new FloatImage(7, 5);
        for (int i = 0; i < tex.Data.Length; i++) tex.Data[i] = i;
        FloatImage desktop = tex.RotateClockwise(turns);
        int x = 1, y = 2, w = 3, h = 2;
        if (x + w > desktop.Width || y + h > desktop.Height) { w = 2; h = 2; }
        FloatImage expected = desktop.Crop(x, y, w, h);

        Assert.True(RotationMath.DesktopRectToTexture(turns, tex.Width, tex.Height, x, y, w, h, out int tx, out int ty, out int tw, out int th));
        FloatImage actual = tex.Crop(tx, ty, tw, th).RotateClockwise(turns);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Data, actual.Data);
    }
}
