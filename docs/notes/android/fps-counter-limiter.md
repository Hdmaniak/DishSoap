# FPS counter + working FPS LOCK (The Dishwasher, MonoGame/Android)

**Author:** Performance/Telemetry Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (SM-A525F / A52, panel 2400×1080 @ 60 Hz, logical backbuffer 1600×720)
**App:** `com.recomp.dishwasher` (`crc641d1cdd92eb70a339.Activity1`)
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope:** edits confined to `src/Dishwasher/`, `<dev-notes>/proof/perf/` and this
notes tree. Read-only trees (`managed/decompiled`, `assets-clean`, `port`,
`rexglue-sdk*`, `android-app`) were **not** modified.

**One line:** the Android **SHOW FPS COUNTER** setting is added (default **off**, persisted in
`android_settings.sav`, old saves load as off), draws a real sliding-window frame rate using
the game's own comic font, and **FPS LOCK now genuinely caps the presented frame rate** —
measured on-device **30 → 29–31**, **60 → 59–60**, **120 → 60**, **Unlimited → 61** — while
`playSeconds` advances at the same wall-clock rate at 30 as at 60 (capped, not slow-motion).

---

## 0. TL;DR

| item | result |
|---|---|
| new setting | `showFps` bool in `AndroidSettings.Data`, default **false**, own file `android_settings.sav` |
| new row | `SHOW FPS COUNTER: ON/OFF` (row 1), between `FPS LOCK` and `BACK`; A toggles; wrap/nav fixed |
| counter | `Platform/PerfOverlay.cs`; ~0.75 s sliding window of presented frames (monotonic clock); drawn bottom-left with `Text.drawCText(string,…)` (the HUD's comic font) |
| counter visibility | main menu + all in-game screens (gameplay, pause, shop, comic strip). **Not** on the loader / fatal-error screens (see §3.3) |
| limiter | `Platform/FrameLimiter.cs` → `IsFixedTimeStep=true; TargetElapsedTime=1/fps` for 30/60/120, `IsFixedTimeStep=false` for Unlimited |
| applied | startup (`Game1.Initialize`) **and immediately** on menu change (`AndroidSettingsActivate`) |
| game-speed assumption | game delta **is** loop-derived; MonoGame default was already fixed 60 Hz — no hard-coded 1/60 constant, so retargeting preserves speed |
| measured 30 | **FPS 30/31** (settings screen), **FPS 29/30** (gameplay) |
| measured 60 | **FPS 60** (settings), **FPS 59/60** (gameplay / comic) |
| measured 120 | **FPS 60** (vsync/panel cap) |
| measured Unlimited | **FPS 61** (vsync/panel cap) |
| game speed @60 | ΔplaySeconds **66** over Δwall **68.4 s** (0.965) |
| game speed @30 | ΔplaySeconds **93** over Δwall **96.0 s** (0.969) — *same rate*, not half |
| old-save compat | XML test + **on-device**: old file (`version`+`fpsLock`, **no** `<showFps>`) loaded as `FPS LOCK: 30`, `SHOW FPS COUNTER: OFF` |
| build | `Build succeeded. 0 Error(s)` (5 pre-existing warnings); APK 181,435,960 B, md5 `d15ad1a49faf9e530af0161085a7448c` |
| device saves | backed up, then **restored exactly** (profile/settings md5 match; `android_settings.sav` removed as before) |
| non-regression | `BlendState.AlphaBlend` = 0; `RenderDiagnostics.Enabled=false`, `InputDiagnostics.Enabled=false`; only 5 source files touched |

---

## 1. Delta-time / game-loop investigation (file:line)

### 1.1 Where the game gets its frame delta

| what | where | note |
|---|---|---|
| `Globals.frameTime` declared | `GameSource/projectDish/Globals.cs:1276` | `public static float frameTime = 0f;` |
| set from the game loop (main path) | `GameSource/projectDish/Game1.cs:1339` | `Globals.frameTime = (float)gameTime.ElapsedGameTime.TotalSeconds;` |
| set from the loop (loader path) | `GameSource/projectDish/Game1.cs:1130` | same line in `UpdateLoader` branch |
| upper clamp | `Game1.cs:1342-1344` | `if (frameTime > 0.05f) frameTime = 0.05f;` (guards tab-out stalls) |
| consumed by gameplay | ~200 sites, e.g. `CSprite.cs`, `Character.cs`, `Map.cs`, `HUD.cs` | all scale by `Globals.frameTime` |
| `playSeconds` accumulation | `Game1.cs:1346-1351` | `playTime += frameTime; if (>1) { -=1; playSeconds++; }` |

**Conclusion:** the game is fully **delta-time driven** from `gameTime.ElapsedGameTime`.
There is **no hard-coded `1/60`, `0.0166`, `16.666`, or `1f/60f` physics/animation step**
anywhere in `GameSource/` (grep found only `Globals.frameTime` uses). The only fixed
constants are the `0.05` frame clamp and effect-local values.

### 1.2 Frame-pacing settings and their defaults

| setting | where | value |
|---|---|---|
| `graphics.SynchronizeWithVerticalRetrace` | `Game1.cs:375` | `false` (set by the game; untouched by this task) |
| `IsFixedTimeStep` | MonoGame default | `true` (`MonoGame.Framework` `Game.cs:315`) |
| `TargetElapsedTime` | MonoGame default | `166667` ticks = **1/60 s** (`Game.cs:317`) |
| `MaxElapsedTime` | MonoGame default | 500 ms (`Game.cs:321`) |

So before this task the game already ran **fixed 60 Hz**: each `Update` received
`ElapsedGameTime == TargetElapsedTime` (`Game.cs:796-806`), i.e. the game's timing is
*consistent with* changing the target. This is exactly why retargeting works.

### 1.3 How MonoGame's Android loop is paced

* The Android game view's worker thread iterates continuously:
  `MonoGameAndroidGameView.Run()` calls `Run(0.0)` → `updates = 1000/0 = +∞`
  (decompiled `AndroidGameView.cs:433,446`), so there is **no pacing in the view**.
* Each iteration raises `UpdateFrame`, which calls `_game.Tick()`
  (`AndroidGameWindow.cs:138`), then `RenderFrame` (`MakeCurrent`).
* `Game.Tick()` (`Game.cs:767-852`) is where pacing happens:
  * fixed step: it `Thread.Sleep(1)`s until `_accumulatedElapsedTime >= TargetElapsedTime`
    (`Game.cs:783-790`), then runs one or more `DoUpdate` with `ElapsedGameTime = TargetElapsedTime`
    (`Game.cs:796-806`) and exactly one `DoDraw` (`Game.cs:838`).
  * variable step: it runs one `DoUpdate` + one `DoDraw` per iteration with the real elapsed
    time (`Game.cs:825-831`).
* `DoDraw → EndDraw → Platform.Present() → GameView.SwapBuffers()`
  (decompiled `Game.cs:870-872`, `AndroidGamePlatform.cs:150-161`). Android EGL's default
  **swap interval is 1 (vsync)**, so `SwapBuffers` blocks to the panel refresh.

**What "Unlimited" can and cannot mean:** with `IsFixedTimeStep=false` there is no artificial
cap, but presentation is still vsync-bound by the EGL swap. On this **60 Hz** A52 panel,
"Unlimited" therefore presents at ~60 fps, not >60. Likewise **120 is not reachable** on a
60 Hz panel: `TargetElapsedTime=1/120` makes the fixed loop run two logic updates per
presented frame (`ElapsedGameTime` split correctly), so game speed is right but presentation
stays ~60. On a 120 Hz device the same code would present at 120.

---

## 2. FPS LOCK — the limiter

### 2.1 New file `Platform/FrameLimiter.cs`

```csharp
public static void Attach(Game game) { _game = game; _applied = -1; Apply(); }

public static void Apply()
{
    int state = AndroidSettings.FpsLock;      // 0 Unlimited, 1 30, 2 60, 3 120
    switch (state)
    {
        case AndroidSettings.FPS_30:  SetFixed(30);  break;
        case AndroidSettings.FPS_60:  SetFixed(60);  break;
        case AndroidSettings.FPS_120: SetFixed(120); break;
        default: _game.IsFixedTimeStep = false; break;   // Unlimited: variable step
    }
}

private static void SetFixed(int fps)
{
    _game.TargetElapsedTime = TimeSpan.FromSeconds(1.0 / fps); // set target first…
    _game.IsFixedTimeStep = true;                              // …then flip the mode
}
```

Because the game's delta is `gameTime.ElapsedGameTime` (§1.1), a fixed 30 Hz loop feeds the
game `1/30 s` per update, so the world advances at the correct rate and only the *presentation
frequency* drops. No game-file change was needed to protect game speed.

### 2.2 Application points (game-file hooks, marked `// PORT`)

| hook | where | so that |
|---|---|---|
| `Dishwasher.FrameLimiter.Attach(this);` | `Game1.cs:403` (end of `Initialize`, after `graphics` setup) | the loaded `fpsLock` is applied at startup |
| `Dishwasher.FrameLimiter.Apply();` | `Platform/AndroidSettingsMenu.cs` `AndroidSettingsActivate`, FPS row | changing the value applies **immediately** |
| `AndroidSettings.Load()` | `Activity1.cs:56` (already present) | the value is read before the loop starts |

Log lines (logcat tag `Dishwasher`), observed on device:

```
[fpslimit] fixed 30 fps (TargetElapsedTime=33.333ms)
[fpslimit] fixed 60 fps (TargetElapsedTime=16.667ms)
[fpslimit] fixed 120 fps (TargetElapsedTime=8.333ms)
[fpslimit] unlimited (IsFixedTimeStep=false, target=8.33ms)
```

---

## 3. SHOW FPS COUNTER — setting, row, counter

### 3.1 Storage (`Platform/AndroidSettings.cs`)

`AndroidSettings.Data` gained `public bool showFps;`. API: `ShowFps` (getter, triggers load),
`SetShowFps(bool)` (sets + saves). The file is the same `android_settings.sav`
(app-private `…/files/Documents/TheDishwasher/`, XML via `XmlSerializer`).

### 3.2 Menu row (`Platform/AndroidSettingsMenu.cs`)

Rows are now `FPS LOCK` (0), `SHOW FPS COUNTER` (1), `BACK` (2); `ANDROID_ROW_COUNT = 3`.
The existing draw-time clamp `if (selOption > COUNT-1) selOption = 0; if (< 0) selOption = COUNT-1;`
provides correct up/down wrap for all three rows, so all are reachable and wrap
(`FPS LOCK → SHOW FPS COUNTER → BACK → FPS LOCK`). Labels are lazy `StringContainer`s
(`"show fps counter: off"/"on"`), matching the game's own programmatic labels; the renderer
upper-cases them. A toggles and persists on change.

### 3.3 Counter (`Platform/PerfOverlay.cs`) and where it draws

* `PerfOverlay.Frame(...)` is called once per **presented** frame from the common tail of
  `Game1.Draw` (`Game1.cs:4422`), after `base.Draw`. Each call stores a monotonic
  `Stopwatch` timestamp; the value shown is `(framesInWindow-1) / span` over a **0.75 s**
  trailing window (a real measured presented-frames-per-second, post-limiter and post-vsync).
* It draws with the game's own text path `Text.drawCText(int x,int y,string,
  SpriteBatch,Texture2D comicTex,bool)` — the same comic font the HUD exp/score readouts use
  — at the **bottom-left** (`x = border.X+12`, `y = screenSize.Y-border.Y-44`), with a 2 px
  drop shadow. The HUD occupies top-left (health) and top-right (score/combo); bottom-right
  holds buttons. Verified by screenshot that the counter clears the health bar and score
  (`13/13c`, `17`).
* **Visibility:** drawn from the shared Draw tail, so it appears on the **main menu**,
  **gameplay**, **pause/shop**, and the in-level **comic strip**. The loader
  (`case 4 → DrawLoader; return`) and the fatal-error screen (`DrawFailDie; return`) return
  before that tail, so the counter is intentionally **not** drawn there.

Hook: `Dishwasher.PerfOverlay.Frame(sprite, text, comicTex);` at `Game1.cs:4422`
(marked `// PORT (fps overlay)`).

