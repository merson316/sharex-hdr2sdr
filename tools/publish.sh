#!/usr/bin/env bash
# Builds the framework-dependent single-file hdr2sdr.exe into dist/ and, when a Windows
# user profile is reachable, copies it to %USERPROFILE%\Tools\hdr2sdr (override with INSTALL_DIR).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then export PATH="$HOME/.dotnet:$PATH"; fi
cd "$(dirname "$0")/.."
dotnet test tests/Hdr2Sdr.Core.Tests -c Release
dotnet publish src/Hdr2Sdr.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
dotnet publish src/Hdr2Sdr.Helper -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
echo "built: dist/hdr2sdr.exe ($(stat -c %s dist/hdr2sdr.exe) bytes), dist/hdr2sdr-helper.exe ($(stat -c %s dist/hdr2sdr-helper.exe) bytes)"

if [ -z "${INSTALL_DIR:-}" ]; then
  if [ -x /mnt/c/Windows/System32/cmd.exe ]; then
    WINUSER=$(cd /mnt/c && /mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n')
    [ -n "$WINUSER" ] && INSTALL_DIR="/mnt/c/Users/$WINUSER/Tools/hdr2sdr"
  elif [ "$(uname -s)" = "MINGW64_NT" ] || [ -n "${USERPROFILE:-}" ]; then
    INSTALL_DIR="${USERPROFILE//\\//}/Tools/hdr2sdr"
  fi
fi
if [ -n "${INSTALL_DIR:-}" ]; then
  mkdir -p "$INSTALL_DIR"
  # a running helper locks its exe; stop it, copy, and the logon task or the user restarts it
  if [ -x /mnt/c/Windows/System32/taskkill.exe ]; then (cd /mnt/c && /mnt/c/Windows/System32/taskkill.exe /IM hdr2sdr-helper.exe /F >/dev/null 2>&1 || true); sleep 1; fi
  cp dist/hdr2sdr.exe "$INSTALL_DIR/hdr2sdr.exe"
  cp dist/hdr2sdr-helper.exe "$INSTALL_DIR/hdr2sdr-helper.exe"
  echo "installed: $INSTALL_DIR/hdr2sdr.exe and hdr2sdr-helper.exe"
  if [ -x /mnt/c/Windows/System32/schtasks.exe ] && (cd /mnt/c && /mnt/c/Windows/System32/schtasks.exe /Query /TN hdr2sdr-helper >/dev/null 2>&1); then
    (cd /mnt/c && /mnt/c/Windows/System32/schtasks.exe /Run /TN hdr2sdr-helper >/dev/null 2>&1) && echo "helper restarted"
  fi
else
  echo "no Windows profile found; copy dist/hdr2sdr.exe wherever you like and point the ShareX action at it"
fi
