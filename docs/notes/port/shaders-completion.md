# Shader completion — Android MGFX build

**Author:** Shader Completion Lead (subagent)
**Date:** 2026-10-02
**Scope:** author the full 19-effect set the game loads, compile it for
`/platform:Android` via the Wine-backed MGCB path, and stage the compiled
`.xnb` for APK packaging.

**Result:** ✅ **Build 21 succeeded, 0 failed.** All 19 runtime effects compiled
and are staged at `Dishwasher/Assets/fx/<name>.xnb`.

---

## 0. Build recipe (exact)

```bash
cd src/Dishwasher/Content
source tools/scripts/env.sh   # now exports MGFXC_WINE_PATH
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
```

Result line (incremental final run, after the one syntax fix below):

```
Build 21 succeeded, 0 failed.
```

First full run reported `Build 20 succeeded, 1 failed`; the single failure was
`newblood.fx(78,15-18): error X3000: syntax error: unexpected token 'line'` —
`line` is a reserved HLSL keyword. The local variable was renamed to `scan`
and the incremental rebuild produced the final `21 succeeded, 0 failed`.

`tools/scripts/env.sh` now contains (task item 2):

```bash
export MGFXC_WINE_PATH="$HOME/.wine-mgcb"
```

## 1. Per-effect status

All 19 `.fx` live in `Dishwasher/Content/fx/`. Technique/pass names are
preserved from the original D3DX reflection; the game selects passes by index
(`CurrentTechnique.Passes[n]`), so the pass **count** is the load-bearing part.

| # | .fx | blueprint (notes/shaders/decompiled) | technique / pass(es) | parameters (exact original names) | compiled |
|---|---|---|---|---|---|
| 1 | `poster.fx` | `poster_t0p0_PS.hlsl` | Poster / P0 | alpha, rMin,gMin,bMin, rMid,gMid,bMid, rMax,gMax,bMax, fore | ✅ |
| 2 | `lense.fx` | `lense_t0p0_PS.hlsl` | PostLense / P0 | refractSampler, backBuffer, edgeBlur, offsets[6] | ✅ |
| 3 | `grad.fx` | `grad_t0p0_PS.hlsl` | PostGrad / P0 | burnmag, gradFlip, rgrad, ggrad, bgrad, bright, levs, width, xcenter, ycenter | ✅ |
| 4 | `blur.fx` | `blur_t0p0_PS.hlsl`, `blur_t0p1_PS.hlsl` | Blast / **P0, P1** | levs, width, xcenter, ycenter, blackblood | ✅ |
| 5 | `bloom.fx` | `bloom_t0p0_PS.hlsl` | PostBloom / P0 | offsets[12] | ✅ |
| 6 | `redhaze.fx` | `redhaze_t0p0_PS.hlsl` | PostHaze / P0 | horiz, glare | ✅ |
| 7 | `fade.fx` | `fade_t0p0_PS.hlsl` | PostFade / P0 | rad, fader, offsets[12] | ✅ |
| 8 | `water.fx` | `water_t0p0_PS.hlsl` | PostWater / P0 | horizon, delta, theta, rnd, puddle | ✅ |
| 9 | `trainblur.fx` | `trainblur_t0p0_PS.hlsl` | Blast / P0 | levs, width, xcenter, ycenter, facta, horizon | ✅ |
| 10 | `trainover.fx` | `trainover_t0p0_PS.hlsl` | Train / P0 | facta | ✅ |
| 11 | `bubble.fx` | `bubble_t0p0_PS.hlsl` | Bubble / P0 | width, xcenter, ycenter, mag, reddish | ✅ |
| 12 | `burnblur.fx` | `burnblur_t0p0_PS.hlsl` | Blast / P0 | levs, width, xcenter, ycenter, mag | ✅ |
| 13 | `comicblur.fx` | `comicblur_t0p0_PS.hlsl` | ComicBlur / P0 | left1,top1,right1,bottom1, left2,top2,right2,bottom2, a, speed | ✅ |
| 14 | `color.fx` | `color_t0p0_PS.hlsl` | PostColor / P0 | x, y, alpha | ✅ |
| 15 | `trail.fx` | `trail_t0p0_PS.hlsl` | PostTrail / P0 | tx, ty, alpha, r, g, b | ✅ |
| 16 | `newblood.fx` | `newblood_t0p0_PS.hlsl` | Blood / P0 | bright, statik, lineStatik | ✅ |
| 17 | `camsplat.fx` | `camsplat_t0p0_PS.hlsl` | CamSplat / P0 | red | ✅ |
| 18 | `ink.fx` | `ink_t0p0_PS.hlsl` | Ink / P0 | bright | ✅ |
| 19 | `wallblood.fx` | `wallblood_t0p0_PS.hlsl` | WallBlood / P0 | red, offsets[6] | ✅ |

