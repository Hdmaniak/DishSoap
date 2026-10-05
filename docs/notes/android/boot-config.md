# Boot config — landscape lock + intro cut (The Dishwasher, MonoGame/Android port)

> ## ⚠️ UPDATE 2026-10-02 (later same day): **INTRO RESTORED**
> `Globals.SkipIntro` is now **`false`** (`GameSource/projectDish/Globals.cs:28`).
> The logo carousel plays during the ~20 s content decode again; no more black
> screen. All six sites below fall through to their original branches. **§2 and
> §6 are historical for the SkipIntro cut** — see
> `android/notes/intro-restored.md` for the revert verification, device timings
> and proof screenshots. Landscape lock / immersive fullscreen / widescreen are
> unchanged.

> ## ⚠️ UPDATE 2026-10-03: **ORIENTATION NOW FOLLOWS BOTH LANDSCAPE DIRECTIONS**
> `Activity1` now uses `ScreenOrientation.UserLandscape` (was `Landscape`), so
> rotating the phone 180° flips the display and keeps the image upright while
> portrait stays disallowed. **§1.1 below is superseded** — see
> `orientation.md` for the change, the widescreen/immersive handling across a
> rotation, and device verification. Immersive fullscreen and the widescreen
> scale are otherwise unchanged.

**Author:** Boot/UX Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (`$ADB`), app `com.recomp.dishwasher`
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

---

## 0. TL;DR

| item | result |
|---|---|
| Landscape lock | **done** — `Activity1` `ScreenOrientation = Landscape`; merged manifest emits `android:screenOrientation="landscape"` |
| Rotation on device turn | **locked** — toggling `user_rotation` 0/1/2 keeps the app at 2400×1080 / `mCurrentOrientation=1` |
| Immersive fullscreen | **done** — status + nav bars hidden (API 30+ `WindowInsetsController`, legacy flags fallback) |
| Intro cut switch | **new** `Globals.SkipIntro = true` (`GameSource/projectDish/Globals.cs:28`) |
| Boot logo sequence | **not drawn** — no XBLA / Dream Play / Ska / release / ESRB carousel during load |
| `Intro` (gameMode 3) sequence | **never entered** |
| Per-level comic strip | **skipped** — `map.comicSwitch` cleared, `strip.playing` never set |
| Start state | **main menu** (title screen) |
| Time-to-interactive | before ≈ **23.2–24.1 s**; after ≈ **23.5–24.7 s** → **no significant change** (content decode dominates; see §5) |
| Crash / FATAL / ContentLoadException | **none**; audio initialises (6 categories, 4 sound banks, 216 cues) |
| Rendering/input fixes regressed | **none** (shaders/blend/input untouched) |
| Known limitation | the port renders at a fixed 1280×720 and does **not** scale to the 2400×1080 display → image sits in the top-left with black to the right/bottom. Pre-existing (see §7). |

---

## 1. Landscape lock

### 1.1 `src/Dishwasher/Activity1.cs:18`

```csharp
ScreenOrientation = ScreenOrientation.Landscape,   // was ScreenOrientation.FullUser
```

`ConfigurationChanges` is unchanged
(`Orientation | Keyboard | KeyboardHidden | ScreenSize`), so an orientation
config change (if one ever fired) would not recreate the activity.

### 1.2 `AndroidManifest.xml`

No change needed. The root manifest declares no `<activity>`; the activity (and
its `android:screenOrientation`) is generated from the `[Activity]` attribute.
The merged output confirms it:

```
obj/Debug/net8.0-android/android/AndroidManifest.xml:
  <activity android:name="crc641d1cdd92eb70a339.Activity1" ...
            android:screenOrientation="landscape" ...>
```

`targetSdkVersion="35"` is preserved.

### 1.3 Immersive fullscreen (optional, landed)

`Activity1.cs:49` calls `HideSystemBars()` **after** `SetContentView` (the decor
view must exist first — calling it before `SetContentView` throws
`NullPointerException` on `Window.InsetsController`). `Activity1.cs:61-102`:

* API ≥ 30: `Window.SetDecorFitsSystemWindows(false)` +
  `Window.InsetsController.Hide(WindowInsets.Type.SystemBars())` with
  `ShowTransientBarsBySwipe`.
