# Rectangle artifact — DXT5 alpha byte-order bug

**Author:** Sprite Artifact Specialist (subagent)
**Date:** 2026-10-02
**Device:** `<device-serial>` (adb `$ADB`), app `com.recomp.dishwasher`
**Scope:** edits confined to `<dev-workspace>/` (tools + content assets) and this notes tree.
Read-only trees (`managed/decompiled`, `assets-clean`, `port`, `rexglue-sdk*`,
`android-app`) were **not** modified.

**Result:** the rectangles are **gone**. Root cause was a content-export bug:
every **DXT5 (BC3)** texture was exported with a byte-swapped alpha block left in
the "natural" order, so its alpha channel decoded to a **checkerboard dither**
instead of 0. Every sprite drawn from a DXT5 atlas (characters, scene props,
blood/effects, HUD, maps) therefore carried a translucent **rectangular halo**.
Corrected by word-swapping the 8-byte alpha block in the exporter and
re-exporting all 77 DXT5 textures.

---

## 0. TL;DR

> The Xbox 360 stores the whole DXT5 payload as byte-swapped 16-bit words.
> `tools/xnb_v2.py` already swapped the 16-bit **RGB565 colour
> endpoints** (`be_endpoints=True`) but decoded the **8-byte alpha block**
> as-is. For a transparent background block (`00 ff …`), the unswapped decode
> yields `a0=0, a1=255` and a repeating index pattern → alpha
> `{0,51,153}` checkerboard, i.e. ~50 % opaque grunge covering the sprite cell.
> The game draws each sprite source cell 1:1, so the dither reads as a literal
> grey/black rectangle behind/around the object (most obvious on the black
> characters and on blood/effect sprites, and while moving/animating because
> the cell changes every frame).
>
> **Fix:** word-swap the alpha block too (`a0=byte1, a1=byte0`, index bytes
> `b3 b2 b5 b4 b7 b6`), re-export, re-stage. RGB is byte-identical; only alpha
> changes (verified 0 differing RGB pixels, 233 125 corrected alpha pixels on
> `head1` alone). Fully-transparent fraction on `head1` goes `0.11 → 0.75`.

---

## 1. What the artifact is and where it appears

Rectangular, axis-aligned translucent patches that sit **behind** (and around)
sprites. They appear:

* **Gameplay** — a dark rectangle around the player: the character is a clean
  black silhouette but is boxed in a dithered rectangle; the wall/floor have
  rectangular noise patches; the HUD health bar has a dithered rectangular
  halo. Visible on every moving frame (the cell changes as the animation
  advances).
* **Menu** — the whole background is a heavy dithered noise with rectangular
  patches behind the skull art, the "dish" ring and the option text; the
  `ink` post-overlay turns the dark dither patches into **red rectangles**.

The shapes are **axis-aligned even when the sprite is rotated**, which is the
first clue that it is the sprite *texture's alpha* (the whole source cell is
drawn) rather than a separate world-space shadow quad.

**Evidence captures** (`<dev-notes>/proof/render/`):

| file | scene |
|---|---|
| `rect-082-moveR.png` | **BEFORE** — café bench, player boxed in a dithered rectangle |
| `rect-095-lvlsel2.png` | **BEFORE** — SELECT LEVEL menu, full-screen dither + red patches |
| `rect-210-gp.png` … `rect-215-moveL.png` | **AFTER** — same café, idle + moving (clean) |
| `rect-204-lvlsel.png`, `rect-201-main.png` | **AFTER** — SELECT LEVEL / main menu (clean art) |
| `cmp-gameplay-beforeafter.png` | full-band before/after (same room) |
| `cmp-char-beforeafter.png` | character zoom before/after |
| `cmp-menu-beforeafter.png` | menu before/after |
| `cmp-moving-frames.png` | five AFTER moving frames, camera scrolls, no rectangles |
| `rect-proof-head1-alpha-BEFORE.png` / `-AFTER.png` | the `head1` atlas alpha over magenta |

---

## 2. Isolation experiments

| # | experiment | result | verdict |
|---|---|---|---|
| 1 | Decode `gfx/head1.xnb` with the **authoritative** `texture2ddecoder` BC3 implementation and diff its alpha against the shipped PNG | **0 differing pixels / 262 144** | the export is a correct *standard* BC3 decode of the bytes → the bytes themselves carry the dither |
| 2 | Word-swap the 8-byte alpha block (16-bit words), decode, inspect a known-transparent top-row block | alpha `= 0` for all 16 pixels | finds the real encoding |
| 3 | Apply the word-swap to all 77 DXT5 textures; count fully-transparent fraction | `head1 0.11→0.75`, `torso1 0.12→0.90`, `legs1 0.12→0.90`, `weapon1 0.11→0.75`, `sprites 0.10→0.55`, `items 0.12→0.81`, `jtext 0.09→0.81`, `comictext 0.09→0.72`, `bubbles 0.06→0.55`, `maps1 0.06→0.37` | consistent across every DXT5 atlas |
| 4 | Diff RGB before/after the word-swap on `head1` | **0 differing RGB pixels**, 233 125 differing alpha pixels | the change is purely alpha; colour was already right |
| 5 | On-device A/B (before = dithered export, after = corrected export), same room/menu | rectangles present → absent | fix confirmed at runtime |

