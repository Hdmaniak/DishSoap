# Comic colour cast — root cause, fix, verification

**Date:** 2026-10-03
**Scope:** decoder fix in `tools/xnb_v2.py`, `tools/export_xnb_textures.py` and
`src/Dishwasher/Platform/Import/XnbV2.cs` (and the same files in the working port's
`<dev-workspace>/` tree). Read-only trees (decompiled game source, cleaned assets) were not
modified.

> This is a sanitized copy of the working note (device serials, LAN addresses and
> absolute workspace paths removed). The proof captures referenced below stay in the
> development tree; they are not part of this repository.

**Status:** diagnosed, fixed, re-exported, rebuilt, and verified on device with
before/after measurements. Menus verified.

---

## 0. TL;DR

The comic panels rendered **blue-lavender** because the XNB decoder read the
Xbox-360 raw 32bpp `SurfaceFormat.Color` surface in the **wrong byte order**.

* The 360 is **big-endian**, so `D3DFMT_A8R8G8B8` (the packed word `0xAARRGGBB`) is
  stored MSB-first as file bytes **`A,R,G,B`**.
* The decoder used a little-endian `B,G,R,A` order. That read the **alpha byte as
  blue** and the real blue as alpha, so every soft/partial-alpha panel pixel came out
  blue.
* **This was a decode-order bug, not a runtime desaturation.** The comic draw path
  draws the panel with `Color.White` and `BlendState.NonPremultiplied` and applies
  **no shader**; nothing tints the panel at runtime.
* Fixed in the Python reference (`xnb_v2.py`) **and** the C# port used by the public
  build (`Platform/Import/XnbV2.cs`) — the two are **byte-identical** on the raw path
  (SHA-256 cross-check below). Re-exported all textures and re-staged `Assets/gfx/**`.

---

## 1. Symptom and prior state

* Reference (YouTube longplay): panel art is **neutral greyscale**, dark neutral
  background, yellow speech bubbles.
* Port: panel art strongly **blue/lavender**.
* An earlier note recorded this as an unresolved residual and attributed it to the
  backdrop brightness / "source art genuinely contains blue-lavender tones". That
  conclusion was wrong — see §3.

---

## 2. Ground truth for one panel texture

`gfx/panels/panel1.xnb` is `SurfaceFormat.Color` (format id 1), 1040×213 raw 32bpp,
exactly `85 + 1040·213·4 = 886165` bytes. The only question for a raw surface is the
byte order. Measured means over the whole texture (including transparent pixels):

| decode | R | G | B | B−R |
|---|---|---|---|---|
| `B,G,R,A` (**old**) | 68.5 | 73.6 | **242.4** | **+174** |
| `R,G,B,A` | **242.4** | 73.6 | 68.5 | −174 |
| `A,R,G,B` (**correct**) | 73.6 | 68.5 | 70.3 | **−3.3** |

The first two cannot be grey (they only swap R↔B); the third is neutral.

### 2.1 The alpha byte is byte 0 (not byte 3)

Across **every** raw `Color` texture, two independent facts pin the order:

