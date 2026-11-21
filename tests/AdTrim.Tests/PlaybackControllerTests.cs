using System.Windows.Input;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// The playhead must move at once on every seek so chained keys add up, never leave the recording,
// and follow the kept timeline when excluded scenes are collapsed. The shuttle and rewind must stop
// cleanly. The players are not started here, so these check the controller's own rules.
public sealed class PlaybackControllerTests
{
    private sealed class Screen : IPlaybackScreen
    {
        public MainViewModel? Current { get; set; }
        public MediaInfo? Media => null;
        public FfmpegRunner? Ffmpeg => null;
        public bool Closing { get; set; }
        public bool Switching { get; set; }
        public bool Analyzing { get; set; }
        public List<string> Notices { get; } = new();
        public void Notice(MainViewModel vm, StatusKind kind, string message) => Notices.Add(message);
        public void UpdateNoFrameOverlay(MainViewModel vm) { }
        public void ShowFrameFailure(MainViewModel vm, string status) { }
    }

    private readonly Screen _screen = new();

    private MainViewModel Recording()
    {
        var vm = EditingRegressionTests.Project();
        vm.SourcePath = "show.mp4";
        vm.FrameRate = new Rational(30, 1);
        _screen.Current = vm;
        return vm;
    }

    [Fact]
    public void ASeekMovesThePlayheadAtOnceAndStaysInTheRecording()
    {
        var vm = Recording();
        var playback = new PlaybackController(_screen);
        playback.SeekTo(vm, 12_000_000);
        Assert.Equal(12_000_000, vm.PlayheadUs);
        playback.SeekTo(vm, -5);
        Assert.Equal(0, vm.PlayheadUs);
        playback.SeekTo(vm, 99_000_000);
        Assert.Equal(vm.DurationUs, vm.PlayheadUs);
    }

    // With excluded scenes collapsed, a seek into an excluded scene lands on the kept video after it.
    [Fact]
    public void ACollapsedSeekFollowsTheKeptTimeline()
    {
        var vm = Recording();
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        vm.IsCollapsed = true;
        new PlaybackController(_screen).SeekTo(vm, 15_000_000);
        Assert.Equal(20_000_000, vm.PlayheadUs);
    }

    [Theory]
    [InlineData(30, 1, 33_333)]
    [InlineData(30000, 1001, 33_367)]
    [InlineData(0, 1, 33_366)]
    public void AFrameIsTheRecordingsFrameDuration(int numerator, int denominator, long expected)
    {
        var vm = new MainViewModel { FrameRate = new Rational(numerator, denominator) };
        Assert.Equal(expected, PlaybackController.FrameDurationUs(vm));
    }

    [Fact]
    public void JRewindsFasterEachPressUpToFourTimesAndKStops()
    {
        var vm = Recording();
        var playback = new PlaybackController(_screen);
        foreach (var expected in new[] { -1, -2, -4, -4 })
        {
            playback.Shuttle(vm, Key.J);
            Assert.Equal(expected, playback.ShuttleRate);
        }
        Assert.True(playback.RewindTimer!.IsEnabled);
        Assert.Equal("Rewinding 4×. K stops.", _screen.Notices[^1]);
        playback.Shuttle(vm, Key.K);
        Assert.Equal(0, playback.ShuttleRate);
        Assert.False(playback.RewindTimer.IsEnabled);
        Assert.Equal("Paused.", _screen.Notices[^1]);
    }

    [Fact]
    public void EachRewindTickStepsThePlayheadBackByTheRate()
    {
        var vm = Recording();
        var playback = new PlaybackController(_screen);
        playback.SeekTo(vm, 10_000_000);
        playback.Shuttle(vm, Key.J);
        playback.Shuttle(vm, Key.J);
        playback.RewindTick();
        Assert.Equal(9_800_000, vm.PlayheadUs);
        playback.SeekTo(vm, 100_000);
        playback.RewindTick();
        Assert.Equal(0, vm.PlayheadUs);
        playback.RewindTick();
        Assert.Equal(0, playback.ShuttleRate);
        Assert.Equal("Reached the start.", _screen.Notices[^1]);
    }

    // Rewinding must not keep moving a recording that is closing, switching or busy.
    [Fact]
    public void RewindStopsWhenTheWindowCannotPlay()
    {
        var vm = Recording();
        var playback = new PlaybackController(_screen);
        playback.SeekTo(vm, 10_000_000);
        playback.Shuttle(vm, Key.J);
        _screen.Switching = true;
        playback.RewindTick();
        Assert.Equal(10_000_000, vm.PlayheadUs);
        Assert.Equal(0, playback.ShuttleRate);
        Assert.False(playback.RewindTimer!.IsEnabled);
    }

    [Fact]
    public void ShuttleKeysWaitWhileTheEditorIsBusyExceptForAnalysis()
    {
        var vm = Recording();
        var playback = new PlaybackController(_screen);
        vm.IsBusy = true;
        playback.Shuttle(vm, Key.J);
        Assert.Equal(0, playback.ShuttleRate);
        _screen.Analyzing = true;
        playback.Shuttle(vm, Key.J);
        Assert.Equal(-1, playback.ShuttleRate);
        playback.StopRewind();
    }

    [Fact]
    public void PausingClearsAPlayUntilPoint()
    {
        Recording();
        var playback = new PlaybackController(_screen) { PlayUntilUs = 20_000_000 };
        playback.PlayPauseBoth();
        Assert.Null(playback.PlayUntilUs);
    }
}
