# Building from source — "bring your own decompile"

This repository ships **only our own port code**. It does
**not** ship the game: no decompiled game C#, no artwork, audio, text or
data, and no retail package.

That means you can *run* the release APK immediately (see `README.md`), but to
*build* it you must supply your own legally obtained copy of the game and
decompile it yourself. This guide explains exactly how.

> **Short version.** Own the game → decompile `game.exe` to C# locally with
> `ilspycmd 9.1.0.7988` → drop the result in
> `src/Dishwasher/GameSource/` → re-apply the (small, but non-trivial) manual
> XNA 3 → MonoGame porting edits → build. The porting edits are **not**
> included, because they live inside the decompiled game code.

---

## 1. Prerequisites

| Component | Version used | Notes |
|---|---|---|
| OS | Ubuntu 26.04 x86_64 | any modern Linux with the below should work |
| .NET SDK | **8.0.425** | `~/.dotnet` via `dotnet-install.sh`; the `android` workload (band 8.0.400) must be installed |
| .NET Android workload | 34.0.154/8.0.100 | `dotnet workload install android` |
| Android SDK | platform **android-35**, build-tools 35.0.0 | the manifest targets SDK 35 |
| Android NDK | **r28b** | only needed for Release / AOT builds |
| JDK | **21** | e.g. `/usr/lib/jvm/java-21-openjdk-amd64` |
| MonoGame templates | 3.8.5.1 | for reference only; this repo is already a project |
| MonoGame content builder | `dotnet-mgcb` 3.8.5.1 | pinned in `src/Dishwasher/.config/dotnet-tools.json` |
| Wine | any recent | only needed to rebuild shaders (MGCB's Windows GL effect compiler); see `docs/notes/port/wine-shader-build.md` |
| ilspycmd | **9.1.0.7988** | newer ilspycmd needs .NET 9/10 and cannot be installed by the .NET 8 SDK |

`tools/scripts/env.sh` sets up the toolchain. It is **fully portable** — it
contains no machine-specific paths and works from any directory. Every location
can be overridden by exporting the matching variable *before* sourcing; the
defaults follow the common per-user layouts.

```sh
source tools/scripts/env.sh
```

| Variable | Default (if unset) | Purpose |
|---|---|---|
| `DOTNET_ROOT` | `$HOME/.dotnet` | .NET SDK root (per-user `dotnet-install.sh` or system-wide). `env.sh` prepends `$DOTNET_ROOT` and `$DOTNET_ROOT/tools` to `PATH`. |
| `MGFXC_WINE_PATH` | `$HOME/.wine-mgcb` | Wine prefix used by MGCB's Windows GL effect compiler. |
| `ANDROID_SDK_DIR` | `${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}` | Android SDK root (platform 35, build-tools 35.0.0). Exported as `ANDROID_HOME`/`ANDROID_SDK_ROOT` too. |
| `ANDROID_NDK_DIR` | `${ANDROID_NDK_HOME:-$HOME/android-ndk-r28b}` | Android NDK r28b root. Exported as `ANDROID_NDK_HOME` too. |
| `JAVA_SDK_DIR` | `$JAVA_HOME`, else `/usr/lib/jvm/java-21-openjdk-amd64`, else inferred from `javac` | JDK 21. Exported as `JAVA_HOME` too. |
| `ADB` | `$ANDROID_SDK_DIR/platform-tools/adb`, else `adb` on `PATH` | `adb` binary used by the helper scripts. |
| `ADB_SERIAL` | unset (adb picks the only device) | Optional target device serial, e.g. from `adb devices`. |
| `DISHWASHER_ASSETS_DIR` | `$HOME/dishwasher-assets-clean` | Directory holding **your** cleaned, extracted game files (`data/`, `gfx/`, `sfx/`, `Resources/`). Never committed. |

Example for a machine using Android Studio's defaults:

```sh
export ANDROID_SDK_DIR="$HOME/Android/Sdk"
export ANDROID_NDK_DIR="$HOME/android-ndk-r28b"
export JAVA_SDK_DIR="/usr/lib/jvm/java-21-openjdk-amd64"
source tools/scripts/env.sh
```

`env.sh` also exports `DISHWASHER_REPO_ROOT` (the repo root) for the helper
scripts. Nothing needs editing inside the script.

### 1.1 Install ilspycmd (the exact pin)

```sh
dotnet tool install -g ilspycmd --version 9.1.0.7988
# -> ~/.dotnet/tools/ilspycmd
```

Newer versions (11.x) are packaged for .NET 9/10 and will fail to run under the
.NET 8 runtime. 9.1.0.7988 is packaged for `net8.0` and works.

---

## 2. Obtain your own copy of the game

You must already own a legitimate copy of *The Dishwasher: Dead Samurai*
(Xbox 360 XBLA). This project neither provides nor links to the game.

The retail XBLA download is an STFS/LIVE ("XContent") package wrapped in a ZIP.
The game logic is a **.NET Compact Framework 3.5 / XNA Game Studio 3.x managed
assembly** (`game.exe`) — that is the part you decompile. It is not native
PowerPC code.

You can unpack the STFS package with the Python tool in this repo (it is our own
validated reader — see `tools/stfs_extract.py`):

```sh
python3 tools/stfs_extract.py "/path/to/Dishwasher, The - Dead Samurai (World) (XBLA).zip" \
    --extract ./extracted
```

The package also contains `game.exe.xex`, a thin native wrapper around the
managed `game.exe`. Extract the **managed** `game.exe` assembly from it. (The
XEX-unwrapping step used during development is not part of this repository; any
method that yields the embedded .NET assembly is fine — the result is a normal
.NET CF assembly whose IL begins with the `BSJB` metadata root.)

Place the assembled files somewhere outside the repo, e.g. `./managed/`.

---

## 3. Decompile to C#

```sh
ilspycmd --disable-updatecheck --nested-directories -p \
  -o /path/to/decompiled/game /path/to/managed/game.exe
```

This produces a C# project tree (namespaces `projectDish`, `dishX.Resources`,
plus one helper in `Microsoft.Xna.Framework`), roughly **64 files / ~60k LOC**.

