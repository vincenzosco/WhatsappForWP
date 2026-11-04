#!/bin/bash
# Ask the library to unpack the .spo the way the app does: through an asset.
set +e
B=/root/bionic
W=$(cd "$(dirname "$0")/../.." && pwd)
L="$B/sys/system/lib64"
NDK="$B/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64"
PRELOAD="$B/stub/libloghook.so $B/stub/libandroidhook.so"

build() {
    "$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
        -O1 -g -Wall -shared -fPIC -o "$1" "$2" || { echo "build failed: $2"; exit 1; }
}
build "$B/stub/libloghook.so" "$W/tools/apk/logstub.c"
build "$B/stub/libandroidhook.so" "$W/tools/apk/assethook.c"
"$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
    -O1 -g -Wall -Wno-int-to-pointer-cast -Wno-pointer-to-int-cast \
    -Wl,--dynamic-linker=/system/bin/linker64 \
    -o "$B/harness" "$W/tools/apk/jni_harness.c" -ldl || exit 1

try() {
    name="$1"; shift
    rm -rf "$B/outA"; mkdir -p "$B/outA"
    echo "==== $name"
    ASSET_DIR="$W/.tools/apk/spo" LD_PRELOAD="$PRELOAD" LD_LIBRARY_PATH="$L" \
        "$B/harness" --lib "$B/libsuperpack.so" "$@" 2>&1 \
        | grep -v -E '^\[jni\] (FindClass|RegisterNatives|Get)|^  [a-zA-Z_]+ +\(' \
        | head -30
    echo "   outA: $(ls -A "$B/outA" | head -5 | tr '\n' ' ')"
}

cd "$B"
try "decompress ref=empty" --fn decompress --spo strings_it.spo --out "$B/outA" --ref ""
try "decompress ref=outA"  --fn decompress --spo strings_it.spo --out "$B/outA" --ref "$B/outA"
