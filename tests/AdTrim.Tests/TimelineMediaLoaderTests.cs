using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// The recording's audio is read once: the graph and ad-break analysis share one pass, and the pass
// is stopped, not leaked, when the recording changes or the disk is needed for something else.
public sealed class TimelineMediaLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-media-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly AudioLevelService _levels;
    private static readonly MediaInfo Media = new(10_000_000, "h264", 320, 180, new(30, 1), [new AudioStream(1, "ac3", 2, true)], 1);

    public TimelineMediaLoaderTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "source.ts");
        File.WriteAllBytes(_source, new byte[4096]);
        // Cached levels let a pass finish without FFmpeg.
        _levels = new AudioLevelService(runner: null, cacheRoot: Path.Combine(_directory, "cache"));
        _levels.SaveCached(_source, 1, Media.DurationUs, new AudioLevels([10, 20, 30], [true]));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private TimelineMediaLoader Loader(int[]? redraws = null)
        => new(() => null, () => false, update => update(), () => { if (redraws is not null) redraws[0]++; }) { AudioLevels = _levels };

    private static MainViewModel Recording() => new() { DurationUs = Media.DurationUs, SourcePath = "x" };

    [Fact]
    public async Task TheGraphFillsFromTheSharedPassAndTheSamePassIsReused()
    {
        var loader = Loader();
        var vm = Recording();
        var pass = loader.EnsureAudioPass(vm, _source, Media);
        await pass.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 100 && vm.AudioLoudness is null; i++) await Task.Delay(10);
        Assert.Equal([10, 20, 30], vm.AudioLoudness);
        Assert.Same(pass, loader.EnsureAudioPass(vm, _source, Media));
        Assert.Same(pass, loader.AudioPass);
    }

    [Fact]
    public async Task ReleasingStopsThePassAndClearsTheGraph()
    {
        var loader = Loader();
        var vm = Recording();
        await loader.EnsureAudioPass(vm, _source, Media).Completion.WaitAsync(TimeSpan.FromSeconds(10));
        loader.ReleaseAudioPass(vm);
        Assert.Null(loader.AudioPass);
        Assert.Null(vm.AudioLoudness);
    }

    // Export and source comments stop a pass that is still reading; a finished one costs nothing and stays.
    [Fact]
    public async Task OnlyAnUnfinishedPassIsDroppedForOtherDiskWork()
    {
        var loader = Loader();
        var vm = Recording();
        var finished = loader.EnsureAudioPass(vm, _source, Media);
        await finished.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        loader.DropUnfinishedAudioPass();
        await loader.StopUnfinishedAudioPassAsync();
        Assert.Same(finished, loader.AudioPass);
        loader.DropAudioPass();
        Assert.Null(loader.AudioPass);
    }

    [Fact]
    public async Task ADifferentRecordingGetsItsOwnPass()
    {
        var other = Path.Combine(_directory, "other.ts");
        File.WriteAllBytes(other, new byte[2048]);
        _levels.SaveCached(other, 1, Media.DurationUs, new AudioLevels([5], [false]));
        var loader = Loader();
        var vm = Recording();
        var first = loader.EnsureAudioPass(vm, _source, Media);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var second = loader.EnsureAudioPass(vm, other, Media);
        Assert.NotSame(first, second);
        Assert.Same(second, loader.AudioPass);
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WithoutFFmpegTheFilmstripIsClearedAndNothingLoads()
    {
        int[] redraws = [0];
        var loader = Loader(redraws);
        var vm = Recording();
        loader.StartThumbnailLoad(vm, _source, Media);
        Assert.Null(loader.ThumbnailLoader);
        Assert.Same(ThumbnailStrip.Empty, vm.ThumbnailTiles);
        Assert.Equal(0, redraws[0]);
        loader.RequestThumbnails([1_000_000]);
    }

    [Fact]
    public async Task FullReadsGoAheadWhenThePreviewIsIdle()
        => await Loader().WaitForFullReadTurnAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
}
