# The Dishwasher: Dead Samurai — Xbox 360 Shader / Effect Feasibility

**Author:** Shader/Effects Specialist (subagent)
**Date:** 2026-10-02
**Inputs:** `$DISHWASHER_ASSETS_DIR/fx/*.xnb` (read-only, 25 Effect assets),
`managed/decompiled/game` (read-only), ReXGlue SDK `src/graphics/pipeline/shader/` (read-only)
**Outputs:** this note; reference material under `<dev-notes>/shaders/`;
re-authored sketches under `src/Dishwasher/Content/fx/`.

**Tooling used:** Python 3.14, `ilspycmd` 9.1.0.7988, clang++ 20, .NET 8 / MGCB 3.8.5.1,
plus a purpose-built port of MojoShader's `fx_2_0` effect parser to big-endian, and a
locally built copy of [hedge-dev/XenosRecomp](https://github.com/hedge-dev/XenosRecomp)
(microcode → HLSL).

---

## 0. TL;DR

* **The 25 Effect XNB files contain the standard D3DX 9.1 compiled-effect binary
  (`fx_2_0`, magic `FE FF 09 01`), byte-swapped to big-endian.** It is *not* a custom
  XNA format. Every one of the 25 parses cleanly against MojoShader/Wine's `fx_2_0`
  reader (adapted to BE) and consumes its payload exactly.
* **The effect container is fully readable:** parameter names/types/semantics/defaults,
  technique names, pass names, render states, sampler state, and the compiled shader
  blobs are all present. There is **no HLSL/GLSL source**, but there *is* complete
  constant-table reflection.
* **The per-shader payload is a native Xenos `ShaderContainer`** (magic
  `0x102A1100` = pixel / `0x102A1101` = vertex), i.e. real Xbox 360 GPU microcode wrapped
  in XNA's standard Xenos container — the same container `hedge-dev/XenosRecomp` and
  Xenia parse.
* **There are 29 shader blobs in the 25 files, 28 distinct by bytecode hash.** Only
  **19 effects are actually loaded by the game**; 6 are dead assets
  (`RadialBlur, blood, blood2, forefade, glisten, radblur`). The 19 shipping effects
  contain **20 blobs, all distinct**, but they collapse to **~16 conceptual shaders**
  (3 blur variants, 2+ blood variants share designs). 27 of the 29 blobs are pixel
  shaders; the only 2 vertex shaders live in the *dead* `RadialBlur`.
* **Microcode → HLSL recovery is proven, not hypothetical.** XenosRecomp, built here
  against the exact XNA blobs, auto-generated readable HLSL for all 29 blobs
  (`<dev-notes>/shaders/decompiled/`).
* **Recommendation: re-author, using auto-translation as the blueprint.** Take the
  XenosRecomp HLSL per effect, hand-clean it into MGCB effects (parameters named exactly
  as the XNA reflection), and compile. Direct "translate-and-ship" is not viable because
  MonoGame consumes MGFX, not raw SPIR-V, and the Android/OpenGL path is built from
  DX9-style bytecode via MojoShader. The shaders are trivial full-screen passes, so the
  re-authoring risk is low.
* **Verified compile:** the six re-authored sketches in `Content/fx/` compile with MGCB
  (`/platform:XboxOne`, native bundled DXC, no Wine) — **6 succeeded, 0 failed**. The
  same sources are `#if OPENGL`-guarded for the Android/GL profile; on this host that
  profile additionally needs Wine + `fxccs.dll` (MGCB's documented Linux requirement),
  which is not installed.

**Speculation is labelled ▸SPEC wherever it appears.**

---

## 1. Container format (hexdump-backed)

### 1.1 XNB wrapper (XNA Game Studio 3.0, Xbox 360)

```
00000000: 584e 4278 0200 a205 0000 012c 4d69 6372  XNBx.......,Micr
00000010: 6f73 6f66 742e 586e 612e 4672 616d 6577  osoft.Xna.Framew
00000020: 6f72 6b2e 436f 6e74 656e 742e 4566 6665  ork.Content.Effe
00000030: 6374 5265 6164 6572 0000 0000 0001 6005  ctReader......`.
00000040: 0000 feff 0901 ...                        ....
```
(from `fx/bloom.xnb`, 1442 bytes)

* `XNBx` = little-endian XNB v2, platform `x` (Xbox 360), flags `0x00` (uncompressed).
* Type reader name `Microsoft.Xna.Framework.Content.EffectReader`, version `0`.
* Object graph: 7-bit reader index `01`, then `EffectReader` payload =
  **`int32 LE length` + bytes**. For bloom the length is `0x560` = 1376 and the effect
  blob starts at file offset `0x42` (66); `66 + 1376 = 1442` = EOF.

### 1.2 Effect payload = D3DX 9.1 `fx_2_0` binary, big-endian

The payload starts `FE FF 09 01`. Read as four bytes this is the D3DX effect
version token: `magic = 0xFEFF`, `major = 0x09`, `minor = 0x01`. This exact test
exists in MojoShader (`mojoshader_effects.c:995`) and Wine
(`d3dx9_36/effect.c`: `0xfeff0000 | (major<<8) | minor`). **Every integer in the
container is big-endian** (Xbox 360); the outer XNB integers are little-endian.

Layout (all offsets relative to the start of the effect blob; `base = blob + 8`):

```
+0x00  u16  magic = 0xFEFF
+0x02  u8   major = 0x09
+0x03  u8   minor = 0x01
+0x04  u32  offset            ; file-relative offset from `base` to the counts block
       base = blob+8
base+offset:
       u32  numParameters
       u32  numTechniques
       u32  unknown
       u32  numObjects
       ; --- parameters, then techniques, then object table ---
```

Strings are not inline: a parameter stores a `u32` offset into the `base` region that
points at a **length-prefixed string**: `u32 byteLength` (including the trailing NUL)
followed by the bytes.

**Parameter value record** (`readparameters` / `readvalue` in MojoShader):

```
u32 typeOffset          ; -> type record below (relative to base)
u32 valueOffset         ; -> default value (relative to base)
u32 flags
u32 numAnnotations      ; then numAnnotations x {u32 typeOffset, u32 valueOffset}
; type record at base+typeOffset:
u32 type                ; D3DXPARAMETER_TYPE  (0=void,1=bool,2=int,3=float,4=string,
                        ;  5..9=texture*, 10..14=sampler*, 15=pixelshader,16=vertexshader)
u32 class               ; D3DXPARAMETER_CLASS (0=scalar,1=vector,2=matrixRows,
                        ;  3=matrixCols,4=object,5=struct)
u32 nameOffset
u32 semanticOffset
u32 elements
; scalar/vector/matrix: u32 columns, u32 rows
; struct:               u32 memberCount, then members
; object:               sampler -> u32 numStates + states; else u32 objectIndex
```

**Technique / pass / state records:**

```
technique:  u32 nameOffset, u32 numAnnotations, u32 numPasses
pass:       u32 nameOffset, u32 numAnnotations, u32 numStates
state:      u32 state, u32 unknown, u32 typeOffset, u32 valueOffset
```

**Object table (where the shader blobs live):**

```
after techniques:  u32 numSmallObjects, u32 numLargeObjects
small object:      u32 index, u32 length, bytes[length]      (padded to 4)
large object:      u32 technique, u32 index, u32 unknown,
                   u32 state, u32 type, u32 length, bytes[length]   (padded to 4)
```

`numLargeObjects` is not stored — it is `numObjects - numSmallObjects - 1`.

### 1.3 Worked example — `fx/bloom.xnb` payload (1376 bytes)

```
payload+0x00: fe ff 09 01   magic = 0xFEFF, version major/minor = 9.1
payload+0x04: 00 00 00 e8   offset = 0xE8  (counts at base+0xE8, base = payload+8)
payload+0x08: ...           base region: length-prefixed strings, type records, defaults
...
payload+0xF0: 00 00 00 02   numParameters = 2
payload+0xF4: 00 00 00 01   numTechniques = 1
payload+0xF8: 00 00 00 03   unknown = 3
payload+0xFC: 00 00 00 02   numObjects = 2
...
payload+0xE4: 50 6f 73 74 42 6c 6f 6f 6d 00                 "PostBloom\0"
```

* `offset = 0xE8` → counts begin at `base + 0xE8 = 8 + 232 = 0xF0`:
  `numParameters = 2`, `numTechniques = 1`, `unknown = 3`, `numObjects = 2`.
* Parsed result for bloom: parameters **`samplerState` (sampler)** and
  **`offsets` (float vector, 12 float2 elements, default shown below)**;
  technique **`PostBloom`**, pass **`P0`**, one render state (`COLORWRITEENABLE1`);
  one large object = the pixel shader blob.

`offsets` default value from the embedded constant data (12 × float2):
```
(-0.3262,-0.4058) (-0.8401,-0.0736) (-0.6959,+0.4571) (-0.2033,+0.6207)
(+0.9623,-0.1950) (+0.4734,-0.4800) (+0.5195,+0.7670) (+0.1855,-0.8931)
(+0.5074,+0.0644) (+0.8964,+0.4125) (-0.3219,-0.9326) (-0.7916,-0.5977)
```

### 1.4 The shader blob is a native Xenos `ShaderContainer`

Every object-table shader entry begins with the **Xenos shader-container header**:

```
u32 flags             ; 0x102A1100 = pixel shader, 0x102A1101 = vertex shader
u32 virtualSize
u32 physicalSize       ; virtualSize + physicalSize == blob length exactly
u32 fieldC
u32 constantTableOffset
u32 definitionTableOffset
u32 shaderOffset
u32 field1C            ; 0 on all 29 blobs
u32 field20            ; 0 on all 29 blobs
```

followed by the **virtual part** `[0, virtualSize)` (constant-table reflection, definition
table, shader descriptor) and the **physical part** `[virtualSize, virtualSize+physicalSize)`
(the GPU microcode + its physical layout). The `Shader` descriptor at `shaderOffset` is:

```
u32 physicalOffset     ; byte offset of the microcode inside the physical part
u32 size               ; byte length of the microcode; physicalOffset + size == physicalSize
u32 field8
u32 fieldC             ; svPos register = (fieldC >> 8) & 0xFF
u32 field10
u32 interpolatorInfo
```

This is *exactly* the structure `XenosRecomp/main.cpp` scans for
(`(flags & 0xFFFFFF00) == 0x102A1100 && field1C == 0 && field20 == 0`,
`dataSize = virtualSize + physicalSize`), and all 25 XNA blobs satisfy it.

The `virtual` constant table is a `D3DXSHADER_CONSTANTTABLE`. For bloom:

```
constantTableOffset = 36 (0x24)
+0x24: u32 container size = 0x110
+0x28: u32 Size   = 0x1c (28)
+0x2c: u32 Creator= 0x103 → string "2.0.6274.0"
+0x30: u32 Version= 0xFFFF0300        ; D3DPS_VERSION(3,0) = ps_3_0
+0x34: u32 Constants = 2
+0x38: u32 ConstantInfo = 0x1c
+0x3c: u32 Flags = 0
+0x40: u32 Target = 0xfc → string "ps_3_0"
```
So the `ps_3_0` / `vs_3_0` strings and the `2.0.6274.0` creator string are **reflection
metadata**, not the code. The XNA 3.0 effect compiler (XNANative) emitted the container;
`2.0.6274.0` identifies it ▸SPEC.

---

## 2. Per-effect inventory

Generated by `<dev-notes>/shaders/tools/effect_parse.py` (a Python port of MojoShader's
`fx_2_0` reader with big-endian reads). All 25 payloads are consumed exactly
(`consumed == payload length`). `Loaded?` is from the 19 `Content.Load<Effect>("fx/…")`
sites in `managed/decompiled/game/projectDish/Game1.cs:549–569`.

| effect | loaded | parameters (name:type) | technique(s) / pass(es) | blobs (kind) | blob bytes |
|---|---|---|---|---|---|
| `RadialBlur` | **dead** | WorldViewProjection:float, UVOffset:float, UVScale:float, TexelSize:float, Center:float, GlobalAlpha:float, PixelDistance:float, DiffuseTexture:texture, EffectMaskTexture:texture, DiffuseTextureSampler:sampler2d, EffectMaskTextureSampler:sampler2d | RadialBlurQuad[p0]; RadialBlurStandard[MainPass] | 4 (PS/VS ×2) | 1284, 516, 1284, 360 |
| `blood` | **dead** | samplerState:sampler, originalMap:sampler, fore:bool | Blood[P0] | 1 (PS) | 1380 |
| `blood2` | **dead** | samplerState:sampler, bright:float, statik:float, lineStatik:float | Blood[P0] | 1 (PS) | 1584 |
| `bloom` | yes | samplerState:sampler, offsets:float | PostBloom[P0] | 1 (PS) | 1016 |
| `blur` | yes | samplerState:sampler, levs:int, width:float, xcenter:float, ycenter:float, blackblood:bool | Blast[P0, P1] | 2 (PS/PS) | 1164, 816 |
| `bubble` | yes | samplerState:sampler, width, xcenter, ycenter, mag, reddish:bool | Bubble[P0] | 1 (PS) | 860 |
| `burnblur` | yes | samplerState:sampler, levs:int, width, xcenter, ycenter, mag | Blast[P0] | 1 (PS) | 904 |
| `camsplat` | yes | samplerState:sampler, red:float | CamSplat[P0] | 1 (PS) | 940 |
| `color` | yes | samplerState:sampler, x:float, y:float, alpha:float | PostColor[P0] | 1 (PS) | 560 |
| `comicblur` | yes | samplerState:sampler, left1, top1, right1, bottom1, left2, top2, right2, bottom2, a, speed | ComicBlur[P0] | 1 (PS) | 596 |
| `fade` | yes | samplerState:sampler, rad, fader:int, offsets:float | PostFade[P0] | 1 (PS) | 1400 |
| `forefade` | **dead** | samplerState:sampler, rad, r, g, b, colorOnly:bool, offsets | PostFade[P0] | 1 (PS) | 1368 |
| `glisten` | **dead** | samplerState:sampler, offx, offy | Glistener[P0] | 1 (PS) | 484 |
| `grad` | yes | samplerState:sampler, burnmag, gradFlip:bool, rgrad, ggrad, bgrad, bright, levs:int, width:int, xcenter, ycenter | PostGrad[P0] | 1 (PS) | 1024 |
| `ink` | yes | samplerState:sampler, bright:float | Ink[P0] | 1 (PS) | 556 |
| `lense` | yes | refractSampler:sampler, backBuffer:sampler, edgeBlur, offsets | PostLense[P0] | 1 (PS) | 1116 |
| `newblood` | yes | samplerState:sampler, bright, statik, lineStatik | Blood[P0] | 1 (PS) | 1048 |
| `poster` | yes | samplerState:sampler, alpha, rMin,gMin,bMin, rMid,gMid,bMid, rMax,gMax,bMax, fore:bool | Poster[P0] | 1 (PS) | 1392 |
| `radblur` | **dead** | samplerState:sampler | PostRad[P0] | 1 (PS) | 976 |
| `redhaze` | yes | samplerState:sampler, horiz:bool, glare:bool | PostHaze[P0] | 1 (PS) | 712 |
| `trail` | yes | samplerState:sampler, tx, ty, alpha, r, g, b | PostTrail[P0] | 1 (PS) | 668 |
| `trainblur` | yes | samplerState:sampler, levs:int, width, xcenter, ycenter, facta, horizon | Blast[P0] | 1 (PS) | 996 |
| `trainover` | yes | samplerState:sampler, facta:float | Train[P0] | 1 (PS) | 756 |
| `wallblood` | yes | samplerState:sampler, red, offsets | WallBlood[P0] | 1 (PS) | 944 |
| `water` | yes | samplerState:sampler, horizon, delta, theta, rnd, puddle:bool | PostWater[P0] | 1 (PS) | 872 |

(Full machine-readable list: `<dev-notes>/shaders/inventory.md`.)

### 2.1 Distinct-shader count

| metric | count |
|---|---:|
| Effect `.xnb` files | 25 |
| Effects actually loaded by the game | **19** |
| Dead effect assets | **6** (`RadialBlur, blood, blood2, forefade, glisten, radblur`) |
| Shader blobs across all 25 effects | **29** |
| Distinct shader blobs (SHA-256) | **28** (RadialBlur's PS is shared by its 2 techniques) |
| Shipping blobs (19 effects) | 20 (all distinct) |
| Shipping blobs that are pixel shaders | **20** (the 2 vertex shaders are only in dead RadialBlur) |
| Conceptual families among shipping effects | **~16** |

Families that share a design and can be one parameterised re-authoring:
* `blur` / `burnblur` / `trainblur` — all use technique **`Blast`**, params
  `levs,width,xcenter,ycenter` (+ per-effect `blackblood`/`mag`/`facta,horizon`).
* `newblood` (shipping) and dead `blood`/`blood2` — technique **`Blood`**.
* `fade` (shipping) and dead `forefade` — technique **`PostFade`**.
* Everything else is a one-off: `PostBloom, PostColor, Bubble, CamSplat, ComicBlur,
  Ink, PostGrad, Poster, PostHaze, PostLense, PostTrail, Train, WallBlood, PostWater`.

So the *minimum* faithful shipping set is **16 shader re-authorings**, two of which
(blur family) already share one body in the sketches.

---

## 3. Microcode analysis (what the payloads really are)

`FE FF 09 01` is **not** DXBC and **not** Xenos microcode itself — it is the D3DX effect
container (§1.2). The actual GPU code lives in the object table, inside the
`ShaderContainer` (§1.4).

### 3.1 Instruction encoding (Xenos / R400-derived)

From ReXGlue's `include/rex/graphics/format/ucode.h` (Xenia-derived, which cites the
official XNA 3.1 Xbox 360 assembler as its primary encoding reference):

* Instruction memory is addressed in **3-DWORD (12-byte) units**: an instruction address
  `a` maps to byte `code + a*12`.
* **Control-flow instructions are 2 DWORDs each and are packed two-per-3-DWORDs.**
  `UnpackControlFlowInstructions()` shows the packing: from dwords `(d0,d1,d2)` you get
  `cf0 = (d0, d1 & 0xFFFF)` and `cf1 = (d1>>16 | d2<<16, d2>>16)`; the opcode is bits
  `[12:15]` of the second DWORD of each (`0=nop, 1=exec, 2=exec_end, 3/4=cond_exec*,
  7/8=loop*, 10=return, 11=jump, 12=alloc`).
* **ALU instructions are 2 DWORDs**; **vertex/texture fetch instructions are 3 DWORDs**.

### 3.2 The simplest effect, decoded end-to-end

Take `fx/glisten.xnb` (the smallest payload, 812 bytes; 1 pixel shader, 484-byte
`ShaderContainer`; params `offx`, `offy`). Its microcode is decompiled below by
XenosRecomp (raw HLSL at `<dev-notes>/shaders/decompiled/glisten_t0p0.hlsl`).
Relevant body (the surrounding `shader_common.h` boilerplate is the XenosRecomp runtime
preamble and is not part of the shader):

```hlsl
// params: offx (c0), offy (c1); samplerState (t0/s0)
float4 c255 = asfloat(uint4(0x40000000,0x3EAAAAAB,0,0));   // (2.0, 0.3333, 0, 0)

r1.xyzw = tfetch2D(samplerState_Texture2DDescriptorIndex,
                   samplerState_SamplerDescriptorIndex, r0.xy, 0).xyzw;
r0.z = r1.x * offy.x;                 // offset the sample by (offx,offy) * red
r0.w = r1.x * offx.x;
r0.xy = r0.zw + r0.yx;
r0.xyz = tfetch2D(..., r0.yx, 0).xyz; // second tap
r0.xyz = r1.xyz * c255.xxx + r0.xyz;  // centre*2 + offset tap
oC0.xyz = r0.xyz * c255.yyy;          // * 1/3
oC0.w = max(r1.w, r1.w);
```

Translated: **a 2-tap directional smear** — tap the centre, tap again offset by
`(offx,offy)` scaled by the centre's red channel, blend `(2·centre + offset)/3`, keep
alpha. This is the "glisten" glint. It is the whole shader; there are no loops, no
branches, no vertex shader.

All 27 pixel shaders are of this class: **full-screen passes over a single bound
texture** (`samplerState`), typically 1–12 taps, sometimes a short unrolled loop
(the blur family) or a small branch ladder (ink, camsplat, color). None use
texture arrays, cube maps, vertex fetch, exports, or memory export.

### 3.3 Where the code sits (bloom worked example)

`bloom` `ShaderContainer` (1016 bytes): `flags=0x102A1100`, `virtualSize=388`,
`physicalSize=628`, `constantTableOffset=36`, `shaderOffset=352`; the `Shader`
descriptor gives `physicalOffset=64`, `size=564` → microcode at
`388 + 64 = 452 … +564 = 1016`. The `virtual` region holds the constant table (with
`ps_3_0`/`2.0.6274.0`) and the definition table; the microcode is the `physical` region.
The decompiled body samples 8 offset pairs (the `offsets[12]` parameter) scaled by the
constant `0.0237` and folds them with component-wise comparisons, then scales by `0.2` —
a bloom blur.

---

## 4. Recovery-option assessment

### 4.1 Is there embedded source or a usable constant table?

* **Embedded HLSL/GLSL: NO.** No source strings of any kind in any of the 25 payloads.
* **Readable constant table: YES.** Every effect exposes parameter names, D3DX types and
  classes, semantics (e.g. bloom's `samplerState`, `offsets`; RadialBlur's
  `WorldViewProjection`, `DiffuseTexture` with semantic `DiffuseMaterialTexture`), default
  values, sampler states, render states, technique/pass names, and the shader profile.
  This is enough to reconstruct the *interface* faithfully; it does not give the *body*.

### 4.2 Can the microcode be translated? — **Yes, and it works here**

| route | what it does | verdict |
|---|---|---|
| **hedge-dev/XenosRecomp** (C++17) | Xenos microcode → **HLSL** | **Proven.** Built here with `clang++ -std=c++17`; fed the raw XNA `ShaderContainer` blobs directly; produced HLSL for **all 29 blobs**. See §5 reproduction. |
| **ReXGlue / Xenia SPIR-V translator** (`src/graphics/pipeline/shader/`) | Xenos microcode → **SPIR-V** (→ GLSL) | Present in the reference SDK (`spirv_translator*.cpp`, `translator*.cpp`, `ucode.h`). The prebuilt `librexgpu-xenos*.so` exports no `rex::` translator symbols, so it was not wired up here; this is the same code path Xenia uses and is a solid fallback. |
| **XNA 3.1 `ShaderCompiler.AssembleFromSource`** (official) | `xps_3_0`/`xvs_3_0` microcode ⇄ disassembly | Windows-only P/Invoke into `XNANative*.dll`. `rshadercompiler` (fgsfdsfgs) bundles `XnaNative1.dll`; not usable on this Linux host. |
| **No recompiler at all** | — | Dead end: the microcode cannot be loaded by MonoGame/MojoShader. |

The practical consequence: **translation is a solved problem for this game.** XenosRecomp
already emits readable, essentially faithful HLSL. The remaining work is *not*
"understand the shaders"; it is "turn the generated HLSL into clean MGCB effects", which
is mechanical because the shaders are tiny full-screen passes.

### 4.3 Recommendation: **auto-translate as blueprint, then re-author and compile**

**Do not attempt to ship translated bytecode/SPIR-V directly.**
* MonoGame loads its own **MGFX** effect format, produced by MGCB from HLSL, not raw
  SPIR-V; there is no MonoGame path that ingests Xenos microcode.
* MonoGame's Android backend is OpenGL ES, whose effect path runs
  HLSL → (DX9 bytecode via fxc) → MojoShader → GLSL. Even a perfect SPIR-V translation
  would not slot into that pipeline.
* The port already needs the 19 effects' *parameters* wired to the game code; a hand
  re-author lets us match them exactly.

**Recommended workflow per effect (≈1–2 h each):**
1. Run XenosRecomp on the extracted `ShaderContainer` blob → reference HLSL
   (`<dev-notes>/shaders/decompiled/`).
2. Cross-check the interface against the reflection (parameter names/types/defaults in
   `inventory.md`).
3. Re-write as a small MonoGame `.fx` with the same parameter names, a pass-through
   sprite VS, and the same tap structure; use the `#if OPENGL` guards (see §5).
4. Compile with MGCB and visually compare against the Xbox capture if available.

**Justification for re-author over direct translation:**
* The shipping shaders are simple and well understood after translation — this is the
  cheapest, most controllable path.
* It produces first-class MonoGame content that works on both the GL and DX build targets.
* The generated HLSL acts as a correctness oracle, so re-authoring risk is low.

**Effort estimate** (one shader-literate engineer, with the decompiled reference in hand):

| work | estimate |
|---|---|
| Build XenosRecomp harness + extract all blobs | **0.5 d** (done here; scripts + reference shipped) |
| Re-author 16 shipping shaders (blur family counts once) | **3–6 d** |
| Wire parameter names / texture binding to the port (`Effect.Parameters`, sampler slots) | **1–2 d** |
| Pixel-compare against original captures / tune constants | **2–4 d** |
| Dead assets (`RadialBlur` incl. the only VS, `blood`, `blood2`, `forefade`, `glisten`, `radblur`) | **optional, +1–2 d** |
| **Total (shipping set)** | **≈7–13 engineer-days** |

The earlier `decompile-inventory.md` rated shaders "HIGH" effort; with the translation
tool built and verified, the honest rating drops to **MEDIUM**, bounded by tuning rather
than by reverse-engineering.

---

## 5. Re-authored sketches + MGCB compilation (proof of the fallback path)

Re-authored files live in `src/Dishwasher/Content/fx/`:

| file | based on | deduced operation |
|---|---|---|
| `Sprite.fx` | (SpriteBatch baseline) | transform + texture × vertex colour |
| `Tint.fx` | `fx/color` (PostColor) | luminance → 2-colour ramp × `alpha` |
| `SpriteShadow.fx` | (shadow primitive) | alpha-silhouette drop shadow |
| `Bloom.fx` | `fx/bloom` (PostBloom) | 12-tap offset blur + bright pass (`offsets`) |
| `Blur.fx` | `fx/blur` (Blast) | unrolled radial blur (`levs`, `width`, `xcenter/ycenter`, `blackblood`) |
| `Ink.fx` | `fx/ink` (Ink) | alpha-threshold ink silhouette (`bright`) |

Each keeps the original XNA parameter names in comments, uses a standard pass-through
sprite vertex shader (the originals are pixel-only because XNA's SpriteBatch supplied the
geometry), and is guarded so the same source targets both profiles:

```hlsl
#if OPENGL
#define SV_POSITION POSITION
#define SV_TARGET COLOR
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define SV_TARGET SV_Target0
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#endif
```

### 5.1 Verified MGCB compile (native DXC, no Wine)

Test project in `/tmp/opencode/fxverify` (copies of the six files), `Verify.mgcb` with
`/platform:XboxOne`:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd /tmp/opencode/fxverify
mgcb Verify.mgcb
```

**Result (exact):**
```
/tmp/opencode/fxverify/Sprite.fx
/tmp/opencode/fxverify/Tint.fx
/tmp/opencode/fxverify/SpriteShadow.fx
/tmp/opencode/fxverify/Bloom.fx
/tmp/opencode/fxverify/Blur.fx
/tmp/opencode/fxverify/Ink.fx

Build 6 succeeded, 0 failed.

Time elapsed 00:00:00.49
```
Outputs: `bin/{Sprite,Tint,SpriteShadow,Bloom,Blur,Ink}.xnb` (9.5–13.8 KB each).
MGCB used the **native linux-x64 DXC** bundled in `dotnet-mgcb 3.8.5.1`
(`…/linux-x64/bin/dxc`, `libdxcompiler.so`).

### 5.2 Android / OpenGL profile — host limitation

The same project with `/platform:Android` fails **before compiling any shader**:

```
Error: MGFXC0001: MGFXC effect compiler requires a valid Wine installation ...
/tmp/opencode/fxverify/Sprite.fx: The type initializer for
  'MonoGame.Effect.Compiler.WineHelper' threw an exception.
Build 0 succeeded, 6 failed.
```

This is a MonoGame 3.8.5 limitation, not a shader error: the GL/Android profile runs
`dotnet c:\fxccs.dll …` **inside Wine** (`MonoGame.Effect.Compiler.WineHelper`) to obtain
DX9 bytecode for MojoShader. It needs `wine`/`wine64`, a `winepath`, and a
`MGFXC_WINE_PATH` prefix containing `fxccs.dll`. Neither Wine nor a prefix is installed on
this host (and there is no passwordless `sudo` to add it). The `#if OPENGL` branch of the
sketches is the input that branch expects; on a Windows build host (or a Linux host with
the Wine prefix provisioned per the MGCB docs) the same `.fx` files compile for Android.

▸SPEC: Because the Android/GL compile could not be executed here, Android-specific
correctness of the `vs_3_0/ps_3_0` branch is inferred from the MonoGame template
conventions, not observed. The DX12/SM6 branch **is** observed to compile.

### 5.3 XenosRecomp reproduction

```bash
# 1. parse the 25 Effect XNBs -> payloads and shader blobs
python3 <dev-notes>/shaders/tools/parse_fx.py        # dumps payloads/
python3 <dev-notes>/shaders/tools/extract_blobs.py   # writes blobs/*.bin
# 2. get XenosRecomp (main branch), then build a minimal harness:
clang++ -std=c++17 -fms-extensions -DFMT_HEADER_ONLY \
        -include pch.h -I. -I<fmt>/include \
        harness.cpp shader_recompiler.cpp -o xr
# 3. translate any blob (input is the raw ShaderContainer)
./xr blobs/bloom_t0p0_PS.bin out.hlsl shader_common.h
```
The only edits to XenosRecomp were a trimmed `pch.h` (removing DXC/smolv/xxhash/zstd,
which the single-shader HLSL path does not use) and the ~15-line `harness.cpp`; both are
preserved in `<dev-notes>/shaders/tools/`. **These are derived from an MIT-licensed
project and are for analysis, not shipped content.**

---

## 6. Measured vs. hypothesised

**Measured (hard evidence):**
* All 25 Effect XNBs are XNB v2 / platform `x` / uncompressed; `EffectReader` payload =
  `int32 LE length + bytes`, bytecode starts at 0x42.
* The payload is D3DX `fx_2_0` (`FE FF 09 01`), **big-endian**; MojoShader/Wine's reader
  (adapted to BE) consumes **all 25** payloads exactly.
* Full parameter/technique/pass/state/object inventory (names, types, classes, semantics,
  defaults) for all 25 effects; 19 are loaded by the game, 6 are dead.
* 29 shader blobs, 28 distinct by SHA-256; 20 shipping blobs all distinct; 27 PS + 2 VS
  (the VS are only in dead `RadialBlur`).
* Each shader blob is a Xenos `ShaderContainer` (`0x102A1100`/`0x102A1101`,
  `virtualSize+physicalSize == blob length`, `field1C==field20==0`) — matching
  XenosRecomp's scanner exactly.
* XenosRecomp (built here) translated all 29 blobs to HLSL.
* Six re-authored `.fx` sketches compile with MGCB 3.8.5.1 `/platform:XboxOne`
  (**6 succeeded, 0 failed**); `/platform:Android` needs Wine (not installed).

**Hypothesised / needs confirmation during the port:**
* That `Content.Load<Effect>("fx/…")` sites map 1:1 to the effect files that exist — the
  6 dead assets may be remnants of cut features (▸SPEC, strongly implied by the absence
  of any `Load` call).
* That the game draws the post-process effects through `SpriteBatch` with a full-screen
  quad (the original effects have no vertex shader; XNA's SpriteBatch supplies the VS).
  ▸SPEC.
* The exact visual intent of a few branchy shaders (`color`, `camsplat`, `ink`) is
  inferred from the decompiled HLSL plus parameter names, not from art-direction docs.
  ▸SPEC.
* That `2.0.6274.0` is the XNA 3.0 effect-compiler version. ▸SPEC.
* Android/OpenGL correctness of the `#if OPENGL` branch (untested here; see §5.2). ▸SPEC.

---

## 7. Artifacts produced

| path | contents |
|---|---|
| `<dev-notes>/shaders/inventory.md` | full per-effect parameter/technique/blob table |
| `<dev-notes>/shaders/decompiled/*.hlsl` | XenosRecomp HLSL for all 29 blobs |
| `<dev-notes>/shaders/payloads/*.bin` | raw 25 effect payloads (D3DX `fx_2_0`, BE) |
| `<dev-notes>/shaders/blobs/*.bin` | raw 29 Xenos `ShaderContainer` blobs |
| `<dev-notes>/shaders/tools/effect_parse.py` | BE `fx_2_0` container parser (MojoShader port) |
| `<dev-notes>/shaders/tools/parse_fx.py` | XNB → effect payload extractor |
| `<dev-notes>/shaders/tools/extract_blobs.py` | effect → `ShaderContainer` blob extractor |
| `<dev-notes>/shaders/tools/xenosrecomp_harness.cpp` | minimal XenosRecomp driver |
| `src/Dishwasher/Content/fx/*.fx` | six re-authored, MGCB-compilable sketches |
