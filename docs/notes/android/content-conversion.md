# Content Conversion — XNB v2 (XNA GS 3.0 / Xbox 360) → MonoGame Android

**Owner:** Content Pipeline Specialist (subagent)
**Date:** 2026-10-02
**Scope:** read every `.xnb` in `assets-clean`, decode the 125 `Texture2D` and the 3
`SpriteFont`, export PNGs under `src/Dishwasher/Content/`, and decide the runtime
loading strategy. Effects (25 `EffectReader`) are **out of scope** (shader agent).

This document separates **measured** results from **assumptions**. Every decode
decision below was verified against either the decompiled XNA 3.0 framework (which is
authoritative) or a rendered image.

---

## 0. TL;DR

| item | result |
|---|---|
| XNB files parsed | **153/153** (`0` errors) |
| `Texture2D` exported | **125/125** verified, `0` failures |
| `SpriteFont` decoded | **3/3**, exact EOF, atlas + metrics exported |
| `EffectReader` | 25 skipped (owned by shader agent) |
| texture surface formats | **Color(raw 32bpp) 45, DXT1 3, DXT5 77** (sums to 125) |
| mip levels | **1** for every texture (no mip chains; level 0 == only level) |
| output | `src/Dishwasher/Content/` — **128 PNGs**, 22 MB |
| tools | `tools/xnb_v2.py`, `tools/export_xnb_textures.py` |
| manifest | `<dev-notes>/content-export-manifest.json` (per-file, sha256) |
| reading strategy | **custom `ContentManager` + PNG + public `SpriteFont` ctor** (see §7) |

---

## 1. The XNB v2 reader

Source: [`tools/xnb_v2.py`](../../tools/xnb_v2.py) (pure stdlib).

### 1.1 Header — one correction to the earlier forensics

`asset-formats.md` described a 1-byte `flags` field at offset 5. The decompiled XNA 3.0
`ContentReader.ReadHeader()` shows the real v2 layout:

```
offset 0  'X','N','B' (3 bytes)
offset 3  platform byte        ('x' = Xbox 360)
offset 4  Int16 version = 2    (2 bytes, little-endian)   <-- flags does NOT exist in v2
offset 6  Int32 file size      (4 bytes, little-endian)
offset 10 type-reader count    (7-bit encoded)
```

So the "flags = 0x00" byte is simply the **high byte of the version `Int16`**. XNB v2
has no compression flags (those were added in v3+). Measured: all 153 files have
version `2`, platform `x`, and declared size == on-disk size.

### 1.2 Body layout (matches `ContentReader.cs` exactly)

```
type-reader count (7-bit)
per reader: 7-bit strlen + UTF-8 type name + Int32 type version
shared-resource count (7-bit)      -- 0 in all 153 files
primary object:
    7-bit typeId   (0 = null, else readerIndex+1)
    reader payload
```

Two semantics taken straight from the decompiled framework were essential and are the
reason a naive XNB parser fails:

1. **`List<T>` with a value-type `T` has no per-element type id.**
   `ContentReader.ReadObjectInternal<T>(typeReader)` checks `TargetIsValueType` and,
   for value types (`Rectangle`, `char`, `Vector3`, …), invokes the element reader
   directly. Reference-type elements (e.g. an embedded `Texture2D`) *do* carry a
   7-bit type id. A parser that always reads a type id per element desynchronises
   after the first glyph.
2. **`List<char>` elements are UTF-8, variable length.**
   `CharReader` calls `BinaryReader.ReadChar()` and `ContentReader : BinaryReader`
   uses the default UTF-8 encoding, so `' '..'\u007F'` cost 1 byte and `'\u0080'..`
   cost 2–3 bytes. A fixed 2-byte "UTF-16" read is wrong (this was the key to decoding
   the SpriteFonts).

### 1.3 Readers implemented

`Texture2DReader`, `SpriteFontReader`, `ListReader<T>`, `RectangleReader`, `CharReader`,
`Vector3Reader`, and `NullableReader<T>` (v3+ safety). `EffectReader` is detected via
the header-only mode and **skipped**, not parsed. Any unknown reader raises
`XnbError` rather than mis-parsing.

`Texture2DReader` payload (same as XNA 3.0 source):
`Int32 format, Int32 width, Int32 height, Int32 mipCount, per mip: Int32 dataSize + bytes`.
Integers are **little-endian** (measured on every file).

