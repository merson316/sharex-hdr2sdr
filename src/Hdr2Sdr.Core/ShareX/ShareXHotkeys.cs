using System.Text.Json;

namespace Hdr2Sdr.Core.ShareX;

/// <summary>A ShareX hotkey as virtual-key code plus modifier flags.</summary>
public readonly record struct HotkeyCombo(int VirtualKey, bool Ctrl, bool Shift, bool Alt, bool Win, string Job)
{
    public bool Matches(int vk, bool ctrl, bool shift, bool alt, bool win)
        => vk == VirtualKey && ctrl == Ctrl && shift == Shift && alt == Alt && win == Win;
}

/// <summary>Reads the capture hotkeys out of ShareX's HotkeysConfig.json.</summary>
public static class ShareXHotkeys
{
    /// <summary>ShareX jobs whose hotkey freezes the screen for a still capture.</summary>
    public static readonly HashSet<string> CaptureJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrintScreen", "ActiveWindow", "ActiveMonitor", "RectangleRegion", "RectangleLight", "RectangleTransparent",
        "CustomRegion", "LastRegion", "WindowRectangle", "ScrollingCapture",
    };

    public static List<HotkeyCombo> Parse(string json)
    {
        var result = new List<HotkeyCombo>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!doc.RootElement.TryGetProperty("Hotkeys", out JsonElement hotkeys) || hotkeys.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement hk in hotkeys.EnumerateArray())
        {
            string job = hk.TryGetProperty("TaskSettings", out JsonElement ts) && ts.TryGetProperty("Job", out JsonElement j) ? j.GetString() ?? "" : "";
            if (!CaptureJobs.Contains(job)) continue;
            if (!hk.TryGetProperty("HotkeyInfo", out JsonElement info)) continue;
            string keys = info.TryGetProperty("Hotkey", out JsonElement k) ? k.GetString() ?? "None" : "None";
            bool win = info.TryGetProperty("Win", out JsonElement w) && w.ValueKind == JsonValueKind.True;
            if (TryParseKeys(keys, out int vk, out bool ctrl, out bool shift, out bool alt))
                result.Add(new HotkeyCombo(vk, ctrl, shift, alt, win, job));
        }
        return result;
    }

    /// <summary>Parses the System.Windows.Forms.Keys flags string ShareX stores, e.g. "PrintScreen, Control".</summary>
    private static bool TryParseKeys(string text, out int vk, out bool ctrl, out bool shift, out bool alt)
    {
        vk = 0; ctrl = shift = alt = false;
        foreach (string partRaw in text.Split(','))
        {
            string part = partRaw.Trim();
            switch (part)
            {
                case "": case "None": continue;
                case "Control": ctrl = true; continue;
                case "Shift": shift = true; continue;
                case "Alt": alt = true; continue;
            }
            if (KeyNames.TryGetValue(part, out int code)) vk = code;
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) vk = char.ToUpperInvariant(part[0]);
            else if (part.StartsWith('D') && part.Length == 2 && char.IsDigit(part[1])) vk = part[1];
            else if (part.StartsWith('F') && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24) vk = 0x70 + f - 1;
            else return false;
        }
        return vk != 0;
    }

    private static readonly Dictionary<string, int> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = 0x2C, ["Snapshot"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["Prior"] = 0x21, ["PageDown"] = 0x22, ["Next"] = 0x22, ["Pause"] = 0x13, ["Scroll"] = 0x91,
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Back"] = 0x08,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
        ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["Oemtilde"] = 0xC0, ["OemMinus"] = 0xBD, ["Oemplus"] = 0xBB, ["OemOpenBrackets"] = 0xDB, ["OemCloseBrackets"] = 0xDD,
        ["OemPipe"] = 0xDC, ["OemSemicolon"] = 0xBA, ["OemQuotes"] = 0xDE, ["Oemcomma"] = 0xBC, ["OemPeriod"] = 0xBE, ["OemQuestion"] = 0xBF,
    };
}
