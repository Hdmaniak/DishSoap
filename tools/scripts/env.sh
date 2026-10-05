#!/usr/bin/env bash
# Source this for the managed MonoGame/Android toolchain.
#
#   source tools/scripts/env.sh
#
# Works in non-login shells (which do not read ~/.bashrc/.profile) and from any
# working directory.  Nothing here is machine-specific: every location can be
# overridden by exporting the matching variable *before* sourcing, e.g.
#
#   export ANDROID_SDK_DIR="$HOME/Android/Sdk"
#   export ANDROID_NDK_DIR="$HOME/android-ndk-r28b"
#   export JAVA_SDK_DIR="/usr/lib/jvm/java-21-openjdk-amd64"
#   source tools/scripts/env.sh
#
# See BUILDING.md §1 for the full list and recommended defaults.

# ---------------------------------------------------------------------------
# Repository root: this file lives in <repo>/tools/scripts/.
# ---------------------------------------------------------------------------
_scripts_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DISHWASHER_REPO_ROOT="$(cd "$_scripts_dir/../.." && pwd)"
unset _scripts_dir
export DISHWASHER_REPO_ROOT

# ---------------------------------------------------------------------------
# .NET SDK
# Default assumes the per-user install made by dotnet-install.sh ($HOME/.dotnet);
# override DOTNET_ROOT for a system-wide SDK.
# ---------------------------------------------------------------------------
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
case ":$PATH:" in
  *":$DOTNET_ROOT:"*) ;;
  *) export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH" ;;
esac

# Wine prefix used by MGCB's Android/OpenGL effect compiler (fxccs.dll +
# d3dcompiler).  See docs/notes/port/wine-shader-build.md.
export MGFXC_WINE_PATH="${MGFXC_WINE_PATH:-$HOME/.wine-mgcb}"

# ---------------------------------------------------------------------------
# Android SDK
# Resolution order: explicit ANDROID_SDK_DIR > ANDROID_HOME > ANDROID_SDK_ROOT
# > Android Studio's default $HOME/Android/Sdk.
# ---------------------------------------------------------------------------
export ANDROID_SDK_DIR="${ANDROID_SDK_DIR:-${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}}"

# ---------------------------------------------------------------------------
# Android NDK (r28b is the version this project targets)
# Resolution order: explicit ANDROID_NDK_DIR > ANDROID_NDK_HOME
# > $HOME/android-ndk-r28b.
# ---------------------------------------------------------------------------
export ANDROID_NDK_DIR="${ANDROID_NDK_DIR:-${ANDROID_NDK_HOME:-$HOME/android-ndk-r28b}}"

# ---------------------------------------------------------------------------
# JDK 21
# Resolution order: explicit JAVA_SDK_DIR > JAVA_HOME > the usual Debian/Ubuntu
# OpenJDK 21 path > the install prefix of `javac`/`java` on PATH.
# ---------------------------------------------------------------------------
if [ -z "${JAVA_SDK_DIR:-}" ]; then
  if [ -n "${JAVA_HOME:-}" ]; then
    JAVA_SDK_DIR="$JAVA_HOME"
  elif [ -d /usr/lib/jvm/java-21-openjdk-amd64 ]; then
    JAVA_SDK_DIR="/usr/lib/jvm/java-21-openjdk-amd64"
  elif command -v javac >/dev/null 2>&1; then
    JAVA_SDK_DIR="$(cd "$(dirname "$(readlink -f "$(command -v javac)")")/.." && pwd)"
  elif command -v java >/dev/null 2>&1; then
    JAVA_SDK_DIR="$(cd "$(dirname "$(readlink -f "$(command -v java)")")/.." && pwd)"
  else
    JAVA_SDK_DIR=""
  fi
fi
export JAVA_SDK_DIR

# Derived variables kept in sync with the ones above.
export ANDROID_HOME="$ANDROID_SDK_DIR"
export ANDROID_SDK_ROOT="$ANDROID_SDK_DIR"
export ANDROID_NDK_HOME="$ANDROID_NDK_DIR"
export JAVA_HOME="$JAVA_SDK_DIR"

# adb: explicit ADB override, else the SDK's platform-tools, else whatever is
# on PATH.
if [ -z "${ADB:-}" ]; then
  if [ -x "$ANDROID_SDK_DIR/platform-tools/adb" ]; then
    ADB="$ANDROID_SDK_DIR/platform-tools/adb"
  else
    ADB="$(command -v adb 2>/dev/null || true)"
  fi
fi
export ADB

# Optional target device for the helper scripts (deploy.sh, shot.sh).  Leave
# unset to let adb pick the only connected device; set it to a serial from
# `adb devices` when several are attached.
export ADB_SERIAL="${ADB_SERIAL:-}"

# A directory holding your own cleaned, extracted game files (data/, gfx/,
# sfx/, Resources/).  Used by the content tooling; point it at wherever you
# unpacked your copy — it is never committed.
export DISHWASHER_ASSETS_DIR="${DISHWASHER_ASSETS_DIR:-$HOME/dishwasher-assets-clean}"

# The Android target framework that works with this .NET 8 SDK + net8 API-34 workload.
export ANDROID_TFM="net8.0-android"
