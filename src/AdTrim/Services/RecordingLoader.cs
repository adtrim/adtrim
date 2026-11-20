using AdTrim.Commands;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>What a recording's markers start from when it opens.</summary>
public enum RecordingStart
{
    /// <summary>Its saved project.</summary>
    SavedProject,
    /// <summary>The chapters in the recording.</summary>
    ImportedChapters,
    /// <summary>The whole recording as one part.</summary>
    Blank,
    /// <summary>AdTrim's suggestions: the cached ones, or a new analysis when there are none.</summary>
    AdTrimSuggestions,
}

/// <summary>Everything read from disk to open a recording, and the decision about what it starts with.</summary>
public sealed record LoadedRecording(
    string Path,
    MediaInfo Media,
    SourceFingerprint Fingerprint,
    SidecarLoadResult Sidecar,
    IReadOnlyList<ChapterBoundary> Chapters,
    MarkerSetState? ImportedMarkers,
    string? ImportError,
    MarkerSetState? CachedSuggestions,
    RecordingStart Start,
    AdTrimProject? EarlierProject)
{
    public bool HasSavedProject => Sidecar.Project is not null;

    public bool ImportedMarkersAvailable => ImportedMarkers is not null
        && (ImportedMarkers.Splits.Count > 0 || ImportedMarkers.StartChapterTitle is not null);

    /// <summary>Why the imported markers cannot be used; null when they can.</summary>
    public string? ImportedMarkersUnavailableReason => ImportedMarkersAvailable ? null
        : ImportedMarkers is null ? "Imported markers could not be read from this recording."
        : "This recording has no imported chapter markers.";

    /// <summary>True when a project exists but was not used: damaged, from a newer AdTrim, or for a changed recording.</summary>
    public bool SidecarNotLoaded => Sidecar.Status is SidecarLoadStatus.Corrupt or SidecarLoadStatus.FingerprintMismatch
        or SidecarLoadStatus.UnsupportedVersion;
}

/// <summary>
/// Reads a recording and its project to open it: interrupted comment recovery, probe, project,
/// chapters and cached suggestions. Knows nothing about the window that shows the result.
/// </summary>
public sealed class RecordingLoader(MediaProbeService probe, ChapterImportService chapters, ProjectStore store,
    MarkerSuggestionStore suggestions)
{
    /// <param name="newFiles">What a recording without a usable project starts with.</param>
    /// <param name="probing">Called once comment recovery is done and probing starts.</param>
    public async Task<LoadedRecording> LoadAsync(string path, NewFileMarkerSource newFiles, Action? probing = null)
    {
        await Task.Run(() => SourceCommentService.Recover(path, store));
        probing?.Invoke();
        var media = await probe.ProbeAsync(path);
        var fingerprint = ProjectStore.FingerprintOf(path, media.DurationUs);
        var sidecar = await Task.Run(() => store.Load(path, media));
        IReadOnlyList<ChapterBoundary> imported = Array.Empty<ChapterBoundary>();
        MarkerSetState? importedMarkers = null;
        string? importError = null;
        try
        {
            imported = await chapters.ImportAsync(path, media.DurationUs, includeStart: true);
            importedMarkers = MarkersFromChapters(imported, MarkerSetSource.Imported);
        }
        catch (Exception ex)
        {
            importError = ex.Message;
        }
        return Resolve(path, media, fingerprint, sidecar, imported, importedMarkers, importError, newFiles);
    }

    /// <summary>The decisions after the slow reads; separate so they can be checked without FFmpeg.</summary>
    internal LoadedRecording Resolve(string path, MediaInfo media, SourceFingerprint fingerprint, SidecarLoadResult sidecar, IReadOnlyList<ChapterBoundary> imported,
        MarkerSetState? importedMarkers, string? importError, NewFileMarkerSource newFiles)
    {
        var cached = suggestions.Load(suggestions.Key(path, fingerprint), media.DurationUs);
        var start = Decide(sidecar, newFiles);
        // A retagged or restored recording fails the identity check although its markers may still fit.
        var earlier = start != RecordingStart.SavedProject && sidecar.Status == SidecarLoadStatus.FingerprintMismatch
            ? store.LoadForChangedRecording(path, media.DurationUs) : null;
        return new(path, media, fingerprint, sidecar, imported, importedMarkers, importError, cached, start, earlier);
    }

    /// <summary>A usable saved project wins; otherwise the new-recording preference decides.</summary>
    public static RecordingStart Decide(SidecarLoadResult sidecar, NewFileMarkerSource newFiles)
    {
        if (sidecar.Project is not null && sidecar.Status is SidecarLoadStatus.Loaded or SidecarLoadStatus.LoadedWithMtimeWarning)
            return RecordingStart.SavedProject;
        return newFiles switch
        {
            NewFileMarkerSource.AdTrim => RecordingStart.AdTrimSuggestions,
            NewFileMarkerSource.None => RecordingStart.Blank,
            _ => RecordingStart.ImportedChapters,
        };
    }

    /// <summary>A marker set from imported chapters: a chapter at zero names the start, the rest become splits.</summary>
    public static MarkerSetState MarkersFromChapters(IReadOnlyList<ChapterBoundary> chapters, MarkerSetSource source)
    {
        var start = chapters.FirstOrDefault(c => c.TimeUs == 0)?.Title;
        var splits = chapters.Where(c => c.TimeUs > 0).Select(c => new PersistedSplit(
            Guid.NewGuid().ToString("N")[..12], c.TimeUs, SplitSource.Chapter, null, null, false, c.Title)).ToArray();
        var preliminary = new MarkerSetState(splits, Array.Empty<string>(), start, source, null);
        return preliminary with { BaselineId = preliminary.ContentId() };
    }
}
