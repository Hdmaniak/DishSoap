# Public build — Stage 2: on-device audio (XMA → WAV/OGG)

**Owner:** Audio-Derivation Specialist (subagent)
**Date:** 2026-10-03
**Status:** ✅ Implemented and verified on a Galaxy S9 (Android 10). SFX play in the
public build; internal build unaffected.

> **Update 2026-10-05 — tolerant import.** The SHA-256 gate described below is now
> tolerant: `sfx/*.xwb`/`.xsb` files that differ or are missing are warnings, not
> blockers. `AudioImporter` derives whatever banks are present and always writes
> `sfx/cue_map.json` (possibly with 0 cues), so a missing bank is simply silent.
> The strict `458/458` behaviour recorded below is the original stage-2 policy; see
> `public-build.md §3.5` for the current rule.

This is Stage 2 of the public (content-free) build: the public APK ships no game
audio, so on first run it must decode the owner's own `sfx/*.xwb` Xbox 360 XMA
banks on-device into the exact layout the runtime `Dishwasher.Audio.XactShim`
already consumes (`sfx/wav/**`, `sfx/ogg/**`, `sfx/cue_map.json`).

> **Music note:** background music does not play in the **internal** build either
> — tracked as a separate pre-existing issue and out of scope here. The work below
> derives valid OGG music files and a 216/216 cue map identical to the internal
> reference; it does not touch the music trigger path.

---

## 0. TL;DR

| thing | result |
|---|---|
| decoder | minimal **FFmpeg 7.1** `xma1`/`xma2` (`wmaprodec.c`), LGPL-2.1+, dynamically linked; OGG via static libvorbis/libogg (BSD) |
| native artifacts | `libavcodec.so` 411 KB + `libavutil.so` 718 KB + `libdishaudio.so` 866 KB (**arm64-v8a only**, ~1.95 MB disk / ~0.77 MB APK delta) |
| C# parsing | `XwbReader.cs` (XWB, big-endian, byte-swapped `DNBW`) + `XsbReader.cs` (XSB cue→wave, ported from `parse_xsb.py`) |
| on-device result | **222/222 waves** (208 PCM16 WAV + 14 Vorbis OGG), **216 cues**, derived in ~35 s |
| cue map | generated shape **exactly matches** the internal reference (216/216 cues, 0 ref/meta diffs) |
| manifest | required total **443 → 458** (+15 `sfx/*.xwb/.xsb/.xgs`), regenerated |
| public APK | 27.99 MB, assets = `Content/fx` 21 + `fx` 21 + `import-manifest.json` only; **contains no game content** |
| device | import `458/458`; `[audio] SoundBank ready … -> 216 cues registered`; cues `slash1/sword1/hit/crush/…` load and play; no crash/ANR |
| internal build | rebuilt and re-verified: extracts 716 files, **216 cues**, unchanged |

---

## 1. Decoder strategy and why

**XMA is not decodable by MonoGame** (all 200 XMA waves are proprietary Xbox 360
XMA/XMA2). The PC conversion used `vgmstream-cli`; on Android we need the same
decoder on-device. vgmstream's XMA support is itself a thin wrapper over FFmpeg's
`xma1`/`xma2` decoders (`libavcodec/wmaprodec.c`), so building those two decoders
from a minimal FFmpeg is the shortest path that produces **the same** audio.

There is no XMA *demuxer* in FFmpeg (only decoders + an XMA2 parser), so:

* the **container** (`.xwb` wave bank, `.xsb` sound bank) is parsed in C# (see §3);
* the bridge (`dishaudio.c`) synthesises the 34-byte `XMA2WAVEFORMATEX` extradata
  FFmpeg's decoder expects from the XWB entry's channels/rate/sample-count, then
  feeds the raw 2048-byte XMA packets to `avcodec_send_packet`/`avcodec_receive_frame`.
  Verified on the host that packet size does not matter (whole stream, 2048, 4096
  all give identical output), so **libavformat is not needed at all** — the native
  library stays small.

**Alternatives considered**

| option | verdict |
|---|---|
| MonoGame's own `WaveBank`/`SoundBank` | ✗ desktop/little-endian layouts; cannot read 360 banks or XMA |
| `vgmstream` compiled for Android | ✗ much larger; its XMA path is FFmpeg anyway |
| FFmpeg CLI as a subprocess | ✗ executing bundled binaries on modern Android is fragile; task prefers a P/Invoke `.so` |
| decode to PCM WAV for everything | ✗ 383 MB of PCM (282 MB is music) — not viable |
| Native Vorbis encoder in FFmpeg | ✗ experimental/poor quality; used real libvorbis instead |

**Output formats.** Short SFX (`waves`, `vox`, `halper` + the 22 already-PCM
entries) are written as **PCM16 WAV** (lossless, small); the long/looping tracks
(`music`, `solo_crux/dish/smash`) are encoded to **OGG Vorbis** by libvorbis
(q≈4) to keep the on-device derivation at ~64 MB instead of hundreds, and to match
the internal build's music set. The 22 raw PCM entries are big-endian 16-bit and
are byte-swapped to little-endian for the WAV.

