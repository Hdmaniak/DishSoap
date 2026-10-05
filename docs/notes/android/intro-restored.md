# Intro restored — boot logo carousel covers the content load

**Author:** Boot/UX Specialist (subagent, follow-up)
**Date:** 2026-10-02
**Device:** `<device-serial>` (`$ADB`), app `com.recomp.dishwasher`
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope:** edits confined to `src/Dishwasher/` and the notes trees.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

---

## 0. TL;DR

| item | result |
|---|---|
| `Globals.SkipIntro` final value | **`false`** (`GameSource/projectDish/Globals.cs:28`) |
| Boot logo carousel | **restored** — XBLA → Dream Build Play → Ska → ESRB/dish drawn during load |
| `gameMode` 3 `Intro` sequence | **same as original** — original decompiled code sets `gameMode = 3; gameMode = 1;` back-to-back, so it never actually plays (see §4) |
| Per-level comic strip (`strip.playing`) | **restored** — plays on level start (screenshots `comic-05`, `comic-06`) |
| Launch → first frame | **2.31 s** (`am start -W TotalTime`, COLD) |
| Launch → first logo (XBLA visible) | **≈ 2.9–3.0 s** (screencap `logo-01-xbla.png`) |
| Launch → main menu/title | **≈ 23.3 s** (`[boot] content loaded; entering main menu`) |
| Content decode window | `+2.64 s` (`Load Content complete`) → `+23.26 s` = **≈ 20.6 s**, fully covered by the carousel |
| Widescreen fill | **unchanged** — menu content bbox `(4,5)-(2396,1076)` = **2392/2400 px (99.7 %) × 1071/1080 (99.2 %)** |
| `FATAL` / `ContentLoadException` | **none** |
| Audio | initialises: 6 categories, 216 cues, 4 sound banks (`SoundBank ready`) |
| Protected fixes | untouched (`MatrixTransform`, `fade`, DXT5 alpha, straight-alpha/`NonPremultiplied`, `AndroidInputBridge`, lense sampler, landscape lock, immersive fullscreen, widescreen) |
| Diagnostic harnesses | `RenderDiagnostics.Enabled = false`, `InputDiagnostics.Enabled = false` |

---

## 1. The change

Single line edit:

```csharp
// GameSource/projectDish/Globals.cs:28
public const bool SkipIntro = false;   // was true
```

No other code change. `Game1.cs` mtime is unchanged (12:57); only `Globals.cs`
was touched (13:11).

### 1.1 Six sites — verified to fall through to original branches

| # | site | `Game1.cs` line | with `SkipIntro=false` |
|---|---|---:|---|
| 1 | `UpdateLoader` leave condition | 2438 | `if (loaded && (loadFrame > 20.45f || !Globals.isX360))` — waits for the 20.45 s logo window again |
| 2 | `UpdateLoader` `loadTrans` frame branch | 2472-2481 | falls to `else if (loadTrans[loadDrawPhase] < 1f) loadTrans[...] += frameTime * 2f` (original ramp) |
| 3 | enter `Intro` mode | 2457-2462 | `gameMode = 3; gameMode = 1;` — **identical to decompiled original** (see §4) |
| 4 | `case 3:` Intro update | 1435-1448 | `intro.Update(character[0]); if (intro.allDone) gameMode = 1;` (original) |
| 5 | comic-strip trigger | 1986-2003 | `map.comicTransFrame = 0; strip.path = comicSwitch; strip.Read(); strip.Reset(); strip.playing = true;` (original) |
| 6 | `DrawLoader` logo carousel | 3432-3488 | all `!Globals.SkipIntro` guards pass → XBLA / Dream Build Play / Ska / release / dish draws run |

All six `// PORT: …SkipIntro…` comments mark only these sites; there are no other
non-flag-guarded edits from the cut agent (grep `// PORT` in `Game1.cs` = 6 hits,
all at the sites above).

---

## 2. Device verification

Sequence (`source tools/scripts/env.sh`; `adb -s <device-serial>`):

```sh
adb install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb shell am force-stop com.recomp.dishwasher
adb logcat -c
adb shell am start -W -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
```

### 2.1 Measured timings (run 2, `run2-amstart.txt` + `run2-logcat.txt`)

| event | clock (logcat) | offset from `START` |
|---|---|---:|
| `ActivityTaskManager: START` | 13:13:27.700 | 0.00 s |
| `Displayed … +2s312ms` (first frame) | 13:13:30.001 | **+2.30 s** |
| `[trace] Load Content complete` | 13:13:30.342 | +2.64 s |
| `[audio] SoundBank ready` ×4 / AudioEngine | 13:13:50.196–50.352 | +22.50 s |
| `[trace] [boot] content loaded; entering main menu` | 13:13:50.964 | **+23.26 s** |