---

## 4. Old-save compatibility

`XmlSerializer` leaves a **missing** `bool` element at its default (`false`), ignores unknown
elements, and the new field is written going forward.

Standalone test (`notes/proof/perf/xmltest/`, exact new struct) — output:

```
[Shape A: old file, no <showFps>]  fpsLock=1 showFps=False
  PASS fpsLock preserved (1)
  PASS missing showFps loads as default false
[Shape B: new file, <showFps>true</showFps>]  fpsLock=2 showFps=True
  PASS fpsLock preserved (2)
  PASS showFps loads true
[Shape C: bare file, version only]
  PASS fpsLock defaults to 0 (Unlimited)
  PASS showFps defaults to false
[Round-trip: write then read]
  PASS writer emits <showFps>
  PASS round-trip fpsLock==1
  PASS round-trip showFps==true
ALL PASS
```

**On-device:** before first launch a seeded file with **no `<showFps>`** (`version`+`fpsLock=1`)
was placed at `android_settings.sav`. The settings screen showed **`FPS LOCK: 30`** and
**`SHOW FPS COUNTER: OFF`** (`03-android-settings-old-save.png`), and the limiter log read
`[fpslimit] fixed 30 fps`. After toggling the counter on and changing the lock, the file was
rewritten with `<showFps>true</showFps>` and survived a cold restart (`14-…`).

