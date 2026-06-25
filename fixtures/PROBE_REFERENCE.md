# Probe reference - ground truth for known fixture files

Raw `ffprobe` output captured from the fixtures. Use these for integration-test assertions and for verifying `MediaProbeService` parses correctly. Do **not** re-probe in CI on every run - these results are pinned.

The files themselves are not in the repo (they're large and personal). Locations on the user's machine are listed below.

## File 1: Raw Plex DVR `.ts` recording

**Path**: `D:\Recorded TV\The Neighborhood (2018)\Season 08\The Neighborhood (2018) - S08E19 - Welcome to Kalamazoo.ts`

**Characteristics**:
- Container: `mpegts`
- Video: `mpeg2video` (Main profile), 1920×1080, frame rate `60000/1001` (≈ 59.94)
- Audio: two AC3 streams (5.1 surround at 6ch, stereo downmix at 2ch), 48 kHz
- Duration: 2096.961966 s (≈ 34min 57s)
- Chapters: **none**

Use case in tests: verifies the app correctly *rejects* `.ts` files in V1 (only MP4 is supported) with a clean error.

### Raw ffprobe output

```json
{
    "streams": [
        {
            "index": 0,
            "codec_name": "mpeg2video",
            "profile": "Main",
            "codec_type": "video",
            "width": 1920,
            "height": 1080,
            "r_frame_rate": "60000/1001"
        },
        {
            "index": 1,
            "codec_name": "ac3",
            "codec_type": "audio",
            "sample_rate": "48000",
            "channels": 6
        },
        {
            "index": 2,
            "codec_name": "ac3",
            "codec_type": "audio",
            "sample_rate": "48000",
            "channels": 2
        }
    ],
    "chapters": [],
    "format": {
        "format_name": "mpegts",
        "duration": "2096.961966"
    }
}
```

## File 2: Post-processed MP4 with ComSkip chapters (PRIMARY FIXTURE)

**Path**: `D:\Recorded TV\.test_autoconvert\The Rookie (2018) - S08E18 - The Bandit.mp4`

**Characteristics**:
- Container: `mov,mp4,m4a,3gp,3g2,mj2` (MP4)
- Video: `mpeg2video` (Main profile), 1920×1080, frame rate `30000/1001` (≈ 29.97)
- Audio: two AC3 streams (5.1 surround at 6ch, stereo at 2ch), 48 kHz
- Data: one `bin_data` stream (the MP4 chapter-text representation, not the
  source caption data - see "What these probes confirm" §4 below)
- Duration: 3596.528833 s (≈ 59min 56s - a typical 1-hour recording)
- **Video stream start_time: 1.771 s** - the video's first PTS sits at 1.771
  rather than 0 (a Plex DVR `.ts`-mid-GOP-capture artifact preserved through
  autoconvert). `MediaInfo.VideoStartTimeUs` carries this; `FrameSnap` uses
  it as a phase offset so its grid aligns with mpv's actual decoded frames.
- **13 chapters** with the user's labeling convention (`Commercial X` / `Part X`)

Use case in tests: **this is the primary integration fixture.** Verifies chapter import, sidecar persistence, refinement, and export end-to-end.

### Chapter structure (ground truth)

13 chapters span the full file. Imported as splits: drop the leading `0` boundary and the final `duration` boundary, leaving **12 split markers** at the internal boundaries below.

| # | Title         | Start (s)  | End (s)    | Is boundary an imported split? |
|---|---------------|------------|------------|--------------------------------|
| 0 | Commercial 1  |    0.000   |   28.260   | yes (the 28.260 boundary)      |
| 1 | Part 1        |   28.260   |  606.970   | yes                            |
| 2 | Commercial 2  |  606.970   |  818.350   | yes                            |
| 3 | Part 2        |  818.350   | 1043.580   | yes                            |
| 4 | Commercial 3  | 1043.580   | 1259.930   | yes                            |
| 5 | Part 3        | 1259.930   | 1715.750   | yes                            |
| 6 | Commercial 4  | 1715.750   | 1958.860   | yes                            |
| 7 | Part 4        | 1958.860   | 2222.990   | yes                            |
| 8 | Commercial 5  | 2222.990   | 2441.410   | yes                            |
| 9 | Part 5        | 2441.410   | 2790.190   | yes                            |
| 10| Commercial 6  | 2790.190   | 2973.400   | yes                            |
| 11| Part 6        | 2973.400   | 3553.550   | yes                            |
| 12| Commercial 7  | 3553.550   | 3594.660   | yes (the 3594.660 boundary)    |

The chapters end at 3594.660 s but the format duration is 3596.528 s - there is ~1.87 s of untagged content at the end of the file.

### Raw ffprobe output

```json
{
    "streams": [
        {
            "index": 0,
            "codec_name": "mpeg2video",
            "profile": "Main",
            "codec_type": "video",
            "width": 1920,
            "height": 1080,
            "r_frame_rate": "30000/1001"
        },
        {
            "index": 1,
            "codec_name": "ac3",
            "codec_type": "audio",
            "sample_rate": "48000",
            "channels": 6
        },
        {
            "index": 2,
            "codec_name": "ac3",
            "codec_type": "audio",
            "sample_rate": "48000",
            "channels": 2
        },
        {
            "index": 3,
            "codec_name": "bin_data",
            "codec_type": "data"
        }
    ],
    "chapters": [
        { "id": 0,  "time_base": "1/1000", "start_time": "0.000000",     "end_time": "28.260000",   "tags": { "title": "Commercial 1" } },
        { "id": 1,  "time_base": "1/1000", "start_time": "28.260000",    "end_time": "606.970000",  "tags": { "title": "Part 1" } },
        { "id": 2,  "time_base": "1/1000", "start_time": "606.970000",   "end_time": "818.350000",  "tags": { "title": "Commercial 2" } },
        { "id": 3,  "time_base": "1/1000", "start_time": "818.350000",   "end_time": "1043.580000", "tags": { "title": "Part 2" } },
        { "id": 4,  "time_base": "1/1000", "start_time": "1043.580000",  "end_time": "1259.930000", "tags": { "title": "Commercial 3" } },
        { "id": 5,  "time_base": "1/1000", "start_time": "1259.930000",  "end_time": "1715.750000", "tags": { "title": "Part 3" } },
        { "id": 6,  "time_base": "1/1000", "start_time": "1715.750000",  "end_time": "1958.860000", "tags": { "title": "Commercial 4" } },
        { "id": 7,  "time_base": "1/1000", "start_time": "1958.860000",  "end_time": "2222.990000", "tags": { "title": "Part 4" } },
        { "id": 8,  "time_base": "1/1000", "start_time": "2222.990000",  "end_time": "2441.410000", "tags": { "title": "Commercial 5" } },
        { "id": 9,  "time_base": "1/1000", "start_time": "2441.410000",  "end_time": "2790.190000", "tags": { "title": "Part 5" } },
        { "id": 10, "time_base": "1/1000", "start_time": "2790.190000",  "end_time": "2973.400000", "tags": { "title": "Commercial 6" } },
        { "id": 11, "time_base": "1/1000", "start_time": "2973.400000",  "end_time": "3553.550000", "tags": { "title": "Part 6" } },
        { "id": 12, "time_base": "1/1000", "start_time": "3553.550000",  "end_time": "3594.660000", "tags": { "title": "Commercial 7" } }
    ],
    "format": {
        "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
        "duration": "3596.528833"
    }
}
```

## File 3: Post-processed MP4 with INCOMPLETE ComSkip chapters (adversarial)

**Path**: `D:\Recorded TV\The Big Bang Theory (2007)\Season 03\The Big Bang Theory (2007) - S03E07 - The Guitarist Amplification.mp4`

**Characteristics**:
- Container: MP4
- Video: `mpeg2video`, 1280×720 (720p - not 1080p), frame rate `60000/1001`
  (≈ 59.94). `r_frame_rate` is canonical; `avg_frame_rate` reports a weird
  `1099413813/18341887` ratio from PTS-span computation - `MediaProbeService`
  prefers `r_frame_rate` for this reason.
- **Video stream start_time: 1.827 s** - same Plex DVR phase-offset pattern
  as the Rookie fixture, slightly different value.
- Audio: 5.1 AC3 (6ch) + mono AC3 (1ch) backup
- Data: one `bin_data` stream (MP4 chapter-text)
- Duration: 1796.576 s (≈ 29 min 57s - a typical 30-min recording)
- **5 chapters** but **incomplete** - ComSkip detected 4 chapter spans but missed the first ad break entirely

Use case in tests: **ComSkip-incompleteness adversarial fixture.** Verifies
that the RefineService scoring can pull a manually-placed marker onto a
real commercial boundary that ComSkip itself failed to flag.

### Chapter structure (as ComSkip wrote it - note the gap)

| # | Title         | Start (s) | End (s)   | Notes                       |
|---|---------------|-----------|-----------|-----------------------------|
| 0 | Part 1        |   0.000   | 682.780   | **Misses the first commercial break around 261.95s (4:21.95)** |
| 1 | Commercial 1  | 682.780   | 935.020   |                             |
| 2 | Part 2        | 935.020   | 1491.970  |                             |
| 3 | Commercial 2  | 1491.970  | 1794.690  |                             |

### Ground-truth missing boundary (verified by user + RefineService probe)

A real commercial break exists at approximately **261.954 s (4:21.95)**.
RefineService finds it with **Medium confidence** when seeded with a
manual split anywhere in `[258s, 264s]`. Signal makeup at that boundary:

- **No black fade** (this is why ComSkip-classic missed it - relies on blackdetect).
- **Silence window 259.795 → 262.092 (~2.3s)** in the 5.1 AC3 track.
- **Multiple scene-change peaks** in the same window (scores 0.73, 0.84).
- Combined refine score ≈ 0.52 → Medium per current scoring thresholds.

### RefineCli verification (2026-05-16, post-keyframe-offset fix)

Manual placements near the missing 4:21 boundary now all converge to
the same Medium-confidence position; the +1.0 s shift on the 04:20 seed
is clearly visible on the timeline:

| #  | Original (s) | Refined (s) | Δ ms     | Top  | Margin | Tied | Confidence |
|----|--------------|-------------|----------|------|--------|------|------------|
|  1 |  260.000     |  261.000    |  +1000.0 | 0.52 | 0.17   |  1   | **Medium** |
|  2 |  261.000     |  261.787    |   +786.7 | 0.52 | 0.16   |  1   | **Medium** |
|  3 |  262.000     |  261.787    |  −213.3  | 0.52 | 0.16   |  1   | **Medium** |
|  4 |  263.000     |  261.787    | −1213.3  | 0.52 | 0.16   |  1   | **Medium** |

All three manual seeds converge into the real silence window. Pass elapsed
0.8s - well under the 60s budget.

Source SHA-256 before+after: `5da06ff1003d40a4c0502496fbd51ec07557cb70e1baddabd3b746c8d819255a` - unchanged.

## File 4: Integration-test fixture (derived from File 2)

**Path**: `fixtures/rookie-30s.mp4` (repo-local, gitignored)

**How it's built**: `tools\make-test-fixture.cmd` slices 30 seconds from File 2
starting at 28:25 (1705s) and re-encodes to `mpeg2video + ac3` with chapters
injected from `tools\rookie-30s-chapters.ffmetadata`. Run the script once
after cloning to (re)generate. Integration tests skip cleanly if the file is
absent.

**Why this fixture exists**: the full Rookie export takes ~13 min and can't
serve as a fast regression net. This 30-sec slice exercises the same code
paths (chapter import, sidecar load-save, segment encode, concat, chapter
mux, two-stage seek, `bin_data` exclusion, deinterlace) in under 10 sec,
against real broadcast content rather than synthetic `testsrc` material.

**Characteristics**:
- Container: MP4
- Video: `mpeg2video` (Main), 1920×1080, frame rate `30000/1001` (≈ 29.97),
  900 frames over 30.030s
- Audio: single `ac3` 5.1 (6ch) at 48 kHz, 29.994s. (Source has a second
  stereo-downmix stream; the fixture drops it - probe-driven audio
  selection has its own unit tests.)
- Data: one `bin_data` stream (auto-injected by the MP4 muxer because
  chapters are present - this is the target of the `bin_data` exclusion
  integration test).
- Duration: 30.030s
- **Video start_time: 0.000** - re-encoding zeros the PTS. The source's
  `1.771` phase offset is *not* present here; the `VideoStartTimeUs`
  code path is not exercised by this fixture.
- **2 chapters** with deterministic boundaries (see below).

### Chapter structure (ground truth)

| # | Title         | Start (s)  | End (s)    | Notes                                |
|---|---------------|------------|------------|--------------------------------------|
| 0 | Part 3        |    0.000   |   10.750   | mapped from source 1705.000 → 1715.750 |
| 1 | Commercial 4  |   10.750   |   30.000   | mapped from source 1715.750 → 1735.000 |

Imported as splits: drop the leading `0` boundary and the final `duration`
boundary, leaving **1 split marker** at the internal boundary at 10.750s.

### Expected ffprobe output (assertion targets)

Tests should assert these properties - they're stable across regenerations
of the fixture from the same source:

```json
{
    "streams": [
        { "index": 0, "codec_name": "mpeg2video", "codec_type": "video",
          "width": 1920, "height": 1080, "r_frame_rate": "30000/1001",
          "nb_frames": "900", "duration": "30.030000" },
        { "index": 1, "codec_name": "ac3", "codec_type": "audio",
          "sample_rate": "48000", "channels": 6, "duration": "29.994000" },
        { "index": 2, "codec_name": "bin_data", "codec_type": "data" }
    ],
    "chapters": [
        { "id": 0, "time_base": "1/1000", "start_time": "0.000000",
          "end_time": "10.750000",  "tags": { "title": "Part 3" } },
        { "id": 1, "time_base": "1/1000", "start_time": "10.750000",
          "end_time": "30.000000",  "tags": { "title": "Commercial 4" } }
    ],
    "format": {
        "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
        "duration": "30.030000"
    }
}
```

## What these probes confirm

1. **MPEG-2 + AC3** is the codec reality - Chromium cannot decode either (drove the WPF + LibVLCSharp stack decision).
2. **Interlaced source** (`mpeg2video` from broadcast is interlaced; XDCAM EX 1080i58 confirmed from the mp4v sample description) - drove the `bwdif` deinterlace requirement on export.
3. **Two audio streams per file** (5.1 + stereo downmix) - drove the probe-driven audio stream selection (pick by channel count + default disposition, not hard-coded index).
4. **`bin_data` stream is present** in the post-processed MP4 - drove the explicit `-map 0:v -map 0:a` on export to drop caption data.
5. **Chapter labeling** uses `Commercial X` / `Part X` - drove the discussion about auto-excluding (user chose neutral import).

## Local FFmpeg availability

The user has FFmpeg installed at:
- `C:\Program Files\ffmpeg\bin\ffprobe.exe`
- `C:\tools\ffmpeg\bin\ffprobe.exe`

These are for **dev-time exploration only**. The app uses the bundled binaries under `binaries/ffmpeg/win-x64/`, resolved via `AppContext.BaseDirectory`. Do not assume FFmpeg is on `PATH` at runtime.
