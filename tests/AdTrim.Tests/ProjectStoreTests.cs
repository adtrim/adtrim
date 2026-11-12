using System.IO;
using System.Text.Json;
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
        _testDir = Path.Combine(AppContext.BaseDirectory, "CseTest-" + Guid.NewGuid().ToString("n").Substring(0, 8));
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":1}")]
    [InlineData("null")]
    public void IncompleteJson_IsReportedWithoutThrowing(string json)
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        File.WriteAllText(store.SidecarPathFor(_sourcePath), json);
        Assert.Equal(SidecarLoadStatus.Corrupt, store.Load(_sourcePath, _media).Status);
    }

    [Fact]
    public void UnsupportedProject_IsNotOverwritten()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var path = store.SidecarPathFor(_sourcePath);
        const string future = "{\"schema_version\":999}";
        File.WriteAllText(path, future);
        Assert.Equal(SidecarLoadStatus.UnsupportedVersion, store.Load(_sourcePath, _media).Status);
        var project = new AdTrimProject(2, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource);
        var fallback = store.Save(project);
        try { Assert.Equal(future, File.ReadAllText(path)); Assert.NotEqual(path, fallback); }
        finally { File.Delete(fallback); }
    }

    // A recording retagged or restored by another tool no longer matches its project, so the
    // project is not loaded. Its markers may still be right, and the first edit must not erase them.
    [Fact]
    public void ProjectForAChangedRecording_IsKeptWhenTheNextSaveReplacesIt()
    {
        var data = Path.Combine(_testDir, "data");
        var store = new ProjectStore(Path.Combine(data, "projects"));
        var work = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new() { new PersistedSplit("abc123", 600_000_000, SplitSource.Manual, null, null, true, "Reviewed break") },
            new(), SidecarLocation.NextToSource);
        var primary = store.Save(work);
        var original = File.ReadAllBytes(primary);
        File.AppendAllText(_sourcePath, "retagged");
        Assert.Equal(SidecarLoadStatus.FingerprintMismatch, store.Load(_sourcePath, _media).Status);

        var fresh = work with { Fingerprint = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs), Splits = new() };
        Assert.Equal(primary, store.Save(fresh));
        var kept = Directory.EnumerateFiles(Path.Combine(data, "rollback", "projects"), "*.adt.json");
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(kept)));

        // Later saves of the same recording have nothing new to keep.
        store.Save(fresh with { Splits = new() { new PersistedSplit("def456", 700_000_000, SplitSource.Manual, null, null, false) } });
        Assert.Single(Directory.EnumerateFiles(Path.Combine(data, "rollback", "projects"), "*.adt.json"));
    }

    [Fact]
    public void NewerFallback_IsPreferredOverOlderPrimary()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var project = new AdTrimProject(2, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource);
        var primary = store.Save(project);
        var fallback = store.AppDataFallbackPathFor(_sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
        File.Copy(primary, fallback, true);
        File.SetLastWriteTimeUtc(primary, DateTime.UtcNow.AddMinutes(-1));
        try { Assert.Contains("fallback", store.Load(_sourcePath, _media).Message); }
        finally { File.Delete(fallback); }
    }

    [Fact]
    public void FailedReplaceBesideSource_FallsBackWithoutLeavingATempFile()
    {
        // A sidecar another program holds or protects must not collect stale .tmp files beside the recording.
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var project = new AdTrimProject(2, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource);
        var primary = store.Save(project);
        var before = File.ReadAllBytes(primary);
        File.SetAttributes(primary, FileAttributes.ReadOnly);
        try
        {
            var saved = store.Save(project with { Splits = new() { new("a", 1_000_000, SplitSource.Manual, null, null, false, "Next") } });
            Assert.Equal(store.AppDataFallbackPathFor(_sourcePath), saved);
            Assert.Equal("Next", store.Load(_sourcePath, _media).Project!.Splits[0].ChapterTitle);
            Assert.Equal(before, File.ReadAllBytes(primary));
            Assert.False(File.Exists(primary + ".tmp"));
            File.Delete(saved);
        }
        finally { File.SetAttributes(primary, FileAttributes.Normal); }
    }

    [Fact]
    public void VersionOne_LoadsAndChapterTitlesRoundtripInVersionTwo()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var project = new AdTrimProject(1, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new() { new("a", 1_000_000, SplitSource.Chapter, null, null, false, "Commercial 1") }, new(), SidecarLocation.NextToSource);
        store.Save(project);
        Assert.Equal("Commercial 1", store.Load(_sourcePath, _media).Project!.Splits[0].ChapterTitle);
        store.Save(project with { SchemaVersion = 2 });
        Assert.Equal(2, store.Load(_sourcePath, _media).Project!.SchemaVersion);
    }

    [Fact]
    public void SaveThenLoad_Roundtrips()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
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
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
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
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
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
    public void SchemaUpgrade_PreservesExactLegacySidecarBeforeSaving()
    {
        var dataDirectory = Path.Combine(_testDir, "appdata");
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"), dataDirectory);
        var old = new AdTrimProject(2, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource);
        store.Save(old);
        var sidecar = store.SidecarPathFor(_sourcePath);
        var original = File.ReadAllBytes(sidecar);

        store.Save(old with { SchemaVersion = AdTrimProject.CurrentSchemaVersion, MarkerSetSource = MarkerSetSource.Imported });

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(dataDirectory, "rollback", "projects"), "*.adt.json"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "rollback", "projects", "manifest.json")));
        var record = Assert.Single(manifest.RootElement.EnumerateArray());
        Assert.Equal(Path.GetFullPath(_sourcePath), record.GetProperty("SourcePath").GetString());
        Assert.Equal(Path.GetFullPath(sidecar), record.GetProperty("OriginalPath").GetString());
        Assert.Equal(Path.GetFullPath(backup), record.GetProperty("BackupPath").GetString());
        Assert.Equal(SidecarLoadStatus.Loaded, store.Load(_sourcePath, _media).Status);
    }

    [Fact]
    public void Load_NoSidecar_ReturnsMissing()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var result = store.Load(_sourcePath, _media);
        result.Status.Should().Be(SidecarLoadStatus.Missing);
    }

    [Fact]
    public void RepeatedSaves_ReplaceTheProjectAndLeaveNoTempFile()
    {
        // Autosave replaces the project every few seconds; each save must land and clean up after itself.
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        var fp = ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs);
        var proj = new AdTrimProject(
            AdTrimProject.CurrentSchemaVersion, _sourcePath, fp, _media,
            new List<PersistedSplit>(), new List<string>(), SidecarLocation.NextToSource);
        store.Save(proj);
        store.Save(proj with { Splits = new() { new("a", 1_000_000, SplitSource.Manual, null, null, false, "Second") } });
        Directory.GetFiles(_testDir, "*.tmp").Should().BeEmpty();
        store.Load(_sourcePath, _media).Project!.Splits.Should().ContainSingle().Which.ChapterTitle.Should().Be("Second");
    }

    [Fact]
    public void CandidateSidecarPathsFor_IncludesPrimaryAndFallback()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
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

    // The backup list is an index. If a power loss leaves it unreadable, older projects must still save.
    [Fact]
    public void DamagedBackupList_DoesNotBlockSavingAnOlderProject()
    {
        var data = Path.Combine(_testDir, "data-damaged");
        var store = new ProjectStore(Path.Combine(data, "projects"));
        var old = new AdTrimProject(2, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource);
        var primary = store.Save(old);
        var rollback = Path.Combine(data, "rollback", "projects");
        Directory.CreateDirectory(rollback);
        File.WriteAllText(Path.Combine(rollback, "manifest.json"), "");

        Assert.Equal(primary, store.Save(old with { SchemaVersion = AdTrimProject.CurrentSchemaVersion }));
        Assert.Single(Directory.EnumerateFiles(rollback, "*.adt.json"));
        Assert.Single(Directory.EnumerateFiles(rollback, "manifest.damaged-*.json"));
        Assert.Contains("schema2", File.ReadAllText(Path.Combine(rollback, "manifest.json")));
    }

    // "Use these markers anyway": the earlier project is offered for the recording as it is now.
    [Theory]
    [InlineData(1_830_000_000L)]
    [InlineData(1_000_000_000L)]
    [InlineData(2_000_000_000L)]
    public void EarlierProject_IsFittedToTheRecordingsCurrentDuration(long durationNow)
    {
        var store = new ProjectStore(Path.Combine(_testDir, "data-fit", "projects"));
        var saved = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs), _media,
            new()
            {
                new PersistedSplit("a", 600_000_000, SplitSource.Manual, null, null, true, "Break"),
                new PersistedSplit("b", 900_000_000, SplitSource.Manual, null, null, true),
                new PersistedSplit("c", 1_500_000_000, SplitSource.Manual, null, null, false),
            },
            new() { "segment-600000000-900000000", "segment-1500000000-1830000000" }, SidecarLocation.NextToSource);
        store.Save(saved);
        File.AppendAllText(_sourcePath, "retagged");

        var fitted = store.LoadForChangedRecording(_sourcePath, durationNow)!;

        Assert.All(fitted.Splits, s => Assert.InRange(s.TimeUs, 1, durationNow - 1));
        Assert.Contains("segment-600000000-900000000", fitted.ExcludedSegmentIds);
        Assert.Equal(durationNow > 1_500_000_000 ? 3 : 2, fitted.Splits.Count);
        // The closing section keeps its exclusion under its new end, or goes with its marker.
        if (durationNow > 1_500_000_000) Assert.Contains($"segment-1500000000-{durationNow}", fitted.ExcludedSegmentIds);
        else Assert.Single(fitted.ExcludedSegmentIds);
        // What comes back must be a project the editor accepts for the new duration.
        var asNow = fitted with { Fingerprint = fitted.Fingerprint with { DurationUs = durationNow }, Media = _media with { DurationUs = durationNow } };
        Assert.Null(ProjectStore.Validate(asNow));
    }

    // Loading picks the newer of the two saves, and file times on a network share follow the
    // server's clock. A fallback save left behind could come back in place of later edits.
    [Fact]
    public void FallbackSave_IsRemovedOnceASaveBesideTheRecordingSucceeds()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback-superseded"));
        var project = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new() { new("a", 1_000_000, SplitSource.Manual, null, null, false, "Earlier") }, new(), SidecarLocation.NextToSource);
        var primary = store.Save(project with { Splits = new() });
        File.SetAttributes(primary, FileAttributes.ReadOnly);
        string fallback;
        try { fallback = store.Save(project); }
        finally { File.SetAttributes(primary, FileAttributes.Normal); }
        Assert.Equal(store.AppDataFallbackPathFor(_sourcePath), fallback);
        // This computer's clock is ahead of the share's.
        File.SetLastWriteTimeUtc(fallback, DateTime.UtcNow.AddMinutes(10));

        store.Save(project with { Splits = new() { new("a", 1_000_000, SplitSource.Manual, null, null, false, "Current") } });

        Assert.Equal("Current", store.Load(_sourcePath, _media).Project!.Splits[0].ChapterTitle);
        Assert.False(File.Exists(fallback));
    }

    [Fact]
    public void FallbackSaveThisVersionCannotRead_IsKept()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback-kept"));
        var fallback = store.AppDataFallbackPathFor(_sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
        File.WriteAllText(fallback, "{\"schema_version\":999}");
        store.Save(new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _sourcePath, ProjectStore.FingerprintOf(_sourcePath, _media.DurationUs),
            _media, new(), new(), SidecarLocation.NextToSource));
        Assert.Equal("{\"schema_version\":999}", File.ReadAllText(fallback));
    }

    // The same recording reaches AdTrim as d:\tv\a.ts and D:\TV\A.ts; both must find one fallback save.
    [Fact]
    public void FallbackSave_IsSharedByEverySpellingOfThePath()
    {
        var store = new ProjectStore(Path.Combine(_testDir, "fallback"));
        Assert.Equal(store.AppDataFallbackPathFor(_sourcePath.ToLowerInvariant()), store.AppDataFallbackPathFor(_sourcePath.ToUpperInvariant()));
    }

    [Fact]
    public void FallbackSaveNamedByAnEarlierVersion_IsStillUsed()
    {
        var directory = Path.Combine(_testDir, "fallback");
        var store = new ProjectStore(directory);
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_sourcePath))))[..16].ToLowerInvariant();
        var legacy = Path.Combine(directory, name + ".adt.json");
        Assert.NotEqual(legacy, store.AppDataFallbackPathFor(_sourcePath));
        Directory.CreateDirectory(directory);
        File.WriteAllText(legacy, "{}");
        Assert.Equal(legacy, store.AppDataFallbackPathFor(_sourcePath));
    }
}
