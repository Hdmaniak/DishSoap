# Direct tap-to-activate for menu rows — The Dishwasher MonoGame/Android port

**Author:** Touch Menu Specialist (subagent)
**Date:** 2026-10-02
**Device (for the later plan):** `<device-serial>` (Samsung A52), app `com.recomp.dishwasher`
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope of edits:** `src/Dishwasher/` and `android/notes/` only.
**Device interaction:** **none.** No adb/device command was run for this change. The plan in §8 is for a later, supervised session.

---

## 1. What changed (behaviour)

Previously touch was: *swipe to move a cursor, tap to press A at the cursor*.
That was cumbersome: the user has to find the row with swipes first.

Now:

| Gesture | Effect |
|---|---|
| **Tap directly on a menu row** | **The cursor jumps to that row and A is pressed** — i.e. exactly as if the user had moved the cursor there and hit A. A tapped **BACK** row goes back naturally. |
| Tap on empty space (a menu screen that *does* draw rows) | **Nothing** (no wild A press). |
| Tap on a screen that draws *no* rows (title / intro / message box / device-failed) | Legacy `A` confirm (those screens have no rows to hit). |
| Swipe up/down/left/right | D-pad up/down/left/right — kept as the secondary cursor-movement mechanism. |
| Two-finger tap | `B` (cancel). |
| Android BACK key/gesture | `B` (cancel), menus only. |

The **gameplay gate is unchanged**: `TouchControls.MenuContext()` still makes
`OnTouch`/`HandleBack` inert while `Game1.gameMode == 0 && !Globals.paused`, so
nothing here can add or change in-game controls.

## 2. Design

### 2.1 Where row rectangles are captured (draw time)

`MainMenu.drawOption(...)` (the innermost overload,
`GameSource/projectDish/MainMenu.cs:5802`) is the single funnel every menu row
draws through. Just after it has computed the row's final layout variables
(`num4`/`num3`/`num18`/`optionOffset`, *after* the `num11 <= 0` fade-out early
return and *after* the `num4 += num10/5` cursor-proximity nudge) it records:

```csharp
// MainMenu.cs:6013  (// PORT (touch))
int _rowX = num4 + i * num6;
int _rowY = i * num5 + 300 + (int)num18 + num3 + (int)optionOffset;
int _rowW = Math.Max(aStringSpace + 80, 170);
Dishwasher.MenuTouchTargets.Add(i, _rowX - 40, _rowY - 5, _rowW, 46);
```

* `i` is the **same index** the menu uses for `selOption`, so a hit maps
  straight onto the existing selection logic.
* `aStringSpace` is `text.GetAStringSpace(s)` computed a few lines above at the
  row's draw size (1.1); the `-40 / +80` padding includes the star cursor on the
  left and finger slop on the right. Height 46 (< the 48-px row pitch) keeps
  adjacent row bands from overlapping.
* The list is **per-frame**: `Dishwasher.MenuTouchTargets.Begin()` clears it at
  the top of `MainMenu.DrawOptionButtons` (`MainMenu.cs:5063`), *before* that
  method's message-box / device-failed early returns, so covered screens record
  **zero** rows and therefore cannot be mis-tapped underneath.
* This covers every `drawOption` menu including the port's own **ANDROID
  SETTINGS** screen and the Options screen (`AndroidSettingsMenu.cs` just calls
  `drawOption`), plus the scrolling SELECT LEVEL / ARCADE / SHOP lists.

### 2.2 Coordinate spaces (the non-obvious part)

There are **three** spaces, and the row rect and the tap point are not in the
same one by default:

1. **Physical window pixels** — what `Activity.DispatchTouchEvent` receives.
   The window spans the full 2400×1080 panel (immersive + cutout). Measured in
   the previous work: `raw = logical × 1.5`.
2. **Logical backbuffer pixels** — the game's `Globals.screenSize`
   (e.g. 1600×720). `Activity1.OnCreate` measures the SurfaceView at this
   logical size and applies `WidescreenConfig.Scale` (~1.5×) to fill the panel.
   `TouchControls.Logical(v)` converts physical → logical by dividing by
   `Scale`. **Thresholds and hit-tests are in this space.**
