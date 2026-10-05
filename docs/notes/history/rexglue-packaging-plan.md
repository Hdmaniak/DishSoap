# The Dishwasher: Dead Samurai — Android (ReXGlue) Packaging Plan

Toolchain / packaging notes for the Xbox 360 → Android ARM64 recompilation port.
This document is the hand-off contract between the Android packaging workstream
and the native (ReXGlue port) workstream.

---

## 1. Host toolchain (installed & verified)

| Component | Version | Path |
|---|---|---|
| OS | Ubuntu 26.04.1 LTS x86_64 | — |
| JDK | OpenJDK **21.0.12.1** (Temurin/Ubuntu, headless + keytool) | `/usr/bin/java`, `/usr/lib/jvm/java-21-openjdk-amd64` |
| Android SDK root | cmdline-tools 12.0 | `$ANDROID_SDK_DIR` |
| sdkmanager | 12.0 | `$ANDROID_SDK_DIR/cmdline-tools/latest/bin/sdkmanager` |
| platform-tools / adb | **37.0.1** | `$ANDROID_SDK_DIR/platform-tools/adb` (also `$ADB`) |
| SDK Platform | android-35 (compileSdk 35) | `$ANDROID_SDK_DIR/platforms/android-35/android.jar` |
| Build-Tools | **35.0.0** | `$ANDROID_SDK_DIR/build-tools/35.0.0/` |
| aapt2 | 2.19-11948202 | `.../build-tools/35.0.0/aapt2` |
| apksigner | 0.9 | `.../build-tools/35.0.0/apksigner` |
| d8 | 8.6.2-dev | `.../build-tools/35.0.0/d8` |
| zipalign | — | `.../build-tools/35.0.0/zipalign` |
| NDK | **r28b** (28.1.13356709) | `$HOME/android-ndk-r28b` |
| CMake (SDK) | 3.22.1 | `$ANDROID_SDK_DIR/cmake/3.22.1` |
| Gradle (wrapper) | **8.12** | downloaded by `./gradlew` |
| Android Gradle Plugin | **8.7.3** | fetched from google() maven |
| SDL3 (vendored, read-only) | 3.5.0 | `<rexglue-sdk>/thirdparty/sdl3` |

`sdkmanager` prints a benign warning (`only understands SDK XML versions up to 3
but ... version 4 was encountered`) because the pre-existing cmdline-tools 12.0
predates android-35 metadata. It does not affect installs or builds.

### Built-in-but-unused
* `$ANDROID_SDK_DIR/jdk17` (Temurin 17.0.20.1) — retained as fallback if
  AGP/Gradle ever rejects JDK 21. Not needed today (JDK 21 builds fine).

---

## 2. Target device (queried via adb)

```
adb devices -l
<device-serial>   device usb:1-3 product:a52qnseea model:SM_A525F device:a52q transport_id:1

ro.build.version.sdk       = 34          (Android 14)
ro.build.version.release   = 14
ro.product.cpu.abi         = arm64-v8a
ro.product.cpu.abilist     = arm64-v8a,armeabi-v7a,armeabi
ro.product.manufacturer    = samsung
ro.product.model           = SM-A525F   (Galaxy A52)
ro.product.board           = atoll
GPU (dumpsys SurfaceFlinger): Qualcomm Adreno 618, OpenGL ES 3.2
```

* Architecture is **arm64-v8a only** → `abiFilters 'arm64-v8a'`, no 32-bit build.
* `minSdk 26` is satisfied (device is API 34); `targetSdk 35` compiles against
  android-35 and runs on the API 34 device.
* Device ABI is a hard match for the recompiled native library.

---

## 3. Signing keystore

