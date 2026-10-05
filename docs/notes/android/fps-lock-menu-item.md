# FPS LOCK settings menu item

> **SUPERSEDED (2026-10-02, later same day):** this in-Options placement was
> **reverted** and FPS LOCK moved into a dedicated top-level **`ANDROID SETTINGS`**
> screen with its own `android_settings.sav`. See
> `android-settings-menu.md`. This file is kept as the historical record of the
> reverted change (and its exact site list, which the revert used).

**Author:** UI/Menu Specialist (subagent)
**Date:** 2026-10-02
**Scope:** edits confined to `src/Dishwasher/` and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

**One-line result:** a new persistent **`FPS LOCK`** option (Unlimited / 30 / 60 /
120) was added to the in-game settings screen, is navigable and wraps, and
round-trips through the existing `settings.sav` — **verified on device
`<device-serial>`**, including loading an old save that has no `fpsLock` field.
**No frame-limiting / vsync / frame-pacing behaviour was added** — the value is
stored and displayed only.

---

## 1. Value mapping and default

`src/Dishwasher/GameSource/projectDish/Settings.cs`:

| value | constant | UI label | meaning (for the later limiter task) |
|---:|---|---|---|
| 0 | `FPS_UNLIMITED` | `fps lock: unlimited` | no limit (default) |
| 1 | `FPS_30` | `fps lock: 30` | limit to 30 fps |
| 2 | `FPS_60` | `fps lock: 60` | limit to 60 fps |
| 3 | `FPS_120` | `fps lock: 120` | limit to 120 fps |

`FPS_LOCK_STATES = 4`; cycling is `(fpsLock + 1) % FPS_LOCK_STATES`.

**Why the default is `0 = Unlimited`:**

* The value is **inert in this task**. Defaulting to `0` keeps the port's
  current behaviour byte-for-byte identical for existing users, so a later
  limiter task cannot silently throttle anyone.
* An **old `settings.sav` has no `<fpsLock>` element**, so `XmlSerializer`
  leaves the field at `default(int) == 0`, which maps directly to Unlimited —
  no migration step and no behaviour change on upgrade.
* `60` would be a defensible "safe mobile" default, but it would be a real
  behaviour change once the limiter lands, with no user opt-in; `0` avoids that.

**To change the set:** edit the four `FPS_*` constants + `FPS_LOCK_STATES` in
`Settings.cs`, the `Write`/`Read` round-trip stays generic, and replace the
`fpsLockStr[4]` literals in `MainText.cs` (order must match the constants).

---

## 2. Sites changed (file:line)

### `GameSource/projectDish/Settings.cs`
| line | change |
|---:|---|
| 25 | `Save` struct: new `public int fpsLock;` (old saves lack it → `0`) |
| 41–49 | new constants `FPS_UNLIMITED/FPS_30/FPS_60/FPS_120/FPS_LOCK_STATES` |
| 61 | new persisted field `public int fpsLock;` |
| 98 | `Write`: `fpsLock = fpsLock` added to the serialized `Save` |
| 134 | `Reset()`: `fpsLock = FPS_UNLIMITED;` |
| 204–208 | `Read()`: `fpsLock = save.fpsLock;` + range clamp to `FPS_UNLIMITED` |

### `GameSource/projectDish/MainText.cs`
| line | change |
|---:|---|
| 484 | new field `public StringContainer[] fpsLockStr;` |
| 3757–3763 | built from literals: `"fps lock: unlimited" / 30 / 60 / 120` |

### `GameSource/projectDish/MainMenu.cs` (settings screen is `case 34`)
| line | change |
|---:|---|
| 2701 | A-press handler: `case 3: settings.fpsLock = (settings.fpsLock + 1) % Settings.FPS_LOCK_STATES;` |
| 2703–2739 | existing cases renumbered: control mode 4→5, storage 5→6, reset 6→7, back 7→8 (per-player `settings` local is unchanged) |
| 1597 | `keyStrong` "view controls" shortcut: `selOption == 4` → `== 5` (control mode moved) |
| 2901/2928 | SFX/BGM left-right volume handler: BGM `selOption == 3` → `== 4`; guard `(2 \|\| 3)` → `(2 \|\| 4)` |
| 4777/4779 | render wrap bounds: `< 0 → 8`, `> 8 → 0` |
| 4798 | `drawOption(3, Globals.maintext.fpsLockStr[settings2.fpsLock], …)` |
| 4799–4814 | BGM 3→4, control 4→5, storage 5→6, reset 6→7, back 7→8 |

