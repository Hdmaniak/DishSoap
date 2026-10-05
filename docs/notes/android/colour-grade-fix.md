# Colour-grade fidelity fix — pink gameplay / green menu

**Author:** Shader Fidelity Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (adb `$ADB`)
**Scope:** edits confined to `<dev-workspace>/` and this notes tree. Read-only reference
trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`, `android-app`)
were not modified.

**Result:** the menu's green wash and gameplay's magenta cast are gone.
Menu/gameplay are now warm/neutral and legible. The dominant cause was a
**systemic omission**: every runtime post-process `.fx` ignored the SpriteBatch
per-draw vertex colour, which the game relies on for its colour grade. Four
additional `.fx` were re-transcribed faithfully against the XenosRecomp HLSL.

---

## 0. TL;DR

1. **Root cause (systemic):** the 19 runtime effects pass `input.Color` through
   their vertex shader but never use it in the pixel shader. On the Xbox the
   sprite pipeline applied the per-draw tint; the game passes non-white colours
   to the `ink`, `wallblood`, `camsplat` and `newblood` draws specifically to
   grade them. Ignoring the colour made the menu `ink` glow channel
   (`Globals` draws it with `(0.5, 0, 0, 1)`) leak green, and made several
   gameplay overlays render at full strength instead of tinted.
2. **Four faithful re-transcriptions** for the biggest measurable culprits:
   `fade.fx` (all three `fader` paths), `bloom.fx`, `ink.fx`, `grad.fx`.
3. All 19 effects now apply `* input.Color`. `MatrixTransform`, technique/pass
   names and parameter names/types are preserved.
4. **Measured**: menu mean `(96,151,89)` (green, G−R +55) → `(140,102,96)`
   (warm, G−R −38). Gameplay mean `(137,46,81)` (magenta) → `(149,83,84)`
   (warm red). The bloom overlay's blue contribution went `ΔB −39` → `ΔB 0`.

---

## 1. What was ruled out first (non-shader causes)

### 1.1 Textures — **innocent** (no R/B inversion)

The clean source art is XNA v2 XNB (`assets-clean/gfx/*.xnb`), raw 32-bpp
`fmt 1`. I parsed `dish.xnb` (640×480) and `dishlogo.xnb` (450×90), re-read the
raw texels as **BGRA** and as **RGBA**, and compared each against the shipped
converted PNG (`Dishwasher/Assets/gfx/*.png`) byte-for-byte:

| texture | conversion vs raw-**BGRA** | vs raw-**RGBA** |
|---|---|---|
| `dish.png` | **MAD = 0.0** (byte-exact) | MAD = 104.8 |
| `dishlogo.png` | BGRA is the matching layout | (RGBA mismatches) |

The converter emitted channel-correct PNGs. A global R/B swap would also have
made the neutral menu clear `(0.85,0.85,0.7)` and the base gameplay scene
(`mode 1`) blue/teal; measured `mode 1` is warm brown `(52,35,34)`. Textures are
not the cause. (The raw-format BGRA proof also exists at
`<dev-notes>/proof/dbp_raw_channelorder_compare.png`.)

### 1.2 Blend / alpha — **innocent**

The game uses `BlendState.AlphaBlend` (straight alpha:
`src.rgb*src.a + dst*(1-src.a)`); the converted PNGs carry independent alpha
(checked above). Normal sprites (text, HUD, logos) render correctly under that
state. If premultiplication were required, ordinary text would fringe/wash out;
it does not. The tint was confined to **custom-effect** draws only
(measured in §2).

### 1.3 Render-targets / MSAA — already exonerated

Per `android/notes/render-diagnosis.md` §3 (RC3): `msaa=0`, RT→RT blits and the
base scene are intact. Not re-tested.

---

## 2. Measured isolation tables

Method: `Platform/RenderDiagnostics.cs` was extended with per-overlay kill
switches (modes 20–34, toggled with the volume keys; see §4). Each frame was
captured with `screencap` and the mean RGB of the game viewport was computed in
Python (PIL). Tables are means; `baseline` is mode 0.

### 2.1 Menu (tight A/B, captures ~1 s apart) — the green is the `ink` overlays

Pre-fix build, region `(30,290)–(1050,820)`:

| mode | action | mean R,G,B | G−R | ⇒ ink contributes |
|---|---|---|---|---|
| 0 | normal (both `ink` overlays) | **96,151,89** | **+55** | — |
| 10 | skip **refract** `ink` overlay | 96,107,89 | +11 | **G +44, B +0** |
| 11 | skip **lense** `ink` overlay | 98,143,91 | +45 | G +8 |
| 12 | skip both | 108,97,91 | −11 | base is neutral |

**Reading:** the green channel comes almost entirely from the `refractTarg`
`ink` overlay (the one drawn on the backbuffer with `sprite.Draw(refractTarg,…,
new Color(0.5,0,0,1))`). `ink`'s output is `(red, glow, 0, a)`; with the vertex
colour ignored, `glow` (green) was never zeroed by the `(0.5,0,0,1)` tint. With
the colour applied, that overlay can only add red.

Post-fix build (same region):

| mode | mean R,G,B | G−R |
|---|---|---|
| 0 | **140,102,96** | **−38** (warm) |
| 10 | 112,103,96 | −9 |
| 11 | 152,105,98 | −47 |
| 12 | 105,105,98 | 0 |

### 2.2 Gameplay (theme 0, Foghorn Café tutorial) — the pink is bloom + colour

Pre-fix build, mode-0 baseline `(137,46,81)` (magenta: G far below R and B):

| mode | action | mean R,G,B | Δ vs mode 0 (R,G,B) | effect contribution |
|---|---|---|---|---|
| 0 | normal | **137,46,81** | — | — |
| **20** | **skip bloom overlay** | **113,40,42** | **−24,−6,−39** | **bloom = R+24 G+6 B+39** |
| 21 | skip fade theme overlay | 134,37,74 | −3,−9,−7 | fade = R+3 G+9 B+7 |
| 22 | skip cam-splat | 137,46,81 | 0,0,0 | inactive (no splats) |
| 23 | skip water | 137,46,81 | 0,0,0 | inactive (no water) |
| **25** | **skip wallblood** | **114,46,81** | **−23,0,0** | **wallblood = R+23** |
| 26 | skip newblood | 137,46,81 | 0,0,0 | inactive (bloodTargMode off) |
| 27 | skip bloom+fade+camsplat | 100,31,34 | −37,−15,−47 | sum of the three |
| 32 | neutral ALL overlays + grad bypass | 97,73,70 | −40,+27,−11 | post chain removed → neutral |
| 34 | neutral overlays, grad kept | 73,31,34 | −64,−15,−47 | grad still applied |

Post-fix build (same region):

| mode | mean R,G,B | Δ vs mode 0 |
|---|---|---|
| 0 | **149,83,84** | — |
| 20 skip bloom | **149,83,84** | **0,0,0** (bloom now no-op on a warm scene) |
| 24 grad bypass | 151,101,97 | +2,+18,+13 |
| 32 all off + bypass | 97,73,71 | −52,−10,−13 |
| 34 all off, grad kept | 97,55,58 | −52,−28,−26 |

> **Caveat:** modes 24/32/34 compare the *final grad* pass against a plain
> full-screen copy, but the real grad draw applies `mapRot`/`zoom`, so those
> three deltas are content-misaligned and are **not** a clean grad isolation.
> The bloom (`20`), wallblood (`25`) and fade (`21`) switches do use identical
> draws and are valid.

### 2.3 Ranked contribution to the casts (pre-fix, measured)

**Gameplay pink/magenta (theme 0):**
1. **`bloom` overlay** — adds `ΔB +39, ΔR +24`; the single biggest blue/magenta
   driver. Fixed by the faithful blue>red bright-pass (§3.2) → now `Δ 0`.
2. **missing vertex colour** — `wallblood` adds `ΔR +23` untinted; `newblood`,
   `camsplat` drawn with `(num,num,num)`/`Black` were also untinted. Fixed
   globally.
3. **`grad` final composite** — measurable hue suppression (G suppressed
   relative to R), but the isolation is confounded by zoom/rotation (above);
   still re-transcribed faithfully.
4. **`fade` theme overlay** — small `Δ(R+3,G+9,B+7)`; the old 3-path logic was
   structurally wrong, now faithful.
5. `camsplat`, `water`, `newblood`, `redhaze`, `trainover`, `bubble`,
   `burnblur`, `comicblur`, `trail`, `poster`, `color`, `blur` P0/P1 — **zero**
   in this scene (their draw sites were not reached). `poster`/`color` are
   loaded but never driven by the game.

**Menu green/orange:**
1. **`refractTarg` `ink` overlay** — `ΔG +44` (glow channel not zeroed).
2. **`lenseTarg` `ink` overlay** — `ΔG +8`.

---

## 3. Corrected effects — approximation vs faithful

### 3.0 Systemic: vertex colour (all 19 runtime effects)

Before, every `MainPS` ignored `input.Color` (only `Sprite.fx`/`SpriteShadow.fx`
multiplied it). After, every pixel-shader output is multiplied by the
interpolated vertex colour, matching MonoGame's own `SpriteEffect`
(`tex2D(...) * input.Color`). This is the single change with the largest visible
effect; it is what turns the menu `ink` from green to red and stops the
untinted gameplay overlays.

### 3.1 `fade.fx` — implemented all three `fader` paths

Reference `fade_t0p0_PS.hlsl`. The microcode keeps both the raw 13-tap sum and
its `/13` average (`c254.z = 1/13`), plus constants `c253=(-0.1, 2.0)`,
`c255=(0.1154, 0.15, 1.5, -0.25)`.

| `fader` | **previous `.fx`** | **faithful now** |
|---|---|---|
| 0 | `rgb = avg`, `a = saturate(dist*2 − 0.25)` | `rgb = avg`, `a = 1.0` (opaque blurred scene) |
| 1 | `rgb = avg * 0.1154`, `a = saturate(dist*−0.6 + 1.5)` | `rgb = avg`, `a = saturate(dist*2 − 0.25)` |
| 2 | *(same as 0)* | `rgb = sum * 0.1154`, `a = saturate(dist*1.5 − 0.1) * 0.15` |

So the old re-author had paths 0 and 1 swapped and never implemented path 2.

### 3.2 `bloom.fx` — faithful blue bright-pass

Reference `bloom_t0p0_PS.hlsl`: it samples **8** offsets scaled by `0.015`,
keeps each tap's **blue channel only when blue > red** (`tfetch(...).xz`,
compare `r0.y > r0.x`), sums, multiplies by `0.2`, and writes a **grayscale**
value with `alpha = 1`.

* Previous: 12 taps × `0.0237`, `max(s − 0.2, 0)` on all channels, average, then
  returned `centre + sum` (a coloured, much brighter image).
* Effect on a warm scene (blue < red): the faithful pass is ≈ 0, so the
  additive `(0.15,0.15,1,0.8)` bloom overlay no longer floods the frame with
  blue. Measured contribution `ΔB −39` → `ΔB 0`.
* The `offsets` array is kept declared `float2[12]` so the MGFX constant-buffer
  layout still matches the table (the microcode reads only 8; the unused four
  are retained at `1e-7` weight — see the crash note in §5).

### 3.3 `ink.fx` — faithful level ramp

Reference `ink_t0p0_PS.hlsl`. `t = 1 − red`; the level is piecewise-linear with
knots at `0.1 / 0.4 / 0.5`:

```
t<0.1 : t                0.1≤t<0.4 : (t−0.1)/3+0.1
0.4≤t<0.5 : 8(t−0.4)+0.2 t≥0.5 : 1
glow  = level<0.5 ? 0.45(0.5−level) : 0
out   = (uv.y*0.25 + bright + glow,  glow,  0,  0.85*level)
```

* Previous: `t = 1−v+bright`, `smoothstep` ink ramp, `saturate(t*0.9)` alpha.
* Combined with the vertex-colour multiply, the menu overlay is now red (its
  `glow` green channel is zeroed by the `(0.5,0,0,1)` tint).

### 3.4 `grad.fx` — faithful walk / gradient / burn

Reference `grad_t0p0_PS.hlsl`. When `levs>0` it walks **7 times away from
`(xcenter,ycenter)` by `dir / width`**, accumulating 8 taps × `0.125`; gradient
`t/(rgrad,ggrad,bgrad)` with `t = gradFlip ? 1−uv.y : uv.y`; burn
`burnmag * length(uv − 0.5)` (the microcode's burn centre is the **screen
centre**, not the blast centre); `col = (acc.rgb + grd) * bright − burn`.

* Previous: ad-hoc step `dir * inv * 10` with a `t = i*0.125` schedule, and the
  burn centred on `(xcenter,ycenter)`.
* No `MatrixTransform`, technique or parameter changes.

### 3.5 Vertex colour added to the remaining effects

`lense`, `camsplat`, `wallblood`, `redhaze`, `water`, `trainover`, `bubble`,
`burnblur`, `comicblur`, `trail`, `newblood`, `poster`, `color`, `blur` (both
passes) and `trainblur` all now multiply their result by `input.Color`. Their
bodies are otherwise the previous observable-behaviour re-authors (noted in
`shaders/COMPLETION.md`); `camsplat`/`wallblood`/`newblood` are still simplified,
but they now honour the game's `Color.DarkGray` / `Color.Black` /
`(num,num,num)` tints.

---

## 4. Diagnostics harness (reused and extended)

`Platform/RenderDiagnostics.cs` gained per-overlay kill switches driven by the
existing volume-key hook (`Activity1.cs`), and `Game1.cs` gained matching
guards (all marked `TEMP RENDER DIAGNOSTICS`):

| mode | effect |
|---|---|
| 20 | skip additive bloom overlay |
| 21 | skip additive fade theme overlay |
| 22 | skip cam-splat overlay |
| 23 | skip water overlay |
| 24 | grad bypass (opaque post-lense copy) |
| 25 | skip wallblood composite |
| 26 | skip newblood / black-blood |
| 27 | skip bloom+fade+camsplat |
| 32 | neutral ALL overlays + grad bypass |
| 33 | neutral ALL + minimal (no lense) |
| 34 | neutral overlays, grad kept |

Volume-up = mode+1, volume-down = mode−1 (the game ignores these keys).

---

## 5. Build / deploy

```sh
cd src/Dishwasher/Content
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
# -> Build 21 succeeded, 0 failed.
cp bin/Android/fx/*.xnb ../Assets/fx/
cd ..
dotnet build -f net8.0-android -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 -p:EmbedAssembliesIntoApk=true
# -> Build succeeded, 0 Error(s).  APK: bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
```

Deployed with `scripts/deploy.sh` (install -r -d, force-stop, logcat -c, `am
start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1`).

**MGFX constant-buffer gotcha hit during this work:** the first `bloom.fx`
revision read only 8 of the declared `float2[12] offsets`; the compiler shrank
the buffer but the reflected default still had 12 elements, and
`EffectPass.Apply()` threw
`System.ArgumentException: Offset and length were out of bounds` inside
`ConstantBuffer.SetData` (stack: `drawBloom`). The fix keeps all 12 referenced at
negligible weight. Any future array-size reduction should be checked the same
way.

---

## 6. Before / after evidence (`<dev-notes>/proof/render/`)

| stage | before | after |
|---|---|---|
| menu / title | `before-menu-green.png` (mean 96,151,89 — green wash, ghosted text) | `after-menu-warm.png` (mean 140,102,96 — warm brown, legible) |
| menu base (both ink overlays skipped) | `before-menu-skip-refract.png` | `after-menu-skip-both.png` |
| gameplay | `before-gameplay-pink.png` (mean 137,46,81 — magenta, washed out) | `after-gameplay-warm.png` (mean 149,83,84 — warm red, detail visible) |

Additional captures from this session: `pre-gp-m20..m34.png` (pre-fix isolation
sweep), `post-gp-m20/m24/m32/m34.png` (post-fix), `fix-menu-01.png` (main menu),
`d3.png` (level select), `e1/e3.png` (intro comic), `c1..c16.png` (navigation).

**What changed visually:** the menu's green/orange noise is gone — the title
screen, main menu, difficulty list and level select are now a warm brown/red
with clearly legible text and the character/skull splash art. Gameplay lost the
uniform magenta wash: the diner walls are now light/neutral under the red theme
lighting, the character and HUD are visible, and no blue bloom floods the frame.

---

## 7. Remaining tint issues (ranked)

1. **`fade` theme overlay haze (expected game data).** Theme 0 draws the fade
   overlay with `(1, 0.8, 1, 0.8)` (a magenta-white haze). That colour is the
   game's own theme table, not a shader bug; it is now applied faithfully and
   contributes the residual warm/pink glow in the diner. If the reference look
   is wanted, tune the level's theme colour, not the shader.
2. **Residual red band at the bottom of menu screens.** Visible in
   `after-menu-warm.png` (a thin red strip above the letterbox). It comes from
   the `refractTarg` `ink` overlay — `ink`'s red output is
   `uv.y*0.25 + bright + glow`, so the overlay has a vertical gradient and the
   bottom edge of the refract RT lights up. Not fully resolved; would need the
   original Xbox capture or a study of the SpriteBatch UV orientation.
3. **`grad` residual hue not cleanly isolated.** The grad-bypass diagnostic
   cannot reproduce the real `mapRot`/`zoom` composite, so grad's remaining
   contribution to the warm cast is unquantified (its faithful transcription is
   in place).
4. **Unverified per-level effects.** `redhaze`, `water`, `trainover`, `bubble`,
   `burnblur`, `comicblur`, `camsplat` and `newblood` have no draw site in the
   sampled theme-0 tutorial, so their per-level colour fidelity was not
   measured; their colour multiply is in place but their bodies remain
   observable-behaviour re-authors.
5. **`poster.fx` / `color.fx`** are loaded but never driven by the game, so
   their bodies cannot affect the shipped colour grade.

---

## 8. Trees touched

Written: `Dishwasher/Content/fx/*.fx` (19 runtime effects + `trainblur`),
`Dishwasher/Content/bin/Android/fx/*.xnb` and `Dishwasher/Assets/fx/*.xnb`
(recompiled/staged), `Dishwasher/Platform/RenderDiagnostics.cs`,
`Dishwasher/GameSource/projectDish/Game1.cs` (diagnostics guards only),
`scripts/deploy.sh`, `scripts/shot.sh`, and this note. `MatrixTransform` and all
technique/pass/parameter names are unchanged. No read-only tree was modified.
