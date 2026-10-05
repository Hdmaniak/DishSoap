# The Dishwasher: Dead Samurai — Asset Format Forensics

**Scope:** `<extracted-package>/assets` (read-only source) → cleaned copy at
`$DISHWASHER_ASSETS_DIR`. This document reports **measured facts**
(magic bytes, counts, decoded fields) separately from **hypotheses** (formats that
could not be fully confirmed).

**Tooling used for this report:** Python 3.14, Pillow 12.1, coreutils `strings`/`xxd`/`file`.
All scripts live in `/tmp/opencode/forensics/` (`xnb.py`, `xwb.py`, `sigscan.py`, `dump_magic.py`, `clean_tree.py`).

---

## 0. TL;DR

* The cleaned tree contains **560 files / 123,327,123 bytes**, identical byte-for-byte
  to the source. **528 files were moved** out of the flattened root and **8 empty
  "shadow" directories were removed.**
* **Every one of the 153 `.xnb` files is XNA 3.0 content:** header `XNBx`, platform
  byte `x` (Xbox 360), **format version `2`**, **flags `0x00` (uncompressed)**.
  Counter-intuitively the XNB payload integers are **little-endian**, not big-endian.
* **`.zdx` is not binary at all** — all 251 files are 100 % printable ASCII/CRLF
  scripts (level logic, combos, menus). They are the single easiest format to handle
  but need a scripting interpreter, not a converter.
* **`.dqx` (character/animation) and `.dgx` (solo mode) are proprietary binary** and
  require reverse engineering. No third-party tool exists.
* **All audio is XACT v42/43, big-endian (Xbox 360), and 200/222 wave entries are XMA**
  (22 are PCM). MonoGame/FNA have **no XACT support** → must transcode.
* **Both `.dll` files are XEX2 native Xbox 360 modules**, not managed PE/BSJB assemblies.

---

## 1. Task 1 — Cleaned tree

### What was wrong
The STFS extractor did not translate Xbox guest path separators. Result on the original:

* **Every file was a top-level entry** with the full Windows-relative path embedded as
  literal `\` characters in its *name* (e.g. a file literally called `gfx\panels\panel1.xnb`).
* **15 sub-directories existed, all empty**, named after path prefixes — 8 of them also
  contained literal backslashes (`Runtime\v2.0`, `data\maps`, `gfx\panels`, …).
* `find . -type f` = 560; `find . -maxdepth 1 -type f` = 560; files at depth > 1 = 0.

### Transformation
Script `/tmp/opencode/forensics/clean_tree.py` (copy first; source never touched):

1. `cp -a port/assets assets-clean`
2. For every root file whose basename contains `\`: split on `\`, `mkdir -p` the parent
   chain, `shutil.move` the file to the nested location.
3. Delete the now-empty shadow directories (basename contains `\`) bottom-up.

### Verification
| metric | source | clean |
|---|---|---|
| regular files | 560 | **560** |
| total bytes | 123,327,123 | **123,327,123** |
| files moved | — | **528** |
| shadow dirs removed | — | **8** |
| literal `\` names remaining | 536 | **0** |
| real directories | 16 | 16 |

### Resulting top-level layout
```
assets-clean/
├── Achievement01..12.png        (12)
├── ArcadeInfo.xml                (1)
├── DashboardIcon.png, Dishwasher_banner.png, GameIcon.png, GameThumbnail.png,
│   Gamerpic2( small).png, Gamerpic4( small).png, TitleImage.png, dishthumb.png, rp.png
├── Rating_CERO_D.png, Rating_ESRB_M.png, Rating_OFLC_MA15R.png, Rating_PEGI_18.png
├── Thumbs.db                     (1, junk)
├── HostLoader.dll                (1, XEX2)
├── default.xex, game.exe.xex     (2, XEX2)
├── data/                         (303 files)
│   ├── arcade.zdx bubbles.zdx combos.zdx levels.zdx pickups.zdx tut.zdx
│   ├── chars/    (38 .dqx)
│   ├── maps/     (222 .zdx)
│   ├── solos/    (15 .dgx)
│   └── strips/   (22 .zdx)
├── fx/                           (25 .xnb — effect bytecode)
├── gfx/                          (129 .xnb + 1 .zdx)
│   ├── credits/  (6 .xnb)
│   ├── maps/     (9 .xnb + maps.zdx)
│   └── panels/   (22 .xnb)
├── Resources/                    (11 .resources)
├── Runtime/v2.0/                 (18: 16 .xex + 2 containers incl. 1 .dll)
├── sfx/                          (15: 7 .xwb, 7 .xsb, 1 .xgs)
└── XlastImages/                  (27 .png)
```

---

## 2. Task 2 — Format table (magic-byte evidence)

Extension counts over the **cleaned tree**: `251 zdx, 153 xnb, 54 png, 38 dqx,
19 xex, 15 dgx, 11 resources, 7 xwb, 7 xsb, 2 dll, 1 xml, 1 xgs, 1 db`.

| ext | count | magic (first bytes) | container / endianness | converter available? | blocker |
|---|---|---|---|---|---|
| `.xnb` | 153 | `58 4E 42 78` = `"XNBx"`; ver `02`, flags `00` | XNA 3.0 XNB, platform Xbox 360; **payload LE**, uncompressed | partial (`Athari/XnaConvert`, custom parser) | MonoGame/FNA refuse version 2; 360 effect bytecode not portable |
| `.xwb` | 7 | `44 4E 42 57` = `"DNBW"` (=byte-swapped `"WBND"`) | XACT wave bank **v43 tool / v42 header, BIG-endian (X360)** | `vgmstream`, `unxwb`, `xact360extract` | 200/222 entries are **XMA**; MonoGame has no XACT |
| `.xsb` | 7 | `4B 42 44 53` = `"KBDS"` (=byte-swapped `"SDBK"`) | XACT sound bank v43/43, **BE**, platform byte `0x03` (X360) | no mainstream tool | cue→wave mapping must be custom-parsed; MonoGame no XACT |
| `.xgs` | 1 | `46 53 47 58` = `"FSGX"` (=`"XGSF"`) | XACT global settings v43/42, **BE**, platform byte `0x07` | no mainstream tool | metadata only; MonoGame no XACT |
| `.zdx` | 251 | none — starts `"new map\r\n"`, `"#\r\n"`, `"Map Defs\r\n"`, `"foghorn cafe\r\n"`… | **plaintext ASCII, CRLF line-oriented** | parsers exist / trivial text parse | game logic must be re-implemented; no asset conversion needed |
| `.dqx` | 38 | none — e.g. `00 00 00 00 06 00 00 00` (box), `08 00 00 00 08 00 00 00` (zombie) | proprietary **binary, LE** ints/floats, embedded ASCII clip names | **none** | full reverse engineering (loaders) |
| `.dgx` | 15 | none — e.g. `EC 51 38 3E` (f32 0.18), `33 33 34 42` (f32 45.05), `38 00 00 00` | proprietary **binary, LE**, embedded ASCII names | **none** | full reverse engineering |
| `.resources` | 11 | `CE CA EF BE` = .NET magic `0xBEEFCACE` | standard .NET `.resources` (ResourceReader v1) | `resgen`/`monodis`/`dotnet`, custom parser | none (localized strings) |
| `.png` | 54 | `89 50 4E 47 0D 0A 1A 0A` | standard PNG | Pillow/ImageMagick | none |
| `.xml` | 1 | `FE FF` BOM | **UTF-16 big-endian** XML, CRLF | any XML tool | none |
| `.db` | 1 | `D0 CF 11 E0 A1 B1 1A E1` | OLE Compound File (`Thumbs.db`) | — | junk; discard |
| `.dll` | 2 | `58 45 58 32` = `"XEX2"` | **Xbox 360 XEX2 native module** (NOT PE/BSJB) | `xextool`/Xenia (code, not assets) | native/compressed; not managed |
| `.xex` | 19 | `58 45 58 32` = `"XEX2"` | Xbox 360 executables | `xextool`/Xenia | packed; game C# is inside `game.exe.xex`, no plaintext `BSJB` |

Head-entropy scan (`sigscan.py`) confirms no hidden compression/encryption signatures
(PNG/DDS/XNB/RIFF/ZIP/GZIP) inside `.zdx/.dqx/.dgx`, and shows those three are
low-entropy binary/text (mean 1.3–4.4), i.e. **not LZ-compressed and not XOR-obfuscated**.

---

## 3. Task 3 — Proof of feasibility

### 3.1 XNB deep decode (all 153 files parsed)

**Header (10 bytes, verified against every file):**
```
0x00  'X','N','B'   platform='x' (Xbox 360)
0x04  version = 2    (XNA Game Studio 3.0)
0x05  flags   = 0x00 (bit7=0 → NOT compressed)
0x06  uint32 LE file size == exact on-disk size
0x0A  header byte 0x01      ; 7-bit type-reader count (1 or 8)
      readers             ; each = 7-bit strlen + ASCII name + int32 version(=0)
      sharedResourceCount ; 7-bit (0)
      object graph        ; 7-bit (readerIndex+1), then payload
