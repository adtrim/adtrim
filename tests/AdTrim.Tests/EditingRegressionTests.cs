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

    // Collapsed preview showed one frame of an excluded scene at a cut: the split time 24:48.310311 was
    // rounded up from the frame's true start, so the span ended just after that frame began.
    [Fact]
    public void Edl_EndsEachKeptSpanBeforeTheNextExcludedFrame()
    {
        // 29.97 fps frame 151 starts at 5,038,366.67 µs; its split is stored as 5,038,367.
        var map = new KeptTimeline(new[] { new Segment { StartUs = 0, EndUs = 5_038_367 }, new Segment { StartUs = 5_372_033, EndUs = 10_000_000 } });
        Assert.Equal("edl://%5%a.mp4,0,5.038366;%5%a.mp4,5.372033,4.627966", map.ToMpvEdl("a.mp4"));
    }

    [Fact]
    public void Edl_EscapesPathAndUsesExactMicroseconds()
    {
        var map = new KeptTimeline(new[] { new Segment { StartUs = 1_234_567, EndUs = 2_345_678 } });
        Assert.Equal("edl://%6%a,b.mp,1.234567,1.11111", map.ToMpvEdl("a,b.mp"));
        // mpv opens EDL entries itself, so a long entry uses the extended-length form and its byte count.
        string longPath = @"C:\" + string.Join(@"\", Enumerable.Repeat("A folder name long enough", 12)) + @"\é.mp4";
        string native = @"\\?\" + longPath;
        Assert.Equal($"edl://%{System.Text.Encoding.UTF8.GetByteCount(native)}%{native},1.234567,1.11111", map.ToMpvEdl(longPath));
        Assert.Equal("edl://%6%a,b.mp,122.634567,1.11111", map.ToMpvEdl("a,b.mp", 121_400_000));
        Assert.StartsWith("edl://%2%", map.ToMpvEdl("é"));
    }

    // Five sections of 10 s each.
    private static MainViewModel FiveSections()
    {
        var vm = new MainViewModel { DurationUs = 50_000_000 };
        vm.Markers.Add(new Split { Id = "start", TimeUs = 0, Label = "Start" });
        foreach (var (id, time) in new[] { ("a", 10), ("b", 20), ("c", 30), ("d", 40) })
            vm.Markers.Add(new Split { Id = id, TimeUs = time * 1_000_000L });
        vm.Markers.Add(new Split { Id = "end", TimeUs = 50_000_000, Label = "End" });
        vm.RebuildSegmentsFromSplits();
        return vm;
    }
    private static string?[] Labels(MainViewModel vm) => vm.Segments.Select(s => s.Label).ToArray();

    // The chapters of an export are the kept sections. Their numbers must not skip what was removed,
    // and a section the user named must not use one up.
    [Fact]
    public void UnnamedKeptSectionsNumberInOrderAroundRemovedAndNamedOnes()
    {
        var vm = FiveSections();
        Assert.Equal(new[] { "Part 1", "Part 2", "Part 3", "Part 4", "Part 5" }, Labels(vm));

        vm.CommandStack.Execute(new RenameSegmentCommand(vm, vm.Segments[1], "Intro"));
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[2]));
        Assert.Equal(new[] { "Part 1", "Intro", "Removed", "Part 2", "Part 3" }, Labels(vm));

        vm.CommandStack.Execute(new AddSplitCommand(vm, 45_000_000, SplitSource.Manual));
        Assert.Equal(new[] { "Part 1", "Intro", "Removed", "Part 2", "Part 3", "Part 4" }, Labels(vm));

        vm.CommandStack.Undo();
        vm.CommandStack.Undo();
        Assert.Equal(new[] { "Part 1", "Intro", "Part 2", "Part 3", "Part 4" }, Labels(vm));
    }

    [Fact]
    public void TheNameTemplateRenamesUnnamedSectionsWithoutMarkingTheProjectChanged()
    {
        var vm = FiveSections();
        vm.CommandStack.Execute(new RenameSegmentCommand(vm, vm.Segments[0], "Cold open"));
        vm.IsDirty = false;
        vm.SectionNameTemplate = "Chapter {n}";
        Assert.Equal(new[] { "Cold open", "Chapter 1", "Chapter 2", "Chapter 3", "Chapter 4" }, Labels(vm));
        Assert.False(vm.IsDirty);
        // Nothing usable falls back to the built-in name; it never produces identical chapters.
        vm.SectionNameTemplate = "Chapter";
        Assert.Equal("Part 1", vm.Segments[1].Label);
    }

    [Fact]
    public void ResetClearsOnlyNamesThatAreJustANumberAndUndoRestoresThem()
    {
        var vm = FiveSections();
        vm.SectionNameTemplate = "Chapter {n}";
        vm.Markers[0].ChapterTitle = "Part 7";
        vm.Markers[1].ChapterTitle = "Intro";
        vm.Markers[2].ChapterTitle = "chapter 12";
        vm.Markers[3].ChapterTitle = "Part 2 of the finale";
        vm.RebuildSegmentsFromSplits();

        var reset = new ResetNumberedNamesCommand(vm);
        Assert.Equal(2, reset.Count);
        vm.CommandStack.Execute(reset);
        Assert.Equal(new[] { "Chapter 1", "Intro", "Chapter 2", "Part 2 of the finale", "Chapter 3" }, Labels(vm));
        Assert.Null(vm.Markers[0].ChapterTitle);

        vm.CommandStack.Undo();
        Assert.Equal(new[] { "Part 7", "Intro", "chapter 12", "Part 2 of the finale", "Chapter 1" }, Labels(vm));
    }

    [Theory]
    [InlineData("Part {n}", true)]
    [InlineData("{n}. rész", true)]
    [InlineData("{n}", true)]
    [InlineData("Chapter", false)]
    [InlineData("{n} of {n}", false)]
    [InlineData("", false)]
    [InlineData("A name that is far too long to be a chapter title {n}", false)]
    public void ATemplateNeedsTheNumberExactlyOnce(string template, bool valid)
        => Assert.Equal(valid, AdTrim.Services.SectionNames.IsValid(template));
}
