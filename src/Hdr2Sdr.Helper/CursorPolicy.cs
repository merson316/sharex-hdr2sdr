using System.Text.Json;
using Hdr2Sdr.Core.Config;

namespace Hdr2Sdr.Helper;

/// <summary>Resolves the effective "include cursor" decision: settings.json, with "auto" following ShareX's own setting.</summary>
public static class CursorPolicy
{
    public static bool IncludeCursor(string settingsPath, string shareXApplicationConfigPath)
    {
        var (settings, _) = SettingsFile.Load(settingsPath);
        if (settings.Cursor == "on") return true;
        if (settings.Cursor == "off") return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(shareXApplicationConfigPath));
            if (doc.RootElement.TryGetProperty("DefaultTaskSettings", out JsonElement task)
                && task.TryGetProperty("CaptureSettings", out JsonElement cap)
                && cap.TryGetProperty("ShowCursor", out JsonElement show))
                return show.ValueKind == JsonValueKind.True;
        }
        catch
        {
            // fall through
        }
        return false;
    }
}
