using System.IO;
using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

public sealed class WaveformServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sourcePath;

    public WaveformServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CseWave-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_testDir);

        _sourcePath = Path.Combine(_testDir, "source.ts");
        File.WriteAllBytes(_sourcePath, Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray());
        File.SetLastWriteTimeUtc(_sourcePath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void CachePathFor_SameInputs_IsStable()
    {
        var a = WaveformService.CachePathFor(_sourcePath, audioStreamIndex: 1, durationUs: 10_000_000, _testDir);
        var b = WaveformService.CachePathFor(_sourcePath, audioStreamIndex: 1, durationUs: 10_000_000, _testDir);

        b.Should().Be(a);
    }

    [Fact]
    public void CachePathFor_ChangesWhenAudioDurationOrVersionChanges()
    {
        var baseline = WaveformService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir);

        WaveformService.CachePathFor(_sourcePath, 2, 10_000_000, _testDir).Should().NotBe(baseline);
        WaveformService.CachePathFor(_sourcePath, 1, 11_000_000, _testDir).Should().NotBe(baseline);
        WaveformService.CachePathFor(
            _sourcePath,
            1,
            10_000_000,
            _testDir,
            cacheVersion: WaveformService.CacheVersion + 1).Should().NotBe(baseline);
    }

    [Fact]
    public void CachePathFor_ChangesWhenSourceIdentityChanges()
    {
        var baseline = WaveformService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir);

        File.SetLastWriteTimeUtc(_sourcePath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        WaveformService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir).Should().NotBe(baseline);
    }

    [Fact]
    public void SaveThenLoadCachedPeaks_Roundtrips()
    {
        var service = new WaveformService(runner: null, cacheRoot: _testDir);
        var expected = new[] { 0f, 0.25f, 0.5f, 1f };

        service.SaveCachedPeaks(_sourcePath, audioStreamIndex: 1, durationUs: 10_000_000, expected);

        service.TryLoadCachedPeaks(_sourcePath, audioStreamIndex: 1, durationUs: 10_000_000, out var actual)
            .Should().BeTrue();
        actual.Should().Equal(expected);
    }

    [Fact]
    public void TryLoadCachedPeaks_WrongDuration_Misses()
    {
        var service = new WaveformService(runner: null, cacheRoot: _testDir);
        service.SaveCachedPeaks(_sourcePath, audioStreamIndex: 1, durationUs: 10_000_000, new[] { 0.5f });

        service.TryLoadCachedPeaks(_sourcePath, audioStreamIndex: 1, durationUs: 11_000_000, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void PeakCountForDuration_UsesConfiguredBinsPerSecond()
    {
        WaveformService.PeakCountForDuration(2_000_000)
            .Should().Be(2 * WaveformService.BinsPerSecond);
    }

    [Fact]
    public async Task GetOrExtractPeaksAsync_CancelledBeforeWork_DoesNotRequireFfmpeg()
    {
        var service = new WaveformService(runner: null, cacheRoot: _testDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetOrExtractPeaksAsync(_sourcePath, 1, 10_000_000, cts.Token));
    }

    [Fact]
    public void MainViewModel_TimelineMedia_DefaultsOff()
    {
        var vm = new MainViewModel();
        vm.ShowWaveform.Should().BeFalse();
        vm.ShowThumbnails.Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }
}
