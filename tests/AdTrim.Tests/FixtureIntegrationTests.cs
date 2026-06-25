using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

/// <summary>
/// Integration tests against real recordings. Opt in by setting
/// `ADTRIM_FIXTURE_MP4` to the absolute path of the documented
/// primary fixture (see fixtures/PROBE_REFERENCE.md):
///
///   D:\Recorded TV\.test_autoconvert\The Rookie (2018) - S08E18 - The Bandit.mp4
///
/// Without the env var these tests are silently skipped, so CI / a fresh
/// clone won't fail. With the env var, they hit a real FFmpeg via
/// `ADTRIM_FFMPEG_DIR` (also env-controlled).
///
/// All operations are read-only; the test hashes the source before AND
/// after each test and asserts equality to prove non-modification.
/// </summary>
public class FixtureIntegrationTests
{
    private static string? Fixture => Environment.GetEnvironmentVariable("ADTRIM_FIXTURE_MP4");
    private static string? BbtFixture => Environment.GetEnvironmentVariable("ADTRIM_FIXTURE_BBT_MP4");

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(fs);
        return Convert.ToHexString(bytes);
    }

    [SkippableFact]
    public async Task RookieFixture_ProbeAndChapters_MatchProbeReference()
    {
        Skip.If(Fixture is null || !File.Exists(Fixture),
            "ADTRIM_FIXTURE_MP4 not set or fixture missing.");

        var hashBefore = Sha256(Fixture);
        var runner = new FfmpegRunner();
        var probe = new MediaProbeService(runner);
        var chapterSvc = new ChapterImportService(runner);

        var media = await probe.ProbeAsync(Fixture);

        // Per fixtures/PROBE_REFERENCE.md "File 2".
        media.DurationUs.Should().BeInRange(3_596_000_000L, 3_597_000_000L,
            because: "documented duration is 3596.528833s ± frame granularity");
        media.VideoCodec.Should().Be("mpeg2video");
        media.Width.Should().Be(1920);
        media.Height.Should().Be(1080);
        media.FrameRate.Numerator.Should().Be(30000);
        media.FrameRate.Denominator.Should().Be(1001);
        // Rookie's video stream is offset by ~1.771 s relative to the audio
        // (mid-GOP start preserved from the Plex .ts capture). This is the
        // FrameSnap phase - without it, the snap grid is offset from mpv's
        // real frames and markers land in the gaps.
        media.VideoStartTimeUs.Should().Be(1_771_000);
        media.AudioStreams.Should().HaveCount(2, because: "5.1 AC3 + stereo downmix");
        // Probe-driven primary picks the 6-channel stream regardless of index.
        media.PrimaryAudio!.Channels.Should().Be(6);

        // Chapter import: 13 chapters in the source, dropping the 0 and
        // duration bookends leaves 12 internal splits at the documented
        // boundaries (PROBE_REFERENCE.md table).
        var boundaries = await chapterSvc.ImportAsync(Fixture, media.DurationUs);
        var expected = new[]
        {
              28_260_000L,  606_970_000L,  818_350_000L, 1_043_580_000L,
            1_259_930_000L, 1_715_750_000L, 1_958_860_000L, 2_222_990_000L,
            2_441_410_000L, 2_790_190_000L, 2_973_400_000L, 3_553_550_000L,
        };
        boundaries.Should().HaveCount(12);
        boundaries.Select(b => b.TimeUs).Should().BeEquivalentTo(expected);

        Sha256(Fixture).Should().Be(hashBefore, because: "source must not be modified");
    }

    [SkippableFact]
    public async Task RookieFixture_RefinePass_RespectsBoundsAndNeighbors()
    {
        Skip.If(Fixture is null || !File.Exists(Fixture),
            "ADTRIM_FIXTURE_MP4 not set or fixture missing.");

        var hashBefore = Sha256(Fixture);
        var runner = new FfmpegRunner();
        var probe = new MediaProbeService(runner);
        var chapterSvc = new ChapterImportService(runner);
        var refine = new RefineService(runner);

        var media = await probe.ProbeAsync(Fixture);
        var chapters = (await chapterSvc.ImportAsync(Fixture, media.DurationUs))
            .Select(c => c.TimeUs).OrderBy(t => t).ToList();

        long prevBound = 0;
        var refinedTimes = new List<long>();
        for (int i = 0; i < chapters.Count; i++)
        {
            var orig = chapters[i];
            var nextBound = i + 1 < chapters.Count ? chapters[i + 1] : media.DurationUs;
            var r = await refine.RefineOneAsync(Fixture, orig, prevBound, nextBound);
            r.Should().NotBeNull("every internal boundary has frames in its ±2s window");
            refinedTimes.Add(r!.RefinedTimeUs);
            prevBound = Math.Max(prevBound, r.RefinedTimeUs);
        }

        // Hard invariants: monotonic + within bounds.
        refinedTimes.Should().BeInAscendingOrder();
        refinedTimes.Should().AllSatisfy(t =>
        {
            t.Should().BeGreaterThan(0);
            t.Should().BeLessThan(media.DurationUs);
        });

        Sha256(Fixture).Should().Be(hashBefore);
    }

    /// <summary>
    /// Regression test for the ComSkip-incompleteness adversarial fixture
    /// (PROBE_REFERENCE.md "File 3"). The BBT recording's first ad break is
    /// missed by ComSkip-classic (no black fade). The user expects the visible
    /// cut at ~261.95s. RefineService must converge to that frame when seeded
    /// from neighbors of the true boundary.
    ///
    /// Why this matters: an earlier offset bug (treating filter pts_time as
    /// keyframe-relative when it is actually seek-normalized to the `-ss`
    /// target) shifted refined times by the keyframe-arrival delta - ~150 ms
    /// for this fixture - and landed inside the silence window at 04:21.000
    /// instead of the user's expected 04:21.953. This test pins the fix.
    /// </summary>
    [SkippableFact]
    public async Task BbtFixture_FirstAdBreak_RefinesToVisibleCutFrame()
    {
        Skip.If(BbtFixture is null || !File.Exists(BbtFixture),
            "ADTRIM_FIXTURE_BBT_MP4 not set or fixture missing.");

        var hashBefore = Sha256(BbtFixture);
        var runner = new FfmpegRunner();
        var refine = new RefineService(runner);

        // Visible first-ad frame is at ~04:21.953 (verified by the user on
        // mpv playback). Allow 50 ms tolerance (~3 frames at 59.94 fps) to
        // accommodate frame-grid quantisation across seeds.
        const long Expected = 261_953_000L;
        const long Tolerance = 50_000L;

        // Seeds 261-263 all bracket the true boundary inside their ±2s
        // window; all three must converge to the visible cut.
        long[] seeds = { 261_000_000L, 262_000_000L, 263_000_000L };
        foreach (var seed in seeds)
        {
            var r = await refine.RefineOneAsync(BbtFixture, seed, 0, 1_796_000_000L);
            r.Should().NotBeNull($"seed {seed / 1_000_000.0:0.000}s has candidate frames in its ±2s window");
            Math.Abs(r!.RefinedTimeUs - Expected).Should().BeLessThan(Tolerance,
                because: $"seed {seed / 1_000_000.0:0.000}s should converge to the visible cut at "
                       + $"{Expected / 1_000_000.0:0.000}s, got {r.RefinedTimeUs / 1_000_000.0:0.000}s");
        }

        Sha256(BbtFixture).Should().Be(hashBefore, because: "source must not be modified");
    }

    /// <summary>
    /// Regression test for the audio-leakage bug fixed 2026-05-16. The export
    /// strategy must produce a segment whose video and audio streams start at
    /// (approximately) the same output time.
    ///
    /// Before the fix, `-ss &lt;start&gt; -i source -c:a copy` did an input-seek
    /// that landed on the preceding video keyframe and wrote every audio
    /// packet from there forward - leaking up to one GOP (~917ms on this
    /// MPEG-2 fixture) of pre-cut audio into the output. The user perceived
    /// this as "a strum from the previous commercial" at the start of an
    /// exported segment that should have begun cleanly at a silent split.
    ///
    /// The fix is the two-stage seek in LibX264EncoderStrategy: coarse
    /// pre-roll seek before `-i`, fine accurate seek after. With it, both
    /// stream start_times land within the true AC3 packet-alignment slip
    /// (~32ms). We allow 100ms to absorb encoder priming + container
    /// rounding without masking a real regression of the underlying bug.
    /// </summary>
    [SkippableFact]
    public async Task BbtFixture_ExportSegment_AudioAndVideoStartAligned()
    {
        Skip.If(BbtFixture is null || !File.Exists(BbtFixture),
            "ADTRIM_FIXTURE_BBT_MP4 not set or fixture missing.");

        var hashBefore = Sha256(BbtFixture);
        var runner = new FfmpegRunner();
        var probe = new MediaProbeService(runner);
        var encoder = new LibX264EncoderStrategy();

        // 8.149s is the split where the original bug surfaced. The 10s
        // duration is arbitrary - long enough to encode through several GOPs.
        const long StartUs = 8_149_000L;
        const long EndUs   = StartUs + 10_000_000L;

        var media = await probe.ProbeAsync(BbtFixture);
        media.PrimaryAudio.Should().NotBeNull();

        var outDir = Path.Combine(Path.GetTempPath(), "AdTrim", "test-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "segment.mp4");

        try
        {
            var segment = new ExportSegment(0, StartUs, EndUs, "Test");
            var args = encoder.BuildSegmentArgs(BbtFixture, segment, media.PrimaryAudio!.Index, outPath);
            var encR = await runner.RunFfmpegAsync(args);
            encR.Success.Should().BeTrue(because: $"encode failed: {encR.Stderr}");
            File.Exists(outPath).Should().BeTrue();

            // Probe the output's per-stream start_time directly. The
            // single-stage-seek bug manifested as video.start_time ≈ 0.917
            // with audio.start_time = 0.000 on this fixture; the two-stage
            // fix collapses the delta to ~11ms.
            var probeR = await runner.RunFfprobeAsync(new[]
            {
                "-v", "error",
                "-print_format", "json",
                "-show_streams",
                outPath,
            });
            probeR.Success.Should().BeTrue();

            using var doc = JsonDocument.Parse(probeR.Stdout);
            double? videoStart = null, audioStart = null;
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                if (!s.TryGetProperty("start_time", out var stProp)) continue;
                if (!double.TryParse(stProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) continue;
                if (type == "video") videoStart = sec;
                else if (type == "audio") audioStart = sec;
            }

            videoStart.Should().NotBeNull("output must have a video stream");
            audioStart.Should().NotBeNull("output must have an audio stream");

            var deltaMs = Math.Abs(videoStart!.Value - audioStart!.Value) * 1000.0;
            deltaMs.Should().BeLessThan(100,
                because: $"audio and video must start within 100ms of each other (was {deltaMs:0.0}ms). "
                       + "A delta in the hundreds of ms indicates the single-stage `-ss before -i` audio leak has regressed.");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }

        Sha256(BbtFixture).Should().Be(hashBefore, because: "source must not be modified");
    }
}
