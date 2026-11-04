# A bionic host for `libsuperpack`

`libsuperpack.so` from the WhatsApp APK is the driver that unpacks the assets the
app ships compressed. This directory holds the small host that loads it on a
plain Linux machine, asks it its own entry points, and tries to unpack the
Italian string pack with it.

It did not get the labels out. The pack is refused by this driver
(`Could not open archive: 1/7/0`), so the work stops at a documented limit, not
at a missing feature of the app. The harness itself works and is worth keeping:
it is what turned "the library only runs on a phone" into a readable transcript
of the library's own decisions.

## Why bionic and not glibc

The library is built for Android, so it is linked against bionic. `dlopen` on a
glibc process fails on the first bionic-only dependency, and no amount of
relinking helps: a bionic `.so` needs a bionic loader and libc to exist at all.

Neither of the two obvious sources carries them:

- the NDK sysroot has stub libraries, not a real `libc.so` or `linker64`;
- the Termux bootstrap does not ship the loader either.

So they come out of an Android system image. API 28 is the pick: its
`system.img` is a plain ext4 image (sparse or not, and `7z` reads both), while
API 29 and later hide the same filesystem inside a dynamic partition
(`super.img`), which is one more image to unpack.

## What is here

| File | What it is |
| --- | --- |
| `jni_harness.c` | The host. A `JavaVM` and a `JNIEnv` table with 260 slots, the ones that matter implemented over plain C objects (byte arrays, strings, object arrays), every other slot a stub that prints its own number. Captures `RegisterNatives`, then calls one method chosen on the command line. |
| `logstub.c` | `__android_log_print` and friends printing to stderr. Built as `libloghook.so` and preloaded, not as `liblog.so`: shadowing the real name breaks every other library that wants symbols the stub does not have. |
| `assethook.c` | The asset API over plain files, so `decompress` can be handed an `AssetManager` that opens a directory named by `ASSET_DIR`. |
| `setup.sh` | One run of the whole thing: NDK, system image, loader and libc where the loader looks for them, build, first calls. |
| `decompress.sh` | The way the app does it: `decompress(AssetManager, name, out, ref)` with the asset stub, two reference directories. |
| `probe.sh` | `decompress_from_so` and `decompress_with_ref`, with the liblog stub talking. |
| `obi.sh` | The container reader (`openNative`/`getFileCountNative`/`getFilePtrNative`/`getFileSizeNative`) and the byte stream (`openObArchiveBytesNative`, `openRawBytesNative`, `readNative`). |
| `obi2.sh` | The same call with the two longs in the other order, which decides how they are read. |
| `classes.sh` | Reads the obfuscated types out of the APK, writes their class files, and proves a JVM accepts them (`javap`, `Class.forName`). |
| `dexinfo.py` | Reads the `class_def` entries for those types from the DEX. |
| `mkclass.py` | Writes class files for names `javac` cannot spell (`X/022`), with the declared methods and empty stubs for the types they mention. |
| `dexcalls.py` | Reads Dalvik bytecode, not decompiled source, and reports the call sites of the driver together with the constants in the callers. |

## Prerequisites

A Linux box or WSL2 Ubuntu, with `python3`, `curl`, `unzip`, `7z`, and a JDK
(`classes.sh` only). The APK is not in the repository; point `APK` at it. The
scripts also need NDK r27c and the API 28 x86_64 system image, both downloaded
by `setup.sh` on first run.

Everything is `x86_64`, matching `lib/x86_64/libsuperpack.so` inside the APK.
Another ABI needs another system image and another `--target`.

## Running it

Inside WSL, from the repository root:

```
APK=/path/to/WhatsApp.apk bash tools/apk/setup.sh
APK=/path/to/WhatsApp.apk bash tools/apk/decompress.sh
APK=/path/to/WhatsApp.apk bash tools/apk/classes.sh
```

`setup.sh` also pulls `assets/compressed/strings/strings_it.spo` out of the APK
into `.tools/apk/spo/`, the directory the asset stub opens (`ASSET_DIR`).
`obi2.sh` additionally tries a `payload.bin` carved out of that pack during the
investigation: its last case needs the file, the others do not.

Everything is downloaded into `/root/bionic` and kept there, because this WSL
instance stops between commands and takes `/tmp` with it. It extracts
`system/bin/linker64` and `system/lib64` from the image, puts them at the paths
bionic resolves by itself (`/system/bin/linker64`, `/system/lib64`), and builds
the harness with:

```
--target=x86_64-linux-android24 --sysroot=$NDK/sysroot
-Wl,--dynamic-linker=/system/bin/linker64
```

That last flag is the whole trick: the harness names the bionic loader as its
interpreter, so it starts like any other program and the loader does the
loading. Starting `linker64` as a command does not work, because it ignores the
arguments and prints that it is a helper program.

At run time:

- `LD_PRELOAD="/root/bionic/stub/libloghook.so /root/bionic/stub/libandroidhook.so"`
  puts the log and asset stubs in front of the real ones;
- `LD_LIBRARY_PATH=/root/bionic/sys/system/lib64`;
- `ASSET_DIR` points at the directory the fake `AssetManager` opens.

Two details that cost a crash each: `JNIEnv` and `JavaVM` are already pointers
to their tables, so `JNI_OnLoad` gets `&gEnvValue` and `&gVmValue`, not the
functions; and the string pack must be the one named by the asset argument,
because the stub opens that name inside `ASSET_DIR`.

## The harness command line

```
harness --lib PATH [--fn NAME] [--spo PATH] [--payload PATH]
        [--out DIR] [--ref DIR]
        [--scenario obi|obib|obib2|raw --buf PATH --mode N]
```

`--lib` is required and is the only thing the harness insists on. With `--fn`
it calls one captured native method and prints its arguments and return; the
argument recipe depends on the signature, and `no argument recipe for NAME`
means exactly that. With `--scenario` it instead feeds a file to the container
or byte-stream entry points, in the order the scenario name picks.

## What the run found

- `JNI_OnLoad -> 65542` and 19 native methods registered across 4 classes, with
  the signatures the library itself declares - real classes (`AssetManager`,
  `InputStream`, paths), not the `[B` the DEX suggests for some of them.
- `decompress` through the fake `AssetManager` opens the asset
  (`archive start: 0 length: 195086`) and then refuses it:
  `Could not open archive: 1/7/0`.
- `openNative(0/1/2, ptr, 195086)` returns `null` with
  `Could not read openbox archive`.
- `openObArchiveBytesNative` crashes inside the library (`0x2fa0e`, and
  `0x2fa2e` with the lengths swapped).
- `openRawBytesNative` returns a handle, but `readNative` reads 0 bytes.

The pack is not a superpack the driver accepts and not an openbox archive
either. It is also the right file: `assets/compressed/strings/strings_it.spo`
in the APK is 195086 bytes, sha256 `e620cae0...4d13a503`, byte for byte what the
harness was handed.

## Limits

- There is no VM. Any JNI slot not implemented prints its number and returns 0;
  a library that needs one of them stops there instead of pretending otherwise.
- The obfuscated types are generated as declarations only. Nothing implements
  them, and nothing needs to: the native code passes them around as types.
- The runtime is bionic placed at `/system/...` in a throwaway container. It is
  not a sandbox and not an emulator.
- None of this is part of the app build. Nothing here ships.

## Where the reasoning lives

`docs/superpowers/specs/2026-11-04-spo-driver-run.md` has the transcript of every
attempt and the disassembly that decided the next one.
