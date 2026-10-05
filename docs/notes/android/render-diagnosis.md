# Rendering diagnosis — black gameplay + unreadable menu

**Author:** Rendering Diagnostics Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (adb `$ADB`)
**Scope:** read-only `managed/decompiled/game`; editing confined to
`<dev-workspace>/` and this notes tree.

**Result:** gameplay is **no longer black** and the menu text is **readable**.
The root cause was a **single systemic shader bug**, not the render-target or
sampler theory. Evidence is in `<dev-notes>/proof/render/`.

---

## 0. TL;DR

> The 19 re-authored post-process `.fx` effects supply a **pass-through vertex
> shader that omits MonoGame's SpriteBatch orthographic pixel→clip transform**.
> In XNA 3.0 those effects were pixel-only (the Xbox SpriteBatch VS supplied the
> geometry); in MonoGame the effect must supply the whole vertex stage, and
> `SpriteBatch` feeds it **pixel-space** vertices (see MonoGame `SpriteEffect`,
> which transforms them with `Matrix.CreateOrthographicOffCenter(0, vp.W, vp.H,
> 0, 0, -1)`). Without that transform every custom-effect SpriteBatch quad
> landed outside clip space and drew nothing, so:
> * **gameplay**: `drawLenseTarg` → `rTarg[1]` stayed black, then the additive
>   overlays and the final `grad` composite (also custom effects) drew nothing →
>   **black screen** (only the default-effect HUD remained);
> * **menu**: the `ink` overlays (`lenseTarg`, `refractTarg`) drew nothing →
>   the wrong/ghosted look.
>
> **Fix:** give every effect a `float4x4 MatrixTransform` and
> `o.Position = mul(input.Position, MatrixTransform)`, and set it from the
> current viewport in the one hook every XNA-3.0 effect block calls —
> `Effect.Begin()` (see `Platform/Shim_EffectCompat.cs`).

---

## 1. Render pipeline map (`GameSource/projectDish/`, READ-ONLY original at `managed/decompiled/game/projectDish/`)

### 1.1 Resources
* Render targets created in `Game1.LoadContent` / the loader:
  `rTarg[2]` (screen), `lenseTarg`, `refractTarg` (screen), `bloomTarg` (128²),
  `fadeTarg` (128²), `waterTarg` (256²), `bloodTarg`, `camSplatTarg` (½),
  `mapTarg` (½), `greenMapTarg` (720²) — `Game1.cs:577–582`, `Game1.cs:870–877`.
  All are `SurfaceFormat.Color`, `DepthFormat.None`, `RenderTargetUsage.DiscardContents`,
  `MultiSampleCount = PresentationParameters.MultiSampleCount` and
  `graphics.PreferMultiSampling = false` (`Game1.cs:357`) ⇒ **msaa = 0** (verified at runtime).
* Effects loaded at `Game1.cs:551–571` (`fx/poster`, `fx/lense`, `fx/grad`,
  `fx/blur`, `fx/bloom`, `fx/redhaze`, `fx/fade`, `fx/water`, `fx/trainblur`,
  `fx/trainover`, `fx/bubble`, `fx/burnblur`, `fx/comicblur`, `fx/color`,
  `fx/trail`, `fx/newblood`, `fx/camsplat`, `fx/ink`, `fx/wallblood`).

### 1.2 `Draw()` sequence — `Game1.Draw`, `Game1.cs:3539`
`gameMode` selects the branch; `curRTarg` is reset to 0 at the end of every
frame (`Game1.cs:4264–4265`, so it is always 0).

**Menu (`case 1`, `Game1.cs:3667–3842`)**
1. `SetRenderTarget(lenseTarg)`, clear White, `mainMenu.DrawOptions` (:3670–3683)
2. `SetRenderTarget(refractTarg)`, clear White, `mainMenu.drawBlobs` (:3684–3686)
3. `SetRenderTarget(rTarg[0])`, clear beige, `dishTex` + rings (:3687–3702)
4. `inkEffect` over **lenseTarg** → `rTarg` (AlphaBlend, colour `(0.5,0,0,1)`) (:3719–3727)
5. `mainMenu.Draw` (text) → `rTarg` (:3729–3743)
6. `SetRenderTarget(null)`, plain `sprite.Draw(rTarg[0])` (Opaque) (:3744–3761)
7. `inkEffect` over **refractTarg** → backbuffer (colour `(0.5,0,0,1)`) (:3764–3784)
8. cursor / frame / buttons / text overlays (plain)

