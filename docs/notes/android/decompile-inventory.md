# The Dishwasher: Dead Samurai — Decompilation Inventory & Modernization Surface

**Author:** Decompilation & Modernization Analyst (subagent)
**Date:** 2026-10-02
**Input trees (read-only):** `<managed-decompile>/*.dll|*.exe`
**Outputs:** `<managed-decompile>/decompiled/game/`, `<managed-decompile>/decompiled/xna3/`
**Toolchain:** ilspycmd 9.1.0.7988 (ICSharpCode.Decompiler 9.1.0.7988), .NET SDK 8.0.425, `source tools/scripts/env.sh`.

> Scope note: This is a *measurement* report. Byte-level asset facts live in
> `asset-formats.md`; this file answers "how much C# must change to run on
> MonoGame/Android, and does the original parser code survive?"

---

## 0. TL;DR

* **Decompilation is essentially perfect.** `game.exe` → **64 `.cs` / 59,813 LOC / 1.7 MB**;
  `Microsoft.Xna.Framework.dll` → **374 `.cs` / 42,523 LOC / 2.4 MB**.
* **ilspycmd emitted 0 warnings** (empty stdout+stderr, exit 0 for both runs). No
  `// Failed to decompile`, no `NotImplementedException` markers in 59,813 LOC.
* **`RenderState` is never used — 0 occurrences.** This is *not* a classic XNA-3
  state-machine port. Rendering is `SpriteBatch` + `Effect.CurrentTechnique` +
  `RenderTarget2D`.
* **The `.zdx` / `.dqx` / `.dgx` parsers are ALL present in the decompiled C#** —
  no reverse-engineering is needed for parsing; only the file-I/O layer changes.
  Section 4.
* **Bounded build against net35 + the XNA 3.0 DLLs produced only 5 real errors,
  all `Thread.SetProcessorAffinity`** (a .NET CF / Xbox 360-only API). The
  decompiled source is otherwise compilable. Section 5.
* The porting mass is **console/LIVE services (17 files), XACT audio (10 files),
  and shader re-authoring (25 effects)** — not the graphics statusing. Section 6.

---

## 1. Decompiled tree / size / types

### 1.1 Commands used

```bash
source tools/scripts/env.sh
ilspycmd --disable-updatecheck --nested-directories -p -r <managed-decompile> \
  -o <managed-decompile>/decompiled/game <managed-decompile>/game.exe
ilspycmd --disable-updatecheck --nested-directories -p -r <managed-decompile> \
  -o <managed-decompile>/decompiled/xna3 <managed-decompile>/Microsoft.Xna.Framework.dll
```

`--nested-directories` mirrors namespaces to folders. Full stdout/stderr captured to
`/tmp/opencode/game_decompile.{out,err}` and `/tmp/opencode/xna_decompile.{out,err}`.

### 1.2 Summary table

| Tree | Path | `.cs` files | LOC | Disk | `.csproj` |
|---|---|---:|---:|---:|---|
| Game | `managed/decompiled/game/` | **64** | **59,813** | 1.7 MB | `game.csproj` (`net35`, `LangVersion 12.0`, `AllowUnsafeBlocks`) |
| XNA 3.0 ref | `managed/decompiled/xna3/` | **374** | **42,523** | 2.4 MB | `Microsoft.Xna.Framework.csproj` |

### 1.3 Game type inventory (authoritative, via `ilspycmd -l`)

| Kind | Count |
|---|---:|
| Classes | **82** (+ `<Module>`) |
| Structs | **6** (`Settings.Save`, `Save`, `Save2`, 3× `PrivateImplementationDetails.__StaticArrayInitTypeSize=` 16/28/60) |
| Interfaces | **1** (`StringContainer.IStringBit`) |
| Enums / Delegates | 0 / 0 |

Namespaces (4): `projectDish` (60 files, incl. nested `projectDish.NetStuff`),
`dishX.Resources`, `Microsoft.Xna.Framework` (one helper: `GameResourceManager.cs`).

Largest game source files (LOC) — the port will concentrate here:

| File | LOC | Role |
|---|---:|---|
| `projectDish/Character.cs` | 6,259 | player/AI character runtime |
| `projectDish/MainMenu.cs` | 6,115 | menus + LIVE flows |
| `projectDish/Game1.cs` | 5,301 | `Game` subclass / boot / render loop |
| `projectDish/Map.cs` | 5,174 | level data + **`.zdx` parser/interpreter** |
| `projectDish/HUD.cs` | 4,116 | HUD |
| `projectDish/MainText.cs` | 4,114 | text / leaderboards |
| `projectDish/SpriteManager.cs` | 3,823 | draw layer |
| `projectDish/Globals.cs` | 2,838 | global state |
| `projectDish/Netplay.cs` | 2,519 | **NetworkSession multiplayer** |
| `projectDish/Text.cs` | 2,409 | font/text |
| `projectDish/CSprite.cs` | 2,329 | sprite primitive |
| `projectDish/StringContainer.cs` | 397 (+ `Strings.cs` 2,471 generated) | localization |

### 1.4 XNA 3.0 reference type inventory

Classes **264**, structs **114**, interfaces **3**, delegates **4**, enums **88** across
9 namespaces (`Microsoft.Xna.Framework{,.Audio,.Content,.GamerServices,.Graphics,
.Graphics.PackedVector,.Input,.Net,.Storage}`). Used as the 3.x→4.0 API mapping
reference.

---

## 2. XNA 3.x-only API surface

### 2.1 `RenderState` — **ZERO**

| Pattern | Occurrences |
|---|---:|
| `RenderState` (any case, whole tree) | **0** |
| `GraphicsDevice.RenderState` | 0 |
| `AlphaBlendEnable`, `CullMode`, `DepthBufferEnable/WriteEnable`, `AlphaTestEnable/Function/ReferenceAlpha`, `BlendFunction`, `SourceBlend`, `DestinationBlend`, `ColorWriteChannels`, `FillMode`, `ScissorTestEnable`, `SeparateAlphaBlend*` | **0 each** |
| `GraphicsDevice.RenderState` property even exists in the XNA 3.0 ref (`xna3/.../Graphics/RenderState.cs:5`, `GraphicsDevice.cs:86`) | — |

**Conclusion:** the game never touches the XNA 3.0 fixed-function state block.
All blending/raster state is expressed through `SpriteBatch.Begin(SpriteBlendMode…)`
and per-`Effect` passes. Ranking table is therefore empty; the real work is §2.2.

### 2.2 Actual XNA 3.x-only / changed API ranking

| Rank | API (XNA 3.0 form) | Sites | XNA 4.0 / MonoGame replacement |
|---:|---|---:|---|
| 1 | `SpriteBatch.Begin(SpriteBlendMode)` (or `+SpriteSortMode, +SaveStateMode`) | **159** | `Begin(SpriteSortMode, BlendState, SamplerState, DepthStencilState, RasterizerState, Effect, Matrix)` |
| 1a | └ `SpriteBlendMode.AlphaBlend` | 124 | `BlendState.AlphaBlend` |
| 1b | └ `SpriteBlendMode.Additive` | 19 | `BlendState.Additive` |
| 1c | └ `SpriteBlendMode.None` | 16 | `BlendState.Opaque` |
| 2 | `SpriteSortMode.Immediate` (retained name) + `SaveStateMode.SaveState` | 19 / 19 | keep sort mode; **drop** `SaveStateMode` arg |
| 3 | `GraphicsDevice.SetRenderTarget(int, RenderTarget2D)` (indexed overload) | **21** (`SetRenderTarget` call tokens = 23) | `SetRenderTarget(RenderTarget2D)` — e.g. `GameRender.cs:76,109`; `Game1.cs:3568,3574,3577,…` |
| 4 | `new RenderTarget2D(dev,w,h,levels,fmt,MultiSampleType,quality)` (+4-arg) | **11 constructions** (29 `RenderTarget2D` refs) | new ctor `(dev,w,h,bool mipMap,fmt,DepthFormat,int sampleCount,RenderTargetUsage)`; `Game1.cs:575–580,867–874` |
| 5 | `PresentationParameters.MultiSampleType` / `.MultiSampleQuality` | 7 / 7 | removed; pass `int` sample count to `RenderTarget2D` / `GraphicsDeviceManager.PreferMultiSampling` |
| 6 | `Effect.CurrentTechnique.Passes[0].Begin()/.End()` | **20** `CurrentTechnique`, 73 `.Parameters[...]` (29 distinct names) | API shape kept, **but 360 effect bytecode must be recompiled** (see below) |
| 7 | `GraphicsDevice.Textures[1] = …` | **2** | `GraphicsDevice.Textures` collection exists in MonoGame; verify sampler-slot semantics (`GameRender.cs:97,103`) |
| 8 | `GraphicsDevice.CreationParameters.Adapter.IsWideScreen` | **1** | not in MonoGame (`Game1.cs:333`) |
| 9 | `AudioStopOptions.AsAuthored/.Immediate` (XACT) | 7 | no MonoGame equivalent (§4 audio) |
| — | `VertexDeclaration` / `VertexElement` / `VertexBuffer` / `IndexBuffer` / `Model` / `SamplerState(s)` / `DrawUserPrimitives` | **0 each** | nothing to migrate |
| — | `GraphicsDevice.Clear` (retained), `GraphicsDeviceManager`, `SurfaceFormat.Color`, `Keyboard/GamePad` | 30 / 4 / 10 | unchanged / minor |

