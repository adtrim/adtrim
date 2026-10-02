using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

public class EditingRegressionTests
{
    internal static MainViewModel Project()
    {
        var vm = new MainViewModel { DurationUs = 30_000_000 };
        vm.Markers.Add(new Split { Id = "start", TimeUs = 0, Label = "Start" });
        vm.Markers.Add(new Split { Id = "a", TimeUs = 10_000_000 });
        vm.Markers.Add(new Split { Id = "b", TimeUs = 20_000_000 });
        vm.Markers.Add(new Split { Id = "end", TimeUs = 30_000_000, Label = "End" });
        vm.RebuildSegmentsFromSplits();
        return vm;
    }

    [Fact]
    public void Rename_FollowsBoundaryThroughMovesAndUndoRestoresImportedName()
    {
        var vm = Project();
        vm.Markers[1].ChapterTitle = "Imported chapter";
        vm.RebuildSegmentsFromSplits();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.IsDirty = false;
        vm.CommandStack.Execute(new RenameSegmentCommand(vm, vm.Segments[1], "  Act two  "));
        Assert.True(vm.IsDirty);
        Assert.Equal("Act two", vm.Markers[1].ChapterTitle);
        Assert.Equal("Act two", vm.Segments[1].Label);
        Assert.True(vm.Segments[1].IsExcluded);
        vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[1], 10_000_000, 11_000_000));
        Assert.Equal("Act two", vm.Segments[1].Label);
        vm.CommandStack.Undo();
        vm.CommandStack.Undo();
        Assert.Equal("Imported chapter", vm.Segments[1].Label);
        vm.CommandStack.Redo();
        Assert.Equal("Act two", vm.Segments[1].Label);
    }

    [Fact]
    public void RenameFirstSegment_UndoRestoresDefaultAndBlankNamesAreRejected()
    {
        var vm = Project();
        Assert.Throws<ArgumentException>(() => new RenameSegmentCommand(vm, vm.Segments[0], "  "));
        vm.CommandStack.Execute(new RenameSegmentCommand(vm, vm.Segments[0], "Opening"));
        Assert.Equal("Start", vm.Markers[0].Label);
        Assert.Equal("Opening", vm.Segments[0].Label);
        vm.CommandStack.Undo();
        Assert.Null(vm.Markers[0].ChapterTitle);
        Assert.Equal("Part 1", vm.Segments[0].Label);
    }

    [Fact]
    public void ExcludeMoveUndoRedo_PreservesDecisionsAndSavedIds()
    {
        var vm = Project();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[1], 10_000_000, 11_000_000));
        Assert.True(vm.Segments[1].IsExcluded);
        Assert.Equal(21_000_000, vm.ExpectedOutputDurationUs);
        Assert.Equal(new[] { "segment-11000000-20000000" }, vm.ExcludedSegmentIds);
        vm.CommandStack.Undo();
        Assert.Equal(20_000_000, vm.ExpectedOutputDurationUs);
        vm.CommandStack.Undo();
        Assert.Empty(vm.ExcludedSegmentIds);
        Assert.All(vm.Segments, s => Assert.False(s.IsExcluded));
        vm.CommandStack.Redo();
        vm.CommandStack.Redo();
        Assert.True(vm.Segments[1].IsExcluded);
        Assert.Equal(new[] { "segment-11000000-20000000" }, vm.ExcludedSegmentIds);
    }

    [Fact]
    public void SplittingExcludedScene_ExcludesBothChildrenAndUndoRestoresParent()
    {
        var vm = Project();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.CommandStack.Execute(new AddSplitCommand(vm, 15_000_000, SplitSource.Manual));
        Assert.Equal(2, vm.Segments.Count(s => s.IsExcluded));
        Assert.Equal(20_000_000, vm.ExpectedOutputDurationUs);
        vm.CommandStack.Undo();
        vm.CommandStack.Undo();
        Assert.Empty(vm.ExcludedSegmentIds);
    }

    [Fact]
    public void MixedMerge_KeepsFootageAndUndoRestoresExclusion()
    {
        var vm = Project();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.CommandStack.Execute(new DeleteSplitCommand(vm, vm.Markers[1]));
        Assert.Equal(30_000_000, vm.ExpectedOutputDurationUs);
        Assert.NotNull(vm.StatusOverride);
        vm.CommandStack.Undo();
        Assert.Equal(20_000_000, vm.ExpectedOutputDurationUs);
        Assert.True(vm.Segments[1].IsExcluded);
        vm.CommandStack.Redo();
        Assert.Equal(30_000_000, vm.ExpectedOutputDurationUs);
    }

    [Fact]
    public void RefinementAndUndo_PreserveExcludedBoundaryIdentity()
    {
        var vm = Project();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.CommandStack.Execute(new BatchedRefineCommand(vm, new[] {
            new RefineMutation(vm.Markers[1], 10_000_000, 10_033_333, null, Confidence.High, null, 10_000_000) }));
        Assert.True(vm.Segments[1].IsExcluded);
        vm.CommandStack.Undo();
        vm.CommandStack.Undo();
        Assert.Empty(vm.ExcludedSegmentIds);
    }

    [Fact]
    public void DiscardedSegmentsAndMarkers_CannotDirtyCurrentProject()
    {
        var vm = Project();
        var old = vm.Segments[0];
        vm.RebuildSegmentsFromSplits();
        vm.IsDirty = false;
        old.State = SegmentState.Excluded;
        Assert.False(vm.IsDirty);
        var marker = vm.Markers[1];
        vm.Markers.Clear();
        vm.IsDirty = false;
        marker.TimeUs++;
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Collapsing_IsPresentationOnly()
    {
        var vm = Project();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.IsDirty = false;
        long revision = vm.Revision;
        vm.IsCollapsed = true;
        Assert.Equal(20_000_000, vm.TimelineDurationUs);
        Assert.Equal(20_000_000, vm.KeptTimeline.ToSource(10_000_000));
        Assert.Equal(10_000_000, vm.KeptTimeline.ToOutput(15_000_000));
        vm.IsCollapsed = false;
        Assert.Equal(revision, vm.Revision);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void KeptMap_MergesAdjacentKeptSpansAndHandlesEmptyOutput()
    {
        var vm = Project();
        Assert.Single(vm.KeptTimeline.Spans);
        foreach (var s in vm.Segments.ToArray()) vm.CommandStack.Execute(new ToggleExcludedCommand(vm, s));
        Assert.Empty(vm.KeptTimeline.Spans);
        Assert.Equal(0, vm.KeptTimeline.ToSource(100));
        Assert.Equal(0, vm.KeptTimeline.ToOutput(100));
    }

    [Fact]
    public void Edl_EscapesPathAndUsesExactMicroseconds()
    {
        var map = new KeptTimeline(new[] { new Segment { StartUs = 1_234_567, EndUs = 2_345_678 } });
        Assert.Equal("edl://%6%a,b.mp,1.234567,1.111111", map.ToMpvEdl("a,b.mp"));
        Assert.StartsWith("edl://%2%", map.ToMpvEdl("é"));
    }
}
