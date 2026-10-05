# PROJECT_STATE.md — *The Dishwasher: Dead Samurai* → native Android port

**Last updated:** 2026-10-05
**Status:** ✅ **Playable on Android.** Boots to menus, touch + gamepad input, audio, widescreen, saves,
rumble/vibration routing, a real EXIT, and a first-run that skips the storage screen (see §18).
**Load time optimized:** ~24.6 s → **~5.0 s warm** (~6.5 s cold after install) via Release+AOT,
parallel PNG decode, skip re-extraction, and a compressed carousel gate (see §10).

**Two build variants exist** (same source tree):
- **internal** (default): bundles all game content (~97 MB APK).
- **public** (`-p:DishwasherPublic=true`): **ships NO game content** (~9.7 MB arm64 APK) — first run
  imports the owner's own copy (zip / folder / retail XBLA zip), checks it against the bundled
  SHA-256 manifest **tolerantly** (a different revision or a missing file is a warning, not a
  blocker), and derives textures, fonts and audio **on-device**. See §13.

**Public build now includes the touch-controls work** (2026-10-03): the optional full-controller
`TOUCH CONTROLS` overlay + `EDIT TOUCH LAYOUT`, the comic `SPEED UP`/`SKIP` buttons, and the
updated `ANDROID SETTINGS` menu (see §17). A device that imported with an **earlier** public build
must **re-import** — the importer skips already-derived content, so an in-place update keeps the
old derived files. See §13/§16.

**Resolved issue:** background **music now plays** in BOTH builds (previously music never
played while SFX did). Root cause was the XACT shim's cue cache; see §14.

**Latest sync (2026-10-05):** rumble/vibration routing, a real **EXIT** that quits, the
storage-screen skip + auto-created first-run save, and options-row tweaks are now in this
repo's `Platform/` (plus `android_settings` format v2 and the `VIBRATE`/`BLUETOOTH`
permissions). The GameSource-side edits are **not** shipped (this repo ships no game code)
and are listed in §18 for builders re-applying the port. The approved shield icon is
already present in `Resources/`.

**Tolerant import (2026-10-05):** the public build's import gate no longer blocks on an
asset mismatch or a missing file — see §13.1. This is the current shipped v1.0 policy.

> **READ THIS FIRST.** This document is the single source of truth for resuming the project.
> A future agent should be able to continue from here with **zero guesswork**.
> Detailed per-subsystem write-ups are in `android/notes/` (§12 index).

---

## 0. TL;DR

The Xbox 360 XBLA title **The Dishwasher: Dead Samurai** now runs as a **native Android ARM64 app**
(`com.recomp.dishwasher`), built from the **decompiled** game recompiled against **MonoGame**.

- **Not** an emulator. **Not** ReXGlue. No PPC emulation/JIT at runtime.
- The game's IL was decompiled to C# (59,813 LOC, 0 decompiler failures) and rebuilt.
- Runs on the A52 at ~60 fps, full 2400×1080 widescreen, with touch **and** Xbox controller input.

---

## 1. What this project is (and is NOT)

**IS:** a source-level native port. Game logic decompiled → recompiled against a reimplemented
XNA API (MonoGame) → compiled to native ARM64 by .NET-for-Android.

**IS NOT:**
- ❌ **ReXGlue static recompilation** — impossible for this title (§2).
- ❌ **Xenia-style emulation** — impossible for this title (§2).
- ❌ an emulator of any kind.

---

## 2. ⚠️ Critical history — DO NOT REPEAT THESE DEAD ENDS

This project burned significant time on two approaches that are **structurally impossible** for
this specific title. Do not re-attempt either. Full evidence: `HANDOFF.md`,
`android/notes/xna-netcf-feasibility.md`, `android/notes/xenia-reference.md`.

### 2.1 ReXGlue static recompilation — IMPOSSIBLE
The Dishwasher is an **XNA Game Studio 3.x / .NET Compact Framework 3.5** title. `default.xex`
is only a launcher (`TitleLauncher.pe`). The game logic is **managed IL** in `game.exe.xex` +
`Microsoft.Xna.Framework*.dll.xex` + `mscorlib`/`System*` (all `ILONLY`, `BSJB`, `v2.0.50727`).
The native modules (`RuntimeHost.xex`, `NetCFUserMode.dll`) are the **.NET CF CLR**, which **JITs
IL → PowerPC at runtime**. A static PPC→C++ recompiler has nothing to translate, and recompiling
the CLR would emit PPC machine code on ARM64. ReXGlue has no interpreter/JIT fallback.
**The ReXGlue work is preserved** (patches in `android/patches/`, audit in `android/notes/`) as a
reusable result for *native* 360 titles — it is simply the wrong tool for this game.

### 2.2 Xenia emulation — IMPOSSIBLE (for reference capture)
Xenia boots the package then dies at `KeCreateUserMode` (no .NET CF user-mode environment).
Xenia issue **#642** for this exact title is labelled `state-crash-xna-WONTFIX`.
So **no emulated visual reference is obtainable on this machine** — use the YouTube frame set
(§3) as the visual ground truth instead.

---

## 3. Repository layout

