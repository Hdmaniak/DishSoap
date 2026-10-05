# Audio conversion & XACT replacement — results

Scope: recover the XACT cue→wave mapping from the 7 Xbox 360 `.xsb` sound banks,
transcode the 7 `.xwb` wave banks, produce runnable audio assets, and design a
drop-in MonoGame audio layer. Read-only source: `assets-clean/sfx/` (15 files).
Target: MonoGame for Android (`MonoGame.Framework.Android 3.8.5.1`).

**Headline numbers**

* **216 cue names recovered** across 7 sound banks (0 collisions).
* **222/222 waves decoded**, 200 XMA + 22 PCM, **0 failures**.
* Output: **222 OGG (30 MB)** + **214 MS-ADPCM WAV (25 MB)** + cue map, **55 MB total**.
* Shim: `<dev-notes>/audio/shim/XactShim.cs` — compiles clean against
  MonoGame 3.8.5.1 (validated both with and without the optional OGG path).

---

## 1. Tooling (exact versions and commands)

| Tool | Version | How obtained |
|---|---|---|
| vgmstream-cli | **r2117** (2026‑05‑19) | `vgmstream-linux.zip` from the r2117 GitHub release, extracted to `/tmp/opencode/tools/vgmstream-cli` |
| ffmpeg | **8.0.1-3ubuntu2** | `apt-get install -y ffmpeg` |
| Python | 3.14.4 | system |

Decode command (`tools/decode_banks.sh`), per bank:

```sh
vgmstream-cli -i -S 0 -o '<work>/wav/<bank>/w?s.wav' <bank>.xwb   # XMA/PCM -> PCM16 WAV
ffmpeg -i <wav> -c:a libvorbis -q:a 4 <bank>_w<idx>.ogg            # size-sane set (all banks)
ffmpeg -i <wav> -c:a adpcm_ms          <bank>_w<idx>.wav           # zero-dep set (SFX + solo)
```

`-i` (single pass, no loop fade) is essential: vgmstream's default `-l 2 -f 10`
decodes two loops plus a 10 s fade, which doubled the music/solo output and
appended a fade. Verification log: `<dev-notes>/audio/decode-verification.log`.

---

## 2. Bank inventory (measured from the parsers)

`.xwb` header: `44 4E 42 57` (`DNBW` = byte-swapped `WBND`), tool v**43**,
header v**42**, big-endian. `.xsb` header: `4B 42 44 53` (`KBDS` = `SDBK`),
tool/format v**43/43**, platform byte **0x03** (X360), big-endian.
`.xgs`: `FSGX` (=`XGSF`), 6 categories.

| wave bank | entries | XMA | PCM | ↔ sound bank | cues (simple/complex) |
|---|---:|---:|---:|---|---:|
| `waves.xwb` | 106 | 89 | 17 | `sounds.xsb` | 88 (68/20) |
| `vox.xwb` | 87 | 87 | 0 | `voxsnd.xsb` | 54 (16/38) |
| `halper.xwb` | 15 | 10 | 5 | `halpsnds.xsb` | 60 (60/0) |
| `music.xwb` | 8 | 8 | 0 | `musicsnd.xsb` | 8 (8/0) |
| `solo_crux.xwb` | 2 | 2 | 0 | `solo_crux.xsb` | 2 (2/0) |
| `solo_dish.xwb` | 2 | 2 | 0 | `solo_dish.xsb` | 2 (2/0) |
| `solo_smash.xwb` | 2 | 2 | 0 | `solo_smash.xsb` | 2 (2/0) |
| **total** | **222** | **200** | **22** | | **216** |

XGS category ids (order in `sfxproj.xgs`):
`0 Global, 1 Default, 2 Music, 3 solo_bg, 4 solo_fg, 5 halper`.

---

## 3. Cue mapping — evidence and location