3. **Menu canvas pixels** — `drawOption` lays rows out in the game's *original*
   menu canvas (1024×600 when `Globals.wideScreen`, else 800×600). `Game1.Draw`
   case 1 renders the menu into `rTarg` at those canvas coordinates and then
   blits `src=(0,0,1024,600)` → `dest=(0,0,screenSize.X,screenSize.Y)`
   (`Game1.cs:3794-3853`). This is the pre-existing menu stretch documented in
   `widescreen.md` §7 (≈1.5625× horizontally vs 1.2× vertically at 1600×720).

`MenuTouchTargets.HitTest` maps the tap point **back into menu-canvas space**
through the *exact* blit rectangles the renderer used, so no separate
assumption about stretch vs letterbox is baked into the hit-test:

```csharp
// Game1.Draw case 1, just before the final menu blit:
Dishwasher.MenuTouchTargets.SetCanvasMapping(value, destinationRectangle);
// value = (0,0,1024,600)  destination = (0,0,screenSize.X,screenSize.Y)
...
// MenuTouchTargets.HitTest, inverse map logical -> canvas:
float cx = src.X + (logicalX - dst.X) * src.Width  / dst.Width;
float cy = src.Y + (logicalY - dst.Y) * src.Height / dst.Height;
// then compare (cx,cy) against the recorded canvas rects.
```

#### Derivation of the physical mapping (verified against the run's frames)

The run's A52 is panel **2400×1080**, `WidescreenConfig.Scale = 1.5`,
`screenSize = 1600×720`, `Globals.wideScreen = true`.

* The SurfaceView is measured at the logical `1600×720` and scaled by exactly
  1.5 with pivot `(0,0)` (window frame `[0,0]–[2400,1080]`, no letterbox), so
  `physical = logical × 1.5`.
* `Game1.Draw` case 1 blits the menu canvas `src=(0,0,1024,600)` to
  `dst=(0,0,1600,720)` — i.e. it is **stretched**, not centered/letterboxed.
  There is no `(0,60)` half-letterbox and no `Globals.border` inset (the
  border-derived dest is overwritten to `(0,0,screenSize)` at
  `Game1.cs:3848-3851`).
* Therefore `logical = canvas × (1600/1024, 720/600) = canvas × (1.5625, 1.2)`
  and `physical = canvas × (2.34375, 1.8)`.

This was cross-checked against the supervised run's own screenshots
(`/tmp/opencode/v2.png` main menu, `v5.png` Android Settings): the measured
text-row centres match `canvas × 1.8` to within ~3 physical px (see the table in
§8). In particular the main menu `HELP & OPTIONS` row (canvas `y=392`) sits at
physical `y≈735`, and `ANDROID SETTINGS` (canvas `440`) at `y≈822` — i.e. a tap
at `(500,755)` is genuinely *on* HELP & OPTIONS and `(500,858)` is genuinely *on*
ANDROID SETTINGS. The earlier "~100 px above" reading was an off-by-one-row
misidentification of the drawn text, not a mapping error: the physical band of
each row is `[(rowY-5)·1.8, (rowY+41)·1.8]`, 82.8 px tall on a 86.4 px pitch.

The colour/alpha paths are untouched; this is geometry only.

### 2.3 Hit-testing and activation path

Threading: `Begin/Add/HitTest/TakePendingRow` all run on the **game thread**
(`MainMenu.Draw` records, `TouchControls.Tick` tests, `MainMenu.Update`
consumes). The only cross-thread hand-off is the tap point, guarded by a lock.

1. `TouchControls.OnTouch` recognises a tap (press+release < 45 logical px,
   < 400 ms) on the **UI thread** and parks the logical DOWN point via
   `QueueTap(...)`.
2. `TouchControls.Tick` (game thread, `Game1.cs:1078`) runs
   `ProcessPendingTap()`:
   * if `Game1.gameMode == 1` (MainMenu is the active/updated screen) and a row
     is hit → `MenuTouchTargets.SetPendingRow(index)`;
   * if `gameMode == 1` and no row is hit but rows were recorded → **do
     nothing** (stray taps do not misfire);
   * otherwise (no rows at all on a main-menu screen, or `gameMode != 1`, e.g.
     a paused gameplay screen whose pause menu is not drawn through
     `drawOption`) → pulse `A` (legacy confirm).