Extra sources also in the manifest (distinct primitives, not loaded by the 19
call sites, kept from the earlier sketches): `Sprite.fx`, `SpriteShadow.fx`.
Both compile → the manifest's 21 total.

All shaders share the same `#if OPENGL`/`#else` guard set as the earlier
sketches (`SV_POSITION`/`SV_TARGET`/`vs_3_0`/`ps_3_0` under OPENGL) and a
pass-through sprite vertex shader (the originals are pixel-only; the XNA
SpriteBatch supplied the geometry).

### Embedded defaults

Where the game never writes a parameter, the original Xbox constant-table
default was embedded in the HLSL so the effect behaves as authored rather than
as an all-zero uniform:

* `bloom.offsets[12]`, `fade.offsets[12]` — the full 12×float2 normal-distribution
  offsets.
* `lense.offsets[6]`, `wallblood.offsets[6]` — the 6 unit-circle offsets.
* `poster` levels/alpha/fore, `grad` (bright=1.1 etc.), `bubble`, `burnblur`,
  `trainover`, `trail`, `color`, `ink`, `camsplat`, `newblood`.

## 2. Cross-check against the game's call sites

Source of truth: `managed/decompiled/game/projectDish/{Game1,GameRender,Strip}.cs`
(73 `Effect.Parameters[...]` sites + `CurrentTechnique.Passes[n]` uses).

| effect variable | game-loaded name | parameters the game writes | pass(es) used | .fx match |
|---|---|---|---|---|
| posterEffect | fx/poster | *(none)* | — | ✅ |
| lenseEffect | fx/lense | edgeBlur | Passes[0] | ✅ |
| gradEffect | fx/grad | gradFlip, rgrad, ggrad, bgrad, bright, burnmag, levs, xcenter, ycenter, width | Passes[0] | ✅ |
| effect | fx/blur | blackblood, xcenter, ycenter, levs, width | **Passes[0] and Passes[1]** | ✅ (2 passes) |
| bloomEffect | fx/bloom | *(none)* | Passes[0] | ✅ |
| hazeEffect | fx/redhaze | horiz, glare | Passes[0] | ✅ |
| fadeEffect | fx/fade | rad, fader | Passes[0] | ✅ |
| waterEffect | fx/water | puddle, horizon, delta, theta, rnd | Passes[0] | ✅ |
| trainEffect | fx/trainblur | xcenter, ycenter, levs, width, facta, horizon | Passes[0] | ✅ |
| trainOverEffect | fx/trainover | facta | Passes[0] | ✅ |
| bubbleEffect | fx/bubble | reddish, xcenter, ycenter, width, mag | Passes[0] | ✅ |
| burnEffect | fx/burnblur | mag, xcenter, ycenter, levs, width | Passes[0] | ✅ |
| comicBlurEffect | fx/comicblur | a, speed | Passes[0] | ✅ |
| colorEffect | fx/color | *(none)* | — | ✅ |
| trailEffect | fx/trail | *(none — passed to Character.Draw, never written)* | — | ✅ |
| bloodEffect | fx/newblood | bright, statik, lineStatik | Passes[0] | ✅ |
| camSplatEffect | fx/camsplat | red | Passes[0] | ✅ |
| inkEffect | fx/ink | bright | Passes[0] | ✅ |
| wallBloodEffect | fx/wallblood | red | Passes[0] | ✅ |

**Mismatches found and fixed in the pre-existing sketches** (these were silent
runtime failures — `Parameters["x"]` returns null for a name/type that does not
exist):

1. `Blur.fx` declared `float2 Center` and had no `xcenter`/`ycenter`/`levs`/pass P1.
   The game writes `xcenter`, `ycenter`, `levs`, `blackblood` and uses
   `Passes[1]` → rewritten as `blur.fx` with separate `xcenter`/`ycenter` and two
   passes.
