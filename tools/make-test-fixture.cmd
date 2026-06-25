@echo off
REM Generates fixtures\rookie-30s.mp4 from the user's full Rookie recording.
REM
REM The output is a 30-second slice starting at 28:25 (1705s) of the source,
REM spanning the real Part 3 -> Commercial 4 boundary at 28:35.750 (1715.75s).
REM It is re-encoded to mpeg2video + ac3 (matching the source codec mix so the
REM export pipeline exercises the real codec path) and chapter metadata is
REM injected from tools\rookie-30s-chapters.ffmetadata.
REM
REM Output is gitignored (.gitignore: *.mp4). Regenerate locally by running
REM this script after cloning. Integration tests skip cleanly when the file
REM is absent.
REM
REM Source path is taken from ADTRIM_ROOKIE_FIXTURE_SRC if set, else
REM the documented default location below.

setlocal

set "SRC=%ADTRIM_ROOKIE_FIXTURE_SRC%"
if not defined SRC set "SRC=D:\Recorded TV\.test_autoconvert\The Rookie (2018) - S08E18 - The Bandit.mp4"

set "FFMPEG=%ADTRIM_FFMPEG_DIR%\ffmpeg.exe"
if not defined ADTRIM_FFMPEG_DIR set "FFMPEG=C:\Program Files\ffmpeg\bin\ffmpeg.exe"
set "FFPROBE=%ADTRIM_FFMPEG_DIR%\ffprobe.exe"
if not defined ADTRIM_FFMPEG_DIR set "FFPROBE=C:\Program Files\ffmpeg\bin\ffprobe.exe"

set "REPO=%~dp0.."
set "META=%~dp0rookie-30s-chapters.ffmetadata"
set "OUT=%REPO%\fixtures\rookie-30s.mp4"

if not exist "%SRC%" goto :missing_src
if not exist "%FFMPEG%" goto :missing_ffmpeg
if not exist "%META%" goto :missing_meta

echo Source:   %SRC%
echo Output:   %OUT%
echo Metadata: %META%
echo.
echo Slicing 30 sec starting at 28:25 (1705s) and re-encoding to mpeg2video + ac3...
echo.

"%FFMPEG%" -y ^
  -ss 1705 -i "%SRC%" ^
  -i "%META%" ^
  -t 30 ^
  -map 0:v:0 -map 0:a:0 ^
  -map_metadata 1 -map_chapters 1 ^
  -c:v mpeg2video -b:v 8M ^
  -c:a ac3 -b:a 384k ^
  "%OUT%"

if errorlevel 1 goto :ffmpeg_failed

echo.
echo Done. Verifying output...
echo.
"%FFPROBE%" -v error -show_format -show_streams -show_chapters -of json "%OUT%"
if errorlevel 1 goto :ffprobe_failed

echo.
echo Fixture written to: %OUT%
exit /b 0

:missing_src
echo ERROR: source fixture not found at "%SRC%"
echo Set ADTRIM_ROOKIE_FIXTURE_SRC to the full Rookie recording path.
exit /b 1

:missing_ffmpeg
echo ERROR: ffmpeg.exe not found at "%FFMPEG%"
echo Set ADTRIM_FFMPEG_DIR to a folder containing ffmpeg.exe + ffprobe.exe.
exit /b 1

:missing_meta
echo ERROR: chapter metadata file not found at "%META%"
echo This file ships with the repo. Reclone or restore it.
exit /b 1

:ffmpeg_failed
echo ERROR: ffmpeg failed while building the fixture. See output above.
exit /b 1

:ffprobe_failed
echo ERROR: ffprobe failed to verify the output fixture. See output above.
exit /b 1
