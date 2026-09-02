using System.Buffers.Binary;

namespace Hdr2Sdr.Core.Imaging;

/// <summary>Builds a CF_DIB payload: BITMAPINFOHEADER + bottom-up 32bpp BGRA rows.</summary>
public static class DibEncoder
{
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA buffer size does not match dimensions.");
        int stride = width * 4;
        var dib = new byte[40 + stride * height];
        var h = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h[0..], 40);              // biSize
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);           // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], height);          // biHeight, positive = bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(h[12..], 1);              // biPlanes
        BinaryPrimitives.WriteInt16LittleEndian(h[14..], 32);             // biBitCount
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 0);              // biCompression = BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], stride * height); // biSizeImage
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = 40 + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                dib[dst + x * 4] = rgba[src + x * 4 + 2];
                dib[dst + x * 4 + 1] = rgba[src + x * 4 + 1];
                dib[dst + x * 4 + 2] = rgba[src + x * 4];
                dib[dst + x * 4 + 3] = 255;
            }
        }
        return dib;
    }
}