### Ordering decision
The option was added at **index 3** exactly as specified, so the resulting top-to-
bottom order is: BLOOD(0), VIBRATION(1), SFX(2), **FPS LOCK(3)**, BGM(4),
CONTROLS(5), SELECT STORAGE DEVICE(6), RESET TO DEFAULTS(7), BACK(8). Existing
indices 3–7 were shifted up by one and every reference to them (A-press, the
`keyStrong` controls shortcut, the SFX/BGM left/right volume handler, and the
render calls) was updated in lock-step. The renderer computes each row's Y from
the index (`i * 48 + …`), so row 3 lands at the same 48 px pitch as the rest;
row 8 (BACK) sits at ≈`8*48 + 300 - 190` ≈ 494 logical px (text baseline ≈ 499),
well inside the 800×600 logical playfield / 1600×720 presented screen — no
overlap (screenshot `01-settings-old-save-unlimited.png`).

### Label / string approach
The label reuses the game's own text system: a `StringContainer` array
(`Globals.maintext.fpsLockStr`) rendered through the same `drawOption(...)` path
as every other setting. It is built from **string literals** rather than a new
`Strings.*` resource entry because the localized strings are shipped as
prebuilt binary `.resources` assets (`Assets/Resources/dishX.Resources.Strings
.{resources,de,es,fr,it,ja,ko,pl,pt,ru,zh-TW}`), which would need a 12-file
regeneration to add a key. The game already constructs option text
programmatically this way (`sfxStr`/`bgmStr`), so this is consistent, and the
renderer upper-cases it exactly like the localized options.

---

## 3. Old-save compatibility (explicit proof)

The pre-change on-device save had **no** `<fpsLock>` element:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Save xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <rumble>false</rumble>
  <blood>1</blood>
  <soundVol>10</soundVol>
  <bgmVol>10</bgmVol>
  <flags>0</flags>
</Save>
```
(fixture saved at `<dev-notes>/proof/fpslock/old-settings.sav.no-fpslock`,
md5 `b66289792aab8640a282335091893dad`).

A standalone `XmlSerializer` test using the post-change `Settings.Save` struct
shape (source: `/tmp/opencode/fpslock-test/`, run with `dotnet run`) gives:

```
[1] old save deserialize: OK, no exception
    blood=1 soundVol=10 bgmVol=10 fpsLock=0 flags=0
    PASS: fpsLock defaults to 0 (Unlimited)
[1b] on-device fixture (/tmp/opencode/fpslock-backup/settings.sav) -> fpsLock=0 blood=1
    PASS: real old settings.sav loads, fpsLock=0
