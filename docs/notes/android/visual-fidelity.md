# Visual fidelity — reference match + motion "blur"

**Author:** Visual Fidelity Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (adb `$ADB`), app `com.recomp.dishwasher`
**Scope:** edits confined to `src/Dishwasher/` and this notes tree. Read-only
trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.
**Reference (ground truth):** `android/notes/reference/dishwasher-reference.jpg`
(750×422).

---

## 0. TL;DR

1. **The "blur" was the `fade` effect.** The game calls
   `drawFade(0.015, 0)` (fader **0**) for every fade, but the re-authored
   `fade.fx` mapped fader 0 to an **opaque** blurred copy of the scene
   (`alpha = 1.0`). The theme then composites `fadeTarg` **additively** onto the
   scene and again each frame, so every frame carried a **full-screen 128×128
   13-tap blurred double-image** at ~80 % strength. That is the soft
   ghost/haze the user sees; it is most obvious around moving, high-contrast
   sprites. Decoding the original microcode
   (`fade_t0p0_PS.hlsl`) shows fader 0 should be the **averaged** blur with a
   **centre-transparent radial alpha mask** `max(2·d−0.25, 0)`, not alpha 1.
   **Fixed** in `Content/fx/fade.fx`.
2. **Same-scene measurement of the fade contribution (interleaved median of 5
   captures each):** buggy **ΔL = +23.3** (ΔRGB +28,+22,+20) → fixed
   **ΔL = +8.8** (ΔRGB +11,+8,+7). The "skip fade" (mode 21) baseline was
   **identical** in both builds (L 55.3, RGB 87,41,43), so the A/B is clean.
3. **The `DiscardContents` lead is disproven.** All 10 render targets are
   explicitly `GraphicsDevice.Clear()`ed before every use, and an empirical
   `DiscardContents → PreserveContents` swap changed nothing (moving-frame
   background edge-energy ratio **0.985**). There is no persistence-dependent
   afterimage to break.
4. **No explicit motion trail exists to be wrong:** `Character.drawTrail()` is
   empty in *both* the original decompile and the port, and the `trail` effect's
   parameters are never set (default `alpha = 0` ⇒ invisible).
5. **After the fade fix the port matches the reference on the defining traits**
   that can be measured across different scenes: overall luminance
   (ref 74.9 → port 81.3), vignette (corner/centre ref 0.51 → port 0.41),
   pure-black characters (ref 5th-pct L 1 → port 0), and blood saturation
   (mean S 181 → 182). Residual gaps are the background **saturation/hue**
   (port ≈ +30 %, walls pink vs grey-brown) and **highlight bloom**, both
   consistent with the reference being a different (more neutral) level than
   the red-lit Foghorn Café; see §4.

---

## 1. Reference traits (measured)

All numbers from `android/notes/reference/dishwasher-reference.jpg`:

| trait | measurement |
|---|---|
| mean luminance (L) | **74.9** |
| vignette (mean L of 4 corner boxes / centre box) | **0.511** |
| radial L centre→corner (6 bins) | **124, 79, 63, 79, 79, 56** |
| top-10 % / middle L | **0.879** (top darker) |
| bottom-10 % / middle L | **0.147** (very dark) |
| mean saturation (HSV S, V>30) | **0.342** (desaturated) |
| wall facade region RGB / S | **(117, 85, 91) / 0.298** (grey-brown) |
| near-black (L<10) fraction | 0.095 (5th-pct L = **1**) |
| red-blood pixels (H≤20/≥235, S>150, V>70) | 2.49 % of frame, mean **S=181, V=81** |
| brightest pixel (p99.9 / max L) | 198 / **217** (highlights do **not** reach white) |

---

## 2. Problem 2 — the "blur" while moving

### 2.1 Mechanism (with evidence)

The gameplay post chain is
`scene → lense → additive bloom/fade/foreLights → grad`. The `fade` step is:

```csharp
// Game1.cs:4001-4003,4069-4104
SetRenderTarget(fadeTarg); Clear(Black); drawFade(0.015f, 0);   // fader == 0
...
sprite.Draw(fadeTarg, getDRect(), new Color(1f, 0.8f, 1f, 0.8f)); // Additive
```

