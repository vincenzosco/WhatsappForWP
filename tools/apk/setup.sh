#!/bin/bash
# One run of the whole thing: get the runtime, build the harness, start the library.
#
# Everything lives in /root/bionic because this WSL instance stops between
# commands and wipes /tmp with it.
set -e
B=/root/bionic
W=$(cd "$(dirname "$0")/../.." && pwd)
APK=${APK:?set APK to the path of WhatsApp.apk}
mkdir -p "$B"
cd "$B"

echo "== NDK"
if [ ! -d ndk ]; then
    [ -f ndk.zip ] || curl -sL -o ndk.zip https://dl.google.com/android/repository/android-ndk-r27c-linux.zip
    unzip -q -o ndk.zip -d ndk
fi
NDK="$B/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64"
SPO="$W/.tools/apk/spo/strings_it.spo"
[ -f "$SPO" ] || { mkdir -p "$SPO/.."; unzip -p "$APK" assets/compressed/strings/strings_it.spo > "$SPO"; }

echo "== system image"
# API 28, because API 29 and later put the filesystem inside a dynamic
# partition (super.img), which is one image more to unpack. API 28 still ships
# a plain sparse ext4 system.img.
if [ ! -f sys28.zip ]; then
    curl -sL -o sys28.zip https://dl.google.com/android/repository/sys-img/android/x86_64-28_r04.zip
fi
[ -d sys28 ] || unzip -q -o sys28.zip -d sys28
IMG=$(find sys28 -name 'system.img' | head -1)
echo "image: $IMG"

# Some of these images are sparse and some are already a plain ext4, and
# simg2img only accepts the first kind. So the image is used as it is when 7z
# already sees a filesystem in it, and converted when it does not.
if 7z l "$IMG" 2>/dev/null | grep -q 'Type = Ext'; then
    SRC="$IMG"
    echo "plain ext4, no conversion needed"
else
    rm -f system.raw
    simg2img "$IMG" system.raw
    SRC=system.raw
fi
ls -l "$SRC"

echo "== extract loader and libs"
if [ ! -f sys/system/bin/linker64 ]; then
    7z x -y -osys "$SRC" 'system/bin/linker64' 'system/lib64/*' > 7z.log 2>&1 || tail -20 7z.log
fi
test -f sys/system/bin/linker64 || { echo "no linker64 extracted"; exit 1; }
find sys -maxdepth 3 -type f | sort | head -30

echo "== runtime where the loader looks for it"
# bionic resolves /system/lib64 and the interpreter path itself, so the
# extracted runtime is put where those names point. Inside this container that
# is harmless and it is what makes a plain "run the program" work.
mkdir -p /system/bin
cp -f sys/system/bin/linker64 /system/bin/linker64
ln -sfn "$B/sys/system/lib64" /system/lib64

if [ ! -f sys/system/bin/linker64 ]; then
    echo "no linker64"; exit 1
fi

echo "== build harness"
cp "$W/tools/apk/jni_harness.c" harness.c
unzip -p "$APK" lib/x86_64/libsuperpack.so > libsuperpack.so
"$NDK/bin/clang" --target=x86_64-linux-android24 --sysroot="$NDK/sysroot" \
    -O1 -g -Wall -Wno-int-to-pointer-cast -Wno-pointer-to-int-cast \
    -Wl,--dynamic-linker=/system/bin/linker64 \
    -o harness harness.c -ldl

echo "== run"
# The harness names /system/bin/linker64 as its interpreter, so it is started
# like any other program and the loader does the loading, which is the one job
# it is known to do here.
mkdir -p "$B/out"
run() {
    echo "---- $*"
    LD_LIBRARY_PATH="$B/sys/system/lib64" "$B/harness" "$@" || echo "exit=$?"
}

run --lib "$B/libsuperpack.so"
run --lib "$B/libsuperpack.so" --fn get_architecture
run --lib "$B/libsuperpack.so" --fn testDecompressorLibraryUsable
run --lib "$B/libsuperpack.so" --fn decompress_with_ref \
    --spo "$SPO" --out "$B/out" --ref "$B/out/ref"
echo "---- output dir"
find "$B/out" -maxdepth 3 | head -20
