# Widescreen rendering — fill the 2400×1080 panel (The Dishwasher, MonoGame/Android)

**Author:** Widescreen Rendering Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (Samsung A52, landscape, panel 2400×1080, display cutout 88 px on the left edge)
**App:** `com.recomp.dishwasher` (`crc641d1cdd92eb70a339.Activity1`)
**Build:** `net8.0-android`, `targetSdkVersion=35`, `EmbedAssembliesIntoApk=true`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

---

## 0. TL;DR

| item | result |
|---|---|
| Fill | **full 2400×1080** — SurfaceFlinger layer `pos=(0,0)`, buffer `1600×720`, transform `tr=[1.50,0.00][0.00,1.50]` ⇒ displayed 2400×1080 |
| Stretch | **none in the world/HUD** — uniform 1.5× (same scale X and Y). Buffer aspect 1600/720 = 2.2222 = panel 2400/1080 |
| Horizontal world | **+25 %** — camera width `screenSize.X` 1280 → **1600** world units |
| Vertical world | **unchanged** — camera height stays **720** world units, so character scale / physics feel identical |
| HUD | anchored to `screenSize` corners; fully on-screen (health top-left, combo/score top-right, buttons bottom-right) |
| FPS | **59.3 fps** (127 frames / 2.13 s, median frame 16.6 ms) at the new resolution |
| Crashes | none — no `FATAL` / `ContentLoadException` in logcat |
| Protected fixes | all intact (shaders/`MatrixTransform`, `fade`, DXT5, straight-alpha blend, `AndroidInputBridge`, lense sampler fix) |
| Residual | the *menu* still uses its original 1024×600 canvas stretched to `screenSize` (see §7) |

---

## 1. The view map (file:line)

Everything the game draws is expressed in **one** coordinate space,
`Globals.screenSize`, and world pixels map **1:1** to that space (a world unit is
a pixel; there is no camera scale other than `zoom`, which is only the special
move/comic zoom of the final composite quad).

### 1.1 Screen / backbuffer

| what | where | value |
|---|---|---|
| `Globals.screenSize` declared | `GameSource/projectDish/Globals.cs:1438` | `Vector2` |
| `Globals.wideScreen` declared | `Globals.cs:1452` | `bool` |
| resolution chosen (old) | `Game1.cs:321,339` (16:9 → 1280×720), `:326,332,343,351-354` | old lookups |
| **widescreen override** | `Game1.cs:358-372` | `screenSize = (WidescreenConfig.LogicalWidth, 720)` |
| preferred backbuffer | `Game1.cs:373-374` | `= screenSize` |
| `ApplyChanges()` (→ `ResetClientBounds`) | `Game1.cs:377` | |
| **backbuffer/viewport pin** | `Game1.cs:378-397` | `PresentationParameters.BackBuffer*` + `GraphicsDevice.Viewport = (0,0,W,H)` |
| `screenScale` (menu/UI factor) | `Game1.cs:398-399` | `screenSize/800`,`/600` |
| `dishRectangle` | `Game1.cs:403-409` | from `screenSize` |
| `getDRect()` / `getSRect()` (final-composite dest/source) | `Game1.cs:5388-5447`, `GameRender.cs:11-50` | return `screenSize`-sized rects |

