@echo off
REM Builds a self-contained, single-file AdTrim.exe ready to copy and
REM run on any Windows x64 machine - no .NET 10 runtime install required on
REM the target. Output lands in:
REM
REM   src\AdTrim\bin\Release\net10.0-windows\win-x64\publish\
REM
REM Drop that whole folder somewhere like C:\Tools\AdTrim\ and
REM double-click AdTrim.exe. The csproj's existing Content rule for
REM binaries\ ensures libmpv-2.dll + ffmpeg.exe + ffprobe.exe land beside
REM the EXE; the resolver in FfmpegRunner / MpvPreviewViewModel prefers
REM AppContext.BaseDirectory\binaries\... over the dev env vars, so the
REM published copy needs no environment setup.
REM
REM Flags:
REM   -c Release                          optimized build
REM   -r win-x64                          target runtime identifier
REM   --self-contained                    bundle the .NET 10 runtime
REM   PublishSingleFile=true              collapse managed assemblies into the EXE
REM   IncludeNativeLibrariesForSelfExtract=true   bundle native runtime libs too
REM   PublishReadyToRun=true              pre-JIT for faster startup
REM
REM NOT enabling trimming: WPF relies on reflection for XAML / data binding
REM and trimming silently breaks both at runtime.

setlocal

set "PROJECT=%~dp0src\AdTrim\AdTrim.csproj"
set "OUTDIR=%~dp0src\AdTrim\bin\Release\net10.0-windows\win-x64\publish"

REM Resolve ffmpeg / ffprobe location for the post-publish copy step. Same
REM env var dev-launch.cmd uses. The runtime resolver in FfmpegRunner only
REM looks under AppContext.BaseDirectory\binaries\ffmpeg\win-x64\ for the
REM published EXE, so we must drop them there explicitly - the csproj's
REM Content rule only copies what's in the SOURCE binaries\ tree, and the
REM user's dev install doesn't live there.
set "FFMPEG_DIR=%ADTRIM_FFMPEG_DIR%"
if not defined ADTRIM_FFMPEG_DIR set "FFMPEG_DIR=C:\Program Files\ffmpeg\bin"

if not exist "%FFMPEG_DIR%\ffmpeg.exe"  goto :missing_ffmpeg
if not exist "%FFMPEG_DIR%\ffprobe.exe" goto :missing_ffprobe

REM Wipe any leftovers from previous publish runs. `dotnet publish` writes
REM the new build's files but does NOT remove files that aren't part of
REM the current build -- so after a project rename (e.g. ComSkipEditor to
REM AdTrim), the OLD assembly's 450 MB self-contained .exe lingers next
REM to the new one and NSIS bundles both, inflating the installer. A full
REM wipe is cheap relative to the publish itself and prevents the trap.
if exist "%OUTDIR%" (
  echo Cleaning previous publish output...
  rmdir /s /q "%OUTDIR%"
)

echo Publishing AdTrim (self-contained, single-file, ReadyToRun)...
echo Source:  %PROJECT%
echo Output:  %OUTDIR%
echo FFmpeg:  %FFMPEG_DIR%
echo.

dotnet publish "%PROJECT%" ^
  -c Release ^
  -r win-x64 ^
  --self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true

if errorlevel 1 goto :publish_failed

REM Drop ffmpeg + ffprobe into the published binaries\ tree so the EXE is
REM actually self-sufficient (the resolver requires them at the path below).
REM Also copy any sidecar .dll files - "shared" FFmpeg builds (e.g. the
REM user's current C:\Program Files\ffmpeg install) put avcodec / avformat /
REM avutil / etc. next to ffmpeg.exe, and the EXE crashes on startup without
REM them. "Static" gyan.dev builds have no .dll files; the conditional copy
REM is a no-op in that case.
set "FFMPEG_OUT=%OUTDIR%\binaries\ffmpeg\win-x64"
if not exist "%FFMPEG_OUT%" mkdir "%FFMPEG_OUT%"
copy /Y "%FFMPEG_DIR%\ffmpeg.exe"  "%FFMPEG_OUT%\ffmpeg.exe"  >nul
if errorlevel 1 goto :copy_failed
copy /Y "%FFMPEG_DIR%\ffprobe.exe" "%FFMPEG_OUT%\ffprobe.exe" >nul
if errorlevel 1 goto :copy_failed
if exist "%FFMPEG_DIR%\*.dll" (
  copy /Y "%FFMPEG_DIR%\*.dll" "%FFMPEG_OUT%\" >nul
  if errorlevel 1 goto :copy_failed
)

REM Sanity-check libmpv landed too - copied automatically via the csproj
REM Content rule from source\binaries\mpv\win-x64\. If it's missing,
REM the EXE will crash at startup with a clearer error from LibMpv.EnsureLoaded.
if not exist "%OUTDIR%\binaries\mpv\win-x64\libmpv-2.dll" (
  echo WARNING: libmpv-2.dll is missing from the publish output.
  echo          The source tree's binaries\mpv\win-x64\libmpv-2.dll wasn't found.
  echo          See binaries\README.md for the one-time install.
)

echo.
echo Publish complete.
echo Copy this folder anywhere (e.g. C:\Tools\AdTrim\) and run AdTrim.exe:
echo   %OUTDIR%
echo.
exit /b 0

:missing_ffmpeg
echo ERROR: ffmpeg.exe not found at "%FFMPEG_DIR%\ffmpeg.exe"
echo Set ADTRIM_FFMPEG_DIR to a folder containing ffmpeg.exe + ffprobe.exe.
exit /b 1

:missing_ffprobe
echo ERROR: ffprobe.exe not found at "%FFMPEG_DIR%\ffprobe.exe"
echo Set ADTRIM_FFMPEG_DIR to a folder containing ffmpeg.exe + ffprobe.exe.
exit /b 1

:publish_failed
echo.
echo ERROR: dotnet publish failed. See output above.
exit /b 1

:copy_failed
echo.
echo ERROR: failed to copy ffmpeg/ffprobe into "%FFMPEG_OUT%".
exit /b 1