`am start -W TotalTime = 2312 ms`.

Screenshot bound (first run, independent):
`frame-03s` (XBLA) … `frame-22s` (ESRB) then menu from `frame-23s`.
So the **carousel is on screen from ≈ +2.9 s to +23.3 s (≈ 20.4 s)**, i.e. it
covers the whole ≈ 20.6 s content-decode window (from `Load Content complete`
to the menu). The intro is visible **during** the load, not after it.

### 2.2 No black screen

`logo-01-xbla.png` (t ≈ +3.0 s) shows the XBOX LIVE arcade logo; the previous
`SkipIntro=true` build's first frames were pure black (`after-landscape-first-frame.png`).

---

## 3. Proof screenshots (`<dev-notes>/proof/boot/`)

| file | what |
|---|---|
| `logo-01-xbla.png` | XBOX LIVE arcade logo (t ≈ +3.0 s) |
| `logo-02-dreambuildplay.png` | DREAM BUILD PLAY / XNA Game Studio (t ≈ +8.0 s) |
| `logo-03-ska.png` | ska studios video games (t ≈ +15.0 s) |
| `logo-04-esrb.png` | "ONLINE INTERACTIONS NOT RATED BY THE ESRB" / dish (t ≈ +20.0 s) |
| `logo-05-title-menu.png` | title/menu "The Dishwasher Dead Samurai — PRESS A TO START!" (t ≈ +48 s, status ≈ +23.3 s) |
| `frame-03s.png` … `frame-36s.png` | full 1 s-interval sequence |
| `comic-05-comic1.png`, `comic-06-later.png` | per-level comic strip playing ("HOW DID I END UP BACK HERE?" …) |
| `comic-00-menu.png` … `comic-04-play.png` | title → Single Player → Story Difficulty → Select Level → PLAY |
| `run2-amstart.txt`, `run2-logcat.txt` | raw `am start -W` output and logcat |
| `menu.png` | title screen used for the widescreen bbox check |

---

## 4. Note on the `gameMode` 3 `Intro` ("comic intro")

`Game1.cs:2457-2462` with `SkipIntro=false` compiles to:

```csharp
gameMode = 3;
gameMode = 1;
```

This is **byte-for-byte the original decompiled logic**
(`managed/decompiled/game/projectDish/Game1.cs:2395-2396` also has
`gameMode = 3;` immediately followed by `gameMode = 1;`). `UpdateLoader()` is
called from the `gameMode == 4` branch and returns before the game-mode switch,
so the `3` never survives; the `Intro` sequence class (`Intro.cs`, the
"ska software presents / a james e silva game / the dishwasher / dead samurai"
comic panels) is dead code in this XNA build. That is consistent with the
pre-cut timings in `boot-config.md` (menu at ~23 s, not ~67 s).

**Therefore:** the load is covered by the **logo carousel** (sites 1, 2, 6), and
the **per-level comic** (site 5) plays when a level starts. Reverting the flag
restores both; it does **not** resurrect the `gameMode` 3 sequence because the
original never entered it either. Restoring that sequence would require a
non-original change (dropping the trailing `gameMode = 1;`) and would add its
~44 s runtime before the menu — deliberately **not** done, to stay faithful to
the original and to the "revert the skip" brief.

---

## 5. Widescreen — no regression

`Platform/WidescreenConfig.cs` (mtime 12:56) and its two marked blocks were not
touched. Logcat at boot:

```
[trace] [ws] screenSize=1600x720 backbuffer=1600x720 viewport=1600x720 displayMode=1600x720 client=1600x720
[ws] panel=2400x1080 logical=1600x720 scale=1.5 view=0x0
```

Screencap bounding-box check (`menu.png`, 2400×1080, threshold >10):

```
content bbox = (4,5)-(2396,1076)  ->  w=2392 / 2400 = 99.7 %   h=1071 / 1080 = 99.2 %
```

The thin 4–5 px black edge is the game's own `Globals.border`; the image fills
the panel.

---

## 6. Clean build / final state

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true        # Build succeeded, 0 errors, 5 warnings
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher
adb -s <device-serial> shell am start -W -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
# drive: input keyevent 96 (A), 22 (RIGHT)
```

Regressions: **none observed.** No `FATAL`, no `ContentLoadException`, audio
initialises, landscape/immersive fullscreen intact, widescreen fills.

Files changed this follow-up: **`GameSource/projectDish/Globals.cs` (1 line)**,
plus this note, the `boot-config.md` update banner, and the screenshots.