---

## 5. Measured FPS on device (screenshots in `<dev-notes>/proof/perf/`)

| FPS LOCK | screen | screenshot | counter reading |
|---|---|---|---|
| **30** | Android Settings (`SHOW FPS COUNTER: ON`) | `04-show-fps-on-fpslock30.png` | **FPS 31** |
| **30** | Android Settings (after the timing run) | `08-fpslock30-after-timing.png` | **FPS 31** |
| **30** | gameplay (tutorial, HUD live) | `13-gameplay-fpslock30.png` / `13c-…` | **FPS 29 / 30** |
| **60** | Android Settings | `05-fpslock60-counter.png`, `15-…` | **FPS 60** |
| **60** | in-level comic strip | `16-gameplay-fpslock60.png` | **FPS 60** |
| **60** | gameplay (tutorial, HUD live) | `17-gameplay-fpslock60.png` | **FPS 59** |
| **120** | Android Settings | `06-fpslock120-counter.png` | **FPS 60** (panel cap) |
| **Unlimited** | Android Settings | `07-fpslock-unlimited-counter.png` | **FPS 61** (panel cap) |
| **30** | main menu after cold restart | `14-cold-restart-mainmenu-fps30.png` | FPS 27 during the fade (settles ~30) |
| **60** | main menu, counter OFF | `19-main-menu-counter-off.png` | *no counter* (OFF works) |
| **60** | Android Settings, counter OFF | `18-show-fps-off-settings.png` | `SHOW FPS COUNTER: OFF` |

