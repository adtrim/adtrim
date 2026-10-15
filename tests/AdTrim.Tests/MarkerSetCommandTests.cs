using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

public sealed class MarkerSetCommandTests
{
    [Fact]
    public void SwitchingAndUndoRestoreEditsNamesExclusionsAndSource()
    {
        var vm = new MainViewModel { DurationUs = 100_000_000 };
        vm.Markers.Add(new Split { TimeUs = 0, Label = "Start" });
        vm.Markers.Add(new Split { TimeUs = 20_000_000, ChapterTitle = "Imported act", Source = SplitSource.Chapter });
        vm.Markers.Add(new Split { TimeUs = 100_000_000, Label = "End" });
        vm.MarkerSetSource = MarkerSetSource.Imported;
        vm.RebuildSegmentsFromSplits();
        vm.MarkerBaselineId = vm.CurrentMarkerSetId();

        var adTrim = new MarkerSetState(
            new[]
            {
                new PersistedSplit("start-ad", 40_000_000, SplitSource.Manual, null, Confidence.High, false, "Commercial break"),
                new PersistedSplit("end-ad", 60_000_000, SplitSource.Manual, null, Confidence.High, false, "Program resumes"),
            },
            new[] { "segment-40000000-60000000" }, null, MarkerSetSource.AdTrim, "baseline-ad");

        vm.CommandStack.Execute(new ReplaceMarkerSetCommand(vm, adTrim, "Use original AdTrim suggestions"));
        Assert.Equal(MarkerSetSource.AdTrim, vm.MarkerSetSource);
        Assert.Equal(new long[] { 0, 40_000_000, 60_000_000, 100_000_000 }, vm.Markers.Select(m => m.TimeUs));
        Assert.True(Assert.Single(vm.Segments, s => s.IsExcluded).IsExcluded);

        Assert.True(vm.CommandStack.Undo());
        Assert.Equal(MarkerSetSource.Imported, vm.MarkerSetSource);
        Assert.Equal("Imported act", vm.Markers.Single(m => m.TimeUs == 20_000_000).ChapterTitle);
        Assert.DoesNotContain(vm.Segments, s => s.IsExcluded);

        Assert.True(vm.CommandStack.Redo());
        Assert.Equal(MarkerSetSource.AdTrim, vm.MarkerSetSource);
        Assert.Single(vm.Segments, s => s.IsExcluded);
    }

    [Fact]
    public void ResetLeavesOneIncludedSectionAndCanBeUndone()
    {
        var vm = new MainViewModel { DurationUs = 100_000_000 };
        vm.Markers.Add(new Split { TimeUs = 0, Label = "Start" });
        vm.Markers.Add(new Split { TimeUs = 20_000_000, ChapterTitle = "Credits" });
        vm.Markers.Add(new Split { TimeUs = 100_000_000, Label = "End" });
        vm.RebuildSegmentsFromSplits();
        vm.Segments[0].State = SegmentState.Excluded;

        vm.CommandStack.Execute(new ClearAllMarkingsCommand(vm));
        Assert.Equal(MarkerSetSource.Blank, vm.MarkerSetSource);
        Assert.DoesNotContain(vm.Markers, m => !m.IsBookend);
        Assert.DoesNotContain(vm.Segments, s => s.IsExcluded);
        Assert.Single(vm.Segments);
        Assert.False(vm.IsTimelineEdited);

        vm.CommandStack.Execute(new AddSplitCommand(vm, 50_000_000, SplitSource.Manual));
        Assert.True(vm.IsTimelineEdited);

        vm.CommandStack.Undo();
        vm.CommandStack.Undo();
        Assert.Equal("Credits", vm.Markers.Single(m => !m.IsBookend).ChapterTitle);
        Assert.True(Assert.Single(vm.Segments, s => s.IsExcluded).IsExcluded);
    }
}
