using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hdr2Sdr.Core.Config;

public static class SettingsFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Loads settings; a missing file is not an error. Returns sanitized settings and an error/fix description or null.</summary>
    public static (Settings Settings, string? Error) Load(string path)
    {
        if (!File.Exists(path)) return (new Settings(), null);
        Settings parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new Settings(), $"cannot read {path}: {e.Message}; using defaults");
        }
        Settings clean = parsed.Sanitized(out List<string> fixes);
        return (clean, fixes.Count == 0 ? null : $"{path}: " + string.Join("; ", fixes));
    }

    public static void Save(string path, Settings settings)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