* API < 30: legacy `SystemUiFlags.HideNavigation | Fullscreen | ImmersiveSticky |
  LayoutFullscreen | LayoutHideNavigation`.
* The whole method is wrapped in `try/catch` (logs `[immersive]`) so a failure
  can never crash the activity.
* Re-applied in `OnWindowFocusChanged` so the bars stay hidden after focus
  returns.

Result: screenshots show no status/nav bars. Input and rendering are unaffected
(menus were driven normally after this was enabled).

---

## 2. Cut the intro

### 2.1 The single switch

`src/Dishwasher/GameSource/projectDish/Globals.cs:20-28`

```csharp
// PORT BOOT CONFIG (Android port, 2026-10-02) — single revertable switch
//   true  => boot fast: skip the boot logo sequence (loadTrans) and the
//            comic/story intro, landing directly in the main menu.
//   false => original XNA behaviour ...
public const bool SkipIntro = true;
```

### 2.2 Sites touched in `GameSource/projectDish/Game1.cs`

| # | site | lines (current) | change |
|---|---|---:|---|
| 1 | `UpdateLoader` leave condition | `2401-2403` | `if (loaded && (Globals.SkipIntro \|\| loadFrame > 20.45f \|\| !Globals.isX360))` — leaves the loader as soon as the content thread is done, instead of also waiting for the ≥20.45 s logo window |
| 2 | `UpdateLoader` frame branch | `2437-2450` | when `SkipIntro`, hold the loader on its final frame (`loadFrame = 21.5`, `loadTrans[i] = 1`); otherwise original `loadTrans` ramp |
| 3 | enter `Intro` mode | `2422-2427` | `if (!Globals.SkipIntro) gameMode = 3;` then `gameMode = 1;` — never enters `GAME_MODE_INTRO` |
| 4 | `case 3:` (Intro update) | `1401-1410` | defensive: if mode 3 is ever reached, go straight to menu instead of `intro.Update` |
| 5 | comic strip trigger | `1951-1968` | `if (SkipIntro) map.comicSwitch = "";` else the original `strip.Read(); strip.Reset(); strip.playing = true;` |
| 6 | `DrawLoader` logo carousel | `3397-3450` | the XBLA / Dream Play / Ska / release / dish draws are guarded by `!Globals.SkipIntro`, so no logos are ever drawn during load |

Sites 1–3 and 5 are the behavioural cut; site 6 removes the visuals of the logo
sequence; site 4 is belt-and-braces.

### 2.3 What the player sees now

`am start` → ~2.7 s to first frame (black) → black + (once loaded) the
"loading" indicator while the content thread decodes ~128 textures → main
**menu/title screen**. No XBLA/Dream Play/Ska/release carousel, no comic strip.

`intro = new Intro()` (loader phase 20) is still constructed — it is cheap and
keeps `intro` non-null for the untouched `Draw`/`case 3` paths. It is simply
never updated.

### 2.4 Start-state choice — main menu

The main menu was chosen over dropping directly into the first level:

* The menu is the game's natural resting state (`mainMenu.inType = 1` was
  already set by the original transition code) and lets the user pick
  Story/Arcade/Options. It is fully initialised by the loader before the
  transition (`mainMenu` built in `Initialize`, `mainMenu.Init()/allTransIn()`
  already called).
* Dropping into a level would require duplicating the menu's level-select /
  difficulty bookkeeping and would risk entering `gameMode = 0` before
  `map.Read()` / `spriteMan.Init()` completed — exactly the inconsistent state
  the task warns about.
* The menu's own `inFrame` fade is only 2 s (`MainMenu.cs:587-590`,
  `inFrame += frameTime * 0.5f`) and is not a blocking/unskippable delay.

The first level is reached in the normal way (title → A → Single Player → A →
difficulty → A → Select Level → RIGHT to PLAY → A). With `SkipIntro=true` the
selected level starts directly (a 0.9 s blackout) — **no comic strip**.

---

## 3. Measured timing (this device)

