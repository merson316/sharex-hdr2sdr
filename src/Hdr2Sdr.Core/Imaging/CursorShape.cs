namespace Hdr2Sdr.Core.Imaging;

/// <summary>DXGI_OUTDUPL_POINTER_SHAPE_TYPE values.</summary>
public enum CursorShapeType { Monochrome = 1, Color = 2, MaskedColor = 4 }

/// <summary>
/// A pointer shape as delivered by Desktop Duplication. For Monochrome, Height counts both the AND and
/// XOR mask rows (each mask is Height/2 rows of 1 bit per pixel); Color and MaskedColor are 32bpp BGRA.
/// </summary>
public sealed record CursorShape(CursorShapeType Type, int Width, int Height, int Pitch, byte[] Data, int HotspotX, int HotspotY)
{
    /// <summary>Visible height in pixels (monochrome shapes store two masks stacked).</summary>
    public int VisibleHeight => Type == CursorShapeType.Monochrome ? Height / 2 : Height;
}
