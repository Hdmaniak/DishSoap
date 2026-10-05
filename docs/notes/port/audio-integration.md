# Audio integration — XACT shim wired into the live port

Owner: Audio Integration Lead. Date: 2026-10-02. Device: `<device-serial>`.
Based on `notes/audio/README.md` + `shim/XactShim.cs`.

**Result: audio fully initialises and the real (no-hack) build boots through all
25 loader phases and renders the title screen.** Loader phases 19 (Sound),
20 (MusicSound/Intro/GuitarHalper) and 21 (VoxSound) all pass.

## 1. Files added / modified

Added:
* `Dishwasher/Platform/Audio/XactShim.cs` — copy of `notes/audio/shim/XactShim.cs`
  (namespace `Dishwasher.Audio`). Two deliberate additions:
  * its two `Log(...)` helpers now route through `global::Dishwasher.Log.Info`
    (logcat) instead of only `Debug.WriteLine`, so boot iteration can see them;
  * added "AudioEngine created / cue map loaded / SoundBank ready / loaded '<file>'"
    diagnostics.

Modified:
* `Dishwasher/Dishwasher.csproj` — added `NVorbis 0.10.5` PackageReference and
  `<DefineConstants>$(DefineConstants);DISHWASHER_OGG</DefineConstants>`.
* The 10 audio game files: `using Microsoft.Xna.Framework.Audio;` →
  `using Dishwasher.Audio;`
  `Sound.cs`, `VoxSound.cs`, `MusicSound.cs`, `Music.cs`, `GuitarSolo.cs`,
  `GuitarHalper.cs`, `Map.cs`, `Globals.cs`, `Credits.cs`, `Intro.cs`.

No game logic was re-authored. The temporary `DISHWASHER_SKIP_SHADERS` diagnostics
used to reach audio before the shader agent finished were **removed**; the final
build contains no `#if DISHWASHER_SKIP_SHADERS` anywhere and no Draw guard.

## 2. How `.xgs` is handled

`new AudioEngine("sfx/sfxproj.xgs")` no longer opens the file. The shim's
`AudioEngine(string)` ctor ignores the path and builds the six fixed XGS
categories (`Global, Default, Music, solo_bg, solo_fg, halper`). The original big-endian
Xbox 360 `.xgs/.xwb/.xsb` are neither packaged nor read at runtime.
Logcat: `[audio] AudioEngine created: 6 categories; .xgs ignored ('sfx/sfxproj.xgs')`.

## 3. OGG vs WAV choice

**NVorbis path (not the WAV fallback).** The `music` bank is OGG-only (8 cues:
`music1..4`, `musicboss`, `musicarena`, `soft1`, `death2`). Added
`NVorbis 0.10.5` + `DISHWASHER_OGG`; the shim's `SoundLoader.TryLoadOgg` decodes
Vorbis to PCM and builds a `SoundEffect`. Non-music cues use the pre-decoded
MS-ADPCM WAVs (`wav/**`). Verified on device: `music3` → `music_w004.ogg`
(45.1 s) decoded and played (`IsPlaying=True`).

## 4. Asset packaging layout

Staged under `Dishwasher/Assets/` (auto-included as `AndroidAsset`, prefix
stripped), exactly mirroring the existing boot-iteration pattern:

```
Assets/sfx/cue_map.json                 (216 cues)
Assets/sfx/wav/<bank>/<bank>_wNNN.wav   214 files, ~25 MB  (all non-music banks)
Assets/sfx/ogg/music/music_wNNN.ogg       8 files, ~22 MB  (music bank, OGG-only)
```

`Assets/sfx/**` → APK `assets/sfx/**` → bootstrap extracts to
`files/content/sfx/**` (CWD root), which is also where `TitleContainer.OpenStream`
resolves `sfx/cue_map.json` and the cue files. `Assets/fx/**` (19 `.xnb`) is the
shader agent's; the final APK contains 19 fx + 223 sfx assets.

## 5. Build / install / launch

```sh
source tools/scripts/env.sh
cd src/Dishwasher
dotnet build -f net8.0-android \
  -p:AndroidSdkDirectory=$ANDROID_SDK_DIR \
  -p:JavaSdkDirectory=/usr/lib/jvm/java-21-openjdk-amd64 \
  -p:EmbedAssembliesIntoApk=true \
  -p:EnableMGCBItems=false        # only because the shader agent's Content.mgcb was mid-edit
                                  # (fails on fx/newblood.fx); Assets/fx is already staged, so
                                  # skipping MGCB does not affect this APK. Drop it once their
                                  # Content.mgcb compiles.

adb="${ADB:-adb}"; serial=<device-serial>
$adb -s $serial install -r bin/Debug/net8.0-android/com.recomp.dishwasher-Signed.apk
$adb -s $serial shell am force-stop com.recomp.dishwasher
$adb -s $serial logcat -c
$adb -s $serial shell am start -n com.recomp.dishwasher/crc641d1cdd92eb70a339.Activity1
$adb -s $serial logcat -d -v time -s Dishwasher:V MonoGame:V AndroidRuntime:E
```

APK: `com.recomp.dishwasher-Signed.apk`, 184,489,873 B.

## 6. Evidence

Clean boot (`/tmp/opencode/audio-final.log`) — effects loaded, no loader failure,
audio registered, title screen rendered (`notes/proof/audio-boot-menu.png`):

```
09:04:40.754 bootstrap: extracted 701 file(s), 0 failure(s) ...
09:05:01.739 [audio] AudioEngine created: 6 categories; .xgs ignored ('sfx/sfxproj.xgs')
09:05:01.844 [audio] cue map loaded: 216 cues from sfx/cue_map.json
09:05:01.844 [audio] SoundBank ready: 'sfx/sounds.xsb' -> 216 cues registered
09:05:01.850 [audio] SoundBank ready: 'sfx/musicsnd.xsb' -> 216 cues registered
09:05:01.851 [audio] SoundBank ready: 'sfx/halpsnds.xsb' -> 216 cues registered
09:05:01.857 [audio] SoundBank ready: 'sfx/voxsnd.xsb' -> 216 cues registered
```

Cue-resolution + playback self-test (`/tmp/opencode/audio-run5.log`, run under the
now-removed temporary skip so the loader could reach audio before the shader agent
finished; phase 25 = complete):

```
09:02:07.331 [audio-test] cue 'click1' resolved=True name=click1
09:02:07.432 [audio] loaded 'sfx/wav/waves/waves_w033.wav' (0.2s)
09:02:07.438 [audio-test] cue 'music3' resolved=True name=music3
09:02:13.137 [audio] loaded 'sfx/ogg/music/music_w004.ogg' (45.1s)
09:02:13.137 [audio-test] music3 IsPlaying=True
09:02:13.797 [loader] ALL PHASES COMPLETE (phase 25, loaded=true)
```

## 7. Residual limitations

* OGG is decoded wholly into a single `SoundEffect` on demand; a long music track
  is ~10–47 MB of PCM and can take ~6 s to decode (`music_w004`, 45 s, took 5.7 s).
  Could OOM/ANR on long tracks on low-memory devices; a streaming OGG reader would
  be the proper fix. Non-music WAV/ADPCM is small and cached.
* `AudioStopOptions.AsAuthored` release/fade tails, RPC/DSP/reverb, per-play
  probabilities and instance limiting are not modelled (see README §7).
* The 8 music cues are the only OGG-only entries; everything else prefers WAV.
* I could not drive the title screen's "PRESS A" with `adb input keyevent`
  (monkey/emulated KeyEvents don't surface as a MonoGame `GamePad`), so cue
  playback was proven with the self-test above rather than by a live menu press.
