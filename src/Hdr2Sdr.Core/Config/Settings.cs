using Hdr2Sdr.Core.Cli;
using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Core.Config;

/// <summary>Persistent user settings. Defaults reproduce v0.1 behaviour. Command-line flags override per field.</summary>
public sealed record Settings
{
    public string Tonemap { get; init; } = "desktop";
    public float Exposure { get; init; } = 1f;
    public float Knee { get; init; } = 1f;
    public float? SdrWhiteNits { get; init; }
    public float? PeakNits { get; init; }
    public float JpegQuality { get; init; } = 0.9f;
    /// <summary>0-100 lossy quality; 101 means lossless.</summary>
    public int WebpQuality { get; init; } = 90;
    /// <summary>auto (follow ShareX's "show cursor"), on, off.</summary>
    public string Cursor { get; init; } = "auto";
    /// <summary>none or jxr.</summary>
    public string HdrSidecar { get; init; } = "none";
    public bool UseHelper { get; init; } = true;

    public const int WebpLossless = 101;
    public static readonly string[] CursorModes = { "auto", "on", "off" };
    public static readonly string[] SidecarModes = { "none", "jxr" };

    /// <summary>Clamps every field into its valid range; returns what was changed (empty when nothing).</summary>
    public Settings Sanitized(out List<string> fixes)
    {
        fixes = new List<string>();
        Settings s = this;
        if (!TonemapperFactory.Names.Contains(s.Tonemap.ToLowerInvariant())) { fixes.Add($"tonemap '{s.Tonemap}' -> desktop"); s = s with { Tonemap = "desktop" }; }
        else s = s with { Tonemap = s.Tonemap.ToLowerInvariant() };
        if (!(s.Exposure >= 0.01f && s.Exposure <= 16f)) { fixes.Add($"exposure {s.Exposure} -> clamped"); s = s with { Exposure = Math.Clamp(float.IsNaN(s.Exposure) ? 1f : s.Exposure, 0.01f, 16f) }; }
        if (!(s.Knee > 0f && s.Knee <= 1f)) { fixes.Add($"knee {s.Knee} -> clamped"); s = s with { Knee = Math.Clamp(float.IsNaN(s.Knee) ? 1f : s.Knee, 0.01f, 1f) }; }
        if (s.SdrWhiteNits is <= 0f) { fixes.Add("sdrWhiteNits <= 0 -> auto"); s = s with { SdrWhiteNits = null }; }
        if (s.PeakNits is <= 0f) { fixes.Add("peakNits <= 0 -> auto"); s = s with { PeakNits = null }; }
        if (!(s.JpegQuality >= 0.1f && s.JpegQuality <= 1f)) { fixes.Add($"jpegQuality {s.JpegQuality} -> clamped"); s = s with { JpegQuality = Math.Clamp(float.IsNaN(s.JpegQuality) ? 0.9f : s.JpegQuality, 0.1f, 1f) }; }
        if (s.WebpQuality < 0 || s.WebpQuality > WebpLossless) { fixes.Add($"webpQuality {s.WebpQuality} -> clamped"); s = s with { WebpQuality = Math.Clamp(s.WebpQuality, 0, WebpLossless) }; }
        if (!CursorModes.Contains(s.Cursor.ToLowerInvariant())) { fixes.Add($"cursor '{s.Cursor}' -> auto"); s = s with { Cursor = "auto" }; }
        else s = s with { Cursor = s.Cursor.ToLowerInvariant() };
        if (!SidecarModes.Contains(s.HdrSidecar.ToLowerInvariant())) { fixes.Add($"hdrSidecar '{s.HdrSidecar}' -> none"); s = s with { HdrSidecar = "none" }; }
        else s = s with { HdrSidecar = s.HdrSidecar.ToLowerInvariant() };
        return s;
    }

    /// <summary>Command-line values win wherever the flag was given.</summary>
    public Settings ApplyCli(CliOptions o) => this with
    {
        Tonemap = o.Tonemap ?? Tonemap,
        Exposure = o.Exposure ?? Exposure,
        Knee = o.Knee ?? Knee,
        SdrWhiteNits = o.SdrWhiteNits ?? SdrWhiteNits,
        PeakNits = o.PeakNits ?? PeakNits,
        JpegQuality = o.JpegQuality ?? JpegQuality,
        WebpQuality = o.WebpQuality ?? WebpQuality,
        Cursor = o.Cursor ?? Cursor,
        HdrSidecar = o.HdrSidecar ?? HdrSidecar,
        UseHelper = o.NoHelper ? false : UseHelper,
    };

    public TonemapParams ToTonemapParams(float monitorSdrWhiteNits, float monitorPeakNits) => new()
    {
        SdrWhiteNits = SdrWhiteNits ?? monitorSdrWhiteNits,
        PeakNits = PeakNits ?? monitorPeakNits,
        Exposure = Exposure,
        Knee = Knee,
    };

    /// <summary>True when the effective tonemap differs from the preview (exact-SDR clip with monitor values).</summary>
    public bool IsCustomTonemap => !(Tonemap == "desktop" && Knee >= 1f && Exposure == 1f && SdrWhiteNits == null && PeakNits == null);
}
