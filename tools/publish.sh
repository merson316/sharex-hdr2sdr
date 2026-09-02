#!/usr/bin/env bash
# Builds the framework-dependent single-file hdr2sdr.exe into dist/ and, when a Windows
# user profile is reachable, copies it to %USERPROFILE%\Tools\hdr2sdr (override with INSTALL_DIR).
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then export PATH="$HOME/.dotnet:$PATH"; fi
cd "$(dirname "$0")/.."
dotnet test tests/Hdr2Sdr.Core.Tests -c Release
dotnet publish src/Hdr2Sdr.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
echo "built: dist/hdr2sdr.exe ($(stat -c %s dist/hdr2sdr.exe) bytes)"

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
  cp dist/hdr2sdr.exe "$INSTALL_DIR/hdr2sdr.exe"
  echo "installed: $INSTALL_DIR/hdr2sdr.exe"
else
  echo "no Windows profile found; copy dist/hdr2sdr.exe wherever you like and point the ShareX action at it"
fi
