using Hdr2Sdr.Core.Color;

namespace Hdr2Sdr.Core.Imaging;

/// <summary>Draws a Desktop Duplication pointer shape into a linear scRGB frame.</summary>
public static class CursorCompositor
{
    /// <summary>
    /// Draws the cursor with its hotspot at (x, y). Cursor colours are sRGB-decoded and multiplied by
    /// sdrWhiteScale (SDR white in scRGB units, e.g. 2.5 for a 200-nit SDR white) so the cursor sits at
    /// SDR white like the rest of the desktop. Pixels off the frame are skipped.
    /// </summary>
    public static void Draw(FloatImage frame, CursorShape shape, int x, int y, float sdrWhiteScale)
    {
        int left = x - shape.HotspotX, top = y - shape.HotspotY;
        int h = shape.VisibleHeight;
        for (int j = 0; j < h; j++)
        {
            int fy = top + j;
            if (fy < 0 || fy >= frame.Height) continue;
            for (int i = 0; i < shape.Width; i++)
            {
                int fx = left + i;
                if (fx < 0 || fx >= frame.Width) continue;
                int d = (fy * frame.Width + fx) * 3;
                switch (shape.Type)
                {
                    case CursorShapeType.Color:
                        BlendColor(frame.Data, d, shape, i, j, sdrWhiteScale);
                        break;
                    case CursorShapeType.MaskedColor:
                        MaskedColor(frame.Data, d, shape, i, j, sdrWhiteScale);
                        break;
                    case CursorShapeType.Monochrome:
                        Monochrome(frame.Data, d, shape, i, j, h, sdrWhiteScale);
                        break;
                }
            }
        }
    }

    private static void BlendColor(float[] px, int d, CursorShape s, int i, int j, float white)
    {
        int o = j * s.Pitch + i * 4;
        float a = s.Data[o + 3] / 255f;
        if (a <= 0f) return;
        float r = Transfer.SrgbDecode(s.Data[o + 2] / 255f) * white;
        float g = Transfer.SrgbDecode(s.Data[o + 1] / 255f) * white;
        float b = Transfer.SrgbDecode(s.Data[o] / 255f) * white;
        px[d] = px[d] * (1f - a) + r * a;
        px[d + 1] = px[d + 1] * (1f - a) + g * a;
        px[d + 2] = px[d + 2] * (1f - a) + b * a;
    }

    private static void MaskedColor(float[] px, int d, CursorShape s, int i, int j, float white)
    {
        int o = j * s.Pitch + i * 4;
        float r = Transfer.SrgbDecode(s.Data[o + 2] / 255f) * white;
        float g = Transfer.SrgbDecode(s.Data[o + 1] / 255f) * white;
        float b = Transfer.SrgbDecode(s.Data[o] / 255f) * white;
        if (s.Data[o + 3] == 0xFF)
        {
            // XOR with the screen: approximate in linear light as |screen - colour| (exact for black/white).
            px[d] = MathF.Abs(px[d] - r);
            px[d + 1] = MathF.Abs(px[d + 1] - g);
            px[d + 2] = MathF.Abs(px[d + 2] - b);
        }
        else
        {
            px[d] = r; px[d + 1] = g; px[d + 2] = b;
        }
    }

    private static void Monochrome(float[] px, int d, CursorShape s, int i, int j, int visibleHeight, float white)
    {
        int byteIndex = i >> 3, bit = 7 - (i & 7);
        bool and = ((s.Data[j * s.Pitch + byteIndex] >> bit) & 1) != 0;
        bool xor = ((s.Data[(j + visibleHeight) * s.Pitch + byteIndex] >> bit) & 1) != 0;
        if (and && !xor) return;                                   // transparent
        if (!and && !xor) { px[d] = px[d + 1] = px[d + 2] = 0f; return; }   // black
        if (!and) { px[d] = px[d + 1] = px[d + 2] = white; return; }        // white
        px[d] = white - px[d]; px[d + 1] = white - px[d + 1]; px[d + 2] = white - px[d + 2];   // invert
    }
}
