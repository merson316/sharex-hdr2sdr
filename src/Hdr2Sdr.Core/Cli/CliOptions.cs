namespace Hdr2Sdr.Core.Cli;

public enum RunMode { Process, ListOutputs, CaptureAll, Help }

public sealed record CliOptions
{
    public RunMode Mode { get; init; } = RunMode.Process;
    public string? InputPath { get; init; }
    public string Tonemap { get; init; } = "desktop";
    public float Exposure { get; init; } = 1f;
    public float Knee { get; init; } = 1f;
    public float? SdrWhiteNits { get; init; }
    public float? PeakNits { get; init; }
    public bool NoClipboard { get; init; }
    public string? OutputPath { get; init; }
    public string? DumpDir { get; init; }
    public bool Verbose { get; init; }
}

public sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}