2. `Ink.fx` declared `float Bright` (capital B); the game writes `"bright"`
   → rewritten as `ink.fx` with `bright`.
3. `Bloom.fx` declared `float2 Offsets` (capital O); renamed to `offsets`.
4. `Tint.fx` was a stand-in for `fx/color` but exposed `Alpha`/`ShadowColor`/
   `HighlightColor` instead of the original `x`/`y`/`alpha` → replaced by
   `color.fx`. (`Tint.fx` removed.)
5. `Sprite.fx` / `SpriteShadow.fx` are not game-loaded names and were kept as
   extras.

No remaining parameter-name, parameter-type, technique or pass-index mismatch
was found. Note `grad.width` is reflected as `int` in the original container but
the game calls `SetValue(float)`; it is declared `float` in `grad.fx` so the
value is consumed as a float, as the original shader does.

## 3. Packaging

Compiled output: `Dishwasher/Content/bin/Android/fx/<name>.xnb`
Staged (copied) to: `Dishwasher/Assets/fx/<name>.xnb`

`Assets/**` is auto-included as `AndroidAsset` (prefix stripped), and
`AndroidContentBootstrap.CopyAssetTree` extracts it to `files/content`, which is
the process CWD — so `ContentManager.Load<Effect>("fx/<name>")` resolves
`fx/<name>.xnb`. No `.csproj` change needed.

Staged files (verified: `XNBa` header, `MGFX` magic at offset 141):

| file | bytes | | file | bytes |
|---|---:|---|---|---:|
| `poster.xnb` | 4897 | | `bubble.xnb` | 4271 |
| `lense.xnb` | 6029 | | `burnblur.xnb` | 5761 |
| `grad.xnb` | 7715 | | `comicblur.xnb` | 5473 |
| `blur.xnb` | 7029 | | `color.xnb` | 3427 |
| `bloom.xnb` | 8737 | | `trail.xnb` | 5595 |
| `redhaze.xnb` | 6927 | | `newblood.xnb` | 4815 |
| `fade.xnb` | 7923 | | `camsplat.xnb` | 3753 |
| `water.xnb` | 5189 | | `ink.xnb` | 3207 |
| `trainblur.xnb` | 5717 | | `wallblood.xnb` | 4775 |
| `trainover.xnb` | 5109 | | | |

**All 19 runtime names present in `Assets/fx/`.**

## 4. Fidelity / assumptions

Interface fidelity is exact for all 19 (names, scalar/vector/bool/int types as
the game calls `SetValue`, technique names, pass counts). Body fidelity is high
for the shaders whose microcode is a straight tap/sum (blur P0, bloom, fade,
trail, lense, wallblood, comicblur, trainblur, burnblur, bubble). The following
branchy/predicate-heavy shaders were re-authored to the **observable** behaviour
of the XenosRecomp output and the game's inputs rather than a predicate-for-
predicate transcription; each carries a header comment noting this:

* `poster.fx` — the original folds a 5-tap cross through four thresholds; this
  uses a luminance posterize with the same level parameters. `poster` is
  **loaded but never driven** by the game, so there is no call site to match.
* `color.fx` — original branch ladder on `x`/`y`; this is a tint overlay. Also
  **loaded but never driven**.
* `ink.fx`, `camsplat.fx`, `newblood.fx`, `wallblood.fx` — original predicate
  chains reduced to threshold/edge masks with the same inputs; the red/alpha
  structure is preserved.
* `redhaze.fx`, `water.fx`, `grad.fx` (burn term), `trainover.fx`, `blur.fx`
  pass P1 (black-blood) — simplified but faithful accumulation/threshold.

No shader was left uncompiled or unassigned. The only thing not attempted is a
pixel-exact reproduction of the Xenos ALU predicate chains (they are not needed
to load, bind parameters, or run; visual tuning is a separate pass).

## 5. Trees touched

Written (allowed): `Dishwasher/Content/fx/*.fx` (19 runtime + 2 extras, and
removal of the superseded `Blur.fx`/`Bloom.fx`/`Ink.fx`/`Tint.fx`),
`Dishwasher/Content/Content.mgcb`, `scripts/env.sh`,
`Dishwasher/Content/bin/Android/fx/*.xnb`, `Dishwasher/Assets/fx/*.xnb`, this
note. No audio files, `Assets/sfx`, game `.cs` sources, APK, or read-only trees
were modified. The APK was **not** rebuilt or deployed.