---

## 2. Measured file counts

From `content-export-manifest.json`:

| primary reader | count |
|---|---|
| `...Texture2DReader` | **125** |
| `...EffectReader` | 25 |
| `...SpriteFontReader` | 3 |
| **total** | **153** |

Texture surface-format histogram (bytes/pixel cross-checked against the declared mip
size and EOF; 125/125 end exactly at EOF):

| id | name | count | bytes/px | check |
|---|---|---|---|---|
| 1 | `Color` (raw 32bpp) | **45** | 4.0 | e.g. `text` 256·256·4 + 85 = 262229 = file |
| 28 | `Dxt1` | **3** | 0.5 | `maps/back1` 512·512/2 + 85 = 131157 = file |
| 32 | `Dxt5` | **77** | 1.0 | `head1` 512·512 + 85 = 262229 = file |

No other surface-format ids appear. **Every texture has exactly one mip level**
(`mip_histogram = {1: 125}`), so "export level 0 only" and "export all levels" are the
same thing here; the exporter is written to emit level 0 and records the count so a
future multi-mip asset is flagged. Sample dimensions: `gfx/dish` 640×480 (raw),
`gfx/sprites` 1024×1024 (DXT5), `gfx/maps/maps1` 1024×1024 (DXT5),
`gfx/panels/panel1` 1040×213 (raw), `gfx/maps/back1` 512×512 (DXT1).

---

## 3. Export stats and output layout

Command (reproducible):

```bash
python3 tools/export_xnb_textures.py \
  --assets $DISHWASHER_ASSETS_DIR \
  --out    src/Dishwasher/Content \
  --manifest <dev-notes>/content-export-manifest.json
# -> xnb total 153 | textures 125 | spritefonts 3 | effects skipped 25 | errors 0
```

Logical paths are preserved: `gfx/foo.xnb` → `Content/gfx/foo.png`.

```
src/Dishwasher/Content/
├── Content.mgcb                     (core agent's; untouched)
├── gfx/          89 texture PNGs + 3 SpriteFont atlases
│   ├── credits/  6   c1..c6.png
│   ├── maps/     8   back1..2, maps1..6
│   └── panels/  22   panel1..22
├── fx/                              (shader agent's .fx; untouched by this work)
└── *.spritefont.json                (Arials, SpriteFont1_JPN, SpriteFont1_CHT)
```

Counts: **89 + 6 + 8 + 22 = 125 texture PNGs**, plus 3 `*_atlas.png` = **128 PNGs**
(22 MB total). Verification: every exported PNG was reopened with Pillow and its size
compared against the XNB header — **125/125 byte-exact dimensions, 0 failures**.
The manifest records per-file format, dimensions, mip count and a sha256.

Cross-check against the game's code (`managed/decompiled/game/projectDish`): every one
of the 38 `Texture2D` load sites (including the dynamic `gfx/head{n}`, `torso{n}`,
`legs{n}`, `weapon{n}`, `maps/maps{n}`, `maps/back{n}`, `panels/panel{n}`, `credits/c{n}`)
and both `SpriteFont` sites now resolves to an exported file — **0 misses**.

The 54 top-level PNGs (`Achievement*.png`, `XlastImages/`, icons) are dashboard/Xbox
art; the game code contains **no** `.png` or `Texture2D.FromStream` reference, so they
were not copied.

---

## 4. DXT endianness handling (measured + visually proven)

**Rule that works:** the two RGB565 colour endpoints are stored **big-endian**
(word-swapped) even though the rest of the XNB is little-endian; the DXT5 8-byte alpha
block and all index bytes are **natural byte order**.

`head1.xnb`, block #309 (file offset `85 + 309*16 = 5029`), raw bytes:

```
cd ff  92 49 49 24 24 92  18 a3  00 00  55 55 8a 55
a0 a1  <- 6 alpha-index bytes ->   c0   c1   <- 4 colour-index bytes
```

| endpoint read | c0 value | RGB |
|---|---|---|
| **big-endian (used)** | `0x18a3` | (24, 20, 24) dark neutral |
| little-endian | `0xa318` | (165, 97, 198) purple |

Visual proof (both rendered from the same bytes): the BE decode shows the character
atlas with natural skin/hair tones; the LE decode is magenta/green. Same test on
`gfx/maps/back1` (DXT1): BE = a natural dark red lit scene, LE = a psychedelic
green/blue false-colour image.

