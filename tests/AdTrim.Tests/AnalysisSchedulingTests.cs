using System.IO;
using AwesomeAssertions;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

/// <summary>Analyses share one process-wide slot; tests that run them must not overlap.</summary>
[CollectionDefinition(Name)]
public sealed class AnalysisGateCollection { public const string Name = "Analysis gate"; }

[Collection(AnalysisGateCollection.Name)]
public sealed class AnalysisSchedulingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AdtSchedule-" + Guid.NewGuid().ToString("n")[..8]);

    public AnalysisSchedulingTests() => Directory.CreateDirectory(_dir);

    private sealed class Recorder : IProgress<CommercialDetectionProgress>
    {
        public readonly List<CommercialDetectionProgress> Reports = new();
        public void Report(CommercialDetectionProgress value) { lock (Reports) Reports.Add(value); }
    }

    private static MediaInfo Media() => new(20_000_000, "h264", 320, 180, new(10, 1), [new(1, "aac", 2, true)], 1);

    [SkippableFact]
    public async Task OnlyAnAnalysisThatReallyWaitsSaysItIsWaiting()
    {
        var runner = AudioLevelServiceTests.Runner();
        var hold = new TaskCompletionSource<AudioLevels>();
        using var cancel = new CancellationTokenSource();
        var first = new Recorder();
        var second = new Recorder();

        var running = new SuggestionEvidenceCollector(runner).CollectAsync("a.mp4", Media(), first, cancel.Token,
            (_, ct) => hold.Task.WaitAsync(ct));
        var queued = new SuggestionEvidenceCollector(runner).CollectAsync("b.mp4", Media(), second, cancel.Token,
            (_, ct) => hold.Task.WaitAsync(ct));
        await Task.Delay(100);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        first.Reports.Should().NotContain(r => r.Queued, "nothing else was analyzing");
        second.Reports.Should().ContainSingle(r => r.Queued);
    }

    [SkippableFact]
    public async Task PicturePassesReadNothingWhilePlaybackHoldsTheDisk()
    {
        var runner = AudioLevelServiceTests.Runner();
        var path = Path.Combine(_dir, "picture.mp4");
        var created = await runner.RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "color=gray:s=320x180:r=10:d=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-an", path]);
        Assert.True(created.Success, created.Stderr);
        var media = await new MediaProbeService(runner).ProbeAsync(path);
        var idle = new TaskCompletionSource();
        var progress = new Recorder();

        var analysis = new SuggestionEvidenceCollector(runner).CollectAsync(path, media, progress, CancellationToken.None,
            (_, _) => Task.FromResult(new AudioLevels([], [])), ct => new ValueTask(idle.Task.WaitAsync(ct)));
        await Task.Delay(500);

        analysis.IsCompleted.Should().BeFalse();
        progress.Reports.Should().NotContain(r => r.Stage != null && r.Stage.StartsWith("Step 2") && r.TimeUs > 0.10 * r.DurationUs,
            "no analysis frame is read while scrubbing holds the disk");

        idle.SetResult();
        var evidence = await analysis;
        evidence.Appearance.Should().NotBeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
