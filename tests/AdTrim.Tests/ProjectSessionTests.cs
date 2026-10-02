using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class ProjectSessionTests
{
    private static AdTrimProject Snapshot(int version) => new(version, "source", new(0, 0, 1),
        new(1, "h264", 1, 1, new(30, 1), Array.Empty<AudioStream>(), -1), new(), new(), SidecarLocation.NextToSource);

    [Fact]
    public async Task PendingSaves_AreWrittenInOrderWithoutConcurrentFileWriters()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var written = new List<int>();
        var session = new ProjectSession(snapshot =>
        {
            if (snapshot.SchemaVersion == 1) { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            written.Add(snapshot.SchemaVersion);
            return "saved";
        });
        var first = session.SaveAsync(new ProjectStore(), Snapshot(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = session.SaveAsync(new ProjectStore(), Snapshot(2));
        Assert.False(second.IsCompleted);
        release.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { 1, 2 }, written);
    }

    [Fact]
    public async Task FailedSave_DoesNotBlockRetry()
    {
        int attempt = 0;
        var session = new ProjectSession(_ => ++attempt == 1 ? throw new IOException("Unavailable") : "saved");
        await Assert.ThrowsAsync<IOException>(() => session.SaveAsync(new ProjectStore(), Snapshot(2)));
        Assert.Equal("saved", await session.SaveAsync(new ProjectStore(), Snapshot(2)));
    }
}
