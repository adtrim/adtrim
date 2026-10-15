using AwesomeAssertions;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

public class MarkerSelectionTests
{
    private static MainViewModel Timeline()
    {
        var vm = new MainViewModel { DurationUs = 10_000_000 };
        vm.Markers.Add(new Split { TimeUs = 0, Label = "Start", Confirmed = true });
        foreach (long t in new[] { 2_000_000L, 4_000_000L, 6_000_000L })
            vm.Markers.Add(new Split { TimeUs = t });
        vm.Markers.Add(new Split { TimeUs = 10_000_000, Label = "End", Confirmed = true });
        vm.RebuildSegmentsFromSplits();
        return vm;
    }

    [Fact]
    public void SelectAll_SelectsEveryMarkerExceptTheBookends()
    {
        var vm = Timeline();
        vm.SelectAllMarkers();
        vm.SelectionKind.Should().Be(SelectionKind.Markers);
        vm.SelectedMarkerCount.Should().Be(3);
        vm.SelectedMarkers.Should().OnlyContain(m => !m.IsBookend && m.IsSelected);
        vm.Markers.Where(m => m.IsBookend).Should().OnlyContain(m => !m.IsSelected);
    }

    [Fact]
    public void SelectingOneMarkerOrClearingEndsTheMultipleSelection()
    {
        var vm = Timeline();
        vm.SelectAllMarkers();
        vm.SelectMarker(vm.Markers[2]);
        vm.SelectionKind.Should().Be(SelectionKind.Marker);
        vm.Markers.Count(m => m.IsSelected).Should().Be(1);
        vm.SelectAllMarkers();
        vm.ClearSelection();
        vm.Markers.Should().OnlyContain(m => !m.IsSelected);
        vm.SelectedMarkerCount.Should().Be(0);
    }

    [Fact]
    public void DeletingSeveralMarkersIsOneUndoStepThatRestoresPositionsAndExclusions()
    {
        var vm = Timeline();
        var before = vm.Markers.Select(m => m.TimeUs).ToList();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        var excluded = vm.CaptureExclusions();
        var doomed = new[] { vm.Markers[1], vm.Markers[3] };

        vm.CommandStack.Execute(new DeleteSplitsCommand(vm, doomed));
        vm.Markers.Select(m => m.TimeUs).Should().Equal(0L, 4_000_000L, 10_000_000L);

        vm.CommandStack.Undo();
        vm.Markers.Select(m => m.TimeUs).Should().Equal(before);
        vm.CaptureExclusions().Should().BeEquivalentTo(excluded);
    }

    [Fact]
    public void BookendsAreNeverDeletedEvenIfPassedIn()
    {
        var vm = Timeline();
        vm.CommandStack.Execute(new DeleteSplitsCommand(vm, vm.Markers.ToList()));
        vm.Markers.Should().HaveCount(2).And.OnlyContain(m => m.IsBookend);
    }

    [Fact]
    public void CtrlClickTogglesMarkersWithoutMovingThePlayhead()
    {
        var vm = Timeline();
        vm.SelectMarker(vm.Markers[1]);
        vm.PlayheadUs = 9_000_000;
        vm.ToggleMarkerSelection(vm.Markers[3]);
        vm.SelectedMarkers.Select(m => m.TimeUs).Should().Equal(2_000_000L, 6_000_000L);
        vm.PlayheadUs.Should().Be(9_000_000);
        vm.ToggleMarkerSelection(vm.Markers[1]);
        vm.SelectionKind.Should().Be(SelectionKind.Marker, "one remaining marker is an ordinary single selection");
        vm.SelectedMarker.Should().BeSameAs(vm.Markers[3]);
        vm.ToggleMarkerSelection(vm.Markers[3]);
        vm.SelectionKind.Should().Be(SelectionKind.None);
        vm.ToggleMarkerSelection(vm.Markers[0]);
        vm.SelectionKind.Should().Be(SelectionKind.None, "bookends are never selectable");
    }

    [Fact]
    public void ShiftClickSelectsEveryMarkerBetweenTheAnchorAndTheClickedMarker()
    {
        var vm = Timeline();
        vm.SelectMarker(vm.Markers[3]);
        vm.SelectMarkerRange(vm.Markers[1]);
        vm.SelectedMarkers.Select(m => m.TimeUs).Should().Equal(2_000_000L, 4_000_000L, 6_000_000L);
        vm.SelectMarkerRange(vm.Markers[2]);
        // The anchor stays at the first clicked marker.
        vm.SelectedMarkers.Select(m => m.TimeUs).Should().Equal(4_000_000L, 6_000_000L);
    }

    [Fact]
    public void SelectRedMarkersSelectsOnlyLowConfidenceOnes()
    {
        var vm = Timeline();
        vm.Markers[1].Confidence = Confidence.Low;
        vm.Markers[2].Confidence = Confidence.Medium;
        vm.Markers[3].Confidence = Confidence.Low;
        vm.SelectMarkersWhere(m => m.Confidence == Confidence.Low);
        vm.SelectedMarkers.Should().BeEquivalentTo(new[] { vm.Markers[1], vm.Markers[3] });
    }

    [Fact]
    public void ShortcutListDocumentsTheNewKeys()
    {
        var keys = KeyboardShortcuts.Groups.SelectMany(g => g.Items).Select(i => i.Keys).ToList();
        foreach (var expected in new[] { "J", "K", "L", "F1", "F2", "Ctrl+A", "Ctrl+Shift+A", "Ctrl+Alt+A", "Ctrl+click", "Shift+click",
            "Alt+← / Alt+→", "N / Shift+N", "Ctrl+Del", "Del", "M", "R" })
            keys.Should().Contain(expected);
    }

    [Fact]
    public void SelectionDropsMarkersThatLeaveTheTimeline()
    {
        // An undo or a replaced marker set must not leave stale, highlighted markers selected.
        var vm = Timeline();
        vm.SelectAllMarkers();
        var removed = vm.Markers[2];
        vm.Markers.Remove(removed);
        vm.SelectedMarkerCount.Should().Be(2);
        removed.IsSelected.Should().BeFalse();
        vm.Markers.Remove(vm.Markers[1]);
        vm.Markers.Remove(vm.Markers[1]);
        vm.SelectionKind.Should().Be(SelectionKind.None);
    }
}
