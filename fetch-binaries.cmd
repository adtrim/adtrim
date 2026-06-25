@echo off
REM Double-click to download/update the bundled ffmpeg + libmpv into binaries\.
REM See binaries\README.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-binaries.ps1"
echo.
pause
