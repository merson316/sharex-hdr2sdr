using System.IO.Compression;
using Hdr2Sdr.Core.Imaging;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class PngTests
{
    [Fact]
    public void Crc32_matches_reference_value()
    {
        // CRC-32 of "123456789" is the standard check value 0xCBF43926.
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Decodes_every_filter_type(int filter)
    {
        var (rgba, w, h) = Png.DecodeRgba8(File.ReadAllBytes(Path.Combine("Fixtures", $"filter{filter}.png")));
        Assert.Equal(4, w);
        Assert.Equal(3, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                Assert.Equal((byte)(x * 60), rgba[i]);
                Assert.Equal((byte)(y * 100), rgba[i + 1]);
                Assert.Equal((byte)((x * y * 37) % 256), rgba[i + 2]);
                Assert.Equal(255, rgba[i + 3]);
            }
    }

    [Fact]
    public void Rgba8_round_trips()
    {
        int w = 5, h = 3;
        var src = new byte[w * h * 4];
        var rng = new Random(7);
        rng.NextBytes(src);
        byte[] png = Png.EncodeRgba8(src, w, h);
        var (back, bw, bh) = Png.DecodeRgba8(png);
        Assert.Equal(w, bw);
        Assert.Equal(h, bh);
        Assert.Equal(src, back);
    }

    [Fact]
    public void Rejects_non_png()
        => Assert.Throws<InvalidDataException>(() => Png.DecodeRgba8(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

    [Fact]
    public void Rgb16_encodes_header_and_raw_rows()
    {
        int w = 2, h = 1;
        byte[] rgb16 = { 0xFF, 0xFF, 0x00, 0x00, 0x80, 0x00, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC };
        byte[] png = Png.EncodeRgb16(rgb16, w, h);
        // IHDR data starts at offset 16: width(4) height(4) depth colourType ...
        Assert.Equal(16, png[16 + 8]);   // bit depth
        Assert.Equal(2, png[16 + 9]);    // colour type RGB
        // IDAT: after signature(8) + IHDR chunk (4+4+13+4 = 25) => offset 33: length(4) "IDAT"
        int idatLen = (png[33] << 24) | (png[34] << 16) | (png[35] << 8) | png[36];
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(png, 37, 4));
        using var z = new ZLibStream(new MemoryStream(png, 41, idatLen), CompressionMode.Decompress);
        var raw = new byte[1 + w * 6];
        int read = 0;
        while (read < raw.Length) { int n = z.Read(raw, read, raw.Length - read); if (n <= 0) break; read += n; }
        Assert.Equal(raw.Length, read);
        Assert.Equal(0, raw[0]);                    // filter type none
        Assert.Equal(rgb16, raw[1..]);
    }
}
