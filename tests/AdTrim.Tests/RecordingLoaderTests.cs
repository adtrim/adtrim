using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

// Opening a recording decides what its markers start from. A saved project must always win, a
// project AdTrim cannot use must never be silently replaced, and the new-recording setting
// decides only when there is no usable project.
public sealed class RecordingLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-loader-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly ProjectStore _store;
    private readonly MarkerSuggestionStore _suggestions;
    private readonly RecordingLoader _loader;
    private static readonly MediaInfo Media = new(1_800_000_000, "h264", 1920, 1080, new Rational(30000, 1001),
        [new AudioStream(1, "ac3", 6, true)], 1);

    public RecordingLoaderTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "Show - S01E01.mp4");
        File.WriteAllBytes(_source, new byte[4096]);
        _store = new ProjectStore(Path.Combine(_directory, "data", "projects"));
        _suggestions = new MarkerSuggestionStore(Path.Combine(_directory, "data"));
        // Probing and chapter import need FFmpeg; these checks give the loader their results directly.
        _loader = new RecordingLoader(null!, null!, _store, _suggestions);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private LoadedRecording Open(NewFileMarkerSource newFiles, IReadOnlyList<ChapterBoundary>? chapters = null, string? importError = null)
    {
        chapters ??= Array.Empty<ChapterBoundary>();
        var markers = importError is null ? RecordingLoader.MarkersFromChapters(chapters, MarkerSetSource.Imported) : null;
        return _loader.Resolve(_source, Media, ProjectStore.FingerprintOf(_source, Media.DurationUs), _store.Load(_source, Media),
            importError is null ? chapters : Array.Empty<ChapterBoundary>(), markers, importError, newFiles);
    }

    private AdTrimProject SaveProject(params long[] splits)
    {
        var project = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _source, ProjectStore.FingerprintOf(_source, Media.DurationUs), Media,
            splits.Select((t, i) => new PersistedSplit("split" + i, t, SplitSource.Manual, null, null, true, null)).ToList(),
            new(), SidecarLocation.NextToSource);
        _store.Save(project);
        return project;
    }

    [Theory]
    [InlineData(NewFileMarkerSource.Imported)]
    [InlineData(NewFileMarkerSource.AdTrim)]
    [InlineData(NewFileMarkerSource.None)]
    public void ASavedProjectWinsOverTheNewRecordingSetting(NewFileMarkerSource newFiles)
    {
        SaveProject(600_000_000);
        var loaded = Open(newFiles);
        Assert.Equal(RecordingStart.SavedProject, loaded.Start);
        Assert.True(loaded.HasSavedProject);
        Assert.False(loaded.SidecarNotLoaded);
        Assert.Equal(600_000_000, Assert.Single(loaded.Sidecar.Project!.Splits).TimeUs);
    }

    [Theory]
    [InlineData(NewFileMarkerSource.Imported, RecordingStart.ImportedChapters)]
    [InlineData(NewFileMarkerSource.AdTrim, RecordingStart.AdTrimSuggestions)]
    [InlineData(NewFileMarkerSource.None, RecordingStart.Blank)]
    public void WithoutAProjectTheNewRecordingSettingDecides(NewFileMarkerSource newFiles, RecordingStart expected)
    {
        var loaded = Open(newFiles);
        Assert.Equal(expected, loaded.Start);
        Assert.False(loaded.HasSavedProject);
        Assert.False(loaded.SidecarNotLoaded);
        Assert.Null(loaded.EarlierProject);
    }

    [Fact]
    public void AProjectSavedBeforeTheSourceWasTouchedStillLoadsWithAWarning()
    {
        SaveProject(600_000_000);
        File.SetLastWriteTimeUtc(_source, DateTime.UtcNow.AddMinutes(5));
        var loaded = Open(NewFileMarkerSource.None);
        Assert.Equal(SidecarLoadStatus.LoadedWithMtimeWarning, loaded.Sidecar.Status);
        Assert.Equal(RecordingStart.SavedProject, loaded.Start);
        Assert.NotNull(loaded.Sidecar.Message);
    }

    // A retagged or restored recording fails the identity check, but its markers may still fit: they are
    // offered, not loaded and not lost.
    [Fact]
    public void AChangedRecordingOffersItsEarlierProjectInsteadOfLoadingIt()
    {
        SaveProject(600_000_000);
        File.AppendAllText(_source, "retagged");
        var loaded = Open(NewFileMarkerSource.Imported);
        Assert.Equal(SidecarLoadStatus.FingerprintMismatch, loaded.Sidecar.Status);
        Assert.True(loaded.SidecarNotLoaded);
        Assert.Equal(RecordingStart.ImportedChapters, loaded.Start);
        Assert.Equal(600_000_000, Assert.Single(loaded.EarlierProject!.Splits).TimeUs);
    }

    [Fact]
    public void AProjectFromANewerAdTrimIsReportedAndLeftAlone()
    {
        const string future = "{\"schema_version\":999}";
        File.WriteAllText(_store.SidecarPathFor(_source), future);
        var loaded = Open(NewFileMarkerSource.None);
        Assert.Equal(SidecarLoadStatus.UnsupportedVersion, loaded.Sidecar.Status);
        Assert.True(loaded.SidecarNotLoaded);
        Assert.Null(loaded.EarlierProject);
        Assert.Equal(RecordingStart.Blank, loaded.Start);
        Assert.Equal(future, File.ReadAllText(_store.SidecarPathFor(_source)));
    }

    [Fact]
    public void ADamagedProjectIsReportedAndTheSettingDecides()
    {
        File.WriteAllText(_store.SidecarPathFor(_source), "{");
        var loaded = Open(NewFileMarkerSource.AdTrim);
        Assert.Equal(SidecarLoadStatus.Corrupt, loaded.Sidecar.Status);
        Assert.True(loaded.SidecarNotLoaded);
        Assert.Equal(RecordingStart.AdTrimSuggestions, loaded.Start);
        Assert.Null(loaded.EarlierProject);
    }

    [Fact]
    public void CachedSuggestionsAreFoundForTheRecordingAsItIsNow()
    {
        Assert.Null(Open(NewFileMarkerSource.AdTrim).CachedSuggestions);
        var state = new MarkerSetState([new PersistedSplit("s1", 900_000_000, SplitSource.Manual, null, null, false, "Break")],
            Array.Empty<string>(), null, MarkerSetSource.AdTrim, null);
        state = state with { BaselineId = state.ContentId() };
        _suggestions.Save(_suggestions.Key(_source, ProjectStore.FingerprintOf(_source, Media.DurationUs)), state);
        Assert.NotNull(Open(NewFileMarkerSource.AdTrim).CachedSuggestions);
        // Suggestions for an earlier version of the file do not apply to the changed recording.
        File.AppendAllText(_source, "retagged");
        Assert.Null(Open(NewFileMarkerSource.AdTrim).CachedSuggestions);
    }

    [Fact]
    public void ImportedChaptersBecomeAMarkerSetNamedByTheChapterAtZero()
    {
        var loaded = Open(NewFileMarkerSource.Imported,
            [new ChapterBoundary(0, "Cold open"), new ChapterBoundary(300_000_000, "Act 1"), new ChapterBoundary(900_000_000, null)]);
        var markers = loaded.ImportedMarkers!;
        Assert.Equal("Cold open", markers.StartChapterTitle);
        Assert.Equal([300_000_000L, 900_000_000L], markers.Splits.Select(s => s.TimeUs));
        Assert.Equal("Act 1", markers.Splits[0].ChapterTitle);
        Assert.All(markers.Splits, s => Assert.Equal(SplitSource.Chapter, s.Source));
        Assert.Equal(markers.ContentId(), markers.BaselineId);
        Assert.True(loaded.ImportedMarkersAvailable);
        Assert.Null(loaded.ImportedMarkersUnavailableReason);
    }

    [Fact]
    public void MissingOrUnreadableChaptersSayWhyThereAreNoImportedMarkers()
    {
        var none = Open(NewFileMarkerSource.Imported);
        Assert.False(none.ImportedMarkersAvailable);
        Assert.Equal("This recording has no imported chapter markers.", none.ImportedMarkersUnavailableReason);
        var unreadable = Open(NewFileMarkerSource.Imported, importError: "ffprobe failed");
        Assert.Null(unreadable.ImportedMarkers);
        Assert.Equal("ffprobe failed", unreadable.ImportError);
        Assert.Equal("Imported markers could not be read from this recording.", unreadable.ImportedMarkersUnavailableReason);
    }
}
