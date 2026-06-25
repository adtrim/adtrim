namespace AdTrim.Models;

public enum SplitSource { Chapter, Manual, Refined }

public sealed record SourceFingerprint(long SizeBytes, long ModifiedUnixMs, long DurationUs);

public sealed record PersistedSplit(
    string Id,
    long TimeUs,
    SplitSource Source,
    long? OriginalTimeUs,
    Confidence? Confidence,
    bool Confirmed);

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
    SidecarLocation SidecarLocation)
{
    public const int CurrentSchemaVersion = 1;
}
