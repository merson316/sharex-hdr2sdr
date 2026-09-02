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

## Requirements

- Windows 10/11 with at least one display in HDR mode (SDR-only setups just get a no-op).
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (ShareX already needs it).
- ShareX 15 or newer (tested with 21.0).

## Install

1. Download `hdr2sdr.exe` from the [latest release](../../releases/latest) and put it somewhere
   permanent, for example `C:\Users\<you>\Tools\hdr2sdr\hdr2sdr.exe`.
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

`tools/publish.sh` does both and, on WSL, copies the exe to `C:\Users\<you>\Tools\hdr2sdr`.

Layout: `src/Hdr2Sdr.Core` (colour maths, tonemappers, PNG codec, region matcher, CLI parsing),
`src/Hdr2Sdr.App` (Desktop Duplication via [Vortice](https://github.com/amerkoleci/Vortice.Windows),
DisplayConfig, WIC image I/O, Win32 clipboard), `tests/Hdr2Sdr.Core.Tests` (xunit).

## Limitations

- The re-capture happens a fraction of a second after ShareX's; content that changed in between
  (video frames, animations) differs slightly, and if the region changed a lot the match fails and
  ShareX's image is kept.
- The mouse cursor is not included. DRM-protected content captures black.
- ShareX's completion toast shows up in the re-capture if it overlaps the region.
- Windows ships no WebP encoder, so a WebP file is left unchanged (the clipboard is still updated).

## License

MIT, see [LICENSE](LICENSE).
