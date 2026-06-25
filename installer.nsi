; AdTrim installer (NSIS)
;
; Builds a per-user, no-UAC installer that:
;   1. Copies the published EXE + binaries from
;      src\AdTrim\bin\Release\net10.0-windows\win-x64\publish\
;      into the user-chosen install dir (defaults to %LOCALAPPDATA%\AdTrim).
;   2. Registers an "Edit with AdTrim" right-click verb on .mp4 files
;      under HKCU\Software\Classes\SystemFileAssociations\.mp4 - so the verb
;      appears alongside whatever default app you have for .mp4 without
;      claiming or replacing the default association.
;   3. Creates Start Menu shortcuts.
;   4. Writes an Add/Remove Programs entry (Settings → Apps → Installed apps).
;   5. Bundles an uninstaller that reverses all of the above.
;
; Build: run installer.cmd, which invokes makensis on this file.
; Prereq: publish.cmd must have been run first.

!define APPNAME       "AdTrim"
!define COMPANYNAME   "AdTrim"
; APPVERSION is passed in by installer.cmd via /DAPPVERSION=x.y.z (parsed
; out of AppVersion.cs - single source of truth). The fallback only fires
; if you invoke makensis directly for testing, in which case the resulting
; installer is clearly marked dev so it doesn't get mistaken for a release.
!ifndef APPVERSION
  !define APPVERSION "0.0.0-dev"
!endif
!define DESCRIPTION   "Frame-accurate, non-destructive editor for trimming ad breaks out of recorded TV."
!define PUBLISH_DIR   "src\AdTrim\bin\Release\net10.0-windows\win-x64\publish"
!define EXE_NAME      "AdTrim.exe"

; Per-user install - no admin elevation, no UAC prompt. Lives only for the
; current user. Per-machine (Program Files + HKLM) is overkill for a
; personal app where the same user is the installer and the audience.
RequestExecutionLevel user
Unicode true

Name "${APPNAME}"
OutFile "AdTrim-Setup-v${APPVERSION}.exe"
InstallDir "$LOCALAPPDATA\${APPNAME}"
InstallDirRegKey HKCU "Software\${APPNAME}" "InstallDir"

; Per-file LZMA: ~30-60 sec compression, installer ~300 MB.
;
; Solid LZMA (SetCompressor /SOLID lzma) gets the installer down to ~200 MB
; but takes 5-10 min of single-threaded CPU on the 770 MB publish payload.
; Per-file is the iteration-friendly default; switch to /SOLID lzma when
; you actually want to share the installer and the build time is worth it.
SetCompressor lzma

!include "MUI2.nsh"

!define MUI_ICON   "src\AdTrim\app.ico"
!define MUI_UNICON "src\AdTrim\app.ico"

; Install pages
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APPNAME}"
!insertmacro MUI_PAGE_FINISH

; Uninstall pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetOutPath "$INSTDIR"

  ; Pull the entire publish folder verbatim. NSIS recurses with /r and
  ; preserves subfolder structure (binaries\ffmpeg\, binaries\mpv\,
  ; localization satellite dirs, etc.).
  File /r "${PUBLISH_DIR}\*.*"

  ; --- Add/Remove Programs entry (per-user hive) ---
  !define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${EXE_NAME},0"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion"  "${APPVERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher"       "${COMPANYNAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "Comments"        "${DESCRIPTION}"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ; Remember install dir so subsequent runs upgrade in place.
  WriteRegStr HKCU "Software\${APPNAME}" "InstallDir" "$INSTDIR"

  ; --- Shell verb on .mp4 ---
  ; Using SystemFileAssociations means the verb is added alongside the
  ; user's existing .mp4 default-app association; double-click still
  ; opens whatever it opened before, but right-click now also offers
  ; "Edit with AdTrim".
  !define VERB_KEY "Software\Classes\SystemFileAssociations\.mp4\shell\EditWithAdTrim"
  WriteRegStr HKCU "${VERB_KEY}"          ""     "Edit with ${APPNAME}"
  WriteRegStr HKCU "${VERB_KEY}"          "Icon" "$INSTDIR\${EXE_NAME},0"
  WriteRegStr HKCU "${VERB_KEY}\command"  ""     '"$INSTDIR\${EXE_NAME}" "%1"'

  ; --- Start Menu shortcuts ---
  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut  "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXE_NAME}" "" "$INSTDIR\${EXE_NAME}" 0
  CreateShortcut  "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"

  ; --- Bundle the uninstaller ---
  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
  ; Reverse the install in the opposite order: registry first (cheap and
  ; visible), then file tree (slow), then Start Menu folder.
  DeleteRegKey HKCU "Software\Classes\SystemFileAssociations\.mp4\shell\EditWithAdTrim"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
  DeleteRegKey HKCU "Software\${APPNAME}"

  RMDir /r "$INSTDIR"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
SectionEnd
