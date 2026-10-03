# Native media source package

This package contains the source archives and build instructions for AdTrim's Windows x64 FFmpeg, ffprobe and libmpv binaries. The archives are separate from the installer so users do not need to download them to run AdTrim.

Use a new, empty build directory on Linux. The build was tested with Ubuntu 26.04, GCC MinGW-w64 13-posix and Meson 1.10.1. The toolchain package list records the exact installed versions. About 6 GB of free space is needed for the retained sources and intermediate files; allow extra space for packaging.

Install the prerequisites using your distribution's package manager:

```sh
sudo apt-get install --no-install-recommends build-essential mingw-w64 cmake ninja-build meson nasm pkg-config python3-jinja2 python3-packaging python3-setuptools git curl ca-certificates xz-utils autoconf automake libtool
```

Place `archives/`, `sources.lock.json`, `unpack.py` and `build.sh` together, then run:

```sh
bash build.sh
```

The script checks archive hashes before extraction. Dependencies are built from the retained archives; Meson dependency downloads are disabled. Completed steps are recorded under `stamps/` so an interrupted build can resume. Start in a fresh directory when changing sources or build options. Build logs are in `logs/`.

The stripped release outputs are `output/ffmpeg.exe`, `output/ffprobe.exe`, and `output/libmpv-2.dll`. The `src/`, `build/` and `prefix/` directories contain build intermediates and installed libraries. Do not distribute the entire prefix as the app payload.

The build retains software H.264, NVIDIA NVENC, Intel QSV and AMD AMF export support, Windows Direct3D/OpenGL video output, WASAPI audio, hardware decoding, and FFmpeg's built-in codecs and filters. AdTrim still controls which features it uses. DVD navigation, scripting engines and unrelated optional third-party codecs are not enabled.

The upstream source archives include their original licenses. Installer notices list the included components. Header libraries, shader generators and source inputs used to generate compiled code are included here too. Driver implementations and Windows system libraries are supplied by the operating system or hardware vendor and are not distributed in this package.

The source revisions and archive hashes are in `sources.lock.json`. Build instructions adapt upstream build options; they do not patch the upstream source code. The source package supports rebuilding the components, without claiming byte-for-byte identical binaries across different toolchain versions.