| Field | Value |
|---|---|
| Path | `<android-app>/keystore/dishwasher.jks` |
| Type | PKCS12 |
| Alias | `dishwasher` |
| Store / key password | `android` / `android` (throwaway dev key) |
| Key | RSA 2048, validity 10000 days, dname `CN=Dishwasher Port,O=Recomp,C=US` |
| SHA-256 | `CE:03:25:FE:D2:20:21:EC:EA:E6:E9:1C:CC:1B:2D:73:E3:B5:D1:BF:A1:3F:72:1E:38:CA:E8:73:93:55:84:50` |

`app/build.gradle` wires this into the `release` `signingConfig`, so
`assembleRelease` emits an already-signed `app-release.apk`.
Debug builds are signed by the standard Android debug key.

> These credentials are development-only and must be replaced before any real
> distribution.

---

## 4. Project layout

```
<android-app>/
├── build.gradle                     # root: AGP 8.7.3
├── settings.gradle                  # rootProject 'DishwasherPort', :app
├── gradle.properties                # buildNative=false by default
├── local.properties                 # sdk.dir (generated; do not commit)
├── gradlew / gradlew.bat
├── gradle/wrapper/                  # gradle-wrapper.{jar,properties} -> 8.12
├── build-apk.sh                     # build entry point
├── deploy.sh                        # install + filtered logcat
├── keystore/dishwasher.jks
├── scripts/
│   ├── push-game-data.sh            # RECOMMENDED data delivery (files dir)
│   └── extract-to-assets.sh         # alternative: embed in APK assets
├── notes/packaging-plan.md          # this file
└── app/
    ├── build.gradle                 # namespace/applicationId com.recomp.dishwasher
    ├── proguard-rules.pro           # SDL JNI keep rules (from SDL3 template)
    ├── libs/arm64-v8a/              # drop prebuilt .so here (packaged always)
    ├── jni/
    │   ├── CMakeLists.txt           # guarded port integration + stub fallback
    │   └── dishwasher_stub.c        # placeholder libdishwasher.so
    └── src/main/
        ├── AndroidManifest.xml
        ├── assets/                  # optional embedded game data (empty)
        ├── java/
        │   ├── com/recomp/dishwasher/DishwasherActivity.java   # our entry point
        │   └── org/libsdl/app/*.java                           # SDL3 Java glue (copied)
        └── res/values/{strings,styles,colors}.xml, mipmap-*/ic_launcher.png
```

Derived from SDL3's `android-project` template
(`rexglue-sdk/thirdparty/sdl3/android-project`), copied — never modified in place.

### App configuration
* applicationId / namespace: **`com.recomp.dishwasher`**
* Entry activity: `com.recomp.dishwasher.DishwasherActivity extends org.libsdl.app.SDLActivity`
  * `getLibraries()` → `{"SDL3", "dishwasher"}` (loads `libSDL3.so` then `libdishwasher.so`)
  * `getMainFunction()` → `SDL_main` (inherited)
* Orientation: `screenOrientation="landscape"` (verified: `screenOrientation=0`)
* `minSdk 26`, `targetSdk 35`, `compileSdk 35`, `abiFilters 'arm64-v8a'`
* Gamepad advertised **not required** (`android.hardware.gamepad` / bluetooth / usb.host / touchscreen all `required="false"`)
* `android:extractNativeLibs="false"` (libs loaded directly from APK)
* SDL3 Java glue kept in `org.libsdl.app`; only the activity lives in our package.

---

## 5. Build / sign / install / log commands

All scripts set `ANDROID_HOME`, `ANDROID_NDK_HOME`, `JAVA_HOME` themselves.

