@echo off
rem Builds waopus.dll - the Opus encoder the phone loads - for Windows Phone ARM.
rem
rem Why a script and not a Visual Studio project: the C++ toolset for Windows Phone
rem 8.1 is not installed on this machine, but the ARM cross compiler that ships with
rem Visual Studio 2015 is (VC\bin\x86_arm), and a DLL that only does arithmetic needs
rem no phone-specific project settings. The C runtime is linked statically (/MT), so
rem the DLL imports nothing from the C runtime and cannot disagree with the phone's.
rem
rem Usage:
rem   tools\build-opus-arm.bat [opus-src] [out-dll]
rem
rem   opus-src  the libopus checkout (default: .tools\opus-src)
rem   out-dll   where the DLL lands   (default: WhatsappApp\waopus.dll)
rem
rem Preparation is one command - the sources are not vendored, the built DLL is what
rem the repository ships:
rem   git clone --depth 1 https://github.com/xiph/opus.git .tools\opus-src
rem
rem `/APPCONTAINER` is not optional: the phone loads this DLL from a packaged process,
rem and a native image with no app-container flag in its `DllCharacteristics` is refused
rem with `0x8007007E` ("the specified module could not be found") even when the file is
rem in the installed package and imports nothing but KERNEL32 - which is exactly what the
rem log of 4 November 2026 showed (`waopus.dll is in the package and Windows will not
rem load it`).
rem
rem Three defines are the whole port: WINAPI_FAMILY_APP (the UCRT refuses desktop ARM
rem headers outright), USE_ALLOCA (libopus needs one of three allocation modes on
rem MSVC) and OPUS_BUILD (libopus' own build switch). The encoder's half of the
rem library is compiled; the decoder is only there because
rem `opus_packet_get_nb_frames` lives in opus_decoder.c.

setlocal enabledelayedexpansion

set "SRC=%~1"
if "%SRC%"=="" set "SRC=.tools\opus-src"
set "OUT=%~2"
if "%OUT%"=="" set "OUT=WhatsappApp\waopus.dll"

set "VC=C:\Program Files (x86)\Microsoft Visual Studio 14.0\VC"
set "KITS=C:\Program Files (x86)\Windows Kits\10"
set "UCRT_VER=10.0.10240.0"

if not exist "%SRC%\include\opus.h" (
  echo waopus: no libopus sources in "%SRC%" - clone xiph/opus there first.
  exit /b 2
)

set "PATH=%VC%\bin\x86_arm;%VC%\bin;%PATH%"
set "INCLUDE=%VC%\include;%KITS%\Include\%UCRT_VER%\ucrt;%KITS%\Include\%UCRT_VER%\um;%KITS%\Include\%UCRT_VER%\shared"
set "LIB=%VC%\lib\arm;%KITS%\Lib\%UCRT_VER%\ucrt\arm;%KITS%\Lib\%UCRT_VER%\um\arm"

set "OBJ=.tools\opus-build\arm"
if exist "%OBJ%" rmdir /s /q "%OBJ%"
mkdir "%OBJ%\celt" "%OBJ%\silk" "%OBJ%\src"

set "CFLAGS=/nologo /c /O2 /MT /DNDEBUG /DOPUS_BUILD /DWINAPI_FAMILY=WINAPI_FAMILY_APP /DUSE_ALLOCA /D_CRT_SECURE_NO_WARNINGS"
set "CFLAGS=!CFLAGS! /I"%SRC%\include" /I"%SRC%\src" /I"%SRC%\celt" /I"%SRC%\silk" /I"%SRC%\silk\float""
set "WACL=%VC%\bin\x86_arm\cl.exe"

echo waopus: compiling the wrapper...
"%WACL%" !CFLAGS! /Fo"%OBJ%\src\waopus.obj" tools\opus\waopus.c
if errorlevel 1 exit /b 1

echo waopus: compiling celt...
for %%f in ("%SRC%\celt\*.c") do (
  if /i not "%%~nf"=="opus_custom_demo" (
    "%WACL%" !CFLAGS! /Fo"%OBJ%\celt\%%~nf.obj" "%%~f"
    if errorlevel 1 exit /b 1
  )
)

echo waopus: compiling silk...
for %%f in ("%SRC%\silk\*.c" "%SRC%\silk\float\*.c") do (
  "%WACL%" !CFLAGS! /Fo"%OBJ%\silk\%%~nf.obj" "%%~f"
  if errorlevel 1 exit /b 1
)

echo waopus: compiling src...
for %%f in ("%SRC%\src\opus.c" "%SRC%\src\opus_decoder.c" "%SRC%\src\opus_encoder.c" "%SRC%\src\repacketizer.c" "%SRC%\src\analysis.c" "%SRC%\src\mlp.c" "%SRC%\src\mlp_data.c" "%SRC%\src\extensions.c" "%SRC%\src\mapping_matrix.c" "%SRC%\src\opus_multistream.c" "%SRC%\src\opus_multistream_encoder.c") do (
  "%WACL%" !CFLAGS! /Fo"%OBJ%\src\%%~nf.obj" "%%~f"
  if errorlevel 1 exit /b 1
)

echo waopus: linking...
"%VC%\bin\x86_arm\link.exe" /nologo /DLL /MACHINE:ARM /APPCONTAINER /INCREMENTAL:NO /OUT:"%OUT%" "%OBJ%\celt\*.obj" "%OBJ%\silk\*.obj" "%OBJ%\src\*.obj"
if errorlevel 1 exit /b 1

echo waopus: %OUT%
endlocal
