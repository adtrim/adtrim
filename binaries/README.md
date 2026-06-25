# Bundled binaries

`AdTrim` resolves `ffmpeg.exe` and `ffprobe.exe` at runtime from
`AppContext.BaseDirectory/binaries/ffmpeg/win-x64/`. They are **not** committed
to the repo (large binaries, license boundary auditability).

## One-time install

1. Download the **gyan.dev** Windows build, "full" variant (must include
   `libx264`, which configures the FFmpeg bundle as a whole as GPL 2+):
   https://www.gyan.dev/ffmpeg/builds/  →  `ffmpeg-release-full.7z`

   **Minimum version: 8.1.2.** Earlier 8.1.x builds carry CVE-2026-8461 (an
   out-of-bounds write in the MagicYUV decoder, fixed in 8.1.2). AdTrim decodes
   user-supplied source files with auto-selected decoders, so the path is
   reachable. After copying, confirm with `ffmpeg.exe -version`.
2. Extract.
3. Copy these two files into this folder:
   - `binaries/ffmpeg/win-x64/ffmpeg.exe`
   - `binaries/ffmpeg/win-x64/ffprobe.exe`
4. Rebuild. The csproj `<Content Include="binaries\**\*.*">` rule copies
   them into the output directory.

## Updating after a security advisory (runbook)

Run this when a new FFmpeg/libmpv CVE lands **or** when `publish.cmd`'s version
gate fails on the review-expiry (it nags every `MaxAgeDays` in
`check-ffmpeg-version.ps1`).

1. **Check the advisories:**
   - FFmpeg - https://ffmpeg.org/security.html
   - libmpv - the shinchiro build's embedded FFmpeg (compare its build date).
2. **If a fix exists, replace the binaries:**
   - FFmpeg: download the new `ffmpeg-release-full.7z`, replace `ffmpeg.exe` +
     `ffprobe.exe` in `binaries/ffmpeg/win-x64/` (the single source for both
     dev and release builds).
   - libmpv: download a current shinchiro `mpv-dev-x86_64-*.7z`, replace
     `binaries/mpv/win-x64/libmpv-2.dll`.
3. **Update the pin in one place** - `check-ffmpeg-version.ps1`:
   `MinVersion` (if the floor moved) and `ReviewedDate` (always → today).
   Mirror the version number in step 1 above.
4. **If nothing needed updating** (already current): just bump `ReviewedDate`
   to today - that clears the gate and records that you checked.
5. **Cut the release:** bump `AppVersion.Numeric`, run `publish.cmd && installer.cmd`,
   publish the GitHub release with the new `AdTrim-Setup-v<version>.exe`.

## Dev-time fallback

`Services/FfmpegRunner` looks for binaries in this order:

1. `AppContext.BaseDirectory/binaries/ffmpeg/win-x64/` (bundled - production path)
2. `%ADTRIM_FFMPEG_DIR%` (env var - dev override)
3. Throws with a clear message.

The user's own dev-only install at `C:\Program Files\ffmpeg\bin\` can be
used by setting `ADTRIM_FFMPEG_DIR=C:\Program Files\ffmpeg\bin`.

## libmpv (video preview backend)

`AdTrim` uses **libmpv** for the video preview pane (the MPV-based
swap replaced LibVLC to bring seek latency from ~1 s to ~50-150 ms - the
MPV swap was a seek-latency win). At runtime it resolves the DLL from
`AppContext.BaseDirectory/binaries/mpv/win-x64/libmpv-2.dll`.

### One-time install

1. Download a Windows libmpv build. The most reliable source is the
   shinchiro builds (sourceforge):
   - https://sourceforge.net/projects/mpv-player-windows/files/libmpv/
   - Pick the latest `mpv-dev-x86_64-*.7z` (must be x86_64, not i686).
   **Security note:** `libmpv-2.dll` statically links its *own* copy of FFmpeg,
   independent of the bundled `ffmpeg.exe`. Bumping `ffmpeg.exe` does **not**
   patch libmpv. The playback path decodes whatever source you load, so for
   CVE-2026-8461 coverage pick a libmpv build whose embedded FFmpeg is 8.1.2 or
   newer (a current shinchiro build satisfies this; verify the build date /
   bundled FFmpeg version if in doubt).
2. Extract the archive.
3. Copy **`libmpv-2.dll`** into `binaries/mpv/win-x64/libmpv-2.dll`.
   (You can ignore the other files in the archive - headers, lib, etc.
   We only need the DLL at runtime.)
4. Rebuild - the csproj's `<Content Include="binaries\**\*.*">` rule
   copies the DLL into the output directory.

Typical DLL size: ~50 MB.

### Dev-time fallback

`Services/LibMpv.EnsureLoaded` looks for `libmpv-2.dll` in this order:

1. `AppContext.BaseDirectory/binaries/mpv/win-x64/` (bundled - production path)
2. `%ADTRIM_MPV_DIR%` (env var - dev override)
3. Throws with a clear error message.

`dev-launch.cmd` sets `ADTRIM_MPV_DIR` if you've installed libmpv
somewhere other than this folder.

### Why a separate download?

libmpv ships under GPLv2-or-later, like FFmpeg's `libx264`. Bundling it
in the repo would make the entire repo GPL-covered for redistribution
purposes. Keeping it as a one-time post-clone install preserves source
flexibility and is consistent with the FFmpeg approach above.

## License notices

When the installer runs, license notices are copied alongside the app from
`installer/licenses/`:

- `LICENSE.FFmpeg.txt` - FFmpeg LGPL/GPL
- `LICENSE.x264.txt` - x264 GPL 2+
- `LICENSE.libmpv.txt` - libmpv GPL 2+
