# Native media binaries

AdTrim uses FFmpeg and ffprobe for analysis/export and libmpv for playback. Release binaries are built from the retained source inputs in [tools/native](../tools/native/README.md). They are not stored in Git.

## Obtain the binaries

For an exact release build, use the binaries from the matching AdTrim installer, or build the matching `AdTrim-Media-Sources-v<version>.tar.gz` archive attached to the [release](https://github.com/adtrim/adtrim/releases). The archive includes source snapshots, build instructions and an input hash manifest. GitHub's automatic AdTrim source archive does not include these native sources.

Place the three outputs here:

- `ffmpeg/win-x64/ffmpeg.exe`
- `ffmpeg/win-x64/ffprobe.exe`
- `mpv/win-x64/libmpv-2.dll`

The project copies them into build and publish output. The .NET runtime is bundled separately by the self-contained publish.

`fetch-binaries.cmd` remains a development utility for third-party builds. Its downloads are not the recorded release build and do not supply this release's matching source package. `publish.cmd` rejects binaries whose hashes differ from `native-build.json`.

## Current build

FFmpeg/ffprobe and libmpv's embedded FFmpeg use FFmpeg 9.0.2, revision `946fcce07b`. libmpv uses mpv revision `3186d369f9`. The build retains libx264, AMD AMF, NVIDIA NVENC and Intel QSV, Windows graphics/audio output, and the codecs and filters used by AdTrim. Other optional third-party features are not enabled.

The native input revisions and hashes are in `tools/native/sources.lock.json`. The output hashes and matching source archive are recorded in `native-build.json`. Required notices are in `installer/licenses/`, copied beside the application. FFmpeg and the combined libmpv build are distributed under GPLv3 with matching source access.

## Updating

1. Check [FFmpeg security advisories](https://ffmpeg.org/security.html) and [mpv advisories](https://github.com/mpv-player/mpv/security/advisories). Review both export and playback: updating the standalone executable alone does not update FFmpeg inside libmpv.
2. Retain the new source archives and any build inputs or patches. Update the source manifest and build instructions, then build in a fresh directory. Do not replace fixed revisions with floating branches.
3. Run application tests, synthetic playback/frame-position checks and hardware export checks. Compare performance with the previous binaries. Record unavailable hardware explicitly.
4. Prepare the matching source archive and notices. Update `native-build.json` with the resulting binary and source-archive hashes. A checksum alone does not establish corresponding-source completeness.
5. Update `check-ffmpeg-version.ps1` after an actual advisory review. Its current minimum is 9.0.2. Run the normal publish and installer commands; do not bypass either payload or version checks.
6. Publish the source archive and its checksum alongside the installer, maintaining the source link in the installed notices.

## Development override

`FfmpegRunner` first uses the bundled files under `AppContext.BaseDirectory/binaries/ffmpeg/win-x64/`, then checks `ADTRIM_FFMPEG_DIR`. libmpv loads from `binaries/mpv/win-x64/libmpv-2.dll` beside the application. No system FFmpeg installation is required.
