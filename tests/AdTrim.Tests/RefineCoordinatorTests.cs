using AdTrim.Models;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// Refinement may move a marker only within its neighbors, never touches confirmed or bookend markers
// unless asked, is one undo step, and changes nothing when it is cancelled or fails.
public sealed class RefineCoordinatorTests
{
    private readonly List<(long Time, long Min, long Max)> _asked = new();

    private RefineCoordinator Refiner(Func<long, RefineResult?> answer, Action? during = null)
        => new(() => (path, time, min, max, ct, audio) =>
        {
            _asked.Add((time, min, max));
            during?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(answer(time));
        }, () => 1);

    private static RefineResult Moved(long from, long to, Confidence confidence = Confidence.High) => new("x", from, to, confidence, 1, 1);

    private static MainViewModel Project()
    {
        var vm = EditingRegressionTests.Project();
        vm.SourcePath = "show.mp4";
        vm.IsDirty = false;
        return vm;
    }

    [Fact]
    public async Task EveryEligibleMarkerMovesInOneUndoStepWithinItsNeighbors()
    {
        var vm = Project();
        await Refiner(t => Moved(t, t + 40_000)).RunAsync(vm, vm.Markers.ToList(), _ => throw new InvalidOperationException());
        // Bookends are skipped. The second marker's lower bound is where the first one moved to.
        Assert.Equal([(10_000_000L, 0L, 20_000_000L), (20_000_000L, 10_040_000L, 30_000_000L)], _asked);
        Assert.Equal([0L, 10_040_000, 20_040_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
        Assert.Equal(10_000_000, vm.Markers[1].OriginalTimeUs);
        Assert.False(vm.IsBusy);
        Assert.Equal("Refined 2 of 2 splits.", vm.Banner!.Title);
        Assert.Equal("2H · 0M · 0L · 0U", vm.RefineSummary);
        vm.CommandStack.Undo();
        Assert.Equal([0L, 10_000_000, 20_000_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
    }

    [Fact]
    public async Task ConfirmedMarkersAreSkippedUnlessASingleOneIsConfirmedByTheUser()
    {
        var vm = Project();
        vm.Markers[1].Confirmed = true;
        await Refiner(t => Moved(t, t + 40_000)).RunAsync(vm, vm.Markers.ToList(), _ => throw new InvalidOperationException());
        Assert.Equal([20_000_000L], _asked.Select(a => a.Time));

        var declined = Project();
        declined.Markers[1].Confirmed = true;
        _asked.Clear();
        await Refiner(t => Moved(t, t + 40_000)).RunAsync(declined, [declined.Markers[1]], _ => false);
        Assert.Empty(_asked);
        Assert.Equal("Nothing to refine.", declined.Banner!.Title);

        var accepted = Project();
        accepted.Markers[1].Confirmed = true;
        await Refiner(t => Moved(t, t + 40_000)).RunAsync(accepted, [accepted.Markers[1]], _ => true);
        Assert.Equal(10_040_000, accepted.Markers[1].TimeUs);
        Assert.StartsWith("Refined to 00:10.040", accepted.Banner!.Title);
        Assert.Equal("Moved +40.0 ms from 00:10.000.", accepted.Banner.Body);
    }

    [Fact]
    public async Task UnchangedResultsLeaveMarkersAndSaySo()
    {
        var vm = Project();
        await Refiner(t => t == 10_000_000 ? null : new RefineResult("x", t, t, Confidence.Unchanged, 0, 0))
            .RunAsync(vm, vm.Markers.ToList(), _ => true);
        Assert.Equal([0L, 10_000_000, 20_000_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
        Assert.False(vm.CommandStack.CanUndo);
        Assert.Equal("No movement found for 2 splits.", vm.Banner!.Title);
        Assert.Equal("0H · 0M · 0L · 2U", vm.RefineSummary);
    }

    [Fact]
    public async Task ACancelledPassChangesNothingAndReleasesTheEditor()
    {
        var vm = Project();
        RefineCoordinator? coordinator = null;
        coordinator = Refiner(t => Moved(t, t + 40_000), () => coordinator!.Cancel());
        await coordinator.RunAsync(vm, vm.Markers.ToList(), _ => true);
        Assert.Equal([0L, 10_000_000, 20_000_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
        Assert.Equal("Refine cancelled.", vm.Banner!.Title);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.BusyOperation);
    }

    [Fact]
    public async Task OpeningAnotherRecordingDuringAPassCancelsIt()
    {
        var vm = Project();
        await Refiner(t => Moved(t, t + 40_000), () => vm.SourcePath = "other.mp4").RunAsync(vm, vm.Markers.ToList(), _ => true);
        Assert.Equal([0L, 10_000_000, 20_000_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
        Assert.Equal("Refine cancelled.", vm.Banner!.Title);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AFailedPassChangesNothingAndSaysWhere()
    {
        var vm = Project();
        await Refiner(t => t == 20_000_000 ? throw new IOException("ffmpeg exited") : Moved(t, t + 40_000))
            .RunAsync(vm, vm.Markers.ToList(), _ => true);
        Assert.Equal([0L, 10_000_000, 20_000_000, 30_000_000], vm.Markers.Select(m => m.TimeUs));
        Assert.Equal(StatusKind.Danger, vm.Banner!.Kind);
        Assert.Equal("Refine failed on split #2.", vm.Banner.Title);
        Assert.Equal("ffmpeg exited", vm.Banner.Body);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task WithoutFFmpegRefineSaysItIsUnavailable()
    {
        var vm = Project();
        await new RefineCoordinator(() => null, () => null).RunAsync(vm, vm.Markers.ToList(), _ => true);
        Assert.Equal("Refine unavailable.", vm.Banner!.Title);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(61_250_000, "01:01.250")]
    [InlineData(3_723_004_000, "1:02:03.004")]
    public void TimesShowHoursOnlyWhenNeeded(long us, string text) => Assert.Equal(text, RefineCoordinator.FormatTimeMs(us));
}
