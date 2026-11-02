@echo off
rem Builds waopus.dll - the Opus encoder the phone loads - for ARM.
rem
rem Why a script and not a Visual Studio project: the C++ toolset for Windows
rem Phone 8.1 is not installed here, but the ARM cross compiler that ships with
rem Visual Studio 2015 is (VC\bin\x86_arm), and a DLL that only does arithmetic
rem needs no phone-specific project settings. The C runtime is linked
rem statically (/MT), so the DLL imports nothing from the C runtime.
rem
rem Usage:
rem   tools\build-opus-arm.bat [opus-src] [out-dll]
rem
rem   opus-src  the libopus checkout (default: .tools\opus-src)
rem   out-dll   where the DLL lands   (default: WhatsappApp\waopus.dll)
rem
rem The sources are not vendored: `git clone --depth 1 https://github.com/xiph/opus.git
rem .tools\opus-src` is the whole preparation, and the built DLL is what the
rem repository ships.

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
set "INCLUDE=%VC%\include;%KITS%\Include\%UCRT_VER%\ucrt;%KITS%\Include\%UCRT_VER%\um"
set "LIB=%VC%\lib\arm;%KITS%\Lib\%UCRT_VER%\ucrt\arm;%KITS%\Lib\%UCRT_VER%\um\arm"

set "OBJ=.tools\opus-build\arm"
if exist "%OBJ%" rmdir /s /q "%OBJ%"
mkdir "%OBJ%" 2>nul

set "CFLAGS=/nologo /c /O2 /MT /DNDEBUG /DOPUS_BUILD"
set "CFLAGS=%CFLAGS% /I"%SRC%\include" /I"%SRC%\src" /I"%SRC%\celt" /I"%SRC%\silk""
set "CL=%VC%\bin\x86_arm\cl.exe"

echo waopus: compiling the wrapper...
"%CL%" %CFLAGS% /Fo"%OBJ%\waopus.obj" tools\opus\waopus.c
if errorlevel 1 exit /b 1

echo waopus: compiling celt...
for %%f in ("%SRC%\celt\*.c") do (
  "%CL%" %CFLAGS% /Fo"%OBJ%\%%~nf.obj" "%%~f" >nul || exit /b 1
)

echo waopus: compiling silk...
for %%f in ("%SRC%\silk\*.c" "%SRC%\silk\float\*.c") do (
  "%CL%" %CFLAGS% /Fo"%OBJ%\%%~nf.obj" "%%~f" >nul || exit /b 1
)

echo waopus: compiling src...
for %%f in ("%SRC%\src\opus.c" "%SRC%\src\opus_encoder.c" "%SRC%\src\repacketizer.c" "%SRC%\src\analysis.c" "%SRC%\src\mlp.c" "%SRC%\src\mlp_data.c" "%SRC%\src\extensions.c" "%SRC%\src\mapping_matrix.c" "%SRC%\src\opus_multistream.c" "%SRC%\src\opus_multistream_encoder.c") do (
  "%CL%" %CFLAGS% /Fo"%OBJ%\%%~nf.obj" "%%~f" >nul || exit /b 1
)

echo waopus: linking...
"%VC%\bin\x86_arm\link.exe" /nologo /DLL /MACHINE:ARM /INCREMENTAL:NO /OUT:"%OUT%" "%OBJ%\*.obj"
if errorlevel 1 exit /b 1

echo waopus: %OUT%
endlocal
