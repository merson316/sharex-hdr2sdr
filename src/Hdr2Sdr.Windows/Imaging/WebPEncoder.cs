using System.Runtime.InteropServices;

namespace Hdr2Sdr.Windows.Imaging;

/// <summary>Thin P/Invoke over libwebp's simple encoding API (libwebp.dll from the Imazen native runtime package).</summary>
public static unsafe class WebPEncoder
{
    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint WebPEncodeRGBA(byte* rgba, int width, int height, int stride, float qualityFactor, out byte* output);

    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint WebPEncodeLosslessRGBA(byte* rgba, int width, int height, int stride, out byte* output);

    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WebPFree(void* ptr);

    /// <summary>Encodes RGBA8. quality 0-100 is lossy; 101 (Settings.WebpLossless) is lossless.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA buffer size does not match dimensions.");
        fixed (byte* p = rgba)
        {
            byte* output;
            nuint size = quality > 100
                ? WebPEncodeLosslessRGBA(p, width, height, width * 4, out output)
                : WebPEncodeRGBA(p, width, height, width * 4, Math.Clamp(quality, 0, 100), out output);
            if (size == 0 || output == null) throw new InvalidOperationException("libwebp failed to encode the image.");
            try
            {
                var bytes = new byte[size];
                Marshal.Copy((IntPtr)output, bytes, 0, (int)size);
                return bytes;
            }
            finally
            {
                WebPFree(output);
            }
        }
    }
}
