#!/bin/bash
# Try the other family of entry points on the same file: the container reader
# (openNative/getFileCount/getFilePtr/getFileSize) and the byte stream
# (openObArchiveBytesNative/openRawBytesNative + readNative).
set +e
B=/root/bionic
W=$(cd "$(dirname "$0")/../.." && pwd)
S="$W/.tools/apk/spo/strings_it.spo"
L="$B/sys/system/lib64"
NDK="$B/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64"
PRELOAD="$B/stub/libloghook.so $B/stub/libandroidhook.so"

echo "== build"
"$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
    -O1 -g -Wall -Wno-int-to-pointer-cast -Wno-pointer-to-int-cast \
    -Wl,--dynamic-linker=/system/bin/linker64 \
    -o "$B/harness" "$W/tools/apk/jni_harness.c" -ldl || { echo "build failed"; exit 1; }

try() {
    name="$1"; shift
    echo "==== $name"
    ASSET_DIR="$W/.tools/apk/spo" LD_PRELOAD="$PRELOAD" LD_LIBRARY_PATH="$L" \
        "$B/harness" --lib "$B/libsuperpack.so" "$@" 2>&1 \
        | grep -v -E '^\[jni\] (FindClass|RegisterNatives)|^  [a-zA-Z_]+ +\(' | head -40
}

cd "$B"
for m in 0 1 2; do
    try "obi mode=$m" --scenario obi --buf "$S" --mode "$m"
done
for m in 0 1; do
    try "obib mode=$m" --scenario obib --buf "$S" --mode "$m"
done
try "raw" --scenario raw --buf "$S"