MonoGame's Android `GraphicsDeviceManager.ResetClientBounds()`
(`Microsoft.Xna.Framework/GraphicsDeviceManager.cs:378`) letterboxes the
backbuffer to the preferred aspect using `GraphicsDevice.DisplayMode`
(= the SurfaceView's measured size). When the view is measured at the logical
size, its aspect equals the preferred aspect and it sets
`Viewport = (0,0,W,H)` with no letterbox.

### 1.2 World camera / orthographic projection

There is **no projection matrix** for the world: sprites are drawn at
`world - Globals.gScroll` (1:1). The "camera" is the scroll:

| what | where | note |
|---|---|---|
| `Globals.gScroll` declared | `Globals.cs:1442` | world→screen offset |
| player-centred target | `Game1.cs:4871-4872` | `vector.X -= screenSize.X/2`; `vector.Y -= screenSize.Y/2 + 100` |
| horizontal clamp | `Game1.cs:4879-4882` | uses `screenSize.X` and `map.xSize*128` |
| vertical clamp | `Game1.cs:4883-4887` | uses `screenSize.Y` |
| smoothed scroll → `gScroll` | `Game1.cs:4901-4973` | |
| `GameRender.gZoom` | `Game1.cs:4870` | written but the world is not zoomed by it |
| effect vertex transform (the real "projection") | `Platform/Shim_EffectCompat.cs:42` + `Content/fx/*.fx` `MatrixTransform` | `CreateOrthographicOffCenter(0, vp.W, vp.H, 0, 0, -1)` |

**So the visible world width is exactly `Globals.screenSize.X`** — widening
`screenSize.X` shows more level horizontally; keeping `screenSize.Y = 720`
keeps the vertical framing identical.

### 1.3 Render targets (all derived from `screenSize` unless noted)

| target | where | size |
|---|---|---|
| `rTarg[2]`, `lenseTarg`, `refractTarg` | `Game1.cs:913-917` (LoadContent), `:613-614` (loader) | `screenSize` → 1600×720 |
| `mapTarg`, `camSplatTarg`, `bloodTarg` | `Game1.cs:918`, `:912`, `:612` | `screenSize/2` → 800×360 |
| `bloomTarg`, `fadeTarg` | `Game1.cs:615,617` | fixed 128² (effect-local, screen-independent) |
| `waterTarg` | `Game1.cs:616` | fixed 256² |
| `greenMapTarg` | `Game1.cs:919` | fixed 720² (map pre-pass) |

The full-screen post effects (`grad`, `lense`, `ink`, `bloom`, `fade`, `water`,
`redhaze`, `camsplat`, …) sample with **normalised** UVs or with effect-local
buffers, or take screen-normalised centres (`Game1.cs:4251-4253` divide blast
coords by `screenSize`), so they follow the new size automatically. No
1280×720 constant is baked into them.

### 1.4 HUD / UI anchoring

Confirmed: the HUD lays itself out from `Globals.screenSize` corners.

| element | where |
|---|---|
| health bar / combo meter | `HUD.cs:343,346` `screenSize - border - (35,100)` |
| skulls (top-right) | `HUD.cs:470` `screenSize.X - border.X - 62` |
| score / money (right) | `HUD.cs:477,489,496,506` `screenSize.X - border.X - w - 30` |
| shop text centred | `HUD.cs:523,573` `screenSize.X/2`, `screenSize.Y/2` |
| pickups centred | `Pickups.cs:149-171` |
| overlay border clamp | `Overlay.cs:188-205` |
| menu buttons | `MainMenu.cs` (fixed 800/1024×600 canvas — see §7) |

Because `screenSize` is the logical 1600×720 and the whole backbuffer is scaled
uniformly, the HUD stays on the logical corners and therefore on the physical
corners.

---

## 2. Approach chosen, and why

**Render the entire game at a logical `1600×720` backbuffer and let the Android
compositor scale the SurfaceView uniformly by 1.5× to the physical 2400×1080
panel.**

* Width `1600 = 720 × (2400/1080)`; logical aspect `1600/720 = 2400/1080`.
* Vertical world units stay **720** (character screen-fraction, physics and
  level framing unchanged). Horizontal world units go **1280 → 1600 (+25 %)**.
* Uniform scale ⇒ **no stretch** (square stays square); exact fit ⇒ **no bars,
  no crop** of the level.
* The game's own code is untouched: the backbuffer is 1600×720, the viewport is
  1600×720, and every render target / HUD anchor / post-effect still sees a
  consistent `screenSize`. Only the *presentation* is scaled, by the display
  hardware.

### Alternatives considered

* **Native 2400×1080 backbuffer** (`screenSize = panel size`). Simplest and
  pixel-sharp, but it changes the vertical framing (1080 world units instead of
  720) so characters occupy a smaller screen fraction and level framing changes;
  also 2.8× the fill cost of the old 1280×720 on an Adreno 618. Rejected as the
  primary choice.
* **Logical 1920×864** (the brief's suggestion). Correct aspect, but 1.66 Mpx
  (1.8× the old cost) and still changes vertical framing (864 vs 720). The
  chosen 1600×720 gives the same *aspect* correction with the same vertical
  framing and only 1.25× the old fill.
* **A global SpriteBatch scale transform.** Would require touching all 159
  `sprite.Begin` sites plus the effect `MatrixTransform` hook and would be
  render-target-dependent. Rejected: the SurfaceView scale does the same job in
  the compositor with **zero** game-draw changes and zero risk to the protected
  shader/blend fixes.

---

## 3. Exact changes

### 3.1 new file — `src/Dishwasher/Platform/WidescreenConfig.cs`

Static handshake between the Activity and the game:
`Enabled`, `LogicalWidth` (1600), `LogicalHeight` (720), `Scale` (1.5).
Additive / revertable.

### 3.2 `src/Dishwasher/Activity1.cs`

* `using Android.Widget;` (added).
* **Cutout:** `Activity1.cs:31-46` — API 28+ sets
  `Window.Attributes.LayoutInDisplayCutoutMode = ShortEdges` so the window
  covers the full 2400 px (verified: window frame went `[88,0]-[2400,1080]` →
  `[0,0]-[2400,1080]`).
* **Logical size + scale:** `Activity1.cs:62-119`.
  * Real panel size from `WindowManager.CurrentWindowMetrics.Bounds`
    (API 30+), else `DefaultDisplay.GetRealSize` (NB:
    `Resources.DisplayMetrics` reports the *app* width 2168, not the panel, so it
    is only a fallback).
  * `wsScale = physH / 720 = 1.5`; `logicalW = round(physW / wsScale) = 1600`
    (forced even for the half-size render targets).
  * Wrap the MonoGame `SurfaceView` in a black `FrameLayout`; measure the view at
    `logicalW × 720`; pivot `(0,0)`; `ScaleX = ScaleY = wsScale`; then
    `SetContentView(root)`. Because the view is *measured* at the logical size,
    MonoGame's `DisplayMode`, GL surface, viewport and all render targets are
    logical; the view transform does the panel scaling.
* One-shot `[ws] panel=… logical=… scale=…` log line.

### 3.3 `src/Dishwasher/GameSource/projectDish/Game1.cs`

* `Game1.cs:358-372` — when `WidescreenConfig.Enabled`, override
  `Globals.screenSize = (LogicalWidth, 720)`, `wideScreen = true`,
  `border = 0`.
* `Game1.cs:378-397` — after `ApplyChanges()`, pin
  `PresentationParameters.BackBufferWidth/Height` and `GraphicsDevice.Viewport`
  to the logical size (defends against `ResetClientBounds` seeing a stale
  pre-layout view size), and log the resolved state.

**Nothing else changed.** No `.fx`, no `GameRender.cs`, no `HUD.cs`, no blend
site, no content manager.

---

## 4. Runtime evidence (device)

Startup logcat:

```
Dishwasher: [ws] panel=2400x1080 logical=1600x720 scale=1.5 view=0x0
Dishwasher: [trace] [ws] screenSize=1600x720 backbuffer=1600x720
            viewport=1600x720 displayMode=1600x720 client=1600x720
```

Window / SurfaceFlinger (the definitive fill proof):

```
Frames: frame=[0,0][2400,1080]                       # window spans the whole panel
Layer (SurfaceView[...]@0(BLAST)): pos=(0,0)
    activeBuffer=[720x1600:...,RGBA_8888]            # EGL/GL surface = 1600x720
    tr=[1.50, 0.00][0.00, 1.50]                      # uniform 1.5x, no skew
    geomLayerBounds / display = 2400x1080
```

`1600×1.5 = 2400`, `720×1.5 = 1080`, and the transform is diagonal-equal ⇒
**the panel is filled exactly with a single uniform scale (no distortion).**

---

## 5. Verification & measurements

### 5.1 Fill

| frame | screenshot | non-black bbox | notes |
|---|---|---|---|
| BEFORE (old build) | `widescreen/baseline-current.png` | `(88,0,1367,718)` → 1280×719, margins L88 R1032 B361 | fixed 1280×720 box in the top-left |
| **AFTER shop UI** | **`widescreen/ws-final-shop.png`** | **`(0,0,2399,1079)` → `fills_frame=True`, fill=1.0000** | **content reaches every edge** |
| AFTER menu | `widescreen/ws-2-menu.png` | `(4,4,2395,1075)` margins L4 T4 R4 B4 | fills the panel; the 4 px is the game's own dark edge/vignette (the old build also had black game edges: `baseline-current.png` `x=88,y=360 = (0,0,0)`) |
| AFTER gameplay | `widescreen/ws-play-1.png`, `ws-play-2.png`, `ws-nav-1.png` | content reaches the full panel edges (`T0 B1079`, `R0` in `ws-nav-1.png`) | dark room art at the other edges |
| AFTER select-level | `widescreen/ws-tut-4.png` | `(4,4,2395,1075)` | |

The strongest fill evidence is, in order: (a) `ws-final-shop.png` measured
`bbox=(0,0,2399,1079)` = **the whole frame**; and (b) the compositor geometry in
§4 — the game surface is a 1600×720 buffer at `pos=(0,0)` scaled by exactly 1.5
⇒ 2400×1080. The occasional dark edge pixels on some frames are game art, not a
letterbox (the pre-change build had black game-art edges too).

### 5.2 No distortion (aspect)

* SurfaceFlinger transform `tr=[1.50,0.00][0.00,1.50]` — X and Y scale equal.
* Buffer aspect `1600/720 = 2.2222` = panel aspect `2400/1080 = 2.2222`.
* Measured HUD health-bar capsule (fixed-size sprite, full health):

| frame | red-fill bbox | w | h | w/h |
|---|---|---:|---:|---:|
| BEFORE `baseline-current.png` | (162,54,387,107) | 226 | 54 | **4.185** |
| AFTER `ws-play-1.png` | (112,82,448,160) | 337 | 79 | **4.266** |

  Width ×1.49, height ×1.46 — i.e. **one uniform ~1.5×**, aspect held to
  within ≈2 % (anti-aliased edge threshold). No horizontal-only stretch.

### 5.3 HUD anchored / fully visible

`ws-play-1.png` / `ws-play-2.png`: health bar top-left, `COMBO! n HITS!` + score
top-right, both fully on-screen with a margin. `ws-tut-4.png`: `ACCEPT`/`BACK`
buttons bottom-right. The HUD is laid out from `screenSize` and scaled with the
panel, so it tracks the physical corners.

### 5.4 More horizontal world

The camera width is `Globals.screenSize.X`: **1280 → 1600 (+25 %)**, vertical
unchanged at 720. Visible in the frames: the old 1280 px image showed only part
of the diner room; the new 1600-unit view shows the left staircase **and** the
right-hand posters/wall in the same shot (`widescreen/ws-play-1.png`,
`ws-nav-1.png`). `widescreen/ws-before-after.png` stacks BEFORE / AFTER-gameplay
/ AFTER-menu.

### 5.5 Performance

`dumpsys SurfaceFlinger --latency <game layer>` (cleared, then sampled) over
2.13 s: **127 frames, 59.3 fps**, median frame **16.6 ms**, p95 16.7 ms, max
33.3 ms. The game's logical fill is 1600×720 = 1.15 Mpx = **1.25× the old
1280×720** (0.92 Mpx); the 1.5× upscale is a compositor (GPU) transform, not a
per-pixel game cost. This is *less* work than the brief's suggested 1920×864
(1.66 Mpx). No resolution compromise was needed on this device.

### 5.6 No crashes

```
grep -iE 'FATAL|AndroidRuntime|ContentLoadException|Unhandled|DirectoryNotFound' logcat  →  empty
Dishwasher: [trace] [boot] content loaded; entering main menu
```

### 5.7 Protected systems — regressed? **No**

* `Content/fx/*.fx` mtimes all predate this session; `MatrixTransform` present in
  19/19 gameplay effects.
* `Globals.SkipIntro == true` (`Globals.cs:28`) untouched.
* `RenderDiagnostics.Enabled == false`, `InputDiagnostics.Enabled == false`.
* `Platform/AndroidInputBridge.cs` untouched; `GameRender.drawLenseTarg` lense
  bind fix untouched.
* `grep -r BlendState.AlphaBlend GameSource/` = **0** (straight-alpha map intact).
* Only 3 source files changed today: `Activity1.cs`, `Game1.cs`,
  `Platform/WidescreenConfig.cs` (plus this note / screenshots).

---

## 6. Build / deploy

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true        # Build succeeded, 0 Error(s)
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher
adb -s <device-serial> logcat -c
adb -s <device-serial> shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
# drive: keyevent 96 (A) x4 -> level select; 22 (RIGHT) then 96 (A) -> gameplay
```

---

## 7. Residual limitations / compromises

1. **Menu/UI canvas stretch (cosmetic, pre-existing design).** The menu path
   composes into its original **1024×600** canvas and then stretches that to
   `screenSize` (`Game1.cs:3794-3809`, `:3760-3763`; `MainMenu.cs` lays out at
   fixed 400/512/300 coords). At 1280×720 that was a ~4 % horizontal stretch; at
   1600×720 it is ~30 % (1024→1600 = 1.5625 vs 600→720 = 1.2). The world / HUD /
   gameplay are unaffected (uniform 1.5×), only the menus are horizontally
   widened. Fixing this properly means re-laying the menu out on a canvas whose
   aspect matches the screen (widen the canvas and the `wideScreen` offsets in
   `MainMenu.cs`), which is a separate, broader change.
2. **Game-art dark edges.** The screenshots show a ~4 px dark rim; this is the
   game's own edge/vignette art (the pre-change build had black game-art edges
   too), not a letterbox. The compositor geometry proves the surface fills
   2400×1080.
3. **Panel is 20:9 (2.222).** A device with a different aspect will pick a
   different `logicalW` (even, `= round(panelW·720/panelH)`) and scale
   accordingly; `LogicalHeight` stays 720 on any device. The approach is not
   hard-coded to 1600 now (only the default is).
4. **Revert:** delete `Platform/WidescreenConfig.cs`, remove the
   `WidescreenConfig` blocks in `Game1.cs:358-397` and `Activity1.cs:31-46,
   62-119` (restore `SetContentView(_view)` and the original `screenSize`
   lookup), rebuild. All original code is intact behind the switch.

---

## 8. Files / artifacts

**Changed / added source**
* **new** `src/Dishwasher/Platform/WidescreenConfig.cs`
* `src/Dishwasher/Activity1.cs` (cutout + logical view + scale)
* `src/Dishwasher/GameSource/projectDish/Game1.cs` (`screenSize` override +
  viewport pin, `:358-397`)

**Screenshots** (`android/notes/widescreen/`, copies in
`<dev-notes>/proof/ws/`)
* `baseline-current.png` — BEFORE: fixed 1280×720 box top-left
* `ws-final-shop.png` — AFTER: in-game shop, measured `bbox=(0,0,2399,1079)` ⇒ fills the whole frame
* `ws-2-menu.png` — AFTER: title/menu fills 2400×1080
* `ws-play-1.png`, `ws-play-2.png`, `ws-nav-1.png` — AFTER: gameplay (HUD top
  corners, wider world)
* `ws-tut-4.png` — AFTER: Select-Level menu
* `ws-before-after.png` — stacked BEFORE / AFTER comparison

**Helper** `tools/scripts/ws_analyze.py` (bbox / corner / rectangle mean).
