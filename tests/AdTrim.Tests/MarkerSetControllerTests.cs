using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// AdTrim's suggestions must never exclude video on their own judgment except where the detector
// says so, never stack markers on top of each other, and never replace edits without asking.
public sealed class MarkerSetControllerTests
{
    private const long Duration = 1_800_000_000;
    private static CommercialBreakProposal Break(long start, long end, bool exclude = false, Confidence endpoints = Confidence.High)
        => new(start, end, Confidence.High, exclude, "test", endpoints);

    [Fact]
    public void EachBreakBecomesALabeledPairAndOnlyDetectorExclusionsAreExcluded()
    {
        var state = MarkerSetController.FromAnalysis(new CommercialDetectionResult(
            [Break(600_000_000, 780_000_000), Break(1_200_000_000, 1_380_000_000, exclude: true)]), Duration);
        Assert.Equal(MarkerSetSource.AdTrim, state.Source);
        Assert.Equal([600_000_000L, 780_000_000, 1_200_000_000, 1_380_000_000], state.Splits.Select(s => s.TimeUs));
        Assert.Equal(["Possible commercial break", null, "Possible commercial break", null], state.Splits.Select(s => s.ChapterTitle));
        Assert.Equal(["segment-1200000000-1380000000"], state.ExcludedSegmentIds);
        Assert.All(state.Splits, s => Assert.False(s.Confirmed));
        Assert.Equal(state.ContentId(), state.BaselineId);
        Assert.Equal((2, 0), MarkerSetController.SuggestionCounts(state));
    }

    [Fact]
    public void BreaksAtTheEdgesBackwardOrOverlappingAreDropped()
    {
        var state = MarkerSetController.FromAnalysis(new CommercialDetectionResult(
        [
            Break(0, 100_000_000), Break(1_700_000_000, Duration), Break(500_000_000, 400_000_000),
            Break(600_000_000, 780_000_000), Break(700_000_000, 900_000_000),
        ]), Duration);
        Assert.Equal([600_000_000L, 780_000_000], state.Splits.Select(s => s.TimeUs));
    }

    [Fact]
    public void UncertainCandidatesAreRedMarkersOnlyWhereNothingSurerIsClose()
    {
        var result = new CommercialDetectionResult([Break(600_000_000, 780_000_000)])
        {
            UncertainBreaks =
            [
                Break(1_000_000_000, 1_100_000_000, endpoints: Confidence.Low),
                Break(650_000_000, 700_000_000, endpoints: Confidence.Low),   // overlaps the break
                Break(783_000_000, 1_500_000_000, endpoints: Confidence.Low), // starts within 5 s of a break end
                Break(200_000_000, 300_000_000, endpoints: Confidence.Medium), // not uncertain
            ],
        };
        var state = MarkerSetController.FromAnalysis(result, Duration);
        var red = state.Splits.Where(s => s.Confidence == Confidence.Low).ToArray();
        Assert.Equal([1_000_000_000L, 1_100_000_000, 1_500_000_000], red.Select(s => s.TimeUs));
        Assert.Equal("Possible commercial break (uncertain)", red[0].ChapterTitle);
        Assert.Null(red[2].ChapterTitle);
        Assert.Equal(state.Splits.OrderBy(s => s.TimeUs), state.Splits);
        Assert.Empty(state.ExcludedSegmentIds);
        Assert.Equal((1, 3), MarkerSetController.SuggestionCounts(state));
    }

    [Fact]
    public void AnEarlierProjectKeepsItsMarkersAndOnlyACurrentBaseline()
    {
        var earlier = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, "x.mp4", new(1, 1, Duration),
            new MediaInfo(Duration, "h264", 1, 1, new(30, 1), [], -1),
            [new PersistedSplit("a", 600_000_000, SplitSource.Manual, null, null, true, "Break")], ["segment-0-600000000"],
            SidecarLocation.NextToSource, "Intro", MarkerSetSource.Imported, "v1:old");
        var state = MarkerSetController.FromEarlierProject(earlier);
        Assert.Equal("Intro", state.StartChapterTitle);
        Assert.Equal(MarkerSetSource.Imported, state.Source);
        Assert.Null(state.BaselineId);
        Assert.Equal("v2:current", MarkerSetController.FromEarlierProject(earlier with { MarkerBaselineId = "v2:current" }).BaselineId);
    }

    [Fact]
    public void EditsAreOnlyDiscardedAfterAskingAndTheQuestionNamesBothSets()
    {
        var vm = EditingRegressionTests.Project();
        vm.MarkerSetSource = MarkerSetSource.Imported;
        vm.MarkerBaselineId = vm.CurrentMarkerSetId();
        Assert.Null(MarkerSetController.DiscardQuestion(vm, MarkerSetSource.AdTrim));
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        var (message, title) = MarkerSetController.DiscardQuestion(vm, MarkerSetSource.AdTrim)!.Value;
        Assert.Equal("Discard edits to imported markers?", title);
        Assert.Contains("your edits to imported markers", message);
        Assert.EndsWith("then loads the original AdTrim suggested markers.", message);
    }

    [Fact]
    public void LoadingSuggestionsSaysWhatWasFoundAndThatNothingWasExcluded()
    {
        var state = MarkerSetController.FromAnalysis(new CommercialDetectionResult([Break(600_000_000, 780_000_000)]), Duration);
        Assert.Equal("1 possible ad break, 0 uncertain red markers. All video remains included; review before excluding.",
            MarkerSetController.AppliedStatus(state, "Load"));
        Assert.Equal("Revert", MarkerSetController.AppliedStatus(state with { Source = MarkerSetSource.Imported }, "Revert"));
        var banner = MarkerSetController.AnalysisFinished(state, TimeSpan.FromSeconds(95));
        Assert.Equal("Ad-break analysis finished in 1 min 35 s.", banner.Title);
        Assert.StartsWith("Found 1 ad break and 0 uncertain red markers. Nothing was excluded;", banner.Body);
    }

    // One analysis per window: a new run cancels the old one, and a finished old run cannot clear the new one.
    [Fact]
    public void ANewAnalysisReplacesTheOldRun()
    {
        var markers = new MarkerSetController(new MarkerSuggestionStore(Path.GetTempPath()));
        using var first = markers.BeginAnalysis(CancellationToken.None);
        using var second = markers.BeginAnalysis(CancellationToken.None);
        Assert.True(first.IsCancellationRequested);
        Assert.True(markers.IsCurrent(second) && markers.IsAnalyzing);
        markers.EndAnalysis(first);
        Assert.True(markers.IsAnalyzing);
        markers.EndAnalysis(second);
        Assert.False(markers.IsAnalyzing);
    }
}
