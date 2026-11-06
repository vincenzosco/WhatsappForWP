@echo off
rem Builds waaudio.dll - the Media Foundation probe the call path asks before it
rem encodes anything - for Windows Phone ARM.
rem
rem Why not a Visual Studio project: this DLL is two exported functions and a
rem handful of COM calls. The ARM cross compiler that ships with Visual Studio
rem 2015 (VC\bin\x86_arm) builds it, and the phone-specific project settings
rem would only restate what the link line says.
rem
rem Why the import libraries are built here: the phone's loader resolves a module
rem name before it resolves symbols, so an image that names a desktop module is
rem refused with 0x8007007E (that is the whole story of waopus.dll on 4 and 5
rem November 2026). tools\audio\*.def name the modules the phone ships, and
rem `lib /def` turns each into the import library the link line uses. Media
rem Foundation is the first surface here that is not an API set: MFPlat.dll and
rem Mfreadwrite.dll are the phone's own modules, and whether the loader resolves
rem them is the question the probe exists to answer.
rem
rem Usage:
rem   tools\build-waudio-arm.bat [out-dll]
rem
rem   out-dll  where the DLL lands (default: WhatsappApp\waaudio.dll)
rem
rem Every path below is absolute on purpose: the script changes directory to the
rem repository root first, and a relative object path was observed here to fail
rem the `if exist` test and then the link's input open, on the same file `dir`
rem listed. Absolute paths make the question moot.
rem
rem The link writes into .tools and the DLL is copied out afterwards: `link`
rem always emits an export object next to its output, and a stray waaudio.exp in
rem the package folder is a file the guard and the repository both have to
rem ignore. .tools is not in the repository.
rem
rem The C runtime is not linked: the entry point is this package's own
rem (tools/opus/waentry.c, /ENTRY:WaDllMain), so stdio, argv and the locale never
rem reach the import table, and the kernel functions the remaining CRT objects ask
rem for are satisfied by the API-set libraries instead of KERNEL32.dll.

setlocal enabledelayedexpansion

cd /d "%~dp0.."
set "ROOT=%CD%"

set "OUT=%~1"
if "%OUT%"=="" set "OUT=%ROOT%\WhatsappApp\waaudio.dll"

set "VC=C:\Program Files (x86)\Microsoft Visual Studio 14.0\VC"
set "KITS=C:\Program Files (x86)\Windows Kits\10"
set "UCRT_VER=10.0.10240.0"

if not exist "%VC%\bin\x86_arm\cl.exe" (
  echo waudio: no ARM cross compiler in "%VC%\bin\x86_arm" - install the C++ tools of Visual Studio 2015.
  exit /b 2
)

set "PATH=%VC%\bin\x86_arm;%VC%\bin;%PATH%"
set "INCLUDE=%VC%\include;%KITS%\Include\%UCRT_VER%\ucrt;%KITS%\Include\%UCRT_VER%\um;%KITS%\Include\%UCRT_VER%\shared"
set "LIB=%VC%\lib\arm;%KITS%\Lib\%UCRT_VER%\ucrt\arm;%KITS%\Lib\%UCRT_VER%\um\arm"

set "OBJ=%ROOT%\.tools\waudio-build\arm"
if exist "%OBJ%" rmdir /s /q "%OBJ%"
mkdir "%OBJ%"

echo waudio: building the import libraries...
lib /nologo /def:"%ROOT%\tools\audio\wamf.def" /machine:arm /out:"%OBJ%\wamf.lib" >nul
if errorlevel 1 exit /b 1
lib /nologo /def:"%ROOT%\tools\audio\wamfrw.def" /machine:arm /out:"%OBJ%\wamfrw.lib" >nul
if errorlevel 1 exit /b 1
lib /nologo /def:"%ROOT%\tools\audio\wacom.def" /machine:arm /out:"%OBJ%\wacom.lib" >nul
if errorlevel 1 exit /b 1
lib /nologo /def:"%ROOT%\tools\opus\waheap.def" /machine:arm /out:"%OBJ%\waheap.lib" >nul
if errorlevel 1 exit /b 1
lib /nologo /def:"%ROOT%\tools\opus\waerr.def" /machine:arm /out:"%OBJ%\waerr.lib" >nul
if errorlevel 1 exit /b 1

set "CFLAGS=/nologo /c /O2 /MT /GS- /GR- /EHs-c- /DNDEBUG /DWINAPI_FAMILY=WINAPI_FAMILY_APP /D_CRT_SECURE_NO_WARNINGS"

echo waudio: compiling the probe...
cl %CFLAGS% /TP /Fo"%OBJ%\waaudio.obj" "%ROOT%\tools\audio\waaudio.c"
if errorlevel 1 exit /b 1

echo waudio: compiling the DLL entry point...
cl %CFLAGS% /Fo"%OBJ%\waentry.obj" "%ROOT%\tools\opus\waentry.c"
if errorlevel 1 exit /b 1

echo waudio: linking...
link /nologo /DLL /MACHINE:ARM /ENTRY:WaDllMain /INCREMENTAL:NO /NODEFAULTLIB:kernel32.lib /NOIMPLIB ^
  /OUT:"%OBJ%\waaudio.dll" "%OBJ%\waaudio.obj" "%OBJ%\waentry.obj" ^
  "%OBJ%\wamf.lib" "%OBJ%\wamfrw.lib" "%OBJ%\wacom.lib" "%OBJ%\waheap.lib" "%OBJ%\waerr.lib" ^
  "%KITS%\Lib\%UCRT_VER%\um\arm\mfuuid.lib"
if errorlevel 1 exit /b 1

copy /y "%OBJ%\waaudio.dll" "%OUT%" >nul
if errorlevel 1 exit /b 1

echo waudio: %OUT%
endlocal
