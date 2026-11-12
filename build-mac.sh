#!/usr/bin/env bash
# Compatibility entry point for the archive-edition macOS build.
set -euo pipefail
exec bash "$(cd "$(dirname "$0")" && pwd)/scripts/build-archive-mac.sh" "$@"