Why the index bytes are *not* swapped: the DXT colour-index and alpha-index fields are
consumed as a linear **byte/bit stream** (pixel 0 = low 2 bits of byte 0), so byte order
is already correct; only the 16-bit *values* (endpoints) need endianness conversion. We
tested the alternative (byte-reversing the colour-index dword): it changes 18 026/262 144
pixels on `head1` and 128 928 on `back1` but produces no cleaner result — the natural
stream order is correct.

Decoders implement the endpoints swap and leave alpha/index untouched:
`decode_dxt1(be_endpoints=True)`, `decode_dxt5(be_endpoints=True)`.

Rendered proof (correct vs wrong) is saved:
`<dev-notes>/proof/head1_dxt5_endpoints_compare.png` and
`<dev-notes>/proof/dbp_raw_channelorder_compare.png`.

### Raw 32bpp channel order (measured) — ⚠️ CORRECTED 2026-10-03

> **CORRECTION.** The order below is **wrong**. The Xbox 360 is **big-endian**, so
> `D3DFMT_A8R8G8B8` (packed word `0xAARRGGBB`) is stored MSB-first as file bytes
> **`A,R,G,B`** — byte 0 is the **alpha**. The `B,G,R,A` decode read the alpha byte as
> blue, which produced the blue cast on the comic panels, the XNA splash (`dbp`) and
> the control-guide art. The original text claimed `B,G,R,A` was proven by the dbp
> splash ("periwinkle-blue with a green BUILD"); the real XNA Game Studio 3.0 splash
> has an **orange BUILD** (measured `(224,64,0)`), neutral DREAM/PLAY, and is produced
> correctly by `A,R,G,B`. Full evidence chain:
> [`comic-colour-fix.md`](comic-colour-fix.md). The code now uses
> `raw_order="argb"` in `tools/xnb_v2.py` / `tools/export_xnb_textures.py` and file
> byte 1→R, 2→G, 3→B, 0→A in `Platform/Import/XnbV2.cs`.

Format id 1 is Xbox-360 `A8R8G8B8`, i.e. in big-endian memory **`A,R,G,B`**. Proof:
`gfx/dbp.xnb` decodes to the XNA "DREAM BUILD PLAY" splash with an orange "BUILD"
(the correct branding); the control-guide art (`gfx/controls`) is natural cream; and
`gfx/arial` is white-on-transparent. The exporter maps to `R,G,B,A` for PNG
(`raw_order="argb"`).

Decoded-payload evidence (for the record):

```
gfx/text.xnb header : 58 4e 42 78 02 00 55 00 04 00
reader table 10..64 : 01 2f "Microsoft.Xna.Framework.Content.Texture2DReader" 00 00 00 00 00
payload 64..96      : 01 01 00 00 00 00 01 00 00 00 01 00 00 01 00 00 00 00 00 04 00 ...
                      ^typeid ^fmt=1   ^w=256      ^h=256      ^mips=1  ^size=262144
data @85, file=262229 = 85 + 262144   -> exact EOF
```

---

## 5. SpriteFont findings

All 3 SpriteFont XNBs decode **exactly to EOF** with the reader (no trailing garbage).
XNA Game Studio 3.0's `SpriteFontReader` (decompiled, authoritative) reads
`texture, glyphs, cropping, charMap, lineSpacing, spacing, kerning` and has **no
`defaultCharacter`** field — that was added in XNA 4.0. MonoGame's reader *does* read a
trailing `bool + char`; our sidecar therefore writes `"default_character": null`.

| file | atlas | format | glyphs | kerning | lineSpacing | spacing | default |
|---|---|---|---|---|---|---|---|
| `gfx/Arials.xnb` | 256×256 | Color | 225 | 225 | 26 | 0.0 | none |
| `gfx/SpriteFont1_JPN.xnb` | 1024×2048 | Color | 872 | 872 | 40 | 0.0 | none |
| `gfx/SpriteFont1_CHT.xnb` | 2048×1024 | Color | 1118 | 1118 | 40 | 0.0 | none |

Character coverage:
* **Arials** — one contiguous run `U+0020–U+0100` (Latin-1 + controls), i.e. the ASCII
  font used for HUD text; `gfx/arial.xnb` (raw 512×256 Texture2D) is its *separate*
  bitmap atlas used by the game's custom text renderer, not this SpriteFont.
