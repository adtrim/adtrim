using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>
/// Review-only break suggestions for recordings with no learnable station graphic.
/// Uses the recording's own learned program profile (<see cref="ShowProfile"/>), splits
/// the timeline into program and breaks at picture transitions, and keeps only edges where
/// station splices show: brief audio silences repeating at whole advertisement-unit spacings.
/// Abstains when the profile contradicts its own premise that program is the majority, or
/// leaves too much undecided. Protection only ever shrinks a break, so errors keep
/// advertisement rather than remove program.
/// </summary>
public static class LogoFreeSegmenter
{
    private const int MinBreak = 45, MinProgram = 10;
    private const double SwitchCost = 25, BlackBonus = 8, DipBonus = 6;
    private const long UnitTolerance = 300_000, DipReach = 250_000;
    private static readonly long[] Units = [15_000_000, 30_000_000, 45_000_000, 60_000_000];

    public static IReadOnlyList<CommercialBreakProposal> FindBreaks(SuggestionEvidence evidence, long durationUs, CancellationToken ct = default)
    {
        var raw = ShowProfile.Score(evidence, durationUs, ct);
        if (raw is null || evidence.Loudness.Length == 0) return [];
        var smooth = ShowProfile.Smooth(raw, 15);
        if (smooth.Count(p => p > .5) > .45 * smooth.Length
            || smooth.Count(p => p > .2 && p < .8) > .25 * smooth.Length) return [];
        var events = Events(evidence, raw.Length);
        var dips = events.Values.Where(e => e.Dip).Select(e => e.Time).Order().ToArray();
        var result = new List<CommercialBreakProposal>();
        foreach (var (start, end) in Segment(raw, events))
        {
            // Entry starts a chain of unit-spaced splices; return ends one. Never grows a break.
            long? entry = dips.Where(t => t >= start && t < end && Chain(dips, t, 1)).Select(t => (long?)t).FirstOrDefault();
            long? exit = dips.Where(t => t > start && t <= end && Chain(dips, t, -1)).Select(t => (long?)t).LastOrDefault();
            if (entry is { } s && exit is { } e && e - s >= MinBreak * 1_000_000L)
                result.Add(new(s, e, Confidence.Low, false,
                    "No station graphic: the recording's learned program profile and repeating splice silences mark this interval. Review required."));
        }
        return result;
    }

    private readonly record struct Event(long Time, bool Black, bool Dip);

    /// <summary>One picture transition per second (black end preferred, else strongest cut),
    /// kept only if it is a black end or coincides with a splice-level silence.</summary>
    private static Dictionary<int, Event> Events(SuggestionEvidence evidence, int seconds)
    {
        var best = new Dictionary<int, (long Time, bool Black, double Score)>();
        foreach (var (time, score) in evidence.Scenes)
        {
            int i = (int)(time / 1_000_000);
            if (i <= 0 || i >= seconds) continue;
            if (!best.TryGetValue(i, out var current) || (!current.Black && score > current.Score)) best[i] = (time, false, score);
        }
        foreach (var (start, end) in evidence.Black)
        {
            int i = (int)(end / 1_000_000);
            if (end - start >= 60_000 && end - start <= SuggestionEngine.MaximumBlackTransitionUs && i > 0 && i < seconds) best[i] = (end, true, 1);
        }
        var quiet = QuietRuns(evidence.Loudness);
        var result = new Dictionary<int, Event>();
        foreach (var (i, e) in best)
        {
            bool dip = quiet.Any(q => q.Start - DipReach <= e.Time && e.Time <= q.End + DipReach);
            if (dip || e.Black) result[i] = new(e.Time, e.Black, dip);
        }
        return result;
    }

    /// <summary>Sorted picture transitions (black ends, cuts) that coincide with a splice silence.</summary>
    internal static long[] SpliceTransitions(SuggestionEvidence evidence, List<(long Start, long End)> quiet)
        => evidence.Black.Where(w => w.End - w.Start >= 60_000 && w.End - w.Start <= SuggestionEngine.MaximumBlackTransitionUs).Select(w => w.End)
            .Concat(evidence.Scenes.Select(s => s.Time)).Where(t => NearQuiet(quiet, t)).Distinct().Order().ToArray();

    internal static bool NearQuiet(List<(long Start, long End)> quiet, long time)
        => quiet.Any(q => q.Start - DipReach <= time && time <= q.End + DipReach);

