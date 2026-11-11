using System.Globalization;
using System.Text.RegularExpressions;
using AdTrim.Models;

namespace AdTrim.Services;

public sealed record CommercialBreakProposal(long StartUs, long EndUs, Confidence Confidence,
    bool AutoExclude, string Evidence, Confidence EndpointConfidence = Confidence.Unchanged);
public sealed record CommercialDetectionResult(IReadOnlyList<CommercialBreakProposal> Breaks,
    string DetectorVersion = CommercialBreakDetector.Version, int BlackTransitions = 0, int SceneChanges = 0, int Candidates = 0)
{
    public IReadOnlyList<CommercialBreakProposal> UncertainBreaks { get; init; } = [];
}
public sealed record CommercialDetectionProgress(long TimeUs, long DurationUs, bool Queued = false)
{
    /// <summary>Human-readable step, e.g. "Step 2 of 4: Measuring the station logo and colors".</summary>
    public string? Stage { get; init; }
}

/// <summary>
/// Experimental review proposals from repeated short units and editing-rate changes.
/// These signals cannot establish commercial identity; proposals remain included.
/// </summary>
public sealed class CommercialBreakDetector(FfmpegRunner runner)
{
    public const string Version = "commercial-v2-review";
    private const int MaximumEvents = 200_000;
    private const long MinBreakUs = 15_000_000;
    private const long MaxBreakUs = 240_000_000;
    private const long ProgramContextUs = 120_000_000;
    private const long MinBlackUs = 60_000;
    private const long MaxEdgeBlackUs = 3_000_000;
    private const double SceneThreshold = 0.32;

