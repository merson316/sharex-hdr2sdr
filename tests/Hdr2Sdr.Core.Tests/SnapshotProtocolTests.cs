using Hdr2Sdr.Core.Imaging;
using Hdr2Sdr.Core.Snapshot;
using Xunit;

namespace Hdr2Sdr.Core.Tests;

public class SnapshotProtocolTests
{
    [Fact]
    public void Half_codec_round_trips_within_half_precision()
    {
        var img = new FloatImage(3, 2);
        float[] values = { 0f, 0.5f, 1f, 2.5f, 12.25f, -0.125f, 100f, 0.001f, 1000f, 3.14159f, 7f, 0.75f, 0.1f, 0.2f, 0.3f, 0.4f, 0.6f, 0.9f };
        Array.Copy(values, img.Data, 18);
        byte[] bytes = Half16Codec.Encode(img);
        Assert.Equal(18 * 2, bytes.Length);
        FloatImage back = Half16Codec.Decode(bytes, 3, 2);
        for (int i = 0; i < 18; i++) Assert.Equal(values[i], back.Data[i], Math.Abs(values[i]) * 0.001 + 1e-4);
    }

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

    [Fact]
    public void Snapshot_header_round_trips_as_json()
    {
        var h = new SnapshotHeader(
            TakenUtc: new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc),
            Outputs: new List<SnapshotOutput>
            {
                new("\\\\.\\DISPLAY1", 0, 0, 3440, 1440, true, 212f, 456f, new CursorRect(100, 200, 32, 32)),
                new("\\\\.\\DISPLAY5", 3440, -422, 1440, 2560, false, 80f, 270f, null),
            });
        string json = h.ToJson();
        SnapshotHeader back = SnapshotHeader.FromJson(json);
        Assert.Equal(h, back);
        Assert.False(back.Overlay);
        Assert.True(SnapshotHeader.FromJson((h with { Overlay = true }).ToJson()).Overlay);
        Assert.DoesNotContain('\n', json);   // one line: the protocol is line-delimited
    }
}
