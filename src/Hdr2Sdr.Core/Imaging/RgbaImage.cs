namespace Hdr2Sdr.Core.Imaging;

/// <summary>Helpers for RGBA8 byte buffers: cropping and compositing tiles onto a virtual desktop.</summary>
public static class RgbaImage
{
    /// <summary>An RGBA8 image placed at (Left, Top) in desktop coordinates.</summary>
    public readonly record struct Tile(byte[] Rgba, int Width, int Height, int Left, int Top);

    /// <summary>The union of a set of tiles, with gaps filled opaque black. Left/Top are in desktop coordinates.</summary>
    public sealed record Canvas(byte[] Rgba, int Left, int Top, int Width, int Height);

    public static byte[] Crop(ReadOnlySpan<byte> rgba, int width, int height, int x, int y, int w, int h)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA buffer size does not match dimensions.");
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Crop ({x},{y},{w}x{h}) is outside the {width}x{height} image.");
        var result = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            rgba.Slice(((y + row) * width + x) * 4, w * 4).CopyTo(result.AsSpan(row * w * 4, w * 4));
        return result;
    }

    public static Canvas Composite(IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) throw new ArgumentException("At least one tile is required.", nameof(tiles));
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (Tile t in tiles)
        {
            if (t.Rgba.Length != t.Width * t.Height * 4) throw new ArgumentException("Tile buffer size does not match its dimensions.");
            left = Math.Min(left, t.Left);
            top = Math.Min(top, t.Top);
            right = Math.Max(right, t.Left + t.Width);
            bottom = Math.Max(bottom, t.Top + t.Height);
        }
        int width = right - left, height = bottom - top;
        var rgba = new byte[checked(width * height * 4)];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;   // opaque black background
        foreach (Tile t in tiles)
        {
            int dx = t.Left - left, dy = t.Top - top;
            for (int row = 0; row < t.Height; row++)
                Array.Copy(t.Rgba, row * t.Width * 4, rgba, ((dy + row) * width + dx) * 4, t.Width * 4);
        }
        return new Canvas(rgba, left, top, width, height);
    }
}
