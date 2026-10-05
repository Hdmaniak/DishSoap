# The Dishwasher: Dead Samurai — managed MonoGame/Android port workspace

Workspace for the **managed** port route: decompile the Xbox 360 XNA game's IL to C#
(`ilspycmd`) and rebuild it on **MonoGame for Android**.

This directory was created and validated by the .NET/MonoGame Toolchain Lead.
It is the canonical recipe for the rest of the team.

> Status: **toolchain installed and validated** (clean throwaway Android app built and
> produced a signed APK). No game code has been decompiled here yet.

---

## 1. Installed tool versions (validated on this host)

| Component | Version | Location / how |
|---|---|---|
| OS | Ubuntu 26.04.1 LTS (x86_64), 16 cores | host |
| .NET SDK | **8.0.425** | `~/.dotnet` (installed via `dotnet-install.sh`, channel `8.0`) |
| .NET runtime | 8.0.31 (`Microsoft.NETCore.App` + `AspNetCore`) | `~/.dotnet/shared` |
| `DOTNET_ROOT` | `$HOME/.dotnet` | exported in `~/.bashrc` + `~/.profile` |
| ilspycmd | **9.1.0.7988** (ICSharpCode.Decompiler 9.1.0.7988) | `~/.dotnet/tools/ilspycmd` |
| MonoGame templates | **MonoGame.Templates.CSharp 3.8.5.1** | `dotnet new install` |
| .NET Android workload | **android 34.0.154/8.0.100** (source SDK band 8.0.400) | `~/.dotnet/packs` |
| MonoGame Android framework | **MonoGame.Framework.Android 3.8.5.1** | NuGet (resolved by template) |
| MonoGame content task | **MonoGame.Content.Builder.Task 3.8.5.1** | NuGet (resolved by template) |
| MonoGame dependency | MonoGame.Library.OpenAL 1.24.3.2 | NuGet transitive |
| MGCB | **MonoGame Content Builder v3.8.5.1** (`dotnet-mgcb` 3.8.5.1) | `~/.dotnet/tools/mgcb` |
| JDK | **OpenJDK 21.0.12.1** | `/usr/lib/jvm/java-21-openjdk-amd64` |
| Android SDK | platform `android-35`, build-tools `34.0.0` + `35.0.0`, platform-tools | `$ANDROID_SDK_DIR` |
| Android NDK | r28b | `$HOME/android-ndk-r28b` |
| adb | — | `$ADB` (device `<device-serial>`, Galaxy A52, API 34, arm64-v8a) |

### Version notes / gotchas
- **apt only ships `dotnet-sdk-10.0` on this host** (no 8.0 / 9.0 packages), so the SDK was
  installed with Microsoft's `dotnet-install.sh` into `$HOME/.dotnet`.
- **`ilspycmd` latest (11.x) requires .NET 9/10 and cannot be installed by the .NET 8 SDK**
  (fails with `The settings file in the tool's NuGet package is invalid: Settings file
  'DotnetToolSettings.xml' was not found in the package.`). Likewise ilspycmd **8.2.0.7535
  targets `net6.0`** and fails to run (no .NET 6 runtime). The correct pin is
  **`ilspycmd 9.1.0.7988`**, whose tool payload is `tools/net8.0/` and runs on .NET 8.
- **Template TFM mismatch (important):** `MonoGame.Templates.CSharp 3.8.5.1` scaffolds
  `mgandroid` with **`<TargetFramework>net9.0-android</TargetFramework>`**, but this host has
  the **.NET 8** SDK + **net8.0** Android workload. Building the untouched template fails with:
  ```
  error NETSDK1139: The target platform identifier android was not recognized.
  ```
  The Android SDK *is* resolved, but the net8 workload does not advertise `android` for a
  `net9.0` target. **Fix: retarget the generated project to `net8.0-android`** (one-line edit;
  see §3). If a .NET 9 SDK + `dotnet workload install android` (9.0 band) is installed later,
  the template can stay on `net9.0-android` unchanged.
- **`/tmp` is tmpfs and is wiped on reboot.** Keep only disposable scratch there
  (`/tmp/opencode`). Installed tooling lives under `~/.dotnet` (persistent).

---

## 2. Environment setup (PATH)

`~/.bashrc` and `~/.profile` already contain:
```sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
```
Use the helper script for any non-login shell / scripts:
```sh
source tools/scripts/env.sh
```
(`scripts/env.sh` exports the same, plus convenience vars for SDK/NDK/Java.)

---

## 3. Create + build a MonoGame Android app (the recipe)

```sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"

# 1) Create the project from the exact template short name: mgandroid
dotnet new mgandroid -n MyGame -o /path/to/MyGame

# 2) RETARGET net9.0-android -> net8.0-android (required on this host, see §1)
sed -i 's#<TargetFramework>net9.0-android</TargetFramework>#<TargetFramework>net8.0-android</TargetFramework>#' \
  /path/to/MyGame/MyGame.csproj

# 3) Build the Android target framework, pointing MSBuild at the SDK/NDK/Java:
cd /path/to/MyGame
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:AndroidNdkDirectory=$HOME/android-ndk-r28b \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64
```

**EXACT template short name: `mgandroid`** (`MonoGame Android Application`).
(Re-create via `scripts/new-android-project.sh MyGame /path/to/MyGame` to get all of the above.)