    private static readonly SemaphoreSlim AnalysisGate = new(1, 1);
    private static readonly Regex BlackStartRx = new(@"black_start:(?<s>[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex BlackEndRx = new(@"black_end:(?<e>[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex ProgressRx = new(@"^out_time_us=(?<t>-?\d+)$", RegexOptions.Compiled);
    private static readonly Regex SilenceRx = new(@"silence_(?<kind>start|end):\s*(?<t>-?[\d.]+)", RegexOptions.Compiled);

    public async Task<CommercialDetectionResult> AnalyzeAsync(string sourcePath, MediaInfo media,
        IProgress<CommercialDetectionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var black = new List<(long Start, long End)>();
        var scenes = new List<(long Time, double Score)>();
        var silences = new List<(long Start, long End)>();
        var pendingSilence = new Queue<long>();
        var pendingPts = new Queue<long>();
        long? pendingBlackStart = null;
        var signalLock = new object();
        bool eventLimitReached = false;
        void OnDiagnostic(string line)
        {
            if (black.Count + scenes.Count + silences.Count >= MaximumEvents)
            { eventLimitReached = true; return; }
            var blackStartMatch = BlackStartRx.Match(line);
            var blackEndMatch = BlackEndRx.Match(line);
            if (blackStartMatch.Success || blackEndMatch.Success)
                lock (signalLock)
                {
                    if (TryParseBlackEvent(line, ref pendingBlackStart, out var interval))
                        black.Add(interval);
                }
            var ptsMatch = Regex.Match(line, @"pts_time:(?<t>[\d.]+)");
            if (ptsMatch.Success)
            {
                var time = Seconds(ptsMatch.Groups["t"].Value);
                lock (signalLock) pendingPts.Enqueue(time);
            }
            var scoreMatch = Regex.Match(line, @"lavfi\.scene_score=(?<v>[\d.]+)");
            if (scoreMatch.Success && double.TryParse(scoreMatch.Groups["v"].Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                lock (signalLock)
                    if (pendingPts.TryDequeue(out var time) && score >= SceneThreshold)
                        scenes.Add((time, Math.Clamp(score, 0, 1)));
            var silenceMatch = SilenceRx.Match(line);
            if (silenceMatch.Success)
            {
                var time = Seconds(silenceMatch.Groups["t"].Value);
                lock (signalLock)
                {
                    if (silenceMatch.Groups["kind"].Value == "start") pendingSilence.Enqueue(time);
                    else if (pendingSilence.TryDequeue(out var silenceStart)) silences.Add((silenceStart, time));
                }
            }
        }
        void OnProgress(string line)
        {
            var match = ProgressRx.Match(line);
            if (match.Success && long.TryParse(match.Groups["t"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var time))
                progress?.Report(new CommercialDetectionProgress(Math.Clamp(time, 0, media.DurationUs), media.DurationUs));
        }

        var graph = "[0:v:0]scale=320:-2,split=2[dark][scene];"
            + "[dark]blackdetect=d=0.06:pix_th=0.10[blackout];"
            + "[scene]select='eq(n,0)+gt(scene\\,0.32)',metadata=print[sceneout]";
        if (media.PrimaryAudioIndex >= 0)
            graph += $";[0:{media.PrimaryAudioIndex}]silencedetect=n=-32dB:d=0.08[audioout]";
        List<string> args = ["-hide_banner", "-nostats", "-v", "info", .. FfmpegRunner.SourceTimestampArgs(media.TimelineStartTimeUs), "-i", sourcePath,
            "-filter_complex", graph, "-map", "[blackout]", "-map", "[sceneout]"];
        if (media.PrimaryAudioIndex >= 0) args.AddRange(["-map", "[audioout]"]);
        args.AddRange(["-progress", "pipe:1", "-stats_period", "2", "-f", "null", "-"]);
        progress?.Report(new CommercialDetectionProgress(0, media.DurationUs, Queued: true));
        await AnalysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FfmpegResult result;
        try
        {
            progress?.Report(new CommercialDetectionProgress(0, media.DurationUs));
            result = await runner.RunFfmpegAsync(args, OnProgress, cancellationToken, OnDiagnostic).ConfigureAwait(false);
        }
        finally { AnalysisGate.Release(); }
        if (!result.Success)
            throw new InvalidOperationException($"Commercial analysis failed (exit {result.ExitCode}): {result.ErrorSummary}");
        lock (signalLock)
        {
            black = black.OrderBy(x => x.Start).ToList();
            scenes = scenes.OrderBy(x => x.Time).ToList();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (eventLimitReached) throw new InvalidOperationException("Too many transitions to analyze safely. Current markings were kept.");
        var candidates = FindBreaks(black, scenes, media.DurationUs,
            media.PrimaryAudioIndex >= 0 ? silences : null);
        var refined = new List<CommercialBreakProposal>();
        var refiner = new RefineService(runner);
        foreach (var proposal in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = await refiner.FindBoundaryFrameAsync(sourcePath, proposal.StartUs, media.DurationUs, cancellationToken).ConfigureAwait(false);
            var end = await refiner.FindBoundaryFrameAsync(sourcePath, proposal.EndUs, media.DurationUs, cancellationToken).ConfigureAwait(false);
            if (start is null || end is null || start >= end) continue;
            refined.Add(proposal with { StartUs = start.Value, EndUs = end.Value,
                EndpointConfidence = Confidence.Medium });
        }
        return new CommercialDetectionResult(refined, Version, black.Count, scenes.Count, candidates.Count);
    }

    public static IReadOnlyList<CommercialBreakProposal> FindBreaks(
        IReadOnlyList<(long Start, long End)> black, IReadOnlyList<(long Time, double Score)> scenes,
        long durationUs, IReadOnlyList<(long Start, long End)>? silence = null)
    {
        var edges = black.Where(w => w.Start >= 0 && w.End < durationUs
                && w.End - w.Start >= MinBlackUs && w.End - w.Start <= MaxEdgeBlackUs)
            .Distinct().OrderBy(w => w.Start).ToArray();
        var orderedScenes = scenes.OrderBy(s => s.Time).ToArray();
        var candidates = new List<(CommercialBreakProposal Proposal, double Score)>();
        for (int left = 0; left < edges.Length; left++)
        {
            for (int right = left + 1; right < edges.Length && right < left + 65; right++)
            {
                // Retain the entry transition, which may still contain a program logo.
                // The exit is the first non-black frame, not the start of the fade.
                var start = edges[left].End;
                var end = edges[right].End;
                var span = end - start;
                if (span > MaxBreakUs) break;
                if (span < MinBreakUs || start < ProgramContextUs || durationUs - end < ProgramContextUs) continue;
                var before = CountBetween(orderedScenes, start - ProgramContextUs, edges[left].Start);
                var after = CountBetween(orderedScenes, end + 1_000_000, end + ProgramContextUs);
                var inside = CountBetween(orderedScenes, start + 1_000_000, edges[right].Start);
                var rate = inside / (double)span;
                var contextRate = Math.Max(before, after) / (double)ProgramContextUs;
                // Black and quiet describe the same fade, not two independent votes.
                // Require a change in editing pattern as well as repeated short units.
                if (inside < 3 || rate < 2 * Math.Max(contextRate, 1.0 / ProgramContextUs)) continue;
                int cadence = 0;
                for (int edge = left; edge < right; edge++)
                {
                    var seconds = (edges[edge + 1].Start - edges[edge].End) / 1_000_000.0;
                    if (new[] { 15, 30, 60 }.Any(n => Math.Abs(seconds - n) <= 2)) cadence++;
                }
                if (cadence < 2) continue;
                bool quietEntry = silence?.Any(w => w.Start <= start && w.End >= edges[left].Start) == true;
                bool quietExit = silence?.Any(w => w.Start <= end && w.End >= edges[right].Start) == true;
                var audioReason = silence is null ? "No audio evidence" : quietEntry && quietExit
                    ? "Quiet outer transitions" : "Audio does not confirm both transitions";
                var evidence = $"{cadence} short units; faster editing than nearby material; {audioReason}. Review required: these patterns also occur within programs.";
                if (candidates.Count >= 10_000)
                    throw new InvalidOperationException("Too many possible breaks to analyze safely. Current markings were kept.");
                candidates.Add((new CommercialBreakProposal(start, end, Confidence.Low, false, evidence),
                    cadence * 10 + Math.Min(5, rate / Math.Max(contextRate, 1.0 / ProgramContextUs))));
            }
        }
        // Weighted interval selection favors a complete pod over overlapping pieces.
        var sorted = candidates.OrderBy(c => c.Proposal.EndUs).ThenBy(c => c.Proposal.StartUs).ToArray();
        var scores = new double[sorted.Length + 1];
        var previous = new int[sorted.Length];
        var take = new bool[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            int lo = 0, hi = i;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid].Proposal.EndUs < sorted[i].Proposal.StartUs) lo = mid + 1;
                else hi = mid;
            }
            previous[i] = lo;
            var with = sorted[i].Score + scores[lo];
            take[i] = with > scores[i];
            scores[i + 1] = Math.Max(scores[i], with);
        }
        var selected = new List<CommercialBreakProposal>();
        for (int i = sorted.Length - 1; i >= 0;)
        {
            if (!take[i]) { i--; continue; }
            selected.Add(sorted[i].Proposal);
            i = previous[i] - 1;
        }
        selected.Reverse();
        return selected;
    }

    private static long Seconds(string value) => (long)Math.Round(
        double.Parse(value, CultureInfo.InvariantCulture) * 1_000_000.0);

    internal static bool TryParseBlackEvent(string line, ref long? pendingStart, out (long Start, long End) interval)
    {
        interval = default;
        var startMatch = BlackStartRx.Match(line);
        var endMatch = BlackEndRx.Match(line);
        if (startMatch.Success) pendingStart = Seconds(startMatch.Groups["s"].Value);
        if (!endMatch.Success || pendingStart is not { } start) return false;
        var end = Seconds(endMatch.Groups["e"].Value);
        pendingStart = null;
        if (end < start) return false;
        interval = (start, end);
        return true;
    }

    private static int CountBetween(IReadOnlyList<(long Time, double Score)> sorted, long start, long end)
        => LowerBound(sorted, end) - LowerBound(sorted, start);

    private static int LowerBound(IReadOnlyList<(long Time, double Score)> sorted, long time)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (sorted[mid].Time < time) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
