# ANDROID SETTINGS menu — top-level entry + own screen + own storage

**Author:** UI/Menu Architect (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (SM-A525F, `adb $ADB`), app `com.recomp.dishwasher`
**Scope:** edits confined to `src/Dishwasher/` and `android/notes/` (+ proof under
`<dev-notes>/proof/`). Read-only trees (`managed/decompiled`, `assets-clean`, `port`,
`rexglue-sdk*`, `android-app`) were **not** modified.

**Result in one line:** the previous agent's in-Options FPS LOCK item is fully reverted
(the game's original Options screen is byte-identical to the decompiled original again),
and a new top-level **`ANDROID SETTINGS`** main-menu entry opens a dedicated screen with
**FPS LOCK** (`Unlimited / 30 / 60 / 120`, default Unlimited) backed by its **own**
`android_settings.sav` — navigable, persistent across a cold restart, and **no frame
limiting is added** (value is stored/displayed only).

---

## 0. TL;DR

| item | result |
|---|---|
| revert of in-Options FPS LOCK | **done** — `Settings.cs` & `MainText.cs` byte-identical to decompiled; `MainMenu.cs` `case 34` A-press + draw blocks byte-identical to decompiled |
| new tree | `Platform/AndroidSettings.cs` (store) + `Platform/AndroidSettingsMenu.cs` (`partial MainMenu` screen) |
| main-menu entry | `ANDROID SETTINGS`, after `HELP & OPTIONS`, before `RETURN TO GAME LIBRARY`, `num3` trial offset honoured |
| screen | `FPS LOCK` + `BACK`, same style/nav as the game's menus |
| storage | `<app-private>/TheDishwasher/android_settings.sav` (XML) — **not** `settings.sav`; separate md5s proven |
| persistence | written on change + on `OnPause`/`OnStop` (autosave hook) + read at startup; survives `am force-stop` |
| build | `Build succeeded. 0 Error(s)` (5 pre-existing warnings) |
| device | fully verified incl. cold restart; device saves backed up **and restored** (md5 match) |
| frame limiting | **none** |

---

## 1. New Android-specific tree

### 1.1 `Platform/AndroidSettings.cs` — storage model (namespace `Dishwasher`)

A small static, lazily-loaded store. Deliberately independent of
`projectDish.Settings`.

* File: `android_settings.sav`, same app-private directory the game uses:
  `StorageContainer.Path` (= `Environment.SpecialFolder.Personal + "/TheDishwasher"`,
  see `Platform/Shim_Storage.cs:49-61`). On this device:
  `/data/data/com.recomp.dishwasher/files/Documents/TheDishwasher/android_settings.sav`.
* Format: .NET `XmlSerializer` over a tiny `[Serializable] struct Data { int version; int fpsLock; }`.
* Values: `FPS_UNLIMITED=0 / FPS_30=1 / FPS_60=2 / FPS_120=3`, `FPS_LOCK_STATES=4`;
  `ClampFps` snaps out-of-range/corrupt values to `0` (Unlimited).
* API: `FpsLock` (getter), `SetFpsLock(v)` (sets **and saves**), `Load()` (startup),
  `Save()` (loads first if needed, so a background before first read cannot clobber),
  `FilePath`.
* Robustness: missing file → defaults; corrupt/unreadable → defaults in memory
  (never overwrites a good file on read failure); all I/O exceptions logged via
  `Log.Exception("android-settings.*")`, never fatal.

Example on-disk content (created on device during verification):

```xml
<?xml version="1.0" encoding="utf-8"?>
<Data xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <version>0</version>
  <fpsLock>1</fpsLock>
</Data>
```

### 1.2 `Platform/AndroidSettingsMenu.cs` — the screen (namespace `projectDish`)

`public partial class MainMenu` extension, so the Android-only screen code lives
**outside** the original `GameSource/projectDish/MainMenu.cs` while reusing the
game's own drawing/navigation/text helpers (`drawOption`, `hitButton`,
`DrawMenuHeading`, `Globals.maintext`, the `selOption` cursor, `transType/transGoal`
state machine).

* New menu-state id `ANDROID_SETTINGS_LEVEL = 42` (unused range; `levelSelOption[]` is
  sized 64).
* Rows: `FPS LOCK` (index 0, cycling 0→1→2→3→0) and `BACK` (index 1).
* `getCurScreen()` maps 42 → screen `6`, the same backdrop/cursor bucket as the
  original Options screen, so the visuals match.
* Labels are **literal-built `StringContainer`s** (`"android settings"`,
  `"fps lock: unlimited/30/60/120"`), the same approach the previous agent used for
  `fpsLockStr` and the game already uses for `sfxStr`/`bgmStr`. Reason: the localized
  strings ship as prebuilt binary `.resources` (11 locales) whose regeneration is out
  of scope. They are built **lazily** on first use (not in a field initializer)
  because `StringContainer`'s reserved-symbol parsing depends on
  `Globals.spriteFontMode`, which is only final after content load. Lowercase literals
  avoid the reserved A/B/X/Y glyphs; the renderer upper-cases them like all options.
* Structure for future options: add a row index constant + one `drawOption` line in
  `DrawAndroidSettingsScreen`, one `case` in `AndroidSettingsActivate`, and a field in
  `AndroidSettings.Data` (XmlSerializer tolerates the new/missing element).

---

## 2. Minimal, clearly-marked edits to the game's own files

Every hook below is marked `PORT (android settings)`.

### 2.1 `GameSource/projectDish/MainMenu.cs` (9 hooks)

| line | hook |
|---:|---|
| 12–14 | class declared `public partial class MainMenu` |
| 1429–1433 | B/back switch: `case 42: AndroidSettingsBack();` → main menu |
| 1604 | `keyStrong` "view controls" restored: `selOption == 5` → `== 4` (revert) |
| 1740–1747 | main-menu A-press (`num8` switch): new `case 6` → `transGoal = 42`; `RETURN TO ARCADE` shifted `case 6`→`case 7` |
| 1761–1764 | A-press switch (`keyJump`): `case 42: AndroidSettingsActivate();` |
| 2916 / 2943 | SFX/BGM handler restored: guard `(2\|4)`→`(2\|3)`, BGM `==4`→`==3` (revert) |
| 3296–3299 | `getCurScreen()`: `case 42: return 6;` |
| 3859–3868 | main-menu draw `case 0`: new row `drawOption(5+num3, GetAndroidSettingsLabel())`; `RETURN TO ARCADE` → `6+num3`; wrap bounds `5+num3`→`6+num3` |
| 4795–4798 | `DrawButtons`: `case 42: DrawAndroidSettingsScreen(...)` |
| 4804/4806 | Options draw wrap bounds restored `8`→`7` (revert) |
| 5671–5672, 5815–5816 | `hitButton`/`drawOption`: `if (level == 34)` → `if (IsSettingsLikeLayout())` (`level==34 \|\| level==42`) so both screens share the exact layout offset |

The `num8` mapping makes the new row behave identically in normal and trial modes:

| row | draw index | non-trial `num8` | trial `num8` | `num8` case |
|---|---:|---:|---:|---:|
| OPTIONS | `4+num3` | 5 | 5 | 5 |
| **ANDROID SETTINGS** | `5+num3` | 6 | 6 | **6 (new)** |
| RETURN TO ARCADE | `6+num3` | 7 | 7 | 7 |

### 2.2 `GameSource/projectDish/Settings.cs` — revert to pristine

Removed the previous agent's `Save.fpsLock`, the `FPS_*`/`FPS_LOCK_STATES`
constants, the `fpsLock` field, and the `Write`/`Reset`/`Read` lines.
Verified: `diff` against `managed/decompiled/game/projectDish/Settings.cs` is
**empty** (byte-identical to the original).

### 2.3 `GameSource/projectDish/MainText.cs` — revert to pristine

Removed the `fpsLockStr` field and its literal initializer. `diff` against the
decompiled original is **empty** (byte-identical).

### 2.4 Port-lifecycle files (not original game logic)

* `Platform/SaveAutosave.cs`: on background, also calls `AndroidSettings.Save()`
  (own try/catch, placed before the game-profile/device guards so it always runs).
* `Activity1.cs`: `AndroidSettings.Load()` right after `AndroidContentBootstrap.Prepare()`
  (explicit read at startup).

---

## 3. Revert of the old placement — exact evidence

Old placement sites (per `fps-lock-menu-item.md §2`) and their state now:

| file | old change | now |
|---|---|---|
| `Settings.cs` | field/constants/Save/Write/Reset/Read | **removed**, file = decompiled original |
| `MainText.cs` | `fpsLockStr` field + init | **removed**, file = decompiled original |
| `MainMenu.cs` `case 34` A-press | `case 3` fps + 5/6/7/8 shifts | **removed**; block byte-identical to decompiled |
| `MainMenu.cs` `case 34` draw | `drawOption(3, fps)` + 4/5/6/7/8 shifts | **removed**; block byte-identical to decompiled |
| `MainMenu.cs` wrap bounds | `<0→8`, `>8→0` | **restored to 7** |
| `MainMenu.cs` `keyStrong` | `selOption == 5` | **restored to 4** |
| `MainMenu.cs` SFX/BGM | guard `(2\|4)`, BGM `==4` | **restored to (2\|3), ==3** |

Proof commands (run this session):

```sh
diff managed/decompiled/game/projectDish/Settings.cs  <dev-workspace>/.../Settings.cs   # empty
diff managed/decompiled/game/projectDish/MainText.cs   <dev-workspace>/.../MainText.cs    # empty
diff <(sed -n '2689,2761p' managed/.../MainMenu.cs) <(sed -n '2689,2761p' <dev-workspace>/.../MainMenu.cs)  # A-press case34 identical
diff <(sed -n '4769,4816p' managed/.../MainMenu.cs) <(sed -n '4769,4816p' <dev-workspace>/.../MainMenu.cs)  # draw  case34 identical
grep -rn "fpsLock\|fpsLockStr\|FPS_LOCK" src/Dishwasher/GameSource/   # NONE
```

---

## 4. Old-save compatibility (both `settings.sav` shapes)

`XmlSerializer` ignores unknown elements and defaults missing ones, so the reverted
`Settings.Save` (rumble/blood/soundVol/bgmVol/flags) loads **both** shapes:

* **Shape A — has `<fpsLock>`** (written by the previous agent; this is the actual
  pre-test on-device save, md5 `fa33d5210aab72bfe9e53fe18157b356`):

  ```xml
  <rumble>false</rumble><blood>1</blood><soundVol>10</soundVol>
  <bgmVol>10</bgmVol><fpsLock>0</fpsLock><flags>0</flags>
  ```

* **Shape B — no `<fpsLock>`** (pristine shapes before the previous agent).

Standalone `XmlSerializer` test using the **reverted** struct (source copied to
`<dev-notes>/proof/android-settings/xmltest/`):

```
[A: with <fpsLock>]    deserialize OK: rumble=False blood=1 soundVol=10 bgmVol=10 flags=0
[B: without <fpsLock>] deserialize OK: rumble=False blood=1 soundVol=10 bgmVol=10 flags=0
[write] reverted save output contains <fpsLock>: False
ALL PASS
```

On-device:

* Install #1 ran against Shape A (the device's real save) → main menu, Android settings
  and Options all opened normally, no exception (`16` cold-boot screenshots, logcat clean).
* A normal game-driven `Settings.Write` (back out of Options) produced Shape B
  (no `<fpsLock>`):
  `<rumble>false</rumble><blood>1</blood><soundVol>10</soundVol><bgmVol>10</bgmVol><flags>0</flags>`.
* A relaunch against a hand-crafted Shape B fixture also loaded cleanly
  (`16-pristine-save-loads.png`).
* The Android settings file is **never** mixed with `settings.sav`: after the whole
  session setting `fpsLock`, `settings.sav` md5 was unchanged
  (`fa33d521…`) while `android_settings.sav` held `fpsLock=1`.

---

## 5. Build / device verification

### 5.1 Build

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
# Build succeeded. 0 Error(s)   (5 warnings: 4× SYSLIB0006 Thread.Abort, 1× XA1008)
```

`targetSdkVersion=35` unchanged. APK
`bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`, 181,431,864 bytes,
md5 `ef050bfd9d4bed30185f6b799e19f9e2`.

### 5.2 Device `<device-serial>` (full GUI run)

Backups taken before testing (`/tmp/opencode/android-settings/device-backup/`,
settings md5 `fa33…`, profile md5 `00ae…`) and **restored afterwards exactly**
(both md5 match; the test-created `android_settings.sav` was removed). Evidence in
`<dev-notes>/proof/android-settings/`:

| evidence | result |
|---|---|
| `01-main-menu.png`, `02-after-a.png` | cold boot against Shape A save; main menu fine |
| `03-android-selected.png` | **`ANDROID SETTINGS`** row present, after `HELP & OPTIONS`, before `RETURN TO GAME LIBRARY`; selector moves onto it |
| `04-android-screen.png` | dedicated screen: heading `ANDROID SETTINGS`, `FPS LOCK: UNLIMITED`, `BACK` |
| `05-fps-30.png`, `06-fps-60.png`, `07-fps-120.png` | A cycles Unlimited→30→60→120 |
| `08-fps-wrap-unlimited.png` | A from 120 wraps to UNLIMITED (4-state wrap) |
| on disk after each A | `android_settings.sav` `fpsLock` changed 1→2→3 (written **on change**) |
| `09-back-to-main.png` | B returns to main menu, selector preserved |
| `10-fps-30-after-cold-restart.png` | `am force-stop` + relaunch → Android settings reads **FPS LOCK: 30** from disk |
| `13-settings-selected.png`, `14-original-options-screen.png` | original Options screen back to pristine: BLOOD / VIBRATION / SFX / BGM / CONTROLS / SELECT STORAGE DEVICE / RESET TO DEFAULTS / BACK — **no FPS LOCK row** |
| `15-bgm-left-index3.png` | restored SFX/BGM left-right handler works at original index 3 (BGM → 90%) |
| `16-pristine-save-loads.png` | Shape B save loads cleanly |
| logcat | no `FATAL EXCEPTION` / `AndroidRuntime` for the app during any run |

Autosave hook: `AndroidSettings.Save()` is invoked from the same
`Activity1.OnPause/OnStop → SaveAutosave.Save()` path documented in
`save-system.md`; the value is written on every change regardless, so a process
kill (`am force-stop`) still preserves the last changed value.

---

## 6. No frame limiting added (confirmation)

* `grep -rn fpsLock GameSource/ Platform/` returns only `Platform/AndroidSettings.cs`
  (the new store). No consumer in the game loop.
* No change to `IsFixedTimeStep`, `TargetElapsedTime`, `SynchronizeWithVerticalRetrace`
  or any frame-pacing code. `Game1.cs:375`'s pre-existing
  `SynchronizeWithVerticalRetrace = false` is **untouched** (`Game1.cs` is not in the
  modified set).
* `Platform/InputDiagnostics.cs` and `Platform/RenderDiagnostics.cs` both still have
  `Enabled = false`.

---

## 7. Non-regression

* `MatrixTransform`, `fade`, DXT5 alpha, straight-alpha / `BlendState.NonPremultiplied`
  (`grep -rl BlendState.AlphaBlend GameSource/` = **0**), `AndroidInputBridge`, the
  lense sampler fix, landscape/immersive, widescreen (`WidescreenConfig.cs`), the
  restored intro, and autosave are untouched.
* Only these files changed this task:
  * **new** `Platform/AndroidSettings.cs`
  * **new** `Platform/AndroidSettingsMenu.cs`
  * `GameSource/projectDish/MainMenu.cs` (revert + 9 marked hooks)
  * `GameSource/projectDish/Settings.cs` (revert →= original)
  * `GameSource/projectDish/MainText.cs` (revert →= original)
  * `Platform/SaveAutosave.cs` (one autosave call)
  * `Activity1.cs` (one startup load call)

---

## 8. Revert instructions (this whole feature)

1. Delete `Platform/AndroidSettings.cs` and `Platform/AndroidSettingsMenu.cs`.
2. Remove the 9 `PORT (android settings)` hooks from
   `GameSource/projectDish/MainMenu.cs` (including `partial`), or restore from
   `managed/decompiled/game/projectDish/MainMenu.cs` where applicable.
3. Remove the `AndroidSettings.Save()` call in `Platform/SaveAutosave.cs` and the
   `AndroidSettings.Load()` call in `Activity1.cs`.
4. Optionally delete the device file
   `files/Documents/TheDishwasher/android_settings.sav`.
5. Rebuild/reinstall.

`Settings.cs` and `MainText.cs` are already back to the decompiled originals; no
further action needed there.
