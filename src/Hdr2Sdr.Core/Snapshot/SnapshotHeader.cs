using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hdr2Sdr.Core.Snapshot;

public sealed record CursorRect(int X, int Y, int Width, int Height);

/// <summary>One output in a snapshot. Pixel data (width*height*3 halves) follows the header on the wire, in list order.</summary>
public sealed record SnapshotOutput(string DeviceName, int Left, int Top, int Width, int Height, bool Hdr, float SdrWhiteNits, float PeakNits, CursorRect? Cursor);

/// <summary>Describes a snapshot; serialized as a single JSON line.</summary>
/// <param name="Overlay">True when the helper showed this frame as an SDR overlay for ShareX to capture, so ShareX's image is already correct.</param>
public sealed record SnapshotHeader(DateTime TakenUtc, List<SnapshotOutput> Outputs, bool Overlay = false)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SnapshotHeader FromJson(string json) => JsonSerializer.Deserialize<SnapshotHeader>(json, Json) ?? throw new InvalidDataException("empty snapshot header");

    public bool Equals(SnapshotHeader? other) => other != null && TakenUtc == other.TakenUtc && Overlay == other.Overlay && Outputs.SequenceEqual(other.Outputs);
    public override int GetHashCode() => HashCode.Combine(TakenUtc, Outputs.Count);
}
