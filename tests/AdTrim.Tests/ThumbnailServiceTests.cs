using System.IO;
using System.Security.Cryptography;
using AwesomeAssertions;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "AdtThumbs-" + Guid.NewGuid().ToString("n")[..8]);

    public ThumbnailServiceTests() => Directory.CreateDirectory(_testDir);

    [Fact]
    public void Strip_NearestPicksTheClosestFrameAndTheEarlierOnATie()
    {
        var strip = ThumbnailStrip.Empty.With([(10_000_000, "a"), (20_000_000, "b"), (40_000_000, "c")]);

        ThumbnailStrip.Empty.Nearest(5).Should().BeNull();
        strip.Nearest(0)!.Value.Path.Should().Be("a");
        strip.Nearest(14_000_000)!.Value.Path.Should().Be("a");
        strip.Nearest(15_000_000)!.Value.Path.Should().Be("a");
        strip.Nearest(31_000_000)!.Value.Path.Should().Be("c");
        strip.Nearest(99_000_000)!.Value.Path.Should().Be("c");
        strip.With([(20_000_000, "b2")]).Nearest(20_000_000)!.Value.Path.Should().Be("b2");
        strip.Count.Should().Be(3, "adding returns a new strip and leaves the old one as it was");
    }

    [Fact]
    public void Snap_RoundsToWholeSecondsWithinTheRecording()
    {
        ThumbnailService.Snap(1_400_000, 60_000_000).Should().Be(1_000_000);
        ThumbnailService.Snap(1_600_000, 60_000_000).Should().Be(2_000_000);
        ThumbnailService.Snap(-5, 60_000_000).Should().Be(0);
        ThumbnailService.Snap(59_900_000, 60_000_000).Should().Be(59_000_000);
    }

    private sealed class FakeExtract
    {
        public readonly List<IReadOnlyList<long>> Batches = new();
        public Task<IReadOnlyList<(long, string)>> Run(IReadOnlyList<long> times, CancellationToken ct)
        {
            lock (Batches) Batches.Add(times);
            return Task.FromResult<IReadOnlyList<(long, string)>>(times.Where(t => t != 13_000_000).Select(t => (t, $"{t}.jpg")).ToList());
        }
    }

    private static ThumbnailLoader Loader(FakeExtract extract, Func<CancellationToken, ValueTask> idle,
        List<(long, string)> delivered, params long[] known)
        => new(known, t => ThumbnailService.Snap(t, 3_600_000_000), extract.Run, idle,
            tiles => { lock (delivered) delivered.AddRange(tiles); });

    [Fact]
    public async Task Loader_FetchesInSmallBatchesAndNeverFetchesATileTwice()
    {
        // Small batches bound how long a scrub can wait for the disk; a known or failed tile costs nothing again.
        var extract = new FakeExtract();
        var delivered = new List<(long, string)>();
        var loader = Loader(extract, _ => ValueTask.CompletedTask, delivered, 1_000_000);
        var times = Enumerable.Range(1, 20).Select(s => s * 1_000_000L).ToList();

        loader.Request(times);
        await loader.Idle;
        loader.Request(times);
        await loader.Idle;

        extract.Batches.Should().OnlyContain(b => b.Count <= ThumbnailLoader.BatchSize);
        extract.Batches.SelectMany(b => b).Should().OnlyHaveUniqueItems().And.HaveCount(19).And.NotContain(1_000_000);
        delivered.Select(d => d.Item1).Should().HaveCount(18).And.NotContain(13_000_000);
        loader.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Loader_WaitsForPlaybackBeforeEachBatchAndTheNewestRequestWins()
    {
        // While the user scrubs nothing is fetched; after a jump, the old screenful is not fetched.
        var extract = new FakeExtract();
        var delivered = new List<(long, string)>();
        var idle = new TaskCompletionSource();
        var loader = Loader(extract, _ => new ValueTask(idle.Task), delivered);

        loader.Request(Enumerable.Range(1, 10).Select(s => s * 1_000_000L));
        loader.IsBusy.Should().BeTrue();
        await Task.Delay(50);
        extract.Batches.Should().BeEmpty();

        loader.Request([500_000_000, 501_000_000]);
        idle.SetResult();
        await loader.Idle;

        extract.Batches.SelectMany(b => b).Should().Equal(500_000_000, 501_000_000);
    }

    [Fact]
    public async Task Loader_AfterCancelFetchesNothingMore()
    {
        var extract = new FakeExtract();
        var loader = Loader(extract, _ => ValueTask.CompletedTask, new());
        loader.Cancel();
        loader.Request([1_000_000]);
        await loader.Idle;
        extract.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_OneRunWritesATileAtEachKeyframeAtTheVideosShape()
    {
        // 4:3 anamorphic SD stored at 720x480: the tile must show the display shape, not the storage shape.
        var runner = AudioLevelServiceTests.Runner();
        var path = Path.Combine(_testDir, "sd.mp4");
        var created = await runner.RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=720x480:r=30000/1001:d=60",
            "-vf", "setsar=8/9", "-c:v", "mpeg2video", "-g", "15", "-an", path]);
        Assert.True(created.Success, created.Stderr);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var service = new ThumbnailService(runner, Path.Combine(_testDir, "cache"));
        var times = new long[] { 5_000_000, 20_000_000, 40_000_000, 59_000_000 };

        var tiles = await service.ExtractAsync(path, 60_000_000, times);

        tiles.Select(t => t.TimeUs).Should().Equal(times);
        foreach (var (_, tile) in tiles)
        {
            var probe = await runner.RunFfprobeAsync(["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", tile]);
            var size = probe.Stdout.Trim().Split(',').Select(int.Parse).ToArray();
            size[1].Should().Be(ThumbnailService.TileHeight);
            (size[0] / (double)size[1]).Should().BeApproximately(4.0 / 3.0, 0.03);
        }
        service.LoadCached(path, 60_000_000).Times.Should().Equal(times);
        Directory.GetFiles(Path.Combine(_testDir, "cache"), "*.tmp.jpg", SearchOption.AllDirectories).Should().BeEmpty();
        SHA256.HashData(await File.ReadAllBytesAsync(path)).Should().Equal(hash);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }
}
