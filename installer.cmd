@echo off
REM Builds AdTrim-Setup.exe by invoking NSIS on installer.nsi.
REM
REM Prereqs:
REM   - publish.cmd has been run (the .nsi pulls from the publish folder)
REM   - NSIS is installed (https://nsis.sourceforge.io/Download); the script
REM     auto-detects the default install locations OR you can override via
REM     %ADTRIM_NSIS_DIR%
REM
REM Output: AdTrim-Setup-v<version>.exe at the repo root, where <version>
REM is read from AppVersion.Numeric in src\AdTrim\AppVersion.cs (single
REM source of truth for the app version). The same version goes into the
REM NSIS APPVERSION macro so the
REM Add/Remove Programs entry stays accurate.
REM
REM End-to-end flow for an updated app:
REM   publish.cmd && installer.cmd
REM   Then run AdTrim-Setup-v<version>.exe.

setlocal enabledelayedexpansion

set "NSI=%~dp0installer.nsi"
set "PUBLISH=%~dp0src\AdTrim\bin\Release\net10.0-windows\win-x64\publish"
set "VERSIONFILE=%~dp0src\AdTrim\AppVersion.cs"

REM ----- Parse AppVersion.Numeric out of AppVersion.cs -----
REM Target line:  public const string Numeric = "1.0.NNNN";
REM Tokenize on '=', take everything after, strip whitespace/quotes/semicolon.
set "APPVERSION="
for /f "tokens=2 delims==" %%a in ('findstr /C:"public const string Numeric" "%VERSIONFILE%"') do (
  set "RAW=%%a"
)
if not defined RAW goto :missing_version
set "RAW=!RAW: =!"
set "RAW=!RAW:"=!"
set "APPVERSION=!RAW:;=!"
if "!APPVERSION!"=="" goto :missing_version

if defined ADTRIM_NSIS_DIR (
  set "MAKENSIS=%ADTRIM_NSIS_DIR%\makensis.exe"
  goto :have_nsis
)
if exist "C:\Program Files (x86)\NSIS\makensis.exe" (
  set "MAKENSIS=C:\Program Files (x86)\NSIS\makensis.exe"
  goto :have_nsis
)
if exist "C:\Program Files\NSIS\makensis.exe" (
  set "MAKENSIS=C:\Program Files\NSIS\makensis.exe"
  goto :have_nsis
)
goto :missing_nsis

:have_nsis
if not exist "%PUBLISH%\AdTrim.exe" goto :missing_publish

set "OUTFILE=%~dp0AdTrim-Setup-v!APPVERSION!.exe"

echo Using NSIS: %MAKENSIS%
echo Version:   !APPVERSION!
echo Publish:   %PUBLISH%
echo Output:    !OUTFILE!
echo.

REM cd into the repo root so relative paths inside installer.nsi resolve.
cd /d "%~dp0"
"%MAKENSIS%" /DAPPVERSION=!APPVERSION! "%NSI%"
if errorlevel 1 goto :failed

echo.
echo Installer built: !OUTFILE!
echo Run it to install AdTrim (per-user, no admin required).
echo.
exit /b 0

:missing_version
echo ERROR: could not parse AppVersion.Numeric from "%VERSIONFILE%".
echo Expected a line like:  public const string Numeric = "1.0.NNNN";
exit /b 1

:missing_nsis
echo ERROR: makensis.exe not found.
echo Install NSIS from https://nsis.sourceforge.io/Download
echo or set ADTRIM_NSIS_DIR to the folder containing makensis.exe.
exit /b 1

:missing_publish
echo ERROR: publish folder not found at "%PUBLISH%"
echo Run publish.cmd first.
exit /b 1

:failed
echo.
echo ERROR: NSIS failed. See output above.
exit /b 1
