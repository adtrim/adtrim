# Testing AdTrim

From the repository root, with the .NET 10 SDK:

```powershell
dotnet restore AdTrim.sln
dotnet build AdTrim.sln --no-restore -m:1 -c Release
dotnet test AdTrim.sln --no-build -c Release
```

Tests report skips when recording fixtures are unavailable. A run with skipped
tests does not establish full integration coverage. Fixture descriptions and
expected probe data are in [PROBE_REFERENCE.md](../fixtures/PROBE_REFERENCE.md).

| Environment variable | Fixture |
| --- | --- |
| `ADTRIM_FIXTURE_MP4` | Primary full-length chaptered MP4 |
| `ADTRIM_FIXTURE_BBT_MP4` | Boundary-refinement and audio-alignment fixture |
| `ADTRIM_ROOKIE_30S_FIXTURE` | Short export fixture; alternatively `fixtures/rookie-30s.mp4` |

Media is not included in the source repository. Supply disposable copies of
matching fixtures; arbitrary videos do not satisfy the pinned assertions.

With the bundled FFmpeg and libmpv components present, these tools exercise
synthetic media without needing private recordings:

```powershell
dotnet run --project tools/AdTrim.Smoke -c Release
python tools/playback_probe.py
python tools/benchmark-editing.py
```

The smoke tool generates a disposable recording and isolated preferences beneath
its build directory. It checks editing, saves, reopening, collapsed playback, and
window layout. Its optional file argument is for matching disposable test fixtures
only: the tool edits their sidecars. Do not pass an original recording.

The native playback probe creates and removes synthetic media and checks kept
audio, timing, and repeated-seek memory. It uses null output devices, so it does
not measure physical display/audio behavior or prove hardware decoding was used.
The editing benchmark compares the working tree with HEAD and excludes rendering.

Hardware export checks use generated progressive and 1080i video:

```powershell
$env:ADTRIM_TEST_HARDWARE = '1'
dotnet run --project tools/AdTrim.Smoke -c Release
Remove-Item Env:ADTRIM_TEST_HARDWARE
```

This checks available adapters, cached detection, the export selector, preference
reload, real exports, frame counts, progressive output, audio start alignment,
chapter names, cancellation after encoding begins, and source hashes. A simulated
device loss after the first segment verifies a full software restart and explicit
hardware failure handling. Missing-input failures must not trigger a retry.
Unsupported vendors are reported as untested. Software-only CI does not validate
hardware drivers.

The dialog checks also verify that listing options and explicitly selected GPU
exports work without creating an evaluation cache. A missing saved GPU must remain
selected until export, then show evaluated alternatives and restart with the user's
choice. Nearby split review buttons retain separate targets and hover highlights.

The tool writes `hardware-results.json` and `hardware-dialog.png` beneath its
isolated smoke directory. Results include elapsed export time, output size, SSIM
against software output, and maximum dispatcher sampling delay. These short
synthetic measurements are not guarantees for real recordings or proof of
visually identical quality. Hardware quality settings are not equivalent to CRF.

Capability checks encode and decode a 12-second generated 1080p sample per adapter
and compare with software. Automatic selects the shortest successful check; actual
speed depends on the footage and segment length. Cache entries include adapter
identity/order, driver version, FFmpeg file identity, and probe version, expire
after seven days, and can be refreshed with Evaluate options. Unknown driver
versions disable persistent caching. All production paths keep CPU decoding and
bwdif deinterlacing until accelerated alternatives pass separate validation.

Before a release, manually check actual video/audio transitions, long-recording
responsiveness, mixed-DPI window restoration, and upgrades/uninstall with disposable
settings and projects. The installer archive passing an integrity check is not
equivalent to testing an installed upgrade.
