# Audio layer — XACT → MonoGame replacement

Owner: Audio Specialist subagent. This directory is **not** part of the live
project; it holds the drop-in shim, the extraction tooling and this integration
guide. Nothing here edits the core project's `.cs` files.

## 0. TL;DR / the one surprising finding

`MonoGame.Framework.Android 3.8.5.1` **does ship** an XACT implementation in
`Microsoft.Xna.Framework.Audio`: `AudioEngine`, `WaveBank`, `SoundBank`, `Cue`,
`AudioCategory`, `AudioStopOptions`, `XactSound`, `XactClip`. It parses
`.xgs/.xwb/.xsb`, but expects **little-endian / Windows XACT** banks and
`SoundEffect`-decodable codecs. This game's banks are the **big-endian Xbox 360**
variants and 200/222 waves are **XMA**, so MonoGame's parser cannot read them.

Two ways forward:

* **Option A — convert the banks to little-endian.** Re-serialise `.xwb`/`.xsb`
  (and `.xgs`) as LE with PCM/ADPCM and let the game keep using MonoGame's own
  XACT classes. Zero game-code changes, but requires XSB/XGS serialisers. Not
  delivered (large, and the task asked for a shim); the parsers here are the
  groundwork.
* **Option B — this shim (delivered).** Keep the game's XACT-shaped call sites
  and back them with `SoundEffect`/`SoundEffectInstance` + an offline cue map.
  Requires changing one `using` line in 10 files.

## 1. Files here

```
notes/audio/
  README.md                     <- this file
  XactShim.cs? -> shim/XactShim.cs
  shim/XactShim.cs              drop-in MonoGame audio layer (namespace Dishwasher.Audio)
  tools/parse_xsb.py            big-endian XSB -> cue -> wave mapping (JSON)
  tools/parse_xwb.py            big-endian XWB entry metadata (codec/offsets/loops)
  tools/build_cue_assets.py     cue_map_raw.json -> runtime cue_map.json/.csv
  tools/decode_banks.sh         XWB -> vgmstream -i -> OGG (all) + MS-ADPCM WAV (SFX+solo)
  decode-verification.log       vgmstream per-bank stream counts + codec
```

## 2. Converted assets

Built under `<dev-workspace>/Content-sfx/` (the core project is active, so they were not
written into it). **Intended final location: `Dishwasher/Content/sfx/`.**

```
Content-sfx/
  cue_map.json       runtime cue -> {bank, category_id, files:[{file, loop}]}
  cue_map.csv        same as a table
  cue_map_raw.json   full parse (per-bank header + cue defs + categories)
  xwb_meta.json      per-wave entry metadata (codec, channels, rate, loop region)
  ogg/<bank>/<bank>_wNNN.ogg    222 files (all waves)          ~30 MB
  wav/<bank>/<bank>_wNNN.wav    214 files (6 banks, MS-ADPCM)  ~25 MB
```

`NNN` is the **0-based wave index in the bank** — exactly the `trackIndex`
stored in the `.xsb`. `wav/` is omitted for `music` (8 long tracks stay OGG-only
to keep the footprint small; add `music` to `ADPCM_BANKS` in `decode_banks.sh`
to emit WAV for it too).

### Android packaging (important)

MonoGame Android's `TitleContainer.OpenStream("sfx/...")` calls
`Application.Context.Assets.Open("sfx/...")` **directly** — there is no
`Content/` prefix. The game's `new AudioEngine("sfx/sfxproj.xgs")` etc. therefore
need the files at APK `assets/sfx/...`. Add an Android asset item that strips the
`Content/` prefix, e.g.:

```xml
<ItemGroup>
  <AndroidAsset Include="Content\sfx\**\*"
                Link="sfx\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

(Confirm against the project's existing Content packaging; the MonoGame content
task's own output lives under `assets/Content/`.) If you prefer the content
pipeline, add the files to `Content.mgcb` and let MGCB place them.

## 3. Integration steps

1. Copy `Content-sfx/*` into `Dishwasher/Content/sfx/` and package it as above.
2. Copy `shim/XactShim.cs` into the project.
3. In **these 10 files** replace `using Microsoft.Xna.Framework.Audio;` with
   `using Dishwasher.Audio;`:
   `Sound.cs`, `VoxSound.cs`, `MusicSound.cs`, `Music.cs`, `GuitarSolo.cs`,
   `GuitarHalper.cs`, `Map.cs`, `Globals.cs`, `Credits.cs`, `Intro.cs`.
   (Verified: the only audio types these files reference are `AudioEngine`,
   `WaveBank`, `SoundBank`, `Cue`, `AudioStopOptions`, `AudioCategory` — all
   provided by the shim. No `SoundEffect`/`SoundEffectInstance` is referenced.)
4. Optional (for OGG music): add
   `<PackageReference Include="NVorbis" Version="0.10.5" />` and
   `<DefineConstants>$(DefineConstants);DISHWASHER_OGG</DefineConstants>`.
   Without it the shim still compiles; WAV/ADPCM cues play, OGG-only music is
   silent instead of crashing.
5. Build. No other game code changes are needed.

## 4. API mapping table

| Game call site (XNA XACT)              | Shim (`Dishwasher.Audio`)        | Backed by |
|---|---|---|
| `new AudioEngine("sfx/sfxproj.xgs")`   | `AudioEngine(string)`            | category registry; `.xgs` not parsed (category list is fixed) |
| `engine.GetCategory(name)`             | `AudioEngine.GetCategory`        | `AudioCategory` bus |
| `cat.SetVolume(v)`                     | `AudioCategory.SetVolume`        | reapplies volume to live instances |
| `engine.Update()`                      | `AudioEngine.Update`             | reaps finished one-shot cues |
| `new WaveBank(engine, path)`           | `WaveBank(AudioEngine,string)`   | token only (map already extracted) |
| `new WaveBank(engine, path, 0, 16)`    | `WaveBank(...,int,short)`        | streaming flag recorded, not used |
| `new SoundBank(engine, path)`          | `SoundBank(AudioEngine,string)`  | loads `sfx/cue_map.json` |
| `sound.PlayCue(name)`                  | `SoundBank.PlayCue`              | `GetCue(name).Play()`; never throws |
| `sound.GetCue(name)`                   | `SoundBank.GetCue`               | `Cue`; unknown name → silent cue |
| `cue.Play()`                           | `Cue.Play`                       | `SoundEffect.CreateInstance()` + `Play` |
| `cue.Stop(AudioStopOptions)`           | `Cue.Stop`                       | `SoundEffectInstance.Stop()` (both options stop now) |
| `cue.Pause()` / `cue.Resume()`         | `Cue.Pause` / `Cue.Resume`       | instance state |
| `cue.IsPlaying/.IsPaused/.IsStopped`   | `Cue` properties                 | instance `SoundState` (`IsPlaying` true while paused, like XNA) |
| `cue.IsPreparing`                      | `Cue.IsPreparing`                | always `false` |
| `cue.IsDisposed` / `cue.Dispose()`     | `Cue` members                    | instance disposal |
| `cue.Name`                             | `Cue.Name`                       | cue map key |
| `AudioStopOptions.AsAuthored/.Immediate` | `AudioStopOptions` enum        | shim stops immediately (no authored release tail) |
| `cue.Apply3D(listener, emitter)`       | `Cue.Apply3D`                    | distance attenuation + constant-power pan (unused by game) |

## 5. Behaviour notes

* **Variations**: complex cues (XSB variation tables / multi-event clips) are
  flattened to their full set of referenced waves. `Cue.Play()` cycles through
  them (`_variation = (_variation + 1) % count`). This is equivalent to XACT's
  *Ordered* playlist; the game's original playlists are mostly ordered or
  weighted, and the exact per-play weight/RNG behaviour is not reproduced.
* **Looping**: only `music` and `solo_*` cues loop (`loop:true` in the map).
  Every XMA entry in the XWB has a whole-stream loop region, so the raw XWB loop
  flag cannot distinguish one-shot SFX from music — bank membership is used.
* **Category volume**: cue → `category_id` from the XSB (XGS order:
  `0 Global, 1 Default, 2 Music, 3 solo_bg, 4 solo_fg, 5 halper`).
* **Missing cues** are silent no-ops (XNA throws; the game's `Music` ctor would
  crash). A one-time `Debug` warning is logged per missing name.
* **Memory**: WAV/ADPCM effects are cached; long OGG music is re-decoded on
  demand (not cached) to bound memory. See `SoundLoader.ShouldCache`.

## 6. Regenerating the assets

```sh
python3 tools/parse_xsb.py --json <out>/cue_map_raw.json assets-clean/sfx/*.xsb
python3 tools/parse_xwb.py --json Content-sfx/xwb_meta.json assets-clean/sfx/*.xwb
bash    tools/decode_banks.sh                                # vgmstream + ffmpeg
python3 tools/build_cue_assets.py                            # cue_map.json/.csv
```

Tool versions (validated): **vgmstream-cli r2117** (2026‑05‑19),
**ffmpeg 8.0.1-3ubuntu2**, Python 3.14.4.

## 7. Known gaps

* `AudioStopOptions.AsAuthored` release/fade tails are not modelled.
* RPC curves, 3D RPCs, filter/reverb DSP, instance limiting and per-play
  probabilities from the XSB/XGS are ignored (the game does not call the
  accessors for them).
* `music` bank is OGG-only; needs `DISHWASHER_OGG` (NVorbis) or a WAV rebuild.
* XMA itself is not decodable by MonoGame; all audio is pre-decoded here.