```bash
cd <android-app>

# --- metadata only (fastest validation, no compilation) -------------------
./gradlew tasks
./gradlew :app:dependencies --configuration releaseRuntimeClasspath

# --- Java-only debug APK (native build OFF) -------------------------------
./build-apk.sh                 # -> app/build/outputs/apk/debug/app-debug.apk

# --- signed release APK ---------------------------------------------------
./build-apk.sh --release       # -> app/build/outputs/apk/release/app-release.apk
                               #    (signed with keystore/dishwasher.jks)

# --- with the native target (port tree OR stub) ---------------------------
./build-apk.sh --native              # configures app/jni/CMakeLists.txt
./build-apk.sh --release --native --clean

# --- raw Gradle equivalents ----------------------------------------------
./gradlew assembleDebug
./gradlew assembleRelease
./gradlew assembleDebug -PbuildNative
# override the port location:
./gradlew assembleDebug -PbuildNative -PportDir=/path/to/port

# --- verify signature / metadata -----------------------------------------
SDK=$ANDROID_SDK_DIR
$SDK/build-tools/35.0.0/apksigner verify --print-certs app/build/outputs/apk/release/app-release.apk
$SDK/build-tools/35.0.0/aapt2 dump badging app/build/outputs/apk/debug/app-debug.apk

# --- install + run --------------------------------------------------------
./deploy.sh                    # install -r debug, launch, stream logcat
./deploy.sh --release
./deploy.sh --no-logcat --no-launch

# --- logcat filter (also what deploy.sh streams) --------------------------
$ADB logcat -s ReXGlue:V Dishwasher:V AndroidRuntime:E DEBUG:V
```

---

## 6. Native library integration (contract for the port workstream)

The APK is designed to pick up `libdishwasher.so` in **two independent ways**:

### A. Prebuilt drop-in (simplest, no CMake needed)
```bash
mkdir -p app/libs/arm64-v8a
cp /path/to/build/libdishwasher.so app/libs/arm64-v8a/
# if SDL3 is linked SHARED, also:
cp /path/to/build/libSDL3.so       app/libs/arm64-v8a/
./build-apk.sh --release
```
`jniLibs.srcDirs = ['libs']` packages these into `lib/arm64-v8a/` automatically.

### B. Source / CMake subdirectory (preferred once the port exists)
`app/jni/CMakeLists.txt` reads `DISHWASHER_PORT_DIR` (default
`<extracted-package>`). If that directory contains `CMakeLists.txt`, it
is added as a subdirectory and **must define a shared-library target named
`dishwasher`**. Enable with `-PbuildNative`.

### C. Stub fallback (validated today)
If `DISHWASHER_PORT_DIR` has no `CMakeLists.txt`, a minimal
`dishwasher_stub.c` is compiled into `libdishwasher.so` that logs to tag
`Dishwasher` and returns. This proves the entire
configure → compile → strip → package → install pipeline before the game code
lands. Verified: the stub `.so` is `ELF 64-bit LSB shared object, ARM aarch64,
for Android 26, built by NDK r28b`.

> Open decision for the port workstream: whether SDL3 is linked **shared**
> (ship `libSDL3.so`, keep `"SDL3"` in `getLibraries()`) or **static**
> (ship only `libdishwasher.so`, drop `"SDL3"` from `getLibraries()`).

---

## 7. Game data / asset strategy

Source data (extracted Xbox 360 package):
`<extracted>/58410902/000D0000/`
* Contains one ~**125,685,760 byte (≈120 MiB)** file named
  `BF12F19032010E0A01D658FDBD4D49505327249858` (the title's `default.xex`
  payload; rename/alias to `default.xex` if the VFS expects that name).
* Nothing is pushed to the device yet — per workstream instructions.

### Option 1 — Push to app-private external files dir (RECOMMENDED)
```bash
scripts/push-game-data.sh            # -> /sdcard/Android/data/com.recomp.dishwasher/files/game/
scripts/push-game-data.sh --dry-run  # preview
```
The ReXGlue VFS maps `.../files/game/` as the game's root (or `game/` prefix).

**Pros**
* APK stays tiny (currently ~92 KB Java-only / ~1.3 MB with stub).
* No re-download / re-install when data changes.
* `default.xex` is a real, mmap-able file on disk → fast load, supports random
  access, no decompression.