The counter is a real measurement, not a report of the setting: 120 reads **60** and
Unlimited reads **61**, because both are vsync-bound to the 60 Hz panel — exactly the
independent behaviour expected if the number comes from presented frames.

**Limitation:** 120 fps is not reachable on this 60 Hz panel (and "Unlimited" is likewise
capped at ~60 by EGL vsync). On a 120 Hz device the same code would present 120.

---

## 6. Game speed is unaffected (capped, not slowed)

Measured with the game's own `playSeconds` counter (incremented in `Game1.Update` from
`Globals.frameTime`, `Game1.cs:1346-1351`), persisted by the existing autosave on
`OnPause/OnStop`, whose logcat line carries the device timestamp:

| FPS LOCK | interval (autosave timestamps) | Δwall | ΔplaySeconds | rate (Δplay/Δwall) |
|---|---|---|---|---|
| **60** | 20:55:14.146 → 20:56:22.531 | 68.385 s | **66** | **0.965** |
| **30** | 20:56:47.884 → 20:58:23.852 | 96.968 s | **93** | **0.969** |

Both settings advance `playSeconds` at essentially **1.0 per wall-clock second** (the ~3 %
shortfall is the activity-resume latency before the game loop restarts, present in both
runs). At 30 fps, if the limiter were slowing the game down, ΔplaySeconds would have been
about **47**, not 93. Therefore **30 fps is choppy at correct speed — capped, not slowed.**
This matches the code path: fixed step sets `ElapsedGameTime = TargetElapsedTime`, so each
update advances `1/30 s` and the world covers the same ground per second.

Logcat evidence:

```
10-02 20:55:14.146  I Dishwasher: [autosave] saved on OnPause playSeconds=1596
10-02 20:56:22.531  I Dishwasher: [autosave] saved on OnPause playSeconds=1662   # 60 fps: +66
10-02 20:56:47.884  I Dishwasher: [autosave] saved on OnStop  playSeconds=1684
10-02 20:58:23.852  I Dishwasher: [autosave] saved on OnPause playSeconds=1777   # 30 fps: +93
```

---