### 2.1 Prime suspects that were refuted

* **Texture-atlas bleed / filtering (suspect #1).** No `GraphicsDevice.SamplerStates`
  / `SamplerState` override exists anywhere in `GameSource/` (grep = 0 hits);
  MonoGame's `SpriteBatch` default is `LinearClamp`. Bleed from a neighbouring
  atlas cell would be a 1-pixel fringe, not a full-cell rectangle, and it would
  rotate with the sprite — the artefact is axis-aligned and cell-sized. The
  exported PNGs preserve the atlas layout (dimensions verified 125/125).
* **Shadow / tint / silhouette quads (suspect #2).** `Sprite.fx`/`SpriteShadow.fx`
  are not among the 19 `Content.Load<Effect>` sites in `Game1.cs:551-571` and are
  never loaded. The rectangle tracks the sprite's own DXT5 source cell
  (`Character.cs:4327/4365/4393` draw `value = 85×85` cells of
  `legsTex/torsoTex/headTex`); a separate shadow quad would not.
* **Straight-vs-premultiplied alpha (suspect #3).** The `Color`-format textures
  (`heart`, `text`, `pickups`, `arial`) decode to clean bimodal alpha
  (`heart` a0 = 0.54) and render cleanly; only the DXT5 atlases show the dither.
  A premultiply mismatch would darken soft edges, not fill the transparent
  background of a cell.
* **Trail / afterimage quads (suspect #4).** `Character.drawTrail()` is empty in
  both the original and the port, and the rectangle is present on the very first
  static gameplay frame (e.g. `rect-210-gp.png`), not only during motion.

### 2.2 On-device menu RT isolation (diagnostics modes 40–43)

Added to reuse the existing harness: **mode 40 = show `refractTarg`, 41 =
`lenseTarg`, 42 = `rTarg[cur]`, 43 = `mapTarg`** (volume keys). `refractTarg`
dumped to a flat white (the ink overlay could only contribute red from dark
input), while `lenseTarg`/`rTarg` carried the menu art plus the DXT5 dither, i.e.
the red rectangles are the `ink` overlay reading the **dithered DXT5 menu art**.
This pointed at the texture alpha, not the shaders, before the decoder test.

---

## 3. Root cause

`tools/xnb_v2.py :: decode_dxt5` (pre-fix, lines 149-185):

```python
a0, a1 = block[0], block[1]                 # <-- alpha endpoints NOT swapped
abits  = int.from_bytes(block[2:8], "little")
ccols  = _decode_color_block(block, 8, be_endpoints, big_endian_index)  # colour DID swap
```

The Xbox 360 DXT5 payload is byte-swapped **16-bit words**. The colour endpoints
were already handled (`be_endpoints=True`), but the 8-byte alpha block was read
in file order. For the canonical transparent block `00 ff 92 49 49 24 24 92`
this gives `a0=0, a1=255` and interpolated alpha `{0,51,153}` per pixel — a
checkerboard. Because the whole cell is drawn (`Character.Draw(... value ...)`
with `value = new Rectangle(partIcon % 6 * 85, partIcon / 6 * 85, 85, 85)`), the
checkerboard is composited over the scene as a translucent grunge rectangle.

This was **verified against the authoritative decoder**: the standard BC3
decode of the same bytes matches the shipped PNG exactly, which is exactly why
it looks "plausible" — the bug is an endianness bug in the source data, not a
decoder crash.

Affected assets: **all 77 DXT5** textures — `gfx/sprites`, `gfx/head1..11`,
`gfx/torso1..15`, `gfx/legs1..12`, `gfx/weapon1..16`, `gfx/items`, `gfx/eckto`,
`gfx/bubbles`, `gfx/comictext`, `gfx/jtext`, `gfx/maps/maps1..6`. The 45
`Color` and 3 `Dxt1` textures were already correct.

---

## 4. Fix applied

**`tools/xnb_v2.py`** — `decode_dxt5` now word-swaps the alpha block
(new `be_alpha: bool = True`), matching the colour-endpoint endianness:

```python
if be_alpha:
    # swap bytes within each 16-bit word of the 8-byte alpha block:
    #   b0 b1 b2 b3 b4 b5 b6 b7  ->  b1 b0 b3 b2 b5 b4 b7 b6
    a0, a1 = block[1], block[0]
    abits = int.from_bytes(
        bytes((block[3], block[2], block[5], block[4], block[7], block[6])),
        "little")
else:
    a0, a1 = block[0], block[1]
    abits = int.from_bytes(block[2:8], "little")
```

**Re-exported** all textures and **re-staged** the PNGs to the runtime asset
tree:

```sh
cd tools
python3 export_xnb_textures.py \
  --assets $DISHWASHER_ASSETS_DIR \
  --out    src/Dishwasher/Content \
  --manifest <dev-notes>/content-export-manifest.json
# -> xnb total 153 | textures 125 | spritefonts 3 | effects skipped 25 | errors 0

# runtime reads the AndroidAsset tree -> keep Assets/gfx in sync with Content/gfx
cd src/Dishwasher/Content/gfx
find . -type f \( -name '*.png' -o -name '*.json' \) \
  -exec cp --parents {} ../../Assets/gfx/ \;
```

No shader, `MatrixTransform`, technique/pass name, parameter name/type,
`targetSdkVersion`, or game logic was touched. The `fx/*.xnb` recompile was
**not** needed (textures only).

---

## 5. Before / after evidence

| | BEFORE (dithered DXT5 alpha) | AFTER (word-swapped alpha) |
|---|---|---|
| café, player | `rect-082-moveR.png` — player boxed in a rectangle, wall/floor tiled noise | `rect-210-gp.png`, `rect-213-moveR.png` — clean silhouette, clean room |
| SELECT LEVEL menu | `rect-095-lvlsel2.png` — dither + red patches | `rect-204-lvlsel.png` — clean "SELECT LEVEL / DEAD SAMURAI" art |
| main menu | `rect-091-main.png` | `rect-201-main.png` |
| character zoom | `cmp-char-beforeafter.png` (left) | `cmp-char-beforeafter.png` (right) |
| full band | `cmp-gameplay-beforeafter.png` (top) | `cmp-gameplay-beforeafter.png` (bottom) |
| menu | `cmp-menu-beforeafter.png` (left column) | (right column) |
| moving | `rect-080/081/082-moveR.png` | `cmp-moving-frames.png` (5 frames, camera scrolls) |
| atlas proof | `rect-proof-head1-alpha-BEFORE.png` | `rect-proof-head1-alpha-AFTER.png` |

**`rect-proof-head1-alpha-BEFORE.png` vs `-AFTER.png`:** the before image is a
magenta/black checkerboard across every cell (wrong partial alpha); the after
image is clean magenta (fully transparent) with crisp black silhouettes, skulls
and coloured figures.

Other fixed builds did not regress: the splash/boot (`rect-200-splash.png`),
main menu, play-mode and difficulty screens all render cleaner versions of the
same art; audio cues keep loading (`logcat` `[audio] loaded 'sfx/wav/...'`);
`MatrixTransform` and the `fade` fix are untouched; on-device input navigation
still reaches gameplay.

---

## 6. Build / deploy (verified)

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true
# -> Build succeeded. 0 Error(s) (1 pre-existing XA1008 warning)
adb -s <device-serial> install -r -d bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
adb -s <device-serial> shell am force-stop com.recomp.dishwasher && adb -s <device-serial> logcat -c
adb -s <device-serial> shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
```

APK contains the corrected atlas: `assets/gfx/head1.png` md5
`e4374dc854a34d76cdee0292826cc5f9` =
`Dishwasher/Assets/gfx/head1.png`.

---

## 7. Remaining occurrence / notes

* **No residual rectangle artifact was observed** in the captured menu or
  gameplay scenes after the fix. The translucent rectangles that remain in the
  menus are the game's **intended option backdrops** (dark boxes behind the
  option text) and the bottom blood/vignette gradient.
* **Diagnostics scaffolding (additive, revertable):** modes 40–43 were added to
  `Platform/RenderDiagnostics.cs` + a `TEMP RENDER DIAGNOSTICS` block at the end
  of the menu `case 1` in `Game1.cs`. They are inert at mode 0 and can be deleted
  with the rest of the harness.
* **Not re-verified end-to-end:** special-sequence effects (`trainover`,
  `burnblur`, `comicblur`, `redhaze`, `water`) were not sampled; their textures
  are corrected by the same export, so their alpha is now right, but their
  shader bodies remain observable-behaviour re-authors (unchanged here).
* The `maps/*.zdx` raw tile data is untouched; `gfx/maps/maps.zdx` remains an
  extra file in `Assets/gfx/maps/` (not a texture).
