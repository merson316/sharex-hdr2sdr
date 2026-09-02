#!/usr/bin/env bash
# Builds the single-file hdr2sdr.exe into dist/ and, when a Windows user profile is reachable,
# copies it to %USERPROFILE%\Tools\hdr2sdr (override with INSTALL_DIR) and restarts the logon task.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then export PATH="$HOME/.dotnet:$PATH"; fi
cd "$(dirname "$0")/.."
dotnet test tests/Hdr2Sdr.Core.Tests -c Release
dotnet publish src/Hdr2Sdr.Helper -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
echo "built: dist/hdr2sdr.exe ($(stat -c %s dist/hdr2sdr.exe) bytes)"

if [ -z "${INSTALL_DIR:-}" ] && [ -x /mnt/c/Windows/System32/cmd.exe ]; then
  WINUSER=$(cd /mnt/c && /mnt/c/Windows/System32/cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n')
  [ -n "$WINUSER" ] && INSTALL_DIR="/mnt/c/Users/$WINUSER/Tools/hdr2sdr"
fi
if [ -n "${INSTALL_DIR:-}" ]; then
  mkdir -p "$INSTALL_DIR"
  (cd /mnt/c && /mnt/c/Windows/System32/taskkill.exe /IM hdr2sdr.exe /F >/dev/null 2>&1 || true); sleep 1
  cp dist/hdr2sdr.exe "$INSTALL_DIR/hdr2sdr.exe"
  echo "installed: $INSTALL_DIR/hdr2sdr.exe"
  if (cd /mnt/c && /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command "Start-ScheduledTask -TaskName hdr2sdr" >/dev/null 2>&1); then
    echo "hdr2sdr restarted"
  else
    echo "no logon task yet: run python3 tools/install.py"
  fi
else
  echo "no Windows profile found; copy dist/hdr2sdr.exe wherever you like and run it"
fi
