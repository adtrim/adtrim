using System.Globalization;
using System.IO;
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
///   5. Choose only candidates strictly inside neighboring boundaries.
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
    /// Refine a single split using actual frames strictly inside the supplied
    /// neighboring boundaries, including source bookends.
    /// Returns null if the source has no candidate frames in the window.
    /// </summary>
    public async Task<RefineResult?> RefineOneAsync(
        string sourcePath,
        long originalTimeUs,
        long minBoundUs,
        long maxBoundUs,
        CancellationToken ct = default, int? primaryAudioIndex = null)
    {
        long winStartUs = Math.Max(0, originalTimeUs - WindowHalfUs);
        long winEndUs = originalTimeUs + WindowHalfUs;

        var candidates = await EnumerateCandidatesAsync(sourcePath, winStartUs, winEndUs, ct).ConfigureAwait(false);
        candidates = candidates.Where(f => f.timeUs > minBoundUs && f.timeUs < maxBoundUs
            && f.timeUs >= winStartUs && f.timeUs <= winEndUs).ToArray();
        if (candidates.Count == 0) return null;
        primaryAudioIndex ??= (await new MediaProbeService(_runner).ProbeAsync(sourcePath, ct).ConfigureAwait(false)).PrimaryAudioIndex;

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
        var signals = await CollectSignalsAsync(sourcePath, winStartUs, winEndUs, winStartUs, primaryAudioIndex.Value, ct).ConfigureAwait(false);

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
            : picked.timeUs;

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
        // ffprobe returns transport PTS; the editor and mpv use a timeline relative to the TS start.
        long originUs = Path.GetExtension(sourcePath).Equals(".ts", StringComparison.OrdinalIgnoreCase)
            ? (await new MediaProbeService(_runner).ProbeAsync(sourcePath, ct).ConfigureAwait(false)).TimelineStartTimeUs : 0;
        // Do not seek at the beginning: TS keyframe seeking can skip the opening GOP.
        var startSec = winStartUs == 0 ? "" : ((winStartUs + originUs) / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);

        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-read_intervals", $"{startSec}%{((winEndUs + originUs) / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture)}",
            "-show_entries", "frame=pts_time,pict_type",
            "-of", "json",
            sourcePath,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success)
            throw new InvalidOperationException($"ffprobe show_frames failed (exit {r.ExitCode}): {r.Stderr}");

        return ParseCandidates(r.Stdout).Select(f => (f.timeUs - originUs, f.pictType)).ToArray();
    }

    public async Task<long?> FindBoundaryFrameAsync(string sourcePath, long timeUs, long durationUs, CancellationToken ct)
    {
        var frames = await EnumerateCandidatesAsync(sourcePath, Math.Max(0, timeUs - 2_000_000),
            Math.Min(durationUs, timeUs + 2_000_000), ct).ConfigureAwait(false);
        // FFmpeg's logged PTS may round by a microsecond. Never snap across a gap.
        return frames.Where(f => f.timeUs > 0 && f.timeUs < durationUs && Math.Abs(f.timeUs - timeUs) <= 1_000)
            .OrderBy(f => Math.Abs(f.timeUs - timeUs)).Select(f => (long?)f.timeUs).FirstOrDefault();
    }

    public async Task<long?> FindTransitionFrameAsync(string sourcePath, long timeUs, long durationUs,
        bool entering, bool fieldTransition, CancellationToken ct)
    {
        var frames = await EnumerateCandidatesAsync(sourcePath, Math.Max(0, timeUs - 2_000_000),
            Math.Min(durationUs, timeUs + 2_000_000), ct).ConfigureAwait(false);
        return SelectTransitionFrame(frames.Select(f => f.timeUs).ToArray(), timeUs, durationUs, entering, fieldTransition);
    }

    internal static long? SelectTransitionFrame(IReadOnlyList<long> frames, long timeUs, long durationUs,
        bool entering, bool fieldTransition)
    {
        var exact = frames.Where(t => t > 0 && t < durationUs && Math.Abs(t - timeUs) <= 1_000)
            .OrderBy(t => Math.Abs(t - timeUs)).Select(t => (long?)t).FirstOrDefault();
        if (exact is not null || !fieldTransition) return exact;
        var before = frames.Where(t => t > 0 && t < timeUs).Select(t => (long?)t).Max();
        var after = frames.Where(t => t > timeUs && t < durationUs).Select(t => (long?)t).Min();
        if (before is null || after is null || after - before > 100_000) return null;
        // Remove an entry frame containing either advertisement field. On return,
        // skip the mixed frame rather than retaining its outgoing commercial field.
        return entering ? before : after;
    }

    /// <summary>
    /// Blackdetect's end can fall inside a fade, after faint frames it still counts as black.
    /// Decodes the bounded black interval and returns the corrected cut, or null to keep blackEnd.
    /// </summary>
    public async Task<long?> FindFadeEdgeFrameAsync(string sourcePath, long blackStart, long blackEnd,
        int width, int height, bool entering, CancellationToken ct)
    {
        if (blackEnd - blackStart > SuggestionEngine.MaximumBlackTransitionUs || width <= 0 || height <= 0) return null;
        var times = new List<long>();
        var lit = new List<int>();
        var graphic = new List<bool>();
        var gate = new object();
        int cornerWidth = width * 3 / 10, cornerHeight = height * 3 / 10;
        long start = Math.Max(0, blackStart - 1_000_000);
        var media = await new MediaProbeService(_runner).ProbeAsync(sourcePath, ct).ConfigureAwait(false);
        string[] args = ["-nostdin", "-hide_banner", "-v", "info",
            "-ss", (start / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture),
            "-t", ((blackEnd - start + 200_000) / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture),
            .. FfmpegRunner.SourceTimestampArgs(media.TimelineStartTimeUs), "-i", sourcePath, "-map", "0:v:0", "-an", "-vf", "showinfo,format=gray",
            "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1"];
        var result = await _runner.RunFfmpegBinaryAsync(args, async stream =>
        {
            var buffer = new byte[width * height];
            while (true)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int next = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (next == 0) break;
                    read += next;
                }
                if (read == 0) break;
                if (read != buffer.Length) throw new InvalidDataException("Incomplete boundary frame.");
                int count = 0, best = 0;
                var corners = new int[4];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (buffer[y * width + x] <= FadeLuma) continue;
                        count++;
                        bool left = x < cornerWidth, right = x >= width - cornerWidth, top = y < cornerHeight, bottom = y >= height - cornerHeight;
                        if (top && left) corners[0]++; else if (top && right) corners[1]++;
                        else if (bottom && left) corners[2]++; else if (bottom && right) corners[3]++;
                    }
                foreach (int c in corners) best = Math.Max(best, c);
                // Dark frame whose only lit pixels sit in one corner: a station graphic over black.
                bool overBlack = count <= GraphicFraction * width * height && count - best <= FadeFraction * width * height;
                lock (gate) { lit.Add(count); graphic.Add(overBlack); }
                if (lit.Count > 1_200) throw new InvalidOperationException("Too many boundary frames.");
            }
        }, line =>
        {
            var match = ShowInfoPts.Match(line);
            if (match.Success)
                lock (gate) times.Add((long)Math.Round(double.Parse(match.Groups["t"].Value, CultureInfo.InvariantCulture) * 1_000_000));
        }, ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Boundary frame decode failed: " + result.Stderr);
        lock (gate)
        {
            if (times.Count != lit.Count) return null;
            long blackLimit = (long)(FadeFraction * width * height);
            var frames = times.Zip(lit).ToArray();
            return SelectFadeEdge(frames, blackStart, blackEnd, blackLimit, entering)
                ?? (entering ? null : SelectGraphicOverBlackReturn(frames.Select((f, i) => (f.First, f.Second, graphic[i])).ToArray(), blackStart, blackEnd, blackLimit));
        }
    }

    private const int FadeLuma = 32;
    private const double FadeFraction = 1e-4, GraphicFraction = .03;
    private static readonly Regex ShowInfoPts = new(@"\sn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:(?<t>[\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Entering: the first faint frame after the last fully black one, so no advertisement fade is kept.
    /// Returning: that last fully black frame, so no program fade is removed. Null keeps blackEnd.
    /// </summary>
    internal static long? SelectFadeEdge(IReadOnlyList<(long Time, int Lit)> frames, long blackStart, long blackEnd,
        long blackLimit, bool entering)
    {
        int last = -1;
        for (int i = 0; i < frames.Count; i++)
            if (frames[i].Time >= blackStart - 1_000 && frames[i].Time < blackEnd - 1_000 && frames[i].Lit <= blackLimit) last = i;
        if (last < 0 || last + 1 >= frames.Count) return null;
        long firstLit = frames[last + 1].Time;
        // No faint frames between full black and blackdetect's end: its cut is already right.
        if (firstLit >= blackEnd - 1_000) return null;
        return entering ? firstLit : frames[last].Time;
    }

    /// <summary>
    /// A return whose dark interval has no fully black frame and opens with the station graphic over
    /// black returns at the first such frame: the graphic belongs to the program side, and anything
    /// after it in the interval is the program fading in. Null keeps the detected end.
    /// </summary>
    internal static long? SelectGraphicOverBlackReturn(IReadOnlyList<(long Time, int Lit, bool Graphic)> frames,
        long blackStart, long blackEnd, long blackLimit)
    {
        var inside = frames.Where(f => f.Time >= blackStart - 1_000 && f.Time < blackEnd - 1_000).ToArray();
        if (inside.Length < 2 || inside.Any(f => f.Lit <= blackLimit) || !inside[0].Graphic || !inside[1].Graphic) return null;
        return inside[0].Time;
    }

    public async Task<long?> FindPreviousFrameAsync(string sourcePath, long timeUs, CancellationToken ct)
    {
        // Used only when the player's backstep cannot cross a damaged timestamp gap.
        foreach (long lookbackUs in new long[] { 2_000_000, 10_000_000, 60_000_000 })
        {
            var startUs = Math.Max(0, timeUs - lookbackUs);
            var frames = await EnumerateCandidatesAsync(sourcePath, startUs, timeUs + 100_000, ct).ConfigureAwait(false);
            var previous = frames.Where(f => f.timeUs < timeUs - 2).Select(f => (long?)f.timeUs).Max();
            if (previous is not null || startUs == 0) return previous;
        }
        return null;
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
            return bestDelta <= 1_000 ? Math.Clamp(best, 0.0, 1.0) : 0.0;
        }
    }

    private async Task<SignalSet> CollectSignalsAsync(
        string sourcePath, long winStartUs, long winEndUs, long offsetUs, int audioIndex, CancellationToken ct)
    {
        var startSec = (winStartUs / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var durSec = ((winEndUs - winStartUs) / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);

        // Newer FFmpeg (N-12xxxx+) rejects filtergraphs where every branch
        // ends in nullsink - the graph reports "zero outputs". Each terminal
        // must produce a labelled output that we then -map and discard via
        // `-f null -`. blackdetect / silencedetect / metadata=print all
        // write to stderr regardless of how the frames are routed.
        var args = new List<string>
        {
            "-v", "info",
            "-ss", startSec, "-t", durSec,
            "-i", sourcePath,
            "-filter_complex",
              "[0:v]split=2[v1][v2];"
            + "[v1]blackdetect=d=0.04:pix_th=0.10[vb];"
            + "[v2]select='eq(n,0)+gt(scene\\,0.0)',metadata=print[vs];"
            + (audioIndex >= 0 ? $"[0:{audioIndex}]silencedetect=n=-30dB:d=0.04[as]" : "anullsrc,atrim=duration=0[as]"),
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
        long? blackStart = null;
        foreach (var line in stderr.Split('\n'))
            if (CommercialBreakDetector.TryParseBlackEvent(line, ref blackStart, out var interval))
                black.Add((offsetUs + interval.Start, offsetUs + interval.End));

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
