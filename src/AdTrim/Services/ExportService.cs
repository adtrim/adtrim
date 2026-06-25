using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AdTrim.Encoders;
using AdTrim.Models;

namespace AdTrim.Services;

public sealed class ExportException : Exception
{
    public ExportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Two-phase exporter (export pipeline V1):
///   Phase 1 - one FFmpeg invocation per kept segment produces a libx264 +
///             AC3-stream-copy intermediate MP4. Sequential, not parallel.
///   Phase 2 - single FFmpeg concat-demuxer + FFMETADATA chapter inject.
///
/// Post-export validation runs `ffprobe` against the output to verify
/// chapter count + codec sanity + duration tolerance.
///
/// `bin_data` and caption streams are dropped via `-map 0:v -map 0:a`.
/// </summary>
public sealed class ExportService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FfmpegRunner _runner;
    private readonly IEncoderStrategy _encoder;

    public ExportService(FfmpegRunner runner, IEncoderStrategy encoder)
    {
        _runner = runner;
        _encoder = encoder;
    }

    public async Task RunExportAsync(
        ExportPlan plan,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new ExportProgress(ExportPhase.Planning, 0, plan.KeptSegments.Count, 0, 0,
            $"Preparing {plan.KeptSegments.Count} segment(s)…"));

        var tempDir = Path.Combine(Path.GetTempPath(), "AdTrim", "exp-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        Directory.CreateDirectory(tempDir);
        bool success = false;

        try
        {
            // Phase 1: per-segment intermediates.
            //
            // Overall progress is weighted by *source duration*, not segment
            // count. A 22-second intro and a 22-minute episode body take
            // wildly different amounts of wall-clock time to re-encode, so
            // counting them as equal slices made the bar jerk forward (and
            // the ETA jerk down) when a short segment finished. Encode speed
            // is roughly constant per source-second under the same codec
            // settings, so source-duration weighting is a much closer proxy
            // for wall-clock cost.
            var intermediatePaths = new List<string>();
            int total = plan.KeptSegments.Count;
            long totalDurationUs = Math.Max(1, plan.KeptSegments.Sum(s => s.DurationUs));
            long completedBeforeUs = 0;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seg = plan.KeptSegments[i];
                var outPath = Path.Combine(tempDir, $"seg_{i:000}.mp4");
                var args = _encoder.BuildSegmentArgs(
                    plan.SourcePath, seg, plan.PrimaryAudioStreamIndex, outPath);

                // Capture loop-locals for the streaming callback closure.
                int segIndex1Based = i + 1;
                long segDurationUs = Math.Max(1, seg.DurationUs);
                long doneBeforeUs = completedBeforeUs;
                string message = $"Encoding {seg.PartTitle} ({segIndex1Based}/{total})";

                progress?.Report(new ExportProgress(
                    ExportPhase.EncodingSegment, segIndex1Based, total,
                    SegmentPercent: 0,
                    OverallPercent: doneBeforeUs / (double)totalDurationUs * 0.95,
                    Message: message));

                // ffmpeg -progress pipe:1 emits records of ~10 key=value lines
                // every ~200ms; we only care about `out_time_us=N` (output
                // microseconds produced so far in *this segment*). Report
                // fires from a threadpool thread; if the caller's IProgress
                // is a Progress<T>, it marshals to its construction-thread
                // automatically.
                void OnStdoutLine(string line)
                {
                    var us = ExportService.TryParseProgressOutTimeUs(line);
                    if (us is null) return;
                    var doneInSegmentUs = Math.Min(us.Value, segDurationUs);
                    var segPct = doneInSegmentUs / (double)segDurationUs;
                    var overall = (doneBeforeUs + doneInSegmentUs) / (double)totalDurationUs * 0.95;
                    progress?.Report(new ExportProgress(
                        ExportPhase.EncodingSegment, segIndex1Based, total,
                        SegmentPercent: segPct,
                        OverallPercent: overall,
                        Message: message));
                }

                var r = await _runner.RunFfmpegAsync(args, OnStdoutLine, ct).ConfigureAwait(false);
                if (!r.Success)
                    throw new ExportException(
                        $"Segment {segIndex1Based} encode failed (exit {r.ExitCode}):\n{Tail(r.Stderr, 1500)}");
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                    throw new ExportException($"Segment {segIndex1Based} produced no output: {outPath}");

                intermediatePaths.Add(outPath);
                completedBeforeUs += segDurationUs;
            }

            // Phase 2: concat-list + FFMETADATA1 chapter file, then a single mux pass.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(
                ExportPhase.Concatenating, plan.KeptSegments.Count, plan.KeptSegments.Count,
                0, 0.95, "Concatenating + writing chapters…"));

            var concatPath = Path.Combine(tempDir, "concat.txt");
            await WriteConcatListAsync(concatPath, intermediatePaths, ct).ConfigureAwait(false);

            var chapterPath = Path.Combine(tempDir, "chapters.ffmetadata");
            var cumulativeMs = await BuildChapterMetadataAsync(
                chapterPath, intermediatePaths, plan.KeptSegments, ct).ConfigureAwait(false);

            // Provenance markers, embedded in the MP4 itself. These travel
            // with the file across copies / downloads / cloud roundtrips,
            // unlike the filesystem "Date modified" stamp.
            //
            // We use `comment` (not `encoder`) for the "what tool made this"
            // marker: FFmpeg's MP4 muxer always overwrites the `encoder`
            // atom with libavformat's own version string (`Lavf62.10.101`)
            // regardless of FFMETADATA or output-level `-metadata` - even
            // with `-fflags +bitexact -flags +bitexact`. `comment`, by
            // contrast, is not auto-written by the muxer and survives the
            // mux step intact. Bonus: `comment` is visible in the default
            // Windows Explorer Details column without needing a column
            // customization.
            //
            //   comment       → Windows Explorer "Comments"
            //   creation_time → Windows Explorer "Media created"
            var creationTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var muxArgs = new[]
            {
                "-y", "-hide_banner", "-nostats",
                "-f", "concat", "-safe", "0", "-i", concatPath,
                "-i", chapterPath,
                "-map", "0", "-map_metadata", "1", "-map_chapters", "1",
                "-c", "copy",
                "-metadata", $"comment=Edited by AdTrim {AppVersion.Display}",
                "-metadata", $"creation_time={creationTime}",
                "-movflags", "+faststart",
                plan.OutputPath,
            };
            var muxR = await _runner.RunFfmpegAsync(muxArgs, ct).ConfigureAwait(false);
            if (!muxR.Success)
                throw new ExportException($"Concat mux failed (exit {muxR.ExitCode}):\n{Tail(muxR.Stderr, 1500)}");

            // Phase 3: post-export validation.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(
                ExportPhase.Validating, plan.KeptSegments.Count, plan.KeptSegments.Count,
                0, 0.99, "Validating output…"));

            await ValidateOutputAsync(plan, cumulativeMs, ct).ConfigureAwait(false);

            progress?.Report(new ExportProgress(
                ExportPhase.Done, plan.KeptSegments.Count, plan.KeptSegments.Count,
                1, 1, "Export complete"));
            success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report(new ExportProgress(
                ExportPhase.Failed, 0, plan.KeptSegments.Count, 0, 0, ex.Message));
            throw;
        }
        finally
        {
            // Temp dir deleted on success; preserved on failure for diagnosis.
            if (success)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private static async Task WriteConcatListAsync(
        string path, IEnumerable<string> intermediatePaths, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var p in intermediatePaths)
        {
            // Concat demuxer requires file paths quoted and forward-slashed for safety.
            var escaped = p.Replace("\\", "/").Replace("'", "'\\''");
            sb.Append("file '").Append(escaped).Append("'\n");
        }
        // FFmpeg's concat demuxer rejects the UTF-8 BOM (`Encoding.UTF8`
        // emits one by default). Use a BOM-less UTF-8 encoder.
        await File.WriteAllTextAsync(path, sb.ToString(), Utf8NoBom, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the FFMETADATA1 chapter file. Chapter timecodes are cumulative
    /// durations measured from the intermediate files via ffprobe, NOT from
    /// the source plan - that's what avoids drift from re-encode rounding.
    /// </summary>
    private async Task<long> BuildChapterMetadataAsync(
        string outPath,
        IReadOnlyList<string> intermediatePaths,
        IReadOnlyList<ExportSegment> segments,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(";FFMETADATA1");

        long cumMs = 0;
        for (int i = 0; i < intermediatePaths.Count; i++)
        {
            var dur = await ProbeDurationMsAsync(intermediatePaths[i], ct).ConfigureAwait(false);
            var startMs = cumMs;
            var endMs = cumMs + dur;
            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={startMs.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"END={endMs.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"title={segments[i].PartTitle}");
            cumMs = endMs;
        }
        await File.WriteAllTextAsync(outPath, sb.ToString(), Utf8NoBom, ct).ConfigureAwait(false);
        return cumMs;
    }

    private async Task<long> ProbeDurationMsAsync(string path, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nokey=1:noprint_wrappers=1",
            path,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Stdout))
            throw new ExportException($"Failed to probe segment duration: {path}");
        var sec = double.Parse(r.Stdout.Trim(), CultureInfo.InvariantCulture);
        return (long)Math.Round(sec * 1000.0);
    }

    private async Task ValidateOutputAsync(ExportPlan plan, long expectedDurationMs, CancellationToken ct)
    {
        // 1. Output exists and non-zero.
        if (!File.Exists(plan.OutputPath))
            throw new ExportException("Output file was not created.");
        if (new FileInfo(plan.OutputPath).Length == 0)
            throw new ExportException("Output file is empty.");

        // 2. Probe: chapter count + codecs + duration.
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_chapters", "-show_streams", "-show_format",
            plan.OutputPath,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success) throw new ExportException("ffprobe on output failed: " + r.Stderr);

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;

        int nbChapters = root.TryGetProperty("chapters", out var chs) ? chs.GetArrayLength() : 0;
        if (nbChapters != plan.KeptSegments.Count)
            throw new ExportException(
                $"Output has {nbChapters} chapters, expected {plan.KeptSegments.Count}.");

        if (root.TryGetProperty("streams", out var streams))
        {
            // Expected audio codec = whatever the source's primary stream was,
            // since `-c:a copy` preserves the codec. Empty string means the
            // probe didn't tell us (legacy callers); fall back to "any audio
            // stream is fine" rather than a hardcoded "ac3" that breaks AAC
            // and other non-AC3 sources.
            var expectedAudioCodec = plan.PrimaryAudioCodec;
            bool sawVideoH264 = false, sawExpectedAudio = false;
            int audioStreamCount = 0;
            string? firstAudioCodec = null;
            foreach (var s in streams.EnumerateArray())
            {
                var t = s.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                var c = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                if (t == "video" && c == "h264") sawVideoH264 = true;
                if (t == "audio")
                {
                    audioStreamCount++;
                    firstAudioCodec ??= c;
                    if (string.IsNullOrEmpty(expectedAudioCodec) || c == expectedAudioCodec)
                        sawExpectedAudio = true;
                }
                // NOTE: a `bin_data` (codec_tag=text, handler=SubtitleHandler) stream
                // may legitimately appear here - the MP4 muxer adds one as the
                // navigable representation of our chapter list. The source's data
                // stream was already dropped at the per-segment-encode step via
                // `-map 0:v -map 0:a`, so anything appearing here is muxer-added,
                // not pass-through. Don't reject it.
            }
            if (!sawVideoH264) throw new ExportException("Output is missing the expected h264 video stream.");
            if (!sawExpectedAudio)
                throw new ExportException(
                    string.IsNullOrEmpty(expectedAudioCodec)
                        ? "Output has no audio stream."
                        : $"Output's audio codec is {firstAudioCodec ?? "(none)"}, expected {expectedAudioCodec} (matching the source's stream-copied primary).");
            if (audioStreamCount > 1)
                throw new ExportException(
                    $"Output has {audioStreamCount} audio streams; expected exactly 1 (primary, stream-copied).");
        }

        if (root.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("duration", out var dur)
            && double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
        {
            var actualMs = (long)Math.Round(sec * 1000.0);
            if (Math.Abs(actualMs - expectedDurationMs) > 200)
                throw new ExportException(
                    $"Output duration {actualMs}ms differs from expected {expectedDurationMs}ms by >200ms.");
        }
    }

    private static string Tail(string s, int chars)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Length <= chars ? s : s[^chars..];

    /// <summary>
    /// Parse one line of ffmpeg's <c>-progress pipe:1</c> output. Returns the
    /// `out_time_us` value (output microseconds produced so far) if this line
    /// carries it, otherwise <c>null</c>. ffmpeg emits records of ~10 lines
    /// every ~200ms; only the <c>out_time_us=N</c> line is interesting for
    /// our progress UI. Lines we don't recognise (frame/fps/bitrate/etc) and
    /// the literal <c>out_time_us=N/A</c> emitted before the first frame
    /// both return null.
    /// </summary>
    internal static long? TryParseProgressOutTimeUs(string line)
    {
        const string Prefix = "out_time_us=";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = line.AsSpan(Prefix.Length).Trim();
        if (rest.Length == 0 || rest.SequenceEqual("N/A")) return null;
        return long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) ? us : null;
    }
}