* **SpriteFont1_JPN** — ASCII + Latin-1 + Cyrillic + kana (`U+3041–U+30FC`) + a
  selected set of ~350 kanji.
* **SpriteFont1_CHT** — ASCII + Latin-1 + Cyrillic + a selected set of ~750 Traditional
  Chinese hanzi + full-width punctuation (`U+FF01/0C/1A/1F`).

The atlases were exported as `Content/gfx/<name>_atlas.png` and the per-glyph data as
`Content/gfx/<name>.spritefont.json`. Each JSON contains, in reader order:
`glyph_rects` (`[x,y,w,h]`), `cropping_rects`, `char_map` (UTF-8 chars), `line_spacing`,
`spacing`, `kerning` (`[left, width, right]`), `default_character`. Example:
`Arials` glyph `'A'` = rect `(62,137,10,10)`, kerning `(1.0, 10.0, 0.0)`.

### Rebuild plan (decision)

`.spritefont` + MGCB is **not viable** for JPN/CHT: MGCB's font processor rasterises
from a TTF, and the original Japanese/Chinese TTF is not in the package; re-rasterising
would also change the glyph atlas. Instead:

> **MonoGame 3.8.5.1 exposes a `public SpriteFont(...)` constructor**, so the runtime
> `ContentManager` can rebuild the font straight from `_atlas.png` + `.spritefont.json`
> with the exact original glyphs, cropping, char map, line spacing and kerning — no TTF,
> no re-export, no reflection. (Verified by decompiling
> `MonoGame.Framework.dll`: `SpriteFont.cs:55705`, ctor is `public`.)

This is what the prototype in §7 / `content-integration.md` does.

---

## 6. Runtime loading strategy — options evaluated

The game makes **59–61 `Content.Load<T>` calls** (`38 Texture2D`, `19 Effect`,
`2 SpriteFont`, `2 CharDef`) and originally set `Content.RootDirectory = "."`.

### Option A — rebuild everything to MonoGame-native `.xnb` with MGCB
* Textures: MGCB compiles each PNG into a v11 `.xnb`; `Load<Texture2D>` is unchanged.
* **Blockers/gotchas (measured):**
  * The source art is **straight (non-premultiplied) alpha** and XNA 3.0
    `SpriteBlendMode.AlphaBlend` is `SourceAlpha / InvSourceAlpha`
    (`SpriteBatch.cs:626`), i.e. non-premultiplied. MGCB's `TextureProcessor` defaults
    `PremultiplyAlpha = true` → must be explicitly disabled or alpha edges break.
  * Uncompressed Color XBns would be ~**126 MB** (31.5 Mpx × 4 B) vs 22 MB of PNG.
  * SpriteFonts still cannot be built by MGCB (no TTF) → still need a custom step.
* Pros: stock `ContentManager`, platform-optimised textures possible, single pipeline.
* Cons: build step, alpha gotcha, size, still custom for fonts.

### Option B — load PNGs directly at runtime (recommended)
* A `ContentManager` subclass overrides `Load<T>`: `Texture2D` → `Texture2D.FromStream`;
  `SpriteFont` → atlas + JSON via the public ctor; everything else → `base.Load<T>`
  (MGCB `.xnb`, i.e. shaders/CharDef).
* **Discovered enabler:** MonoGame's stock `ContentManager` *already* falls back to
  `Content/<asset>.png` when the `.xnb` is missing
  (`ReadAsset` → `LoadTexture2DFromImageFile`). **But** it uses
  `DefaultColorProcessors.PremultiplyAlpha` — wrong for this game's non-premultiplied
  blending. The subclass exists to load with `Texture2D.FromStream` (straight alpha)
  instead.
* Pros: no texture build, exact alpha semantics, 22 MB instead of ~126 MB, fast
  iteration, keeps every `Load<Texture2D>` site.
* Cons: runtime decode cost (≈140 MB RGBA if all textures resident — fine for the
  Adreno 618 test device), custom cache/disposal, PNGs must be packaged as assets.

**Recommendation: Option B for textures + SpriteFonts, MGCB (Option A) for `Effect`
and `CharDef`.** The shader agent needs MGCB for `.fx` → MGFX anyway, so MGCB stays in
the loop; textures/fonts simply bypass it. This is the least invasive change to the 59
`Load<T>` call sites and avoids both the premultiply hazard and the size blow-up.