**Retained & safe:** `Game`/`GraphicsDeviceManager`, `Clear`, `Texture2D`,
`SpriteFont`, `Vector2/3/4`, `Color`, `Rectangle`, `GamePad`/`Keyboard`,
`Effect.Parameters`. **MonoGame has no `SaveStateMode` and no indexed `SetRenderTarget`.**

### 2.3 Effects — the real graphics blocker

* `ContentManager.Load<Effect>` = **19 sites, 19 distinct effects**:
  `fx/{bloom,blur,bubble,burnblur,camsplat,color,comicblur,fade,grad,ink,lense,
  newblood,poster,redhaze,trail,trainblur,trainover,wallblood,water}` (asset tree
  has 25 `.xnb` effect files).
* These are **Xbox-360 effect microcode (`FE FF 09 01…`)** — unusable by
  MonoGame/MojoShader. There is **no HLSL source in the package** (asset ship only
  `.xnb`). The 19 effects must be **rewritten by hand** against the game's ~29 named
  effect parameters.

---

## 3. Content pipeline

`Game1.cs:237` sets `Content.RootDirectory = "."` — content keys are asset-relative
paths like `"gfx/sprites"`, `"fx/bloom"`. Any MonoGame content migration must
preserve this.

| `ContentManager.Load<T>` | Sites |
|---|---:|
| `Texture2D` | 38 |
| `Effect` | 19 |
| `SpriteFont` | 2 (`gfx/SpriteFont1_JPN`, `gfx/SpriteFont1_CHT`) |
| `CharDef` | 2 (custom reader) |
| **Total** | **61** |

* Calls are concentrated in `Game1.cs` (57), `Strip.cs` (2), `ThreadLoader.cs` (1),
  `Credits.cs` (1).
* Custom reader: `projectDish/CharReader.cs:6` (`ContentTypeReader<CharDef>`) — **only
  exercised on the non-default path** (`Globals.oldSchoolLoading == false`).
* 11 `.resources` + generated `dishX/Resources/Strings.cs` with **1,211 `GetString`
  keys**; culture switch at `MainText.cs:3149`, `Game1.cs:500`.

---

## 4. Asset parsers — **PRESENT in decompiled C#** ✅

This is the single most important confirmation for the port: **the original readers
for `.zdx`, `.dqx`, and `.dgx` are all in the managed game code.** No reverse
engineering is required to *parse* them; the formats can be treated as documented by
the decompiled readers. Work reduces to file-I/O + threading.

| Format | Parser (file:line) | I/O | Evidence |
|---|---|---|---|
| `.zdx` (251 text maps/scripts) | `projectDish/Map.cs` — `MapScript.setFromScript` (line **57**), `Map.Read` (line **4188**), `Map.Write` (line **4132**) | `StreamReader`/`StreamWriter` | ~**177** distinct command tokens (`tag`, `monster`, `goto`, `iffalsegoto`, `endlevel`, `set*exit/entrance`, …) emulated in a `switch`; text is line-oriented `Split(' ')` |
| `.zdx` (others) | `ComboList.cs:497` (`data/combos.zdx`), `MainMenu.cs:494` (`data/levels.zdx`), `Strip.cs:915,961` (`data/strips`, `data/bubbles.zdx`), `Arcade.cs:318` (`data/arcade.zdx`), `Map.cs:5126` (`gfx/maps/maps.zdx`) | `StreamReader` | 8 `.zdx` call sites total |
| `.dqx` (38 char/anim) | `projectDish/CharDef.cs` — `ReadBinary()` lines **101–180** | `BinaryReader(File.Open(...))` | Reads LE `Int32`/`Single` + `ReadString` (7-bit-prefixed names); frame → part icon/loc/rot/flip; animation → key index/duration/4 commands with command/level/param. **Active path**: `Globals.oldSchoolLoading = true` (`Globals.cs:1111`) |
| `.dqx` fallback `.xnb` | `CharReader.cs` `ContentTypeReader<CharDef>` | ContentManager | **Not used** by default; char `.xnb` absent from asset tree |
| `.dgx` (15 solo) | `projectDish/GuitarSolo.cs` — `ThreadRead()` line **905** (read `data/solos/solo_*.dgx`), `Write()` line **827** | `BinaryReader`/`BinaryWriter` | `float startTime/endTime`, `int beats`, string name, `beats*4+1` cue ints, `beats+1` pose/stage ints |