`fadeTarg` is a **128×128** downsample of the whole scene run through a 13-tap
blur. The buggy `fade.fx` returned that blur with `alpha = 1.0`, so under
`BlendState.Additive` (src·src.a + dst) each frame added `0.8 × blurred-scene`
on top of the sharp scene — a soft, low-resolution duplicate of the entire
frame. On a moving, high-contrast character the duplicate reads as a **blur /
ghosting halo**. This is a per-frame spatial haze (not a temporal trail), which
is why it is present whenever the game runs and is most visible on motion.

**Microcode decode (decisive).** In `fade_t0p0_PS.hlsl`
(`notes/shaders/decompiled/`), constants are
`c253=(-0.1, 2.0)`, `c254=(0, 1, 1/13, -0.5)`, `c255=(0.1154, 0.15, 1.5, -0.25)`
and the branch tests **`fader == 1`**; both other values (0 and 2) fall to the
same path:

| fader | rgb (correct) | alpha (correct) |
|---|---|---|
| `== 1` | `sum × 0.1154` | `max(1.5·d − 0.1, 0) × 0.15` |
| `!= 1` (0 and 2) | `sum / 13` (averaged blur) | `max(2.0·d − 0.25, 0)` |

The game only ever calls `drawFade(…, 0)` (three call sites: `Game1.cs:3576`,
`:3642`, `:4003`; the `fader` argument is a literal `0`). The previous
re-author instead emitted `float4(blur, 1.0)` for `fader==0`, the `2.0/−0.25`
mask for `fader==1`, and the `sum×0.1154` path for `fader==2` — **shifted by
one**. Fixed to the table above.

### 2.2 Experiments that excluded the other hypotheses

| hypothesis | test | result |
|---|---|---|
| **Intended afterimage trail** (`trail.fx`) | `Character.drawTrail` is empty in the original **and** the port; `trailEffect` is never parameterised, defaults `tx=ty=0, alpha=0` | no trail is ever drawn → not the mechanism |
| **RT ghosting** (`DiscardContents`) | all 10 RTs are `Clear()`ed before use; swapped all to `PreserveContents`, recorded moving frames, measured background edge energy | Discard = 8.294, Preserve = 8.173, **ratio 0.985** → inert |
| **Global spatial motion blur** | fixed build, camera-static room, moving frames vs idle frames, background edge energy | idle = 10.835, moving = 10.929, **ratio 1.009** → background is sharp |
| **Wrong radial `blur`/`trainblur`/`comicblur`** | call sites: `fx/blur` P0 only in intro (`drawTarg`) / comic (`drawComicTarg`); `trainblur`/`burnblur`/`comicblur` only special sequences | not reached in normal gameplay |

### 2.3 Fix applied

`Dishwasher/Content/fx/fade.fx`, `MainPS` (MatrixTransform, technique `PostFade`,
pass `P0` and all parameter names/types unchanged):

```hlsl
float4 outc;
if (fader == 1)
{
    float dist = length(uv - 0.5);
    outc = float4(sum * 0.1154, max(1.5 * dist - 0.1, 0.0) * 0.15);
}
else                     // fader 0 and 2
{
    float dist = length(uv - 0.5);
    outc = float4(blur, max(2.0 * dist - 0.25, 0.0));
}
return outc * input.Color;
```

Corrected backup kept at `android/notes/fx-fade-fixed.bak`.

---

## 3. Problem 1 — reference vs port trait table

Port frames are the 1080×750 game band (rows 336–1086 of the device
screenshot). "Pre-fix" = buggy `fade`; "post-fix" = corrected `fade`.

