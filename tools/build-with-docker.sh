#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
PROJECT_DIR=$(realpath "$SCRIPT_DIR/..")

IMAGE="$(docker build -q -f "$SCRIPT_DIR/builder.Dockerfile" "$SCRIPT_DIR")"

docker run --rm \
    -v "$PROJECT_DIR:/opt/project" \
    -w /opt/project \
    "$IMAGE" \
    ./tools/publish.sh
