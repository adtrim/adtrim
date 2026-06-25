# AdTrim

A Windows desktop editor for trimming commercials out of recorded TV - designed for the kind of MPEG-2 + AC3 broadcast captures that come out of Plex DVR, NextPVR, MythTV, and similar over-the-air recording stacks.

AdTrim is a *manual* editor: you mark the cut points, audition them, and export. It does not auto-detect commercials. It's the tool you reach for when you want frame-accurate control over the output, or when an automatic tool got it 95% right and you want to clean up the last 5%.

> **Status:** v1.0 - usable but rough. Built primarily for one person's workflow (mine). Released publicly because others may find it useful.

> **Repository:** [github.com/adtrim/adtrim](https://github.com/adtrim/adtrim). File issues and pull requests there.

---

## What it does

- **Loads** `.mp4` recordings without re-muxing the source. (`.ts` support is planned, but not shipped yet.)
- **Imports chapters** from MP4 files that already have `Commercial X` / `Part X` chapter atoms (e.g. Plex DVR post-processed output) and uses them as starting cut points.
- **Timeline editing** - drop split markers, drag them to refine, toggle segments as kept/excluded, undo/redo freely.
- **Frame-accurate refinement** - for each split, runs a local ffprobe pass to find the precise frame boundary, snapping the cut to a real keyframe / scene change.
- **mpv-based preview** - fast scrubbing on MPEG-2 sources (50-150 ms per seek, vs. ~1 s with the LibVLC backend it replaced).
- **Export** - produces an MP4 with libx264-encoded video (deinterlaced via bwdif) and AC3 audio stream-copied from the source.
- **Source files are never modified.** All edits live in a `.adt.json` sidecar next to the recording. Delete the sidecar and you're back to the original.

## What it doesn't do

- No automatic commercial detection. (Use a separate tool like [Comskip](https://www.kaashoek.com/comskip/) first if you want that, then import its output as a starting point - chapter import is the closest thing today.)
- No batch mode. One file at a time.
- No frame-accurate audio cuts. Audio is stream-copied (AC3), which is packet-accurate, so cut boundaries can slip by up to ~32 ms. This is fine for commercial-trimming; not appropriate for music-video editing.
- No direct `.ts`/`.mkv` editing yet.
- No support for non-Windows platforms. WPF + Windows-only video stack.

---

## Install

Download the latest installer from the [Releases](https://github.com/adtrim/adtrim/releases) page and run it.

The installer is unsigned (no code-signing certificate yet), so Windows SmartScreen will warn you the first time you run it. Click "More info" → "Run anyway" to proceed. The bundled binaries (ffmpeg, ffprobe, libmpv) are pulled from upstream public builds - see [Bundled components](#bundled-components) below.

**System requirements:**
- Windows 10 21H2 or newer / Windows 11
- ~400 MB disk for the install (most of which is ffmpeg + libmpv)
- A recorded TV file to edit

---

## Build from source

You'll need the .NET 10 SDK and the two third-party Windows binaries the app depends on at runtime.

```pwsh
git clone https://github.com/adtrim/adtrim.git
cd adtrim
```

Drop the runtime binaries into the gitignored `binaries/` tree (see [binaries/README.md](binaries/README.md) for the full one-time setup):

- `binaries/ffmpeg/win-x64/ffmpeg.exe`
- `binaries/ffmpeg/win-x64/ffprobe.exe`
- `binaries/mpv/win-x64/libmpv-2.dll`

Then build and run:

```pwsh
dotnet build src/AdTrim/AdTrim.csproj
dotnet run --project src/AdTrim/AdTrim.csproj
```

To build the installer (NSIS-based), run `publish.cmd` followed by `installer.cmd` from the repo root. The output is `AdTrim-Setup.exe` next to the scripts.

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

Copyright © 2026 Mark Hewitt.

In short: you can use, modify, and redistribute it, including for commercial purposes - but any distributed derivative must also be released under GPLv3 with source available. The original copyright notice must be preserved.

---

## Issues and contributions

Bug reports and pull requests are welcome at [github.com/adtrim/adtrim](https://github.com/adtrim/adtrim). This is a hobby project - response times will vary.
