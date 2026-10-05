# The Dishwasher: Dead Samurai — native Android port

A **native Android (arm64-v8a) port** of the Xbox 360 XBLA title
*The Dishwasher: Dead Samurai*, built by decompiling the game's managed code and
recompiling it ahead-of-time against [MonoGame](https://monogame.net/) for .NET.

> **This is a static (ahead-of-time) recompilation of the game's managed code —
> not an emulator.** It does not include, download or distribute the game.
> **You must own a legitimate copy** and import your own game data to play.

> **Not affiliated with, authorised, endorsed by, or connected to Microsoft,
> Xbox, Xbox LIVE, or Ska Studios.** All game names, marks, code, art, audio and
> text remain the property of their respective owners. No game content is
> included or distributed by this project.

---

## Contents

- [What this is](#what-this-is)
- [What works](#what-works)
- [Install and import your own game data](#install-and-import-your-own-game-data)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [Licence and legal](#licence-and-legal)

---

## What this is

The Xbox 360 version of *The Dishwasher: Dead Samurai* is an **XNA Game Studio /
.NET Compact Framework** title: its game logic ships as managed IL. This project
decompiles that IL to C#, ports it from XNA 3.0 to the XNA 4.0–compatible
MonoGame API, and compiles it to **native ARM64** with .NET for Android.

The result is a normal Android app (`com.recomp.dishwasher`) that runs directly
on the CPU/GPU — there is **no PowerPC emulation and no runtime JIT of console
code**. In that sense it is a *static recompilation*: the game's logic is turned
into native ARM64 ahead of time.

It is **not**:

- ❌ an emulator;
- ❌ a build of the game — it contains no game code or assets;
- ❌ usable without your own copy of the game.

### The public APK

The released APK is the **public / content-free** variant. It ships only this
project's own code:

- the Android platform layer and shims,
- the 19 re-authored shaders (as compiled MGFX),
- the first-run importer and the SHA-256 ownership manifest.

Everything derived from the game — textures, fonts, audio — is produced **on
your device** from **your own copy**, then compared against a manifest of
SHA-256 hashes. Nothing is downloaded. The check is **tolerant**: a file that
differs (a different game revision) or is missing is a warning, not a blocker.

---

## What works

| Feature | Status |
|---|---|
| Full single-player story, arcade, guitar solo, shop, comics | ✅ |
| Touch input (menu navigation; tap = select/activate, swipe = d-pad) | ✅ |
| Optional on-screen gamepad + editable layout (default OFF) | ✅ |
| Gamepad (Bluetooth/USB, Xbox-style mapping) | ✅ |
| Rumble / vibration routing (phone and/or controller; OFF/PHONE/CONTROLLER/BOTH) | ✅ |
| Widescreen, uniform scaling (e.g. 2400×1080, no stretch/crop) | ✅ |
| LAN co-op / System Link over UDP (verified with two phones) | ✅ |
| Local split-screen co-op | ✅ |
| Saves (`profile.sav`, `settings.sav`) with autosave on pause/stop and on EXIT | ✅ |
| In-app **Android Settings** (FPS lock: Unlimited/30/60/120; FPS counter) | ✅ |
| Real **EXIT** (saves through the game's own routines, then quits) | ✅ |
| No storage-device screen; first-run save created automatically | ✅ |
| On-device import of your own game data (tolerant SHA-256 check) | ✅ |
| On-device audio decoding (XMA → PCM/OGG) | ✅ |

Known limits: a gamepad is recommended for gameplay (the optional on-screen pad is a
fallback); controller vibration is first-class only on Android 12+, on Android ≤ 11 it is
best-effort via Bluetooth HID (Xbox One-family pads) while the phone vibrator always works;
arm64-v8a only; saves are app-private and lost on uninstall; long music tracks decode on
first play (a short hitch). See `docs/PROJECT_STATE.md` §9 for the full list.

---

## Install and import your own game data

### 1. Install the APK

Download the latest release APK from the **Releases** page and install it on an
**arm64-v8a** Android device (Android 5.0 / API 21 or newer):

```sh
adb install -r dishwasher-public-arm64-<version>.apk
```

Verify the download against the published SHA-256 checksum first:

```sh
sha256sum -c dishwasher-public-arm64-<version>.apk.sha256
```

### 2. Import your game data

On first launch the app shows an **import screen** and asks for your own copy of
the game. You can supply it in any of these forms:

- **the retail XBLA `.zip`** — the app unpacks the STFS/LIVE package inside it;
- **a `.zip` of extracted game files**;
- **a folder** of extracted game files (Android's document picker).

Every required original file is compared against its **exact byte size and
SHA-256 hash** from a bundled manifest, but the check is **tolerant**:

- a **differing** file (a different game revision) is a **warning and is still
  imported** — a different revision's asset is a valid asset;
- a **missing** file is a **warning**; that asset is simply not derived (e.g. a
  missing audio bank means that sound is absent, and the rest still works);
- the import is only **refused** when there is **nothing usable** — no usable
  file at all, or a missing **core boot file** (`data/levels.zdx`,
  `gfx/text.xnb`, `gfx/Arials.xnb`,
  `Resources/dishX.Resources.Strings.resources`).

The screen shows a concise summary (`X matched · Y differed · Z missing`) and
says whether the import is fine and continuing or which features may be missing;
the **full per-file detail is written to the log** (`adb logcat -s Dishwasher`,
`[import]` lines), never dumped on screen. Per-file derive failures are logged
and never abort the import. Strict ownership gating can be re-enabled at build
time with `-p:DishwasherStrictContent=true` (`PublicBuild.StrictContentValidation`,
default `false`).

After the check the app derives textures and fonts from the game's XNB files and
decodes the audio banks **on your device**, using whatever was found, then starts
the game.

> **You must legally own the game.** This project provides no game files, no
> links to them, and no way to obtain them. You are responsible for complying
> with the law where you live.

---

## How it works

1. **No assets are shipped.** The public APK contains only our compiled shaders
   and a hash manifest.
2. **Tolerant ownership check.** On import, 458 original files (`data/**`,
   `gfx/**.xnb`, `gfx/maps/maps.zdx`, `Resources/**`, `sfx/**` banks) are
   compared by size + SHA-256 against `import-manifest.json`. Mismatches and
   missing files are warnings, not blockers; only "nothing usable" (no usable
   content, or a missing core boot file) is refused.
3. **Textures and fonts** are derived on-device from the game's XNB v2 files by
   a C# decoder, producing straight-alpha PNGs and runtime SpriteFonts — pixel-
   identical to the project's Python reference exporter.
4. **Audio** is decoded on-device by a small bundled **XMA decoder**
   (minimal FFmpeg + libvorbis/libogg behind our `libdishaudio.so` bridge).
5. **The port itself** replaces XNA 3.0 with MonoGame and reimplements the
   console services (LIVE/GamerServices, storage, networking) as Android shims,
   plus touch input, a widescreen backbuffer, an FPS limiter, saves, and the
   LAN co-op session layer.

Technical deep-dives live under [`docs/`](docs/), including
[`docs/PROJECT_STATE.md`](docs/PROJECT_STATE.md) and the per-subsystem notes in
[`docs/notes/android/`](docs/notes/android/).

---

## Building from source

This repository contains **our code only**. To build it you must supply your own
decompiled copy of the game — see **[`BUILDING.md`](BUILDING.md)**, which covers:

- obtaining and unpacking your own copy;
- decompiling `game.exe` to C# with `ilspycmd 9.1.0.7988`;
- the manual XNA 3 → MonoGame porting edits you must re-apply (not included);
- the toolchain (.NET 8, Android SDK 35, NDK r28b, JDK 21, Wine);
- exact commands for the public, arm64-only, internal and debug builds;
- how to sign the release APK with **your own** keystore.

---

## Licence and legal

- Original code, scripts and artwork in this repository: **MIT** — see
  [`LICENSE`](LICENSE).
- Third-party components and their licences: see
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
  Notably, the public build bundles **FFmpeg** (LGPL-2.1-or-later, dynamically
  linked) and **libogg/libvorbis** (BSD-3-Clause), and uses **MonoGame** (MS-PL),
  **NVorbis** (MIT) and **OpenAL Soft** (LGPL).
- **No game content is included or distributed.** *The Dishwasher: Dead Samurai*
  and all of its content are © their respective owners (Ska Studios and others).
  This is an unofficial fan project, not affiliated with or endorsed by
  Microsoft, Xbox, Xbox LIVE, or Ska Studios. **You must own the game.**
