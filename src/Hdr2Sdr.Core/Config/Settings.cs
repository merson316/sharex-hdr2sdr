using Hdr2Sdr.Core.Tonemap;

namespace Hdr2Sdr.Core.Config;

/// <summary>Persistent user settings for the overlay.</summary>
public sealed record Settings
{
    public string Tonemap { get; init; } = "desktop";
    public float Exposure { get; init; } = 1f;
    public float Knee { get; init; } = 1f;
    /// <summary>null = the SDR white level Windows reports for the monitor.</summary>
    public float? SdrWhiteNits { get; init; }
    /// <summary>null = the peak luminance the monitor reports.</summary>
    public float? PeakNits { get; init; }

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
        return s;
    }

    public TonemapParams ToTonemapParams(float monitorSdrWhiteNits, float monitorPeakNits) => new()
    {
        SdrWhiteNits = SdrWhiteNits ?? monitorSdrWhiteNits,
        PeakNits = PeakNits ?? monitorPeakNits,
        Exposure = Exposure,
        Knee = Knee,
    };
}