    /// <summary>Runs at or below the looser of -50 dBFS and 6 dB above the recording's quietest 1%.
    /// None when the recording is never much quieter than usual (1% floor within 15 dB of the median):
    /// splices are then indistinguishable from ordinary pauses.</summary>
    internal static List<(long Start, long End)> QuietRuns(byte[] loudness)
    {
        var runs = new List<(long, long)>();
        if (loudness.Length == 0) return runs;
        var sorted = loudness.Order().ToArray();
        double floor = sorted[(int)(.01 * (sorted.Length - 1))] - 120.0, median = sorted[sorted.Length / 2] - 120.0;
        if (floor > median - 15) return runs;
        double level = Math.Max(-50, floor + 6) + 120;
        for (int i = 0; i < loudness.Length;)
        {
            if (loudness[i] > level) { i++; continue; }
            int j = i;
            while (j < loudness.Length && loudness[j] <= level) j++;
            runs.Add((i * 20_000L, j * 20_000L));
            i = j;
        }
        return runs;
    }

    /// <summary>Advertisements have exact lengths, so splices sit within a few frames of whole units;
    /// a looser match links unrelated pauses (a 45.70 s gap bridged an opening theme).</summary>
    internal static bool Chain(long[] dips, long t, int direction)
    {
        foreach (long u in Units)
            foreach (long first in Near(dips, t + direction * u))
                foreach (long v in Units)
                    if (Near(dips, first + direction * v).Any()) return true;
        return false;
    }

    private static IEnumerable<long> Near(long[] sorted, long target)
    {
        int i = Array.BinarySearch(sorted, target - UnitTolerance);
        if (i < 0) i = ~i;
        for (; i < sorted.Length && sorted[i] <= target + UnitTolerance; i++) yield return sorted[i];
    }

    /// <summary>Best program/break labeling of the seconds (duration-limited Viterbi): switches
    /// only at kept transitions, breaks at least 45 s, program at least 10 s.</summary>
    private static List<(long Start, long End)> Segment(double[] raw, Dictionary<int, Event> events)
    {
        int n = raw.Length, programStates = MinProgram, size = MinProgram + MinBreak;
        int Index(int label, int d) => label == 0 ? d : programStates + d;
        int Cap(int label) => label == 0 ? MinProgram : MinBreak;
        double Emit(int label, int i)
        {
            double p = Math.Clamp(raw[i], 1e-3, 1 - 1e-3);
            return label == 0 ? Math.Log(1 - p) : Math.Log(p);
        }
        var score = new double[size];
        Array.Fill(score, double.NegativeInfinity);
        score[Index(0, 0)] = Emit(0, 0);
        var back = new int[n][];
        back[0] = new int[size];
        for (int i = 1; i < n; i++)
        {
            var next = new double[size];
            Array.Fill(next, double.NegativeInfinity);
            var from = new int[size];
            Array.Fill(from, -1);
            for (int label = 0; label < 2; label++)
            {
                int cap = Cap(label);
                for (int d = 1; d < cap; d++) { next[Index(label, d)] = score[Index(label, d - 1)]; from[Index(label, d)] = Index(label, d - 1); }
                if (score[Index(label, cap - 1)] > next[Index(label, cap - 1)])
                { next[Index(label, cap - 1)] = score[Index(label, cap - 1)]; from[Index(label, cap - 1)] = Index(label, cap - 1); }
                if (events.TryGetValue(i, out var e))
                {
                    double cost = SwitchCost - (e.Black ? BlackBonus : 0) - (e.Dip ? DipBonus : 0);
                    int other = 1 - label;
                    double candidate = score[Index(other, Cap(other) - 1)] - cost;
                    if (candidate > next[Index(label, 0)]) { next[Index(label, 0)] = candidate; from[Index(label, 0)] = Index(other, Cap(other) - 1); }
                }
                double emit = Emit(label, i);
                for (int d = 0; d < cap; d++) next[Index(label, d)] += emit;
            }
            score = next; back[i] = from;
        }
        int state = Array.IndexOf(score, score.Max());
        var labels = new int[n];
        for (int i = n - 1; i >= 0; i--)
        {
            labels[i] = state < programStates ? 0 : 1;
            if (i > 0) state = back[i][state];
        }
        var result = new List<(long, long)>();
        int? begin = null;
        for (int i = 0; i < n; i++)
        {
            if (labels[i] == 1 && begin is null) begin = i;
            if (labels[i] == 0 && begin is { } b)
            {
                if (events.TryGetValue(b, out var s) && events.TryGetValue(i, out var e)) result.Add((s.Time, e.Time));
                begin = null;
            }
        }
        return result;
    }
}