## 7. Build / device

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
# Build succeeded. 0 Error(s)  (5 warnings: 4× SYSLIB0006 Thread.Abort, 1× XA1008)
```

* APK: `bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`,
  **181,435,960 bytes**, md5 `0e08d56ee5c77eb6a3b8303f12a3a489` (final build, 21:05).
* `AndroidManifest.xml:3` `targetSdkVersion="35"` unchanged.
* Installed `-r -d`; driven entirely with `adb shell input keyevent` (A=96, B=97,
  DPAD up=19/down=20/right=22); ~23 s to interactive after a cold boot.
* No `FATAL EXCEPTION` / `AndroidRuntime` for the app during the session.
* Final smoke test with defaults (no `android_settings.sav`): log
  `[fpslimit] unlimited (IsFixedTimeStep=false, …)`, title/menu renders, counter
  correctly absent (`20-final-smoke-default.png`).
* Device saves **backed up before** testing to `/tmp/opencode/fps-device-backup/` and
  **restored afterwards exactly**:

  | file | backup md5 | restored device md5 |
  |---|---|---|
  | `profile.sav` | `00ae2cad2b410b7baf4104dd72071c01` | `00ae2cad2b410b7baf4104dd72071c01` |
  | `settings.sav` | `fa33d5210aab72bfe9e53fe18157b356` | `fa33d5210aab72bfe9e53fe18157b356` |

  The test-created `android_settings.sav` was removed (it did not exist before this task).

---

## 8. Non-regression

* Only these source files changed:
  * `Platform/AndroidSettings.cs` (add `showFps` + accessors)
  * `Platform/AndroidSettingsMenu.cs` (new row, count, apply limiter)
  * **new** `Platform/FrameLimiter.cs`
  * **new** `Platform/PerfOverlay.cs`
  * `GameSource/projectDish/Game1.cs` (2 marked hooks: `FrameLimiter.Attach` at `:403`,
    `PerfOverlay.Frame` at `:4422`)
* `grep -rl BlendState.AlphaBlend GameSource/` = **0** (straight-alpha /
  `BlendState.NonPremultiplied` sites untouched; the counter itself uses `NonPremultiplied`).
* `RenderDiagnostics.Enabled == false` and `InputDiagnostics.Enabled == false`.
* No change to `MatrixTransform`, `fade`, DXT5 alpha, `AndroidInputBridge`, the lense
  sampler fix, landscape/immersive, widescreen (`WidescreenConfig.cs`), the restored intro,
  autosave, or the pristine original OPTIONS screen (`Settings.cs`/`MainText.cs` untouched).
* `SynchronizeWithVerticalRetrace` (`Game1.cs:375`) was **not** modified.

---

## 9. Limitations

1. **Unlimited is still ~60 on this device.** There is no artificial cap, but MonoGame's
   Android `Present()` uses an EGL swap whose default interval is 1 (vsync), so presentation
   is bound to the 60 Hz panel. "Unlimited" therefore means "as fast as vsync allows".
2. **120 is not reachable on a 60 Hz panel.** `TargetElapsedTime=1/120` is applied faithfully
   (two logic updates per presented frame, correct game speed), but only ~60 frames/s are
   shown.
3. The counter is **not drawn** on the loader / fatal-error screens because those paths
   return before the shared `Game1.Draw` tail (they are transient/system screens).
4. The measured integer can jitter by ±1 frame/s (29–31 at 30; 59–61 at 60) because it is a
   genuine sliding-window average, not a fixed report.

---

## 10. Revert instructions

1. Delete `Platform/FrameLimiter.cs` and `Platform/PerfOverlay.cs`.
2. In `GameSource/projectDish/Game1.cs` remove the two marked blocks
   (`PORT FRAME LIMITER` at `:401-405` and `PORT (fps overlay)` at `:4419-4423`).
3. Revert `Platform/AndroidSettingsMenu.cs` to the two-row screen and remove the
   `Dishwasher.FrameLimiter.Apply()` call; remove `showFps` from
   `Platform/AndroidSettings.cs` (or leave it — it is backward/forward compatible).
4. Optionally delete `files/Documents/TheDishwasher/android_settings.sav` on device.
5. Rebuild / reinstall. Nothing else is affected.

## 11. Evidence index (`<dev-notes>/proof/perf/`)

| File | What |
|---|---|
| `01-main-menu-old-save.png` | cold boot against the seeded old-format save (no `<showFps>`) |
| `03-android-settings-old-save.png` | old save loaded: `FPS LOCK: 30`, `SHOW FPS COUNTER: OFF` |
| `04-show-fps-on-fpslock30.png`, `08-…` | 30 → counter **FPS 31** |
| `05-fpslock60-counter.png`, `15-…` | 60 → counter **FPS 60** |
| `06-fpslock120-counter.png` | 120 → counter **FPS 60** (panel cap) |
| `07-fpslock-unlimited-counter.png` | Unlimited → counter **FPS 61** (panel cap) |
| `13-gameplay-fpslock30.png`, `13c-…` | gameplay 30 → **FPS 29 / 30**, clear of HUD |
| `16-gameplay-fpslock60.png`, `17-…` | in-level 60 → **FPS 60 / 59**, clear of HUD |
| `14-cold-restart-mainmenu-fps30.png` | persistence: 30 + counter on after `am force-stop` |
| `18-show-fps-off-settings.png` | `SHOW FPS COUNTER: OFF` + no counter |
| `19-main-menu-counter-off.png` | counter absent on the main menu |
| `20-final-smoke-default.png` | final build, default settings: title renders, no counter |
| `xmltest/` | old-save-compatibility XmlSerializer test (`ALL PASS`) |
