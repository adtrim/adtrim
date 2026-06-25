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

REM ffmpeg/ffprobe are bundled from the project's own binaries tree - the
REM single source for both dev and release builds. The csproj Content rule
REM copies binaries\ into the build output (the same path the runtime
REM resolver reads), so publish.cmd only validates the tree here; it does
REM not copy from a system install. Drop the gyan.dev 8.1.2+ "full" build
REM into binaries\ffmpeg\win-x64\ (see binaries\README.md).
set "FFMPEG_DIR=%~dp0binaries\ffmpeg\win-x64"

if not exist "%FFMPEG_DIR%\ffmpeg.exe"  goto :missing_ffmpeg
if not exist "%FFMPEG_DIR%\ffprobe.exe" goto :missing_ffprobe

REM Version gate: existence isn't enough. Refuse to bundle a stale or
REM git-master FFmpeg (this is how CVE-2026-8461 reached a buildable state).
REM The floor lives in check-ffmpeg-version.ps1 (single source of truth).
echo Checking FFmpeg version...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-ffmpeg-version.ps1" -FfmpegDir "%FFMPEG_DIR%"
if errorlevel 1 goto :bad_ffmpeg_version

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

REM ffmpeg + ffprobe land in the output via the csproj Content rule (the same
REM mechanism that bundles libmpv). The version gate above already validated
REM the source tree, so sanity-check they made it across; a miss means an
REM empty source tree.
if not exist "%OUTDIR%\binaries\ffmpeg\win-x64\ffmpeg.exe" (
  echo WARNING: ffmpeg.exe is missing from the publish output.
  echo          binaries\ffmpeg\win-x64\ffmpeg.exe wasn't found in the source tree.
  echo          See binaries\README.md for the one-time install.
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

:bad_ffmpeg_version
echo.
echo ERROR: bundled FFmpeg failed the version gate. See messages above.
echo Update the binaries in "%FFMPEG_DIR%" - see binaries\README.md.
exit /b 1

:publish_failed
echo.
echo ERROR: dotnet publish failed. See output above.
exit /b 1
