using AdTrim.Models;

namespace AdTrim.Encoders;

/// <summary>
/// Encoder seam - V1 ships LibX264 only. Future variants (NVENC / QSV / AMF
/// / smart-rendering) implement the same interface and the user picks via
/// Settings.
/// </summary>
public interface IEncoderStrategy
{
    /// <summary>Display name shown in settings.</summary>
    string DisplayName { get; }

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
