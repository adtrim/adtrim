AdTrim third-party license notices
==================================

The installer copies this folder next to AdTrim.exe so binary redistribution
has a stable place for dependency license notices.

Included components:

- FFmpeg and ffprobe 9.0.2, AdTrim source build (GPLv3).
  FFmpeg revision: 946fcce07b. License: LICENSE.FFmpeg.txt.
- libmpv, mpv revision 3186d369f9, linked with FFmpeg 9.0.2 (GPLv3 combined build).
  Upstream mpv license: LICENSE.libmpv.txt; GPLv3: LICENSE.FFmpeg.txt.
- Native dependencies, headers and shader build inputs:
  See native/COMPONENTS.txt and the accompanying per-component notices.
  Matching source archive: AdTrim-Media-Sources-v1.1.0.tar.gz
  Download: https://github.com/adtrim/adtrim/releases/tag/v1.1.0
  The archive includes source snapshots, hashes and build instructions.
- Microsoft .NET 10.0.12.
  License: LICENSE.dotnet.txt; dependencies: ThirdPartyNotices.dotnet.txt.
  Source: https://github.com/dotnet/dotnet
- CommunityToolkit.Mvvm 8.4.2 (MIT).
  License: LICENSE.CommunityToolkit.Mvvm.txt.
  Source: https://github.com/CommunityToolkit/dotnet/tree/v8.4.2

Source links identify upstream projects. They do not replace the distributor's
obligation to provide complete corresponding source, including linked libraries
and build scripts, for GPL-covered binaries.

AdTrim itself is licensed under GPLv3. See the repository LICENSE file.
