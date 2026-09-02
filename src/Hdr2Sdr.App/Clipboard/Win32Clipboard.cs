using System.ComponentModel;
using System.Runtime.InteropServices;
using Hdr2Sdr.Core.Imaging;

namespace Hdr2Sdr.App.Clipboard;

public static class Win32Clipboard
{
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>Replaces the clipboard contents with the image as CF_DIB and "PNG". Retries briefly if another app holds the clipboard.</summary>
    public static void SetImage(ReadOnlySpan<byte> rgba, int width, int height, byte[] png)
    {
        byte[] dib = DibEncoder.Encode(rgba, width, height);
        uint pngFormat = RegisterClipboardFormat("PNG");
        if (pngFormat == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClipboardFormat(PNG) failed");

        bool opened = false;
        for (int attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened) Thread.Sleep(50);
        }
        if (!opened) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenClipboard failed after 10 attempts");

        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed");
            Put(CfDib, dib);
            Put(pngFormat, png);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void Put(uint format, byte[] bytes)
    {
        IntPtr h = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed");
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            GlobalFree(h);
            throw new Win32Exception(err, "GlobalLock failed");
        }
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            GlobalFree(h);
            throw new Win32Exception(err, $"SetClipboardData({format}) failed");
        }
        // On success the clipboard owns the handle; do not free it.
    }
}
