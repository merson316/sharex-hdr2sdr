# hdr2sdr for ShareX

Natural-looking SDR screenshots from an HDR desktop, as a [ShareX](https://getsharex.com/) post-capture action.

ShareX captures the screen with GDI. On a Windows 11 desktop running in HDR mode that yields an
8-bit image in which HDR content (games, HDR video, bright UI) is clipped and washed out, and the
Windows Snipping Tool's own HDR conversion tends to come out overexposed. `hdr2sdr.exe` runs after
every ShareX capture and fixes the image:

1. Re-captures the screen in 16-bit float scRGB through DXGI Desktop Duplication.
2. Finds where ShareX's image came from by normalized cross-correlation against the fresh capture,
   on each monitor and, if needed, on the composited virtual desktop.
3. Tonemaps that region using the monitor's real SDR white level from Windows' HDR settings, so
   ordinary windows look exactly like a non-HDR screenshot and only HDR highlights are compressed.
4. Overwrites the saved file in the same format ShareX used and puts the result on the clipboard.

Region, window, monitor and multi-monitor fullscreen captures are all handled. If anything goes
wrong the tool exits without touching ShareX's file or the clipboard.

An optional resident helper (`hdr2sdr-helper.exe`, tray icon) makes the result exact even for
moving content: it freezes the HDR frame the instant you press a ShareX hotkey, includes the mouse
cursor, and offers a settings dialog with a live preview of your last capture.

## Requirements

- Windows 10/11 with at least one display in HDR mode (SDR-only setups just get a no-op).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (ShareX already needs it).
- ShareX 15 or newer (tested with 21.0).

## Install

1. Download `hdr2sdr.exe` (and, if you want it, `hdr2sdr-helper.exe`) from the
   [latest release](../../releases/latest) and put them somewhere permanent, for example
   `C:\Users\<you>\Tools\hdr2sdr\`.
2. Register it as a ShareX action. Either run the helper with ShareX closed
   (`python3 tools/install_sharex_action.py`, works from WSL or Windows Python; it backs up the
   config first), or do it in the ShareX UI:
   - Task settings > Actions > Add
     - Name: `HDR to SDR`
     - File path: the exe
     - Arguments: `"$input"`
     - Extensions: `png, jpg, jpeg, bmp, gif, tif, tiff, webp`
     - Hidden window: on
   - Task settings > After capture tasks: tick **Perform actions** (keep "Copy image to clipboard"
     and "Save image to file" on).

That's it. Every capture is now processed automatically; the clipboard ends up holding the
tonemapped image because the action runs after ShareX's own clipboard copy.

3. Optional helper: put `hdr2sdr-helper.exe` next to `hdr2sdr.exe` and the installer registers it as a
   logon task and starts it (`--no-helper` to skip). Or just run it. A tray icon appears; double-click
   it for settings.

## Settings

Defaults reproduce the plain behaviour. Everything can be changed in
`%LOCALAPPDATA%\hdr2sdr\settings.json`, most easily through the helper's settings dialog, which
re-tonemaps your last capture live as you move the sliders:

```json
{
  "tonemap": "desktop", "exposure": 1.0, "knee": 1.0,
  "sdrWhiteNits": null, "peakNits": null,
  "jpegQuality": 0.9, "webpQuality": 90,
  "cursor": "auto", "hdrSidecar": "none", "carryAnnotations": true,
  "useHelper": true, "helperKeyboardHook": true,
  "helperRingMs": 250, "helperRingFrames": 12, "helperHistoryMs": 0
}
```

Command-line flags override the file; the file overrides the defaults. `cursor: auto` follows
ShareX's own "show cursor" option (cursor capture needs the helper). `hdrSidecar: jxr` also saves the
raw HDR region as a `.jxr` next to the SDR file, the same scRGB JPEG XR format Game Bar writes, which
Windows Photos shows in HDR. `webpQuality: 101` means lossless.

## The helper

Without the helper, the action re-captures the screen when it runs, which is after you finished
drawing the region, so a video or game shows a later frame. The helper keeps a Desktop Duplication
session open and, when you press one of ShareX's capture hotkeys, freezes the frame at that instant
and hands it to the action over a named pipe. The action falls back to a live capture whenever the
helper is not running or its snapshot does not belong to this capture.

After the trigger the helper keeps recording for `helperRingMs` (up to `helperRingFrames` frames on the
GPU) and the action picks the recorded frame that matches ShareX's capture best, so the two line up to
the frame. With `helperHistoryMs` above zero the helper also keeps that much recent history at all
times (one frame of VRAM per ~16 ms); then captures started from ShareX's tray menu or command line are
aligned too, because the helper notices ShareX's region window appearing and reaches back into the
history. With history on you can turn `helperKeyboardHook` off entirely.

Privacy: by default the helper installs a low-level keyboard hook to notice ShareX's hotkeys. It only
compares each key-down against the combinations read from ShareX's own `HotkeysConfig.json`; keys are
never stored or logged. Frames live in memory only, the newest snapshot for at most two minutes. Pause
it from the tray menu whenever you like, or disable the hook in the settings dialog.

## Command line

```
hdr2sdr.exe <input> [options]
hdr2sdr.exe --list-outputs
hdr2sdr.exe --capture-all --dump-dir <dir>

  --tonemap desktop|hable|aces  operator (default desktop)
  --exposure <float>            linear gain before tonemapping (default 1.0)
  --knee <0..1>                 desktop operator: where the BT.2390 roll-off starts, as a
                                fraction of SDR white; 1.0 = exact SDR, clip above (default)
  --sdr-white <nits>            override the monitor's SDR white level
  --peak <nits>                 override the monitor's peak luminance
  --no-clipboard                do not touch the clipboard
  --output <path>               write here instead of overwriting the input
  --dump-dir <dir>              write raw captures and previews for diagnosis
  --verbose                     progress on stderr
```

Exit codes: 0 ok (or nothing to do), 2 bad arguments, 3 capture failed, 4 region not found,
5 write/clipboard failed. The last run is logged to `%LOCALAPPDATA%\hdr2sdr\last.log`.

## Tonemapping

- **desktop** (default): scales the capture so Windows' SDR white level maps to white. Everything at
  or below SDR white is pixel-identical to a non-HDR screenshot; brighter pixels are clipped on
  luminance with hue preserved. `--knee 0.5` switches to a BT.2390 roll-off starting at half SDR
  white, which keeps highlight detail in HDR video and games at the cost of slightly darkening
  pure-white SDR windows. (A roll-off that ends at SDR white has to start well below it, so exact SDR
  and highlight roll-off are mutually exclusive; the flag lets you pick.)
- **hable** and **aces**: filmic curves for game captures, tuned with `--exposure`.

To change the defaults for ShareX, add flags to the action's arguments, e.g.
`"$input" --tonemap hable --exposure 1.2`.

## Build from source

Requires the .NET 9 SDK. Everything except the DXGI/WIC/clipboard layer is cross-platform and
unit-tested, so the whole thing builds and tests on Linux/WSL and produces a Windows exe:

```
dotnet test tests/Hdr2Sdr.Core.Tests
dotnet publish src/Hdr2Sdr.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
```

`tools/publish.sh` builds both executables and, on WSL, copies them to `C:\Users\<you>\Tools\hdr2sdr`.
Without a local SDK, `tools/build-with-docker.sh` runs the same script inside the project's builder
image (`ghcr.io/merson316/sharex-hdr2sdr/builder`, published by CI from `tools/builder.Dockerfile`;
set `BUILDER_LOCAL=1` to build the image yourself) and leaves the executables in `dist/`.

Layout: `src/Hdr2Sdr.Core` (colour maths, tonemappers, PNG codec, region matcher, CLI parsing,
settings, cursor compositing, snapshot protocol), `src/Hdr2Sdr.Windows` (Desktop Duplication via
[Vortice](https://github.com/amerkoleci/Vortice.Windows), DisplayConfig, WIC and libwebp image I/O,
JPEG XR, Win32 clipboard, helper client), `src/Hdr2Sdr.App` (the action), `src/Hdr2Sdr.Helper`
(tray helper), `tests/Hdr2Sdr.Core.Tests` (xunit).

## Limitations

- Without the helper, anything that moved between the hotkey and the end of your region selection
  (video frames, animations) shows its later state, and the cursor is not included. The region is
  still located correctly as long as part of it stayed put; if everything changed, the match fails
  and ShareX's image is kept. With the helper both problems go away.
- DRM-protected content captures black.
- Games in true exclusive fullscreen cannot be captured by Desktop Duplication (the log then says so
  and ShareX's image is kept); use borderless windowed mode. Most current games run as a borderless
  flip under Windows' fullscreen optimisations and work fine.
- Captures started without a ShareX hotkey (tray menu, command line) get a live re-capture unless
  `helperHistoryMs` is on; they cannot be frozen at the right instant otherwise. Non-region captures
  started that way (active window, monitor) are not detectable at all and always re-capture live.
- Edits made in ShareX's image editor before saving (arrows, text, blur, pixelate, highlights) are
  carried over onto the tonemapped result by comparing ShareX's image with ours, but only over SDR
  content: on and right around HDR surfaces (video, games) Windows renders GDI's copy through its own
  tone curve, so differences there cannot be told from edits and are left alone. A resize in the
  editor defeats the match and keeps ShareX's image. `--no-annotations` turns the carry-over off.

## License

MIT, see [LICENSE](LICENSE).
