namespace Hdr2Sdr.Core.Imaging;

/// <summary>Row-major single-channel float image, values nominally in [0,1].</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; set; }

    public GrayImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        Width = width;
        Height = height;
        Data = new float[checked(width * height)];
    }

    /// <summary>Box-average downsample by an integer factor. Trailing rows/columns that do not fill a box are dropped.</summary>
    public GrayImage Downsample(int factor)
    {
        if (factor <= 1) return this;
        int nw = Width / factor, nh = Height / factor;
        if (nw == 0 || nh == 0) throw new ArgumentOutOfRangeException(nameof(factor), "Image too small for this downsample factor.");
        var result = new GrayImage(nw, nh);
        float inv = 1f / (factor * factor);
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                float sum = 0f;
                for (int j = 0; j < factor; j++)
                {
                    int row = (y * factor + j) * Width + x * factor;
                    for (int i = 0; i < factor; i++) sum += Data[row + i];
                }
                result.Data[y * nw + x] = sum * inv;
            }
        return result;
    }
}