3. `MainMenu.Update` (`MainMenu.cs:1146`, inside the existing per-character
   loop, right after `Character character = charArray[m];`) consumes one pending
   row and does:

   ```csharp
   int _touchRow = Dishwasher.MenuTouchTargets.TakePendingRow();
   if (_touchRow >= 0)
   {
       selOption = _touchRow;
       if (level == ANDROID_SETTINGS_LEVEL)          // PORT (touch)
       {
           Sound.playCue("sword1");
           AndroidSettingsActivate();                // custom screen: direct
       }
       else
       {
           character.keyJump = true;                 // game's own menus
       }
   }
   ```

   For the game's own menus `character.keyJump = true` is the **same edge** the
   gamepad path produces; the existing `if (character.keyJump)` switch later in
   the same iteration (`MainMenu.cs:1673`) then runs unchanged. For the custom
   Android Settings screen the activation is called directly with the same
   semantics (see §3.1). So BACK rows, disabled rows, toggles, transitions,
   sounds, `hitButton` blobs and `levelSelOption[level] = selOption` all behave
   exactly as a controller A press at that row.

### 2.4 Scrolling lists

Because rects are captured in `drawOption` *after* the layout is final, a
scrolling list (`raised: true`, used by SELECT LEVEL / ARCADE / LEADERBOARDS /
server list) gets the real per-frame `selScroll`/`num18`/`num3` offsets that are
actually on screen. Rows faded out (`num11 <= 0`) return before the recording
line, so off-screen rows are **not** tappable — the user swipes to bring a row
into view, then taps it. Nothing is hard-coded: no per-screen layout table.

### 2.5 No-hit behaviour (chosen)

* Menu screen with rows: a tap on empty space is **ignored**. This is the
  explicit requirement not to turn the whole screen into an A press.
* Screen with no rows: tap = A (title "press start", comic/intro, message boxes,
  device-failed). This preserves the previous tap behaviour exactly where there
  is nothing to hit, and avoids stranding the user on those screens.

## 3. Files / sites changed

**New**

* `src/Dishwasher/Platform/MenuTouchTargets.cs` — row list, the blit-derived
  canvas↔screen mapping (`SetCanvasMapping`/`HitTest`) and the pending-row
  hand-off (`Begin`/`Add`/`SetPendingRow`/`TakePendingRow`).

**Edited (all marked `// PORT (touch)`)**

* `src/Dishwasher/GameSource/projectDish/MainMenu.cs`
  * `:5063` — `MenuTouchTargets.Begin()` at the top of `DrawOptionButtons`.
  * `:6013-6023` — record the drawn row rect in the innermost `drawOption`.
  * `:1143-1170` (`TakePendingRow()` at `:1146`) — consume a hit row in
    `MainMenu.Update`, set `selOption`. For the **Android Settings** screen
    (`level == ANDROID_SETTINGS_LEVEL`) it now calls `Sound.playCue("sword1")` +
    `AndroidSettingsActivate()` directly (and does **not** raise `keyJump`);
    every other row menu keeps raising `character.keyJump` (see §3.1).
* `src/Dishwasher/Platform/TouchControls.cs`
  * `:11-22` — gesture-map comment updated.
  * `:73-80` — tap-point fields + lock.
  * `:131` — `ProcessPendingTap()` call at the top of `Tick`.
  * `:155-191` — `ProcessPendingTap()` (hit-test / no-hit rules); the
    `tap -> select row` log now also prints the logical and physical points.
  * `:194-201` — `QueueTap()` (UI thread → game thread hand-off).
  * `:286-291` — tap branch now calls `QueueTap(...)` instead of
    `Pulse(Buttons.A)`.
* `src/Dishwasher/GameSource/projectDish/Game1.cs`
  * `:3852-3855` — `MenuTouchTargets.SetCanvasMapping(value, destinationRectangle)`
    immediately before the menu blit in `Draw` case 1, so the hit-test uses the
    renderer's exact canvas→screen transform.

No other file was touched. In particular `Activity1.cs`, `AndroidInputBridge.cs`,
`WidescreenConfig.cs`, the frame-limiter/perf hooks and `AndroidSettings.cs` are
unchanged.