| trait | how measured | REFERENCE | PORT pre-fix | PORT post-fix | verdict |
|---|---|---|---|---|---|
| overall luminance | mean L | 74.9 | **112.4** | **81.3** | ✅ fixed — now close |
| vignette | corner/centre L | 0.511 | 0.500 | 0.410 | ✅ present (port a little stronger) |
| radial falloff | L centre→corner (6 bins) | 124→56 | 157→80 | 144→59 | ✅ present |
| bottom gradient | L bottom10 %/middle | 0.147 | 0.069 | ~0.01–0.05 | ✅ present |
| top gradient | L top10 %/middle | 0.879 | 1.43 | 1.12–1.32 | ⚠️ port top brighter (scene) |
| background saturation | mean HSV S (V>30) | 0.342 | 0.485 | **0.446** | ⚠️ port ≈ +30 % |
| wall region | mean RGB / S | (117,85,91) / 0.298 grey-brown | — | **(204,123,129) / 0.409** pink | ⚠️ residual (theme) |
| character black level | 5th-pct L | 1 | 3 | **0** | ✅ pure black |
| blood saturation | mean S of red pixels | 181 | 179 | **182** | ✅ matched |
| blood brightness | mean V of red pixels | 81 | 111 | 122 | ⚠️ port brighter |
| highlight bloom | fraction V>240 | **0.000** (max L 217) | 0.244 | 0.05–0.10 | ⚠️ port highlights clip |

**Reading:** the fade fix moved the port from a washed-out +50 % brightness to
within ~8 % of the reference, and the reference's defining dark traits
(vignette, bottom gradient, pure-black characters, red blood) are all present
after the fix. The remaining deltas are **hue/saturation and highlight
headroom**, not missing contrast/vignette. The reference frame is a darker,
more neutral-lit level than the red-lit Foghorn Café, so part of the residual
is content; a global desaturation/tone change was deliberately **not** added
(it would mis-grade every level and cannot be justified from a different
scene).

**Which effect produces the reference's vignette/darkening?** The `grad`
full-screen composite. Its microcode computes `burn = burnmag · |uv−0.5|` and
does `col = (acc + grd)·bright − burn` — a radial edge darkening (the vignette)
plus a vertical `grd = t /(rgrad,ggrad,bgrad)` gradient. Our `grad.fx` already
implements this faithfully (burn centred on the screen, matching the microcode,
not the blast centre), and the measured radial profile confirms it is active
and comparable to the reference. It is **not missing**.

---

## 4. Effects corrected / not corrected

| effect | action | basis |
|---|---|---|
| `fade.fx` | **corrected** fader-branch decode (fader 0 ⇒ averaged blur + centre-transparent mask) | microcode `fade_t0p0_PS.hlsl`; measured fade ΔL +23.3 → +8.8 |
| `grad.fx` | left as-is (verified faithful: burn + gradient + bright) | microcode; radial profile matches reference |
| `bloom.fx`, `ink.fx` | left as-is (faithful re-transcriptions from the prior colour-grade pass) | prior work |
| `blur`, `trainblur`, `comicblur`, `burnblur` | left as-is (not reached in normal gameplay; radial blur is intentional on blasts) | call-site audit |
| `lense.fx` refraction | left as-is; noted as an observable-behaviour re-author (original adds a `G·0.15` displacement term and uses ±0.003/0.15; ours uses ±0.002/0.6) | microcode; invisible in the café (no refract sprites) — ranked residual |
| `DiscardContents` | **left at the original value** (Preserve swap reverted) | all RTs cleared; Preserve/Discard ratio 0.985 |

---

## 5. Build / deploy (final)

```sh
source tools/scripts/env.sh
cd src/Dishwasher/Content
mgcb Content.mgcb /platform:Android /outputDir:bin/Android /intermediateDir:obj/Android
# -> Build 21 succeeded, 0 failed.
cp bin/Android/fx/*.xnb ../Assets/fx/
cd ..
dotnet build -f net8.0-android -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 -p:EmbedAssembliesIntoApk=true
# -> Build succeeded.  0 Error(s)  (1 pre-existing warning XA1008 targetSdk 35 vs API 34)
adb install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb shell am force-stop com.recomp.dishwasher && adb logcat -c
adb shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
```

Packaged shader verified: `assets/fx/fade.xnb` in the APK =
`Content/bin/Android/fx/fade.xnb` = `Assets/fx/fade.xnb`, sha1
`6b9b61841c09c61eb23e0c93dfd6b541a82b62de`.

`targetSdkVersion=35` kept; `MatrixTransform`, technique `PostFade`, pass `P0`
and every parameter name/type are unchanged.

---

## 6. Before / after evidence (`<dev-notes>/proof/render/`)

