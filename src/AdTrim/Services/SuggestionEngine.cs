using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Review-only sequence suggestions from learned graphics and picture transitions.</summary>
public sealed class SuggestionEngine(FfmpegRunner runner)
{
    public const string Version = "commercial-v3-preview-19";
    // Some programs fade up through several seconds of black; all 3-10 s black
    // intervals in the reviewed recordings were break transitions.
    internal const long MaximumBlackTransitionUs = 10_000_000;

    /// <summary>Named analysis steps and their share of the overall progress bar.</summary>
    internal static readonly (string Name, double Start, double End)[] Stages =
    [
        ("Listening to the audio", 0, .10),
        ("Scanning the picture", .10, .55),
        ("Measuring the station logo and colors", .55, .95),
        ("Placing markers on exact frames", .95, 1),
    ];

    /// <summary>Overall progress for <paramref name="fraction"/> of step <paramref name="stage"/>.</summary>
    public static CommercialDetectionProgress StageProgress(int stage, double fraction, long durationUs)
    {
        var (name, start, end) = Stages[stage];
        double overall = start + (end - start) * Math.Clamp(fraction, 0, 1);
        return new((long)(durationUs * overall), durationUs) { Stage = $"Step {stage + 1} of {Stages.Length}: {name}" };
    }

    /// <param name="audio">The recording's shared audio pass, if one is running; see
    /// <see cref="SuggestionEvidenceCollector.CollectAsync"/>.</param>
    /// <param name="waitForIdle">Lets the picture passes pause for playback seeks; see
    /// <see cref="SuggestionEvidenceCollector.CollectAsync"/>.</param>
    public async Task<CommercialDetectionResult> AnalyzeAsync(string path, MediaInfo media,
        IProgress<CommercialDetectionProgress>? progress = null, CancellationToken cancellationToken = default,
        Func<IProgress<double>, CancellationToken, Task<AudioLevels>>? audio = null,
        Func<CancellationToken, ValueTask>? waitForIdle = null)
    {
        var evidence = await new SuggestionEvidenceCollector(runner).CollectAsync(path, media, progress, cancellationToken, audio, waitForIdle).ConfigureAwait(false);
        return await AnalyzeEvidenceAsync(path, media, evidence, cancellationToken, progress).ConfigureAwait(false);
    }