### 3.1 Drop it into the repository

```sh
cp -r /path/to/decompiled/game/* src/Dishwasher/GameSource/
```

`GameSource/` is intentionally **git-ignored**: never commit it. The Android SDK
project glob will pick up the `.cs` files automatically once they are present.

> **Important:** this repository's `src/` was built from a decompile named with
> the same namespaces and file layout. If your decompiler emits different file
> names it will still compile, but the `// PORT` line references in the docs may
> not line up.

---

## 4. Re-apply the porting edits (required — not included)

The decompiled game targets **XNA 3.0**; the port targets **MonoGame 4.0 / .NET
8 for Android**. Those manual changes live *inside the game source*, so they
cannot be published and you must re-apply them. The full list is documented in
`docs/notes/port/PORT_NOTES.md`; the main mechanical edits are:

| Change | Approx. sites | What to do |
|---|---:|---|
| `SpriteBatch.Begin(SpriteBlendMode…)` | 159 | replace with `Begin(SpriteSortMode…, BlendState…)` (`AlphaBlend`→`BlendState.AlphaBlend`, `Additive`→`BlendState.Additive`, `None`→`BlendState.Opaque`); **drop the `SaveStateMode` argument** |
| `GraphicsDevice.SetRenderTarget(int, RenderTarget2D)` | 23 | use the un-indexed `SetRenderTarget(RenderTarget2D)` |
| `new RenderTarget2D(…, levels, MultiSampleType, quality)` | ~10 | use the MonoGame constructor (`bool mipMap`, `DepthFormat`, `int sampleCount`, `RenderTargetUsage`) |
| `RenderTarget2D.GetTexture()` | ~50 | `RenderTarget2D` *is* a `Texture2D` in MonoGame; drop `.GetTexture()` |
| `Thread.CurrentThread.SetProcessorAffinity(5)` | 5 | delete (Xbox 360-only API) |
| `GraphicsDevice.CreationParameters.Adapter` | 1 | `GraphicsDevice.Adapter` |
| `Game1.LoadGraphicsContent(bool)` / `UnloadGraphicsContent(bool)` | 2 | rename to `LoadContent()` / `UnloadContent()` |
| `FullScreenRefreshRateInHz` assignment | 1 | remove |
| `new GamerServicesComponent(this)` registration | 1 | remove (shimmed in `Platform/Shim_GamerServices.cs`) |
| audio `using Microsoft.Xna.Framework.Audio;` | 10 files | switch to `using Dishwasher.Audio;` (the shim in `Platform/Audio/XactShim.cs`) |
| a `Color` ctor disambiguation in `Text.cs` | 1 | see `PORT_NOTES.md` |

