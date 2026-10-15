# Marker sources

Use **Markers** above the timeline, or **Tools > Ad break detection**:

- **Revert to original imported markers** restores the recording's chapter markers and names. Imported chapter names never automatically exclude video.
- **AdTrim suggested markers > Analyze ad breaks** analyzes the recording. Once it has been analyzed, the item reads **Analyze ad breaks again** and runs a fresh analysis. Suggestions are experimental and remain included. Review them before excluding anything.
- **AdTrim suggested markers > Revert to original AdTrim suggested markers** reloads the cached analysis. It is available once the recording has been analyzed.
- **Delete all markings** removes internal splits, names, and exclusions. All video is included. It is also in the **Edit** menu.

Switching sources asks before discarding edits. Undo restores the previous edit during the current session. There is one working timeline; discarded edits are not saved as alternate timelines.

**Tools > Ad break detection > Starting markers for new files** chooses imported markers, AdTrim suggested markers, or none (the whole recording, with no markers). Imported markers are the default. A saved project always takes precedence, including a deliberately cleared timeline. Cancel analysis with **Cancel analysis** or Escape. Cancellation and analysis failure keep the current edit.

## Detection limits

The detector uses black transitions, audio silence, repeated short units, and nearby editing rates. These patterns also occur within programs. The detector has not demonstrated replacement-level accuracy and does not automatically exclude its proposals. It can miss entire commercial breaks, including breaks without the expected transition patterns.

Proposed timestamps are checked against actual nearby frame timestamps. That establishes that a frame exists, not that it is the correct program/commercial boundary. It does not recognize title cards, logos, credits, neighboring shows, or sketch names. Generic split refinement remains a separate heuristic; it does not identify the last frame containing a network logo.

The project saves only the current edit and its starting-source metadata. Original suggestions use a bounded cache separate from the project. Detector updates invalidate cached results. Existing older projects are backed up before a schema upgrade; downgrading the app requires restoring the older project backup as well as the older executable.

## Read-only analysis report

With bundled FFmpeg available through `ADTRIM_FFMPEG_DIR`, run:

```powershell
dotnet run --project src/AdTrim.RefineCli -- --detect recording.mp4
```

This prints proposal evidence, integer-microsecond timestamps, detector version, elapsed time, and source hash verification. It does not write project sidecars. Reported process memory covers the report process, not its FFmpeg children. Synthetic detector tests run the media pipeline with and without audio and with nonzero timestamps; they do not establish accuracy on broadcasts.

## Suggestion engine

**Analyze ad breaks** in the editor and `--suggest` (in place of `--detect`) run
the same analysis. It learns a recurring corner graphic from the recording and
considers direct scene cuts alongside black transitions. Interlaced scene events
are mapped to actual source frames: a mixed advertisement field is excluded on
entry and skipped on return. Analysis reads the primary audio once, in a pass the
editor shares with the timeline waveform, then streams reduced frames in two
picture passes. It retains bounded evidence, supports cancellation, and in the
editor pauses its reads while you scrub. For primary 5.1 audio, centre-channel
dominance supports picture boundaries when the station graphic appears late.
Stereo or unknown layouts use picture evidence alone. Centre-channel dominance is
a mixing pattern, not proof of program identity. When no stable graphic can be
learned, the recording's own appearance and repeating splice silences are used
instead, and those breaks are reported with low confidence.

A long graphic disappearance alone no longer produces a proposed break. Direct
cuts need corroborating return evidence plus repeated short units, or paired
audio changes at black picture boundaries. The audio cue only votes when the
recording supplies contrasting mixes. Contradictory audio and ambiguous entries
are reported separately as uncertain candidates; the editor shows them as red
markers for review. All proposals still require review and remain included.
Stricter checks can miss real breaks; abstention is not proof that a recording
has no commercials.

`--suggest-evidence` includes bounded measurements in the read-only report.
`--replay-suggestions` evaluates that report without reading video or mapping
source frames. `--suggest-from-evidence` takes the source and its evidence report,
checks source identity with a hash, and maps accepted boundaries to source frames.
These analysis tools do not write project sidecars.

This engine produces review suggestions only. It can mistake logo-free program
material for advertising, miss fades, and place boundaries incorrectly. Repeated
audio recognition and neighboring-show recognition are not implemented. The editor's
**Analyze ad breaks** uses this engine; `--suggest` runs the same analysis headless and
does not change saved projects, editor marker sources, or exports.
