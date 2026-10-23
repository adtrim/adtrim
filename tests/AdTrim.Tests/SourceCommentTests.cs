using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class SourceCommentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-comments-" + Guid.NewGuid().ToString("N"));
    private readonly MediaInfo _media = new(1_000_000, "h264", 160, 90, new(25, 1), Array.Empty<AudioStream>(), -1);
    public SourceCommentTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string FilePath(string name) => Path.GetFullPath(Path.Combine(_directory, name));
    private static byte[] Box(string type, params byte[][] parts)
    {
        var bytes = new byte[8 + parts.Sum(p => p.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(bytes, 4);
        int at = 8;
        foreach (var part in parts) { part.CopyTo(bytes, at); at += part.Length; }
        return bytes;
    }
    private static byte[] U32(uint n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return b; }
    private static byte[] Value(string text, uint locale = 0) => Box("data", U32(1), U32(locale), Encoding.UTF8.GetBytes(text));
    private string Fixture(string name = "source.mp4", uint locale = 0, bool tail = false, string comment = "Original user comment")
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isom"));
        var keys = Box("keys", U32(0), U32(3), Box("mdta", Encoding.UTF8.GetBytes("comment")),
            Box("mdta", Encoding.UTF8.GetBytes("title")), Box("mdta", Encoding.UTF8.GetBytes("custom_preserve")));
        var meta = Box("meta", U32(0), Box("hdlr", new byte[8], Encoding.ASCII.GetBytes("mdta"), new byte[13]), keys,
            Box("ilst", Box(Encoding.Latin1.GetString(U32(1)), Value(comment, locale)),
                Box(Encoding.Latin1.GetString(U32(2)), Value("Original title")),
                Box(Encoding.Latin1.GetString(U32(3)), Value("Original custom value"))));
        byte[] Movie(uint offset) => Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", U32(0), U32(1), U32(offset)))))),
            Box("udta", meta, Box("chpl", new byte[] { 1, 2, 3, 4 })));
        var initial = Movie(0);
        var movie = Movie((uint)(ftyp.Length + (tail ? 0 : initial.Length) + 8));
        var media = Box("mdat", new byte[] { 11, 22, 33, 44 });
        var uuid = Box("uuid", Encoding.ASCII.GetBytes("opaque-test-data-preserved"));
        var bytes = tail ? ftyp.Concat(media).Concat(movie).Concat(uuid).ToArray() : ftyp.Concat(movie).Concat(media).Concat(uuid).ToArray();
        string path = FilePath(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagingMigratesOnlyCommentAndKeepsEveryOtherMetadataValueAndMediaOffset(bool tail)
    {
        string source = Fixture(tail: tail), stage = FilePath("staged.mp4");
        string hash = Hash(source);
        await Mp4Comments.StageAsync(source, stage, "2.0.1", new(2026, 10, 22), default);
        Assert.Equal(new[] { "Original user comment; Exported with AdTrim 2.0.1 (2026-10-22)" }, Mp4Comments.Read(stage));
        string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(stage));
        Assert.Contains("Original title", bytes);
        Assert.Contains("Original custom value", bytes);
        Assert.Contains("opaque-test-data-preserved", bytes);
        var raw = File.ReadAllBytes(stage);
        int stco = bytes.IndexOf("stco", StringComparison.Ordinal);
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(stco + 12));
        Assert.Equal(new byte[] { 11, 22, 33, 44 }, raw.AsSpan((int)offset, 4).ToArray());
        Assert.Equal(hash, Hash(source));
    }

    [Fact]
    public async Task LocalizedCommentsAreRefusedRatherThanDiscarded()
    {
        string source = Fixture(locale: 1033), stage = FilePath("staged.mp4");
        string hash = Hash(source);
        await Assert.ThrowsAsync<NotSupportedException>(() => Mp4Comments.StageAsync(source, stage, "2.0.1", new(2026, 10, 22), default));
        Assert.Equal(hash, Hash(source));
    }

    [Fact]
    public void LabelsDistinguishCompletedExportsFromOrdinaryMentionsAndReplaceOnlyOurPreviousLabel()
    {
        Assert.False(Mp4Comments.HasExportLabel(new[] { "I want to try AdTrim", "Exported with another editor" }));
        Assert.True(Mp4Comments.HasExportLabel(new[] { "My notes; Exported with AdTrim 2.0.1 (2026-10-22)" }));
        Assert.True(Mp4Comments.HasExportLabel(new[] { "Edited by AdTrim v2.0.0" }));
        Assert.Equal("My notes; Exported with AdTrim 2.0.2 (2026-10-23)", Mp4Comments.Merge(
            new[] { "My notes; Exported with AdTrim 2.0.1 (2026-10-22)" }, "2.0.2", new(2026, 10, 23)));
    }

    [Fact]
    public async Task SuccessfulMarkRefreshesPrimaryAndFallbackWithoutLosingEditDecisions()
    {
        string source = Fixture();
        var store = new ProjectStore(FilePath("fallback"));
        var fingerprint = ProjectStore.FingerprintOf(source, _media.DurationUs);
        var project = new AdTrimProject(3, source, fingerprint, _media, new(), new() { "segment-0-1000000" }, SidecarLocation.NextToSource);
        store.Save(project);
        Directory.CreateDirectory(FilePath("fallback"));
        File.Copy(store.SidecarPathFor(source), store.AppDataFallbackPathFor(source));
        var updated = await SourceCommentService.MarkAsync(source, fingerprint, store, default);
        Assert.NotEqual(fingerprint.SizeBytes, updated.SizeBytes);
        Assert.Equal(SidecarLoadStatus.Loaded, store.Load(source, _media).Status);
        Assert.Equal(project.ExcludedSegmentIds, store.Load(source, _media).Project!.ExcludedSegmentIds);
        File.Delete(store.SidecarPathFor(source));
        Assert.Equal(SidecarLoadStatus.Loaded, store.Load(source, _media).Status);
        Assert.False(SourceCommentService.HasPendingUpdate(source));
        Assert.False(File.Exists(source + ".adtrim-export-comment.bak"));
    }

    [Fact]
    public async Task AProjectFromANewerAppPreventsChangingTheSource()
    {
        string source = Fixture();
        string hash = Hash(source);
        var store = new ProjectStore(FilePath("fallback"));
        File.WriteAllText(store.SidecarPathFor(source), "{\"schema_version\":999}");
        await Assert.ThrowsAsync<IOException>(() => SourceCommentService.MarkAsync(source, ProjectStore.FingerprintOf(source, _media.DurationUs), store, default));
        Assert.Equal(hash, Hash(source));
        Assert.Equal("{\"schema_version\":999}", File.ReadAllText(store.SidecarPathFor(source)));
    }

    [Fact]
    public async Task CancellationDoesNotMarkOrReplaceTheSource()
    {
        string source = Fixture();
        string hash = Hash(source);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceCommentService.MarkAsync(source,
            ProjectStore.FingerprintOf(source, _media.DurationUs), new ProjectStore(FilePath("fallback")), stop.Token));
        Assert.Equal(hash, Hash(source));
        Assert.False(SourceCommentService.HasPendingUpdate(source));
    }

    [Fact]
    public void InterruptedReplacementIsRolledBackUsingVerifiedRecoveryFiles()
    {
        string source = Fixture(), backup = source + ".adtrim-export-comment.bak";
        string hash = Hash(source);
        File.Copy(source, backup);
        File.WriteAllText(source, "replacement interrupted");
        string stageName = Path.GetFileName(source) + ".adtrim-comment-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(source + ".adtrim-export-comment.json", JsonSerializer.Serialize(new {
            BeforeHash = hash, AfterHash = Hash(source), StageName = stageName, Projects = Array.Empty<object>(), Committed = false }));
        SourceCommentService.Recover(source, new ProjectStore(FilePath("fallback")));
        Assert.Equal(hash, Hash(source));
        Assert.False(SourceCommentService.HasPendingUpdate(source));
    }

    [Fact]
    public async Task FolderDiscoveryRecursesWithoutRelyingOnSidecarsAndSeparatesUnreadableFiles()
    {
        string unmarked = Fixture();
        Directory.CreateDirectory(FilePath("nested"));
        string marked = FilePath("nested/marked.mp4");
        await Mp4Comments.StageAsync(unmarked, marked, "2.0.1", new(2026, 10, 22), default);
        File.WriteAllText(FilePath("broken.mp4"), "not a video");
        File.WriteAllText(unmarked + ".adt.json", "a sidecar does not prove export");
        var rows = new List<UnexportedFile>();
        await foreach (var entry in UnexportedFiles.ScanAsync(_directory)) rows.Add(entry);
        Assert.Contains(rows, r => r.Path == marked && r.Exported);
        Assert.Contains(rows, r => r.Path == unmarked && !r.Exported && r.Error is null);
        Assert.Contains(rows, r => r.Path.EndsWith("broken.mp4") && r.Error is not null);
        Assert.DoesNotContain(rows, r => r.IsFolder);
        Directory.CreateDirectory(FilePath("empty"));
        var folders = new List<UnexportedFile>();
        await foreach (var entry in UnexportedFiles.ScanAsync(_directory, includeFolders: true))
            if (entry.IsFolder) folders.Add(entry);
        Assert.Contains(folders, r => r.Path == FilePath("empty"));
        Assert.Contains(folders, r => r.Path == FilePath("nested"));
    }

    private AdTrimProject ExportedProject(string source)
    {
        var fingerprint = ProjectStore.FingerprintOf(source, _media.DurationUs);
        return new(AdTrimProject.CurrentSchemaVersion, source, fingerprint, _media, new(), new(), SidecarLocation.NextToSource,
            LastExport: new("AdTrim", "2.0.4", DateTimeOffset.UtcNow, fingerprint));
    }

    [Fact]
    public async Task DiscoverySkipsLeadingDotFoldersAtEveryDepthWithoutInspectingTheirFiles()
    {
        string visible = Fixture("visible.mp4");
        foreach (string hidden in new[] { ".ts_archive/nested", "Show/.cache/Season 1" })
        {
            Directory.CreateDirectory(FilePath(hidden));
            File.WriteAllText(FilePath(hidden + "/broken.mp4"), "Must not be probed");
        }
        var rows = new List<UnexportedFile>();
        await foreach (var row in UnexportedFiles.ScanAsync(_directory, includeFolders: true)) rows.Add(row);
        Assert.Contains(rows, r => r.Path == visible);
        Assert.Contains(rows, r => r.IsFolder && r.Path == FilePath("Show"));
        Assert.DoesNotContain(rows, r => Path.GetRelativePath(_directory, r.Path).Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.') && part != "."));
        Assert.DoesNotContain(rows, r => r.Error is not null);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Original user comment", false)]
    public async Task Mp4UsesSidecarOnlyWhenCommentsAreEmpty(string comment, bool exported)
    {
        string source = Fixture(comment: comment);
        var store = new ProjectStore(FilePath("fallback"));
        store.Save(ExportedProject(source));
        string hash = Hash(source);
        var rows = new List<UnexportedFile>();
        await foreach (var row in UnexportedFiles.ScanAsync(_directory)) rows.Add(row);
        Assert.Equal(exported, Assert.Single(rows).Exported);
        Assert.Null(rows[0].Error);
        Assert.Equal(hash, Hash(source));
    }

    [Fact]
    public async Task Mp4CommentMarkerDoesNotRequireAReadableSidecar()
    {
        string source = Fixture(comment: "Exported with AdTrim 2.0.4 (2026-10-22)");
        File.WriteAllText(source + ".adt.json", "unreadable sidecar");
        await foreach (var row in UnexportedFiles.ScanAsync(_directory))
        {
            Assert.True(row.Exported);
            Assert.Null(row.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TsUsesOnlyItsSidecarWithoutParsingTheMedia(bool exported)
    {
        string source = FilePath("recording.TS");
        File.WriteAllText(source, "Disposable bytes that cannot be parsed as MP4");
        if (exported) new ProjectStore(FilePath("fallback")).Save(ExportedProject(source));
        string hash = Hash(source);
        var rows = new List<UnexportedFile>();
        await foreach (var row in UnexportedFiles.ScanAsync(_directory)) rows.Add(row);
        Assert.Equal(exported, Assert.Single(rows).Exported);
        Assert.Null(rows[0].Error);
        Assert.Equal(hash, Hash(source));
    }

    [Fact]
    public void ExportRecordSurvivesProjectSaveAndRejectsChangedSourceIdentity()
    {
        string source = Fixture(comment: "");
        var store = new ProjectStore(FilePath("fallback"));
        var project = ExportedProject(source);
        store.Save(project);
        var reloaded = store.Load(source, _media).Project!;
        store.Save(reloaded with { StartChapterTitle = "User changed the title" });
        Assert.Equal(project.LastExport, store.Load(source, _media).Project!.LastExport);
        Assert.True(store.HasExportRecord(source));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        Assert.False(store.HasExportRecord(source));
        File.SetLastWriteTimeUtc(source, DateTimeOffset.FromUnixTimeMilliseconds(project.Fingerprint.ModifiedUnixMs).UtcDateTime);
        using (var output = File.Open(source, FileMode.Append)) output.WriteByte(0);
        Assert.False(store.HasExportRecord(source));
    }

    [Fact]
    public async Task EmptyCommentsWithAnUnreadableSidecarHaveUnknownExportStatus()
    {
        string source = Fixture(comment: "");
        File.WriteAllText(source + ".adt.json", "{\"schema_version\":999}");
        await foreach (var row in UnexportedFiles.ScanAsync(_directory))
        {
            Assert.False(row.Exported);
            Assert.NotNull(row.Error);
        }
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".ts")]
    public void FallbackExportRecordIsRecognizedAndOldProjectsAreNotAssumedExported(string extension)
    {
        string source = extension == ".mp4" ? Fixture(comment: "") : FilePath("recording.ts");
        if (extension == ".ts") File.WriteAllText(source, "Source sentinel");
        var store = new ProjectStore(FilePath("fallback"));
        var project = ExportedProject(source);
        var primary = store.Save(project with { SchemaVersion = 3, LastExport = null });
        Assert.False(store.HasExportRecord(source));
        store.Save(project);
        Directory.CreateDirectory(FilePath("fallback"));
        File.Copy(primary, store.AppDataFallbackPathFor(source));
        File.Delete(primary);
        Assert.True(store.HasExportRecord(source));
    }
}
