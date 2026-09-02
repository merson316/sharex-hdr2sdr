using System.Runtime.InteropServices;

namespace Hdr2Sdr.App.Display;

public sealed record DisplayInfo(string GdiDeviceName, float SdrWhiteNits, bool AdvancedColorEnabled);

/// <summary>Reads per-monitor SDR white level and advanced-colour (HDR) state via the DisplayConfig API.</summary>
public static class DisplayConfigInterop
{
    private const uint QdcOnlyActivePaths = 2;
    private const uint InfoGetSourceName = 1;
    private const uint InfoGetAdvancedColorInfo = 9;
    private const uint InfoGetSdrWhiteLevel = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshRateNumerator; public uint refreshRateDenominator; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public uint type; public uint size; public Luid adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel { public DeviceInfoHeader header; public uint sdrWhiteLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo { public DeviceInfoHeader header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo info);

    /// <summary>Keyed by GDI device name such as \\.\DISPLAY1 (matches DXGI OutputDescription.DeviceName).</summary>
    public static Dictionary<string, DisplayInfo> Query()
    {
        var result = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint numPaths, out uint numModes) != 0) return result;
        var paths = new PathInfo[numPaths];
        var modes = new ModeInfo[numModes];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return result;

        for (int i = 0; i < numPaths; i++)
        {
            PathInfo p = paths[i];
            var name = new SourceDeviceName
            {
                header = new DeviceInfoHeader { type = InfoGetSourceName, size = (uint)Marshal.SizeOf<SourceDeviceName>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrEmpty(name.viewGdiDeviceName)) continue;

            float sdrWhite = 80f;
            var wl = new SdrWhiteLevel
            {
                header = new DeviceInfoHeader { type = InfoGetSdrWhiteLevel, size = (uint)Marshal.SizeOf<SdrWhiteLevel>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref wl) == 0 && wl.sdrWhiteLevel > 0) sdrWhite = wl.sdrWhiteLevel / 1000f * 80f;

            bool hdr = false;
            var ac = new AdvancedColorInfo
            {
                header = new DeviceInfoHeader { type = InfoGetAdvancedColorInfo, size = (uint)Marshal.SizeOf<AdvancedColorInfo>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref ac) == 0) hdr = (ac.value & 0x2) != 0;   // bit 1 = advancedColorEnabled

            result[name.viewGdiDeviceName] = new DisplayInfo(name.viewGdiDeviceName, sdrWhite, hdr);
        }
        return result;
    }
}
