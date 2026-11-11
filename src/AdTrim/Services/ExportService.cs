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
        CancellationToken ct = default,
        bool allowSoftwareFallback = false)
    {
        var reporting = new ForwardProgress(p => progress?.Report(p with { EncoderName = _encoder.DisplayName }));
        try { await RunAttemptAsync(plan, reporting, ct).ConfigureAwait(false); }
        catch (HardwareExportException) when (allowSoftwareFallback && _encoder.IsHardware && !ct.IsCancellationRequested)
        {
            const string notice = "Hardware encoding failed. Export restarted using software.";
            var software = new LibX264EncoderStrategy();
            progress?.Report(new(ExportPhase.Restarting, 0, plan.KeptSegments.Count, 0, 0, notice, software.DisplayName, notice));
            var fallbackProgress = new ForwardProgress(p => progress?.Report(p with { FallbackNotice = notice }));
            await new ExportService(_runner, software).RunExportAsync(plan, fallbackProgress, ct).ConfigureAwait(false);
        }
    }

    // Found before encoding instead of at the final step, which can be an hour later.
    private static void EnsureDestinationWritable(string destination, bool overwrite)
    {
        string folder = Path.GetDirectoryName(destination)!;
        try
        {
            string probe = Path.Combine(folder, ".adtrim-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportException($"AdTrim cannot save to {folder}: {ex.Message} Choose a different folder.", ex);
        }
        if (!overwrite || !File.Exists(destination)) return;
        try
        {
            using (new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportException($"\"{Path.GetFileName(destination)}\" cannot be replaced. It is open in another program or read-only. "
                + "Close it or choose a different filename.", ex);
        }
    }

    private static string? KeepUnderFreeName(string staging, string destination)
    {
        string folder = Path.GetDirectoryName(destination)!, name = Path.GetFileNameWithoutExtension(destination), extension = Path.GetExtension(destination);
        for (int n = 2; n < 100; n++)
        {
            string candidate = Path.Combine(folder, $"{name} ({n}){extension}");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try { File.Move(staging, candidate); return candidate; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private sealed class ForwardProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => report(value); }

    private async Task RunAttemptAsync(ExportPlan plan, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(plan.Format) || !Path.GetExtension(plan.OutputPath).Equals(plan.Format.Extension(), StringComparison.OrdinalIgnoreCase))
            throw new ExportException("The output filename does not match the selected file format.");
        ExportSafety.EnsureDifferentFiles(plan.SourcePath, plan.OutputPath);
        if (File.Exists(plan.OutputPath) && !plan.Overwrite)
            throw new ExportException("Output already exists. Confirm overwrite first.");
        if (plan.KeptSegments.Count == 0 || plan.KeptSegments.Any(s => s.StartUs < 0 || s.EndUs > plan.SourceDurationUs || s.DurationUs <= 0))
            throw new ExportException("Invalid kept segments.");
        var sourceInfo = new FileInfo(plan.SourcePath);
        var sourceSize = sourceInfo.Length;
        var sourceWrite = sourceInfo.LastWriteTimeUtc;
        var current = await new MediaProbeService(_runner).ProbeAsync(plan.SourcePath, ct).ConfigureAwait(false);
        if (current.DurationUs != plan.SourceDurationUs || (plan.Fingerprint is { } fp &&
            (fp.SizeBytes != sourceSize || fp.DurationUs != current.DurationUs)))
            throw new ExportException("Source changed since the project was opened. Reopen it before exporting.");
        _encoder.ValidateDevice();
        var destination = Path.GetFullPath(plan.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureDestinationWritable(destination, plan.Overwrite);
        var userCancellation = ct;
        await using var disk = new ExportDiskGuard(new[] { Path.GetTempPath(), Path.GetDirectoryName(destination)! }, ct);
        ct = disk.Token;
        using var workspace = ExportWorkspace.Create(destination);
        var staging = workspace.StagingPath;
        progress?.Report(new ExportProgress(ExportPhase.Planning, 0, plan.KeptSegments.Count, 0, 0,
            $"Preparing {plan.KeptSegments.Count} segment(s)…"));

        var tempDir = workspace.TempDirectory;

        try
        {
            await ValidateAudioCopyAsync(plan, current, tempDir, ct).ConfigureAwait(false);

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
            var intermediateStartsUs = new List<long>();
            var intermediateVideoStartsUs = new List<long>();
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
                {
                    if (_encoder.IsHardware && HardwareExportException.IsDeviceFailure(r.Stderr))
                        throw new HardwareExportException($"{_encoder.DisplayName} failed: {Tail(r.Stderr, 1200)}");
                    throw new ExportException(
                        $"Segment {segIndex1Based} encode failed (exit {r.ExitCode}):\n{Tail(r.Stderr, 1500)}");
                }
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                    throw new ExportException($"Segment {segIndex1Based} produced no output: {outPath}");

                intermediatePaths.Add(outPath);
                var timing = await ProbeSegmentTimingAsync(outPath, ct).ConfigureAwait(false);
                long measuredMs = timing.DurationMs;
                intermediateStartsUs.Add(timing.StartUs);
                intermediateVideoStartsUs.Add(timing.VideoStartUs);
                if (Math.Abs(measuredMs * 1000 - seg.DurationUs) > Math.Max(200_000, 2_000_000 / Math.Max(1, current.FrameRate.AsDouble)))
                    throw new ExportException($"Part {segIndex1Based} duration differs from the requested material. Output was not published.");
                completedBeforeUs += segDurationUs;
            }

            // Phase 2: concat-list + FFMETADATA1 chapter file, then a single mux pass.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(
                ExportPhase.Concatenating, plan.KeptSegments.Count, plan.KeptSegments.Count,
                0, 0.95, "Preparing parts and chapters…"));

            var concatPath = Path.Combine(tempDir, "concat.txt");
            await WriteConcatListAsync(concatPath, intermediatePaths, plan.KeptSegments, intermediateStartsUs, ct).ConfigureAwait(false);

            var chapterPath = Path.Combine(tempDir, "chapters.ffmetadata");
            long cumulativeMs = (long)Math.Round(plan.ExpectedOutputDurationUs / 1000.0);
            if (plan.Format == ExportFormat.Mp4)
                await BuildChapterMetadataAsync(chapterPath, plan.KeptSegments, intermediateStartsUs[0], intermediateVideoStartsUs, ct).ConfigureAwait(false);

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
            //   comment       -> Windows Explorer "Comments"
            //   creation_time -> Windows Explorer "Media created"
            var creationTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var muxArgs = new List<string>
            {
                "-y", "-hide_banner", "-nostats", "-progress", "pipe:1", "-stats_period", "0.2",
                "-f", "concat", "-safe", "0", "-i", concatPath,
            };
            if (plan.Format == ExportFormat.Mp4)
            {
                muxArgs.AddRange(new[] { "-i", chapterPath, "-map", "0", "-map_metadata", "1", "-map_chapters", "1",
                    "-c", "copy", "-metadata", $"comment=Edited by AdTrim {AppVersion.Display}",
                    "-metadata", $"creation_time={creationTime}", "-movflags", "+faststart", "-f", "mp4" });
            }
            else
            {
                muxArgs.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
                    // A shared audio PES would hide timestamp gaps at joins.
                    "-c", "copy", "-pes_payload_size", "0", "-f", "mpegts" });
            }
            muxArgs.Add(staging);
            var muxTimer = System.Diagnostics.Stopwatch.StartNew();
            var muxLock = new object();
            bool finalizing = false;
            double muxPercent = 0;
            void OnMuxProgress(string line)
            {
                var timeUs = TryParseProgressOutTimeUs(line);
                if (timeUs is null) return;
                lock (muxLock)
                {
                    if (finalizing) return;
                    muxPercent = Math.Max(muxPercent, Math.Clamp(timeUs.Value / (double)Math.Max(1, cumulativeMs * 1000), 0, 1));
                    double? remaining = muxPercent >= 0.01 && muxPercent < 1 && muxTimer.Elapsed.TotalSeconds >= 0.5
                        ? muxTimer.Elapsed.TotalSeconds * (1 - muxPercent) / muxPercent : null;
                    progress?.Report(new ExportProgress(ExportPhase.Concatenating, total, total,
                        muxPercent, 0.95 + muxPercent * 0.03, "Stitching parts…", RemainingSeconds: remaining));
                }
            }
            void OnMuxDiagnostic(string line)
            {
                if (!line.Contains("Starting second pass", StringComparison.Ordinal)
                    || !line.Contains("moov", StringComparison.Ordinal)) return;
                lock (muxLock)
                {
                    if (finalizing) return;
                    finalizing = true;
                    progress?.Report(new ExportProgress(ExportPhase.Finalizing, total, total,
                        1, 0.98, "Finalizing MP4…"));
                }
            }
            var muxR = await _runner.RunFfmpegAsync(muxArgs, OnMuxProgress, ct, OnMuxDiagnostic).ConfigureAwait(false);
            if (!muxR.Success)
                throw new ExportException($"Concat mux failed (exit {muxR.ExitCode}):\n{Tail(muxR.Stderr, 1500)}");

            // Phase 3: post-export validation.
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(
                ExportPhase.Validating, plan.KeptSegments.Count, plan.KeptSegments.Count,
                0, 0.99, "Validating output…"));

            await ValidateOutputAsync(plan with { OutputPath = staging }, cumulativeMs, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            sourceInfo.Refresh();
            if (sourceInfo.Length != sourceSize || sourceInfo.LastWriteTimeUtc != sourceWrite)
                throw new ExportException("Source changed during export. Output was not published.");
            ExportSafety.EnsureDifferentFiles(plan.SourcePath, destination);
            try
            {
                if (plan.Overwrite && File.Exists(destination)) File.Replace(staging, destination, null);
                else File.Move(staging, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The video is encoded and validated; cleanup would delete it. Keep it under a free name.
                if (KeepUnderFreeName(staging, destination) is not { } kept) throw;
                throw new ExportException($"The export finished, but \"{Path.GetFileName(destination)}\" could not be written: {ex.Message} "
                    + $"The finished video was saved as \"{Path.GetFileName(kept)}\".", ex);
            }

            progress?.Report(new ExportProgress(
                ExportPhase.Done, plan.KeptSegments.Count, plan.KeptSegments.Count,
                1, 1, "Export complete"));
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && disk.Failure is not null)
        {
            throw new ExportException(disk.Failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HardwareExportException)
        {
            progress?.Report(new ExportProgress(
                ExportPhase.Failed, 0, plan.KeptSegments.Count, 0, 0, ex.Message));
            throw;
        }
        finally
        {
            if (!await workspace.CleanupAsync().ConfigureAwait(false))
                progress?.Report(new ExportProgress(ExportPhase.Cleanup, 0, 0, 0, 0,
                    "Some export temporary files could not be removed. AdTrim will retry cleanup the next time it starts."));
        }
    }

    private static async Task WriteConcatListAsync(
        string path, IReadOnlyList<string> intermediatePaths, IReadOnlyList<ExportSegment> segments,
        IReadOnlyList<long> startsUs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < intermediatePaths.Count; i++)
        {
            // Concat demuxer requires file paths quoted and forward-slashed for safety.
            var escaped = intermediatePaths[i].Replace("\\", "/").Replace("'", "'\\''");
            sb.Append("file '").Append(escaped).Append("'\n");
            // Packet tails can exceed the cut by one audio frame. Using their
            // container durations here would accumulate padding at every join.
            // The concat demuxer subtracts each file's start_time. Compensate
            // for differing encoder offsets without seeking past opening audio.
            long durationUs = segments[i].DurationUs + (i + 1 < startsUs.Count ? startsUs[i + 1] - startsUs[i] : 0);
            sb.Append("duration ").Append((durationUs / 1_000_000m)
                .ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
        }
        // FFmpeg's concat demuxer rejects the UTF-8 BOM (`Encoding.UTF8`
        // emits one by default). Use a BOM-less UTF-8 encoder.
        await File.WriteAllTextAsync(path, sb.ToString(), Utf8NoBom, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the FFMETADATA1 chapter file. Chapter timecodes are cumulative
    /// requested durations used by the concat list. Copied packet tails must
    /// not accumulate padding in either the timeline or its chapters.
    /// </summary>
    private static async Task BuildChapterMetadataAsync(
        string outPath,
        IReadOnlyList<ExportSegment> segments,
        long firstStartUs,
        IReadOnlyList<long> videoStartsUs,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(";FFMETADATA1");

        long cumulativeUs = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            long startMs = i == 0 ? 0 : (long)Math.Round((cumulativeUs + videoStartsUs[i] - firstStartUs) / 1000.0);
            cumulativeUs += segments[i].DurationUs;
            long endUs = i + 1 == segments.Count ? cumulativeUs : cumulativeUs + videoStartsUs[i + 1] - firstStartUs;
            long endMs = Math.Max(startMs + 1, (long)Math.Round(endUs / 1000.0));
            sb.AppendLine("[CHAPTER]");
            sb.AppendLine("TIMEBASE=1/1000");
            sb.AppendLine($"START={startMs.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"END={endMs.ToString(CultureInfo.InvariantCulture)}");
            var title = segments[i].PartTitle.Replace("\\", "\\\\")
                .Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#")
                .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\\n");
            sb.AppendLine($"title={title}");
        }
        await File.WriteAllTextAsync(outPath, sb.ToString(), Utf8NoBom, ct).ConfigureAwait(false);
    }

    private async Task<(long DurationMs, long StartUs, long VideoStartUs)> ProbeSegmentTimingAsync(string path, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error",
            "-show_entries", "format=duration,start_time:stream=codec_type,start_time",
            "-of", "json",
            path,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success || string.IsNullOrWhiteSpace(r.Stdout))
            throw new ExportException($"Failed to probe segment duration: {path}");
        using var doc = JsonDocument.Parse(r.Stdout);
        var format = doc.RootElement.GetProperty("format");
        long Us(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var item)
                || !decimal.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                throw new ExportException("Encoded part timing could not be verified. Output was not published.");
            return (long)Math.Round(seconds * 1_000_000);
        }
        var video = doc.RootElement.GetProperty("streams").EnumerateArray()
            .First(s => s.GetProperty("codec_type").GetString() == "video");
        return ((long)Math.Round(Us(format, "duration") / 1000.0), Us(format, "start_time"), Us(video, "start_time"));
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

        if (!root.TryGetProperty("format", out var outputFormat) || !outputFormat.TryGetProperty("format_name", out var outputName)
            || (plan.Format == ExportFormat.Ts ? outputName.GetString() != "mpegts" : !(outputName.GetString() ?? "").Split(',').Contains("mp4")))
            throw new ExportException("Output container does not match the selected file format.");
        int nbChapters = root.TryGetProperty("chapters", out var chs) ? chs.GetArrayLength() : 0;
        int expectedChapters = plan.Format == ExportFormat.Mp4 ? plan.KeptSegments.Count : 0;
        if (nbChapters != expectedChapters)
            throw new ExportException(
                $"Output has {nbChapters} chapters, expected {expectedChapters}.");

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
            if (Math.Abs(actualMs * 1000 - plan.ExpectedOutputDurationUs) > Math.Max(200_000, plan.KeptSegments.Count * 2_000_000 / Math.Max(1, plan.FrameRate.AsDouble)))
                throw new ExportException("Output duration differs from the requested material. Output was not published.");
        }
        else throw new ExportException("Output duration could not be verified.");


    }

    private async Task ValidateAudioCopyAsync(ExportPlan plan, MediaInfo media, string tempDir, CancellationToken ct)
    {
        if (media.PrimaryAudio is not { } audio || audio.Index != plan.PrimaryAudioStreamIndex
            || (!string.IsNullOrEmpty(plan.PrimaryAudioCodec) && plan.PrimaryAudioCodec != audio.Codec))
            throw new ExportException("The selected source audio changed. Reopen the recording before exporting.");
        if (plan.Format == ExportFormat.Ts && audio.Codec is not ("aac" or "ac3" or "eac3" or "mp2" or "mp3" or "dts"))
            throw new ExportException($"Audio codec '{audio.Codec}' cannot be copied to a supported TS audio stream. Choose MP4 or another source; audio is not converted automatically.");
        foreach (var format in plan.Format == ExportFormat.Ts ? new[] { "mp4", "mpegts" } : new[] { "mp4" })
        {
            string check = Path.Combine(tempDir, format == "mp4" ? "audio-check.mp4" : "audio-check.ts");
            var args = new[] { "-y", "-v", "error", "-i", plan.SourcePath, "-map", $"0:{audio.Index}",
                "-t", "0.1", "-c:a", "copy", "-map_metadata", "-1", "-map_chapters", "-1", "-f", format, check };
            var result = await _runner.RunFfmpegAsync(args, ct: ct).ConfigureAwait(false);
            if (!result.Success)
                throw new ExportException($"Audio codec '{audio.Codec}' could not be copied to {(format == "mp4" ? "the MP4 segment format" : "TS")}. Audio is not converted automatically.\n{Tail(result.Stderr, 600)}");
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