**Recorded format layouts (from the readers, for the report):**

* `.dqx` (LE): `legsIdx:i32, torsoIdx:i32, headIdx:i32, weaponAux:i32 (weapon=mod100, aux=div100),
  frameCount:i32`; per frame: `name:string`, `partCount:i32`; per part:
  `icon:i32, x:f32, y:f32, rot:f32, flip:i32`. Then `animCount:i32`; per anim:
  `name:string, numKeys:i32, keyCount:i32`; per key: `frameIdx:i32, duration:i32`;
  4 commands each: `packed:i32` (-1 = none; `cmd=mod1000`, `level=div1000`) then
  `type:i32` (0=none/1=string/2=int) + payload.
* `.dgx` (LE): `startTime:f32, endTime:f32, beats:i32, name:string`,
  `cue[0..beats*4]:i32`, `poseStage[0..beats]:i32` (`pose=mod100, stage=div100`).

Because `BinaryReader.ReadString`/`ReadInt32` are .NET-native, these readers port to
.NET 8/MonoGame unchanged except `File.Open` relative paths (`Content.RootDirectory="."`).

---

## 5. Bounded build attempt (net35 + XNA 3.0 refs)

A temporary copy was built (then deleted) with `game.csproj` HintPaths pointed at
`managed/*.dll`. The `net35` reference assemblies resolved automatically from the
cached `Microsoft.NETFramework.ReferenceAssemblies.net35` 1.0.3 package; XNA 3.0
DLLs were referenced directly.

```bash
dotnet build game.csproj -v n --nologo   # full log: /tmp/opencode/build_full.log
```

**Result: only 5 real errors (10 log lines), all one API; 32 warnings.**

| Error code | Unique sites | API |
|---|---:|---|
| CS1061 | **5** | `System.Threading.Thread.SetProcessorAffinity` |

All five:
`Globals.cs:1762`, `Game1.cs:702`, `ThreadLoader.cs:38`, `ThreadLoader.cs:61`,
`Netplay.cs:2035` — each `Thread.CurrentThread.SetProcessorAffinity(5);`.
This is a **.NET Compact Framework / Xbox 360-only** call (pin thread to CPU 5);
desktop .NET 3.5 and Android have no such API. **Delete or no-op.**

Warning categories (32 unique, all benign): `CS0169` unused field ×12, `CS0649` never
assigned ×9, `CS0414` assigned-but-unused ×7, `CS0219` unused local ×3, `CS0672`
obsolete-override ×1.

**Interpretation:** the decompilation is clean enough to compile. The modernize step
(MonoGame/XNA 4.0) will surface *far more* errors (SpriteBatch signatures, render
targets, LIVE removal), but those are all enumerated mechanically in §2–§4 — none
are decompiler artifacts.

---

## 6. Console / LIVE, audio, P/Invoke, platform APIs

### 6.1 Xbox LIVE / console services — **17 files**

| Type (XNA GamerServices/Net/Storage) | Sites | Notable files |
|---|---:|---|
| `Gamer` (`.SignedInGamers`, `.Gamertag`, …) | 100 | MainText, SpriteManager, Globals, Game1, Credits |
| `Guide.*` | **77** | see breakdown below |
| `SignedInGamer` | 40 | 20+ files |
| `NetworkSession` | 25 | `Netplay.cs`, `Leader.cs` |
| `LeaderboardReader` / `LeaderboardWriter` | 13 / 8 | `Leader.cs`, `MainText.cs`, `HUD.cs` |
| `StorageDevice` / `StorageContainer` | 12 / 7 | `Settings.cs`, `Player.cs`, `Game1.cs` |
| `LocalNetworkGamer` / `NetworkGamer` | 11 / 7 | `Netplay.cs` |
| `NetworkSessionProperties`, `SendDataOptions`, `PacketReader/Writer`, `AvailableNetworkSession` | 9/6/4/3/3 | `Netplay.cs` |
| `GamerProfile`, `AchievementCollection`, `SignedInGamerCollection`, `GamerServicesComponent`, `GamerPresence` | 2/2/1/1/1 | `Game1.cs:239` (`Components.Add(new GamerServicesComponent(this))`) |
| `Avatar`, `XLast` (code) | **0** | XLAST only as embedded `Microsoft.Xna.Framework.XlastConfiguration` resource |