Method: `adb shell am start -W` for the first frame, plus logcat
`ActivityTaskManager: START` / `Displayed` and the boot marker
`[boot] content loaded; entering main menu`. Device clock is ~2 s ahead of the
host, so all times below are computed **relative to the `START` line**, not to
host wall-clock.

| run | first frame (`TotalTime`) | menu/title (`START`→boot) |
|---|---:|---:|
| **BEFORE** (original build, `FullUser`, logos + comic) | 2311–2319 ms | **23.2–24.1 s** (screenshot bound: loader at +23.16 s, menu at +24.14 s) |
| **AFTER** (landscape + `SkipIntro`) run 1 | 2713 ms | **24.69 s** |
| **AFTER** run 2 | 2854 ms | **23.7–24.4 s** (phase 25 at +23.48 s) |

**Conclusion: there is no meaningful time-to-interactive improvement.** The
cut removes the logo animation and the comic, but the wait is the content
loader, not the logo gate.

### 3.1 Why — measured loader phase breakdown

Per-phase logcat timestamps (temporary boot log; the loader thread runs
independently of the display). Start of each phase:

| phase | content | duration |
|---:|---|---:|
| 1 | `gfx/text`, `skastudios`, `dbp`, `dish`, `release` | 1.29 s |
| 3 | `map.ReadSegs()` + `bubbles`, `controls`, `howtoplay`, `dishbuy`, `achievement` | 2.21 s |
| 7 | `sprites`, `jtext`, `arial` | 1.61 s |
| 10 | `heart`, `pickups`, `eckto`, `items`, `comictext` | 1.07 s |
| 11 | `LoadChar(ecktom)` | 1.24 s |
| 16 | `gfx/maps/maps1..6` | **5.99 s** |
| 17 | 11 heads + 15 torsos (+ `maintext.ReadArcadeStrings`) | 2.22 s |
| 18 | 12 legs + 16 weapons | 3.17 s |
| 23 | `LoadChar(ecktog)` | 0.50 s |
| | **phase 0 → phase 25 total** | **20.23 s** |

The original `loadFrame > 20.45f` gate is therefore coincident with the loader
finishing at ~20.2 s: before, the menu appeared at `max(20.45, load)` after the
loader thread started; after, at `load`. On this device both are ≈ the same, so
removing the gate saves ≤0.3 s. The ~20 s is PNG decode + GPU upload of ~128
textures (`DishwasherContentManager.LoadPng` → `Texture2D.FromStream`);
`Sound.Initialize` itself is <0.2 s.

A future, larger win (out of scope for this task and **not** implemented
because it risks the inconsistent state the brief prohibits) would be to make
the menu ready after the menu-essential phases (0–10) + audio, then load the
char/map textures in the background while the menu is interactive, gating
gameplay entry on `loaded`.

---

## 4. Verification log

* **Landscape at launch:** display `logicalFrame=Rect(0,0-2400,1080)`,
  `mCurrentOrientation=1`; merged manifest `android:screenOrientation="landscape"`.
* **No rotation:** `accelerometer_rotation` off, `user_rotation` = 0, 1, 2 —
  the app stayed 2400×1080 / `mCurrentOrientation=1` in all three
  (`after-rotation-lock-ur0.png`, `after-rotation-lock-ur2.png`). Restored
  `accelerometer_rotation=1` afterwards.
* **No crash:** `grep -iE 'FATAL|ContentLoadException|DirectoryNotFound|Unhandled'`
  over logcat = empty across the launch, menu, level-select and gameplay runs.
* **Audio:** `[audio] AudioEngine created: 6 categories`, `216 cues`,
  4 `SoundBank ready` lines.
* **Input:** `adb shell input keyevent 96` (A) and `22` (RIGHT) drive
  title → main menu → Single Player → difficulty → Select Level → PLAY →
  gameplay.
* **Comic skipped:** after selecting PLAY, `after-select-1s.png` is the level
  blackout (not a comic panel) and `gameplay2.png` is the tutorial room with
  the character — the comic strip never plays.

### 4.1 Screenshots (`android/notes/boot-config/`)

