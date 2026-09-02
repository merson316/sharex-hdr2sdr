using Hdr2Sdr.Core.Color;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Core.Imaging;

public static class PixelConvert
{
    /// <summary>Tonemaps and sRGB-encodes to RGBA8 (alpha 255). Rows are processed in parallel.</summary>
    public static byte[] ToRgba8(FloatImage img, ITonemapper tm)
    {
        int w = img.Width, h = img.Height;
        var rgba = new byte[w * h * 4];
        float[] src = img.Data;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3, d = (y * w + x) * 4;
                float r = src[s], g = src[s + 1], b = src[s + 2];
                tm.Map(ref r, ref g, ref b);
                rgba[d] = ToByte(Transfer.SrgbEncode(r));
                rgba[d + 1] = ToByte(Transfer.SrgbEncode(g));
                rgba[d + 2] = ToByte(Transfer.SrgbEncode(b));
                rgba[d + 3] = 255;
            }
        });
        return rgba;
    }

    /// <summary>BT.709 luma of the sRGB-encoded bytes, scaled to [0,1]. Used only for matching.</summary>
    public static GrayImage ToGray(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA buffer size does not match dimensions.");
        var g = new GrayImage(width, height);
        const float inv = 1f / 255f;
        for (int i = 0, p = 0; i < width * height; i++, p += 4)
            g.Data[i] = (0.2126f * rgba[p] + 0.7152f * rgba[p + 1] + 0.0722f * rgba[p + 2]) * inv;
        return g;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