**Blend-state translation the core agent MUST apply** (XNA 3.0 → MonoGame), otherwise
the straight-alpha textures render wrong:

| XNA 3.0 `SpriteBlendMode` | MonoGame `BlendState` | factors |
|---|---|---|
| `AlphaBlend` | `BlendState.NonPremultiplied` | `SourceAlpha / InvSourceAlpha` |
| `Additive` | `BlendState.Additive` | `SourceAlpha / One` |
| `None` | `BlendState.Opaque` | `One / Zero` |

**Android packaging:** declare the raw assets so they land at `assets/Content/...`:
```xml
<ItemGroup>
  <AndroidAsset Include="Content\**\*.png" />
  <AndroidAsset Include="Content\**\*.json" />
</ItemGroup>
```
`TitleContainer.OpenStream("Content/gfx/text.png")` then resolves them (same root as
the MGCB `.xnb` output).

### `CharDef` (2 sites) and Effects (19 sites)
* `Content.Load<CharDef>("data/chars/...")` — there are **no** CharDef `.xnb`; the
  data lives in `data/chars/*.dqx` (proprietary; separate RE workstream). This needs a
  custom `ContentTypeReader` + pipeline (or the ported game's own `.dqx` reader), which
  is outside this task. Our loader falls through to `base.Load<T>` for it.
* Effects fall through to `base.Load<Effect>` and consume the shader agent's MGCB
  output.

---

## 7. Deliverables and integration

* Reader: `tools/xnb_v2.py`
* Exporter: `tools/export_xnb_textures.py`
* Manifest: `<dev-notes>/content-export-manifest.json`
* C# loading prototype + wiring guide: `<dev-notes>/content-integration.md`
  (reference copy `tools/csharp/DishwasherContentManager.cs`; it **compiles
  clean** against MonoGame.Framework 3.8.5.1 — verified `Build succeeded, 0 errors`).
* Content: `src/Dishwasher/Content/` (added additively; another agent is actively
  editing `Content/fx` and the project files, so no project file was modified).

---

## 8. What failed / not handled / assumptions

* **Effects intentionally not parsed.** `EffectReader` payloads are Xbox-360
  microcode (`fx/bloom` begins `FE FF 09 01 …`); not portable, shader agent's job.
  The exporter detects and skips them.
* **XACT audio, `.dqx`, `.dgx`, `.zdx`, `.resources`** untouched (other workstreams).
* **54 top-level PNGs** not copied (game never loads them; dashboard art).
* No file used >1 mip, so multi-mip export is untested against real data (code path
  exists and records the level count).
* **CORRECTED 2026-10-03:** the Xbox 360 `A8R8G8B8` byte order is **`A,R,G,B`**
  (big-endian), *not* `B,G,R,A`. The earlier dbp-based conclusion was mistaken; the
  real XNA Game Studio 3.0 splash has an orange BUILD. See the correction banner in
  §4 and [`comic-colour-fix.md`](comic-colour-fix.md).
* **Assumption (high confidence):** DXT index fields are natural byte order (bit
  stream). Proven visually on `head1`/`back1`; the byte-swapped-index alternative was
  also rendered and looked no better.
* The three `SpriteFont`s are rebuilt at runtime in C#; that path was compile-tested
  but **not** run on-device (no MonoGame graphics device was driven in this task).

---

## 9. Reproduce / verify

```bash
# decode one file (prints reader table, dimensions, formats, EOF check)
python3 tools/xnb_v2.py \
        $DISHWASHER_ASSETS_DIR/gfx/head1.xnb

# re-run the whole export (idempotent)
python3 tools/export_xnb_textures.py \
  --assets $DISHWASHER_ASSETS_DIR \
  --out    src/Dishwasher/Content \
  --manifest <dev-notes>/content-export-manifest.json

# count + verify
find src/Dishwasher/Content -name '*.png' | wc -l   # 128
python3 - <<'PY'
import json,os
from PIL import Image
m=json.load(open('<dev-notes>/content-export-manifest.json'))
C='src/Dishwasher/Content'
bad=[r for r in m['records'] if r.get('reader')=='Texture2DReader'
     and Image.open(os.path.join(C,r['out'])).size!=(r['width'],r['height'])]
print('textures', m['textures_exported'], 'dimension mismatches', len(bad))
PY
```