* `gfx/arial` (the game's custom text atlas) has the tuple `(0,255,255,255)` as its
  majority (103,775 px). Under `A,R,G,B` that is a **transparent white** background
  (A=0) — correct. Under `B,G,R,A` it is an **opaque yellow** background — wrong.
* `gfx/text` and `gfx/heart` glyph tuples are `(255,209,209,209)`, `(255,155,155,155)`,
  i.e. `b0` = alpha and `b1=b2=b3` = grey. Under `A,R,G,B` these are grey glyphs with
  the correct alpha.

So byte 0 is the alpha; bytes 1..3 are R,G,B.

### 2.2 External ground truth

* **Reference comic:** neutral greyscale throughout. `panel1.xnb` decoded `A,R,G,B`,
  composited over black, is neutral greyscale line art plus a **red** "CAFE" sign
  (under `B,G,R,A` it is blue). "Red stays red" is confirmed by the red level-title
  banner in the same clip, which is red only if byte 1 is R.
* **XNA "DREAM BUILD PLAY" splash** (`gfx/dbp.xnb`, raw `Color`): under `A,R,G,B` it
  is the real XNA Game Studio splash — neutral DREAM/PLAY and an **orange BUILD**
  (dominant distinct colour `(224,64,0)`) which only makes sense if byte 1 = R. Under
  `B,G,R,A` BUILD is blue.
* **Control-guide art** (`gfx/controls.xnb`, raw `Color`): under `A,R,G,B` it is the
  natural **cream** guide with a real controller; under `B,G,R,A` the whole screen is
  blue.

All 45 raw textures share this one order; there is **no per-texture exception**.

### 2.3 Independent implementation cross-check

`tools/xnb_v2.py::decode_texture(..., raw_order="argb")` and the C# port
`Platform/Import/XnbV2.cs::DecodeTexture` were compared by decoding the same files and
hashing the resulting RGBA8888 buffer:

| file | C# `rgba_sha256` | Python `rgba_sha256` |
|---|---|---|
| `gfx/panels/panel1.xnb` | `b4871357…b801` | `b4871357…b801` |
| `gfx/dbp.xnb` | `9822f8da…864b` | `9822f8da…864b` |
| `gfx/text.xnb` | `45271f3d…f54` | `45271f3d…f54` |

Byte-identical → the public build's on-device derivation matches the internal bundled
PNGs.

---

## 3. Why the earlier BGRA conclusion was wrong

The earlier note validated `B,G,R,A` using `gfx/dbp.xnb`, claiming the correct splash
is "periwinkle-blue with a green BUILD". That is **not** the XNA Game Studio 3.0
splash. The real splash (and the `A,R,G,B` decode of `dbp.xnb`) has an **orange
BUILD**. The DXT-side endianness findings were correct and remain untouched: the DXT
RGB565 endpoints are big-endian and the DXT5 alpha block is word-swapped. The **raw
`Color` path was the single place that had been decoded little-endian**; the whole
platform is big-endian, so `A,R,G,B` is the consistent answer.

---

## 4. Fix (files / sites)

| file | change |
|---|---|
| `tools/xnb_v2.py` | `decode_texture`: default `raw_order="argb"`; file byte 1→R, 2→G, 3→B, 0→A. `bgra`/`rgba` kept only for A/B comparison. |
| `tools/export_xnb_textures.py` | both call sites `raw_order="bgra"` → `"argb"`; docstring corrected. |
| `src/Dishwasher/Platform/Import/XnbV2.cs` | `DecodeTexture` raw path: `o[i+0]=raw[i+1]; o[i+1]=raw[i+2]; o[i+2]=raw[i+3]; o[i+3]=raw[i+0]`; header/summary comments corrected. |
| `Dishwasher/Content/gfx/**` | re-exported (125 textures + 3 SpriteFont atlases). |
| `Dishwasher/Assets/gfx/**` | re-staged (runtime asset tree). |

The public build derives textures on-device through `Platform/Import/ContentImporter.cs`
(`DecodeTexture`, changed) → `PngWriter` (RGBA, unchanged), so it inherits the fix.

**Blast radius (verified by hash):** re-export changed exactly **48 PNGs** — the 45
raw `Color` Texture2D plus the 3 SpriteFont atlases. All **80 DXT1/DXT5** PNGs are
**byte-identical** (the DXT5 alpha fix is untouched).

---

## 5. Before / after measurements

Metric over the comic's central band (excludes the black top/bottom margins), sampled
every 2 px. `blueFrac` = fraction of pixels with `B > max(R,G) + 20`.

| frame | mean RGB | B − R | blueFrac |
|---|---|---|---|
| reference | (41.3, 38.5, 28.3) | −13.0 | **0.001** |
| port **before** | (67.1, 63.6, 61.5) | −5.6 | **0.200** |
| port **after** | (60.8, 58.4, 47.9) | −13.0 | **0.002** |

Key result: the comic's blue-pixel fraction collapses **0.200 → 0.002**, matching the
reference's **0.001**, and the panel's B−R now equals the reference (−13.0).

---

## 6. Public build inheritance and the re-import requirement

The **public build inherits this fix**: `Platform/Import/XnbV2.cs` is compiled into the
public APK, and its `DecodeTexture` raw path now produces the same bytes the Python
exporter and the internal bundled PNGs produce (§2.3). Verify the shipped binary by
decompiling `DecodeTexture` from the built assembly, or by re-running the cross-check.

**A device that already imported with the old decoder must re-import.** The importer
skips derived content that already exists (it stamps `content.stamp` and does not
overwrite), so the old, blue-decoded PNGs will persist after an in-place app update.
To pick up the correction on such a device, re-import — either clear the app's derived
content (settings → app storage, or reinstall) and re-run the first-run import, or
otherwise force the importer to regenerate. The derived-content stamp path itself was
not modified.

---

## 7. Menus and non-regression

* The **Single Player** and **SELECT LEVEL** screens now show the graffiti art
  **black/neutral** instead of blue, and the crossed cleavers white.
* Font atlases (`Arials`, `SpriteFont1_JPN/CHT`) are now white-on-transparent (the
  `B,G,R,A` decode gave their soft edges a yellow tinge); menu/HUD text renders
  normally.
* Untouched and re-verified: **DXT5 alpha fix** (all 80 DXT PNGs byte-identical),
  `MatrixTransform`, `fade.fx`, straight-alpha `BlendState.NonPremultiplied`, lense
  sampler, landscape/rotation/immersive, widescreen, intro, autosave, Android
  Settings, FPS counter/limiter, LAN co-op, music/SFX.

---

## 8. Residual / follow-ups

* **Brightness, not hue.** The fixed comic is still a little brighter than the
  reference (band mean ~58 vs ~41); this is the pre-existing grade/brightness gap and
  is unrelated to the colour cast.
* Older notes (`content-conversion.md`, `rectangle-artifact.md`) previously described
  the old `B,G,R,A` order; `content-conversion.md` now carries a correction banner.
