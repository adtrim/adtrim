using AdTrim.Models;
using System.Security.Cryptography;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

[Collection(AnalysisGateCollection.Name)]
public sealed class SuggestionEngineIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimarySurroundEvidenceUsesTheSourceClock(bool timestampOffset)
    {
        var root = FindRoot();
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root, "binaries", "ffmpeg", "win-x64"));
        var runner = new FfmpegRunner();
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-surround-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "surround.mp4");
        try
        {
            var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=gray:s=320x180:r=5:d=120",
                "-f", "lavfi", "-i", "aevalsrc=0.01*sin(2*PI*400*t)|0.01*sin(2*PI*500*t)|0.3*sin(2*PI*600*t)|0|0|0:s=8000:d=120:c=5.1",
                "-vf", "drawbox=x=10:y=150:w=10:h=10:color=white:t=fill", "-c:v", "libx264", "-preset", "ultrafast",
                "-c:a", "aac", "-ar", "48000", "-pix_fmt", "yuv420p" };
            if (timestampOffset) args.AddRange(["-output_ts_offset", "1.3"]);
            args.Add(path);
            var created = await runner.RunFfmpegAsync(args);
            Assert.True(created.Success, created.Stderr);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var media = await new MediaProbeService(runner).ProbeAsync(path);
            var evidence = await new SuggestionEvidenceCollector(runner).CollectAsync(path, media, null, CancellationToken.None);
            Assert.NotEmpty(evidence.CentreDialogue);
            if (timestampOffset) Assert.False(evidence.CentreDialogue[0]);
            Assert.All(evidence.CentreDialogue.Skip(3).Take(110), sample => Assert.True(sample));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.False(File.Exists(path + ".adt.json"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LearnedGraphicAndRepeatedUnitsFindALongBreakWithoutAudio(bool timestampOffset)
    {
        var root = FindRoot();
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root, "binaries", "ffmpeg", "win-x64"));
        var runner = new FfmpegRunner();
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-suggestion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "direct-cuts.mp4");
        try
        {
            var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=white:s=320x180:r=10:d=1000",
                "-vf", "geq=lum='if(between(T,150,469.99),24+188*mod(floor((T-150)/30),2),64+80*mod(floor(T/30),2))':cb=128:cr=128,drawbox=x=10:y=150:w=10:h=10:color=white:t=fill:enable='not(between(t,150,469.99))'",
                "-an", "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-pix_fmt", "yuv420p" };
            if (timestampOffset) args.AddRange(["-output_ts_offset", "1.3"]);
            args.Add(path);
            var created = await runner.RunFfmpegAsync(args);
            Assert.True(created.Success, created.Stderr);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var media = await new MediaProbeService(runner).ProbeAsync(path);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SuggestionEngine(runner).AnalyzeAsync(path, media,
                new CancelAtProgress(cancellation), cancellation.Token));
            var result = await new SuggestionEngine(runner).AnalyzeAsync(path, media);
            var proposal = Assert.Single(result.Breaks);
            long offset = timestampOffset ? 1_300_000 : 0;
            Assert.Equal(150_000_000 + offset, proposal.StartUs);
            Assert.Equal(470_000_000 + offset, proposal.EndUs);
            Assert.False(proposal.AutoExclude);
            var mismapped = new SuggestionEvidence([], [(150_050_000L + offset, .8), (180_050_000L + offset, .8),
                (210_050_000L + offset, .8), (470_050_000L + offset, .8)],
                Enumerable.Range(0, 1000).Select(i => i >= 150 + offset / 1_000_000 && i < 470 + offset / 1_000_000 ? 0.0 : 1.0).ToArray(), 44, false);
            var rejected = await new SuggestionEngine(runner).AnalyzeEvidenceAsync(path, media, mismapped);
            Assert.Empty(rejected.Breaks);
            Assert.Contains("no valid source frames", Assert.Single(rejected.UncertainBreaks).Evidence);
            Assert.Equal(1, rejected.Candidates);
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.False(File.Exists(path + ".adt.json"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RecordingThatStartsInsideABreakReportsALowConfidenceReturnOnARealFrame()
    {
        // No graphic for the first 26 s and a cut at 25 s: the editor shows this as a red marker,
        // so it must come back mapped to a real frame with Low confidence, not as unmappable.
        var root = FindRoot();
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root, "binaries", "ffmpeg", "win-x64"));
        var runner = new FfmpegRunner();
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-suggestion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "starts-in-break.mp4");
        try
        {
            // Audio: a tone with short natural pauses every 10 s; one falls on the 25 s splice.
            var created = await runner.RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "color=white:s=320x180:r=10:d=600",
                "-f", "lavfi", "-i", "aevalsrc='0.3*sin(2*PI*440*t)*not(between(mod(t\\,10)\\,4.9\\,5.2))':s=48000:d=600",
                "-vf", "geq=lum='if(lt(T,25),200,64+80*mod(floor(T/30),2))':cb=128:cr=128,drawbox=x=10:y=150:w=10:h=10:color=white:t=fill:enable='gte(t,26)'",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", path]);
            Assert.True(created.Success, created.Stderr);
            var media = await new MediaProbeService(runner).ProbeAsync(path);
            var result = await new SuggestionEngine(runner).AnalyzeAsync(path, media);
            var edge = Assert.Single(result.UncertainBreaks, b => b.StartUs == 0);
            Assert.Equal(Confidence.Low, edge.EndpointConfidence);
            Assert.Equal(25_000_000, edge.EndUs);
            Assert.False(edge.AutoExclude);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OneAnalysisReportsOneProgressBarThatNeverRestarts()
    {
        // Two video passes used to each run 0-100%, which looked like the analysis ran twice.
        var root = FindRoot();
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root, "binaries", "ffmpeg", "win-x64"));
        var runner = new FfmpegRunner();
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-suggestion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "progress.mp4");
        try
        {
            var created = await runner.RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "color=gray:s=320x180:r=10:d=120",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=120",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", path]);
            Assert.True(created.Success, created.Stderr);
            var media = await new MediaProbeService(runner).ProbeAsync(path);
            var reports = new List<double>();
            var stages = new List<string?>();
            await new SuggestionEngine(runner).AnalyzeAsync(path, media, new Recorder(reports, stages));
            var fractions = reports.Where(r => r >= 0).ToList();
            // The user sees each named step once, in order, so the bar reads as one run with four parts.
            var steps = stages.Where(s => s is not null).Distinct().ToList();
            Assert.Equal(4, steps.Count);
            Assert.All(steps.Select((s, i) => (s, i)), x => Assert.StartsWith($"Step {x.i + 1} of 4: ", x.s));
            Assert.True(fractions.Zip(fractions.Skip(1)).All(p => p.Second >= p.First), "progress went backwards: " + string.Join(", ", fractions.Select(f => f.ToString("0.00"))));
            Assert.True(fractions.Max() > .9 && fractions.Max() <= 1);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Recorder(List<double> fractions, List<string?>? stages = null) : IProgress<CommercialDetectionProgress>
    {
        public void Report(CommercialDetectionProgress value)
        {
            if (!value.Queued && value.DurationUs > 0) lock (fractions)
            {
                fractions.Add(value.TimeUs / (double)value.DurationUs);
                stages?.Add(value.Stage);
            }
        }
    }

    private sealed class CancelAtProgress(CancellationTokenSource cancellation) : IProgress<CommercialDetectionProgress>
    {
        public void Report(CommercialDetectionProgress value)
        {
            if (!value.Queued && value.TimeUs >= 10_000_000) cancellation.Cancel();
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdTrim.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Source checkout with bundled media tools required.");
    }
}
