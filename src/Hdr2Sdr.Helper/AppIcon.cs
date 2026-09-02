using System.Drawing;

namespace Hdr2Sdr.Helper;

public static class AppIcon
{
    private static Icon? _icon;

    /// <summary>The embedded multi-size application icon; falls back to the generic one if the resource is missing.</summary>
    public static Icon Get()
    {
        if (_icon != null) return _icon;
        using Stream? s = typeof(AppIcon).Assembly.GetManifestResourceStream("hdr2sdr.icon.ico");
        _icon = s != null ? new Icon(s) : SystemIcons.Application;
        return _icon;
    }
}