### 3.1 Bug 2 — why the custom screen now activates (and the game's menus are unchanged)

The tap is consumed in the per-character loop of `MainMenu.Update` (same place
for every screen). For the game's own `case`-based menus the row's activation
lives in the `if (character.keyJump)` switch, so raising that edge selects **and**
activates. That switch sits behind `if (!Guide.IsVisible)` and the menu's
transition guards; the custom Android Settings screen (`level == 42`) is the one
screen whose activation is routed through `AndroidSettingsActivate()` inside that
switch. A directly-injected `character.keyJump` could therefore land on a frame
where the switch section is not reached, leaving the cursor moved but the toggle
unfired.

The touch path now handles `level == ANDROID_SETTINGS_LEVEL` explicitly:
it sets `selOption` and calls `AndroidSettingsActivate()` immediately (the same
call a controller A press reaches through the switch), then does **not** raise
`keyJump`, so case 42 cannot double-fire. `AndroidSettingsActivate()` already
implements exactly the controller-A semantics — cycle FPS LOCK
(`Unlimited→30→60→120`, `FrameLimiter.Apply()`), toggle SHOW FPS COUNTER, or
`hitButton`+`Save`+transition for BACK. Because the controller path is untouched,
a physical A press still activates via the switch; because the game's own menus
still take the `character.keyJump` branch, their behaviour is byte-for-byte
unchanged.

## 4. Build result

```
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
```

**Build succeeded. 0 Error(s).** Only pre-existing, unrelated warnings:
`SYSLIB0006` (`Thread.Abort` obsolete), `CA1422` (`Activity.OnBackPressed`
obsolete — untouched `Activity1.cs`), `XA1008` (API34 vs targetSdk35).

APK: `src/Dishwasher/bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`.

## 5. Regression guard (unchanged by this change)

Physical controller (`AndroidInputBridge.RouteKey/RouteMotion`), `MatrixTransform`
effect param, `fade`, DXT5 alpha, straight-alpha/`BlendState.NonPremultiplied`,
lense sampler fix, landscape/immersive, widescreen, restored intro, autosave, the
Android Settings tree (FPS LOCK / SHOW FPS COUNTER) and the frame limiter are all
untouched. Both diagnostic harnesses stay `Enabled=false`. Swipe navigation and
BACK→B are retained.

## 6. Edge cases / notes

* **Message box up:** `Begin()` runs before `DrawOptionButtons` returns for the
  message box, so no rows are recorded and a tap dismisses the box with A
  (legacy).
* **Level transitions:** the recorded rows always belong to the last drawn
  frame, and the hit row is computed on the game thread the frame before the
  existing `keyJump` switch. A row index that is out of range for the (new)
  screen simply falls through the existing `switch`/clamps — no crash.
* **Paused gameplay:** `gameMode == 0`, so row hit-testing is skipped and tap =
  A, matching the previous build (the pause overlay is not drawn through
  `drawOption`).
* **Menu horizontal stretch:** row rects are stored in canvas space and scaled
  in `HitTest`, so they remain correct if the panel aspect (and therefore
  `LogicalWidth`/`canvas` scale) changes.

## 7. Revert

Delete `Platform/MenuTouchTargets.cs`; remove the `// PORT (touch)` edits in
`MainMenu.cs` (`:5063`, `:6013-6023`, `:1143-1170`), in `Game1.cs`
(`:3852-3855`), and in `TouchControls.cs`; in `TouchControls.cs` remove
`QueueTap`/`ProcessPendingTap` and the `_tap*` fields and restore
`Pulse(Buttons.A)` in the tap branch. Swipe/BACK/gate code is otherwise unchanged.

## 8. On-device verification plan (DO NOT RUN unattended — for a later, supervised session)

No device command has been run for this task. When a supervised verification is
approved, tap the **physical panel** points below (`input tap X Y`). The model is
`physical = canvas × (2.34375, 1.8)`, i.e. `[(rowY−5)·1.8, (rowY+41)·1.8]` for
the band and `(rowY+18)·1.8` for its centre. `x` is chosen well inside each
row's padded rect (and on the text).

### Main menu — physical tap points (non-trial; the run's v2.png state)

