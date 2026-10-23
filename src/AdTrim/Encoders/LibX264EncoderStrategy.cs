using System.Globalization;
using AdTrim.Models;

namespace AdTrim.Encoders;

/// <summary>
/// V1 encoder: libx264 video + AC3 stream-copy. Always deinterlaces with
/// bwdif (interlacing policy). The source's chapter list is
/// stripped (`-map_chapters -1`) - chapters are injected at the concat
/// step with our own Part-N metadata.
/// </summary>
public sealed class LibX264EncoderStrategy : IEncoderStrategy
{
    public string DisplayName => "Software (H.264)";

    // TS seeking can land after the preceding GOP. Decode a bounded lead-in
    // before the accurate output seek, including long broadcast GOPs.
    private const double TransportPreRollSec = 10.0;

    public IReadOnlyList<string> BuildSegmentArgs(
        string sourcePath,
        ExportSegment segment,
        int primaryAudioStreamIndex,
        string outputPath)
        => BuildArgs(sourcePath, segment, primaryAudioStreamIndex, outputPath,
            Array.Empty<string>(), "bwdif=mode=send_frame:parity=auto:deint=all",
            new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p" });

    internal static IReadOnlyList<string> BuildArgs(string sourcePath, ExportSegment segment,
        int primaryAudioStreamIndex, string outputPath, IReadOnlyList<string> deviceArgs,
        string filter, IReadOnlyList<string> encoderArgs)
    {
        // Two-stage seek: coarse `-ss` *before* `-i` jumps cheaply to a
        // decoder lead-in before the cut; fine `-ss` *after* `-i` then
        // does the accurate decode-and-discard up to the requested frame.
        //
        // Single-stage `-ss <start> -i` was previously used and caused up to
        // one GOP (~1s on the BBT MPEG-2 fixture) of pre-cut audio to bleed
        // into the output: the input-seek lands on the preceding video
        // keyframe, and `-c:a copy` writes every audio packet from there
        // forward. Verified against the BBT fixture 2026-05-16: output
        // video.start_time was 0.917 while audio.start_time was 0.000.
        //
        // With the two-stage seek, both streams start within ~50ms of each
        // other - the residual is the true AC3 packet alignment (~32ms).
        var startSec = segment.StartUs / 1_000_000.0;
        var endSec   = segment.EndUs   / 1_000_000.0;
        bool transport = System.IO.Path.GetExtension(sourcePath).Equals(".ts", StringComparison.OrdinalIgnoreCase);
        var coarse   = Math.Max(0, startSec - (transport ? TransportPreRollSec : 2.0));
        var fine     = startSec - coarse;
        var dur      = endSec - startSec;

        var coarseStr = coarse.ToString("0.000000", CultureInfo.InvariantCulture);
        var fineStr   = fine  .ToString("0.000000", CultureInfo.InvariantCulture);
        var durStr    = dur   .ToString("0.000000", CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-nostats",
            // Machine-readable progress on stdout (key=value records every
            // ~200ms). ExportService parses these into per-segment percent
            // updates so the UI doesn't sit at "Encoding part X/Y" for
            // minutes with no movement. Costs nothing if no one listens.
            "-progress", "pipe:1",
        };
        args.AddRange(deviceArgs);
        // Seeking to zero can discard audio preceding the first video keyframe.
        if (coarse > 0) args.AddRange(new[] { "-ss", coarseStr });
        args.AddRange(new[] {
            "-i", sourcePath,
            "-ss", fineStr,
            "-t",  durStr,
            "-map", "0:v:0",
            "-map", $"0:{primaryAudioStreamIndex}",
            "-map_chapters", "-1",
            "-vf", filter,
        });
        // TS-derived timestamps can make automatic sync drop a boundary frame even for CFR sources.
        if (transport)
            args.AddRange(new[] { "-fps_mode", "cfr" });
        args.AddRange(encoderArgs);
        args.AddRange(new[] {
            "-c:a", "copy",
            // MP4 can retain negative B-frame DTS. Shifting it to zero would
            // move copied audio by an encoder-dependent amount on every part.
            "-avoid_negative_ts", "disabled",
            "-f", "mp4",
            outputPath,
        });
        return args;
    }
}
