using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>
/// A recording window's marker sets: the imported chapters, AdTrim's cached and new suggestions,
/// the analysis run that produces them, and the rules for loading one into the editor.
/// Knows nothing about the window's menus, dialogs or taskbar.
/// </summary>
public sealed class MarkerSetController(MarkerSuggestionStore suggestions)
{
    /// <summary>Uncertain markers this close to a confident one are left out.</summary>
    public const long UncertainMarkerSpacingUs = 5_000_000;

    public MarkerSuggestionStore Suggestions => suggestions;

    /// <summary>The recording's chapters as a marker set; null when they could not be read.</summary>
    public MarkerSetState? Imported { get; private set; }

    /// <summary>True when the recording has chapters to revert to.</summary>
    public bool ImportedAvailable { get; private set; }

    public void SetImported(MarkerSetState? imported, bool available)
    {
        Imported = imported;
        ImportedAvailable = available;
    }

    // ---- The analysis run ----

    /// <summary>The running analysis; null when none is running.</summary>
    public CancellationTokenSource? Analysis { get; private set; }

    public bool IsAnalyzing => Analysis is not null;

    /// <summary>Cancels any earlier run and starts tracking a new one, which the caller disposes.</summary>
    public CancellationTokenSource BeginAnalysis(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Analysis?.Cancel();
        Analysis = linked;
        return linked;
    }

    public bool IsCurrent(CancellationTokenSource run) => ReferenceEquals(Analysis, run);

    public void EndAnalysis(CancellationTokenSource run)
    {
        if (ReferenceEquals(Analysis, run)) Analysis = null;
    }

    public void CancelAnalysis() => Analysis?.Cancel();

    public async Task WaitForAnalysisToStopAsync()
    {
        if (Analysis is not { } analysis) return;
        analysis.Cancel();
        while (ReferenceEquals(Analysis, analysis)) await Task.Delay(50);
    }

    // ---- Rules ----

    /// <summary>
    /// AdTrim's suggestion set from an analysis: a labeled pair of markers per break, auto-excluded where
    /// the detector says so, and red low-confidence markers for uncertain candidates that do not overlap
    /// a break or sit within <see cref="UncertainMarkerSpacingUs"/> of a confident marker.
    /// </summary>
    public static MarkerSetState FromAnalysis(CommercialDetectionResult result, long durationUs)
    {
        var splits = new List<PersistedSplit>();
        var exclusions = new List<string>();
        foreach (var proposal in result.Breaks.OrderBy(b => b.StartUs))
        {
            if (proposal.StartUs <= 0 || proposal.EndUs >= durationUs || proposal.EndUs <= proposal.StartUs
                || splits.Any(s => s.TimeUs >= proposal.StartUs && s.TimeUs < proposal.EndUs)) continue;
            var startId = Guid.NewGuid().ToString("N")[..12];
            var endId = Guid.NewGuid().ToString("N")[..12];
            splits.Add(new PersistedSplit(startId, proposal.StartUs, SplitSource.Manual, null,
                proposal.EndpointConfidence, false, "Possible commercial break"));
            splits.Add(new PersistedSplit(endId, proposal.EndUs, SplitSource.Manual, null,
                proposal.EndpointConfidence, false, null));
            if (proposal.AutoExclude) exclusions.Add($"segment-{proposal.StartUs}-{proposal.EndUs}");
        }
        // Uncertain candidates appear as low-confidence (red) markers, unless they overlap a
        // suggested break or a more confident marker is already within a few seconds.
        var confident = splits.Select(s => s.TimeUs).ToList();
        foreach (var candidate in result.UncertainBreaks.OrderBy(b => b.StartUs))
        {
            if (candidate.EndpointConfidence != Confidence.Low || candidate.EndUs <= candidate.StartUs
                || result.Breaks.Any(b => b.StartUs < candidate.EndUs && candidate.StartUs < b.EndUs)) continue;
            foreach (var (time, isStart) in new[] { (candidate.StartUs, true), (candidate.EndUs, false) })
            {
                if (time <= 0 || time >= durationUs || confident.Any(t => Math.Abs(t - time) <= UncertainMarkerSpacingUs)
                    || splits.Any(s => s.TimeUs == time)) continue;
                splits.Add(new PersistedSplit(Guid.NewGuid().ToString("N")[..12], time, SplitSource.Manual, null,
                    Confidence.Low, false, isStart ? "Possible commercial break (uncertain)" : null));
            }
        }
        splits.Sort((a, b) => a.TimeUs.CompareTo(b.TimeUs));
        var provisional = new MarkerSetState(splits, exclusions, null, MarkerSetSource.AdTrim, null);
        return provisional with { BaselineId = provisional.ContentId() };
    }

    /// <summary>The markers of a project saved for an earlier version of the recording.</summary>
    public static MarkerSetState FromEarlierProject(AdTrimProject earlier)
        => new(earlier.Splits, earlier.ExcludedSegmentIds, earlier.StartChapterTitle, earlier.MarkerSetSource,
            earlier.MarkerBaselineId?.StartsWith("v2:", StringComparison.Ordinal) == true ? earlier.MarkerBaselineId : null);

