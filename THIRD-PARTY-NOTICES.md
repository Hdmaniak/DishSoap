# Third-Party Notices

This repository and the release APK built from it include or build against the
third-party components listed below. Each is the property of its respective
authors and is provided under its own licence.

This project does **not** use, link, or depend on Xenia or ReXGlue emulation or
static-recompilation code (see `PROJECT_STATE.md` §2 and `docs/HANDOFF.md`).
The managed port runs on MonoGame / .NET for Android. A single, clearly scoped
provenance acknowledgement for the STFS *format* is given at the end.

---

## 1. Runtime components shipped in the APK

### MonoGame — `MonoGame.Framework.Android` 3.8.5.1
- **Licence:** MS-PL (Microsoft Public License)
- **Homepage:** https://monogame.net/ · https://github.com/MonoGame/MonoGame
- **Used for:** the XNA 4.0-compatible framework the port is compiled against.

### .NET for Android / Mono runtime (libmonosgen-2.0.so, libxamarin-app.so, …)
- **Licence:** MIT
- **Homepage:** https://dotnet.microsoft.com/apps/android
- **Used for:** the native runtime that executes the managed app on device.

### NVorbis 0.10.5
- **Licence:** MIT — Copyright (c) 2020 Andrew Ward
- **Homepage:** https://github.com/ioctlLR/NVorbis
- **Used for:** decoding OGG Vorbis background-music tracks that the XACT shim
  feeds to MonoGame's `SoundEffect` (`DISHWASHER_OGG`).

### OpenAL Soft (bundled as `libopenal.so` by MonoGame.Library.OpenAL / `MonoGame.Library.OpenAL` 1.24.3.x)
- **Licence:** LGPL-2.0-or-later, dynamically linked. Portions (HRTF data set,
  Spherical-Harmonic-Transform) are BSD-3-Clause.
- **Homepage:** https://openal-soft.org/
- **Used for:** audio output on Android.
- **Relink statement:** `libopenal.so` is a separate, dynamically linked shared
  library; it can be replaced/relinked by the recipient by rebuilding MonoGame's
  OpenAL library or supplying an ABI-compatible `libopenal.so`.

### FFmpeg 7.1 — minimal build (`libavcodec.so`, `libavutil.so`)
- **Licence:** LGPL-2.1-or-later. **Dynamically linked.**
- **Homepage / source:** https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz
- **Build config:** `--disable-everything` with only the `xma1`/`xma2` audio
  decoders enabled (see `tools/xma-decoder/build-xma-decoder.sh`).
- **Full licence text:** `tools/xma-decoder/COPYING.LGPLv2.1`
- **Used for:** on-device decoding of the owner's original Xbox 360 XMA audio in
  the public build.
- **Required notice / relink statement:**
  > This software uses libraries from the FFmpeg project under the
  > LGPLv2.1. FFmpeg is used unmodified and is dynamically linked
  > (`libdishaudio.so` has `DT_NEEDED` on `libavcodec.so` and `libavutil.so`),
  > so the LGPL requirement that recipients be able to relink against a
  > modified FFmpeg is satisfied. To rebuild these exact binaries, run
  > `tools/xma-decoder/build-xma-decoder.sh`; the script downloads the pinned
  > official FFmpeg 7.1 source release. No FFmpeg source is modified.

### libogg 1.3.5 and libvorbis 1.3.7
- **Licence:** BSD-3-Clause — Copyright (c) 2002-2020 Xiph.Org Foundation
- **Homepage:** https://www.xiph.org/ · https://www.xiph.org/licenses/
- **Used for:** Vorbis encoding inside `libdishaudio.so` (statically linked).
- **Required BSD-3-Clause notice:**
  > Redistribution and use in source and binary forms, with or without
  > modification, are permitted provided that the following conditions are met:
  > (1) redistributions of source code must retain the above copyright notice,
  > this list of conditions and the following disclaimer; (2) redistributions
  > in binary form must reproduce the above copyright notice, this list of
  > conditions and the following disclaimer in the documentation and/or other
  > materials provided with the distribution; (3) neither the name of the
  > Xiph.Org Foundation nor the names of its contributors may be used to
  > endorse or promote products derived from this software without specific
  > prior written permission.
  >
  > THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
  > AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
  > IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
  > ARE DISCLAIMED.

---

## 2. Build-time tooling (not redistributed in the APK)

These are used to produce the repository's own artifacts and are **not** shipped
inside the release APK:

| Component | Licence | Role |
|---|---|---|
| MonoGame Content Builder (`dotnet-mgcb`) 3.8.5.1 | MS-PL | compiles our re-authored `Content/fx/*.fx` to MGFX `.xnb` |
| MojoShader (via MonoGame's effect pipeline; also the model for our `tools/shaders/effect_parse.py` D3DX effect parser) | zlib | effect bytecode handling |
| glslang / SPIRV-Tools (via MonoGame's GL effect compiler) | Apache-2.0 / BSD-3-Clause | GLSL→SPIR-V for the OpenGL backend |
| `fxccs`/`d3dcompiler_47` (Windows, run under Wine for `-p:Platform=Android`) | Microsoft redistributable | HLSL compile step (Windows-only toolchain) |
| Wine | LGPL-2.1-or-later | runs the Windows effect compiler on Linux |
| ilspycmd 9.1.0.7988 | MIT | **you** use this to decompile *your own* copy of the game (see `BUILDING.md`); not part of this repo |
| Python 3 standard library | PSF-2.0 | our Python tooling |

---

## 3. Artwork in this repository

The app icon (`docs/assets/icon-512.png`, `docs/assets/icon-192.png`,
`docs/assets/ic_launcher-512.png`), the in-app launcher resources
(`src/Dishwasher/Resources/drawable/icon.png` plus `Resources/mipmap-*`), and
the README banner (`docs/assets/banner.png`) are original works created for this
project — the **"halftone-burst"** design. They are covered by this repository's
MIT licence and contain no imagery derived from the game. They are generated
procedurally by `docs/assets/generate-icon.py`, which uses the **DejaVu Sans**
typeface (Bitstream Vera / public licence) for generic banner text only.

---

## 4. Provenance acknowledgement (format reference — not a runtime dependency)

The STFS/LIVE ("XContent") package reader used by the public build
(`src/Dishwasher/Platform/Import/StfsReader.cs`, and its validated Python
reference `tools/stfs_extract.py`) was written with reference to the STFS
container parser in the Xenia project and ReXGlue SDK:

- **Xenia** — Copyright (c) 2022 Ben Vanik and Xenia project contributors,
  BSD-3-Clause. https://xenia.jp
- **ReXGlue SDK** — Copyright (c) 2026 Tom Clay, BSD-3-Clause
  (portions derived from Xenia). `rexglue-sdk/src/filesystem/devices/stfs_container_device.cpp`

This acknowledgement covers only the documented STFS on-disk format. No
emulation, PowerPC recompilation, or other Xenia/ReXGlue code is used, linked,
or shipped by this project, and neither Xenia nor ReXGlue is a dependency.
