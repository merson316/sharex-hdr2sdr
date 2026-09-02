using Hdr2Sdr.Core.Imaging;
using SharpGen.Runtime.Win32;
using Vortice.WIC;

namespace Hdr2Sdr.Windows.Imaging;

/// <summary>
/// Writes a linear scRGB float image as a JPEG XR file with 64bpp RGBA half pixels, the same layout
/// Xbox Game Bar uses for HDR screenshots, so Windows Photos shows it in HDR and tools can re-tonemap it.
/// </summary>
public static class JxrEncoder
{
    public static byte[] EncodeHalf(FloatImage img)
    {
        int w = img.Width, h = img.Height;
        var pixels = new byte[w * h * 8];
        for (int i = 0, p = 0; i < w * h; i++, p += 8)
        {
            Write(pixels, p, img.Data[i * 3]);
            Write(pixels, p + 2, img.Data[i * 3 + 1]);
            Write(pixels, p + 4, img.Data[i * 3 + 2]);
            Write(pixels, p + 6, 1f);
        }

        using var factory = new IWICImagingFactory();
        using var ms = new MemoryStream();
        using IWICBitmapEncoder encoder = factory.CreateEncoder(ContainerFormat.Wmp, ms);
        using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out IPropertyBag2 options);
        using (options)
        {
            options.Set("ImageQuality", 1.0f);   // 1.0 = lossless for the JPEG XR encoder
            frame.Initialize(options).CheckError();
        }
        frame.SetSize((uint)w, (uint)h).CheckError();
        frame.SetPixelFormat(PixelFormat.Format64bppRGBAHalf);
        frame.WritePixels((uint)h, (uint)(w * 8), pixels).CheckError();
        frame.Commit();
        encoder.Commit();
        return ms.ToArray();
    }

    /// <summary>Reads a JPEG XR (or any WIC image) back as linear RGBA floats; used by the verification harness.</summary>
    public static (float[] Rgba, int Width, int Height) DecodeFloat(byte[] file)
    {
        using var factory = new IWICImagingFactory();
        using var ms = new MemoryStream(file);
        using IWICBitmapDecoder decoder = factory.CreateDecoderFromStream(ms);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);
        using IWICFormatConverter converter = factory.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format128bppRGBAFloat).CheckError();
        var size = converter.Size;
        var floats = new float[size.Width * size.Height * 4];
        converter.CopyPixels((uint)(size.Width * 16), floats);
        return (floats, size.Width, size.Height);
    }

    private static void Write(byte[] dst, int offset, float value)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)value);
        dst[offset] = (byte)h;
        dst[offset + 1] = (byte)(h >> 8);
    }
}
