#!/usr/bin/env bash
# Builds the framework-dependent single-file hdr2sdr.exe into dist/ and copies it to the Windows install dir.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PATH=~/.dotnet:$PATH
cd "$(dirname "$0")/.."
dotnet test tests/Hdr2Sdr.Core.Tests -c Release
dotnet publish src/Hdr2Sdr.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/
INSTALL_DIR=${INSTALL_DIR:-/mnt/c/Users/<you>/Tools/hdr2sdr}
mkdir -p "$INSTALL_DIR"
cp dist/hdr2sdr.exe "$INSTALL_DIR/hdr2sdr.exe"
echo "installed: $INSTALL_DIR/hdr2sdr.exe ($(stat -c %s "$INSTALL_DIR/hdr2sdr.exe") bytes)"
