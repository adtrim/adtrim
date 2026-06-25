using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>
/// Per-split refinement. Local window only ([t-2s, t+2s]). For each split:
///   1. Enumerate candidate frames via ffprobe -show_frames over the window.
///   2. Collect black / silence / scene-change signals via a single ffmpeg
///      invocation over the same window.
///   3. Score each candidate frame as
///        0.45 · black + 0.35 · silence + 0.20 · scene
///      (signals are 0..1; "in window" → 1.0 unless distance-decayed).
///   4. Pick the highest-scoring candidate; the refined split time is that
///      frame's pkt_pts_time (already frame-authoritative).
///   5. Clamp to neighbors + source bounds.
///   6. Confidence bucket from (top, margin to second-best).
///
/// IMPORTANT: the filter graph + scoring formula here are
/// plausible but unvalidated against the user's actual MPEG-2 / XDCAM EX
/// 1080i58 recordings. RefineCli runs this driver standalone for validation
/// before the editor UI calls it.
/// </summary>
public sealed class RefineService
{
    private readonly FfmpegRunner _runner;
    private const long WindowHalfUs = 2_000_000L;   // ±2s

    public RefineService(FfmpegRunner runner) => _runner = runner;

    /// <summary>
    /// Refine a single split. `neighbors` is `(prevUs, nextUs)` - refined time
    /// is clamped to `(prev+1us, next-1us)`. `duration` is `(0, durationUs)`.
    /// Returns null if the source has no candidate frames in the window.
    /// </summary>
    public async Task<RefineResult?> RefineOneAsync(
        string sourcePath,
        long originalTimeUs,
        long minBoundUs,
        long maxBoundUs,
        CancellationToken ct = default)
    {
        long winStartUs = Math.Max(0, originalTimeUs - WindowHalfUs);
        long winEndUs = originalTimeUs + WindowHalfUs;

        var candidates = await EnumerateCandidatesAsync(sourcePath, winStartUs, winEndUs, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;

        // ffmpeg with `-ss N -i FILE` (input seek, no -copyts) emits frames
        // whose pts_time is *seek-normalized*: source_pts - N. Frames before
        // the seek target are decoded (from the preceding keyframe) but not
        // emitted with negative timestamps. So filter pts_time + winStartUs =
        // source PTS, and ffprobe's `-read_intervals` already returns source
        // PTS - they align after adding winStartUs as the offset.
        //
        // Verified empirically on the BBT fixture (2026-05-16): `-ss 258 -t 4`
        // + scene-cut showinfo reports pts_time:3.953533, which corresponds
        // to source 261.953 - exactly the visible cut frame.
        var signals = await CollectSignalsAsync(sourcePath, winStartUs, winEndUs, winStartUs, ct).ConfigureAwait(false);

        // Score every candidate.
        //
        // The scene-cut score is the *strongest direct evidence* for a
        // boundary - it spikes at exactly the frame where the picture changes.
        // Black/silence proximity are corroborating signals (they often
        // surround a real cut but extend across many frames either side).
        // Original weights (0.45 black + 0.35 silence + 0.20 scene)
        // were tuned for files dominated by fade-to-black transitions; for
        // sharp scene cuts in fully-lit content (BBT credit-roll → ad), the
        // scene peak gets drowned out and the algorithm picks a mid-silence
        // frame instead of the actual cut. Reweighted so scene dominates.
        var scored = new List<(long timeUs, double score, double black, double silence, double scene)>(candidates.Count);
        foreach (var (tUs, _) in candidates)
        {
            var b = EdgeScore(tUs, signals.BlackWindows);
            var s = EdgeScore(tUs, signals.SilenceWindows);
            var sc = signals.SceneScoreAt(tUs);
            scored.Add((tUs, 0.70 * sc + 0.15 * b + 0.15 * s, b, s, sc));
        }

        // When multiple candidates tie at the top score (which is the normal
        // case for a multi-frame black/silence window - every frame inside
        // scores identically), we want the MEDIAN frame, not whichever sort
        // happened to put first. That centres the refined marker inside the
        // detected event instead of landing on the leading edge.
        scored.Sort((a, b) => b.score.CompareTo(a.score));
        var topScore = scored[0].score;
        var tied = scored.TakeWhile(s => s.score >= topScore - 1e-6).OrderBy(s => s.timeUs).ToList();
        var picked = tied[tied.Count / 2];

        var secondScore = scored.Skip(tied.Count).Select(s => s.score).DefaultIfEmpty(0.0).First();
        var margin = topScore - secondScore;

        // Unchanged: no candidate scored, marker stays put.
        // Threshold below the Low bucket so we don't move on noise (a single
        // black frame far from the marker isn't a real boundary).
        var confidence = ClassifyConfidence(topScore, margin, tiedCount: tied.Count);
        long refinedUs = confidence == Confidence.Unchanged
            ? originalTimeUs
            : FrameSnap.Clamp(picked.timeUs, minBoundUs + 1, maxBoundUs - 1);

        return new RefineResult(
            SplitId: "",   // caller maps back to the source split
            OriginalTimeUs: originalTimeUs,
            RefinedTimeUs: refinedUs,
            Confidence: confidence,
            TopScore: topScore,
            Margin: margin,
            TiedAtTop: tied.Count);
    }

    /// <summary>
    /// Confidence bucketing. The `tiedCount` parameter is the number of
    /// candidate frames that all share the top score - a *high* tied count
    /// indicates a multi-frame event (a real black/silence window), which
    /// promotes Medium to High even when margin is 0.
    /// </summary>
    private static Confidence ClassifyConfidence(double topScore, double margin, int tiedCount)
    {
        if (topScore < 0.15) return Confidence.Unchanged;
        if (topScore >= 0.75 && tiedCount >= 3) return Confidence.High; // strong multi-frame event
        if (topScore > 0.6 && margin > 0.2) return Confidence.High;
        if (topScore >= 0.4) return Confidence.Medium;
        return Confidence.Low;
    }

    /// <summary>
    /// Score for "frame is near the EDGE of a black/silence window".
    /// <para>The previous version returned 1.0 anywhere inside a window,
    /// which made every frame in (say) a 5-second credit roll tie at the
    /// top - and median-of-tied then picked the middle, missing the
    /// actual cut at the edge.</para>
    /// <para>This version peaks at the start/end edges and decays linearly
    /// toward the center over <paramref name="decayUs"/>. Frames at an
    /// edge score 1.0; frames more than decayUs from any edge score 0.0.
    /// 1 second is generous enough to cover GOP-distance keyframe shifts
    /// while still penalizing mid-window frames.</para>
    /// </summary>
    private static double EdgeScore(long tUs, IReadOnlyList<(long start, long end)> windows, long decayUs = 1_000_000)
    {
        double best = 0.0;
        foreach (var (s, e) in windows)
        {
            if (tUs < s || tUs > e) continue;
            var distFromStart = tUs - s;
            var distFromEnd = e - tUs;
            var minDist = Math.Min(distFromStart, distFromEnd);
            var score = Math.Max(0, 1.0 - minDist / (double)decayUs);
            if (score > best) best = score;
        }
        return best;
    }

    // -------------------------------------------------------------------
    // Step 1: enumerate candidate frames
    // -------------------------------------------------------------------
    private async Task<IReadOnlyList<(long timeUs, char pictType)>> EnumerateCandidatesAsync(
        string sourcePath, long winStartUs, long winEndUs, CancellationToken ct)
    {
        var startSec = (winStartUs / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var durSec = ((winEndUs - winStartUs) / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);

        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-read_intervals", $"{startSec}%+{durSec}",
            "-show_entries", "frame=pts_time,pict_type",
            "-of", "json",
            sourcePath,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success)
            throw new InvalidOperationException($"ffprobe show_frames failed (exit {r.ExitCode}): {r.Stderr}");

        return ParseCandidates(r.Stdout);
    }

    internal static IReadOnlyList<(long timeUs, char pictType)> ParseCandidates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("frames", out var frames)) return Array.Empty<(long, char)>();