| # | row (index) | canvas `rowY` | band (physical y) | **tap (X, Y)** |
|---|---|---|---|---|
| 1 | SINGLE PLAYER GAME (0) | 176 | 308–391 | **(450, 349)** |
| 2 | MULTIPLAYER GAME (1) | 224 | 394–477 | **(450, 436)** |
| 3 | ACHIEVEMENTS (2) | 296 | 524–607 | **(450, 565)** |
| 4 | LEADERBOARDS (3) | 344 | 610–693 | **(450, 652)** |
| 5 | HELP & OPTIONS (4) | 392 | 697–779 | **(450, 738)** |
| 6 | ANDROID SETTINGS (5) | 440 | 783–866 | **(450, 824)** |
| 7 | RETURN TO GAME LIBRARY (6) | 488 | 869–952 | **(450, 911)** |

(If `Globals.trial` were true, an `UNLOCK FULL GAME` row appears at index 2 and
rows 2–7 shift down one index; the run's screenshot shows the non-trial layout.)

### ANDROID SETTINGS screen — physical tap points (level 42)

| # | row (index) | canvas `rowY` | band (physical y) | **tap (X, Y)** |
|---|---|---|---|---|
| 1 | FPS LOCK (0) | 110 | 189–272 | **(700, 230)** |
| 2 | SHOW FPS COUNTER (1) | 158 | 275–358 | **(700, 317)** |
| 3 | BACK (2) | 206 | 362–445 | **(700, 403)** |

These predicted centres match the run's own screenshots: main-menu measured
`MULTI 433, ACH 563, LEAD 650, HELP 735, ANDROID 822, RETURN 910` (predicted
`436, 565, 652, 738, 824, 911`); settings measured `SHOW 317, BACK 405`
(predicted `317, 403`).

Plan:

1. Install and launch (as in `boot-config.md`/`widescreen.md`):
   `adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk`,
   then `am force-stop` + `logcat -c` + `am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1`.
2. `adb logcat -s MonoGame:D` and confirm `[touch] menu controls initialised`.
3. On the main menu tap **HELP & OPTIONS** at `(450,738)` → the log must read
   `[touch] tap -> select row 4 (logical 300,492 physical 450,738)` and the HELP
   screen opens in one tap. A tap at `(450,824)` must log row 5 and open
   ANDROID SETTINGS; `(450,911)` must log row 6. Taps on rows 1–3 must log
   rows 0–2.
4. On ANDROID SETTINGS: tap `(700,230)` → log row 0 and FPS LOCK cycles
   Unlimited→30→60→120; tap `(700,317)` → log row 1 and SHOW FPS COUNTER
   toggles off/on (perf overlay appears/disappears); tap `(700,403)` → log row 2
   and the screen returns to the main menu. Each activation must happen on the
   **first** tap.
5. Enter SINGLE PLAYER GAME → difficulty → SELECT LEVEL. Swipe down once, then
   tap a lower level that the scroll exposed; confirm the level starts.
6. Tap empty space between rows and confirm **nothing happens** (no
   `tap -> select row` log, cursor does not move, no screen change).
7. Confirm swipes still move the cursor exactly one row per swipe (secondary
   mechanism) and two-finger tap / Android BACK still cancel.
8. Confirm a physical controller still navigates with no interference, and that
   touch does nothing during active gameplay.
9. Toggle `TouchControls.Verbose = true` (edit + rebuild) for the raw
   `DOWN raw=… logical=…` lines; the `tap -> select row N` line (now with its
   logical and physical point) is emitted at `Info` level regardless.

## 9. Honest status

Code + build are complete (0 errors). **On-device verification is pending** —
this change has not been exercised on the phone, by design (no device
interaction; no `adb` command was run). The geometry is now taken directly from
the renderer's own canvas→screen blit (`Game1.Draw` case 1 →
`MenuTouchTargets.SetCanvasMapping`), and the predicted points in §8 were
cross-checked against the supervised run's captured frames (`/tmp/opencode/v2`
and `v5`), which match the model to ~3 physical px. Bug 2 (custom-screen
activation) is fixed by activating the Android Settings row directly on the tap
path while leaving the controller/`keyJump` path untouched; the plan above is the
first thing to run when a supervised device session is approved.