**Gameplay (`case 0`, `Game1.cs:3844–4180`)**
1. `greenMapTarg` → `mapTarg` (wall blood), `map.Draw` → `mapTarg` (:3847–3868)
2. `SetRenderTarget(rTarg[0])`: `map.Draw`, wallBlood composite of `mapTarg`,
   characters, trails, sprite layers, fore-effects (:3869–3967)
3. `camSplatTarg` (via `GameRender.drawCamSplats`, `GameRender.cs:74`) (:3970)
4. `bloodTarg` when `bloodTargMode` (:3982–3987)
5. `bloomTarg` ← `drawBloom()` (effect pass) (:3988–3990)
6. `fadeTarg` ← `drawFade(0.015,0)` (effect pass) (:3991–3993)
7. `refractTarg` ← `GameRender.drawRefract` (`GameRender.cs:132`) (:3994–3997)
8. `waterTarg` ← `drawWater()` when water (:3998–4003)
9. `SetRenderTarget(rTarg[1])`; `drawMinimalTarg` (`GameRender.cs:81`) **or**
   `drawLenseTarg` (`GameRender.cs:91`) — the latter binds `GraphicsDevice.Textures[1]`
   (:4005–4013)
10. additive bloom/fade/foreLights + `drawFadeOverLay` (`GameRender.cs:52`) (:4014–4098)
11. `SetRenderTarget(null)`; final **`gradEffect`** full-screen composite (:4099–4133)
12. cam-splat overlay, pickups, overlay, HUD (plain/`camSplatEffect`) (:4140–4179)

**Comic strip (`case 2`, `Game1.cs:4182–4192`)** `rTarg[0]` ← `strip.Draw` with
`comicBlurEffect`, then plain blit to backbuffer.

### 1.3 The one place the old code used `GraphicsDevice.Textures[1]`
`GameRender.drawLenseTarg` (`GameRender.cs:91`): `lense` samples the sprite
texture (`refractTarg`) on s0 and the scene (`rTarg[0]`) on **s1**; the game
binds s1 via `GraphicsDevice.Textures[1]` (old port line 97), not via
`Effect.Parameters`. MGFX sampler reflection (parsed from `lense.xnb`):
`ps_s0`→textureSlot 0 (`refractSampler`), `ps_s1`→textureSlot 1 (`backBuffer`).

---

## 2. Experiments (all behind `Dishwasher.RenderDiagnostics`, toggled live with
the **volume keys**; the game ignores them). Modes and results:

| mode | action | measured result |
|---|---|---|
| 0 | normal | **pre-fix: black**; post-fix: scene visible |
| 1 | final composite draws `rTarg[0]` (pre-lense) | map/scene present → base render is fine |
| 2 | final composite draws `rTarg[1]` (post-lense) | pre-fix black; post-fix visible |
| 3 | force `drawMinimalTarg` (plain copy, no lense) + composite | post-fix scene visible |
| 4/5/6/7 | show `refractTarg`/`lenseTarg`/`mapTarg`/`bloomTarg` | mapTarg/bloomTarg render; refract has blood |
| 8 | apply `grad` via `sprite.Begin(..., gradEffect)` | pre-fix still black (effect itself broken) |
| 9 | log pixel readback of every RT + `lastPath` (throttled) | **hard numbers, see §2.1** |
| 14/15/16 | copy `mapTarg`/`refractTarg`/`rTarg[0]` → `rTarg[1]` at the *end* then show | all render → RT→RT blits are fine |
| 18 | readback after `drawMinimalTarg` | see §2.1 |
| 10/11/12 | menu: skip refract / lense / both `ink` overlays | isolates the menu overlay tint |
| 90 | lense with the **original** bind order (before Apply) | **dark purple / mostly black** vs mode 0's full scene → A/B evidence for fix #2 (see `93` vs `94`) |