        var list = new List<(long, char)>();
        foreach (var f in frames.EnumerateArray())
        {
            if (!f.TryGetProperty("pts_time", out var ptsTime)) continue;
            if (!double.TryParse(ptsTime.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                continue;
            char pt = '?';
            if (f.TryGetProperty("pict_type", out var pictType))
            {
                var s = pictType.GetString();
                if (!string.IsNullOrEmpty(s)) pt = s[0];
            }
            list.Add(((long)Math.Round(sec * 1_000_000.0), pt));
        }
        return list;
    }

    // -------------------------------------------------------------------
    // Step 2: collect signals via a single ffmpeg invocation
    // -------------------------------------------------------------------
    internal sealed record SignalSet(
        IReadOnlyList<(long start, long end)> BlackWindows,
        IReadOnlyList<(long start, long end)> SilenceWindows,
        IReadOnlyList<(long timeUs, double score)> SceneScores)
    {
        /// <summary>
        /// Nearest-neighbor scene score at time <paramref name="tUs"/> (clamped 0..1).
        /// Linear interpolation isn't worth it: scene_score samples land on
        /// real frame boundaries, and candidate frames are denser than the
        /// score reporting rate - every query already lands ≤1 frame from a
        /// sample, so interpolation would change the answer by less than
        /// frame-level quantisation already does.
        /// </summary>
        public double SceneScoreAt(long tUs)
        {
            double best = 0.0;
            long bestDelta = long.MaxValue;
            foreach (var (t, s) in SceneScores)
            {
                var d = Math.Abs(t - tUs);
                if (d < bestDelta) { bestDelta = d; best = s; }
            }
            return Math.Clamp(best, 0.0, 1.0);
        }
    }

    private async Task<SignalSet> CollectSignalsAsync(
        string sourcePath, long winStartUs, long winEndUs, long offsetUs, CancellationToken ct)
    {
        var startSec = (winStartUs / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var durSec = ((winEndUs - winStartUs) / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);

        // Newer FFmpeg (N-12xxxx+) rejects filtergraphs where every branch
        // ends in nullsink - the graph reports "zero outputs". Each terminal
        // must produce a labelled output that we then -map and discard via
        // `-f null -`. blackdetect / silencedetect / metadata=print all
        // write to stderr regardless of how the frames are routed.
        var args = new[]
        {
            "-v", "info",
            "-ss", startSec, "-t", durSec,
            "-i", sourcePath,
            "-filter_complex",
              "[0:v]split=2[v1][v2];"
            + "[v1]blackdetect=d=0.04:pix_th=0.10[vb];"
            + "[v2]select='gt(scene\\,0.0)',metadata=print[vs];"
            + "[0:a]silencedetect=n=-30dB:d=0.04[as]",
            "-map", "[vb]",
            "-map", "[vs]",
            "-map", "[as]",
            "-f", "null", "-",
        };
        var r = await _runner.RunFfmpegAsync(args, ct).ConfigureAwait(false);
        // FFmpeg exits 0 even when blackdetect/silencedetect find nothing.
        if (!r.Success)
            throw new InvalidOperationException($"ffmpeg signal pass failed (exit {r.ExitCode}): {r.Stderr}");

        return ParseSignals(r.Stderr, offsetUs);
    }

    private static readonly Regex BlackRx = new(
        @"black_start:(?<s>[\d.]+)\s+black_end:(?<e>[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceStartRx = new(
        @"silence_start:\s*(?<s>-?[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRx = new(
        @"silence_end:\s*(?<e>-?[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SceneRx = new(
        @"pts_time:(?<t>[\d.]+).*?lavfi\.scene_score=(?<v>[\d.]+)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Parses ffmpeg's signal-pass stderr. <paramref name="offsetUs"/> is the
    /// source-PTS at which ffmpeg's output clock = 0. With input-seek
    /// (`-ss N -i FILE`, no `-copyts`), filter pts_time is seek-normalized:
    /// adding N (= winStartUs) reconstructs source PTS, which aligns with
    /// ffprobe candidate timestamps.
    /// </summary>
    internal static SignalSet ParseSignals(string stderr, long offsetUs)
    {
        var black = new List<(long, long)>();
        foreach (Match m in BlackRx.Matches(stderr))
        {
            var s = (long)Math.Round(double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) * 1_000_000.0);
            var e = (long)Math.Round(double.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) * 1_000_000.0);
            black.Add((offsetUs + s, offsetUs + e));
        }

        // silencedetect emits start/end on separate lines, often interleaved per channel.
        // Pair them by order of appearance; tolerate an unmatched trailing start.
        var sStarts = new Queue<long>();
        var silence = new List<(long, long)>();
        foreach (Match m in Regex.Matches(stderr, @"silence_(?<k>start|end):\s*(?<v>-?[\d.]+)"))
        {
            var t = (long)Math.Round(double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) * 1_000_000.0);
            if (m.Groups["k"].Value == "start") sStarts.Enqueue(offsetUs + t);
            else if (sStarts.TryDequeue(out var openStart)) silence.Add((openStart, offsetUs + t));
        }

        var scenes = new List<(long, double)>();
        foreach (Match m in SceneRx.Matches(stderr))
        {
            var t = (long)Math.Round(double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture) * 1_000_000.0);
            var v = double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            scenes.Add((offsetUs + t, v));
        }
        return new SignalSet(black, silence, scenes);
    }
}