* Avoids Play Store size limits entirely for the data.
* Easy to update during development (`adb push` only the changed files).

**Cons**
* Requires `adb`/file-manager step separate from install (first-run UX).
* `/sdcard/Android/data/<pkg>/` is scoped-storage managed; on API 30+ the app
  owns it, and desktop `adb push` works, but some OEM file managers cannot.
* Data is not covered by APK signature / Play install; needs a bootstrap/copy
  step for real distribution.

### Option 2 — Embed as APK assets (`AAssetManager`)
```bash
scripts/extract-to-assets.sh         # copies into app/src/main/assets/game/
./build-apk.sh --release
```
Native code reads via `AAssetManager` with prefix `game/...`.

**Pros**
* Single-file distribution; data always present, signature-protected.
* No runtime copy/IO for small files.

**Cons**
* APK balloons to ≈130 MB+ (assets may compress further, but the .xex is large).
* Exceeds Google Play's **base-module limit** (200 MB hard cap; >150 MB blocks
  uploads for many account states) and greatly slows install/launch.
* Assets are compressed and accessed through AAssetManager, which:
  * does **not** give a file descriptor/mmap for arbitrary offsets the way a
    normal file does (Android 9+ `AAsset_openFileDescriptor` exists but is
    unreliable for compressed assets),
  * forces the XEX/asset loader through a streaming API → slower startup and
    possibly incompatible with the emulator's file-backed memory mapping.
* Every data tweak requires a full APK rebuild + reinstall.

### Recommendation
**Use Option 1 (external files dir) for development and the physical device**,
with the ReXGlue VFS rooted at
`/sdcard/Android/data/com.recomp.dishwasher/files/game/`.
If/when a Play-ready build is needed, move the data behind **Play Asset
Delivery** (install-time or fast-follow asset packs) so the base APK stays
small while assets are still distributed through the store — the on-disk layout
is the same as Option 1, so no VFS change is required.
Option 2 is kept only as a fallback and is explicitly **not** the default
(`app/src/main/assets/` ships empty).

---

## 8. Validation performed

| Check | Result |
|---|---|
| `java -version`, `keytool` | OpenJDK 21.0.12.1 ✓ |
| SDK packages installed | platform-tools, platforms;android-35, build-tools;35.0.0, cmake;3.22.1 ✓ |
| `aapt2` / `apksigner` / `d8` present | ✓ |
| `./gradlew tasks` | **BUILD SUCCESSFUL** ✓ |
| `./build-apk.sh` (debug, Java-only) | **BUILD SUCCESSFUL**, `app-debug.apk` 92,412 B ✓ |
| `./build-apk.sh --release` | **BUILD SUCCESSFUL**, signed `app-release.apk` 81,663 B ✓ |
| `./build-apk.sh --native --clean` (stub .so) | **BUILD SUCCESSFUL**, `app-debug.apk` ~1.37 MB, ARM64 `libdishwasher.so` ✓ |
| APK badging | `com.recomp.dishwasher`, minSdk 26, targetSdk 35, native-code `arm64-v8a`, landscape ✓ |
| `adb install -r app-debug.apk` | **Success** (installed package verified, then uninstalled) ✓ |
| Device data push | NOT performed (intentional) — scripts ready ✓ |

## 9. Open items / next steps
1. Native workstream delivers `libdishwasher.so` (+ `libSDL3.so` if dynamic) →
   drop into `app/libs/arm64-v8a/` or expose a CMake target `dishwasher`.
2. Decide SDL3 shared vs static linkage (affects `getLibraries()`).
3. Wire the ReXGlue VFS root to the external files dir (`game/`) or to
   `AAssetManager` depending on the chosen data strategy.
4. Optionally add `REXGLUE`/`ReXGlue` log tag parity so `deploy.sh`'s filter
   captures engine output (currently `ReXGlue`, `Dishwasher`, `AndroidRuntime`,
   `DEBUG`).