```
~/dishwasher/
├── PROJECT_STATE.md              ← THIS FILE (start here)
├── HANDOFF.md                    ← the ReXGlue/feasibility verdict + evidence
│
├── Dishwasher, The - Dead Samurai (World) (XBLA).zip      (61.9 MB, the ROM)
├── extracted/58410902/000D0000/BF12F190…     (STFS/LIVE package, 125 MB)
├── assets-clean/                 ← ✅ CLEANED game assets (560 files) — use this
├── managed/                      ← extracted managed assemblies + decompiled C#
│   ├── game.exe                  ← the game (IL) — the thing we port
│   ├── mscorlib.dll, System*.dll, Microsoft.Xna.Framework*.dll
│   ├── native/                   ← the 4 native XEX modules (for reference only)
│   ├── decompiled/game/          ← ✅ THE C# SOURCE OF TRUTH (projectDish)
│   └── decompiled/xna3/          ← decompiled XNA 3.0 framework (API reference)
│
├── src/Dishwasher/           ← ✅ THE ANDROID APP (build this)
├── tools/                ← XNB v2 → PNG exporter (xnb_v2.py, export_xnb_textures.py)
├── tools/scripts/env.sh        ← source this before any dotnet command
├── <dev-notes>/                ← port-side notes (shaders, audio, wine, content)
├── tools/                        ← ✅ DURABLE tooling rescued from /tmp (see §11)
│
├── android/notes/                ← ✅ ALL subsystem write-ups + evidence (see §12)
├── android/patches/              ← ReXGlue-era patches (preserved, not used)
├── recordings/                   ← device screen recording(s)
└── port/, rexglue-sdk/, android-app/, fiber-work/, android-vkprobe/
                                  ← ReXGlue-era artifacts (historical; not part of the port)
```

**The port itself is `src/Dishwasher/`.** Everything else is input data, history, or tooling.

### App structure (`src/Dishwasher/`)
```
Activity1.cs                  ← Android entry: touch dispatch, BACK, widescreen sizing, immersive, autosave
AndroidManifest.xml
Dishwasher.csproj             ← net8.0-android, all 4 ABIs, targetSdk 35
DishwasherContentManager.cs   ← loads PNGs STRAIGHT-alpha (not premultiplied) + runtime SpriteFont
GameSource/                   ← 62 .cs — the decompiled game (projectDish). Keep edits minimal & marked.
  projectDish/                  (Game1.cs = the Game class; MainMenu.cs = menus; etc.)
Assets/                       ← game data packaged INTO the apk (data/, gfx/, fx/, sfx/, Resources/)
Content/                      ← source textures/fx + Content.mgcb (shaders compiled via MGCB)
Resources/                    ← ✅ app icon: approved shield artwork (regenerable — see below)
  drawable/icon.png             legacy @drawable/icon
  mipmap-<density>/             legacy ic_launcher(+_round) + adaptive _foreground/_background
  mipmap-anydpi-v26/            adaptive icon (manifest → @mipmap/ic_launcher[/_round])
Platform/                     ← ✅ ALL Android-specific code lives here
  AndroidContentBootstrap.cs    extract APK assets → app-private files/content, set CWD
  AndroidInputBridge.cs         ⚠️ MonoGame Android gamepad fix + synthetic touch pad
  AndroidSettings.cs            Android-only settings store (own file; v2 adds vibration routing)
  AndroidSettingsMenu.cs        the ANDROID SETTINGS screen (partial class MainMenu)
  AndroidRumble.cs              ✅ value-aware, routed rumble: device vibrator + controller
  BluetoothHidRumble.cs         ✅ C# facade over the Java HID-host rumble helper (API < 31)
  Java/RumbleHidHost.java       ✅ @hide BluetoothHidHost.sendData output-report path
  PortExit.cs                   ✅ EXIT menu entry really quits (save → finish task → kill)
  VibrationMenu.cs              ✅ game's own settings row 1 = OFF/PHONE/CONTROLLER/BOTH
  FrameLimiter.cs               FPS lock implementation
  PerfOverlay.cs                FPS counter overlay
  MenuTouchTargets.cs           draw-time menu row rects + hit-testing
  TouchControls.cs              touch gestures (tap/swipe/back) → gamepad synthesis
  TouchGamepadOverlay.cs        optional gameplay on-screen gamepad (sticks/d-pad/face/triggers),
                                comic SPEED UP/SKIP buttons, and drag-to-arrange layout editor
  SaveAutosave.cs               autosave on OnPause/OnStop (calls the game's own save routines)
  WidescreenConfig.cs           logical backbuffer + scale
  Shim_*.cs                     XNA Storage / GamerServices / Net / EffectCompat shims
  Audio/XactShim.cs             XACT → MonoGame audio shim (namespace Dishwasher.Audio)
  Log.cs                        logcat logging ([Dishwasher] tag)
  InputDiagnostics.cs           debug (Enabled=false)
  RenderDiagnostics.cs          debug harness (Enabled=false)
```

### App icon (approved artwork)
The launcher icon is the **user-supplied shield artwork** (a hooded, gas-masked
figure with red eyes and a bloodied cleaver inside a rounded shield frame). The
durable source is `icon.jpeg`; the single approved crop — which keeps the
shield frame and trims the black margin — is
`icon.jpeg.crop((410, 106, 966, 650))` (556×544), re-padded onto a 556×556
canvas whose background is the artwork's own colour `#171717`. It ships as a
proper **adaptive icon** (`Resources/mipmap-anydpi-v26/ic_launcher*.xml`
referencing per-density `ic_launcher_foreground` / `ic_launcher_background`),
with legacy `mipmap-*` PNG fallbacks for pre-API-26 devices and a
`drawable/icon.png` fallback. The manifest (`AndroidManifest.xml` +
`Activity1.cs`) points at `@mipmap/ic_launcher` / `@mipmap/ic_launcher_round`.
There is no README banner (the previous abstract banner asset was removed).

On the adaptive layers the shield is held inside the central safe zone (its
largest dimension is 54 % of the 108 dp layer, ~58 dp ≈ 88 % of the safe circle)
and the background is solid `#171717`, so the launcher mask cannot clip the
frame/head/cleaver. The fraction is parameterised (`SAFE_FRAC` / `--safe-frac`
on the generator) so it can be tuned. The superseded `docs/assets/generate-icon.py`
(procedural "halftone-burst") is no longer wired in.

Everything is regenerated from the single source of truth
`docs/assets/generate-icon-from-art.py`:

```sh
python3 docs/assets/generate-icon-from-art.py --art /path/to/icon.jpeg \
    --android src/Dishwasher/Resources          # repo Android resource overlay
    # --android /path/to/src/Dishwasher/Resources   # working port
    # (also writes docs/assets/icon-512, icon-192, ic_launcher-512)
```