Also required, and also inside the game source:

- **Shader `float4x4 MatrixTransform`** must be used in the effect vertex stage —
  this is already handled by the 19 re-authored `.fx` shipped here, but the game
  code must still supply the parameter.
- **Straight-alpha blend** (`BlendState.NonPremultiplied`) at the ~124
  `AlphaBlend` sites, to match `DishwasherContentManager`'s straight-alpha PNG
  loading. See `docs/notes/android/final-consolidation.md`.
- **`Globals.SkipIntro` / `Globals.ParallelTextureDecode` /
  `Globals.LoaderCarouselSeconds`** are referenced by `Platform/` code; the
  corresponding fields must exist in your `Globals.cs` (see
  `docs/notes/android/load-optimization.md`).

All of our Android-specific code already lives under `src/Dishwasher/Platform/`
and is included — you should **not** need to re-author it.

---

## 5. Content

- **`src/Dishwasher/Content/fx/*.fx` and `src/Dishwasher/Assets/fx/*.xnb`** —
  our 19 re-authored shaders, already compiled. You do **not** need game assets
  to build the *public* APK.
- **`src/Dishwasher/Assets/import-manifest.json`** — the SHA-256 proof-of-
  ownership manifest used by the public build at first run. It contains hashes
  only (no content). Regenerate with `tools/gen_import_manifest.py` if needed.
- **Internal build only:** you additionally need the game assets staged under
  `src/Dishwasher/Assets/{data,gfx,sfx,Resources}/`. Derive them with
  `tools/xnb_v2.py` (XNB v2 → straight-alpha PNG) and the audio tooling under
  `tools/audio/`; see `docs/notes/android/content-conversion.md`,
  `docs/notes/android/audio-conversion.md` and
  `docs/notes/port/content-integration.md`.

### 5.1 Rebuilding the shaders (optional)

The compiled effects are already shipped. Only rebuild them if you change a
`.fx`:

```sh
cd src/Dishwasher/Content
source ../../../tools/scripts/env.sh         # sets MGFXC_WINE_PATH
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
# then stage Content/bin/Android/fx/*.xnb -> ../Assets/fx/
```

This needs Wine plus MGCB's Windows GL effect compiler; see
`docs/notes/port/wine-shader-build.md`. If you do **not** want to run it, build
with `-p:EnableMGCBItems=false` — the pre-compiled `Assets/fx/*.xnb` are used at
runtime.

---

## 6. Build commands

Run from `src/Dishwasher/`. Set `ANDROID_*`/`JAVA_*` via
`tools/scripts/env.sh` or pass the MSBuild properties explicitly.

### 6.1 Public build (content-free) — **recommended**

This is the variant that ships no game content and imports the owner's own copy
at first run.

```sh
dotnet build -c Release -f net8.0-android \
  -p:DishwasherPublic=true \
  -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
  -p:AndroidNdkDirectory="$ANDROID_NDK_DIR" \
  -p:JavaSdkDirectory="$JAVA_SDK_DIR" \
  -p:EmbedAssembliesIntoApk=true \
  -p:AndroidPackageFormat=apk
```

### 6.2 Public build, **arm64-v8a only** (the release artifact)

```sh
dotnet build -c Release -f net8.0-android \
  -p:DishwasherPublic=true \
  -p:RuntimeIdentifier=android-arm64 \
  -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
  -p:AndroidNdkDirectory="$ANDROID_NDK_DIR" \
  -p:JavaSdkDirectory="$JAVA_SDK_DIR" \
  -p:EmbedAssembliesIntoApk=true \
  -p:AndroidPackageFormat=apk
```

Output: `bin/Release/net8.0-android/android-arm64/com.recomp.dishwasher-Signed.apk`.
Verify it is arm64-only:

```sh
unzip -l bin/Release/net8.0-android/android-arm64/com.recomp.dishwasher-Signed.apk \
  | grep -oE 'lib/[^/]+/' | sort -u          # -> lib/arm64-v8a/
```

### 6.3 Internal build (bundles all game content)

Requires the assets from §5. Same command **without**
`-p:DishwasherPublic=true`.

