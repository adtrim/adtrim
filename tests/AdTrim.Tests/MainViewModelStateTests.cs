using AwesomeAssertions;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

public class MainViewModelStateTests
{
    [Fact]
    public void ConfirmProgressLabel_UsesActualInternalSplitCount()
    {
        var vm = new MainViewModel();
        vm.Markers.Add(new Split { TimeUs = 0, Label = "Start", Confirmed = true });
        vm.Markers.Add(new Split { TimeUs = 1_000_000, Confirmed = true });
        vm.Markers.Add(new Split { TimeUs = 2_000_000, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = 3_000_000, Label = "End", Confirmed = true });

        vm.ConfirmProgressLabel.Should().Be("1 / 2");
        vm.ConfirmProgressPercent.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void ExpectedOutputDurationUs_ExcludesMarkedSegments()
    {
        var vm = new MainViewModel { DurationUs = 3_000_000 };
        vm.Markers.Add(new Split { TimeUs = 0, Label = "Start", Confirmed = true });
        vm.Markers.Add(new Split { TimeUs = 1_000_000 });
        vm.Markers.Add(new Split { TimeUs = 3_000_000, Label = "End", Confirmed = true });
        vm.RebuildSegmentsFromSplits();

        vm.CommandStack.Execute(new ToggleExcludedCommand(vm.Segments[0]));

        vm.ExpectedOutputDurationUs.Should().Be(2_000_000);
    }

    [Fact]
    public void SetConfirmedCommand_RoundtripsAsSingleUndoableCommand()
    {
        var splits = new[]
        {
            new Split { Confirmed = false },
            new Split { Confirmed = true },
        };
        var command = new SetConfirmedCommand(splits, confirmed: true);

        command.Do();
        splits.Should().OnlyContain(s => s.Confirmed);

        command.Undo();
        splits[0].Confirmed.Should().BeFalse();
        splits[1].Confirmed.Should().BeTrue();
    }

    [Fact]
    public void ThumbnailService_TimeForTile_UsesMidpointsAndClamps()
    {
        ThumbnailService.TimeForTile(0, 4, 4_000_000).Should().Be(500_000);
        ThumbnailService.TimeForTile(3, 4, 4_000_000).Should().Be(3_500_000);
        ThumbnailService.TimeForTile(0, 0, 4_000_000).Should().Be(0);
    }
}
