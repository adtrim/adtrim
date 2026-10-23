namespace AdTrim.Models;

public enum ExportFormat { Mp4, Ts }

public static class ExportFormats
{
    public static ExportFormat Normalize(ExportFormat format) => format == ExportFormat.Ts ? ExportFormat.Ts : ExportFormat.Mp4;
    public static string Extension(this ExportFormat format) => Normalize(format) == ExportFormat.Ts ? ".ts" : ".mp4";
    public static bool IsSupportedSource(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".ts";
}

/// <summary>One kept segment in an export plan, with its source-time bounds.</summary>
public sealed record ExportSegment(int Index, long StartUs, long EndUs, string PartTitle)
{
    public long DurationUs => EndUs - StartUs;
}

/// <summary>
/// Immutable export plan: the segments to keep + output destination + the
/// resolved primary audio stream index/codec from the source probe.
/// </summary>
public sealed record ExportPlan(
    string SourcePath,
    string OutputPath,
    long SourceDurationUs,
    int PrimaryAudioStreamIndex,
    IReadOnlyList<ExportSegment> KeptSegments,
    Rational FrameRate,
    string PrimaryAudioCodec = "",
    SourceFingerprint? Fingerprint = null,
    bool Overwrite = false,
    ExportFormat Format = ExportFormat.Mp4)
{
    public long ExpectedOutputDurationUs => KeptSegments.Sum(s => s.DurationUs);
}

public enum ExportPhase
{
    Planning,
    Cleanup,
    Restarting,
    EncodingSegment,
    Concatenating,
    Finalizing,
    Validating,
    Done,
    Failed,
}

public sealed record ExportProgress(
    ExportPhase Phase,
    int CurrentSegment,           // 1-based
    int TotalSegments,
    double SegmentPercent,        // 0.0..1.0 for current segment
    double OverallPercent,        // 0.0..1.0
    string Message,
    string? EncoderName = null,
    string? FallbackNotice = null,
    double? RemainingSeconds = null);
