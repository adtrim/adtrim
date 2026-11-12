using System.IO;
using AwesomeAssertions;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class MediaCacheCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AdtCacheCleanup-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly DateTime _now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private string Thumbs => Path.Combine(_root, "thumbnails");
    private string Levels => Path.Combine(_root, "audio-levels");
    private string Waveforms => Path.Combine(_root, "waveforms");

    private string Folder(string name, int daysAgo)
    {
        var path = Path.Combine(Thumbs, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "1000000.jpg"), [1]);
        Directory.SetLastWriteTimeUtc(path, _now.AddDays(-daysAgo));
        return path;
    }

    private string FileAt(string dir, string name, int daysAgo)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, _now.AddDays(-daysAgo));
        return path;
    }

    private int Run() => MediaCacheCleanup.Run(Thumbs, "_h112_v2", Levels, 1, Waveforms, _now);

    [Fact]
    public void RemovesWhatHasNotBeenUsedForMonthsAndKeepsRecentWork()
    {
        var stale = Folder("0123456789abcdef_d600000000_h112_v2", daysAgo: 120);
        var recent = Folder("1123456789abcdef_d600000000_h112_v2", daysAgo: 10);
        var staleLevels = FileAt(Levels, "0123456789abcdef_a1_d600000000_v1.adtlevels", 120);
        var recentLevels = FileAt(Levels, "1123456789abcdef_a1_d600000000_v1.adtlevels", 10);

        Run().Should().Be(2);

        Directory.Exists(stale).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        File.Exists(staleLevels).Should().BeFalse();
        File.Exists(recentLevels).Should().BeTrue();
    }

    [Fact]
    public void FormatsThisVersionNoLongerReadsGoAtOnce()
    {
        var oldThumbs = Folder("0123456789abcdef_d600000000_w320_v1", daysAgo: 1);
        var oldLevels = FileAt(Levels, "0123456789abcdef_a1_d600000000_v0.adtlevels", 1);
        var oldWave = FileAt(Waveforms, "0123456789abcdef_a1_d600000000_b25_v2.adtwave", 1);

        Run();

        Directory.Exists(oldThumbs).Should().BeFalse();
        File.Exists(oldLevels).Should().BeFalse();
        File.Exists(oldWave).Should().BeFalse();
        Directory.Exists(Waveforms).Should().BeFalse("the emptied legacy folder is removed too");
    }

    // A test build and the installed version share one data folder. When their cache formats differ,
    // the older one must not delete what the newer one just made, on every start of either.
    [Fact]
    public void CachesFromANewerBuildAreKeptUntilTheyAgeOut()
    {
        var newerThumbs = Folder("0123456789abcdef_d600000000_h112_v3", daysAgo: 1);
        var newerLevels = FileAt(Levels, "0123456789abcdef_a1_d600000000_v2.adtlevels", 1);
        var staleNewer = Folder("1123456789abcdef_d600000000_h112_v3", daysAgo: 120);

        Run().Should().Be(1);

        Directory.Exists(newerThumbs).Should().BeTrue();
        File.Exists(newerLevels).Should().BeTrue();
        Directory.Exists(staleNewer).Should().BeFalse();
    }

    [Fact]
    public void NeverTouchesAnythingThatIsNotAdTrimCache()
    {
        // Only entries named the way AdTrim names its caches can be removed, however old.
        var notes = Path.Combine(Thumbs, "my notes");
        Directory.CreateDirectory(notes);
        Directory.SetLastWriteTimeUtc(notes, _now.AddYears(-2));
        var stray = FileAt(Levels, "readme.txt", 800);
        var keep = FileAt(Waveforms, "keep.txt", 800);

        Run().Should().Be(0);

        Directory.Exists(notes).Should().BeTrue();
        File.Exists(stray).Should().BeTrue();
        File.Exists(keep).Should().BeTrue();
        Directory.Exists(Waveforms).Should().BeTrue();
    }

    [Fact]
    public void OpeningARecordingKeepsItsCacheAlive()
    {
        var folder = Folder("0123456789abcdef_d600000000_h112_v2", daysAgo: 120);
        MediaCacheCleanup.Touch(folder);

        MediaCacheCleanup.Run(Thumbs, "_h112_v2", Levels, 1, Waveforms, DateTime.UtcNow);

        Directory.Exists(folder).Should().BeTrue();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
