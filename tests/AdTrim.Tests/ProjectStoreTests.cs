using System.IO;
using System.Text;
using AwesomeAssertions;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class ProjectStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sourcePath;
    private readonly MediaInfo _media;

    public ProjectStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CseTest-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        Directory.CreateDirectory(_testDir);

        _sourcePath = Path.Combine(_testDir, "source.mp4");
        // Write enough bytes that File.Length is a meaningful fingerprint.
        File.WriteAllBytes(_sourcePath, Enumerable.Range(0, 1024).Select(b => (byte)(b & 0xFF)).ToArray());

        _media = new MediaInfo(
            DurationUs: 1_830_000_000L,
            VideoCodec: "h264",
            Width: 1920, Height: 1080,
            FrameRate: new Rational(30000, 1001),
            AudioStreams: new[] { new AudioStream(1, "ac3", 6, true) },
            PrimaryAudioIndex: 1);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var store = new ProjectStore();
        var fp = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs);
        var splits = new List<PersistedSplit>
        {
            new("a", 312_000_000, SplitSource.Chapter, OriginalTimeUs: null, Confidence: null, Confirmed: true),
            new("b", 468_000_000, SplitSource.Refined, OriginalTimeUs: 469_000_000, Confidence: Confidence.High, Confirmed: false),
        };
        var proj = new AdTrimProject(
            SchemaVersion: AdTrimProject.CurrentSchemaVersion,
            SourcePath: _sourcePath,
            Fingerprint: fp,
            Media: _media,
            Splits: splits,
            ExcludedSegmentIds: new List<string> { "segment-312000000-468000000" },
            SidecarLocation: SidecarLocation.NextToSource);

        store.Save(proj);

        var result = store.Load(_sourcePath, _media);
        result.Status.Should().Be(SidecarLoadStatus.Loaded);
        result.Project.Should().NotBeNull();
        result.Project!.Splits.Should().HaveCount(2);
        result.Project.Splits[1].Confidence.Should().Be(Confidence.High);
        result.Project.ExcludedSegmentIds.Should().ContainSingle().Which.Should().Be("segment-312000000-468000000");
    }

    [Fact]
    public void Load_DurationMismatch_IsFingerprintMismatch()
    {
        var store = new ProjectStore();
        var fp = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs);
        var proj = new AdTrimProject(
            AdTrimProject.CurrentSchemaVersion, _sourcePath, fp, _media,
            new List<PersistedSplit>(), new List<string>(), SidecarLocation.NextToSource);
        store.Save(proj);

        // Probe a different duration.
        var alteredMedia = _media with { DurationUs = _media.DurationUs + 5_000_000 };
        var result = store.Load(_sourcePath, alteredMedia);
        result.Status.Should().Be(SidecarLoadStatus.FingerprintMismatch);
    }

    [Fact]
    public void Load_MtimeDrift_LoadsWithWarning()
    {
        var store = new ProjectStore();
        var fp = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs);
        var proj = new AdTrimProject(
            AdTrimProject.CurrentSchemaVersion, _sourcePath, fp, _media,
            new List<PersistedSplit>(), new List<string>(), SidecarLocation.NextToSource);
        store.Save(proj);

        // Bump mtime without changing size or duration.
        File.SetLastWriteTimeUtc(_sourcePath, DateTime.UtcNow.AddMinutes(1));
        var result = store.Load(_sourcePath, _media);
        result.Status.Should().Be(SidecarLoadStatus.LoadedWithMtimeWarning);
        result.Project.Should().NotBeNull();
    }

    [Fact]
    public void Load_NoSidecar_ReturnsMissing()
    {
        var store = new ProjectStore();
        var result = store.Load(_sourcePath, _media);
        result.Status.Should().Be(SidecarLoadStatus.Missing);
    }

    [Fact]
    public void Save_AtomicWriteLeavesNoTmpFile()
    {
        var store = new ProjectStore();
        var fp = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs);
        var proj = new AdTrimProject(
            AdTrimProject.CurrentSchemaVersion, _sourcePath, fp, _media,
            new List<PersistedSplit>(), new List<string>(), SidecarLocation.NextToSource);
        store.Save(proj);
        File.Exists(_sourcePath + ".adt.json.tmp").Should().BeFalse();
        File.Exists(_sourcePath + ".adt.json").Should().BeTrue();
    }

    [Fact]
    public void CandidateSidecarPathsFor_IncludesPrimaryAndFallback()
    {
        var store = new ProjectStore();
        var paths = store.CandidateSidecarPathsFor(_sourcePath).ToList();

        paths.Should().HaveCount(2);
        paths[0].Should().Be(_sourcePath + ".adt.json");
        paths[1].Should().EndWith(".adt.json");
        paths[1].Should().NotBe(paths[0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }
}