```
All 153 are `platform='x'`, `version=2`, `flags=0x00`. Reader distribution:

| reader | count | notes |
|---|---|---|
| `...Texture2DReader` | 125 | textures |
| `...EffectReader` | 25 | Xbox-360 compiled shader bytecode |
| `...SpriteFontReader` | 3 | `SpriteFont1.xnb`, `_JPN`, `_CHT` |

**Texture format ids (bytes/pixel measured against declared mip size, and every payload
ends exactly at EOF — 125/125):**

| fmt id | count | bytes/px | hypothesis |
|---|---|---|---|
| `1` | 45 | 4.000 | 32-bpp `A8R8G8B8`, uncompressed |
| `28` | 3 | 0.500 | DXT1 |
| `32` | 77 | 1.000 | DXT5 |

**Worked example — `gfx/text.xnb`:** `Texture2DReader`, fmt `1`, **256×256**, mips 1,
mip size `262144` = 256·256·4; data at offset 85; **85 + 262144 = 262229 = file size.**
`gfx/dish.xnb`: 640×480, data size 1,228,800 = 640·480·4 (exact).
`gfx/panels/panel1.xnb`: 1040×213, 886,080 = 1040·213·4 (exact).

**Byte order:**
* Integer fields are **little-endian** (e.g. width `80 02 00 00` = 640; effect bytecode
  length `60 05 00 00` = 1376, and 62 + 4 + 1376 = 1442 = file size).
* 32-bpp texels: alpha is the 4th byte and varies, so order is one of RGBA/BGRA;
  **hypothesis (medium-high): `B,G,R,A`** per the Xbox 360 D3D `A8R8G8B8` convention.
  Either way it is a trivial channel swap at conversion time.
* **DXT5 colour endpoints are BIG-endian** (byte-swapped RGB565) while the 8-byte alpha
  block is little-endian. This was proven by decoding `gfx/head1.xnb` both ways — the
  byte-swapped RGB565 produces clean skin/colour (see `head1_DXT5_BEcolor.png`), the
  non-swapped version produces a magenta/green cast. Sprites decode as recognizable
  512×512 character-part atlases.

**Effects:** `fx/bloom.xnb` decodes to `EffectReader`, bytecode length 1376, first bytes
`FE FF 09 01 00 00 …`. This is **Xbox-360-specific effect microcode**, not PC D3D9 or
DXBC — it cannot be fed to MonoGame/MojoShader. The 25 effects must be rewritten/recompiled
from HLSL source (which is not in the package).

**SpriteFonts:** e.g. `gfx/SpriteFont1_JPN.xnb` declares 8 type readers
(`SpriteFontReader`, `Texture2DReader`, `ListReader`/`RectangleReader`,
`ListReader<char>`/`CharReader`, `ListReader<Vector3>`/`Vector3Reader`) and embeds a
large (8.4 MB) shared Texture2D atlas + glyph/cropping/kerning tables. Fully parseable;
the embedded texture extracts with the same Texture2D logic.

### 3.2 XWB deep decode

Structure matches DirectXTK `WaveBankReader` (XACT v42/43): 52-byte header
(`DNBW`, u32 tool=43, u32 header=42, 5 × REGION), 96-byte `BANKDATA`, then per-entry
24-byte `ENTRY` = `{flags:4|duration:28, MINIWAVEFORMAT, REGION play, SAMPLEREGION loop}`.
All 7 banks are **non-compact** (per-entry formats).

| bank | entries | codecs |
|---|---|---|
| `music.xwb` | 8 | XMA ×8 |
| `vox.xwb` | 87 | XMA ×87 |
| `waves.xwb` | 106 | XMA ×89, PCM ×17 |
| `halper.xwb` | 15 | XMA ×10, PCM ×5 |
| `solo_crux/dish/smash.xwb` | 2 each | XMA ×2 each |
| **total** | **222** | **XMA 200, PCM 22** |

Example `sfx/music.xwb` entry 0: XMA, 2 ch, ~48 000 Hz, duration 12,114,944 samples,
play region length 3,489,792 B; extracted payload
`/tmp/opencode/forensics/extracted/music.xwb.e0.xma` (3,489,792 B).
So the audio is **Xbox 360 XMA**, not PCM/xWMA — this is the audio blocker.

Approx. XMA bandwidth: `music.xwb` is 22 MB for ~45 min of music (≈ 64–80 kbps), classic
XMA. The 17 PCM entries in `waves.xwb` and 5 in `halper.xwb` are trivially convertible.

---

## 4. Task 4 — Tooling plan (Linux, no Windows)

| format | tool / library | handles 360/BE? | plan |
|---|---|---|---|
| `.xnb` textures | **`Athari/XnaConvert`** (C#; supports XNA 1.0–4.0, Texture2D→PNG) | yes, all versions | run under `dotnet`/`mono` on Linux, or use our parser (`xnb.py`) |
| `.xnb` textures | `xnbcli` (Node) | **No** — XNA 4.0/LZX only; rejects v2 | do not use |
| `.xnb` textures | MonoGame `ContentManager` | **No** — accepts only `version 4/5` | do not use |
| `.xnb` textures | Pillow / ImageMagick | n/a | convert extracted raw/DXT→PNG; load with `Texture2D.FromStream` on Android |
| DXT1/DXT5 | `nvtt`, `compressonator`, `texconv` | n/a | remember 360 RGB565 is BE; swap before decode |
| `.xnb` effects | none | — | **rewrite** from HLSL (360 microcode unusable) |
| `.xnb` SpriteFont | `Athari/XnaConvert`, custom | partial | extract atlas+metrics, or regenerate with MonoGame font pipeline |
| `.xwb` (+PCM) | `unxwb` (aluigi) | versions 46/44 in mainline; 43/42 supported by variants | extract entries |
| `.xwb` (+XMA) | **`vgmstream`** (`xwb.c` + XMA decoder) | yes (360 XMA) | `vgmstream-cli -o out.wav music.xwb` — best single tool |
| `.xwb` (+XMA) | `ffmpeg` (`xma2` decoder) | yes | fallback for raw `.xma`; needs correct framing |
| `.xsb` / `.xgs` | none mainstream | — | custom parse (Xentax format) to recover cue names → wave indices |
| `.resources` | `resgen`/`monodis` (Mono), `dotnet`, or custom | n/a | extract localized strings |
| `.xml` | any | n/a | parse UTF-16 BE |
| `.png` | Pillow/ImageMagick | n/a | direct |
| `.zdx` | custom | n/a | plaintext; write parser/interpreter for the game logic |
| `.dqx` / `.dgx` | **none** | — | reverse-engineer and write custom loaders |
| `.xex` / `.dll` | `xextool`, Xenia, Ghidra/IDA | yes | if code is needed (managed game logic in `game.exe.xex`) |

**Key version facts to remember for the port:**
* XNB **v2** = XNA GS 3.0. MonoGame `ContentReader` throws on anything but **v4/v5**.
* XACT **v42/43** banks are **big-endian**; XNA 3.0 .NET assemblies reference
  `Microsoft.Xna.Framework, Version=2.0.0.0` / `mscorlib 3.5.0.0`.

---

## 5. Proprietary formats — verdict

### `.zdx` — **plaintext game scripts** (confidence: very high)
* All 251 files printability ratio = **1.000** (pure `[A-Za-z0-9 ,.#'()\-]+\r\n`).
* Examples:
  * `data/maps/a1welcome.zdx`: `new map`, `TAG INIT`, `SETMAPTHEME CEM`,
    `ADDBUCKET GOON 1432 402`, `HEALTHBUCKET ZOMBIE 1510 222`,
    `IFNOTBUCKETGOTO WAITB1`, `ENDLEVEL`.
  * `data/combos.zdx`: combo definitions (`#cleaver`, `ocho slam`, `x x x x x`).
  * `gfx/maps/maps.zdx`: map segment rectangles (`#seg 1`, `0 0 198 324`).
  * `data/arcade.zdx`: tutorial/help text + level id.
* **No compression, no XOR, no embedded PNG/DDS/XNB.** Not an asset container — it is the
  game's own scripting/logic language. Converting it means *re-implementing the
  interpreter/game logic*, not decoding bytes.

### `.dqx` — **per-character animation/rig data** (confidence: high)
* Proprietary binary, little-endian, low entropy, **not compressed/obfuscated**.
* Header = small int32 counts; body interleaves `int32`/`float32` keyframe values with
  ASCII animation-state names — every file contains strings such as `idle1..idleN`,
  `land1..4`, `attack1..13`, `dying1..4`, `jhit1..2`. `box.dqx` has 11 clips, `zombie.dqx`
  has 177+ named clips.
* Heavy use of `0xFFFFFFFF` (-1) sentinels. (An apparent ".NET BinaryFormatter header" hit
  was a **false positive**: the byte pattern is `int32 0` + `int32 1` + `int32 -1` runs.)
* **Verdict:** Ska Studios' custom serialized character animation/rig format. Requires a
  custom reader; the clip-name index makes structural RE tractable.

### `.dgx` — **solo-mode data** (confidence: low–medium)
* Proprietary binary, little-endian, 15 files named `solo_crux{,0..3}.dgx`,
  `solo_dish{,0..3}.dgx`, `solo_smash{,0..3}.dgx`; each embeds its matching ASCII name.
* Layout: leading `float32`s (e.g. `0.180000`, `45.05`), then int counts, then small
  repeated int/float vectors, all LE.
* **Verdict:** almost certainly "solo mode" (survival/challenge) configuration — spawn
  waves / arena parameters for the three solo maps. Small (1–2 KB) and likely fully
  recoverable, but needs RE. `solo_*` also have `.xwb/.xsb` audio and `solo_crux`
  `dgx`+`xwb` pairs, reinforcing the "solo challenge" interpretation.

---

## 6. Top 3 blockers for MonoGame-on-Android

1. **XNB version 2 is unloadable by modern MonoGame/FNA (v4/v5 only), and 25 `.xnb`
   effects contain Xbox-360-specific bytecode (`FE FF 09 01 …`).**
   *Mitigation:* batch-convert textures (`Athari/XnaConvert` or our v2 parser; 45 raw
   32-bpp, 77 DXT5, 3 DXT1 — remember BE RGB565 in DXT) to PNG and load them with
   `Texture2D.FromStream`; re-author the 25 effects from HLSL by hand.

2. **All audio is XACT (v42/43, big-endian) with 200/222 XMA entries; MonoGame/FNA have
   zero XACT support.**
   *Mitigation:* `vgmstream` (or `unxwb`+`ffmpeg`) to transcode each `.xwb` to wav/ogg,
   plus a custom `.xsb`/`.xgs` parser to recover cue→wave names and category settings.

3. **Proprietary `.dqx` (38 animation files) and `.dgx` (15 solo files) have no tooling,
   and the 251 `.zdx` scripts are game logic, not assets.**
   *Mitigation:* reverse-engineer the `.dqx`/`.dgx` binary layouts and write loaders;
   reimplement the `.zdx` interpreter (or port the original interpreter out of the
   managed code in `game.exe.xex`, which is a packed XEX2 with no plaintext `BSJB`).

---

## 7. Measured vs. hypothesised

**Measured (hard evidence):** file counts and sizes; 528 moved / 8 dirs removed; all
magic bytes above; XNB platform/version/flags = `x`/2/0; LE integer fields; texture
dimensions and 4B/0.5B/1B-per-pixel accounting ending exactly at EOF; reader-type
distribution (125/25/3); effect bytecode size/first bytes; XWB `DNBW` BE + tool 43 /
header 42 + 24-byte entries + XMA/PCM histogram (200/22); XSB `KBDS` BE platform 3;
XGS `FSGX` BE platform 7; `.zdx` 100 % printable; `.resources` `0xBEEFCACE`; `Thumbs.db`
OLE; XEX2 magic on all 19 `.xex` + both `.dll`.

**Hypothesised (needs confirmation):**
* fmt id mapping `1→A8R8G8B8`, `28→DXT1`, `32→DXT5` (strongly implied by bytes/pixel and a
  valid DXT5 decode, but the enum name is inferred).
* 32-bpp channel order `B,G,R,A` on disk.
* `.dqx` = character animation/rig; `.dgx` = solo-mode configuration.
* The `.zdx` opcode semantics (the text is certain; the meaning of individual commands is inferred from context).
