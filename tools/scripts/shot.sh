#!/usr/bin/env bash
# shot.sh <name>  -- screencap + pull into notes/proof/render/ (override OUT=...)
# Set ADB_SERIAL to target a specific device (see tools/scripts/env.sh).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/env.sh"

OUT="${OUT:-$DISHWASHER_REPO_ROOT/notes/proof/render}"
mkdir -p "$OUT"

ADB_ARGS=()
[ -n "${ADB_SERIAL:-}" ] && ADB_ARGS=(-s "$ADB_SERIAL")

"$ADB" "${ADB_ARGS[@]}" shell screencap -p /sdcard/_shot.png
"$ADB" "${ADB_ARGS[@]}" pull /sdcard/_shot.png "$OUT/$1.png" >/dev/null
echo "$OUT/$1.png"
