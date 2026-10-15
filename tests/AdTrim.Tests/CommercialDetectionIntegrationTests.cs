using System.Security.Cryptography;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class CommercialDetectionIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamingPassFindsCompletePodAndKeepsSourceUnchanged(bool audio, bool offset)
    {
        var root = FindRoot();
        Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(root, "binaries", "ffmpeg", "win-x64"));
        var runner = new FfmpegRunner();
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-detector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.mp4");
        try
        {
            var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=white:s=96x54:r=10:d=420" };
            if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=420"]);
            args.AddRange(["-vf", "geq=lum='if(between(T,150,150.19)+between(T,180,180.19)+between(T,210,210.19),16,if(between(T,150,210),64+148*mod(floor(T/5),2),64+148*mod(floor(T/30),2)))':cb=128:cr=128",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-pix_fmt", "yuv420p"]);
            if (audio) args.AddRange(["-c:a", "aac"]);
            if (offset) args.AddRange(["-output_ts_offset", "1.3"]);
            args.Add(path);
            var created = await runner.RunFfmpegAsync(args);
            Assert.True(created.Success, created.Stderr);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var media = await new MediaProbeService(runner).ProbeAsync(path);
            var result = await new CommercialBreakDetector(runner).AnalyzeAsync(path, media);
            Assert.True(result.Breaks.Count == 1, System.Text.Json.JsonSerializer.Serialize(result));
            var proposal = Assert.Single(result.Breaks);
            long shift = offset ? 1_300_000 : 0;
            Assert.Equal(150_200_000 + shift, proposal.StartUs);
            Assert.Equal(210_200_000 + shift, proposal.EndUs);
            Assert.False(proposal.AutoExclude);
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.False(File.Exists(path + ".adt.json"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CommercialBreakDetector(runner).AnalyzeAsync(path, media, cancellationToken: cancelled.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdTrim.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run these tests from the source checkout with its bundled media tools.");
    }
}
