namespace AdTrim.Models;

public enum SplitSource { Chapter, Manual, Refined }

public enum MarkerSetSource { Imported, AdTrim, Unknown, Blank }

public sealed record SourceFingerprint(long SizeBytes, long ModifiedUnixMs, long DurationUs);

public sealed record ExportProvenance(string Application, string Version, DateTimeOffset ExportedAtUtc,
    SourceFingerprint SourceFingerprint);

public sealed record PersistedSplit(
    string Id,
    long TimeUs,
    SplitSource Source,
    long? OriginalTimeUs,
    Confidence? Confidence,
    bool Confirmed,
    string? ChapterTitle = null);

public enum SidecarLocation { NextToSource, AppdataFallback }

/// <summary>
/// Sidecar schema persisted as JSON next to the source MP4. Microsecond
/// integer times; undo stack is NOT persisted (in-memory only).
/// </summary>
public sealed record AdTrimProject(
    int SchemaVersion,
    string SourcePath,
    SourceFingerprint Fingerprint,
    MediaInfo Media,
    List<PersistedSplit> Splits,
    List<string> ExcludedSegmentIds,
    SidecarLocation SidecarLocation,
    string? StartChapterTitle = null,
    MarkerSetSource MarkerSetSource = MarkerSetSource.Unknown,
    string? MarkerBaselineId = null,
    ExportProvenance? LastExport = null)
{
    public const int CurrentSchemaVersion = 4;
}
