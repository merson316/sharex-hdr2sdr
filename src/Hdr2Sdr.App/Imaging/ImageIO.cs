using System.Runtime.InteropServices;
using Hdr2Sdr.Core.Imaging;
using SharpGen.Runtime.Win32;
using Vortice.WIC;

namespace Hdr2Sdr.App.Imaging;

/// <summary>
/// Reads any image the Windows Imaging Component can decode (PNG, JPEG, BMP, GIF, TIFF, WebP, HEIF, ...)
/// and writes back in the same container. PNG goes through the cross-platform codec in Core.
/// </summary>
public static class ImageIO
{
    private const uint CoinitMultithreaded = 0x0;
    private static bool _comInitialized;

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    private static void EnsureCom()
    {
        if (_comInitialized) return;
        CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);   // S_OK, S_FALSE (already) or RPC_E_CHANGED_MODE (STA) are all fine
        _comInitialized = true;
    }

    public static (byte[] Rgba, int Width, int Height) Load(string path)
    {
        if (IsPng(Path.GetExtension(path)))
        {
            try
            {
                return Png.DecodeRgba8(File.ReadAllBytes(path));
            }
            catch (InvalidDataException)
            {
                // 16-bit, interlaced or palette PNGs: let WIC handle them below.
            }
        }
        EnsureCom();
        using var factory = new IWICImagingFactory();
        using IWICBitmapDecoder decoder = factory.CreateDecoderFromFileName(path);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);
        using IWICFormatConverter converter = factory.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppRGBA).CheckError();
        var size = converter.Size;
        var rgba = new byte[size.Width * size.Height * 4];
        converter.CopyPixels((uint)(size.Width * 4), rgba);
        return (rgba, size.Width, size.Height);
    }

    /// <summary>Encodes to the container implied by the file extension. Throws if Windows has no encoder for it.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, string extension, float jpegQuality = 0.9f)
    {
        if (IsPng(extension)) return Png.EncodeRgba8(rgba, width, height);
        ContainerFormat container = Container(extension) ?? throw new NotSupportedException($"No encoder for '{extension}'.");

        EnsureCom();
        using var factory = new IWICImagingFactory();
        using IWICBitmap bitmap = factory.CreateBitmapFromMemory((uint)width, (uint)height, PixelFormat.Format32bppRGBA, rgba, (uint)(width * 4));
        using var ms = new MemoryStream();
        using IWICBitmapEncoder encoder = factory.CreateEncoder(container, ms);
        using IWICBitmapFrameEncode frame = encoder.CreateNewFrame(out IPropertyBag2 options);
        using (options)
        {
            if (container == ContainerFormat.Jpeg) options.Set("ImageQuality", jpegQuality);
            frame.Initialize(options).CheckError();
        }
        frame.SetSize((uint)width, (uint)height).CheckError();

        using IWICFormatConverter converter = factory.CreateFormatConverter();
        if (container == ContainerFormat.Gif)
        {
            using IWICPalette palette = factory.CreatePalette();
            palette.InitializeFromBitmap(bitmap, 256, false);
            frame.SetPixelFormat(PixelFormat.Format8bppIndexed);
            frame.SetPalette(palette);
            converter.Initialize(bitmap, PixelFormat.Format8bppIndexed, BitmapDitherType.ErrorDiffusion, palette, 0.0, BitmapPaletteType.Custom).CheckError();
        }
        else
        {
            Guid target = container is ContainerFormat.Jpeg or ContainerFormat.Bmp ? PixelFormat.Format24bppBGR : PixelFormat.Format32bppBGRA;
            frame.SetPixelFormat(target);
            converter.Initialize(bitmap, target).CheckError();
        }
        frame.WriteSource(converter).CheckError();
        frame.Commit();
        encoder.Commit();
        return ms.ToArray();
    }

    private static bool IsPng(string extension) => extension.Equals(".png", StringComparison.OrdinalIgnoreCase);

    private static ContainerFormat? Container(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => ContainerFormat.Jpeg,
        ".bmp" => ContainerFormat.Bmp,
        ".gif" => ContainerFormat.Gif,
        ".tif" or ".tiff" => ContainerFormat.Tiff,
        ".webp" => ContainerFormat.Webp,
        ".heic" or ".heif" => ContainerFormat.Heif,
        _ => null,
    };
}
