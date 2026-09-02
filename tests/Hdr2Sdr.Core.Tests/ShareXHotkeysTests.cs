using Hdr2Sdr.Core.ShareX;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class ShareXHotkeysTests
{
    [Fact]
    public void Parses_sharex_capture_hotkeys_and_ignores_other_jobs()
    {
        const string json = """
        {
          "Hotkeys": [
            { "TaskSettings": { "Job": "RectangleRegion" }, "HotkeyInfo": { "Hotkey": "PrintScreen, Control", "Win": false } },
            { "TaskSettings": { "Job": "PrintScreen" }, "HotkeyInfo": { "Hotkey": "PrintScreen, Shift", "Win": false } },
            { "TaskSettings": { "Job": "ActiveWindow" }, "HotkeyInfo": { "Hotkey": "PrintScreen, Alt", "Win": false } },
            { "TaskSettings": { "Job": "ActiveMonitor" }, "HotkeyInfo": { "Hotkey": "M, Control, Shift", "Win": true } },
            { "TaskSettings": { "Job": "ScreenRecorder" }, "HotkeyInfo": { "Hotkey": "R, Control, Shift", "Win": false } },
            { "TaskSettings": { "Job": "None" }, "HotkeyInfo": { "Hotkey": "None", "Win": false } }
          ]
        }
        """;
        List<HotkeyCombo> combos = ShareXHotkeys.Parse(json);
        Assert.Equal(4, combos.Count);
        Assert.Contains(combos, c => c.Job == "RectangleRegion" && c.VirtualKey == 0x2C && c.Ctrl && !c.Shift && !c.Alt && !c.Win);
        Assert.Contains(combos, c => c.Job == "ActiveMonitor" && c.VirtualKey == (int)'M' && c.Ctrl && c.Shift && c.Win);
        Assert.DoesNotContain(combos, c => c.Job == "ScreenRecorder");
    }

    [Fact]
    public void Combo_matches_exact_modifier_state()
    {
        var c = new HotkeyCombo(0x2C, Ctrl: true, Shift: false, Alt: false, Win: false, Job: "RectangleRegion");
        Assert.True(c.Matches(0x2C, ctrl: true, shift: false, alt: false, win: false));
        Assert.False(c.Matches(0x2C, ctrl: true, shift: true, alt: false, win: false));
        Assert.False(c.Matches(0x2C, ctrl: false, shift: false, alt: false, win: false));
        Assert.False(c.Matches(0x2D, ctrl: true, shift: false, alt: false, win: false));
    }
}
