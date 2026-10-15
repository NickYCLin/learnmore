#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

NODE_BIN="${NODE_BIN:-node}"
NODE_MODULES="${NODE_MODULES:-$SCRIPT_DIR/node_modules}"

export NODE_PATH="${NODE_PATH:-$NODE_MODULES}"

"$NODE_BIN" "$SCRIPT_DIR/learnmore_mika_avatar_switch_smoke.cjs" "$@"