[2] new Save serialized: <fpsLock>2</fpsLock> present
[3] round-trip fpsLock=2 -> PASS
ALL PASS
```

The same was proven **through the real game on device** (§5, screenshot 01): the
APK was installed over the existing old save and the settings screen opened
normally showing `FPS LOCK: UNLIMITED` — no exception, no `Reset()` fallback.
`XmlSerializer` ignores missing elements (default values) and ignores unknown
elements, so new saves remain loadable by code that predates the field too.

---

## 4. Both players (co-op) handled

`Globals.settings` and `Globals.coopSettings` are both instances of the same
`Settings` class (`Globals.cs:1360/1362`), and the whole `case 34` path is
already per-player:

* A-press uses `Settings settings = (optionsOwner == Globals.mainPlayerIndex) ?
  Globals.settings : Globals.coopSettings;` then `settings.fpsLock = …`.
* Render uses `Settings settings2 = …` and `settings2.fpsLock`.
* Persistence uses the existing per-player `Settings.Write(player)` called on
  back-out (`MainMenu.cs:2746–2753`).

No main-player-only special case was introduced for the new field.

---

## 5. Build / device verification

### Build
```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
# Build succeeded. 0 Error(s)  (5 warnings: 4× SYSLIB0006 Thread.Abort, 1× XA1008)
```
`targetSdkVersion=35` unchanged. APK:
`bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk` (20:23).

### Device — `<device-serial>` (SM-A525F), app `com.recomp.dishwasher`
A device **was attached** during this task, so full GUI verification was
performed. Backups were taken first (`/tmp/opencode/fpslock-backup/`,
md5 `b662…dad` settings / `00ae…c01` profile) and the device was **restored to
exactly those bytes afterwards** (verified by md5).

| evidence | result |
|---|---|
| `01-settings-old-save-unlimited.png` | old save opens; new row reads **FPS LOCK: UNLIMITED** (default 0) in the correct position; all other rows intact |
| `02-fps-selected.png` | selector navigates onto the new row |
| `03..05` | A cycles **30 → 60 → 120** on screen |
| `06-fps-wrap-to-unlimited.png` | A from 120 wraps to **UNLIMITED** (4-state wrap) |
| on-disk after back-out | `<fpsLock>2</fpsLock>` (md5 `61c0a159a2b9ba379e72550ca1bf4060`) |
| `07-fps-60-after-cold-restart.png` | `am force-stop` + relaunch → settings reads **FPS LOCK: 60** from `settings.sav` |
| `08-up-wrap-*.png` / `09-down-wrap-*.png` | Up from BLOOD(0) wraps to BACK(8); Down from BACK(8) wraps to BLOOD(0) |
| `10-reset-to-defaults-unlimited.png` | RESET TO DEFAULTS sets the new field back to UNLIMITED |
| `11-bgm-index4-still-works.png` | shifted BGM left-right volume handler still works at its new index 4 |

Evidence directory: `<dev-notes>/proof/fpslock/`.

**Not device-verified:** the co-op (`optionsOwner != mainPlayerIndex`) visual
path, because ADB keyevents only drive player 1 and no second controller was
available. It is the same `case 34` code with a per-player `Settings` local
(§4); it should be spot-checked when a second controller is available.

---

## 6. No frame limiting added (confirmation)

* `fpsLock` has **no consumers** outside `Settings.cs` / `MainText.cs` /
  `MainMenu.cs` (grep for `fpsLock` in `GameSource/` returns only those).
* No changes to `IsFixedTimeStep`, `TargetElapsedTime`, `SynchronizeWithVerticalRetrace`,
  or any frame-pacing code. `Game1.cs:375`'s pre-existing
  `SynchronizeWithVerticalRetrace = false` was **not touched** (Game1.cs is not
  in the modified set).
* `Platform/InputDiagnostics.cs` and `Platform/RenderDiagnostics.cs` both still
  have `Enabled = false`.
* Only three files changed: `Settings.cs`, `MainText.cs`, `MainMenu.cs`
  (`find -newermt` confirms).
* No changes to `MatrixTransform`, `fade`, DXT5 alpha, straight-alpha /
  `BlendState.NonPremultiplied`, `AndroidInputBridge`, the lense sampler fix,
  landscape/immersive, widescreen, the restored intro, or the autosave.

---

## 7. Revert

1. `Settings.cs`: remove `Save.fpsLock`, the `FPS_*` constants, `fpsLock`,
   the `Write`/`Reset`/`Read` lines.
2. `MainText.cs`: remove `fpsLockStr` field + initializer.
3. `MainMenu.cs`: remove `case 3`, shift indices 5→4 / 6→5 / 7→6 / 8→7, restore
   the `keyStrong`/SFX-BGM/render indices and the `<0 → 7` / `> 7 → 0` bounds.
4. Rebuild/reinstall. Old saves without `<fpsLock>` remain valid either way.