`Guide.*` call breakdown: `IsVisible` 43, `BeginShowStorageDeviceSelector` 7,
`BeginShowMessageBox` 6, `SimulateTrialMode` 5, `IsTrialMode` 4, `ShowSignIn` 3,
`ShowGamerCard` 2, `ShowAchievements` 2, `EndShowStorageDeviceSelector` 2,
`ShowMarketplace` / `ShowFriends` / `EndShowMessageBox` 1 each.

Files: `Netplay.cs` (2,519 LOC), `Leader.cs` (897), `Achievements.cs` (511),
`Game1.cs`, `MainMenu.cs`, `Globals.cs`, `MainText.cs`, `Player.cs`, `Settings.cs`,
`HUD.cs`, `SpriteManager.cs`, `Map.cs`, `Presence.cs`, `Credits.cs`, `Character.cs`,
`GuitarSolo.cs`, `Strings.cs`. `Netplay.cs` implements a custom `PacketReader`/
`PacketWriter` protocol over `NetworkSession` (14 serialization sites).

### 6.2 Audio — XACT, **10 files / ~67 API refs**

| API | Sites | Files |
|---|---:|---|
| `AudioEngine` | 12 | `Sound.cs`, `VoxSound.cs`, `MusicSound.cs`, `GuitarSolo.cs` |
| `WaveBank` | 11 | same |
| `SoundBank` | 11 | same (incl. per-track `sfx/solo_*.xsb`) |
| `Cue` (`.Play/.Stop/.Pause/.Resume/.IsPlaying/…`) | 11 + call sites | `Music.cs`, `Credits.cs`, `GuitarHalper.cs`, `Map.cs`, `Intro.cs` |
| `AudioStopOptions` | 7 | as above |
| `AudioCategory` (`SetVolume`) | 2–3 | `Sound.cs` |
| `Xact` (literal) | 0 | — |

Banks referenced: `sfx/sfxproj.xgs`, `waves.xwb`, `sounds.xsb`, `vox.xwb`,
`voxsnd.xsb`, `music.xwb` (16 sub-banks), `musicsnd.xsb`, `halper.xwb`,
`halpsnds.xsb`, plus `sfx/solo_{crux,dish,smash}.{xsb,xwb}`. MonoGame has **no
XACT**; per `asset-formats.md`, 200/222 wave entries are Xbox 360 XMA and must be
transcoded (vgmstream) and the cue→wave map recovered from the `.xsb`.

### 6.3 P/Invoke

**Zero `[DllImport]`, zero `extern`, zero `Marshal`/`StructLayout`.** The only
`using System.Runtime.InteropServices` is generated `Properties/AssemblyInfo.cs:3`
(`[ComVisible(false)]`), not native calls. **No native shim layer required.**

### 6.4 .NET CF / Windows-specific APIs

| API | Sites | Verdict |
|---|---:|---|
| `Thread.SetProcessorAffinity` (.NET CF/X360) | **5** | only CF-specific API; delete/no-op |
| `System.Windows.Forms` / `Microsoft.Win32` / `Registry` / `System.Drawing` / P/Invoke | **0** | none — clean |
| `StorageDevice`/`StorageContainer` + `XmlSerializer` + `File.Open(container.Path…)` | 12/7 + 3 `XmlSerializer` | console/XNA storage → replace with Android file/`IsolatedStorage`; `Settings.cs:50–93,141–173` |
| `Thread`, `Thread.Sleep`, `GC.Collect`, `DateTime`, `ResourceManager`, `CultureInfo` | 12 thread sites etc. | portable to .NET 8; culture/`ResourceManager` need resource embedding |
| `Xact` / `GamerServices` / `Guide` | console-only | see §6.1/§6.2 |

### 6.5 Entry point / boot

* `projectDish.Program.Main` — `Program.cs:5`: `using Game1 game = new Game1(); game.Run();`
* `projectDish.Game1 : Microsoft.Xna.Framework.Game` — `Game1.cs:15`.
* Boot: ctor sets `Content.RootDirectory = "."` (`Game1.cs:237`) and adds
  `new GamerServicesComponent(this)` (`Game1.cs:239`); `Initialize()` sets
  screen/border and spawns `ThreadLoader` background char loading; a large
  `switch(loadPhase)` `Loader()` streams assets (`Game1.cs:443+`).

