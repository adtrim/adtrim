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
    private string Fixture(string name = "source.mp4", uint locale = 0, bool tail = false, string comment = "Original user comment", int mediaBytes = 4)
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
        var media = Box("mdat", new byte[] { 11, 22, 33, 44 }.Concat(new byte[Math.Max(0, mediaBytes - 4)]).ToArray());
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
    public async Task VerifiedCommentUpdatePreservesCachesWithoutTrustingLaterExternalChanges()
    {
        string source = Fixture();
        var before = ProjectStore.FingerprintOf(source, _media.DurationUs);
        var suggestions = new MarkerSuggestionStore(_directory);
        var state = new AdTrim.Commands.MarkerSetState([], [], "Original proposal", MarkerSetSource.AdTrim, null);
        state = state with { BaselineId = state.ContentId() };
        suggestions.Save(suggestions.Key(source, before), state);
        string thumbnailRoot = FilePath("thumbnails"), audioRoot = FilePath("levels");
        var thumbnails = new ThumbnailService(null, thumbnailRoot);
        string oldTiles = ThumbnailService.CacheDirectoryFor(source, before.DurationUs, thumbnailRoot);
        Directory.CreateDirectory(oldTiles);
        byte[] image = [1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(oldTiles, "0.jpg"), image);
        await File.WriteAllBytesAsync(Path.Combine(oldTiles, "0.jpg.tmp.jpg"), [4]);
        var audio = new AudioLevelService(null, audioRoot);
        var levels = new AudioLevels([20, 40, 60], [true]);
        audio.SaveCached(source, 1, before.DurationUs, levels);

        var after = await SourceCommentService.MarkAsync(source, before, new ProjectStore(dataDirectory: _directory), default);
        Assert.NotEqual(before, after);
        suggestions.PreserveAfterMetadataUpdate(source, before, after);
        thumbnails.PreserveAfterMetadataUpdate(source, before);
        audio.PreserveAfterMetadataUpdate(source, 1, before);

        Assert.Equal(state.BaselineId, suggestions.Load(suggestions.Key(source, after), after.DurationUs)?.BaselineId);
        var tile = thumbnails.LoadCached(source, after.DurationUs).Nearest(0);
        Assert.NotNull(tile);
        Assert.Equal(image, await File.ReadAllBytesAsync(tile.Value.Path));
        Assert.NotEqual(Path.Combine(oldTiles, "0.jpg"), tile.Value.Path);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(tile.Value.Path)!));
        Assert.True(audio.TryLoadCached(source, 1, after.DurationUs, out var loaded));
        Assert.Equal(levels.Loudness, loaded.Loudness);
        Assert.Equal(levels.CentreDialogue, loaded.CentreDialogue);
        Assert.True(File.Exists(Path.Combine(oldTiles, "0.jpg")));

        using (var file = File.Open(source, FileMode.Append)) file.WriteByte(99);
        var external = ProjectStore.FingerprintOf(source, after.DurationUs);
        Assert.Null(suggestions.Load(suggestions.Key(source, external), external.DurationUs));
        Assert.Equal(0, thumbnails.LoadCached(source, external.DurationUs).Count);
        Assert.False(audio.TryLoadCached(source, 1, external.DurationUs, out _));
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

    // Records every report and can act at the start of a named step, to inject a fault there.
    private sealed class RecordingProgress(Action<string>? atStep = null) : IProgress<SourceCommentProgress>
    {
        private readonly HashSet<string> _started = new();
        public List<SourceCommentProgress> Values { get; } = new();
        public void Report(SourceCommentProgress value)
        {
            Values.Add(value);
            if (_started.Add((value.Restoring ? "restore:" : "") + value.Step)) atStep?.Invoke(value.Step);
        }
        public IEnumerable<string> Steps => Values.Where(v => !v.Restoring && v.Step != "").Select(v => v.Step).Distinct();
    }

    // Changes one media byte in place and keeps the size and modified time, so only a content read can notice.
    private static void TamperKeepingFingerprint(string path)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        int media = bytes.AsSpan().IndexOf(new byte[] { 11, 22, 33, 44 }) + 100;
        bytes[media] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, modified);
    }

    private const int LargeMedia = 3 << 20;

    [Fact]
    public async Task CommentProgressFollowsTheFiveVerifiedPassesAndOnlyCompletesAfterCommit()
    {
        // The step can take minutes on a network share: progress must move through each pass and
        // must not reach 100% before the replaced file is verified and committed.
        string tracked = Fixture("tracked.mp4", mediaBytes: LargeMedia), plain = Fixture("plain.mp4", mediaBytes: LargeMedia);
        var progress = new RecordingProgress();
        await SourceCommentService.MarkAsync(tracked, ProjectStore.FingerprintOf(tracked, _media.DurationUs),
            new ProjectStore(FilePath("fallback")), default, progress);
        await SourceCommentService.MarkAsync(plain, ProjectStore.FingerprintOf(plain, _media.DurationUs),
            new ProjectStore(FilePath("fallback")), default);

        Assert.Equal(Hash(plain), Hash(tracked));
        Assert.True(Mp4Comments.HasExportLabel(Mp4Comments.Read(tracked)));
        var fractions = progress.Values.Select(v => v.Fraction).ToList();
        Assert.Equal(fractions.OrderBy(v => v), fractions);
        Assert.DoesNotContain(progress.Values, v => v.Restoring);
        Assert.Equal(new[] { SourceCommentService.CopyingStep, SourceCommentService.CheckingStep,
            SourceCommentService.RecheckingStep, SourceCommentService.VerifyingStep }, progress.Steps);
        // Copy (2) + check (1) + recheck (1) come before the final verification pass (1).
        double verifyStart = progress.Values.First(v => v.Step == SourceCommentService.VerifyingStep).Fraction;
        Assert.InRange(verifyStart, 0.75, 0.85);
        Assert.InRange(fractions[^2], 0.95, 0.99);
        Assert.Equal(1.0, fractions[^1]);
        Assert.Single(fractions, f => f == 1.0);
        Assert.False(File.Exists(tracked + ".adtrim-export-comment.bak"));
        Assert.Empty(Directory.GetFiles(_directory, "tracked.mp4.adtrim-comment-*"));
    }

    [Fact]
    public async Task RepeatingAnIdenticalCommentReadsOnlyTheIndexAndReportsCompletion()
    {
        string source = Fixture(mediaBytes: LargeMedia);
        var store = new ProjectStore(FilePath("fallback"));
        var marked = await SourceCommentService.MarkAsync(source, ProjectStore.FingerprintOf(source, _media.DurationUs), store, default);
        string hash = Hash(source);
        var modified = File.GetLastWriteTimeUtc(source);
        var progress = new RecordingProgress();

        var again = await SourceCommentService.MarkAsync(source, marked, store, default, progress);

        Assert.Equal(marked, again);
        Assert.Equal(hash, Hash(source));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(source));
        Assert.Equal(new[] { 1.0 }, progress.Values.Select(v => v.Fraction));
        Assert.Empty(Directory.GetFiles(_directory, "source.mp4.adtrim-*"));
    }

    [Fact]
    public async Task TheCopyReadBackCatchesAStagedWriteThatDoesNotMatchTheSource()
    {
        // The single read-back replaces the old side-by-side comparison; it must still reject a bad copy.
        string source = Fixture(mediaBytes: LargeMedia), stage = FilePath("staged.mp4");
        string hash = Hash(source);
        var error = await Assert.ThrowsAsync<IOException>(() => Mp4Comments.StageAsync(source, stage, "2.0.1", new(2026, 10, 22), default,
            checking: () => TamperKeepingFingerprint(stage)));
        Assert.Contains("preservation check failed", error.Message);
        Assert.Equal(hash, Hash(source));
    }

    [Fact]
    public async Task StagingHashesMatchIndependentReadsOfBothFiles()
    {
        string source = Fixture(mediaBytes: LargeMedia), stage = FilePath("staged.mp4");
        var result = await Mp4Comments.StageAsync(source, stage, "2.0.1", new(2026, 10, 22), default);
        Assert.Equal(Hash(source), result.SourceHash);
        Assert.Equal(Hash(stage), result.StagedHash);
        Assert.True(Mp4Comments.NeedsUpdate(source, "2.0.1", new(2026, 10, 22)));
        Assert.False(Mp4Comments.NeedsUpdate(stage, "2.0.1", new(2026, 10, 22)));
        Assert.True(Mp4Comments.NeedsUpdate(stage, "2.0.1", new(2026, 10, 23)));
    }

    [Fact]
    public async Task AContentChangeBeforeReplacementIsCaughtByTheRecheckAndNothingIsReplaced()
    {
        string source = Fixture(mediaBytes: LargeMedia);
        var fingerprint = ProjectStore.FingerprintOf(source, _media.DurationUs);
        var progress = new RecordingProgress(step => { if (step == SourceCommentService.RecheckingStep) TamperKeepingFingerprint(source); });
        var error = await Assert.ThrowsAsync<IOException>(() => SourceCommentService.MarkAsync(source, fingerprint,
            new ProjectStore(FilePath("fallback")), default, progress));
        Assert.Contains("changed during comment preparation", error.Message);
        Assert.False(Mp4Comments.HasExportLabel(Mp4Comments.Read(source)));
        Assert.False(SourceCommentService.HasPendingUpdate(source));
        Assert.Empty(Directory.GetFiles(_directory, "source.mp4.adtrim-*"));
        Assert.DoesNotContain(progress.Values, v => v.Fraction == 1.0);
    }

    [Fact]
    public async Task CancellingWhileCopyingLeavesTheSourceAndNoRecoveryFiles()
    {
        string source = Fixture(mediaBytes: LargeMedia);
        string hash = Hash(source);
        using var stop = new CancellationTokenSource();
        var progress = new RecordingProgress(step => { if (step == SourceCommentService.CopyingStep) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceCommentService.MarkAsync(source,
            ProjectStore.FingerprintOf(source, _media.DurationUs), new ProjectStore(FilePath("fallback")), stop.Token, progress));
        Assert.Equal(hash, Hash(source));
        Assert.False(SourceCommentService.HasPendingUpdate(source));
        Assert.Empty(Directory.GetFiles(_directory, "source.mp4.adtrim-*"));
        Assert.DoesNotContain(progress.Values, v => v.Restoring || v.Fraction == 1.0);
    }

    [Fact]
    public async Task CancellingAfterReplacementRestoresTheOriginalAndSaysSo()
    {
        string source = Fixture(mediaBytes: LargeMedia);
        string hash = Hash(source);
        var store = new ProjectStore(FilePath("fallback"));
        var fingerprint = ProjectStore.FingerprintOf(source, _media.DurationUs);
        var project = new AdTrimProject(3, source, fingerprint, _media, new(), new() { "segment-0-1000000" }, SidecarLocation.NextToSource);
        store.Save(project);
        string sidecar = File.ReadAllText(store.SidecarPathFor(source));
        using var stop = new CancellationTokenSource();
        var progress = new RecordingProgress(step => { if (step == SourceCommentService.VerifyingStep) stop.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceCommentService.MarkAsync(source, fingerprint, store, stop.Token, progress));

        Assert.Equal(hash, Hash(source));
        Assert.Equal(sidecar, File.ReadAllText(store.SidecarPathFor(source)));
        Assert.False(SourceCommentService.HasPendingUpdate(source));
        Assert.Empty(Directory.GetFiles(_directory, "source.mp4.adtrim-*"));
        var restoring = progress.Values.Where(v => v.Restoring).ToList();
        Assert.NotEmpty(restoring);
        Assert.All(restoring, v => Assert.Equal(SourceCommentService.RestoringStep, v.Step));
        Assert.DoesNotContain(progress.Values, v => v.Fraction == 1.0);
    }

    [Fact]
    public async Task AChangeAfterReplacementFailsVerificationAndKeepsTheOriginalInItsBackup()
    {
        string source = Fixture(mediaBytes: LargeMedia);
        string hash = Hash(source);
        var progress = new RecordingProgress(step => { if (step == SourceCommentService.VerifyingStep) TamperKeepingFingerprint(source); });

        await Assert.ThrowsAsync<IOException>(() => SourceCommentService.MarkAsync(source,
            ProjectStore.FingerprintOf(source, _media.DurationUs), new ProjectStore(FilePath("fallback")), default, progress));

        // The unknown file is neither the original nor the verified update, so recovery refuses to guess.
        Assert.True(SourceCommentService.HasPendingUpdate(source));
        Assert.Equal(hash, Hash(source + ".adtrim-export-comment.bak"));
        Assert.Contains(progress.Values, v => v.Restoring);
        Assert.DoesNotContain(progress.Values, v => v.Fraction == 1.0);
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

    // The layout FFmpeg and Windows write. A second metadata box would hide the title from
    // readers that use the first one.
    [Fact]
    public async Task CommentJoinsAnExistingStandardMetadataBoxInsteadOfAddingASecond()
    {
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isom"));
        var meta = Box("meta", U32(0), Box("hdlr", new byte[8], Encoding.ASCII.GetBytes("mdirappl"), new byte[9]),
            Box("ilst", Box("©nam", Value("Original title")), Box("©cmt", Value("Original user comment")),
                Box("©too", Value("Lavf61.7.100"))));
        var media = Box("mdat", new byte[] { 11, 22, 33, 44 });
        var movie = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", U32(0), U32(1), U32((uint)(ftyp.Length + 8))))))),
            Box("udta", meta));
        string source = FilePath("standard.mp4"), stage = FilePath("standard-staged.mp4");
        File.WriteAllBytes(source, ftyp.Concat(media).Concat(movie).ToArray());

        await Mp4Comments.StageAsync(source, stage, "2.1.14", new(2026, 11, 11), default);

        Assert.Equal(new[] { "Original user comment; Exported with AdTrim 2.1.14 (2026-11-11)" }, Mp4Comments.Read(stage));
        string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(stage));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(bytes, "meta"));
        Assert.Contains("Original title", bytes);
        Assert.Contains("Lavf61.7.100", bytes);
        // The same comment again changes nothing, so the recording is not rewritten.
        Assert.False(Mp4Comments.NeedsUpdate(stage, "2.1.14", new(2026, 11, 11)));
    }

    // The copy is as large as the recording and sits on the disk a DVR may be recording to.
    [Theory]
    [InlineData(10_000_000_000L, 4_000_000_000L, true)]
    [InlineData(4_400_000_000L, 4_000_000_000L, false)]
    public void CommentCopyNeedsRoomForTheRecordingPlusAReserve(long free, long size, bool allowed)
    {
        void Check() => SourceCommentService.EnsureRoomForCopy(FilePath("source.mp4"), size, _ => free);
        if (allowed) Check(); else Assert.Throws<IOException>(Check);
        // A share that reports no free space figure is not refused.
        SourceCommentService.EnsureRoomForCopy(FilePath("source.mp4"), size, _ => null);
    }

    [Fact]
    public void CopyAbandonedByACrashIsRemovedTheNextTimeTheRecordingIsUsed()
    {
        string source = Fixture();
        string abandoned = source + ".adtrim-comment-" + Guid.NewGuid().ToString("N") + ".tmp";
        string unrelated = source + ".adtrim-comment-notes.tmp";
        File.WriteAllText(abandoned, "a recording-sized copy");
        File.WriteAllText(unrelated, "not ours");
        string hash = Hash(source);
        SourceCommentService.Recover(source, new ProjectStore(FilePath("fallback")));
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(hash, Hash(source));
    }

    // A newer app's project in the recording's folder diverts this version's saves to the fallback.
    [Fact]
    public void ExportRecordInTheFallbackIsFoundBesideAProjectFromANewerApp()
    {
        string source = FilePath("recording.ts");
        File.WriteAllText(source, "Source sentinel");
        var store = new ProjectStore(FilePath("fallback"));
        File.WriteAllText(store.SidecarPathFor(source), "{\"schema_version\":999}");
        Assert.Equal(store.AppDataFallbackPathFor(source), store.Save(ExportedProject(source)));
        Assert.True(store.HasExportRecord(source));
    }

    // "Delete project sidecar" removes this export's projects only. One the app could not read
    // was never loaded and may be the only copy of someone's work.
    [Fact]
    public void OnlyProjectsThisVersionCouldLoadAreOfferedForDeletion()
    {
        string source = FilePath("recording.ts");
        File.WriteAllText(source, "Source sentinel");
        var store = new ProjectStore(FilePath("fallback"));
        var project = ExportedProject(source);
        File.WriteAllText(store.SidecarPathFor(source), "{\"schema_version\":999}");
        string fallback = store.Save(project);
        Assert.Equal(new[] { fallback }, store.DeletableSidecarsFor(source, project.Fingerprint));
        File.WriteAllText(store.SidecarPathFor(source), "not json");
        Assert.Equal(new[] { fallback }, store.DeletableSidecarsFor(source, project.Fingerprint));
        Assert.Empty(store.DeletableSidecarsFor(source, project.Fingerprint with { SizeBytes = project.Fingerprint.SizeBytes + 1 }));
    }

    // Choosing a whole drive would otherwise list deleted recordings from its Recycle Bin and an
    // access error for System Volume Information.
    [Fact]
    public async Task DiscoverySkipsWindowsSystemFolders()
    {
        string visible = Fixture("kept.mp4");
        string bin = FilePath("$RECYCLE.BIN");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "deleted.mp4"), "Must not be listed");
        File.SetAttributes(bin, File.GetAttributes(bin) | FileAttributes.Hidden | FileAttributes.System);
        try
        {
            var rows = new List<UnexportedFile>();
            await foreach (var row in UnexportedFiles.ScanAsync(_directory, includeFolders: true)) rows.Add(row);
            Assert.Contains(rows, r => r.Path == visible);
            Assert.DoesNotContain(rows, r => r.Path.Contains("$RECYCLE.BIN"));
        }
        finally { File.SetAttributes(bin, FileAttributes.Directory); }
    }

    // The list only reads. A recording another program has open for writing is still listed, and
    // that program is not blocked while the list is built.
    [Fact]
    public async Task DiscoveryReadsARecordingAnotherProgramHasOpenForWriting()
    {
        string source = Fixture("in-use.mp4");
        using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var rows = new List<UnexportedFile>();
        await foreach (var row in UnexportedFiles.ScanAsync(_directory)) rows.Add(row);
        Assert.Contains(rows, r => r.Path == source && r.Error is null);
    }
}