---

## 4. Build & deploy (exact commands)

```sh
source tools/scripts/env.sh      # sets DOTNET_ROOT, MGFXC_WINE_PATH, SDK/NDK/JDK
cd src/Dishwasher

# RELEASE build (use this — Release+AOT is ~5× faster to load; see §10):
dotnet build -c Release -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk
# APK: bin/Release/net8.0-android/com.recomp.dishwasher-Signed.apk   (~95 MB, arm64)

# Debug build (only if you need it): same but -c Debug → bin/Debug/... (~181 MB, all ABIs).

# APK: bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk   (~181 MB)
# NOTE: EmbedAssembliesIntoApk=true is REQUIRED or the APK crashes on install
#       ("No assemblies found … Fast Deployment").
# NOTE: keep targetSdkVersion=35 (34 demands a missing platforms/android-34).

# Deploy + run:
ADB="${ADB:-adb}"; S=<device-serial>
$ADB -s $S install -r bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
$ADB -s $S shell am force-stop com.recomp.dishwasher
$ADB -s $S logcat -c
$ADB -s $S shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
$ADB -s $S logcat -d -v time -s Dishwasher:V AndroidRuntime:E
```

**Shaders** (only if you change `Content/fx/*.fx`):
```sh
cd src/Dishwasher/Content
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
# then stage:  Content/bin/Android/fx/*.xnb → Assets/fx/
# Requires MGFXC_WINE_PATH (set by env.sh). See <dev-notes>/wine-shader-build.md.
```

**Reference-frame capture (visual ground truth):**
`~/dishwasher/android/notes/reference/youtube/` — 548 interval frames + 137 scene frames +
5 contact sheets (+ `f-…png`, `INDEX.md`). Use these; Xenia cannot produce references (§2.2).

---

## 5. Toolchain & device facts

| Thing | Value |
|---|---|
| Device | Samsung **SM-A525F** (Galaxy A52), Android 14 (API 34), arm64-v8a, **Adreno 618, 60 Hz**, 2340×1080 panel (landscape 2400×1080) |
| adb serial | `<device-serial>` (second device seen: `<device-serial>` = Galaxy S24, Android 16/API 36, 120 Hz) |
| .NET SDK | 8.0.425 at `~/.dotnet` (tools in `~/.dotnet/tools`) |
| ilspycmd | **9.1.0.7988** (newer needs .NET 9/10) |
| MonoGame | 3.8.5.1 (`MonoGame.Framework.Android`) |
| Android SDK | `$ANDROID_SDK_DIR` (platform 35, build-tools 35.0.0) |
| NDK | r28b `$HOME/android-ndk-r28b` |
| JDK | 21 at `/usr/lib/jvm/java-21-openjdk-amd64` |
| Wine (for MGCB shaders) | prefix `~/.wine-mgcb` |
| sudo | `printf 'lolkol\n' \| sudo -S -p '' <cmd>` |
| Template | `dotnet new mgandroid` — but retarget `net9.0-android` → **`net8.0-android`** |

---

## 6. Architecture — how the port works

1. **`AndroidContentBootstrap`** extracts the APK's `Assets/**` into app-private
   `files/content` and sets CWD there, so the game's relative `File.Open("data/…")` calls work.
