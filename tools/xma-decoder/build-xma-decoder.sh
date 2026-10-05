#!/usr/bin/env bash
# build-xma-decoder.sh -- rebuild the public build's XMA audio decoder from source.
#
# Produces, into src/Dishwasher/Platform/Audio/native/arm64-v8a/:
#   libavcodec.so, libavutil.so  (minimal FFmpeg, LGPL-2.1-or-later, DYNAMIC)
#   libdishaudio.so              (our bridge; links the two above dynamically,
#                                 statically contains libvorbis/libogg, BSD)
#
# Requires: Android NDK r28b.  Resolution order: $NDK, $ANDROID_NDK_DIR,
#           $ANDROID_NDK_HOME, then $HOME/android-ndk-r28b.
#           autoconf/automake/make/curl (the release tarballs ship ./configure)
#
# Usage:  tools/xma-decoder/build-xma-decoder.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NDK="${NDK:-${ANDROID_NDK_DIR:-${ANDROID_NDK_HOME:-$HOME/android-ndk-r28b}}}"
TC="$NDK/toolchains/llvm/prebuilt/linux-x86_64"
API=24
OUT="$HERE/../../src/Dishwasher/Platform/Audio/native/arm64-v8a"
WORK="$HERE/build"
PREFIX="$WORK/prefix-arm64"

export CC="$TC/bin/aarch64-linux-android$API-clang"
export CXX="$TC/bin/aarch64-linux-android$API-clang++"
export AR="$TC/bin/llvm-ar"
export RANLIB="$TC/bin/llvm-ranlib"
export STRIP="$TC/bin/llvm-strip"
export CFLAGS="-fPIC -O2"
export CXXFLAGS="-fPIC -O2"

mkdir -p "$WORK" "$PREFIX" "$OUT"

# -- fetch tarballs if absent -------------------------------------------------
fetch() { # url filename
  [ -f "$HERE/$2" ] || curl -fsSL -o "$HERE/$2" "$1"
}
fetch https://downloads.xiph.org/releases/ogg/libogg-1.3.5.tar.gz      libogg-1.3.5.tar.gz
fetch https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.gz libvorbis-1.3.7.tar.gz
fetch https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz                    ffmpeg-7.1.tar.xz

# -- libogg / libvorbis (static, BSD) ----------------------------------------
cd "$WORK"
[ -d libogg-1.3.5 ]     || tar xzf "$HERE/libogg-1.3.5.tar.gz"
[ -d libvorbis-1.3.7 ]  || tar xzf "$HERE/libvorbis-1.3.7.tar.gz"
cd libogg-1.3.5
[ -f Makefile ] || ./configure --host=aarch64-linux-android --prefix="$PREFIX" \
    --disable-shared --enable-static --disable-docs
make -j"$(nproc)" && make install
cd ../libvorbis-1.3.7
[ -f Makefile ] || ./configure --host=aarch64-linux-android --prefix="$PREFIX" \
    --disable-shared --enable-static --disable-docs --with-ogg="$PREFIX"
make -j"$(nproc)" && make install

# -- minimal FFmpeg (shared, LGPL) -------------------------------------------
[ -d "$WORK/ffmpeg-7.1" ] || tar xf "$HERE/ffmpeg-7.1.tar.xz" -C "$WORK"
mkdir -p "$WORK/ffmpeg-build"
cd "$WORK/ffmpeg-build"
if [ ! -f config.h ]; then
  "$WORK/ffmpeg-7.1/configure" \
    --prefix="$PREFIX" \
    --enable-cross-compile --target-os=android --arch=aarch64 --cpu=armv8-a \
    --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --nm="$TC/bin/llvm-nm" \
    --strip="$STRIP" --sysroot="$TC/sysroot" --sysinclude="$TC/sysroot/usr/include" \
    --disable-everything --disable-programs --disable-doc --disable-network \
    --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
    --disable-postproc --disable-avformat \
    --enable-avcodec --enable-avutil --enable-decoder=xma1 --enable-decoder=xma2 \
    --enable-shared --disable-static --disable-symver --disable-stripping
fi
make -j"$(nproc)" && make install

# -- our bridge ---------------------------------------------------------------
cd "$HERE"
"$CC" -O2 -fPIC -shared -o "$WORK/libdishaudio.so" dishaudio.c \
  -I"$PREFIX/include" -L"$PREFIX/lib" \
  -lavcodec -lavutil -lvorbisenc -lvorbis -logg -llog -lm \
  -Wl,-soname,libdishaudio.so -Wl,-rpath-link,"$PREFIX/lib" -Wl,--no-undefined

# -- strip + install into the app --------------------------------------------
cp "$PREFIX/lib/libavcodec.so" "$PREFIX/lib/libavutil.so" "$WORK/libdishaudio.so" "$OUT/"
"$STRIP" --strip-unneeded "$OUT"/*.so
echo "installed into $OUT:"
ls -la "$OUT"