---

## 7. Ranked porting work (by effort)

| # | Workstream | Evidence | Volume | Effort | Depends on |
|---:|---|---|---:|---|---|
| 1 | **Strip/stub Xbox LIVE, Guide, achievements, leaderboards, storage, multiplayer** | 17 files; Guide 77, Gamer 100, SignedInGamer 40, NetworkSession 25; `Netplay.cs` 2,519 LOC, `Leader.cs` 897, `Achievements.cs` 511 | ~250+ sites | **HIGH** | design decisions (local vs Google Play), all UI flows |
| 2 | **Replace XACT audio** (transcode 222 XMA waves; parse `.xsb` cue map; new mixer over `SoundEffect`/native) | 10 files / ~67 refs; `Sound.cs`, `VoxSound.cs`, `MusicSound.cs`, `GuitarSolo.cs` | 67 refs + 7 banks | **HIGH** | vgmstream/ffmpeg; XSB parser |
| 3 | **Re-author 19–25 shaders** (360 microcode unusable, no HLSL source) | `Load<Effect>` 19 distinct; 73 `.Parameters[…]`, 29 names; 25 `fx/*.xnb` | 19 effects | **HIGH** | HLSL reconstruction |
| 4 | **Convert XNB v2 → MonoGame** (125 textures, 3 fonts; v2 unsupported) | `Load<Texture2D>` 38, `Load<SpriteFont>` 2; `asset-formats.md` §3.1 | 153 files | **MED** | batch converter (`Athari/XnaConvert` or custom) |
| 5 | **XNA 3→4 graphics statusing** (SpriteBatch/render-targets) | §2.2: 159 SpriteBatch.Begin, 21 SetRenderTarget, 11 `new RenderTarget2D`, 7+7 MultiSample | ~200 sites | **MED** | mechanical, but wide |
| 6 | **Android content/asset loading** (`Content.RootDirectory="."`, 61 `Load<T>`, custom `CharReader`, resource embedding) | `Game1.cs:237`, `Load<T>` 61 | 61 sites | **MED** | converted assets; MGCB pipeline |
| 7 | **Port `.zdx`/`.dqx`/`.dgx` readers to Android I/O** (code exists; just `File.Open` → asset extraction) | §4; `CharDef.cs:109`, `GuitarSolo.cs:827/905`, 8 `.zdx` sites | ~12 sites | **LOW** | none — parsers already exist |
| 8 | **Localization / `.resources` migration** (1,211 keys, 11 resources, culture switch) | `Strings.cs`, `MainText.cs:3149`, `Game1.cs:500` | 1,211 keys | **LOW–MED** | resource pipeline |
| 9 | **Input mapping** (GamePad/Keyboard only; no touch) | `GamePad.GetState/GetCapabilities/SetVibration`, `Keyboard.GetState`; `GamePadType.Guitar` | — | **MED** | existing `input-gamepad-plan.md` |
| 10 | **Remove .NET CF `SetProcessorAffinity`** | 5 sites, the only net35 compile errors | 5 | **TRIVIAL** | none |

### Effort-ranked one-liner

> Remove 5 CF calls (trivial) → port already-written binary/text parsers (low) →
> mechanical XNA-4 graphics statusing + content loading (med) → convert XNB/audio
> assets (med/high) → **rewrite shaders (high)** → **delete/stub LIVE+net+storage
> (high)**.

---

## 8. Measured vs. hypothesised

**Measured (hard evidence):** tree paths and sizes; 64 / 374 files; 59,813 / 42,523
LOC; type counts; 0 ilspycmd warnings; 0 `RenderState`; the §2–§6 frequency tables;
5 unique net35 compile errors all `SetProcessorAffinity`; the parser file:line list
and recorded `.dqx`/`.dgx` layouts.

**Hypothesised (needs confirmation during port):**
* That `GraphicsDevice.Textures[1] = renderTarget.GetTexture()` (`GameRender.cs:97`)
  maps cleanly to MonoGame's `Textures` collection semantics.
* That all 19 effects are truly required (some may be dead / replaceable with
  `SpriteBatch` states).
* That `oldSchoolLoading` (`.dqx`) is the intended shipping path (it is `true`, and
  no char `.xnb` exists in the asset tree — strong evidence, not proof).
* The 5 `SetProcessorAffinity(5)` calls are performance hints only (core index 5) and
  can be dropped without behavior change.
