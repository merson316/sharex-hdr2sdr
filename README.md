# hdr2sdr

HDR-to-SDR post-capture action for [ShareX](https://getsharex.com/) on Windows 11 HDR desktops.

ShareX captures with GDI, which on an HDR desktop produces clipped, washed-out images of HDR content.
`hdr2sdr.exe` runs after each capture, re-captures the screen in 16-bit float through DXGI Desktop
Duplication, locates the region ShareX captured, tonemaps it using the monitor's real SDR white level,
overwrites the saved PNG and copies the result to the clipboard.

## Install

1. Build from WSL: `tools/publish.sh` (needs the .NET 9 SDK at `~/.dotnet`). This writes `dist/hdr2sdr.exe`
   and copies it to `C:\Users\<you>\Tools\hdr2sdr\hdr2sdr.exe`. The exe needs the .NET 9 Desktop Runtime,
   which ShareX already requires.
2. Close ShareX, then `python3 tools/install_sharex_action.py`, then start ShareX again.
   Or in ShareX: Task settings > Actions > Add: name `HDR to SDR`, path to the exe, arguments `"$input"`,
   extensions `png, jpg, jpeg, bmp, gif, tif, tiff, webp`, hidden window on; and tick "Perform actions"
   under After capture tasks.

## Usage

Nothing to do: every region, window, monitor and fullscreen capture is processed automatically,
including fullscreen captures that span several monitors and regions that straddle two of them.
The saved file is rewritten in whatever format ShareX chose (PNG, JPEG, BMP, GIF, TIFF; WebP is
decoded but Windows has no WebP encoder, so a WebP file is left as is while the clipboard is updated).
The clipboard always holds a PNG plus a DIB of the tonemapped image. Failures leave ShareX's own
image in place.

Command line (see `hdr2sdr.exe --help`):

    hdr2sdr.exe <input.png> [--tonemap desktop|hable|aces] [--exposure 1.0] [--knee 1.0]
                [--sdr-white nits] [--peak nits] [--no-clipboard] [--output path] [--dump-dir dir] [--verbose]
    hdr2sdr.exe --list-outputs
    hdr2sdr.exe --capture-all --dump-dir <dir>

## Tonemapping

- `desktop` (default): scales so Windows' SDR white level maps to white. Everything at or below SDR
  white is identical to a non-HDR screenshot; brighter pixels are clipped with hue preserved.
  `--knee 0.5` enables a BT.2390 roll-off starting at half SDR white, which keeps highlight detail
  in HDR video/games at the cost of slightly darkening pure-white SDR windows.
- `hable`, `aces`: filmic curves for game captures; tune with `--exposure`.

## Diagnostics

Log of the last run: `%LOCALAPPDATA%\hdr2sdr\last.log`. Exit codes: 0 ok, 2 bad arguments,
3 capture failed, 4 region not found, 5 write/clipboard failed. `--dump-dir` writes the raw capture
(16-bit linear PNG, 65535 = monitor peak) and the SDR preview per output.

## Limitations

Content that changed between ShareX's capture and the re-capture differs slightly; if the region
changed a lot (video, scrolling) the match can fail and the original is kept. Pixels on SDR monitors
are passed through unchanged; only HDR monitors are re-tonemapped. The cursor is not included. ShareX's completion toast appears in the
re-capture if it overlaps the region. DRM-protected content captures black.
