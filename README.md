# hdr2sdr for ShareX

Correct SDR screenshots from an HDR desktop, for [ShareX](https://getsharex.com/) on Windows.

ShareX captures the screen with GDI. On a Windows 11 desktop running in HDR mode that yields an
8-bit image in which HDR content (games, HDR video, bright UI) is clipped and washed out, and the
Windows Snipping Tool's own conversion tends to come out overexposed.

`hdr2sdr.exe` is a small tray app that fixes this *before* ShareX captures. It keeps a Desktop
Duplication session open on each monitor and watches ShareX's own capture hotkeys. When you press one:

1. it freezes the current frame in 16-bit float scRGB,
2. tonemaps it using the SDR white level Windows reports for that monitor (so ordinary windows come
   out exactly like a non-HDR screenshot and only HDR highlights are compressed),
3. shows the result as a borderless window over each HDR monitor for a fraction of a second, and
4. starts the same ShareX capture job through ShareX's command line.

ShareX therefore photographs correct SDR pixels in the first place. Its region selector, editor,
effects, uploads and cursor drawing all work unchanged; there is nothing to fix afterwards. The
overlay disappears as soon as ShareX's selector appears (about 0.3 s) or right after an instant
capture (fullscreen, active window, monitor).

Idea and refinement by [Dragory](https://github.com/Dragory).

## Requirements

- Windows 10/11 with at least one display in HDR mode (on SDR-only setups the app does nothing).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (ShareX already needs it).
- ShareX 15 or newer (tested with 21.0). Hotkeys are read from ShareX's `HotkeysConfig.json`, so you
  keep configuring them in ShareX.

## Install

1. Download `hdr2sdr.exe` from the [latest release](../../releases/latest) and put it somewhere
   permanent, for example `C:\Users\<you>\Tools\hdr2sdr\hdr2sdr.exe`.
2. Run it once. A tray icon appears and your ShareX hotkeys now go through it. To start it at logon,
   tick "Start at logon" in its settings (double-click the tray icon), or run
   `python3 tools/install.py` from this repo, which registers the logon task and also removes the
   ShareX post-capture action that versions before 0.5 used.

That's it. There is no ShareX-side configuration.

## Using it

- **Hotkeys:** ShareX's capture hotkeys (region, active window, active monitor, fullscreen, last
  region, window, custom region, scrolling capture) are intercepted and replayed with the overlay.
- **Tray menu:** Capture > Region / Active window / Active monitor / Fullscreen / Last region start the
  same jobs with the overlay; Pause hands the hotkeys back to ShareX; Settings opens the dialog.
- **Command line:** `hdr2sdr.exe --capture RectangleRegion` (or another job name) asks the running
  instance to capture with the overlay, handy for other launchers.

Captures started from ShareX's own tray menu or command line bypass the overlay and come out as
ShareX makes them.

## Settings

Double-click the tray icon. Everything is stored in `%LOCALAPPDATA%\hdr2sdr\settings.json`:

```json
{ "tonemap": "desktop", "exposure": 1.0, "knee": 1.0, "sdrWhiteNits": null, "peakNits": null }
```

- **desktop** (default): scales the frame so Windows' SDR white level maps to white. Everything at or
  below SDR white is pixel-identical to a non-HDR screenshot; brighter pixels are clipped on
  luminance with hue preserved. `knee` below 1.0 switches to a BT.2390 roll-off starting at that
  fraction of SDR white, which keeps highlight detail in HDR video and games at the cost of slightly
  darkening pure-white SDR windows. (A roll-off that ends at SDR white has to start well below it, so
  exact SDR and highlight roll-off are mutually exclusive.)
- **hable** and **aces**: filmic curves for game captures, tuned with `exposure`.
- `sdrWhiteNits` / `peakNits` override what the monitor reports.

The dialog previews the last frozen frame re-tonemapped live as you move the sliders.

## Privacy

The app installs a low-level keyboard hook to notice ShareX's hotkeys. It only compares each key-down
against the combinations read from ShareX's own `HotkeysConfig.json`; keys are never stored or logged.
Frames live in memory only. Pause it from the tray menu whenever you like, or exit it.
Log: `%LOCALAPPDATA%\hdr2sdr\helper.log`.

## Build from source

Requires the .NET 9 SDK; builds on Linux/WSL and produces a Windows exe:

```
dotnet test tests/Hdr2Sdr.Core.Tests
dotnet publish src/Hdr2Sdr.Helper -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
```

`tools/publish.sh` does both and, on WSL, installs the exe to `C:\Users\<you>\Tools\hdr2sdr`.
Without a local SDK, `tools/build-with-docker.sh` runs the same inside the project's builder image
(`ghcr.io/merson316/sharex-hdr2sdr/builder`; `BUILDER_LOCAL=1` builds the image yourself).

Layout: `src/Hdr2Sdr.Core` (colour maths, tonemappers, settings, ShareX hotkey parsing; cross-platform,
unit-tested), `src/Hdr2Sdr.Windows` (Desktop Duplication via
[Vortice](https://github.com/amerkoleci/Vortice.Windows), DisplayConfig), `src/Hdr2Sdr.Helper` (the tray
app), `tests/Hdr2Sdr.Core.Tests` (xunit).

## Limitations

- Jobs replayed through ShareX's command line run with ShareX's default task settings, not per-hotkey
  overrides.
- HDR content looks SDR for the ~0.3 s the overlay is up; ShareX's selector covers it.
- Games in true exclusive fullscreen cannot be captured by Desktop Duplication; use borderless
  windowed mode (most current games already are, under Windows' fullscreen optimisations).
- DRM-protected content captures black.
- Captures started from ShareX's own tray menu or command line are not intercepted.

## License

MIT, see [LICENSE](LICENSE).
