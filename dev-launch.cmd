@echo off
REM Dev-time launcher for AdTrim.
REM Points the runtime FFmpeg + libmpv resolvers at known dev installs so we
REM don't need to drop bundled binaries into binaries\ffmpeg\... and
REM binaries\mpv\... until the installer story lands.

setlocal
if not defined ADTRIM_FFMPEG_DIR set "ADTRIM_FFMPEG_DIR=C:\Program Files\ffmpeg\bin"

echo Using FFmpeg from: %ADTRIM_FFMPEG_DIR%
if not exist "%ADTRIM_FFMPEG_DIR%\ffmpeg.exe" goto :missing_ffmpeg

REM libmpv: prefer bundled binaries\mpv\win-x64\libmpv-2.dll; otherwise allow
REM an env-var override. The runtime resolver also checks the bundled path.
if not defined ADTRIM_MPV_DIR (
    if exist "%~dp0binaries\mpv\win-x64\libmpv-2.dll" (
        set "ADTRIM_MPV_DIR=%~dp0binaries\mpv\win-x64"
    )
)
if defined ADTRIM_MPV_DIR (
    echo Using libmpv  from: %ADTRIM_MPV_DIR%
) else (
    echo WARNING: libmpv-2.dll not found in binaries\mpv\win-x64\ and
    echo          ADTRIM_MPV_DIR is unset. The video preview will fail
    echo          to initialize. See binaries\README.md ^(libmpv section^).
)

echo Launching: dotnet run --project "%~dp0src\AdTrim\AdTrim.csproj"
echo.

dotnet run --project "%~dp0src\AdTrim\AdTrim.csproj" --configuration Debug

set EXITCODE=%ERRORLEVEL%
echo.
echo dotnet run exited with code %EXITCODE%
exit /b %EXITCODE%

:missing_ffmpeg
echo ERROR: ffmpeg.exe not found at "%ADTRIM_FFMPEG_DIR%\ffmpeg.exe"
echo Set ADTRIM_FFMPEG_DIR to a folder containing ffmpeg.exe + ffprobe.exe,
echo or drop them into binaries\ffmpeg\win-x64\ -- see binaries\README.md.
exit /b 1
