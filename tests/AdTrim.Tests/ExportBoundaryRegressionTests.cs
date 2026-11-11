using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class ExportBoundaryRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-export-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly Lazy<FfmpegRunner> _tools = new(AudioLevelServiceTests.Runner);
    private FfmpegRunner Runner => _tools.Value;
    public ExportBoundaryRegressionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private async Task Run(params string[] args)
    {
        var result = await Runner.RunFfmpegAsync(args);
        Assert.True(result.Success, result.Stderr);
    }
    private async Task<string> Source(string extension, bool delayedVideo = false)
    {
        string path = PathFor("source" + extension);
        await Run("-y", "-v", "error", "-f", "lavfi", "-i",
            "nullsrc=s=160x96:r=25:d=14,geq=lum='30+mod(N,180)':cb=128:cr=128",
            "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*(440*t+7*t*t)):s=48000:d=14",
            "-vf", delayedVideo ? "setpts=PTS+1.2/TB" : "null", "-c:v", "mpeg2video", "-g", "100",
            "-sc_threshold", "1000000000", "-bf", "2", "-c:a", "ac3", "-b:a", "192k", "-avoid_negative_ts", "make_zero", path);
        return path;
    }
    private async Task<ExportPlan> Plan(string source, ExportFormat format, params ExportSegment[] segments)
    {
        var media = await new MediaProbeService(Runner).ProbeAsync(source);
        return new(source, PathFor("output" + format.Extension()), media.DurationUs, media.PrimaryAudioIndex,
            segments, media.FrameRate, media.PrimaryAudio!.Codec, Format: format);
    }
    private async Task<JsonElement[]> Packets(string path)
    {
        var probe = await Runner.RunFfprobeAsync(new[] { "-v", "error", "-select_streams", "a:0", "-show_packets",
            "-show_data_hash", "sha256", "-show_entries", "packet=pts_time,data_hash", "-of", "json", path });
        Assert.True(probe.Success, probe.Stderr);
        using var doc = JsonDocument.Parse(probe.Stdout);
        return doc.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.Clone()).ToArray();
    }
    [SkippableTheory]
    [InlineData(".mp4", ExportFormat.Mp4)]
    [InlineData(".mp4", ExportFormat.Ts)]
    [InlineData(".ts", ExportFormat.Mp4)]
    [InlineData(".ts", ExportFormat.Ts)]
    public async Task OpeningExportRetainsAudioBeforeTheFirstVideoFrame(string extension, ExportFormat format)
    {
        string source = await Source(extension, delayedVideo: true);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var plan = await Plan(source, format, new ExportSegment(1, 0, 8_000_000, "Opening"));
        await new ExportService(Runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var input = await Packets(source); var output = await Packets(plan.OutputPath);
        Assert.Equal(input[0].GetProperty("data_hash").GetString(), output[0].GetProperty("data_hash").GetString());
        Assert.InRange(output.Length, 249, 251);
        if (format == ExportFormat.Mp4)
        {
            var probe = await Runner.RunFfprobeAsync(new[] { "-v", "error", "-show_chapters", "-show_format", "-of", "json", plan.OutputPath });
            using var doc = JsonDocument.Parse(probe.Stdout);
            double chapterEnd = double.Parse(doc.RootElement.GetProperty("chapters")[0].GetProperty("end_time").GetString()!, CultureInfo.InvariantCulture);
            double duration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(chapterEnd, 7.99, Math.Min(8.01, duration + .002));
        }
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Mp4)]
    [InlineData(ExportFormat.Ts)]
    public async Task TransportCutDecodesTheRequestedFrameAfterALongGop(ExportFormat format)
    {
        string source = await Source(".ts");
        var plan = await Plan(source, format, new ExportSegment(1, 6_200_000, 7_200_000, "Mid GOP"));
        await new ExportService(Runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var decoded = await Runner.RunFfmpegAsync(new[] { "-v", "error", "-i", plan.OutputPath, "-an", "-frames:v", "1",
            "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-" });
        Assert.True(decoded.Success, decoded.Stderr);
        string level = decoded.Stdout.Split('\n').First(l => l.StartsWith("lavfi.signalstats.YAVG="));
        Assert.InRange(double.Parse(level.Split('=')[1], CultureInfo.InvariantCulture), 183, 187);
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Mp4)]
    [InlineData(ExportFormat.Ts)]
    public async Task JoinsPreserveEveryCopiedAudioPacketTimestamp(ExportFormat format)
    {
        string source = await Source(".mp4");
        var spans = new[] { new ExportSegment(1, 640_000, 1_673_333, "One"), new(2, 2_210_000, 3_077_777, "Two"),
            new(3, 4_201_000, 5_322_222, "Three"), new(4, 8_123_000, 9_234_567, "Four"), new(5, 10_113_000, 11_877_777, "Five") };
        var plan = await Plan(source, format, spans);
        await new ExportService(Runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var input = await Packets(source); var output = await Packets(plan.OutputPath);
        var byHash = input.Select((p, i) => (p, i)).GroupBy(x => x.p.GetProperty("data_hash").GetString()!)
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
        int previous = -1, previousPart = -1; double previousOffset = 0;
        double? initialTimelineOffset = null;
        var seen = new HashSet<int>();
        foreach (var packet in output)
        {
            int index = byHash[packet.GetProperty("data_hash").GetString()!].First(i => i > previous);
            double sourceTime = double.Parse(input[index].GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture);
            int part = Array.FindIndex(spans, s => sourceTime >= s.StartUs / 1e6 && sourceTime < s.EndUs / 1e6 + .033);
            Assert.True(part >= 0, "No excluded audio may leak into the output.");
            double offset = double.Parse(packet.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture) - sourceTime;
            double expectedStart = spans.Take(part).Sum(s => s.DurationUs) / 1e6;
            double timelineOffset = offset + spans[part].StartUs / 1e6 - expectedStart;
            initialTimelineOffset ??= timelineOffset;
            Assert.InRange(Math.Abs(timelineOffset - initialTimelineOffset.Value), 0, .002);
            if (part == previousPart)
            {
                Assert.Equal(previous + 1, index);
                Assert.InRange(Math.Abs(offset - previousOffset), 0, .002);
            }
            seen.Add(part); previous = index; previousPart = part; previousOffset = offset;
        }
        Assert.Equal(spans.Length, seen.Count);
        if (format == ExportFormat.Mp4)
        {
            var frames = await Runner.RunFfmpegAsync(new[] { "-v", "error", "-i", plan.OutputPath, "-an", "-vf",
                "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-" });
            Assert.True(frames.Success, frames.Stderr);
            var joins = new List<double>();
            double time = 0; double? previousLevel = null;
            foreach (string line in frames.Stdout.Split('\n'))
            {
                var stamp = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:(\S+)");
                if (stamp.Success) time = double.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!line.StartsWith("lavfi.signalstats.YAVG=")) continue;
                double level = double.Parse(line.Split('=')[1], CultureInfo.InvariantCulture);
                if (previousLevel is { } last && Math.Abs(level - last) > 5) joins.Add(time);
                previousLevel = level;
            }
            Assert.Equal(spans.Length - 1, joins.Count);
            var probe = await Runner.RunFfprobeAsync(new[] { "-v", "error", "-show_chapters", "-of", "json", plan.OutputPath });
            using var doc = JsonDocument.Parse(probe.Stdout);
            var chapters = doc.RootElement.GetProperty("chapters");
            for (int i = 0; i < joins.Count; i++)
            {
                double start = double.Parse(chapters[i + 1].GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
                Assert.InRange(Math.Abs(start - joins[i]), 0, .041);
            }
        }
    }
    [SkippableTheory]
    [InlineData(ExportFormat.Mp4)]
    [InlineData(ExportFormat.Ts)]
    public async Task ShortIntermediatesCannotValidateAgainstTheirOwnShortDuration(ExportFormat format)
    {
        string source = await Source(".mp4");
        var plan = await Plan(source, format, new ExportSegment(1, 0, 8_000_000, "Opening"));
        var error = await Assert.ThrowsAsync<ExportException>(() => new ExportService(Runner, new ShortEncoder()).RunExportAsync(plan));
        Assert.Contains("requested", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(plan.OutputPath));
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
    private sealed class ShortEncoder : IEncoderStrategy
    {
        public string DisplayName => "Short test encoder";
        public IReadOnlyList<string> BuildSegmentArgs(string source, ExportSegment segment, int audio, string output)
            => new LibX264EncoderStrategy().BuildSegmentArgs(source, segment with { EndUs = segment.StartUs + 4_000_000 }, audio, output);
    }
    [SkippableTheory]
    [InlineData(".mp4", ExportFormat.Mp4)]
    [InlineData(".mp4", ExportFormat.Ts)]
    [InlineData(".ts", ExportFormat.Mp4)]
    [InlineData(".ts", ExportFormat.Ts)]
    public async Task DamagedSourceAudioIsCopiedFaithfullyWithoutBlockingExport(string extension, ExportFormat format)
    {
        string original = await Source(".mp4");
        string raw = PathFor("damaged.ac3"), source = PathFor("damaged" + extension);
        await Run("-v", "error", "-i", original, "-map", "0:a:0", "-c:a", "copy", raw);
        byte[] bytes = await File.ReadAllBytesAsync(raw);
        // Keep the sync word/header so copying succeeds; destroy a later block.
        int frame = 384 * 60;
        Array.Fill(bytes, (byte)255, frame + 20, 200);
        await File.WriteAllBytesAsync(raw, bytes);
        await Run("-v", "error", "-i", original, "-i", raw, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", source);
        var damaged = await Runner.RunFfmpegAsync(new[] { "-v", "error", "-xerror", "-err_detect", "explode",
            "-i", source, "-map", "0:a:0", "-f", "null", "-" });
        Assert.False(damaged.Success, "The fixture must contain an independently observable decode failure.");
        var plan = (await Plan(source, format, new ExportSegment(1, 0, 8_000_000, "Damaged"))) with { Overwrite = true };
        byte[] sentinel = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(plan.OutputPath, sentinel);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        await new ExportService(Runner, new LibX264EncoderStrategy()).RunExportAsync(plan);
        var input = await Packets(source);
        var output = await Packets(plan.OutputPath);
        Assert.True(output.Length > 60, "The damaged packet must be included in the kept material.");
        Assert.Equal(input.Take(output.Length).Select(p => p.GetProperty("data_hash").GetString()),
            output.Select(p => p.GetProperty("data_hash").GetString()));
        Assert.NotEqual(sentinel, await File.ReadAllBytesAsync(plan.OutputPath));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }

    private sealed class Report(Action<ExportProgress> report) : IProgress<ExportProgress>
    { void IProgress<ExportProgress>.Report(ExportProgress value) => report(value); }

    // A target that cannot be replaced must be reported at once, not after the whole encode.
    [SkippableFact]
    public async Task TargetOpenInAnotherProgramIsReportedBeforeEncoding()
    {
        string source = await Source(".mp4");
        var plan = (await Plan(source, ExportFormat.Mp4, new ExportSegment(1, 0, 2_000_000, "Opening"))) with { Overwrite = true };
        File.WriteAllText(plan.OutputPath, "existing video");
        int reports = 0;
        using (new FileStream(plan.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<ExportException>(() => new ExportService(Runner, new LibX264EncoderStrategy())
                .RunExportAsync(plan, new Report(p => { if (p.Phase != ExportPhase.Failed) reports++; })));
            Assert.Contains("cannot be replaced", error.Message);
        }
        Assert.Equal(0, reports);
        Assert.Equal("existing video", File.ReadAllText(plan.OutputPath));
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }

    // The encode can take an hour. If its name is taken meanwhile, the finished video is kept
    // under another name instead of being deleted with the temporary files.
    [SkippableFact]
    public async Task FinishedVideoIsKeptWhenItsNameIsTakenDuringTheExport()
    {
        string source = await Source(".mp4");
        var plan = await Plan(source, ExportFormat.Mp4, new ExportSegment(1, 0, 2_000_000, "Opening"));
        bool taken = false;
        var error = await Assert.ThrowsAsync<ExportException>(() => new ExportService(Runner, new LibX264EncoderStrategy())
            .RunExportAsync(plan, new Report(_ =>
            {
                if (taken) return;
                taken = true;
                File.WriteAllText(plan.OutputPath, "someone else's file");
            })));
        string kept = PathFor("output (2).mp4");
        Assert.Contains("output (2).mp4", error.Message);
        Assert.Equal("someone else's file", File.ReadAllText(plan.OutputPath));
        var probe = await Runner.RunFfprobeAsync(new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", kept });
        Assert.True(probe.Success, probe.Stderr);
        Assert.InRange(double.Parse(probe.Stdout.Trim(), CultureInfo.InvariantCulture), 1.8, 2.2);
        Assert.Empty(Directory.GetFiles(_directory, ".adtrim-*"));
    }
}