### 2.1 Measured readbacks (mode 9, pre-`MatrixTransform` build — the decisive data)
```
rt rTarg[cur]   1280x720 msaa=0 center=(139,136,132,255)   <- scene present
rt rTarg[other] 1280x720 msaa=0 center=(5,5,4,255)          <- lense output ~black
rt refractTarg  1280x720 msaa=0 center=(0,0,0,255)
rt mapTarg       640x360  msaa=0 center=(0,82,0,255)        <- green map
rt bloomTarg     128x128  msaa=0 center=(0,0,0,255)
curRTarg=0  lastPath=lense
```
`rTarg[cur]` **had the world**; `rTarg[other]` (the lense composite) was black.
Mode 16 (copy `rTarg[0]`→`rTarg[1]`) rendered the *full scene* ⇒ render targets,
formats, orientation and RT→RT blitting were all **innocent**. The fault was the
custom-effect passes.

**The smoking gun:** MonoGame's own `SpriteEffect` projects pixel-space vertices:
`Matrix.CreateOrthographicOffCenter(0, viewport.Width, viewport.Height, 0, 0, -1)`
(decompiled `SpriteEffect.cs:61`), and `posFixup` (decompiled `GraphicsDevice.cs:2493–2516`)
is only the D3D→GL Y-flip / half-texel offset, **not** a viewport scale. The
re-authored `.fx` VS did `o.Position = input.Position;` with no projection, so a
full-screen quad at pixel coords 0…1280 was interpreted as clip space and was
off-screen. Pixel-only effects + MonoGame's SpriteBatch need the projection.

---

## 3. Root causes

### RC1 (primary) — pass-through VS missing the SpriteBatch ortho transform
* **Where:** every re-authored `Content/fx/*.fx` `MainVS`.
* **Effect:** all 19 post-process passes (`lense`, `grad`, `bloom`, `fade`,
  `wallblood`, `ink`, `brawblood`, `camsplat`, …) rendered nothing. Gameplay's
  `rTarg[1]` stayed black; the backbuffer was only ever cleared black + HUD.
* **Evidence:** §2.1 + `65/66/67` (pre-lense world vs post-lense black) +
  mode 16 (`46-mode16-rt-to-rt.png`) showing the intact world.
* **Fix:** see §4.

### RC2 — `GraphicsDevice.Textures[1]` clobbered by `EffectPass.Apply()`
`EffectPass.Apply()` calls `SetShaderSamplers`, which does
`graphicsDevice.Textures[samplerInfo.textureSlot] = effect.Parameters[sampler].Data as Texture`.
For a combined `sampler2D backBuffer`, `Data` is not a `Texture`, so it writes
**null** to slot 1. The old port bound slot 1 *before* `effectPass.Begin()`
(`GameRender.cs` old line 97), so the binding was nulled and `ps_s1` sampled
black. Fixed by binding s1 **after** `Apply()` (mode 90 keeps the old order for
A/B). This is required for `lense` to see the scene.

### RC3 (contributing, not the black cause) — `drawMinimalTarg`/RT chain exonerated
Not a bug; recorded here because the earlier hypothesis (render-target / viewport /
MSAA) was disproved by modes 1/3/16 and `msaa=0` readbacks.

---

## 4. Fixes applied

1. **`Platform/Shim_EffectCompat.cs`** — `Effect.Begin()` now sets the effect's
   `MatrixTransform` parameter to `Matrix.CreateOrthographicOffCenter(0,
   Viewport.Width, Viewport.Height, 0, 0, -1)`, matching MonoGame's `SpriteEffect`.
   This is the single hook every XNA-3.0 `effect.Begin()…Pass.Begin()…` block uses,
   so no game call site changed.
2. **`Content/fx/*.fx` (19 runtime effects)** — added
   `float4x4 MatrixTransform;` and `o.Position = mul(input.Position, MatrixTransform);`.
   Originals preserved at
   `<dev-notes>/shaders/pre-matrixtransform-fx/` (byte-for-byte copies).
3. **`GameSource/projectDish/GameRender.cs`** — `drawLenseTarg` binds
   `GraphicsDevice.Textures[1]` **after** `effectPass.Begin()` (RC2).
4. Recompiled all 19 for Android: `mgcb Content.mgcb /platform:Android
   /outputDir:bin/Android` → `Build 21 succeeded, 0 failed`; staged
   `Content/bin/Android/fx/*.xnb` → `Assets/fx/*.xnb`.