2. **`DishwasherContentManager`** replaces MonoGame's content manager: loads `Content/**.png`
   **straight-alpha** via `Texture2D.FromStream` (MonoGame's stock path **premultiplies**, which
   corrupted this game's art) and rebuilds `SpriteFont`s at runtime from decoded atlases.
3. **`AndroidInputBridge`** works around a **MonoGame bug**: `GamePad.Initialize()` is never called
   by the framework, so gamepads never enumerate. It reflects into the internal `AndroidGamePad`
   and also publishes a **synthetic slot-0 pad** driven by touch.
4. **`TouchControls`** maps gestures → gamepad state; **`MenuTouchTargets`** records menu row
   rectangles at draw time so taps land on the row you touch.
5. **`WidescreenConfig` + `Activity1`** render into a **logical** backbuffer (height fixed 720,
   width = panel aspect) and scale the view uniformly to fill the panel — no stretch, no crop,
   +25% horizontal world vs the original 16:9.
6. **`FrameLimiter` / `PerfOverlay`** implement FPS lock + an on-screen FPS counter.
7. **`SaveAutosave`** calls the game's own save routines on `OnPause`/`OnStop`.

---

## 7. Subsystem status

| Subsystem | Status | Notes / where |
|---|---|---|
| Game logic (59.8k LOC) | ✅ running | `GameSource/` (from `managed/decompiled/game`) |
| Textures | ✅ | 125 XNB v2 → PNG (`tools/xnb_v2.py`); raw `Color` = **`A,R,G,B`** (big-endian), **DXT5 alpha is byte-swapped (BE)** |
| Fonts | ✅ | 3 SpriteFonts rebuilt at runtime (metrics decoded) |
| Shaders | ✅ | 19/19 effects re-authored from Xenos microcode HLSL, compiled by MGCB+Wine |
| Audio | ✅ | 216 cues, XACT shim + NVorbis OGG (`Platform/Audio/XactShim.cs`) |
| Gamepad | ✅ | `AndroidInputBridge` (fixes the MonoGame init bug) |
| Touch | ✅ | menus: tap = row select/activate, swipe = d-pad, two-finger/Android BACK = B. Optional gameplay **TOUCH CONTROLS** overlay (default OFF) + layout editor + comic SPEED UP/SKIP — see §17 |
| **Rumble / vibration** | ✅ | `Platform/AndroidRumble.cs`: **OFF / PHONE / CONTROLLER / BOTH** via the game's own options row 1; API ≥ 31 controller vibrator, API < 31 device vibrator + Bluetooth HID fallback (`Platform/Java/RumbleHidHost.java`). See §18 |
| **EXIT / quit** | ✅ | `Platform/PortExit.cs` — EXIT saves through the game's own routines, then finishes + removes the task and stops the process (no stale relaunch). See §18 |
| First-run save | ✅ auto | `Globals.SkipNewSavePrompt` creates the profile (the exact "yes" call) with no dialog. See §18 |
| Storage-device screen | ✅ skipped | `Globals.SkipStorageCheck` — the shim resolves synchronously, so the "CHECKING STORAGE DEVICE" fade is skipped. See §18 |
| Options rows | ✅ tweaked | SELECT STORAGE DEVICE row removed; vibration row added; rows renumbered (RESET 6→5, BACK 7→6) |
| Console services (LIVE/achievements) | ✅ shimmed | `Shim_GamerServices.cs`; LIVE co-op entry hidden |
| **Multiplayer (System Link / LAN)** | ✅ **works, 2 phones verified** | `Platform/Shim_Net.cs` — real UDP session over LAN, port 27315. See `android/notes/multiplayer-lan.md` |
| Local co-op (split-screen) | ✅ | `NetworkSessionType.Local` = in-process, one device, 2 controllers |
| Saves | ✅ | `profile.sav` + `settings.sav` (game's own XML) + `android_settings.sav`; autosave on pause/stop and on EXIT |
| Widescreen | ✅ | 2400×1080 fill, uniform 1.5× on the A52 |
| Intro (logo carousel) | ✅ restored | `Globals.SkipIntro = false` |
| FPS lock + counter | ✅ | `ANDROID SETTINGS`: FPS LOCK (Unlimited/30/60/120), SHOW FPS COUNTER |
| Android Settings menu | ✅ | top-level main-menu entry → own screen + own storage |
| Load time | ✅ **~5 s warm** (~6.5 s cold) | was ~24.6 s. Release+AOT + parallel PNG decode + skip re-extraction + 4 s carousel gate |

---

## 8. 🔒 Protected fixes — DO NOT REGRESS

Each of these fixed a real bug. Breaking any of them reintroduces a visible defect:

1. **Shader `float4x4 MatrixTransform`** in all 19 `Content/fx/*.fx` — without it, effects render
   nothing → **black screen**. (XNA3 effects were pixel-only; MonoGame needs the full vertex stage.)
2. **`fade.fx` fader branches** — a shifted branch made a full-screen blur composite additively
   every frame → the "blur when moving". Branches must be `fader==1` tested correctly.
3. **DXT5 alpha byte-swap** in `tools/xnb_v2.py` (`be_alpha=True`) — otherwise alpha decodes
   to a checkerboard → **dithered rectangles behind sprites**. Re-export if textures are regenerated.
4. **Straight-alpha content + `BlendState.NonPremultiplied`** (124 sites) — XNA3 `AlphaBlend` is
   straight; mapping it to MonoGame's premultiplied `AlphaBlend` made low-alpha quads render as
   hard bright rectangles. The content manager and the blend state **must stay consistent**.
5. **`AndroidInputBridge`** — without it, `GamePad.GetState` is always disconnected (MonoGame bug).
6. **Lense sampler fix** — `Textures[1]` must be bound **after** `EffectPass.Apply()`.
7. **Widescreen logical backbuffer** — changing `screenSize` requires updating render targets,
   post-effect constants, and HUD anchors **consistently**.
8. **`targetSdkVersion=35`** and **`EmbedAssembliesIntoApk=true`** in the build.
9. **Raw-texture byte order is `A,R,G,B`** in `tools/xnb_v2.py`,
   `tools/export_xnb_textures.py` and `Platform/Import/XnbV2.cs`. The Xbox 360 is
   big-endian, so `SurfaceFormat.Color` (`D3DFMT_A8R8G8B8`) is stored `A,R,G,B`;
   the old `B,G,R,A` decode read the alpha as blue → every raw texture, the comic
   panels and some menu art rendered blue. See `android/notes/comic-colour-fix.md`.

---

## 9. Known issues / limitations

- **Load time now ~5 s warm.** The remaining bottleneck is the **4.0 s logo-carousel gate** — the
  actual loader (~1.5 s) is entirely hidden behind it; shortening it further makes the 0.5 s
  cross-fades overlap and look like a glitch. Next target: the ~0.83 s startup→first-frame.
- **Menu canvas originally 1024×600** — menus were stretched until the widescreen fix; verify menu
  layout if you touch `MainMenu` sizing.
- **"Unlimited"/120 FPS cap at the panel refresh** (60 Hz on the A52; the S24's 120 Hz panel can
  actually reach 120). `FrameLimiter` uses `IsFixedTimeStep`/`TargetElapsedTime`.
- **Gameplay input** — play with a physical gamepad, or enable the optional on-screen
  **TOUCH CONTROLS** overlay in `ANDROID SETTINGS` (§17). Menu tap/swipe gestures remain
  available independently and never affect active gameplay.
- **Controller rumble** is first-class only on **Android 12+ (API ≥ 31)** via
  `InputDevice.VibratorManager`. On Android ≤ 11 the controller path is best-effort: the
  device vibrator always works, but the gamepad only via the `@hide` Bluetooth HID-host
  output report and only for Xbox One-family pads (`Platform/BluetoothHidRumble.cs`); the
  `BLUETOOTH`/`BLUETOOTH_ADMIN` permissions are scoped to `maxSdkVersion=30`.
- **`am force-stop` / LMK kills** cannot run managed save code — only level-completion autosave
  protects that case (Android platform constraint). The explicit **EXIT** row does save
  synchronously before quitting.
- **Saves are app-private** — lost on uninstall / clear-data.
- **Build is now Release + AOT, arm64** (~95 MB APK) — `EmbedAssembliesIntoApk=true`,
  `AndroidPackageFormat=apk`. (Debug builds are ~181 MB, all-ABI, and much slower to load.)
- **APK size** trimmable; an unused `INTERNET` permission is inherited from the template.

---

## 10. Completed optimizations & remaining work

### Done: load-time optimization (see `android/notes/load-optimization.md`)
Baseline **~24.6 s** → **~5.0 s warm / ~6.5 s cold**. Each change was measured independently:

| Change | Effect | Kept |
|---|---|---|
| **Release + AOT** (default full AOT) | loader 22.5 s → 6.2 s | ✅ |
| **Skip APK re-extraction** (`content.stamp` = versionCode+LastUpdateTime) | extract 1.5 s → 0; first frame 2.1 → ~0.7 s | ✅ |
| **Parallel PNG decode** (prefetch pool; GL upload stays on the render thread) | loader 5.2 s → ~1.5 s | ✅ |
| **Compressed carousel gate** (`Globals.LoaderCarouselSeconds` 20.45 → **4.0**) | menu 22.3 → ~5.0 s | ✅ |
| Defer character/map assets | not needed (loader no longer the critical path) | ❌ documented |
| Parallel v1 (Load blocking on a prefetch Task) | **deadlocked** — reverted, reimplemented non-blocking | ❌ |

Final flags: `Globals.LoaderCarouselSeconds = 4.0f`, `Globals.ParallelTextureDecode = true`,
`Globals.SkipIntro = false`.

### Remaining
- **Startup → first frame (~0.83 s)** — the next optimization target.
- **Menu stretch polish** — menus compose on a 1024×600 canvas; verify aspect at the widescreen size.
- **Distribution build** — already Release+AOT; drop the unused `INTERNET` permission, consider
  arm64-only to shrink the APK.
- ✅ **Gameplay touch overlay** — delivered as the optional `TOUCH CONTROLS` full-controller
  overlay + layout editor (2026-10-03, see §17); default OFF.

---

## 11. ⚠️ Working rules for agents

1. **NEVER use `/tmp` for anything you want to keep — it is wiped** (it happened once mid-project).
   Use `<dev-workspace>/…` for all durable artifacts. Durable tooling now lives in
   `~/dishwasher/tools/` (rescued from `/tmp`).
2. **Do not touch the device unless the user explicitly authorises it.** Running adb input injection
   on the user's phone unattended caused a real incident. Supervised runs only, announced.
3. **Back up device saves before testing and restore them after** (md5-verify).
   Backups: `~/dishwasher/tools/device-save-backups/`.
4. **Keep `GameSource/` edits minimal and marked** (`// PORT …`). Android-specific code belongs in
   `Platform/`. `managed/decompiled/` is READ-ONLY source of truth.
5. **Never claim a fix/perf win without measurement** (screenshot + numbers; before/after).
6. **Respect §8** — the protected fixes.
7. `source tools/scripts/env.sh` before any `dotnet`/`mgcb` command.
8. Disk is tight (~3.6 GB free) — clean APKs/intermediates between iterations.

---

## 12. Documentation index (`android/notes/`)

| File | Covers |
|---|---|
| `decompile-inventory.md` | what the decompiled game contains; XNA 3→4 porting surface |
| `xna-netcf-feasibility.md` | **why ReXGlue cannot work** (the IL/CLR proof) |
| `xenia-reference.md` | why Xenia cannot run this title (`#642 WONTFIX`) |
| `asset-formats.md` | every asset format; XNB v2; `.zdx`/`.dqx`/`.dgx`; audio banks |
| `content-conversion.md` | XNB v2 → PNG, DXT endianness, SpriteFont plan, loading strategy |
| `shader-feasibility.md` | effect container format; Xenos microcode → HLSL (XenosRecomp) |
| `audio-conversion.md` | XACT → OGG/WAV; cue map; shim design |
| `music-fix.md` | **background music fix** — trigger map (file:line), shim cue-cache root cause, device evidence |
| `render-diagnosis.md` | the black-screen root cause (MatrixTransform) + render pipeline map |
| `colour-grade-fix.md` | vertex-colour fix, faithful post-effect transcription |
| `visual-fidelity.md` | the fade/"blur when moving" bug; reference-vs-port measurements |
| `rectangle-artifact.md` | DXT5 alpha byte-order bug (dithered rectangles) |
| `comic-cutscene.md` | comic panel path + the premultiplied-alpha discovery |
| `comic-colour-fix.md` | **blue comic/menu cast** — raw 32bpp byte-order bug (`A,R,G,B`), fix + before/after |
| `final-consolidation.md` | the straight-alpha/NonPremultiplied blend fix (124 sites) |
| `widescreen.md` | logical backbuffer, uniform scale, view map |
| `boot-config.md` / `intro-restored.md` | landscape, immersive, intro skip + restore, timings |
| `save-system.md` | save format/paths, autosave triggers, background persistence |
| `android-settings-menu.md` | the ANDROID SETTINGS tree + storage separation |
| `fps-counter-limiter.md` | FPS counter + working frame limiter, game-speed invariance proof |
| `touch-controls.md` | touch synthesis, coordinate spaces, BACK mapping |
| `touch-tap-select.md` | draw-time row hit-testing (tap = select+activate) |
| `input-diagnosis.md` | the MonoGame `GamePad.Initialize()` bug |
| `multiplayer-lan.md` | **System Link LAN co-op** — netcode map, UDP protocol, 2-phone verification |
| `public-build.md` | **no-bundled-content build** — switch, import/validation, on-device texture derivation |
| `public-build-audio.md` | public build **audio** — native XMA decoder (ffmpeg/vorbis), import wiring, licences |
| `orientation.md` | landscape flip (`UserLandscape`), rotation-safe widescreen |
| `load-optimization.md` | load-time work (24.6 s → ~5 s): Release+AOT, parallel decode, carousel gate |
| `storage-screen.md` | storage-device screen skipped, first-run auto-created save, options-row renumbering |
| `rumble.md` | vibration routing (OFF/PHONE/CONTROLLER/BOTH), API split, Bluetooth HID fallback |
| `exit-quit.md` | EXIT really quits (save → finish task → kill) instead of `MoveTaskToBack` |
| `reference/youtube/` | 685 reference frames + contact sheets + `INDEX.md` |
| `reference/xenia/` | Xenia attempt logs/screenshots (negative result) |

Port-side notes: `<dev-notes>/` (shaders, audio, wine shader build, content integration,
`proof/` screenshots).

---

## 13. Public build (no-bundled-content variant)

Built with **`-p:DishwasherPublic=true`**. Full write-ups: `android/notes/public-build.md`,
`android/notes/public-build-audio.md`.

- **Ships no game content** (~9.7 MB arm64 APK; assets = only `fx/` + `Content/fx/` (our
  re-authored shaders) + `import-manifest.json`). Verify with an asset listing, don't assume.
- **Includes the touch-controls work (2026-10-03):** `TOUCH CONTROLS` OFF/ON + `EDIT TOUCH LAYOUT`
  in ANDROID SETTINGS, a full on-screen gamepad (both sticks, d-pad, face + shoulder buttons,
  triggers) and the comic `SPEED UP` (hold) / `SKIP` (tap) buttons. The overlay is default OFF and
  only routed in-level. See §17.
- **⚠️ Re-import reminder:** if you imported with an **earlier** public build, you must clear /
  regenerate the derived content and **re-import**. The importer skips already-derived files, so an
  in-place app update keeps the stale derived PNGs/audio (this includes the raw-texture colour fix
  in §16).
- **First run** → import screen (touch-driven) → user picks the **retail XBLA zip**, a **zip** of
  extracted files, or a **folder** (SAF, content-URI safe; it even unpacks the STFS/LIVE package).
- **Tolerant content check (default, 2026-10-05):** the supplied copy is compared to
  `assets/import-manifest.json` (458 original files: `data/**`, `gfx/**.xnb`, `gfx/maps/maps.zdx`,
  `Resources/**`, `sfx/*.xwb/.xsb/.xgs`), but a **differing** file (a different game revision) or a
  **missing** file is a **warning, not a blocker**:
  - *matched* → used as-is;
  - *differed* (size or SHA-256) → warned and still imported (a different revision's asset is a
    valid asset);
  - *missing* → warned and skipped (that asset will not be derived; e.g. a missing `.xwb`/`.xsb`
    means that audio bank is simply absent/silent);
  - **nothing usable** → the only block: *no* usable file, or any of the **core boot files**
    (`data/levels.zdx`, `gfx/text.xnb`, `gfx/Arials.xnb`,
    `Resources/dishX.Resources.Strings.resources`) absent. Per-file derive/copy failures are logged
    and never abort the import.
  The screen shows a concise summary (`X matched · Y differed · Z missing`) and says whether the
  import is fine-but-continuing vs. which features may be missing; the **full 458-line per-file
  detail goes to the log** (`logcat -s Dishwasher`, `[import]` lines), not the screen. Regenerate the
  manifest with `tools/gen_import_manifest.py`.
- **Strict mode (re-enable)**: `PublicBuild.StrictContentValidation` defaults **false**. Build with
  `-p:DishwasherStrictContent=true` to define `DISHWASHER_STRICT_CONTENT` and restore the original
  ownership gate (any missing/mismatched file blocks, per-file report).
- **On-device derivation:** textures/fonts via a C# port of the XNB v2 decoder
  (`Platform/Import/XnbV2.cs`, pixel-identical to the Python reference); audio via a native
  **XMA decoder**.
- **Inherits the raw-texture colour fix (2026-10-03):** `XnbV2.cs::DecodeTexture` now
  decodes raw `SurfaceFormat.Color` as **`A,R,G,B`** (was `B,G,R,A`), so the public build
  no longer renders the comic/menu art blue. See `android/notes/comic-colour-fix.md`.
  **A device that already imported with the old decoder must re-import** — the importer
  skips already-derived content, so an in-place app update keeps the old blue PNGs until
  the derived content is cleared/regenerated.
- **Audio decoder:** minimal **FFmpeg 7.1** (`xma1`/`xma2` only, **LGPL-2.1 dynamically linked**) +
  **libvorbis (BSD)** in `Platform/Audio/native/arm64-v8a/` (~1.95 MB, **arm64-only**), P/Invoked from
  `NativeXma.cs` and guarded to degrade to silence. Containers parsed in C#: `XwbReader.cs`,
  `XsbReader.cs`, `AudioImporter.cs` → 222/222 waves, 216 cues (cue map identical to internal).
  Build sources + licences: `tools/xma-decoder/`.
- **Known limitation:** long music tracks are Vorbis q≈4 (as internal); short SFX are lossless PCM16
  (~38 MB derived vs internal ~25 MB ADPCM). Decoder is arm64-only (all targets are arm64).

## 14. ✅ Resolved — background music never played (both builds)

**Status:** fixed and verified on device in both builds. Full write-up:
`android/notes/music-fix.md`. Proof logs: `android/notes/proof/music-fix/`.

**Symptom.** SFX played in menus and in-game; background music did not, in either the
internal or the public build.

**Root cause (shim, not the game and not the assets).** All menu/in-game background
music is driven by the game class `Music`, whose `Play()`/`setMusic()` **dispose a
`Cue` then immediately re-fetch it by name** to (re)start it — legitimate XNA usage,
where `SoundBank.GetCue` returns a fresh playable cue. The shim's
`Dishwasher.Audio.SoundBank.GetCue` instead returned its **cached** `Cue` even after
the game had disposed it; `Cue.Play()` begins `if (IsDisposed …) return;`, so every
music play was a silent no-op, retried every frame. SFX were unaffected because they
use `SoundBank.PlayCue` fire-and-forget and never dispose a cue.

**Fix.** `src/Dishwasher/Platform/Audio/XactShim.cs` — `SoundBank.GetCue` returns a
cache hit only while `!c.IsDisposed`; otherwise it creates a fresh `Cue` (XNA
semantics). One method, ~6 lines. No cue-name special-casing, no game-logic edits, no
build-specific code; both builds share the file.

**Verification (device SM-G960F / serial `<device-serial>`).**
- Pre-fix instrumented logcat (game-driven, no self-test): `Cue.Play('soft1') ->
  REFUSED: IsDisposed=true`, ~60×/s forever.
- Post-fix internal: menu `soft1` and gameplay `music2` both play
  (`... state=Playing`, `Music` volume ramps to 1.0), 122 SFX plays, 0 `REFUSED`,
  no crash/ANR.
- Post-fix public: on-device import derives 222/222 waves (208 WAV, 14 OGG) / 216 cues;
  `soft1` plays from a derived OGG; 0 `REFUSED`.
- Final (instrumentation-removed) internal and public builds both log
  `[audio] cue playing: 'soft1' -> sfx/ogg/music/music_w007.ogg (loop=True)`.

**Residual gap.** The public enemy-encounter `music2` switch was not separately
captured (reaching it needs physical movement; no controller in the automated run) —
the same fixed shim path was exercised by `soft1` on the derived OGG. Also, long music
OGGs decode synchronously on first play (~3–5 s hitch, pre-existing; no ANR). Details in
`android/notes/music-fix.md §6`.

**Warning retained for future agents (still valid).** An early "music works" claim came
from the audio agent's **self-test** calling `playCue("music3")` directly and seeing
`IsPlaying=True`. That only proves the shim can play an OGG when asked — it does NOT
prove the game ever asks for music, and it masked the bug above. Treat component
self-tests as evidence about the component, not about game behaviour.

## 15. ❌ TV / native-1080p variant — attempted and abandoned (2026-10-03)

A separate TV variant (`-p:DishwasherTv=true` → render natively at the panel
**1920×1080**, SurfaceView scale **1.0**, no in-app upscale) was prototyped and
tested on the Android TV at `<lan-ip>` (`armeabi-v7a`, API 30). It booted
correctly and the compositor confirmed a true native buffer (1920×1080,
identity layer transform — vs the phone/TV baseline 1280×720 scaled ×1.5), but
the TV's SoC is **too slow**: the game's own FPS counter fell to **~19–21 fps**
on the main menu at 1080p (FPS lock 30). Not shippable.

**All TV changes were reverted** (WidescreenConfig, Activity1, Dishwasher.csproj,
the Game1/Intro loader/draw tweaks). Both phone builds re-verified clean
(internal + `-p:DishwasherPublic=true`, 0 errors).

**Do not retry without a faster device.** The TV is not a target.

## 16. ✅ Resolved — comic/menu blue colour cast (raw-texture byte order)

**Status:** fixed in both build variants' decoder and in the release APK. Full
write-up: `android/notes/comic-colour-fix.md`.

**Symptom.** Comic panels (and some menu art) rendered **blue-lavender** instead of
neutral greyscale.

**Root cause.** The Xbox 360 is **big-endian**, so raw 32bpp `SurfaceFormat.Color`
(`D3DFMT_A8R8G8B8`, packed word `0xAARRGGBB`) is stored MSB-first as file bytes
**`A,R,G,B`**. The decoder used a little-endian **`B,G,R,A`** order, reading the
alpha byte as blue and the real blue as alpha, so every raw texture came out blue.
This was a decode-order bug, not a runtime desaturation.

**Fix.** `tools/xnb_v2.py` (`raw_order` default `"argb"`), `tools/export_xnb_textures.py`
(both call sites), and `src/Dishwasher/Platform/Import/XnbV2.cs` (the raw-32bpp path
the **public build** compiles to derive textures on-device) now decode file byte
1→R, 2→G, 3→B, 0→A. The 80 DXT1/DXT5 textures are unchanged (the DXT5 alpha fix is
untouched). The public build **inherits the fix** through `XnbV2.cs`.

**Re-import required.** A device that already imported with the old decoder keeps the
old derived PNGs (the importer skips existing derived content). Clear/regenerate the
derived content and re-import to pick up the correction; the derived-content stamp
path was not changed.

**Verification.** Comic central-band blue-pixel fraction **0.200 → 0.002** (reference
**0.001**); B−R **−5.6 → −13.0** (reference **−13.0**). The Python and C# decoders
produce byte-identical RGBA for the same XNB. The release APK was rebuilt and the
corrected `DecodeTexture` confirmed in the shipped assembly.

## 17. ✅ Touch controls — optional on-screen gamepad + layout editor (2026-10-03)

**Status:** in the source tree and in the rebuilt public release APK. Owner-driven work done in
the working port and now synced into this repo (`src/Dishwasher/Platform/`).

**New/changed source** (all under `src/Dishwasher/Platform/`, no game-logic files in this repo):
- `TouchGamepadOverlay.cs` (**new**) — the on-screen pad itself: left stick + L3 + d-pad + LT/LB,
  right stick + R3 + A/B/X/Y diamond + RB/RT, plus the comic `SPEED UP` / `SKIP` buttons and the
  `EDIT TOUCH LAYOUT` drag-to-arrange screen (positions stored normalised, so they survive any
  resolution / aspect / widescreen change).
- `TouchControls.cs` — now routes in-level touches to the overlay and owns the menu gestures;
  comic touches are only routed when `TOUCH CONTROLS` is ON.
- `AndroidInputBridge.cs` — publishes the merged touch + hardware pad state (buttons, both sticks,
  triggers) into slot 0; a deflected physical stick wins over the on-screen stick.
- `AndroidSettings.cs` / `AndroidSettingsMenu.cs` — `touchOn` + `touchLayout` persisted settings
  and the new menu rows (`touch controls: off/on`, `edit touch layout`).
- `Activity1.cs` — the touch dispatch now consumes overlay touches and falls through otherwise.

**Behaviour:** default **OFF** (fully inert; menus unchanged). When ON it is drawn and routed only
in-level (`Game1.portTouchPadContext`), never over the front-end or comics, with the layout editor
as the one documented exception. Revert = delete `TouchGamepadOverlay.cs` + the marked
`PORT (touchpad)` hooks.

**Release verification (this repo's build):** the shipped APK's `assemblies.blob` is Xamarin
**`XALZ` (LZ4)**-compressed, so a naive `grep` of the blob returns 0 even when the code is present.
Decoding the app entry (SHA-256 `7282fef1…`, the 2026-10-05 tolerant-import build)
shows `TouchGamepadOverlay`, `TouchControls`, `EnterEditor`, `OnEditorTouch`,
`SetTouchControls`, `TouchOn`, the `SPEED UP` (×3) / `SKIP` (×2) / `touch controls` (×2)
literals, **and the tolerant-import symbols** `ContentImporter`, `ImportReport`,
`NothingUsable`, `CorePresent`, `CoreAnchors`, `StrictContentValidation`, the
`matched / differed / missing` summary strings, `Nothing usable found`,
`Import complete — continuing` and `core_present`.
`apksigner verify` shows the release DN and v1/v2/v3. `targetSdkVersion=35`, `lib/arm64-v8a` only,
assets = `fx/`, `Content/fx/`, `import-manifest.json`; leakage scan (paths, serials, LAN IPs,
keystore password) = **0**.

**⚠️ Re-import reminder (unchanged):** a public-build device that imported **before** this release
keeps its old derived content — clear/regenerate it and **re-import** to pick up the corrected
derived textures/audio; the importer skips files that already exist.

## 18. ✅ Storage skip, EXIT quit, auto-save, rumble/vibration, options tweaks (2026-10-05)

**Status:** in the working port and now synced into this repo's `Platform/`; the public release
APK is rebuilt from this source.

**New/changed source shipped in this repo** (`src/Dishwasher/`):

| File | Change |
|---|---|
| `Platform/AndroidRumble.cs` (**new**) | value-aware, routed rumble engine: maps the game's `GamePad.SetVibration` value (0..1 per motor) to the device vibrator and/or the controller. `Initialize` / `Set` / `ApplyMode` / `StopAll`; requests of 0 cancel, and pause/stop/focus-loss stop all motors. |
| `Platform/BluetoothHidRumble.cs` (**new**) | C# facade over the Java helper + per-controller HID report table (Xbox One model 1708; DualShock 4 is a clearly-marked extension point). |
| `Platform/Java/RumbleHidHost.java` (**new**) | API < 31 Bluetooth HID-host output-report path (`BluetoothHidHost.sendData`) with the standard `VMRuntime.setHiddenApiExemptions` bypass. |
| `Platform/PortExit.cs` (**new**) | the EXIT menu entry saves through the game's own routines, then finishes + removes the task and stops the process. (MonoGame's `Game.Exit()` only calls `MoveTaskToBack`, so the single-instance task stayed alive and relaunching showed a stale screen.) |
| `Platform/VibrationMenu.cs` (**new**) | partial `projectDish.MainMenu`: the game's OWN options row 1 (`HELP & OPTIONS > SETTINGS`) cycles **OFF / PHONE / CONTROLLER / BOTH**. |
| `Platform/AndroidSettings.cs` | persisted `vibration` field, format `VERSION = 2` (older saves default to **BOTH**), plus `SetVibration` / `ClampVib` / `Vibration`. |
| `Activity1.cs` | `AndroidRumble.Initialize(this)` at start; `StopAll()` on pause / stop / focus-loss. |
| `AndroidManifest.xml` | `VIBRATE`, plus `BLUETOOTH` / `BLUETOOTH_ADMIN` scoped to `android:maxSdkVersion="30"`. |
| app icon | the approved shield artwork (`Resources/…`, `drawable/icon.png`) — already present in this repo; regenerable via `docs/assets/generate-icon-from-art.py`. |

**GameSource edits (NOT shipped — `GameSource/` is git-ignored; builders must re-apply these):**
- `projectDish/Globals.cs`: `SkipStorageCheck = true`, `SkipNewSavePrompt = true`.
- `projectDish/MainMenu.cs`: skip the "CHECKING STORAGE DEVICE" fade; create the profile
  automatically instead of the "create new save data?" dialog; the SELECT STORAGE DEVICE row
  is removed (rows after it renumbered: RESET 6→5, BACK 7→6); row 1 draws `VibrationLabel(...)`
  and `case 1:` calls `CycleVibration(settings)`. `MainMenu` must be declared **`partial`** so
  `Platform/VibrationMenu.cs` can extend it.
- `projectDish/Game1.cs`: the same skip / auto-create at the boot prompt (both the message-box
  and the boot path); the storage-check gate honours `Globals.SkipStorageCheck`; `case 4:` calls
  `Dishwasher.PortExit.Quit()`.
- `projectDish/MainText.cs`: `returnToArcadeStr = new StringContainer("exit")` — the main-menu
  row and its confirmation dialog both draw this field, so both now read **EXIT**.

**Revert:** delete the four new `Platform/*` files (+ `Platform/Java/RumbleHidHost.java`), restore
the four `GamePad.SetVibration` calls in `Game1.cs` and `case 4: Exit();`, remove the
`AndroidRumble` hooks in `Activity1.cs`, and drop the `VIBRATE` / `BLUETOOTH` permissions.

**Notes:** `docs/notes/android/storage-screen.md`, `docs/notes/android/rumble.md`,
`docs/notes/android/exit-quit.md`.

**Sync tooling:** `tools/release/sanitize_paths.py` is now shipped here; it is the public,
leak-free copy of the working port's release sanitizer (the exact private literals are
base64-encoded in the script so the public tree never contains them). Run it after any future
sync from the working port to reproduce the sanitization.

