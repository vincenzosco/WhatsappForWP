#!/bin/bash
# Run the same calls again, but with a liblog that talks.
set +e
B=/root/bionic
W=$(cd "$(dirname "$0")/../.." && pwd)
S="$W/.tools/apk/spo/strings_it.spo"
L="$B/sys/system/lib64"
NDK="$B/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64"

echo "== liblog stub"
mkdir -p "$B/stub"
# Called libloghook.so and not liblog.so: shadowing liblog breaks every other
# library that expects the real one (libcutils wants symbols this stub does not
# have). Preloading keeps the real liblog around and only takes the log calls
# first.
"$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
    -O1 -g -Wall -shared -fPIC -o "$B/stub/libloghook.so" "$W/tools/apk/logstub.c" \
    || { echo "stub build failed"; exit 1; }
ls -l "$B/stub/libloghook.so"

echo "== harness"
"$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
    -O1 -g -Wall -Wno-int-to-pointer-cast -Wno-pointer-to-int-cast \
    -Wl,--dynamic-linker=/system/bin/linker64 \
    -o "$B/harness" "$W/tools/apk/jni_harness.c" -ldl || { echo "harness build failed"; exit 1; }

try() {
    name="$1"; shift
    rm -rf "$B/outA" "$B/outB"; mkdir -p "$B/outA" "$B/outB"
    echo "==== $name"
    LD_PRELOAD="$B/stub/libloghook.so" LD_LIBRARY_PATH="$L" \
        "$B/harness" --lib "$B/libsuperpack.so" "$@" 2>&1 \
        | grep -v -E '^\[jni\] (FindClass|RegisterNatives)|^  [a-zA-Z_]+ +\(|^\[jni\] Get' | head -20
    echo "   outA: $(ls -A "$B/outA" | head -3 | tr '\n' ' ')"
}

cd "$B"
try "from_so out=outA ref=empty" --fn decompress_from_so --spo "$S" --out "$B/outA" --ref ""
try "with_ref out=outA ref=outA" --fn decompress_with_ref --spo "$S" --out "$B/outA" --ref "$B/outA"
