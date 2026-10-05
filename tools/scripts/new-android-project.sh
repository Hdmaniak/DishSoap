#!/usr/bin/env bash
# Create a MonoGame Android project, retarget it to net8.0-android, and build an APK.
#
# Usage: scripts/new-android-project.sh <Name> <OutputDir> [--build]
#   e.g. scripts/new-android-project.sh MyGame "$HOME/games/MyGame" --build
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "$SCRIPT_DIR/env.sh"

NAME="${1:?usage: $0 <Name> <OutputDir> [--build]}"
OUT="${2:?usage: $0 <Name> <OutputDir> [--build]}"
DO_BUILD="${3:-}"

echo "==> dotnet new mgandroid -n $NAME -o $OUT"
dotnet new mgandroid -n "$NAME" -o "$OUT"

CSPROJ="$OUT/$NAME.csproj"
echo "==> retarget $CSPROJ -> $ANDROID_TFM"
sed -i "s#<TargetFramework>net9.0-android</TargetFramework>#<TargetFramework>${ANDROID_TFM}</TargetFramework>#" "$CSPROJ"
grep -n TargetFramework "$CSPROJ"

if [[ "$DO_BUILD" == "--build" ]]; then
  echo "==> dotnet build -f $ANDROID_TFM"
  ( cd "$OUT" && dotnet build -f "$ANDROID_TFM" \
      -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
      -p:AndroidNdkDirectory="$ANDROID_NDK_DIR" \
      -p:JavaSdkDirectory="$JAVA_SDK_DIR" )
  echo "==> APK(s):"
  find "$OUT/bin" -name '*.apk' -exec ls -la {} \;
fi
