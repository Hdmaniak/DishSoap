#!/usr/bin/env bash
# Deploy the current APK and launch into a clean logcat.
# Set ADB_SERIAL to target a specific device (see tools/scripts/env.sh).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/env.sh"

# Built APK under the source tree in this repository; override with APK=...
APK="${APK:-$DISHWASHER_REPO_ROOT/src/Dishwasher/bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk}"

ADB_ARGS=()
[ -n "${ADB_SERIAL:-}" ] && ADB_ARGS=(-s "$ADB_SERIAL")

"$ADB" "${ADB_ARGS[@]}" install -r -d "$APK"
"$ADB" "${ADB_ARGS[@]}" shell am force-stop com.recomp.dishwasher
"$ADB" "${ADB_ARGS[@]}" logcat -c
"$ADB" "${ADB_ARGS[@]}" shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
echo "launched"
