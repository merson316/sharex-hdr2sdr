namespace Hdr2Sdr.Core.Cli;

public enum RunMode { Process, ListOutputs, CaptureAll, Help }

public sealed record CliOptions
{
    public RunMode Mode { get; init; } = RunMode.Process;
    public string? InputPath { get; init; }
    // Tonemapping options are null when the flag was not given, so settings.json can supply them.
    public string? Tonemap { get; init; }
    public float? Exposure { get; init; }
    public float? Knee { get; init; }
    public float? SdrWhiteNits { get; init; }
    public float? PeakNits { get; init; }
    public float? JpegQuality { get; init; }
    /// <summary>0-100, or 101 for lossless.</summary>
    public int? WebpQuality { get; init; }
    public string? Cursor { get; init; }
    public string? HdrSidecar { get; init; }
    public bool NoHelper { get; init; }
    public string? SettingsPath { get; init; }
    public bool NoClipboard { get; init; }
    public string? OutputPath { get; init; }
    public string? DumpDir { get; init; }
    public bool Verbose { get; init; }
}

public sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}
