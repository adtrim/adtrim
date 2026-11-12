using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class TransportStreamTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-ts-test-" + Guid.NewGuid().ToString("N"));
    private readonly Lazy<FfmpegRunner> _tools = new(AudioLevelServiceTests.Runner);
    private FfmpegRunner _runner => _tools.Value;
    public TransportStreamTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string FilePath(string name) => Path.Combine(_directory, name);
    private async Task Run(params string[] args)
    {
        var result = await _runner.RunFfmpegAsync(args);
        Assert.True(result.Success, result.Stderr);
    }
    private async Task<string> Source(string extension, string audio = "ac3", string video = "mpeg2video", bool interlaced = false, long timestampOffset = 120)
    {
        string source = FilePath("source" + extension);
        var args = new List<string> { "-y", "-v", "error", "-f", "lavfi", "-i",
            "nullsrc=size=160x96:rate=25:duration=6,geq=lum='if(between(T,2,4),220,40+N)':cb=128:cr=128",
            "-f", "lavfi", "-i", "aevalsrc=0.4*sin(2*PI*if(between(t\\,2\\,4)\\,1000\\,440)*t):s=48000:d=6",
            "-c:v", video, "-g", "12", "-bf", "2", "-c:a", audio, "-b:a", "192k" };
        if (interlaced) args.AddRange(new[] { "-flags", "+ilme+ildct", "-vf", "setfield=tff" });
        if (extension == ".ts") args.AddRange(new[] { "-output_ts_offset", timestampOffset.ToString(CultureInfo.InvariantCulture) });
        args.Add(source);
        await Run(args.ToArray());
        return source;
    }
    [SkippableTheory]
    [InlineData(120, 200_000)]
    [InlineData(60000, 200_000)]
    [InlineData(120, 3_000_000)]
    [InlineData(60000, 3_000_000)]
    public async Task FrameRecoveryAndRefinementFindFramesIncludingTheOpeningGop(long timestampOffset, long position)
    {
        string source = await Source(".ts", interlaced: true, timestampOffset: timestampOffset);
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        long target = media.VideoStartTimeUs + position;
        var refiner = new RefineService(_runner);
        var previous = await refiner.FindPreviousFrameAsync(source, target, CancellationToken.None);
        Assert.NotNull(previous);
        Assert.InRange(Math.Abs(previous.Value - (target - 40_000)), 0, 2);
        var boundary = await refiner.FindBoundaryFrameAsync(source, target, media.DurationUs, CancellationToken.None);
        Assert.NotNull(boundary);
        Assert.InRange(Math.Abs(boundary.Value - target), 0, 2);
    }
    private async Task<ExportPlan> Plan(string source, ExportFormat format)
    {
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        return new(source, FilePath("finished" + format.Extension()), media.DurationUs, media.PrimaryAudioIndex,
            new[] { new ExportSegment(1, 640_000, 1_640_000, "Opening"), new ExportSegment(2, 4_200_000, 5_200_000, "Closing") },
            media.FrameRate, media.PrimaryAudio!.Codec, Format: format);
    }
    [SkippableTheory]
    [InlineData("ac3", "mpeg2video", 120, true)]
    [InlineData("aac", "libx264", 60000, false)]
    public async Task TsAudioAndAnalysisStayOnTheRecordingClock(string audio, string video, long offset, bool interlaced)
    {
        string source = await Source(".ts", audio, video, interlaced, offset);
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var levels = await new AudioLevelService(_runner).MeasureAsync(source, media, timeout.Token);
        Assert.InRange(levels.Loudness.Length, 295, 310);
        Assert.True(levels.Loudness[10] > 80, "Transport timestamps must not become leading silence in the waveform.");
        var evidence = await new SuggestionEvidenceCollector(_runner).CollectAsync(source, media, null, timeout.Token,
            (_, _) => Task.FromResult(levels));
        Assert.Equal(interlaced, evidence.FieldTransitions);
        Assert.InRange(evidence.Appearance.Length / ShowProfile.Width, 5, 7);
        Assert.Contains(evidence.Scenes, s => Math.Abs(s.Time - 2_000_000) < 100_000);
        Assert.All(evidence.Scenes, s => Assert.InRange(s.Time, 0, media.DurationUs));
    }
    [SkippableTheory]
    [InlineData(".mp4", 0)]
    [InlineData(".ts", 120)]
    [InlineData(".ts", 60000)]
    public async Task FadeRefinementReturnsTheVisibleFrameOnTheEditorClock(string extension, long offset)
    {
        string source = FilePath("fade" + extension);
        await Run("-v", "error", "-f", "lavfi", "-i",
            "nullsrc=s=160x96:r=25:d=4,geq=lum='if(lt(T,2.04),16,if(lt(T,2.16),50,180))':cb=128:cr=128",
            "-c:v", "mpeg2video", "-g", "12", "-output_ts_offset", offset.ToString(CultureInfo.InvariantCulture), source);
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        long phase = media.VideoStartTimeUs;
        var refiner = new RefineService(_runner);
        var entering = await refiner.FindFadeEdgeFrameAsync(source, phase + 1_000_000, phase + 2_160_000,
            160, 96, true, CancellationToken.None);
        Assert.NotNull(entering);
        Assert.InRange(Math.Abs(entering.Value - (phase + 2_040_000)), 0, 2);
        var returning = await refiner.FindFadeEdgeFrameAsync(source, phase + 1_000_000, phase + 2_160_000,
            160, 96, false, CancellationToken.None);
        Assert.NotNull(returning);
        Assert.InRange(Math.Abs(returning.Value - (phase + 2_000_000)), 0, 2);
    }
    [SkippableTheory]
    [InlineData(".mp4", ExportFormat.Mp4, "ac3", "mpeg2video")]
    [InlineData(".mp4", ExportFormat.Ts, "ac3", "mpeg2video")]
    [InlineData(".ts", ExportFormat.Mp4, "ac3", "mpeg2video")]
    [InlineData(".ts", ExportFormat.Ts, "ac3", "mpeg2video")]
    [InlineData(".mp4", ExportFormat.Mp4, "aac", "libx264")]
    [InlineData(".mp4", ExportFormat.Ts, "aac", "libx264")]
    [InlineData(".ts", ExportFormat.Mp4, "aac", "libx264")]
    [InlineData(".ts", ExportFormat.Ts, "aac", "libx264")]
    public async Task ExportsKeepOnlyRequestedPicturesAndAudioWithoutChangingSource(string extension, ExportFormat format, string audio, string video)
    {
        string source = await Source(extension, audio, video, interlaced: video == "mpeg2video");
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var plan = await Plan(source, format);
        await new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var probe = await _runner.RunFfprobeAsync(new[] { "-v", "error", "-show_streams", "-show_format", "-show_chapters", "-of", "json", plan.OutputPath });
        Assert.True(probe.Success, probe.Stderr);
        using var doc = JsonDocument.Parse(probe.Stdout);
        var chapters = doc.RootElement.GetProperty("chapters");
        Assert.Equal(format == ExportFormat.Mp4 ? 2 : 0, chapters.GetArrayLength());
        if (format == ExportFormat.Mp4) Assert.Equal("Closing", chapters[1].GetProperty("tags").GetProperty("title").GetString());
        bool hasTags = doc.RootElement.GetProperty("format").TryGetProperty("tags", out var tags);
        if (format == ExportFormat.Mp4) Assert.Contains("Edited by AdTrim", tags.GetProperty("comment").GetString());
        else Assert.False(hasTags && tags.TryGetProperty("comment", out _));
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video");
        var outputAudio = Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal(audio, outputAudio.GetProperty("codec_name").GetString());
        double videoStart = double.Parse(streams.First(s => s.GetProperty("codec_type").GetString() == "video").GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
        double audioStart = double.Parse(outputAudio.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(videoStart - audioStart), 0, 0.05);
        var pictures = await _runner.RunFfmpegAsync(new[] { "-v", "error", "-i", plan.OutputPath, "-an", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-" });
        Assert.True(pictures.Success, pictures.Stderr);
        double[] levels = pictures.Stdout.Split('\n').Where(l => l.StartsWith("lavfi.signalstats.YAVG="))
            .Select(l => double.Parse(l.Split('=')[1], CultureInfo.InvariantCulture)).ToArray();
        Assert.InRange(levels.Length, 49, 51);
        Assert.InRange(levels[0], 55, 58);
        Assert.InRange(levels[^1], 167, 171);
        Assert.DoesNotContain(levels, l => l > 190);
        string pcm = FilePath("audio.pcm");
        await Run("-v", "error", "-i", plan.OutputPath, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", pcm);
        byte[] bytes = await File.ReadAllBytesAsync(pcm);
        // Excluded material has a 1000 Hz tone; every kept section has a 440 Hz tone.
        for (int start = 4800; start + 2400 < bytes.Length / 2; start += 4800)
        {
            double Energy(int frequency)
            {
                double re = 0, im = 0;
                for (int i = 0; i < 2400; i++)
                {
                    double sample = BitConverter.ToInt16(bytes, (start + i) * 2);
                    double phase = 2 * Math.PI * frequency * i / 48000;
                    re += sample * Math.Cos(phase); im += sample * Math.Sin(phase);
                }
                return re * re + im * im;
            }
            Assert.True(Energy(440) > Energy(1000) * 20, "Excluded audio must not leak into kept material.");
        }
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
    [SkippableFact]
    public async Task TransportTimestampOriginDoesNotShiftRefinementIntoAnotherScene()
    {
        string source = await Source(".ts");
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        Assert.InRange(media.TimelineStartTimeUs, 120_000_000, 123_000_000);
        Assert.InRange(media.VideoStartTimeUs, 0, 50_000);
        var result = await new RefineService(_runner).RefineOneAsync(source, 2_000_000, 1_500_000, 2_500_000);
        Assert.NotNull(result);
        Assert.InRange(result.RefinedTimeUs, 1_950_000, 2_100_000);
    }
    [SkippableFact]
    public async Task AmbiguousProgramsAreRejectedBeforeChoosingAnAudioTrack()
    {
        string source = await Source(".ts");
        string multi = FilePath("multiprogram.ts");
        await Run("-v", "error", "-i", source, "-map", "0:v", "-map", "0:a", "-map", "0:v", "-map", "0:a",
            "-c", "copy", "-program", "program_num=1:st=0:st=1", "-program", "program_num=2:st=2:st=3", multi);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => new MediaProbeService(_runner).ProbeAsync(multi));
        Assert.Contains("multiple programs", error.Message);
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Ts, ExportPhase.EncodingSegment)]
    [InlineData(ExportFormat.Ts, ExportPhase.Concatenating)]
    [InlineData(ExportFormat.Ts, ExportPhase.Validating)]
    [InlineData(ExportFormat.Mp4, ExportPhase.EncodingSegment)]
    [InlineData(ExportFormat.Mp4, ExportPhase.Concatenating)]
    [InlineData(ExportFormat.Mp4, ExportPhase.Validating)]
    public async Task CancelledTsSourceExportPreservesExistingDestinationAndSource(ExportFormat format, ExportPhase phase)
    {
        string source = await Source(".ts");
        var plan = (await Plan(source, format)) with { Overwrite = true };
        await File.WriteAllTextAsync(plan.OutputPath, "Existing output");
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(p => { if (p.Phase == phase) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan, progress, cancel.Token));
        Assert.Equal("Existing output", await File.ReadAllTextAsync(plan.OutputPath));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Mp4)]
    [InlineData(ExportFormat.Ts)]
    public async Task OverwriteKeepsTheOldDestinationUntilValidation(ExportFormat format)
    {
        string source = await Source(".ts");
        var plan = (await Plan(source, format)) with { Overwrite = true };
        await File.WriteAllTextAsync(plan.OutputPath, "Existing output");
        bool validated = false;
        var progress = new CallbackProgress(p =>
        {
            if (p.Phase != ExportPhase.Validating) return;
            Assert.Equal("Existing output", File.ReadAllText(plan.OutputPath));
            validated = true;
        });
        await new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan, progress);
        Assert.True(validated);
        Assert.True(new FileInfo(plan.OutputPath).Length > 1000);
        await Run("-v", "error", "-xerror", "-i", plan.OutputPath, "-f", "null", "-");
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Mp4)]
    [InlineData(ExportFormat.Ts)]
    public async Task RepeatedShortCutsDoNotAccumulateAudioVideoDrift(ExportFormat format)
    {
        string source = await Source(".ts", "aac", "libx264");
        var plan = await Plan(source, format);
        long[] starts = { 120_000, 440_000, 760_000, 1_080_000, 1_400_000, 1_720_000, 4_120_000, 4_440_000, 4_760_000, 5_080_000 };
        plan = plan with { KeptSegments = starts.Select((start, i) => new ExportSegment(i + 1, start, start + 200_000, "Kept")).ToArray() };
        await new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var probe = await _runner.RunFfprobeAsync(new[] { "-v", "error", "-show_streams", "-of", "json", plan.OutputPath });
        Assert.True(probe.Success, probe.Stderr);
        using var json = JsonDocument.Parse(probe.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() is "video" or "audio").ToArray();
        double End(JsonElement stream) => double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture)
            + double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var packets = await _runner.RunFfprobeAsync(new[] { "-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json", plan.OutputPath });
        using var packetJson = JsonDocument.Parse(packets.Stdout);
        double PacketEnd(int index) => packetJson.RootElement.GetProperty("packets").EnumerateArray()
            .Where(p => p.GetProperty("stream_index").GetInt32() == index)
            .Max(p => double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture)
                + double.Parse(p.GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture));
        Assert.True(Math.Abs(PacketEnd(0) - PacketEnd(1)) <= 0.05,
            $"Video/audio packet ends: {PacketEnd(0)}, {PacketEnd(1)}. Estimated stream ends: {End(streams[0])}, {End(streams[1])}.");
        await Run("-v", "error", "-xerror", "-i", plan.OutputPath, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-");
        var pictures = await _runner.RunFfmpegAsync(new[] { "-v", "error", "-i", plan.OutputPath, "-an", "-vf",
            "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-" });
        Assert.True(pictures.Success, pictures.Stderr);
        double[] luma = pictures.Stdout.Split('\n').Where(l => l.StartsWith("lavfi.signalstats.YAVG="))
            .Select(l => double.Parse(l.Split('=')[1], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(starts.Length * 5, luma.Length);
        for (int i = 0; i < starts.Length; i++)
            Assert.InRange(Math.Abs(luma[i * 5] - (40 + starts[i] * 25 / 1_000_000)), 0, 2);
    }
    [SkippableFact]
    public async Task UnsupportedTsAudioFailsBeforePublishingOrChangingTheSource()
    {
        string source = await Source(".mp4", "alac", "libx264");
        var plan = await Plan(source, ExportFormat.Ts);
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var error = await Assert.ThrowsAsync<ExportException>(() => new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan));
        Assert.Contains("Audio codec 'alac'", error.Message);
        Assert.False(File.Exists(plan.OutputPath));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
    [SkippableFact]
    public async Task ExistingDestinationAndSourceAliasesAreRejectedForTs()
    {
        string source = await Source(".ts");
        var plan = await Plan(source, ExportFormat.Ts);
        await File.WriteAllTextAsync(plan.OutputPath, "Keep this file");
        var exporter = new ExportService(_runner, new LibX264EncoderStrategy());
        await Assert.ThrowsAsync<ExportException>(() => exporter.RunExportAsync(plan));
        Assert.Equal("Keep this file", await File.ReadAllTextAsync(plan.OutputPath));
        await Assert.ThrowsAsync<ExportException>(() => exporter.RunExportAsync(plan with { OutputPath = source, Overwrite = true }));
    }
    [SkippableFact]
    public async Task TsExportKeepsTheProbeSelectedSurroundTrackRatherThanTheFirstAudioTrack()
    {
        string source = FilePath("surround.ts");
        await Run("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=25:duration=6",
            "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000:duration=6",
            "-f", "lavfi", "-i", "aevalsrc=0.2*sin(2*PI*440*t)|0.2*sin(2*PI*440*t)|0|0|0|0:s=48000:d=6:c=5.1",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "mpeg2video", "-c:a:0", "aac", "-c:a:1", "ac3",
            "-disposition:a:0", "default", "-disposition:a:1", "0", source);
        var plan = await Plan(source, ExportFormat.Ts);
        Assert.Equal(2, plan.PrimaryAudioStreamIndex);
        var sourceMedia = await new MediaProbeService(_runner).ProbeAsync(source);
        var levels = await new AudioLevelService(_runner).MeasureAsync(source, sourceMedia);
        Assert.InRange(levels.Loudness.Length, 295, 310);
        Assert.True(levels.Loudness[50] > 80, "The waveform must use the selected surround track, not the silent first audio track.");
        Assert.InRange(levels.CentreDialogue.Length, 5, 7);
        await new ExportService(_runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var output = await new MediaProbeService(_runner).ProbeAsync(plan.OutputPath);
        Assert.Single(output.AudioStreams);
        Assert.Equal("ac3", output.PrimaryAudio!.Codec);
        Assert.Equal(6, output.PrimaryAudio.Channels);
    }
    [SkippableFact]
    public async Task DamagedTsDoesNotOpenAsAnEmptyOrSuccessfulRecording()
    {
        string source = FilePath("broken.ts");
        await File.WriteAllTextAsync(source, "Not a media file");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MediaProbeService(_runner).ProbeAsync(source));
    }
    [SkippableFact]
    public async Task TimestampResetDoesNotPreventOpeningAReadableRecording()
    {
        string source = await Source(".ts");
        string reset = FilePath("timestamp-reset.ts");
        byte[] packets = await File.ReadAllBytesAsync(source);
        await using (var output = File.Create(reset))
        {
            await output.WriteAsync(packets);
            await output.WriteAsync(packets);
        }
        var media = await new MediaProbeService(_runner).ProbeAsync(reset);
        Assert.True(media.DurationUs > 0);
        Assert.NotNull(media.PrimaryAudio);
    }
    private sealed class CallbackProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => report(value); }

    // Broadcast GOPs run to several seconds. A probe that begins at the next keyframe loses the
    // frames around a boundary, and the ad break that depends on them is dropped.
    [SkippableFact]
    public async Task BoundaryFramesAreFoundInsideALongGop()
    {
        string source = FilePath("long-gop.ts");
        await Run("-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=25:duration=30",
            "-c:v", "mpeg2video", "-g", "250", "-bf", "2", "-output_ts_offset", "120", source);
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        var refiner = new RefineService(_runner);
        foreach (long position in new[] { 5_000_000L, 15_000_000L, 27_000_000L })
        {
            long target = media.VideoStartTimeUs + position;
            var boundary = await refiner.FindBoundaryFrameAsync(source, target, media.DurationUs, CancellationToken.None);
            Assert.NotNull(boundary);
            Assert.InRange(Math.Abs(boundary.Value - target), 0, 2);
            var transition = await refiner.FindTransitionFrameAsync(source, target, media.DurationUs, entering: true, fieldTransition: false, CancellationToken.None);
            Assert.NotNull(transition);
        }
    }

    // Broadcast recordings start their sound before their picture. FFmpeg then zeroes a video-only
    // pass at the first picture, so every detected time was early by that gap and ad breaks landed
    // a second or two before the real cut, or on no frame at all.
    [SkippableFact]
    public async Task PictureAnalysisStaysOnTheRecordingClockWhenThePictureStartsAfterTheSound()
    {
        string source = FilePath("late-picture.ts");
        await Run("-y", "-v", "error", "-itsoffset", "1.5", "-f", "lavfi", "-i",
            "nullsrc=size=160x96:rate=25:duration=12,geq=lum='if(between(T,5,5.99),16,150)':cb=128:cr=128",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=14",
            "-map", "0:v", "-map", "1:a", "-c:v", "mpeg2video", "-g", "12", "-c:a", "ac3", "-b:a", "192k",
            "-output_ts_offset", "120", source);
        var media = await new MediaProbeService(_runner).ProbeAsync(source);
        Assert.InRange(media.VideoStartTimeUs, 1_400_000, 1_600_000);
        Assert.Equal(media.PrimaryAudioIndex, media.TimelineAnchorStreamIndex);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var levels = await new AudioLevelService(_runner).MeasureAsync(source, media, timeout.Token);
        var evidence = await new SuggestionEvidenceCollector(_runner).CollectAsync(source, media, null, timeout.Token,
            (_, _) => Task.FromResult(levels));
        var black = Assert.Single(evidence.Black);
        // The picture's sixth second is 6 s after the first frame, wherever the sound began.
        Assert.InRange(black.End - media.VideoStartTimeUs, 5_960_000, 6_040_000);
        var frame = await new RefineService(_runner).FindBoundaryFrameAsync(source, black.End, media.DurationUs, timeout.Token);
        Assert.NotNull(frame);
    }
}