    public async Task<CommercialDetectionResult> AnalyzeEvidenceAsync(string path, MediaInfo media,
        SuggestionEvidence evidence, CancellationToken cancellationToken = default,
        IProgress<CommercialDetectionProgress>? progress = null)
    {
        var uncertain = new List<CommercialBreakProposal>();
        var proposals = Classify(evidence, media.DurationUs, uncertain.Add);
        int candidates = proposals.Count + uncertain.Count;
        var refined = new List<CommercialBreakProposal>();
        // Proposals whose edges have no valid source frames stay in the report, never shown.
        var unmappable = new List<CommercialBreakProposal>();
        var refiner = new RefineService(runner);
        int mapped = 0, toMap = Math.Max(1, proposals.Count + uncertain.Count);
        void Mapped() => progress?.Report(StageProgress(3, ++mapped / (double)toMap, media.DurationUs));
        progress?.Report(StageProgress(3, 0, media.DurationUs));
        foreach (var proposal in proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mapped();
            long? start, end;
            try
            {
                long startUs = await FadeEdgeAsync(proposal.StartUs, entering: true).ConfigureAwait(false);
                long endUs = await FadeEdgeAsync(proposal.EndUs, entering: false).ConfigureAwait(false);
                start = await refiner.FindTransitionFrameAsync(path, startUs, media.DurationUs,
                    entering: true, evidence.FieldTransitions, cancellationToken).ConfigureAwait(false);
                end = await refiner.FindTransitionFrameAsync(path, endUs, media.DurationUs,
                    entering: false, evidence.FieldTransitions, cancellationToken).ConfigureAwait(false);
            }
            // One unreadable stretch must not discard the full-recording passes already done.
            catch (Exception ex) when (IsUnreadableBoundary(ex)) { start = end = null; }
            if (start is null || end is null || start >= end)
            {
                unmappable.Add(proposal with { Evidence = "Uncertain: no valid source frames at both boundaries.", EndpointConfidence = Confidence.Unchanged });
                continue;
            }
            refined.Add(proposal with { StartUs = start.Value, EndUs = end.Value, EndpointConfidence = Confidence.Medium });
        }
        // Uncertain candidates are shown as low-confidence markers, so they also need real frames.
        var shown = new List<CommercialBreakProposal>(unmappable);
        foreach (var candidate in uncertain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mapped();
            long? start = null, end = null;
            try
            {
                start = candidate.StartUs <= 0 ? 0 : await refiner.FindTransitionFrameAsync(path,
                    await FadeEdgeAsync(candidate.StartUs, entering: true).ConfigureAwait(false), media.DurationUs,
                    entering: true, evidence.FieldTransitions, cancellationToken).ConfigureAwait(false);
                end = candidate.EndUs >= media.DurationUs ? media.DurationUs : await refiner.FindTransitionFrameAsync(path,
                    await FadeEdgeAsync(candidate.EndUs, entering: false).ConfigureAwait(false), media.DurationUs,
                    entering: false, evidence.FieldTransitions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreadableBoundary(ex)) { start = end = null; }
            shown.Add(start is { } s && end is { } e && s < e
                ? candidate with { StartUs = s, EndUs = e, EndpointConfidence = Confidence.Low }
                : candidate with { EndpointConfidence = Confidence.Unchanged });
        }
        return new(refined, Version, evidence.Black.Count, evidence.Scenes.Count, candidates)
            { UncertainBreaks = shown };

        async Task<long> FadeEdgeAsync(long time, bool entering)
        {
            var black = evidence.Black.Where(w => w.End == time).Select(w => ((long, long)?)w).FirstOrDefault();
            if (black is not { } window) return time;
            return await refiner.FindFadeEdgeFrameAsync(path, window.Item1, window.Item2, media.Width, media.Height,
                entering, cancellationToken).ConfigureAwait(false) ?? time;
        }
    }

    // What the local frame probes throw for a damaged or unexpected stretch of the recording.
    private static bool IsUnreadableBoundary(Exception ex)
        => ex is InvalidOperationException or System.IO.InvalidDataException or System.Text.Json.JsonException or FormatException;

    /// <summary>Graphic-based breaks, plus uncertain candidates that the recording's own
    /// learned program profile clearly marks as non-program, with edges re-chosen from it.</summary>
    public static IReadOnlyList<CommercialBreakProposal> Classify(SuggestionEvidence evidence, long durationUs,
        Action<CommercialBreakProposal>? onUncertain = null)
    {
        // Without a learnable station graphic, only the recording's own profile and splices can speak.
        if (evidence.GraphicPixels == 0 || evidence.GraphicMatches.Count == 0)
            return LogoFreeSegmenter.FindBreaks(evidence, durationUs);
        var uncertain = new List<CommercialBreakProposal>();
        var accepted = FindBreaks(evidence, durationUs, uncertain.Add).ToList();
        var profile = uncertain.Count > 0 ? ShowProfile.Score(evidence, durationUs) : null;
        foreach (var candidate in uncertain)
        {
            var promoted = profile is null ? null : Promote(evidence, profile, candidate);
            if (promoted is not null) accepted.Add(promoted);
            // The recording's own profile says this is the program (an intro, a logo-free scene): no marker.
            else if (!LooksLikeProgram(profile, candidate)) onUncertain?.Invoke(candidate);
        }
        var quiet = LogoFreeSegmenter.QuietRuns(evidence.Loudness);
        var splices = LogoFreeSegmenter.SpliceTransitions(evidence, quiet);
        // A recording can start or end inside a break. A logo-free edge can also be a cold open or
        // credits, so these are only ever shown as uncertain, never promoted, and only when the
        // inner edge is a station splice and the stretch does not look like the program itself.
        var edges = EdgeCandidates(evidence, durationUs);
        if (edges.Count > 0) profile ??= ShowProfile.Score(evidence, durationUs);
        foreach (var edge in edges)
            if (IsSplice(evidence, quiet, edge.StartUs <= 0 ? edge.EndUs : edge.StartUs) && !LooksLikeProgram(profile, edge))
                onUncertain?.Invoke(edge);
        return accepted.Select(p => ProtectProgramEdges(evidence, p, quiet, splices)).OrderBy(p => p.StartUs).ToArray();
    }

    /// <summary>A splice silence at the transition, counting silence anywhere in its black interval.</summary>
    internal static bool IsSplice(SuggestionEvidence evidence, List<(long Start, long End)> quiet, long time)
        => LogoFreeSegmenter.NearQuiet(quiet, time) || evidence.Black.Any(w => w.End == time
            && w.End - w.Start <= MaximumBlackTransitionUs && quiet.Any(q => q.Start <= w.End && q.End >= w.Start));

    // Abbott cold opens score .01-.06; real start-of-recording breaks .14-.61.
    internal const double ProgramLikeProfile = .1;

    internal static bool LooksLikeProgram(double[]? profile, CommercialBreakProposal edge)
    {
        if (profile is null) return false;
        int first = (int)(edge.StartUs / 1_000_000) + 3, last = Math.Min(profile.Length, (int)(edge.EndUs / 1_000_000) - 3);
        if (last - first < 5) return false;
        double sum = 0;
        for (int i = first; i < last; i++) sum += profile[i];
        return sum / (last - first) < ProgramLikeProfile;
    }

    /// <summary>Logo-free stretches touching the start or end of the recording (at least 10 s),
    /// ending (or starting) at the picture transition nearest to where the graphic settles.</summary>
    internal static IReadOnlyList<CommercialBreakProposal> EdgeCandidates(SuggestionEvidence evidence, long durationUs)
    {
        var matches = evidence.GraphicMatches;
        int n = matches.Count;
        if (n < 60) return [];
        bool Absent(int sample)
        {
            int strong = 0;
            for (int j = Math.Max(0, sample - 7); j <= Math.Min(n - 1, sample + 7); j++) if (matches[j] > .65) strong++;
            return strong < 3;
        }
        var black = evidence.Black.Where(w => w.End - w.Start >= 60_000 && w.End - w.Start <= MaximumBlackTransitionUs).Select(w => w.End).ToArray();
        long? Near(long time)
        {
            var dark = black.Where(t => t > 0 && t < durationUs && Math.Abs(t - time) <= 10_000_000).OrderBy(t => Math.Abs(t - time)).ToArray();
            if (dark.Length > 0) return dark[0];
            return evidence.Scenes.Where(s => s.Time > 0 && s.Time < durationUs && Math.Abs(s.Time - time) <= 10_000_000)
                .OrderBy(s => Math.Abs(s.Time - time)).ThenByDescending(s => s.Score).Select(s => (long?)s.Time).FirstOrDefault();
        }
        var result = new List<CommercialBreakProposal>();
        int lead = 0;
        while (lead < n && Absent(lead)) lead++;
        // A centered window sees the graphic settle about five samples after it appears.
        if (lead >= 10 && lead < n && Near((lead + 5) * 1_000_000L) is { } back)
            result.Add(new(0, back, Confidence.Low, false,
                "Uncertain: the recording begins without the station graphic (a break in progress or a cold open). Review required."));
        int tail = n - 1;
        while (tail >= 0 && Absent(tail)) tail--;
        if (n - 1 - tail >= 10 && tail >= 0 && Near((tail - 4) * 1_000_000L) is { } away && away < durationUs)
            result.Add(new(away, durationUs, Confidence.Low, false,
                "Uncertain: the recording ends without the station graphic (a break, credits or the next program). Review required."));
        return result;
    }

    /// <summary>
    /// Keeps logo-free program material that touches a break. A segment shorter than any
    /// advertisement between two black intervals at a return is the program's own act-in sting.
    /// An edge without a splice silence (closing credits, a next-week preview) moves inward to the
    /// nearest splice that starts or ends a chain of advertisement units within 25 s; further away,
    /// the edge is more likely a direct cut into an advertisement and is left alone. Edges only
    /// ever move into the break, so a wrong move keeps advertisement rather than removing program.
    /// </summary>
    internal static CommercialBreakProposal ProtectProgramEdges(SuggestionEvidence evidence, CommercialBreakProposal proposal,
        List<(long Start, long End)> quiet, long[] splices)
    {
        long start = proposal.StartUs, end = proposal.EndUs;
        var black = evidence.Black.Where(w => w.End - w.Start >= 60_000 && w.End - w.Start <= MaximumBlackTransitionUs).ToArray();
        foreach (var closing in black.Where(w => w.End == end))
        {
            var sting = black.Where(w => w.End < closing.Start && closing.Start - w.End <= StingUs).OrderBy(w => w.End).FirstOrDefault();
            if (sting.End > 0 && sting.End - start >= 45_000_000) end = sting.End;
        }
        if (quiet.Count > 0)
        {
            // A black-interval edge is a splice if silence touches any part of that interval: its
            // detected end can lie a few frames into a fade, after the silence has ended.
            bool Splice(long t) => LogoFreeSegmenter.NearQuiet(quiet, t)
                || black.Any(w => w.End == t && quiet.Any(q => q.Start <= w.End && q.End >= w.Start));
            if (!Splice(start))
            {
                long? inner = splices.Where(t => t > start && t <= start + AdjacentProgramUs && LogoFreeSegmenter.Chain(splices, t, 1)).Select(t => (long?)t).FirstOrDefault();
                if (inner is { } s && end - s >= 45_000_000) start = s;
            }
            if (!Splice(end))
            {
                long? inner = splices.Where(t => t < end && t >= end - AdjacentProgramUs && LogoFreeSegmenter.Chain(splices, t, -1)).Select(t => (long?)t).LastOrDefault();
                if (inner is { } e && e - start >= 45_000_000) end = e;
            }
        }
        return start == proposal.StartUs && end == proposal.EndUs ? proposal : proposal with { StartUs = start, EndUs = end };
    }

    // Shorter than any advertisement unit: a sting, bumper or title card belongs to the program.
    internal const long StingUs = 3_000_000;
    // Logo-free program material next to a break (credits, previews) seen so far: 10.7-23.4 s.
    internal const long AdjacentProgramUs = 25_000_000;

    internal const double ProfileAgreement = .8;

    internal static CommercialBreakProposal? Promote(SuggestionEvidence evidence, double[] profile, CommercialBreakProposal candidate)
    {
        int first = (int)(candidate.StartUs / 1_000_000) + 3, last = (int)(candidate.EndUs / 1_000_000) - 3;
        if (last - first < 30 || last > profile.Length) return null;
        double inside = 0;
        for (int i = first; i < last; i++) inside += profile[i];
        if (inside / (last - first) < ProfileAgreement) return null;
        var black = evidence.Black.Where(w => w.End - w.Start >= 60_000 && w.End - w.Start <= MaximumBlackTransitionUs)
            .Select(w => w.End).ToHashSet();
        var events = black.Concat(evidence.Scenes.Select(s => s.Time)).Distinct().Order().ToArray();
        long start = Edge(candidate.StartUs, entering: true), end = Edge(candidate.EndUs, entering: false);
        if (end - start < 45_000_000) return null;
        return candidate with
        {
            StartUs = start, EndUs = end,
            Evidence = "The recording's learned program profile marks this interval as non-program, with edges at its sharpest changes. Review required.",
        };

        // The candidate event within 20 s where the profile changes most sharply, preferring black.
        long Edge(long time, bool entering)
        {
            long best = time; double bestScore = double.NegativeInfinity;
            foreach (long t in events)
            {
                if (Math.Abs(t - time) > 20_000_000) continue;
                int i = (int)(t / 1_000_000);
                int b0 = Math.Max(0, i - 8), a1 = Math.Min(profile.Length, i + 9);
                if (i - b0 < 3 || a1 - (i + 1) < 3) continue;
                double before = 0, after = 0;
                for (int k = b0; k < i; k++) before += profile[k];
                for (int k = i + 1; k < a1; k++) after += profile[k];
                double change = (after / (a1 - i - 1) - before / (i - b0)) * (entering ? 1 : -1) + (black.Contains(t) ? .1 : 0);
                if (change > bestScore) { bestScore = change; best = t; }
            }
            return best;
        }
    }

    public static IReadOnlyList<CommercialBreakProposal> FindBreaks(SuggestionEvidence evidence, long durationUs,
        Action<CommercialBreakProposal>? onUncertain = null)
    {
        if (evidence.GraphicPixels == 0 || evidence.GraphicMatches.Count == 0) return [];
        var black = evidence.Black.Where(w => w.End > 0 && w.End < durationUs
            && w.End - w.Start >= 60_000 && w.End - w.Start <= MaximumBlackTransitionUs).ToArray();
        var bounds = new[] { 0L }.Concat(black.Select(w => w.End).Distinct().Order()).Append(durationUs).ToArray();
        var states = new bool?[bounds.Length - 1];
        for (int i = 0; i < states.Length; i++)
        {
            int begin = Math.Clamp((int)(bounds[i] / 1_000_000) + 2, 0, evidence.GraphicMatches.Count);
            int end = Math.Clamp((int)(bounds[i + 1] / 1_000_000) - 1, begin, evidence.GraphicMatches.Count);
            if (end - begin < 3) continue;
            int strong = 0;
            for (int sample = begin; sample < end; sample++)
                if (evidence.GraphicMatches[sample] > .65) strong++;
            states[i] = strong / (double)(end - begin) >= .2;
        }
        var darkCandidates = new List<(long Start, long End)>();
        int? previousPresent = null;
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] != true) continue;
            if (previousPresent is { } previous)
            {
                long missingUs = 0;
                for (int j = previous + 1; j < i; j++)
                    if (states[j] == false) missingUs += bounds[j + 1] - bounds[j];
                if (missingUs >= 20_000_000)
                    darkCandidates.Add((bounds[previous + 1], bounds[i]));
            }
            previousPresent = i;
        }
        // Measure sustained changes on a fixed clock, independently of cut density.
        // Otherwise rapidly edited material loses all its usable context windows.
        var gaps = new List<(long Start, long End)>();
        int? gapStart = null;
        for (int sample = 0; sample < evidence.GraphicMatches.Count; sample++)
        {
            int strong = 0;
            for (int j = Math.Max(0, sample - 7); j <= Math.Min(evidence.GraphicMatches.Count - 1, sample + 7); j++)
                if (evidence.GraphicMatches[j] > .65) strong++;
            bool absent = strong < 3;
            if (absent) { gapStart ??= sample; continue; }
            if (gapStart is { } start && start > 0 && sample - start >= 20)
                gaps.Add((start * 1_000_000L, sample * 1_000_000L));
            gapStart = null;
        }
        bool audioUsable = HasDiscriminatingAudio(evidence);
        var audioStarts = audioUsable ? FindAudioStarts(evidence.CentreDialogue) : [];
        var transitions = evidence.Scenes.Select(s => s.Time).Concat(black.Select(w => w.End)).Distinct().Order().ToArray();
        var result = new List<CommercialBreakProposal>();
        foreach (var gap in gaps)
        {
            var supported = darkCandidates.Where(c => Math.Abs(c.Start - gap.Start) <= 15_000_000
                && Math.Abs(c.End - gap.End) <= 15_000_000).ToArray();
            if (supported.Length > 0)
            {
                var candidate = supported.OrderBy(c => Math.Abs(c.Start - gap.Start) + Math.Abs(c.End - gap.End)).First();
                if (ContradictsDialogue(candidate.Start, candidate.End))
                {
                    onUncertain?.Invoke(Proposal(candidate.Start, candidate.End, "Uncertain: program-like audio contradicts the proposed interval"));
                    continue;
                }
                result.Add(Proposal(candidate.Start, candidate.End, "Sustained graphic change agrees with outer black transitions"));
                continue;
            }
            // Short logo-free program sections cannot support an independent pod.
            // Longer gaps still require review; duration is not commercial proof.
            if (gap.End - gap.Start < 45_000_000) continue;
            // With a centred 15-s window and three strong samples, a sharp loss
            // appears five seconds late and a return five seconds early.
            long? entry = TransitionNear(gap.Start - 5_000_000), exit = TransitionNear(gap.End + 5_000_000);
            bool audioEntry = false, audioReturn = false;
            if (audioUsable)
            {
                var anchor = audioStarts.Where(t => Math.Abs(t - (gap.Start - 5_000_000)) <= 15_000_000)
                    .OrderBy(t => Math.Abs(t - (gap.Start - 5_000_000))).Select(t => (long?)t).FirstOrDefault();
                audioEntry = anchor is not null;
                if (anchor is { } onset && (entry is null || !black.Any(w => w.End == entry)))
                {
                    var cut = evidence.Scenes.Where(s => s.Time > 0 && s.Time < durationUs && Math.Abs(s.Time - onset) <= 3_000_000)
                        .OrderByDescending(s => s.Score).ThenBy(s => Math.Abs(s.Time - onset)).Select(s => (long?)s.Time).FirstOrDefault();
                    entry = cut ?? TransitionNear(onset);
                }
                if (entry is { } begin)
                {
                    // A late station graphic must not move a return into dialogue.
                    // Require both immediate and sustained centre-channel support;
                    // a nearby ad cut followed later by dialogue is insufficient.
                    var returns = black.Where(w => w.End > begin + 45_000_000 && w.End <= gap.End + 15_000_000
                        && AudioFraction(w.End, 1, 7) >= .5 && AudioFraction(w.End, 2, 30) >= .5
                        && AudioFraction(w.End, -30, -2) < .5
                        && AudioFraction(w.End, 2, 30) - AudioFraction(w.End, -30, -2) >= .3)
                        .OrderBy(w => w.End).ToArray();
                    if (returns.Length > 0) { exit = returns[0].End; audioReturn = true; }
                }
            }
            if (entry is not { } start || exit is not { } end || start >= end)
            {
                onUncertain?.Invoke(Proposal(gap.Start, gap.End, "Uncertain: no complete pair of picture transitions"));
                continue;
            }
            if (ContradictsDialogue(start, end))
            {
                onUncertain?.Invoke(Proposal(start, end, "Uncertain: program-like audio contradicts the proposed interval"));
                continue;
            }
            bool programBefore = GraphicFraction(start, -30, -2) >= .2 || AudioFraction(start, -30, -2) >= .5;
            bool graphicReturn = GraphicFraction(end, 1, 7) >= .5 && GraphicFraction(end, 2, 30) >= .5;
            bool pairedAudio = audioEntry && audioReturn && black.Any(w => w.End == start);
            bool cadence = HasAdvertisementCadence(start, end, transitions);
            bool localAudio = !audioUsable || AudioFraction(start, -30, -2) >= .5 && AudioFraction(start, 2, 30) <= .2;
            if (!programBefore || !localAudio || !(audioReturn || graphicReturn) || !(pairedAudio || cadence))
            {
                string reason = !programBefore ? "no supported program context before the interval"
                    : !localAudio ? "no local agreement between audio and picture at entry"
                    : !(audioReturn || graphicReturn) ? "no corroborated return to program"
                    : "graphic absence lacks corroborating break evidence";
                onUncertain?.Invoke(Proposal(start, end, "Uncertain: " + reason));
                continue;
            }
            result.Add(Proposal(start, end, pairedAudio
                ? "Graphic gap agrees with audio changes and black picture boundaries"
                : "Graphic gap agrees with repeated short units and supported program return"));
        }
        return result.OrderBy(p => p.StartUs).ToArray();

        double AudioFraction(long time, int startOffset, int endOffset)
        {
            if (!audioUsable) return 0;
            int start = Math.Clamp((int)(time / 1_000_000) + startOffset, 0, evidence.CentreDialogue.Count);
            int end = Math.Clamp((int)(time / 1_000_000) + endOffset, start, evidence.CentreDialogue.Count);
            if (end - start < endOffset - startOffset) return 0;
            int count = 0;
            for (int i = start; i < end; i++) if (evidence.CentreDialogue[i]) count++;
            return count / (double)(end - start);
        }

        double GraphicFraction(long time, int startOffset, int endOffset)
        {
            int start = Math.Clamp((int)(time / 1_000_000) + startOffset, 0, evidence.GraphicMatches.Count);
            int end = Math.Clamp((int)(time / 1_000_000) + endOffset, start, evidence.GraphicMatches.Count);
            if (end - start < endOffset - startOffset) return 0;
            int count = 0;
            for (int i = start; i < end; i++) if (evidence.GraphicMatches[i] > .65) count++;
            return count / (double)(end - start);
        }

        bool ContradictsDialogue(long start, long end)
        {
            if (!audioUsable) return false;
            int begin = Math.Clamp((int)(start / 1_000_000) + 5, 0, evidence.CentreDialogue.Count);
            int finish = Math.Clamp((int)(end / 1_000_000) - 5, begin, evidence.CentreDialogue.Count);
            int count = 0;
            for (int i = begin; i < finish; i++) if (evidence.CentreDialogue[i]) count++;
            return finish > begin && count / (double)(finish - begin) >= .2
                || AudioFraction(end, -6, -1) >= .5;
        }

        long? TransitionNear(long time)
        {
            var dark = black.Where(w => Math.Abs(w.End - time) <= 10_000_000).OrderBy(w => Math.Abs(w.End - time)).ToArray();
            if (dark.Length > 0) return dark[0].End;
            return evidence.Scenes.Where(s => s.Time > 0 && s.Time < durationUs && Math.Abs(s.Time - time) <= 10_000_000)
                .OrderBy(s => Math.Abs(s.Time - time)).ThenByDescending(s => s.Score).Select(s => (long?)s.Time).FirstOrDefault();
        }
        static CommercialBreakProposal Proposal(long start, long end, string reason)
            => new(start, end, Confidence.Low, false,
                reason + ". Review required: intros, credits and program scenes can also lose the graphic.");
    }

    private static bool HasDiscriminatingAudio(SuggestionEvidence evidence)
    {
        int present = 0, absent = 0, presentCentre = 0, absentCentre = 0;
        for (int i = 0; i < Math.Min(evidence.GraphicMatches.Count, evidence.CentreDialogue.Count); i++)
        {
            if (evidence.GraphicMatches[i] > .65)
            {
                present++;
                if (evidence.CentreDialogue[i]) presentCentre++;
            }
            else if (evidence.GraphicMatches[i] < .2)
            {
                absent++;
                if (evidence.CentreDialogue[i]) absentCentre++;
            }
        }
        // A centre-heavy mix is common in both programs and advertisements.
        // It has no vote unless the recording itself supplies contrasting states.
        return present >= 30 && absent >= 30 && presentCentre / (double)present >= .5
            && presentCentre / (double)present - absentCentre / (double)absent >= .3;
    }

    private static bool HasAdvertisementCadence(long start, long end, long[] transitions)
    {
        long[] units = [15_000_000, 30_000_000, 60_000_000];
        const long tolerance = 750_000;
        foreach (long unit in units)
        {
            long target = start + unit;
            int index = Array.BinarySearch(transitions, target - tolerance);
            if (index < 0) index = ~index;
            for (; index < transitions.Length && transitions[index] <= Math.Min(target + tolerance, end); index++)
            {
                long first = transitions[index];
                foreach (long next in units)
                {
                    long secondTarget = first + next;
                    if (secondTarget - start < 45_000_000 || secondTarget - tolerance > end) continue;
                    int second = Array.BinarySearch(transitions, secondTarget - tolerance);
                    if (second < 0) second = ~second;
                    if (second < transitions.Length && transitions[second] <= Math.Min(secondTarget + tolerance, end)) return true;
                }
            }
        }
        return false;
    }

    private static IReadOnlyList<long> FindAudioStarts(IReadOnlyList<bool> dialogue)
    {
        var starts = new List<long>();
        int? gap = null;
        for (int sample = 0; sample < dialogue.Count; sample++)
        {
            int strong = 0;
            for (int j = Math.Max(0, sample - 7); j <= Math.Min(dialogue.Count - 1, sample + 7); j++)
                if (dialogue[j]) strong++;
            if (strong < 3) { gap ??= sample; continue; }
            if (gap is { } start && start > 0 && sample - start >= 45)
                starts.Add((start - 5) * 1_000_000L);
            gap = null;
        }
        return starts;
    }
}
