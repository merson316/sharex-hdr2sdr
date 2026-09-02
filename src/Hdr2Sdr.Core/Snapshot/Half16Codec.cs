using Hdr2Sdr.Core.Imaging;

namespace Hdr2Sdr.Core.Snapshot;

/// <summary>FloatImage &lt;-&gt; little-endian IEEE half samples, used to ship snapshots between processes.</summary>
public static class Half16Codec
{
    public static byte[] Encode(FloatImage img)
    {
        var bytes = new byte[img.Data.Length * 2];
        for (int i = 0; i < img.Data.Length; i++)
        {
            ushort h = BitConverter.HalfToUInt16Bits((Half)img.Data[i]);
            bytes[i * 2] = (byte)h;
            bytes[i * 2 + 1] = (byte)(h >> 8);
        }
        return bytes;
    }

    public static FloatImage Decode(ReadOnlySpan<byte> bytes, int width, int height)
    {
        var img = new FloatImage(width, height);
        if (bytes.Length != img.Data.Length * 2) throw new ArgumentException("Half buffer size does not match dimensions.");
        for (int i = 0; i < img.Data.Length; i++)
            img.Data[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8)));
        return img;
    }
}
