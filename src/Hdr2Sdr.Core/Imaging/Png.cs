using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Hdr2Sdr.Core.Imaging;

/// <summary>Minimal PNG codec: decodes 8-bit non-interlaced grey/RGB/grey+alpha/RGBA; encodes RGBA8 and RGB16.</summary>
public static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static (byte[] Rgba, int Width, int Height) DecodeRgba8(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG file.");

        int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= file.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos, 4));
            string type = Encoding.ASCII.GetString(file, pos + 4, 4);
            int dataStart = pos + 8;
            if (len < 0 || dataStart + len + 4 > file.Length) throw new InvalidDataException("Truncated PNG chunk.");
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart + 4, 4));
                bitDepth = file[dataStart + 8];
                colorType = file[dataStart + 9];
                interlace = file[dataStart + 12];
            }
            else if (type == "IDAT")
            {
                idat.Write(file, dataStart, len);
            }
            pos = dataStart + len + 4;
            if (type == "IEND") break;
        }

        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no valid IHDR.");
        if (bitDepth != 8) throw new InvalidDataException($"Unsupported PNG bit depth {bitDepth}; only 8-bit PNGs are supported.");
        if (interlace != 0) throw new InvalidDataException("Interlaced PNGs are not supported.");
        int channels = colorType switch
        {
            0 => 1, 2 => 3, 4 => 2, 6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG colour type {colorType}.")
        };

        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != raw.Length) throw new InvalidDataException("PNG image data is truncated.");
        }

        var rgba = new byte[width * height * 4];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            Array.Copy(raw, rowStart + 1, cur, 0, stride);
            Unfilter(raw[rowStart], cur, prev, channels);
            for (int x = 0; x < width; x++)
            {
                int s = x * channels, d = (y * width + x) * 4;
                switch (channels)
                {
                    case 1: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = 255; break;
                    case 2: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = cur[s + 1]; break;
                    case 3: rgba[d] = cur[s]; rgba[d + 1] = cur[s + 1]; rgba[d + 2] = cur[s + 2]; rgba[d + 3] = 255; break;
                    default: rgba[d] = cur[s]; rgba[d + 1] = cur[s + 1]; rgba[d + 2] = cur[s + 2]; rgba[d + 3] = cur[s + 3]; break;
                }
            }
            (prev, cur) = (cur, prev);
        }
        return (rgba, width, height);
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (filter)
        {
            case 0: break;
            case 1: for (int i = bpp; i < n; i++) cur[i] += cur[i - bpp]; break;
            case 2: for (int i = 0; i < n; i++) cur[i] += prev[i]; break;
            case 3:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] += (byte)((a + prev[i]) >> 1);
                }
                break;
            case 4:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] += Paeth(a, b, c);
                }
                break;
            default: throw new InvalidDataException($"Bad PNG filter type {filter}.");
        }
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return (byte)a;
        return pb <= pc ? (byte)b : (byte)c;
    }

    public static byte[] EncodeRgba8(ReadOnlySpan<byte> rgba, int width, int height)
        => Encode(rgba, width, height, channels: 4, bytesPerSample: 1, colorType: 6);

    /// <summary>Encodes 16-bit RGB. Samples must already be big-endian (PNG byte order).</summary>
    public static byte[] EncodeRgb16(ReadOnlySpan<byte> rgb16BigEndian, int width, int height)
        => Encode(rgb16BigEndian, width, height, channels: 3, bytesPerSample: 2, colorType: 2);

    private static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, int bytesPerSample, int colorType)
    {
        int stride = width * channels * bytesPerSample;
        if (width <= 0 || height <= 0 || pixels.Length != stride * height)
            throw new ArgumentException("Pixel buffer size does not match the given dimensions.");

        using var ms = new MemoryStream();
        ms.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = (byte)(bytesPerSample * 8);
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            // Filter type 0 on every row; zlib "Optimal" keeps screenshots reasonably small.
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(pixels.Slice(y * stride, stride));
                }
            }
            WriteChunk(ms, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }
}