### MSBuild properties (what is actually required)

| Property | Required? | Value on this host |
|---|---|---|
| `AndroidSdkDirectory` | **Yes** | `$ANDROID_SDK_DIR` |
| `JavaSdkDirectory` | No (auto-detected), recommended | `/usr/lib/jvm/java-21-openjdk-amd64` |
| `AndroidNdkDirectory` | No for Debug APK; needed for AOT/native (Release) | `$HOME/android-ndk-r28b` |
| `TargetFramework` / `-f` | Yes | `net8.0-android` |

Verified: `dotnet build -f net8.0-android -p:AndroidSdkDirectory=$ANDROID_SDK_DIR`
alone **succeeds** (Java auto-detected; NDK not needed for Debug). Passing all three is the
robust form for CI and Release/AOT builds.

### Outputs
- Unsigned: `bin/Debug/net8.0-android/<id>.apk`
- **Signed: `bin/Debug/net8.0-android/<id>-Signed.apk`** ← installable
- Integrated content build (via `MonoGame.Content.Builder.Task`) writes
  `Content/bin/Android/Content/...` and `Content/obj/Android/net8.0-android/...`.

Install/run on the attached device:
```sh
$ADB -s <device-serial> install -r bin/Debug/net8.0-android/<id>-Signed.apk
$ADB -s <device-serial> shell monkey -p <ApplicationId> 1
```

---

## 4. Throwaway build test results (evidence)

Reproduced twice (including after a `/tmp` wipe) in `/tmp/opencode/mgtest`:

| Test | Command | Result |
|---|---|---|
| Untouched template | `dotnet build -f net9.0-android -p:AndroidSdkDirectory=... -p:AndroidNdkDirectory=...` | **FAIL** — `error NETSDK1139: The target platform identifier android was not recognized.` |
| `-f net8.0-android` on untouched net9 csproj | same props | **FAIL** — same `NETSDK1139` |
| Template retargeted to net8.0-android | `dotnet build -f net8.0-android -p:AndroidSdkDirectory=... -p:AndroidNdkDirectory=... -p:JavaSdkDirectory=...` | **SUCCESS** — `Build succeeded. 0 Error(s)`; signed APK produced |
| Retargeted, only `AndroidSdkDirectory` | `dotnet build -f net8.0-android -p:AndroidSdkDirectory=$ANDROID_SDK_DIR` | **SUCCESS** |
| Headless content | `mgcb Content/Content.mgcb /platform:Android /outputDir:... /intermediateDir:...` | **SUCCESS** (`Build 0 succeeded, 0 failed` — template ships no content) |

Warnings observed (benign): `XA1008: The TargetFrameworkVersion (Android API level 34) is lower
than the targetSdkVersion (35)` (net8 workload is API 34; manifest targets 35), and javac
`source/target value 8 is obsolete`. To silence XA1008, either use an API-35 workload
(.NET 9) or lower `android:targetSdkVersion` in `AndroidManifest.xml` to 34.

Blocker encountered and resolved: ilspycmd version pin (see §1).

---

## 5. MGCB — headless content rebuild for Android

Global tool `dotnet-mgcb` **3.8.5.1** is installed; command is `mgcb`
(also `dotnet mgcb ...`). This is fully headless (no display needed).

```sh
# Build the content for Android explicitly:
mgcb Content/Content.mgcb \
  /platform:Android \
  /outputDir:Content/bin/Android \
  /intermediateDir:Content/obj/Android \
  /quiet

# Equivalent via the dotnet driver:
dotnet mgcb Content/Content.mgcb /platform:Android \
  /outputDir:Content/bin/Android /intermediateDir:Content/obj/Android
```
Notes:
- The `mgandroid` template already includes `MonoGame.Content.Builder.Task`, so a normal
  `dotnet build -f net8.0-android ...` runs MGCB automatically for the Android platform
  (preferred for incremental rebuilds).
- The template's `Content/Content.mgcb` already contains `/platform:Android` and
  `/outputDir:bin/$(Platform)`.
- The GUI (`mgcb-editor`) is **not** required and is not usable headless; the CLI is enough.

---

## 6. Workspace layout

```
<dev-workspace>/
├── README.md                     # this file
├── scripts/
│   ├── env.sh                    # source this: DOTNET_ROOT/PATH + SDK/NDK/JDK vars
│   └── new-android-project.sh    # create + retarget + build a mgandroid project
└── notes/
    └── toolchain-versions.txt    # raw `dotnet --info` / workload / tool output snapshot
```

Scratch/throwaway work lives in `/tmp/opencode/` (wiped on reboot):
`mgtest/` (validated Android project), `apk-build.log`, `build-min.log`, `mgtest-diag.log`.

### Boundaries (do NOT touch from this workspace)
`<extracted-package>`, `<managed-decompile>`,
`<rexglue-sdk>*`, `<android-app>`.
These belong to other agents/routes.

---

## 7. Decompilation (next step, another agent)

`ilspycmd` is ready for headless IL→C#:
```sh
ilspycmd -p -o /tmp/opencode/decompiled GameAssembly.dll     # whole assembly to a project
ilspycmd GameAssembly.dll > out.cs                           # single file
ilspycmd --help                                              # options (9.1.0.7988)
```
Decompilation is intentionally **not** done in this workspace yet.