| file | what |
|---|---|
| `before-portrait-logo-xbla.png`, `before-portrait-logo-dish.png` | **before**: logo carousel (XBLA, Dream Play/Ska/release) in the portrait band |
| `before-portrait-menu.png` | **before**: portrait main menu after the ~24 s wait |
| `after-landscape-first-frame.png` | **after**: first content frame — black, no logos |
| `after-landscape-loading.png` | **after**: mid-load — black/loading, no logos |
| `after-landscape-title.png` | **after**: title screen reached (landscape) |
| `after-landscape-difficulty.png` | **after**: Story Difficulty menu |
| `after-landscape-gameplay-no-comic.png` | **after**: tutorial gameplay reached with **no comic** |
| `after-rotation-lock-ur0.png`, `after-rotation-lock-ur2.png` | rotation test at `user_rotation` 0 and 2 — both landscape |

---

## 5. Known limitation — fixed-resolution image does not fill the display

On the 2400×1080 display the game image occupies roughly the left 1280 px and
the top ~720 px, with black to the right and bottom. This is **pre-existing**,
not caused by the landscape lock or immersive change:

* The game always draws into a fixed 1280×720 coordinate space
  (`Globals.screenSize = 1280×720`, `graphics.PreferredBackBufferWidth/Height =
  1280/720`, `getDRect()` in `Game1.cs:5390`).
* MonoGame's `GraphicsDeviceManager.ResetClientBounds()`
  (`MonoGame.Framework.Android 3.8.5.1`) letterboxes the viewport to the
  preferred 16:9 aspect inside the display (here 1920×1080, offset 240) — but
  the game still draws at 1280×720 inside that viewport, so it does not fill
  even the letterbox.
* In the old portrait `FullUser` capture this was hidden because the viewport
  was only 1080 px wide, so the 1280-px-wide image filled the width.

Fixing it means scaling the 1280×720 backbuffer to the display (change
`Globals.screenSize`/viewport and re-audit every hard-coded UI offset) — a
rendering change outside this task's scope and a risk to the protected
`MatrixTransform` / `fade` / DXT5 / straight-alpha / lense fixes. Left as-is
and documented.

---

## 6. How to revert

**Landscape:**
* `Activity1.cs:18` → `ScreenOrientation = ScreenOrientation.FullUser`.
* Immersive: delete the `HideSystemBars();` call at `Activity1.cs:49`, the
  `OnWindowFocusChanged` override (`Activity1.cs:95-102`) and the
  `HideSystemBars()` method (`Activity1.cs:61-93`). (Or leave immersive; it is
  independent of the orientation lock.)

**Intro:**
* Set `Globals.SkipIntro = false` (`Globals.cs:28`). All six sites above then
  take their original branches:
  * `UpdateLoader` waits for `loadFrame > 20.45f`;
  * `loadTrans` ramps through the logo phases again;
  * `gameMode = 3` is entered and `intro.Update` runs;
  * `map.comicSwitch` triggers `strip.playing = true`;
  * the `DrawLoader` logo carousel is drawn again.
* No other code change is needed — the original statements are intact in the
  `else` branches / behind the `!SkipIntro` guards.

**Full revert of this task:** set `SkipIntro = false`, restore `FullUser`,
remove the immersive block, rebuild.

---

## 7. Files changed this task

* `GameSource/projectDish/Globals.cs` — `SkipIntro` switch (1 line + comment).
* `GameSource/projectDish/Game1.cs` — 6 guarded sites (loader transition,
  loader frame branch, Intro entry, `case 3`, comic trigger, `DrawLoader`
  logo draws) + one one-time `[boot]` log line at menu entry.
* `Activity1.cs` — `ScreenOrientation.Landscape`; immersive `HideSystemBars()`
  + `OnWindowFocusChanged`.
* `AndroidManifest.xml` — unchanged (`targetSdkVersion` preserved).
* **new** this note + `android/notes/boot-config/*.png`.
* Shaders, blend map, `AndroidInputBridge`, `RenderDiagnostics`,
  `InputDiagnostics`, `DishwasherContentManager`: **untouched**.

## 8. Build / deploy used

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true        # Build succeeded, 0 errors, 5 warnings
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher && adb -s <device-serial> logcat -c
adb -s <device-serial> shell am start -W -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
# drive: input keyevent 96 (A) / 22 (RIGHT)
```
