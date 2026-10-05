#!/usr/bin/env bash
# decode_banks.sh -- transcode the Xbox 360 XACT wave banks to runnable audio.
#
#   XWB (XMA/PCM, big-endian X360)
#     -> vgmstream-cli -i -S 0        (one pass, no loop fade; XMA -> PCM16 WAV)
#     -> ffmpeg -c:a libvorbis        (OGG Vorbis, size-sane set: ALL banks)
#     -> ffmpeg -c:a adpcm_ms         (MS-ADPCM WAV, zero-dependency set: SFX+solo)
#
# Tool versions used:
#   vgmstream-cli r2117 (May 19 2026)  -- https://github.com/vgmstream/vgmstream
#   ffmpeg 8.0.1-3ubuntu2 (libvorbis, adpcm_ms)
#
# Usage:  vgmstream-cli must be on PATH or set VGM=/path/to/vgmstream-cli
set -euo pipefail

# All locations are overridable.  Defaults are portable (no machine paths):
#   SRC  <- $DISHWASHER_ASSETS_DIR/sfx   (set by tools/scripts/env.sh)
#   WORK <- $XDG_CACHE_HOME/dishwasher/audio-work
VGM="${VGM:-vgmstream-cli}"
SRC="${SRC:-${DISHWASHER_ASSETS_DIR:-$HOME/dishwasher-assets-clean}/sfx}"
WORK="${WORK:-${XDG_CACHE_HOME:-$HOME/.cache}/dishwasher/audio-work}"
DEST="${DEST:-$HOME/dishwasher-content-sfx}"
# Banks emitted as MS-ADPCM WAV (directly loadable by SoundEffect.FromStream on
# Android). music.* stays OGG-only to keep the footprint small.
ADPCM_BANKS="waves vox halper solo_crux solo_dish solo_smash"

mkdir -p "$WORK/wav" "$WORK/ogg"
rm -rf "$WORK/wav" "$WORK/ogg"
mkdir -p "$WORK/wav" "$WORK/ogg"

echo "== decode XWB -> WAV (vgmstream -i) =="
for f in "$SRC"/*.xwb; do
    b=$(basename "$f" .xwb)
    mkdir -p "$WORK/wav/$b" "$WORK/ogg/$b"
    "$VGM" -i -S 0 -o "$WORK/wav/$b/w?s.wav" "$f" >/dev/null
    echo "  $b: $(ls "$WORK/wav/$b" | wc -l) waves"
done

echo "== WAV -> OGG (all banks) =="
for d in "$WORK"/wav/*/; do
    b=$(basename "$d")
    for w in "$d"w*.wav; do
        s=$(basename "$w" .wav); s=${s#w}; idx=$((s - 1))
        printf -v out "%s/%s_w%03d.ogg" "$WORK/ogg/$b" "$b" "$idx"
        ffmpeg -hide_banner -loglevel error -y -i "$w" -c:a libvorbis -q:a 4 "$out"
    done
done

echo "== WAV -> MS-ADPCM WAV (SFX + solo banks) =="
rm -rf "$DEST/ogg" "$DEST/wav"
mkdir -p "$DEST/ogg" "$DEST/wav"
cp -r "$WORK/ogg"/* "$DEST/ogg/"
for b in $ADPCM_BANKS; do
    mkdir -p "$DEST/wav/$b"
    for w in "$WORK/wav/$b"/w*.wav; do
        s=$(basename "$w" .wav); s=${s#w}; idx=$((s - 1))
        printf -v out "%s/wav/%s/%s_w%03d.wav" "$DEST" "$b" "$b" "$idx"
        ffmpeg -hide_banner -loglevel error -y -i "$w" -c:a adpcm_ms "$out"
    done
done

echo "== done =="
echo "  OGG : $(find "$DEST/ogg" -name '*.ogg' | wc -l) files, $(du -sh "$DEST/ogg" | cut -f1)"
echo "  WAV : $(find "$DEST/wav" -name '*.wav' | wc -l) files, $(du -sh "$DEST/wav" | cut -f1)"