| file | what |
|---|---|
| `vf-fade-before-after.png` | **before/after side-by-side**, same tutorial room |
| `vf-BUGGY-gp2.png` | BEFORE — buggy `fade` (opaque blob) |
| `vf-018-gp.png` | AFTER — corrected `fade` |
| `vf-ref-vs-port.png` | **reference vs port** side-by-side (post-fix) |
| `vf-001-resumed.png` | pre-fix cafe gameplay (washed pink, mean L 112) |
| `vf-031-cafe.png` | post-fix cafe gameplay (mean L 81) |
| `vf-iso2-m0-*.png` / `vf-iso2-m21-*.png` | fixed build, interleaved fade-on/off isolation (5 each) |
| `vf-bug-iso-m0-*.png` / `vf-bug-iso-m21-*.png` | buggy build, same isolation (5 each) |
| `vf-final-moving-fixed.png` | consecutive moving frames, fixed build (camera static, character sharp) |
| `vf-final-moving-preserve.png` | same scene, `PreserveContents` build (identical) |
| `vf-before-lp.png` / `vf-after-lp.png` | longpress-move A/B (input works) |

Fade-contribution isolation (same scene, interleaved, median of 5):

| build | m0 (fade on) | m21 (fade off) | fade contribution |
|---|---|---|---|
| buggy | L 78.6, RGB (115,63,63) | L 55.3, RGB (87,41,43) | **ΔL +23.3**, ΔRGB (+28,+22,+20) |
| fixed | L 64.0, RGB (99,49,50) | L 55.3, RGB (87,41,43) | **ΔL +8.8**, ΔRGB (+11,+8,+7) |

---

## 7. Residual gaps (ranked)

1. **Background saturation / hue (high, but likely content).** Port ≈ +30 %
   saturation and red-shifted walls vs the reference's grey-brown. The café's
   theme-0 lighting (`rGrad 9 / gGrad 16`) plus the additive theme fade
   `(1, 0.8, 1, 0.8)` are the drivers; both are game data, faithfully applied.
   Tuning should be per-level theme data, not a global shader grade.
2. **Highlight headroom (medium).** Port has 5–10 % near-white pixels; the
   reference caps at L 217. The fade fix already reduced the added haze; the
   rest comes from `grad.bright = getBrightVal()+0.2 = 1.1` (game code) and the
   additive bloom, both faithful. Would need tone-mapping / a per-level
   decision.
3. **Top-of-frame gradient (low).** Port top is brighter than the reference;
   follows from the café's bright upper wall vs the reference's dark sky —
   scene composition, not a shader.
4. **`lense.fx` refraction approximation (low).** Structural re-author
   (±0.002/×0.6 vs the microcode's ±0.003/×0.15 plus a `G·0.15` term); invisible
   in the sampled café (no refract sprites) but a true pixel-difference
   elsewhere.
5. **`wallblood`, `newblood`, `camsplat`, `redhaze`, `water`, `trainover`,
   `bubble`, `burnblur`, `comicblur`, `poster`, `color`** remain
   observable-behaviour re-authors (none affected the two reported problems in
   the sampled level).
6. **Device/display geometry.** The Activity stays in portrait
   (`wm size 1080×2400`, rotation 0) and MonoGame renders the 1280×720 scene
   into a 1080×750 band; the aspect is not 16:9. Out of scope for this task
   (geometry was declared correct) but worth a separate look.

---

## 8. Trees touched / revert notes

* **Written:** `Dishwasher/Content/fx/fade.fx` (fader-branch fix),
  `Dishwasher/Content/bin/Android/fx/*.xnb`, `Dishwasher/Assets/fx/*.xnb`
  (recompiled/staged), this note, and the captures under
  `<dev-notes>/proof/render/vf-*`. A copy of the corrected shader is at
  `android/notes/fx-fade-fixed.bak`.
* **Temporary experiment fully reverted:** the 10
  `RenderTargetUsage.PreserveContents` edits in
  `GameSource/projectDish/Game1.cs` were set back to the shipping
  `DiscardContents` (verified: 10 `DiscardContents`, 0 `PreserveContents`).
  The temporary buggy `fade` branch (to produce the BEFORE captures) was
  restored from backup and no `TEMP EXPERIMENT` markers remain.
* No read-only tree, audio asset, or `targetSdkVersion` was changed;
  `MatrixTransform` / technique / pass / parameter names are intact.