5. **Diagnostics harness (additive, revertable):** `Platform/RenderDiagnostics.cs`,
   volume-key hook in `Activity1.cs`, and mode switches in `Game1.cs` /
   `GameRender.cs` (all marked `TEMP RENDER DIAGNOSTICS`).

---

## 5. Before / after evidence (`<dev-notes>/proof/render/`)

| stage | before (pre-fix build) | after (fixed build) |
|---|---|---|
| menu / title | `01-after-A.png`, `02-after-A2.png` (ghosted text) | `70-menu-fixed.png`, `81-menu-final.png` (legible text) |
| gameplay | `13-gameplay-b.png`, `50-mode0-normal-alive.png` (**black + HUD only**) | `63-gameplay-fixed.png`, `64-gameplay-fixed2.png`, `94-mode0-fixed-samelevel.png` (**Foghorn Café diner scene + HUD visible**) |
| proof: base scene intact | `46-mode16-rt-to-rt.png` (RT→RT copy) | `65-new-mode1.png` (pre-lense) |
| proof: post-lense | `48-mode2-postlense-alive.png` (near-black) | `66-new-mode2.png` (scene) |
| proof: minimal copy | `47-mode3-minimal-alive.png` | `67-new-mode3.png` |
| proof: lense bind order (same level) | `93-mode90-oldbindorder.png` (dark purple) | `94-mode0-fixed-samelevel.png` (full scene) |
| readback | — | §2.1 (mode 9 logcat) |

**What is now visible in gameplay:** the Foghorn Café diner interior (walls,
tables, neon, the player character), plus the full HUD (health bar, score,
money, comic banners) — the same level that was previously a pure-black screen.
**In the menu:** the "PRESS A TO START" and main-menu item text is now legible
(white/green with outline) instead of the ghosted/dim pre-fix rendering.

---

## 6. Remaining rendering issues (ranked by impact)

1. **Colour-grade / tint fidelity (high cosmetic).** Gameplay is strongly
   pink/magenta; the menu background is a green/orange noisy wash. The geometry
   is correct; the tint comes from the *game-driven* post overlays
   (`fade` theme colour `(1,0.8,1,0.8)` for theme 0; the menu `ink` overlay).
   The `fade.fx` re-author is **structurally different** from the disassembly
   (decompiled `fade_t0p0_PS.hlsl` has three `fader` paths: fader=0 → opaque
   blurred scene; fader=1 → `max(0, dist*2-0.5)` mask; fader=2 →
   `max(0, dist*1.5-0.1)` with `rgb*=0.1154`), whereas our `.fx` only
   distinguishes fader==1 vs else. `poster`, `color`, `camsplat`, `ink`,
   `wallblood`, `redhaze`, `water`, `grad`, `trainover`, `blur` P1 are likewise
   observable-behaviour re-authors (documented in `shaders/COMPLETION.md`).
   Reference captures are needed to tune them.
2. **`GraphicsDevice.Textures[1]` code-smell (low, fixed locally).** Fix #2 is a
   local reorder; a cleaner long-term fix is to have the re-authored `lense`
   expose a `Texture2D`+`SamplerState` pair and set it via
   `Effect.Parameters[...].SetValue(...)`, which `SetShaderSamplers` would then
   honour.
3. **Menu background composition (medium).** Modes 10–12 (`70–73`) show the
   green wash originates in the `ink`/`refract`/`lense` overlays; verifying the
   intended menu art requires an original capture.
4. **`MSAA`/`DiscardContents`** were checked and are not contributing
   (`msaa=0`, and mode 16 proves RT→RT composition works).
5. `DrawFailDie` / `DrawLoader` / credits paths were not re-verified end-to-end,
   but they use the same fixed effect set.

---

## 7. Reproduce

```sh
source tools/scripts/env.sh
cd src/Dishwasher/Content
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
cp bin/Android/fx/*.xnb ../Assets/fx/
cd ..
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
$ADB install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
```

Runtime experiment harness (additive): **volume-up = mode+1**, **volume-down =
mode−1**; mode descriptions and the readback logger are in
`Platform/RenderDiagnostics.cs`. Mode 9 dumps render-target pixels to logcat
(tag `Dishwasher`, prefix `[rdiag]`).