```sh
dotnet build -c Release -f net8.0-android \
  -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
  -p:JavaSdkDirectory="$JAVA_SDK_DIR" \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk
```

> `-p:EmbedAssembliesIntoApk=true` is required: without it the APK relies on
> Fast Deployment and will abort on install with "No assemblies found".
> Keep `targetSdkVersion=35` in `AndroidManifest.xml`.

### 6.4 Debug build (optional)

Same as §6.3 with `-c Debug`; larger, all-ABI, and much slower to load. Useful
only while developing.

---

## 7. Signing the release APK (use your own key)

The APKs produced above are signed with the **Android debug key** unless you
tell MSBuild otherwise. A debug-signed APK is fine for local testing but
**cannot be used for a public release**, and — critically — you can only ever
*update* an installed app in place with the **same** signing key. Make your own
release key now and keep it safe.

> **Never commit your keystore or its passwords.** `.gitignore` already covers
> `*.jks`, `*.keystore`, `keystore.properties`, `*.p12`, etc. Store the key
> **outside the repository** (e.g. `$HOME/dishwasher-release-keystore/`) and
> back it up; if you lose it you can never update your own app in place.

### 7.1 Create a release keystore (once)

```sh
mkdir -p "$HOME/dishwasher-release-keystore"
keytool -genkeypair -v \
  -keystore "$HOME/dishwasher-release-keystore/dishwasher-release.jks" \
  -alias dishwasher-release \
  -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=Dishwasher Public Release, OU=Homebrew Port, O=The Dishwasher Port, C=US"
# you will be prompted for the store password and the key password
```

`keytool` is part of the JDK, so `$JAVA_SDK_DIR/bin` (or your PATH) must have it.

### 7.2 Option A — sign during the build (MSBuild properties)

Add these to the arm64 public build (§6.2). Keep the secrets in your shell or a
file that is **not** committed:

```sh
read -rsp "keystore password: " DW_STOREPASS; echo
dotnet build -c Release -f net8.0-android \
  -p:DishwasherPublic=true \
  -p:RuntimeIdentifier=android-arm64 \
  -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
  -p:AndroidNdkDirectory="$ANDROID_NDK_DIR" \
  -p:JavaSdkDirectory="$JAVA_SDK_DIR" \
  -p:EmbedAssembliesIntoApk=true \
  -p:AndroidPackageFormat=apk \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore="$HOME/dishwasher-release-keystore/dishwasher-release.jks" \
  -p:AndroidSigningKeyAlias=dishwasher-release \
  -p:AndroidSigningStorePass="$DW_STOREPASS" \
  -p:AndroidSigningKeyPass="$DW_STOREPASS"
```

### 7.3 Option B — sign the built APK afterwards

`apksigner` (and `zipalign`) ship in `$ANDROID_SDK_DIR/build-tools/35.0.0/`.
The APK must be zipaligned first, then signed:

```sh
BT="$ANDROID_SDK_DIR/build-tools/35.0.0"
APK=src/Dishwasher/bin/Release/net8.0-android/android-arm64/com.recomp.dishwasher-Signed.apk

zipalign -f -p 4 "$APK" /path/outside/repo/com.recomp.dishwasher-unsigned.apk

"$BT/apksigner" sign \
  --ks "$HOME/dishwasher-release-keystore/dishwasher-release.jks" \
  --ks-key-alias dishwasher-release \
  --out dishwasher-public-arm64-1.0.apk \
  /path/outside/repo/com.recomp.dishwasher-unsigned.apk
```

### 7.4 Verify the signer and publish a checksum

```sh
"$BT/apksigner" verify --print-certs dishwasher-public-arm64-1.0.apk
# the output must show YOUR distinguished name, not "CN=Android Debug"

sha256sum dishwasher-public-arm64-1.0.apk > dishwasher-public-arm64-1.0.apk.sha256
```

---

## 8. Honesty note

Building from a fresh decompile is **not** a one-command affair: the decompiled
source has to be brought from XNA 3.0 to MonoGame, and those edits are
deliberately not distributed (they are the game's code). Expect to spend real
effort on §4, and to consult the per-subsystem write-ups under `docs/notes/`.
If you only want to *play*, use the prebuilt release APK and import your own
game data — that path needs no decompile at all.
