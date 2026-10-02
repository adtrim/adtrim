using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class ExportDiskGuardTests
{
    [Fact]
    public void ExportCannotStartWhenDestinationIsAlreadyLowOnSpace()
    {
        Assert.Throws<ExportException>(() => new ExportDiskGuard(new[] { "temp", "output" }, default,
            path => path == "output" ? ExportDiskGuard.ReserveBytes - 1 : long.MaxValue));
    }

    [Theory]
    [InlineData("temp")]
    [InlineData("output")]
    public async Task SpaceLossOnEitherDriveCancelsEncoding(string lowPath)
    {
        int low = 0;
        await using var guard = new ExportDiskGuard(new[] { "temp", "output" }, default,
            path => Volatile.Read(ref low) == 1 && path == lowPath ? 0 : long.MaxValue, TimeSpan.FromMilliseconds(10));
        Interlocked.Exchange(ref low, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, guard.Token));
        Assert.Contains(lowPath, guard.Failure);
        Assert.Contains("disk space is low", guard.Failure);
    }

    [Fact]
    public async Task UserCancellationIsNotMisreportedAsLowDiskSpace()
    {
        using var cts = new CancellationTokenSource();
        await using var guard = new ExportDiskGuard(new[] { "temp" }, cts.Token, _ => long.MaxValue);
        cts.Cancel();
        Assert.True(guard.Token.IsCancellationRequested);
        Assert.Null(guard.Failure);
    }
}
