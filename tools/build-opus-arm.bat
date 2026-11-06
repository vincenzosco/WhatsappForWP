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
rem tools\build-opus-arm.bat [opus-src] [out-dll]
rem
rem opus-src the libopus checkout (default.tools\opus-src)
rem out-dll where the DLL lands (default: WhatsappApp\waopus.dll)
rem
rem Preparation is one command - the sources are not vendored, the built DLL is what
rem the repository ships:
rem git clone --depth 1 https://github.com/xiph/opus.git.tools\opus-src
rem
rem What this script cannot do, and what the guard at the end says out loud:
rem the CRT this toolset links is the desktop ARM one, so the image imports
rem `KERNEL32.dll` and the console half of the CRT start-up (`WriteConsoleW`,
rem `SetStdHandle`, `GetCommandLineW`). On 4 and the phone
rem answered `0x8007007E` to exactly that image, while the three ARM modules it
rem does load import `api-ms-win-core-*` and never `KERNEL32.dll`. `/APPCONTAINER`
rem was tried and is not the answer: those modules have the bit clear, and the
rem image that is refused has it set.
rem
rem A loadable build needs a toolset whose import libraries name the API sets
rem (the Windows Phone 8.1 C++ one, which is not installed on this machine), or
rem an image linked against no desktop CRT at all. The entry point is this DLL's
rem own now (`tools/opus/waentry.c`, `/ENTRY:WaDllMain`) and the container bit is
rem gone; the CRT is still the desktop one, so the import table still names
rem `KERNEL32.dll` and the guard at the last step still refuses the image - on
rem purpose, here, instead of on the phone at the first voice note.
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

set "CFLAGS=/nologo /c /O2 /MT /GS- /DNDEBUG /DFIXED_POINT /DOPUS_BUILD /DWINAPI_FAMILY=WINAPI_FAMILY_APP /DUSE_ALLOCA /D_CRT_SECURE_NO_WARNINGS"
set "CFLAGS=!CFLAGS! /I"%SRC%\include" /I"%SRC%\src" /I"%SRC%\celt" /I"%SRC%\silk" /I"%SRC%\silk\fixed""
set "WACL=%VC%\bin\x86_arm\cl.exe"

echo waopus: compiling the wrapper...
"%WACL%" !CFLAGS! /Fo"%OBJ%\src\waopus.obj" tools\opus\waopus.c

echo waopus: compiling the DLL entry point...
"%WACL%" !CFLAGS! /Fo"%OBJ%\src\waentry.obj" tools\opus\waentry.c
if errorlevel 1 exit /b 1
if errorlevel 1 exit /b 1

echo waopus: compiling celt...
for %%f in ("%SRC%\celt\*.c") do (
  if /i not "%%~nf"=="opus_custom_demo" (
    "%WACL%" !CFLAGS! /Fo"%OBJ%\celt\%%~nf.obj" "%%~f"
    if errorlevel 1 exit /b 1
  )
)

echo waopus: compiling silk...
for %%f in ("%SRC%\silk\*.c" "%SRC%\silk\fixed\*.c") do (
  "%WACL%" !CFLAGS! /Fo"%OBJ%\silk\%%~nf.obj" "%%~f"
  if errorlevel 1 exit /b 1
)

echo waopus: compiling src...
for %%f in ("%SRC%\src\opus.c" "%SRC%\src\opus_decoder.c" "%SRC%\src\opus_encoder.c" "%SRC%\src\repacketizer.c" "%SRC%\src\analysis.c" "%SRC%\src\mlp.c" "%SRC%\src\mlp_data.c" "%SRC%\src\extensions.c" "%SRC%\src\mapping_matrix.c" "%SRC%\src\opus_multistream.c" "%SRC%\src\opus_multistream_encoder.c") do (
  "%WACL%" !CFLAGS! /Fo"%OBJ%\src\%%~nf.obj" "%%~f"
  if errorlevel 1 exit /b 1
)

echo waopus: linking...
"%VC%\bin\x86_arm\link.exe" /nologo /DLL /MACHINE:ARM /ENTRY:WaDllMain /INCREMENTAL:NO /NODEFAULTLIB:kernel32.lib .tools\waapiset\waheap.lib .tools\waapiset\waerr.lib /OUT:"%OUT%" "%OBJ%\celt\*.obj" "%OBJ%\silk\*.obj" "%OBJ%\src\*.obj"
if errorlevel 1 exit /b 1
echo waopus: checking the image against what the phone accepts...
where node >nul 2>&1
if errorlevel 1 (
  echo waopus: node is not on PATH, so the image was not checked.
  exit /b 1
)
node "%~dp0check-waopus.js"
if errorlevel 1 (
  echo waopus: the guard refused this image - the phone would answer 0x8007007E.
  exit /b 1
)

echo waopus: %OUT%
endlocal