The 360 `.xsb` layout was reverse-engineered and ported (big-endian) from
MonoGame's own `SoundBank.cs`/`XactSound.cs`/`XactClip.cs`. **Two 360-specific
deviations** from MonoGame's PC layout were found by inspecting the bytes:

1. Complex-sound **clip records are 5 bytes** on 360
   (`u8 volumeDb, u32 clipOffset`), not the 9-byte PC record that also stores two
   filter fields.
2. Standalone **variation tables** are
   `u16 marker(0x000b), u16 count, u32 0xffffffff, count × (u32 soundOffset, u8 wMin, u8 wMax)`
   — a different header from the PC inline-Wave layout.

Everything else (sound header, event ids 1/3/4/6, cue tables) matched the
MonoGame algorithm once byte-swapped. The parser is `tools/parse_xsb.py`.

**Recovered data lives as data (all 216 cues, 308 cue→wave refs):**

* `Content-sfx/cue_map.json` — runtime map consumed by the shim.
* `Content-sfx/cue_map.csv` — same as a table (`bank,cue,type,variation,wave_bank,wave_index,file,kind,loop`).
* `Content-sfx/cue_map_raw.json` — full per-bank parse (header, categories, cue defs).

Cross-checks (all pass, 0 mismatches):

* **51/51** distinct literal cue strings found in the decompiled game
  (`Sound.playCue`, `getCue(...)`) resolve in the map.
* All dynamic cues resolve: `musicarena/music1..4/musicboss/death2/soft1` and
  `solo_{crux,dish,smash}_{fg,bg}`.
* The 60-entry `GuitarHalper.audioString[]` array matches the 60 `halpsnds` cues
  exactly (e.g. `a_chord1 → halper[0]`, `e_hammer5 → halper[14]`).
* Cue names are globally unique across all banks (no collisions).

Representative rows (full table in `Content-sfx/cue_map.csv`):

| cue | bank | type | category | → wave file(s) |
|---|---|---|---|---|
| `crush` | sounds | simple | Default | `waves_w009` |
| `shop` | sounds | simple | Default | `waves_w074`, `waves_w026` |
| `chunk` | sounds | complex | Default | `waves_w084`, `w074`, `w069`, `w070` |
| `dishgrowl` | voxsnd | complex | Default | `vox_w000`, `w004`, `w005` |
| `a_chord1` | halpsnds | simple | halper(5) | `halper_w000` |
| `music1` | musicsnd | simple | Music | `music_w002` (loop) |
| `musicboss` | musicsnd | simple | Music | `music_w000` (loop) |
| `solo_crux_bg` | solo_crux | simple | solo_bg | `solo_crux_w000` (loop) |
| `solo_crux_fg` | solo_crux | simple | solo_fg | `solo_crux_w001` (loop) |

---

## 4. Decode results

* **222/222 streams decoded.** Per-bank stream counts from vgmstream (`-m`):
  waves 106, vox 87, halper 15, music 8, solo_crux/dish/smash 2 each = 222.
  All report `encoding: Xbox Media Audio 2` (except the 22 PCM entries).
* Every decoded wave produced a file; `decode-verification.log` shows no errors.
  Only the deliberate OGG/ADPCM encoding is lossy.
* Loop metadata: every XMA entry carries a whole-stream loop region
  (`loop_offset=384`, `loop_length≈stream samples`). This cannot distinguish
  one-shot SFX from music, so looping is assigned by bank
  (`music` + `solo_*` = 14 looping cues).

---

## 5. Output assets

Location (core project is owned by another agent, so written outside it):
**`<dev-workspace>/Content-sfx/`**, intended final path **`Dishwasher/Content/sfx/`**.

```
Content-sfx/
  cue_map.json / cue_map.csv / cue_map_raw.json / xwb_meta.json
  ogg/<bank>/<bank>_wNNN.ogg   222 files, 30 MB   (all waves; Vorbis q4)
  wav/<bank>/<bank>_wNNN.wav   214 files, 25 MB   (MS-ADPCM; 6 banks)
  TOTAL 55 MB
```

