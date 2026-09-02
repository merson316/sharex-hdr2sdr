#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
PROJECT_DIR=$(realpath "$SCRIPT_DIR/..")

# Use the published builder image unless BUILDER_LOCAL=1; fall back to a local build when the pull fails.
IMAGE="${BUILDER_IMAGE:-ghcr.io/merson316/sharex-hdr2sdr/builder:latest}"
if [ "${BUILDER_LOCAL:-0}" = "1" ] || ! docker pull -q "$IMAGE" >/dev/null 2>&1; then
    echo "building the builder image locally"
    IMAGE="$(docker build -q -f "$SCRIPT_DIR/builder.Dockerfile" "$SCRIPT_DIR")"
fi

docker run --rm \
    -v "$PROJECT_DIR:/opt/project" \
    -w /opt/project \
    "$IMAGE" \
    ./tools/publish.sh
