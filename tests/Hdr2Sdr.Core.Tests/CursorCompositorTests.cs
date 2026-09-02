using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class CursorCompositorTests
{
    private static FloatImage Gray(int w, int h, float v)
    {
        var img = new FloatImage(w, h);
        Array.Fill(img.Data, v);
        return img;
    }

    private static (float r, float g, float b) Px(FloatImage img, int x, int y)
        => (img.Data[(y * img.Width + x) * 3], img.Data[(y * img.Width + x) * 3 + 1], img.Data[(y * img.Width + x) * 3 + 2]);

    [Fact]
    public void Color_cursor_blends_with_alpha_and_uses_sdr_white_scale()
    {
        // 2x2 BGRA cursor: opaque red, transparent, half-transparent white, opaque black
        byte[] data =
        {
            0, 0, 255, 255,   0, 0, 0, 0,
            255, 255, 255, 128,   0, 0, 0, 255,
        };
        var shape = new CursorShape(CursorShapeType.Color, 2, 2, 8, data, 0, 0);
        var frame = Gray(4, 4, 0.5f);
        CursorCompositor.Draw(frame, shape, 1, 1, sdrWhiteScale: 2.5f);
        var red = Px(frame, 1, 1);
        Assert.Equal(2.5, red.r, 0.01);       // opaque sRGB red -> linear 1.0 * 2.5 (SDR white in scRGB units)
        Assert.Equal(0, red.g, 0.01);
        Assert.Equal((0.5f, 0.5f, 0.5f), Px(frame, 2, 1));   // transparent: untouched
        var half = Px(frame, 1, 2);
        Assert.InRange(half.r, 1.4f, 1.6f);   // 0.5 * 0.5 + 2.5 * 0.502
        Assert.Equal((0f, 0f, 0f), Px(frame, 2, 2));
        Assert.Equal((0.5f, 0.5f, 0.5f), Px(frame, 0, 0));   // outside the cursor
    }

    [Fact]
    public void Hotspot_and_edges_are_respected()
    {
        byte[] data = { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 };
        var shape = new CursorShape(CursorShapeType.Color, 2, 2, 8, data, 1, 1);
        var frame = Gray(3, 3, 0f);
        CursorCompositor.Draw(frame, shape, 0, 0, 1f);   // hotspot at (1,1) => top-left of cursor at (-1,-1): only (0,0) is on screen
        Assert.Equal(1f, Px(frame, 0, 0).r, 0.01);
        Assert.Equal(0f, Px(frame, 1, 0).r);
        Assert.Equal(0f, Px(frame, 0, 1).r);
        CursorCompositor.Draw(frame, shape, 3, 3, 1f);   // top-left at (2,2): only (2,2) on screen
        Assert.Equal(1f, Px(frame, 2, 2).r, 0.01);
    }

    [Fact]
    public void Monochrome_cursor_applies_and_xor_masks()
    {
        // 8x2 monochrome: rows 0..1 = AND mask, rows 2..3 = XOR mask (height in the shape is the total, 4)
        // pixel 0: AND 1, XOR 0 => screen unchanged; pixel 1: AND 0, XOR 0 => black; pixel 2: AND 0, XOR 1 => white; pixel 3: AND 1, XOR 1 => inverted
        byte[] data =
        {
            0b1001_0000, 0b0000_0000,   // AND rows (2 rows of 8 px, 1 byte per row)
            0b0011_0000, 0b0000_0000,   // XOR rows
        };
        var shape = new CursorShape(CursorShapeType.Monochrome, 8, 4, 1, data, 0, 0);
        var frame = Gray(8, 2, 0.25f);
        CursorCompositor.Draw(frame, shape, 0, 0, 1f);
        Assert.Equal(0.25f, Px(frame, 0, 0).r, 0.01);   // unchanged
        Assert.Equal(0f, Px(frame, 1, 0).r, 0.01);      // black
        Assert.Equal(1f, Px(frame, 2, 0).r, 0.01);      // white (SDR white scale 1)
        Assert.Equal(0.75f, Px(frame, 3, 0).r, 0.01);   // inverted
    }

    [Fact]
    public void Masked_color_cursor_copies_or_xors()
    {
        // 2x1 masked colour: alpha 0 => copy colour; alpha 0xFF => XOR with screen
        byte[] data = { 0, 0, 255, 0,   255, 255, 255, 255 };
        var shape = new CursorShape(CursorShapeType.MaskedColor, 2, 1, 8, data, 0, 0);
        var frame = Gray(2, 1, 1f);
        CursorCompositor.Draw(frame, shape, 0, 0, 1f);
        Assert.Equal(1f, Px(frame, 0, 0).r, 0.01);    // copied red
        Assert.Equal(0f, Px(frame, 0, 0).g, 0.01);
        Assert.Equal(0f, Px(frame, 1, 0).r, 0.01);    // white XOR white = black
    }
}
