using AdTrim.Models;

namespace AdTrim.Encoders;

/// <summary>
/// Export encoding strategy. All implementations preserve the shared cut and audio arguments.
/// </summary>
public interface IEncoderStrategy
{
    /// <summary>Display name shown in settings.</summary>
    string DisplayName { get; }
    bool IsHardware => false;
    void ValidateDevice() { }

    /// <summary>
    /// Build the FFmpeg argument list that produces one segment's intermediate
    /// MP4 from the source. The runner appends nothing else.
    /// </summary>
    IReadOnlyList<string> BuildSegmentArgs(
        string sourcePath,
        ExportSegment segment,
        int primaryAudioStreamIndex,
        string outputPath);
}
