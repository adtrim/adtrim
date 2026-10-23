# TS workflow verification

Run `dotnet restore AdTrim.sln`, then `powershell -File tools/verify-ts.ps1` on
Windows with the bundled media tools present. The command generates disposable
recordings and uses isolated application settings. It does not use personal
recordings or change the installed application.

This check runs every solution test except the separately configured
`FixtureIntegrationTests`, followed by the TS application smoke checks. It fails
if any selected test is skipped. The normal source-only CI job
does not contain the bundled native binaries, so its generated-media test skips
do not establish that TS support works. Run this local check before packaging a
build that changes media handling. It complements the full solution tests,
application smoke checks, playback probe and editing benchmark.

The generated-media checks cover:

- MP4 and TS inputs and outputs, AAC and AC3, MPEG-2 and H.264, and interlacing.
- Recording-relative waveform, picture-analysis, scene and fade timings despite
  nonzero transport timestamps. Cached waveform and suggestion versions change
  when their timestamp interpretation changes.
- Both preview panes after timeline clicks, exact seeks, frame steps, rapid
  seeks, and pause/resume, including two- and three-second MPEG-2 GOPs. The
  three-second fixture checks predecessors against independently decoded frame
  timestamps rather than assuming the advertised frame rate.
- Collapsed seeking and playback across an excluded scene, then return to the
  full editor. Mid-GOP scene starts, repeated seeks, seeks during timeline load,
  and native-player versus editor-clock agreement have explicit assertions.
  Filenames include spaces, an apostrophe and Unicode.
- Application handoff acknowledgement, saved cuts and exclusions after reopening,
  sticky export formats, remembered sidecar deletion, chapter notices and
  source-dependent export tracking. Sidecar preference checks cover on/off saves,
  another recording window, a fresh process and unrelated stale-window saves.
  The TS chapter notice stays fully visible on initial setup at minimum window
  size, with export actions outside the scrolling settings. Large-window actions
  follow the form. The format, destination, acceleration and post-export cards
  stay in that order in one column. Captures cover MP4, TS and long episode filenames.
- Every short-cut join's picture position and final audio/video packet alignment.
- Opening audio before the first video frame, long-GOP transport cuts and copied
  audio packet timestamps within every kept part. Export boundary regressions
  use known picture levels and unique audio packet payloads, not export success
  alone. TS assembly preserves individual audio packet timestamps across joins.
  Concat durations follow requested cuts with compensation for intermediate
  start times. Negative B-frame decode timestamps are retained in MP4 parts;
  forcing them to zero must not shift copied audio from one part to the next.
- Requested duration compared with each intermediate and the final output, so
  shortened intermediates cannot validate one another. Damaged source audio must
  export to both formats with copied packet payloads unchanged. Inherited decoder
  errors do not block publication.
- Cancellation during encoding, assembly and validation; atomic overwrite; source
  protection; cleanup ownership and abandoned MP4/TS staging recovery.
- Unsupported audio, ambiguous programs, unreadable input and opening readable
  recordings with timestamp resets.
- A completed export whose project tracking cannot be saved, preserving both the
  output and the previous sidecar while showing a warning.

Successful generated tests do not establish compatibility with every broadcast
stream. Full-recording fixtures, physically gapless playback, unavailable GPU
vendors and Explorer installation/upgrade/uninstall still need separate checks.
The application handoff check does not install or exercise Explorer's shell UI.

## Coverage limits

Passing tests verify their stated observations, not every behavior of a system:

- Generated broadcasts cover selected codec and timestamp patterns. They do not
  represent every broadcaster, damaged stream, timestamp discontinuity, GOP
  length, or changing track layout.
- Commercial-detection examples verify known designed boundaries. Evidence
  collection and valid proposed timestamps do not measure false positives or
  missed breaks across real programs; that requires reviewed reference markings.
- Hardware argument tests verify configuration. Only successful native exports
  on an available adapter exercise that vendor's driver.
- Save and publication tests inject failures at known boundaries. They do not
  simulate arbitrary power loss, filesystem corruption or disconnected storage.
- Short repeated-join tests check cut signals and audio packets. They do not
  establish full-length export reliability, all-frame picture quality or
  physically gapless playback on actual output devices.
- UI smoke checks cover selected dimensions and transitions. Mixed-DPI desktop
  behavior, accessibility, Explorer shell registration and installer lifecycle
  require separate checks.