    /// <summary>Suggested breaks (each starts with a labeled marker) and uncertain red markers in a set.</summary>
    public static (int Breaks, int Uncertain) SuggestionCounts(MarkerSetState state) =>
        (state.Splits.Count(s => s.Confidence != Confidence.Low && s.ChapterTitle is not null),
         state.Splits.Count(s => s.Confidence == Confidence.Low));

    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public static string ElapsedText(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s"
        : $"{Math.Max(1, (int)Math.Round(elapsed.TotalSeconds))} s";

    /// <summary>Rough time left from the rate so far; null until there is enough progress to estimate.</summary>
    public static string? TimeLeftText(TimeSpan elapsed, double done, double remaining)
    {
        if (elapsed < TimeSpan.FromSeconds(5) || done < .03) return null;
        double seconds = elapsed.TotalSeconds * remaining / done;
        return seconds < 60 ? "Less than a minute left" : $"About {(int)Math.Ceiling(seconds / 60)} min left";
    }

    public static BannerInfo AnalysisFinished(MarkerSetState state, TimeSpan elapsed)
    {
        var (breaks, uncertain) = SuggestionCounts(state);
        return new BannerInfo(StatusKind.Success, $"Ad-break analysis finished in {ElapsedText(elapsed)}.",
            $"Found {Plural(breaks, "ad break")} and {Plural(uncertain, "uncertain red marker")}. Nothing was excluded; "
            + "review each break and exclude the ones you want removed.", Array.Empty<BannerAction>());
    }

    /// <summary>The status line after a marker set is loaded.</summary>
    public static string AppliedStatus(MarkerSetState state, string action)
    {
        var (breaks, uncertain) = SuggestionCounts(state);
        return state.Source == MarkerSetSource.AdTrim
            ? $"{Plural(breaks, "possible ad break")}, {Plural(uncertain, "uncertain red marker")}. All video remains included; review before excluding."
            : action;
    }

    /// <summary>
    /// What to ask before edits are replaced by <paramref name="destination"/> markers; null when there are
    /// no edits to lose. Ask before any analysis work, not after.
    /// </summary>
    public static (string Message, string Title)? DiscardQuestion(MainViewModel vm, MarkerSetSource destination)
    {
        if (!vm.IsTimelineEdited) return null;
        var sourceName = vm.MarkerSetSource switch
        {
            MarkerSetSource.Imported => "imported markers",
            MarkerSetSource.AdTrim => "AdTrim suggested markers",
            _ => "current markings",
        };
        var target = destination == MarkerSetSource.Imported ? "the original imported markers" : "the original AdTrim suggested markers";
        var confirmationTitle = vm.MarkerSetSource switch
        {
            MarkerSetSource.Imported => "Discard edits to imported markers?",
            MarkerSetSource.AdTrim => "Discard edits to AdTrim suggested markers?",
            _ => "Discard current edits?",
        };
        return ($"This discards your edits to {sourceName}, including split positions, section names, and exclusions, then loads {target}.",
            confirmationTitle);
    }

    // ---- Loading markers into the editor ----

    public static void HydrateFromSidecar(MainViewModel vm, AdTrimProject proj)
    {
        vm.LastExport = proj.LastExport;
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();
        vm.MarkerSetSource = proj.MarkerSetSource;
        vm.MarkerBaselineId = proj.MarkerBaselineId?.StartsWith("v2:", StringComparison.Ordinal) == true
            ? proj.MarkerBaselineId : null;

        // Add bookends + persisted internal splits.
        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
        vm.Markers[0].ChapterTitle = proj.StartChapterTitle;
        foreach (var ps in proj.Splits.OrderBy(s => s.TimeUs))
        {
            vm.Markers.Add(new Split
            {
                Id = ps.Id,
                TimeUs = ps.TimeUs,
                Source = ps.Source,
                OriginalTimeUs = ps.OriginalTimeUs,
                Confidence = ps.Confidence,
                Confirmed = ps.Confirmed,
                ChapterTitle = ps.ChapterTitle,
            });
        }
        vm.Markers.Add(new Split { TimeUs = vm.DurationUs, Confidence = null, Confirmed = true, Label = "End" });

        foreach (var id in proj.ExcludedSegmentIds) vm.ExcludedSegmentIds.Add(id);
        vm.RebuildSegmentsFromSplits();
    }

    public static void HydrateFromChapters(MainViewModel vm, MediaInfo media, IReadOnlyList<ChapterBoundary> chapters)
    {
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();

        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
        vm.Markers[0].ChapterTitle = chapters.FirstOrDefault(c => c.TimeUs == 0)?.Title;
        foreach (var c in chapters.Where(c => c.TimeUs > 0))
        {
            vm.Markers.Add(new Split
            {
                TimeUs = c.TimeUs,
                ChapterTitle = c.Title,
                Source = SplitSource.Chapter,
                Confidence = null,
                Confirmed = false,
            });
        }
        vm.Markers.Add(new Split { TimeUs = media.DurationUs, Confidence = null, Confirmed = true, Label = "End" });
        vm.MarkerSetSource = MarkerSetSource.Imported;
        var state = RecordingLoader.MarkersFromChapters(chapters, MarkerSetSource.Imported);
        vm.MarkerBaselineId = state.ContentId();
        vm.RebuildSegmentsFromSplits();
    }

    public static void HydrateBlank(MainViewModel vm, MarkerSetSource source)
    {
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();
        vm.Markers.Add(new Split { TimeUs = 0, Confirmed = true, Label = "Start" });
        vm.Markers.Add(new Split { TimeUs = vm.DurationUs, Confirmed = true, Label = "End" });
        vm.MarkerSetSource = source;
        vm.RebuildSegmentsFromSplits();
        vm.MarkerBaselineId = vm.CurrentMarkerSetId();
    }
}
