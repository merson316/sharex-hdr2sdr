namespace Hdr2Sdr.Core.Imaging;

/// <summary>Maps rectangles between desktop orientation and the unrotated texture of a rotated output.</summary>
public static class RotationMath
{
    /// <summary>
    /// Converts a rectangle in desktop (rotated) coordinates to the texture rectangle that, once converted and rotated
    /// by <see cref="FloatImage.RotateClockwise"/> with the same quarter turns, yields the desktop rectangle.
    /// rotationQuarterTurns: 0 = identity, 1 = the desktop is the texture rotated 90° clockwise, 2, 3.
    /// texW/texH are the unrotated texture dimensions.
    /// </summary>
    public static bool DesktopRectToTexture(int rotationQuarterTurns, int texW, int texH, int x, int y, int w, int h, out int tx, out int ty, out int tw, out int th)
    {
        switch (((rotationQuarterTurns % 4) + 4) % 4)
        {
            case 0: tx = x; ty = y; tw = w; th = h; return true;
            case 1: tx = y; ty = texH - x - w; tw = h; th = w; return true;   // dest (dx,dy) <- source (dy, H-1-dx)
            case 2: tx = texW - x - w; ty = texH - y - h; tw = w; th = h; return true;
            default: tx = texW - y - h; ty = x; tw = h; th = w; return true;  // dest (dx,dy) <- source (W-1-dy, dx)
        }
    }
}
