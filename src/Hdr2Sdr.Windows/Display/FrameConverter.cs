using Hdr2Sdr.Core.Color;
using Hdr2Sdr.Core.Imaging;
using Vortice.DXGI;

namespace Hdr2Sdr.Windows.Display;

public static unsafe class FrameConverter
{
    /// <summary>Converts a mapped desktop texture to linear scRGB (1.0 = 80 nits) and rotates it into desktop orientation.</summary>
    public static FloatImage ToScRgb(IntPtr data, int rowPitch, int width, int height, Format format, ModeRotation rotation)
    {
        var img = new FloatImage(width, height);
        float[] dst = img.Data;
        byte* basePtr = (byte*)data;

        Parallel.For(0, height, y =>
        {
            byte* row = basePtr + (long)y * rowPitch;
            int di = y * width * 3;
            switch (format)
            {
                case Format.R16G16B16A16_Float:
                {
                    ushort* p = (ushort*)row;
                    for (int x = 0; x < width; x++)
                    {
                        dst[di + x * 3] = Transfer.HalfToFloat(p[x * 4]);
                        dst[di + x * 3 + 1] = Transfer.HalfToFloat(p[x * 4 + 1]);
                        dst[di + x * 3 + 2] = Transfer.HalfToFloat(p[x * 4 + 2]);
                    }
                    break;
                }
                case Format.R10G10B10A2_UNorm:
                {
                    // HDR10 swap chain: PQ-encoded BT.2020. Decode to nits, convert to BT.709, scale to scRGB.
                    uint* p = (uint*)row;
                    for (int x = 0; x < width; x++)
                    {
                        uint v = p[x];
                        float r = Transfer.PqDecode((v & 0x3FF) / 1023f);
                        float g = Transfer.PqDecode(((v >> 10) & 0x3FF) / 1023f);
                        float b = Transfer.PqDecode(((v >> 20) & 0x3FF) / 1023f);
                        Transfer.Bt2020To709(ref r, ref g, ref b);
                        dst[di + x * 3] = r / 80f;
                        dst[di + x * 3 + 1] = g / 80f;
                        dst[di + x * 3 + 2] = b / 80f;
                    }
                    break;
                }
                default:
                {
                    // B8G8R8A8_UNorm: sRGB-encoded SDR desktop. 1.0 = SDR white = 80 nits in scRGB terms.
                    for (int x = 0; x < width; x++)
                    {
                        dst[di + x * 3] = Transfer.SrgbDecode(row[x * 4 + 2] / 255f);
                        dst[di + x * 3 + 1] = Transfer.SrgbDecode(row[x * 4 + 1] / 255f);
                        dst[di + x * 3 + 2] = Transfer.SrgbDecode(row[x * 4] / 255f);
                    }
                    break;
                }
            }
        });

        return rotation switch
        {
            ModeRotation.Rotate90 => img.RotateClockwise(1),
            ModeRotation.Rotate180 => img.RotateClockwise(2),
            ModeRotation.Rotate270 => img.RotateClockwise(3),
            _ => img,
        };
    }
}