`NNN` = 0-based wave index in the bank = the `.xsb` `trackIndex`. No `cues/`
hardlink tree is shipped (it inflates copies); the JSON/CSV map is canonical and
`build_cue_assets.py --links` regenerates hardlinks on demand.

Packaging note: MonoGame Android's `TitleContainer.OpenStream("sfx/...")` calls
`Application.Context.Assets.Open("sfx/...")` directly, so the files must land at
APK `assets/sfx/...`. See `notes/audio/README.md §2` for the `AndroidAsset` item.

---

## 6. Shim design

File: **`<dev-notes>/audio/shim/XactShim.cs`** (namespace `Dishwasher.Audio`).

Because `MonoGame.Framework.Android 3.8.5.1` already defines
`Microsoft.Xna.Framework.Audio.{AudioEngine,WaveBank,SoundBank,Cue,AudioCategory,AudioStopOptions}`,
the shim cannot use that namespace (duplicate type error). It re-implements the
same surface in `Dishwasher.Audio` on top of `SoundEffect`/`SoundEffectInstance`.
Integration is a one-line `using` swap in the 10 files that use the XACT types
(listed in the README); those files reference only the 6 XACT types, so the swap
is clean.

* `SoundBank` loads `sfx/cue_map.json` via `TitleContainer`, builds cue
  definitions, and resolves names. Unknown names yield a **silent `Cue`** instead
  of throwing (the game's `Music` ctor would otherwise crash).
* `Cue.Play()` lazily loads the referenced file and creates a
  `SoundEffectInstance`, sets `IsLooped` (map flag), volume (cue × category) and
  plays. `Pause/Resume/Stop` and `IsPlaying/IsPaused/IsStopped/IsDisposed/Name`
  map directly to the instance; `IsPlaying` is true while paused, matching XNA.
* `AudioCategory.SetVolume` reapplies to live instances (the game changes
  `Music` volume every frame). `AudioEngine.Update` reaps finished one-shots.
* WAV/ADPCM loads through `SoundEffect.FromStream` (Android supports
  PCM/IEEE/MS-ADPCM/IMA4). OGG is decoded through **NVorbis** behind
  `#if DISHWASHER_OGG`; long music is not cached, short effects are.
* Variation playlists are flattened to their wave set and cycled (≈ XACT
  *Ordered*). `Apply3D` does distance attenuation + pan.

API mapping table: see `notes/audio/README.md §4`.

**Compile evidence:** shim built against `MonoGame.Framework.DesktopGL 3.8.5.1`
(net8.0) — `Build succeeded. 0 Warning(s) 0 Error(s)` — both with and without
`DISHWASHER_OGG` + `NVorbis 0.10.5`. (The Android target could not be built on
this host: the installed SDK has only `platforms/android-35`, while the .NET 8
Android workload requires `android-34`, which is absent. The managed API surface
is identical; this is a host SDK gap, not a shim issue.)

---

## 7. Known gaps / blockers

* **XMA is not decodable by MonoGame** — all 200 XMA waves are pre-decoded here;
  this conversion is mandatory, not optional.
* **`music` has no WAV set** (8 tracks, OGG-only). Playback needs
  `DISHWASHER_OGG` + NVorbis, or run `decode_banks.sh` with `music` added to
  `ADPCM_BANKS`. Without it music is silent but the game does not crash.
* **`AudioStopOptions.AsAuthored`** release/fade tails are not modelled (stop is
  immediate for both options).
* **Not modelled:** RPC curves, per-play probabilities/weights, instance
  limiting, low-pass/reverb DSP, XACT 3D RPCs. The game's call sites do not
  exercise these.
* **Variation RNG** is a simple round-robin rather than the authored
  ordered/random/weighted playlists.
* The `.xgs` global settings (RPC/curve data) are not parsed; only its category
  name↔id order is used.
