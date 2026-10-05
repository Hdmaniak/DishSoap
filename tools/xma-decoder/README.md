# xma-decoder — on-device Xbox 360 XMA decode for the public build

This directory builds the small native library the **public** build uses to decode
the owner's original `sfx/*.xwb` XMA audio into playable WAV/OGG.  The internal
build does not ship or use it.

## What it produces

`build-xma-decoder.sh` installs three arm64-v8a shared libraries into
`src/Dishwasher/Platform/Audio/native/arm64-v8a/`:

| file | what | licence | linkage |
|---|---|---|---|
| `libavcodec.so` | minimal FFmpeg (`--disable-everything --enable-decoder=xma1,xma2`) | **LGPL-2.1-or-later** | dynamic |
| `libavutil.so`  | minimal FFmpeg support library | **LGPL-2.1-or-later** | dynamic |
| `libdishaudio.so` | our bridge (see `dishaudio.c`); statically links libvorbis/libogg | own code; contains **BSD-3** libvorbis/libogg | links the two above dynamically |

`libdishaudio.so` exports:

* `dw_version()`
* `dw_xma_to_file(data,size,ch,rate,num_samples,out_path,fmt)` — `fmt 0` PCM16 WAV, `fmt 1` OGG Vorbis
* `dw_pcm16be_to_wav(data,size,ch,rate,out_path)` — X360 big-endian PCM waves
* `dw_free(ptr)`

The C# side (`Platform/Audio/NativeXma.cs`) P/Invokes these; the container parsing
(`Platform/Import/XwbReader.cs` / `XsbReader.cs`) stays managed.

## Licence compliance

* FFmpeg is used unmodified, built from the official 7.1 release with a minimal
  decoder set.  Exact configure flags are in `build-xma-decoder.sh`.  It is
  **dynamically linked** (`libdishaudio.so` has `DT_NEEDED libavcodec.so,
  libavutil.so`), so the LGPL requirement to allow relinking is satisfied.
  `COPYING.LGPLv2.1` is included here.
* The FFmpeg source is available from <https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz>
  and a copy is kept next to this README; the full command to rebuild the exact
  binaries is `build-xma-decoder.sh`.
* libvorbis/libogg are BSD-3-Clause (permissive), statically linked — see
  <https://www.xiph.org/licenses/>.

## Rebuild

```sh
source tools/scripts/env.sh   # NDK/SDK/JDK on PATH vars
tools/xma-decoder/build-xma-decoder.sh
# then rebuild the app:
cd src/Dishwasher
dotnet build -c Release -f net8.0-android -p:DishwasherPublic=true \
  -p:AndroidSdkDirectory="$ANDROID_SDK_DIR" \
  -p:JavaSdkDirectory="$JAVA_SDK_DIR" \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk
```

The script is idempotent: extracted sources and build trees live under
`tools/xma-decoder/build/` and can be deleted between runs.

## Why FFmpeg's XMA decoder

FFmpeg 7.1's `xma1`/`xma2` decoders (`libavcodec/wmaprodec.c`) are the same
implementation vgmstream calls on the PC side, so the on-device output matches the
internal build.  There is no XMA *demuxer* anywhere: the XWB container is parsed in
C#, and the bridge synthesises the 34-byte `XMA2WAVEFORMATEX` extradata that
`avcodec` expects (see `build_xma2_extradata` in `dishaudio.c`).  Feeding the raw
2048-byte XMA packets directly to `avcodec_send_packet` works and avoids libavformat.