### Licensing

* **FFmpeg** `libavcodec`/`libavutil` are **LGPL-2.1-or-later**, built unmodified
  from the official 7.1 release with `--disable-everything --enable-decoder=xma1,xma2`.
  They are shipped as separate `.so` files and `libdishaudio.so` links them
  **dynamically**, so the LGPL relinking requirement is met. `COPYING.LGPLv2.1`
  and the exact configure flags are kept in
  `tools/xma-decoder/` (see its README).
* **libvorbis/libogg** are **BSD-3-Clause** (permissive) and statically linked.

---

## 2. Native artifact

`src/Dishwasher/Platform/Audio/native/arm64-v8a/` (only ABI shipped; targets
are arm64):

| file | bytes | notes |
|---|---:|---|
| `libavcodec.so` | 411,464 | stripped; `DT_NEEDED libavutil.so` |
| `libavutil.so`  | 717,984 | stripped |
| `libdishaudio.so` | 866,472 | stripped; `DT_NEEDED libavcodec.so, libavutil.so, liblog.so, libm.so` |

Total ~1.95 MB on disk; the APK grows by ~0.77 MB (compressed). Built by
`tools/xma-decoder/build-xma-decoder.sh` (idempotent; sources/scripts
kept, build trees are disposable).

`Platform/Audio/NativeXma.cs` P/Invokes the bridge and is fully guarded —
`Probe()` calls `dw_version()` in a try/catch, and every decode call catches
`DllNotFoundException`/`EntryPointNotFoundException`, so a missing library
(desktop harness, other ABI) degrades to "audio not derived" instead of crashing.

---

## 3. C# container parsing and cue mapping

Ported from the validated Python parsers
(`<dev-notes>/audio/tools/parse_xwb.py`, `parse_xsb.py`) and cross-checked
against the internal reference.

* **`Platform/Import/XwbReader.cs`** — XWB `DNBW` (byte-swapped `WBND`),
  big-endian, 5 segments. Reads BANK_DATA (name, flags, entry-meta size) and each
  entry: `entryInfo` → `numSamples = (info>>4)&0x0FFFFFFF` (the Python parser's
  older `>>28` field was not used for decode; validated against the 22 PCM
  entries), `format` → codec/channels/rate/block-align, plus the file
  offset/length inside the WAVE_DATA segment. `GetWaveData(i)` returns the raw
  bytes.
* **`Platform/Import/XsbReader.cs`** — XSB `KBDS` (`SDBK`), big-endian; a faithful
  port including the two 360-specific deviations the audio agent found:
  5-byte complex clip records and the standalone variation-table header. Produces
  cue names, type, category id and `(waveBankIndex, trackIndex)` refs.
* **`Platform/Import/AudioImporter.cs`** — for each of the 7 banks decodes every
  wave (per-wave try/catch; one bad wave never aborts the import), then maps every
  cue to files and writes `sfx/cue_map.json` in the runtime shape
  (`{cue: {bank, wave_bank, type, category_id, files:[{bank,index,file,kind,loop}]}}`).
  Note the **sound-bank names differ** from the wave-bank names:
  `waves↔sounds`, `vox↔voxsnd`, `halper↔halpsnds`, `music↔musicsnd`; the three
  `solo_*` banks match.

**Correctness proof (desktop harness).** `tools/csharp/importer-test/`
links the same sources and runs the real retail package: it validates 458/458,
derives 222/222 waves, and the resulting `cue_map.json` is compared field-by-field
with the internal `Assets/sfx/cue_map.json`:
**216/216 cues, 0 missing/extra, 0 file-set diffs, 0 type/category diffs.**

---

## 4. Import wiring

`ContentImporter.StageAndDerive` now:

1. skips staging `sfx/**` originals (verified but never copied — they stay in the
   user's own copy), and
2. runs `AudioImporter.Derive(...)` after the texture/font staging, on the same
   background thread, emitting `ImportProgress {Phase="Audio"}` so the existing
   progress bar/status line shows `Audio 137/222 music_w003.ogg`.

The original `sfx/*.xwb/.xsb/.xgs` are **not** staged and **not** shipped. Audio
derivation is best-effort and wrapped in try/catch: a decoder failure logs and
leaves the game silent rather than aborting the import.

The shim now opens its map/files through `PublicBuild.OpenContent(...)` instead of
`TitleContainer.OpenStream(...)`, so it finds the app-private derived tree (it
still falls back to APK assets for the internal build). `PublicBuild.ContentReady()`
additionally requires `sfx/cue_map.json`.

The import screen text was updated from the Stage-1 "runs silently" note to say
audio is decoded on-device, and the success view reports the wave/cue counts.

---

## 5. Manifest change

`tools/gen_import_manifest.py` now also accepts original audio banks
(`sfx/**` ending `.xwb`/`.xsb`/`.xgs`; the stray non-package
`assets-clean/sfx/solo_crux.xwb.wav` is excluded by extension).
Regenerated with:

```sh
python3 tools/gen_import_manifest.py \
  --source $DISHWASHER_ASSETS_DIR \
  --out    src/Dishwasher/Assets/import-manifest.json
# required total: 458   counts: {data:303, gfx_xnb:128, gfx_zdx:1, resources:11, sfx:15}
```

**443 → 458 required originals** (+15: 7 `.xwb` + 7 `.xsb` + 1 `.xgs`). Still
nothing derived is ever listed.

---

## 6. Verification evidence (device)

All under `android/notes/proof/public-build-audio/`:

| file | shows |
|---|---|
| `01-import-screen.png` | import screen with the updated audio text |
| `02-import-success.png` | `✓ Import complete: All 458 …` + `Audio: 222/222 waves (208 WAV, 14 OGG), 216 cues` + START |
| `03-public-boot.png` | public build booted into the game after import |
| `04-public-mainmenu.png` | main menu (derived textures + audio loaded) |
| `public-run-logcat-excerpt.txt` | raw logcat: import counts, cue registration, cue playback, internal-build re-verify |
| `apk-and-assets.txt` | APK size, asset listing, content-free check, native lib sizes |

Key logcat (public build):

```
[import] audio: 222/222 waves derived (208 wav, 14 ogg), 216 cues, native=True
[import] SUCCESS All 458 required original files verified (size + SHA-256). | Audio: 222/222 waves (208 WAV, 14 OGG), 216 cues
[audio] SoundBank ready: 'sfx/sounds.xsb' -> 216 cues registered          # was 0 cues in Stage 1
[audio] cue playing: 'slash1' -> sfx/wav/waves/waves_w001.wav (loop=False)
[audio] cue playing: 'crush'  -> sfx/wav/waves/waves_w009.wav (loop=False)
[audio] cue playing: 'goondie' -> sfx/wav/vox/vox_w072.wav (loop=False)
```

* **SFX work** in the public build: menu cues (`slash1`, `sword1`) and in-game
  cues (`hit`, `crush`, `chunk`, `splat`, `pistol`, enemy voice, …) resolved to the
  derived files and played. No crash, no ANR in the session (validation + 222-wave
  decode + texture derivation ran on a background thread).
* **Content-free public APK**: assets are exactly `assets/Content/fx` (21),
  `assets/fx` (21), `assets/import-manifest.json`; no `.xwb/.xsb/.xgs`, and no
  `assets/data|gfx|sfx|Resources`.
* **Internal build unaffected**: rebuilt clean (96.9 MB, 223 sfx assets), clean
  installed, extracts 716 files and registers **216 cues**.

Device hygiene: saves were backed up via root before the clean install and restored
afterward with **matching md5** (`profile.sav`, `settings.sav`,
`android_settings.sav`); the two Microsoft document providers disabled for picker
automation were re-enabled; the pushed ROM zip and temporary uploads were removed.

---

## 7. Limitations / remaining gaps

* **Music**: does not play in the **internal build either** — a separate
  pre-existing issue in the music trigger/playback path, **not** caused by this
  work. The derived OGG files are valid (ffprobe decodes them) and the cue map
  matches the internal reference exactly, so the derivation side is complete.
* **arm64 only.** Decoder libs are packaged for `arm64-v8a`; other ABIs get silent
  audio (no crash). Real targets are arm64 (S9/S24).
* **Lossy for long tracks**: music/solo are Vorbis q≈4 (as internal); short SFX
  are lossless PCM16, slightly larger than the internal MS-ADPCM set (38 MB vs
  ~25 MB).
* **Not modelled** (inherited from the shim): XACT RPC curves, probabilities,
  AsAuthored release tails, 3D RPCs. The game's call sites don't exercise them.
* The decoder adds ~0.77 MB to the public APK and ~1.95 MB in memory; the import
  takes ~35 s on an S9 for the audio phase alone.

---

## 8. Files touched

**Added**
* `Dishwasher/Platform/Import/XwbReader.cs`, `XsbReader.cs`, `AudioImporter.cs`
* `Dishwasher/Platform/Audio/NativeXma.cs`
* `Dishwasher/Platform/Audio/native/arm64-v8a/{libavcodec,libavutil,libdishaudio}.so`
* `tools/xma-decoder/` (`dishaudio.c`, `build-xma-decoder.sh`, `README.md`, licences, tarballs)
* `android/notes/proof/public-build-audio/*`

**Modified**
* `Dishwasher/Platform/Import/ContentImporter.cs` — skip staging `sfx/**`; run audio derivation; return `AudioImportResult`
* `Dishwasher/Platform/Import/ImportController.cs` — updated audio text + success counts
* `Dishwasher/Platform/PublicBuild.cs` — `ContentReady()` also requires `sfx/cue_map.json`
* `Dishwasher/Platform/Audio/XactShim.cs` — content open via `PublicBuild.OpenContent`; always-on proof-of-playback/failure logs
* `Dishwasher/Dishwasher.csproj` — public-only `AndroidNativeLibrary` for the decoder
* `tools/gen_import_manifest.py` — include original `sfx/**`
* `Dishwasher/Assets/import-manifest.json` — regenerated (458)

**Not touched:** game logic, protected fixes, both diagnostic harnesses
(`Enabled=false`).
