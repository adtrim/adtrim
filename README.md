# AdTrim

A Windows desktop editor for trimming commercials out of recorded TV - designed for the kind of MPEG-2 + AC3 broadcast captures that come out of Plex DVR, NextPVR, MythTV, and similar over-the-air recording stacks.

AdTrim is a *manual* editor: you mark the cut points, review transitions, and export. It does not auto-detect commercials. It's the tool you reach for when you want frame-accurate control over the output, or when an automatic tool got it 95% right and you want to clean up the last 5%.

> **Status:** v1.0 - usable but rough. Built primarily for one person's workflow (mine). Released publicly because others may find it useful.

> **Repository:** [github.com/adtrim/adtrim](https://github.com/adtrim/adtrim). File issues and pull requests there.

---

## What it does

- **Loads** `.mp4` recordings without re-muxing the source. (`.ts` support is planned, but not shipped yet.)
- **Imports chapters** from MP4 files that already have `Commercial X` / `Part X` chapter atoms (e.g. Plex DVR post-processed output) and uses them as starting cut points.
- **Separate recording windows** - use File > New window (Ctrl+N), or launch AdTrim again. Each window edits one recording. Opening a file from Explorer reuses an empty window or opens another; opening the same recording again activates its existing window.
- **Timeline editing** - drop split markers, drag them to refine, toggle segments as kept/excluded, undo/redo freely.
- **Chapter names** - right-click a segment and choose **Rename segment**. Names are saved with the project and become chapter names in the exported MP4. Renaming supports undo/redo.
- **Hardware export** - choose Automatic, a detected NVIDIA/Intel/AMD adapter, or Software. Opening the window only lists devices; there is no startup benchmark. Explicit GPU and Software choices export directly. Automatic uses valid cached results or evaluates options as the first export step. Evaluate options tests every option and shows results beside the export controls, including the fastest successful result, without changing your selection. Measured test times also appear in the encoder dropdown. A missing GPU or hardware encoder failure evaluates alternatives and asks which option to use to restart the entire export. Input, disk and permission errors do not trigger GPU evaluation. Results expire after seven days or when FFmpeg, adapters, or driver details change; unknown driver versions are not cached. Hardware exports can differ in size and quality. Decoding and deinterlacing use the CPU.
- **Collapse excluded scenes** - press **P** or use the visible toggle button to switch between editing the full recording and playing only the kept scenes. Click the eye icon at a join to play from two seconds before that cut and review the transition. Tab keeps its normal focus-navigation role.
- **Timeline navigation** - **Home** jumps to the start, **End** to the end, and **0-9** to percentage stops. These use the kept timeline when excluded scenes are collapsed.
- **Review workflow** - imported chapter titles remain visible. **Shift+C** confirms the current split and selects the next unconfirmed split; **N** moves to the next unconfirmed split without confirming.
- **Frame-accurate refinement** - for each split, runs a local ffprobe pass to find the precise frame boundary, snapping the cut to a real keyframe / scene change.
- **mpv-based preview** - fast scrubbing on MPEG-2 sources (50-150 ms per seek, vs. ~1 s with the LibVLC backend it replaced).
- **Export** - produces an MP4 with libx264-encoded video (deinterlaced via bwdif) and AC3 audio stream-copied from the source.
- **Source files are never modified.** All edits live in a `.adt.json` sidecar next to the recording. Delete the sidecar and you're back to the original.

Collapsed playback uses mpv's virtual timeline and bounded read-ahead, without rendering a preview file. It uses the existing hardware-decoding preference where supported. Transitions can still depend on the codec, disk, and decoder; this is a preview of the kept material, not a promise of gapless playback or an exact encoded-export preview. Exported audio still uses packet boundaries.

Moving or refining a split preserves the adjacent exclusion decisions. Splitting an excluded scene leaves both pieces excluded. Deleting a boundary between a kept and excluded scene keeps the merged scene; Undo restores the original decision.

Project saves run in the background. The status changes to **Saved** only after writing succeeds; opening another file, closing a project, and exiting flush pending edits. If the recording's folder cannot be written, saves use `%LOCALAPPDATA%\AdTrim\projects`. Reopening uses the newest valid matching save, including that fallback. Corrupt and newer-version projects are preserved. To reset a project completely, remove its fallback save as well as its adjacent sidecar.

Waveform/thumbnail visibility, output folder, window bounds, and recent playback positions are stored separately in `%LOCALAPPDATA%\AdTrim\preferences.json`. Viewing and playback changes do not create project edits. Waveform and thumbnail extraction remain opt-in on a fresh setup.

Exports are staged and validated before replacing an existing output. Replacement requires confirmation (or `--overwrite` in the export CLI). The source cannot be selected as the output, including through a Windows hard link.

Export temporary files are tracked and removed after success, failure, or cancellation, with three cleanup attempts. If removal fails, the export window shows a warning and keeps a recovery record. Five seconds after startup, background recovery checks up to 100 recorded exports, skipping active exports and deleting only recognized temporary files. Untracked leftovers from older versions are not removed automatically. Export checks free space in the temporary and output locations before encoding and every two seconds, stopping if available space falls below a 512 MiB reserve. The reserve is a safeguard, not a prediction of the final file size or a guarantee against other programs filling the disk between checks.

## What it doesn't do

- No automatic commercial detection. (Use a separate tool like [Comskip](https://www.kaashoek.com/comskip/) first if you want that, then import its output as a starting point - chapter import is the closest thing today.)
- No batch mode. One file at a time.
- No frame-accurate audio cuts. Audio is stream-copied (AC3), which is packet-accurate, so cut boundaries can slip by up to ~32 ms. This is fine for commercial-trimming; not appropriate for music-video editing.
- No direct `.ts`/`.mkv` editing yet.
- No support for non-Windows platforms. WPF + Windows-only video stack.

---

## Install

Download the latest installer from the [Releases](https://github.com/adtrim/adtrim/releases) page and run it.

The installer is unsigned (no code-signing certificate yet), so Windows SmartScreen will warn you the first time you run it. Click "More info" â†’ "Run anyway" to proceed. The bundled binaries (ffmpeg, ffprobe, libmpv) are pulled from upstream public builds - see [Bundled components](#bundled-components) below.

New installations default to `%LOCALAPPDATA%\Programs\AdTrim`. Updates retain the existing installation folder, including the old `%LOCALAPPDATA%\AdTrim` location, so existing shortcuts keep working. Close AdTrim and run the newer installer to update; no separate uninstall is needed. Settings, saved projects, and caches remain under `%LOCALAPPDATA%\AdTrim`. The uninstaller removes only installation-owned files and leaves user data in place. Upgrades do not run the legacy recursive uninstaller.

**System requirements:**
- Windows 10 21H2 or newer / Windows 11
- ~400 MB disk for the install (most of which is ffmpeg + libmpv)
- A recorded TV file to edit

---

## Build from source

### Prerequisites (one-time)

- **.NET 10 SDK** - `dotnet --version` should report `10.x`.
- **NSIS** (only needed to build the installer) - install from https://nsis.sourceforge.io/Download, or point `%ADTRIM_NSIS_DIR%` at the folder containing `makensis.exe`.
- **The bundled runtime binaries** (`ffmpeg.exe`, `ffprobe.exe`, `libmpv-2.dll`) - these are gitignored and not in the repo. Fetch them:

```pwsh
git clone https://github.com/adtrim/adtrim.git
cd adtrim
.\fetch-binaries.cmd
```

`fetch-binaries.cmd` downloads, checksum-verifies, and drops the binaries into `binaries/` (see [binaries/README.md](binaries/README.md) for what it pulls and the manual fallback).

### Run from source (dev)

```pwsh
dotnet build src/AdTrim/AdTrim.csproj
dotnet run --project src/AdTrim/AdTrim.csproj
```

### Build the installer EXE

From the repo root:

```pwsh
.\publish.cmd      # self-contained single-file build -> src\AdTrim\bin\Release\net10.0-windows\win-x64\publish\
.\installer.cmd    # wraps that in the NSIS installer
```

(or `.\publish.cmd && .\installer.cmd` to chain them). The result is:

```
.installers\AdTrim-Setup-v<version>.exe
```

`<version>` comes from `AppVersion.Numeric` in [src/AdTrim/AppVersion.cs](src/AdTrim/AppVersion.cs). **When cutting a new release, bump that constant first** - it drives the in-app version and the installer filename. Keep the version properties in [src/AdTrim/AdTrim.csproj](src/AdTrim/AdTrim.csproj) and the identity in [src/AdTrim/app.manifest](src/AdTrim/app.manifest) in sync. The installer folder is ignored by Git; upload installers as release assets, not source files.

### Troubleshooting

- **`makensis.exe not found`** - NSIS isn't installed or isn't on the auto-detected path. Install it, or set `%ADTRIM_NSIS_DIR%` to the folder with `makensis.exe`.
- **Gate fails: "non-release build" or "below 8.1.2"** - the bundled ffmpeg is stale, a nightly, or below the security floor. Run `.\fetch-binaries.cmd` to refresh it.
- **Gate fails: "last security-reviewed ... days ago"** - the version gate nags for a security re-review every ~90 days, so an old checkout *will* hit this. Re-check the advisories the message links, then set `ReviewedDate` in [check-ffmpeg-version.ps1](check-ffmpeg-version.ps1) to today (or run `.\fetch-binaries.cmd` if a fix is actually due).
- **`publish folder not found`** - run `.\publish.cmd` before `.\installer.cmd`.

### Validation

```pwsh
dotnet build AdTrim.sln -m:1
dotnet test AdTrim.sln --no-build
```

Some integration tests require local recording fixtures and report a skip when absent. See [Testing](docs/TESTING.md) for the application smoke check and fixture setup.

---

## Bundled components

The installer redistributes the following third-party binaries:

| Component | License | Source |
|---|---|---|
| FFmpeg (with libx264) | GPL v2 or later | [gyan.dev "full" Windows build](https://www.gyan.dev/ffmpeg/builds/) |
| libmpv | GPL v2 or later | [shinchiro Windows builds](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/) |

License notices for redistributed binaries are installed under the app's `licenses` folder.

Because both bundled binaries are GPL, the installer as a whole is GPL-licensed for redistribution purposes. The AdTrim source code itself is also GPLv3 (see below) - the bundled-deps choice and the source-license choice are aligned.

---

## License

AdTrim is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE) for the full text.

Copyright Â© 2026 Mark Hewitt.

In short: you can use, modify, and redistribute it, including for commercial purposes - but any distributed derivative must also be released under GPLv3 with source available. The original copyright notice must be preserved.

---

## Issues and contributions

Bug reports and pull requests are welcome at [github.com/adtrim/adtrim](https://github.com/adtrim/adtrim). This is a hobby project - response times will vary.
