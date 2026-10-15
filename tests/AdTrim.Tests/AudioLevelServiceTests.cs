using System.IO;
using System.Security.Cryptography;
using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

[Collection(AnalysisGateCollection.Name)]
public sealed class AudioLevelServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _sourcePath;

    public AudioLevelServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AdtLevels-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_testDir);
        _sourcePath = Path.Combine(_testDir, "source.ts");
        File.WriteAllBytes(_sourcePath, Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray());
        File.SetLastWriteTimeUtc(_sourcePath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void CachePathFor_ChangesWithAudioStreamDurationVersionAndSourceIdentity()
    {
        var baseline = AudioLevelService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir);

        AudioLevelService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir).Should().Be(baseline);
        AudioLevelService.CachePathFor(_sourcePath, 2, 10_000_000, _testDir).Should().NotBe(baseline);
        AudioLevelService.CachePathFor(_sourcePath, 1, 11_000_000, _testDir).Should().NotBe(baseline);
        AudioLevelService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir, AudioLevelService.CacheVersion + 1).Should().NotBe(baseline);
        File.SetLastWriteTimeUtc(_sourcePath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        AudioLevelService.CachePathFor(_sourcePath, 1, 10_000_000, _testDir).Should().NotBe(baseline);
    }

    [Fact]
    public void SaveThenLoadCached_RoundtripsLoudnessAndDialogue()
    {
        var service = new AudioLevelService(runner: null, cacheRoot: _testDir);
        var expected = new AudioLevels([0, 25, 71, 120], [true, false, true]);

        service.SaveCached(_sourcePath, 1, 10_000_000, expected);

        service.TryLoadCached(_sourcePath, 1, 10_000_000, out var actual).Should().BeTrue();
        actual.Loudness.Should().Equal(expected.Loudness);
        actual.CentreDialogue.Should().Equal(expected.CentreDialogue);
        service.TryLoadCached(_sourcePath, 1, 11_000_000, out _).Should().BeFalse();
    }

    [Fact]
    public void WithoutACacheRoot_NothingIsCached()
    {
        // Analysis outside the editor (tests, the CLI) must not write into the user's app data.
        var service = new AudioLevelService(runner: null);
        service.SaveCached(_sourcePath, 1, 10_000_000, new AudioLevels([1], []));
        service.TryLoadCached(_sourcePath, 1, 10_000_000, out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetOrMeasureAsync_CancelledBeforeWork_DoesNotRequireFfmpeg()
    {
        var service = new AudioLevelService(runner: null, cacheRoot: _testDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetOrMeasureAsync(_sourcePath, Media(), cts.Token));
    }

    // 8 kHz f32le frames as ffmpeg delivers them; each frame is the given channel values.
    private static byte[] Pcm(double seconds, params float[] frame)
    {
        int frames = (int)(seconds * 8000);
        var bytes = new byte[frames * frame.Length * sizeof(float)];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < frame.Length; c++)
                BitConverter.TryWriteBytes(bytes.AsSpan((i * frame.Length + c) * sizeof(float)), (i % 2 == 0 ? 1 : -1) * frame[c]);
        return bytes;
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return base.ReadAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task ReadLevelsAsync_ReadsNothingWhilePlaybackHoldsTheDisk()
    {
        // Not reading the pipe is what stops ffmpeg reading the recording during a scrub.
        using var pcm = new CountingStream(Pcm(2, .5f));
        var idle = new TaskCompletionSource();

        var reading = AudioLevelService.ReadLevelsAsync(pcm, false, 2_000_000, _ => new ValueTask(idle.Task), null, null, TimeSpan.Zero, default);
        await Task.Delay(100);

        pcm.Reads.Should().Be(0);
        reading.IsCompleted.Should().BeFalse();

        idle.SetResult();
        (await reading).Loudness.Should().HaveCount(100);
    }

    [Fact]
    public async Task ReadLevelsAsync_PartialsGrowLeftToRightAndMatchTheFinalLoudness()
    {
        var sound = Pcm(5, .5f);
        using var pcm = new MemoryStream([.. sound, .. new byte[sound.Length]]);
        var partials = new List<byte[]>();

        var levels = await AudioLevelService.ReadLevelsAsync(pcm, false, 10_000_000, null, partials.Add, null, TimeSpan.Zero, default);

        levels.Loudness.Should().HaveCount(10 * AudioLevelService.LoudnessPerSecond);
        partials.Should().HaveCountGreaterThan(1);
        partials.Select(p => p.Length).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        foreach (var partial in partials)
            partial.Should().Equal(levels.Loudness.Take(partial.Length));
        // -6 dBFS for the first half, then silence measured from the audio, not from missing values.
        levels.Loudness.Take(250).Should().OnlyContain(v => v == 114);
        levels.Loudness.Skip(250).Should().OnlyContain(v => v == 0);
    }

    [Fact]
    public async Task ReadLevelsAsync_SurroundFramesFeedLoudnessAndDialogueFromOneRead()
    {
        // FL, FR, FC and the loudness mix; a trailing partial second still counts for dialogue,
        // a trailing partial 20 ms block does not count for loudness (as the separate reads did).
        using var pcm = new MemoryStream(Pcm(2.505, .01f, .01f, .2f, .25f));

        var levels = await AudioLevelService.ReadLevelsAsync(pcm, true, 2_505_000, null, null, null, TimeSpan.Zero, default);

        levels.CentreDialogue.Should().Equal(true, true, true);
        levels.Loudness.Should().HaveCount(125).And.OnlyContain(v => v == 108);
    }

    [Fact]
    public void ShouldYieldToSeeks_WaitsOutAScrubButNeverForever()
    {
        AudioLevelService.ShouldYieldToSeeks(0, seekInFlight: false).Should().BeTrue();
        AudioLevelService.ShouldYieldToSeeks(AudioLevelService.SeekQuietMs - 1, false).Should().BeTrue();
        AudioLevelService.ShouldYieldToSeeks(AudioLevelService.SeekQuietMs, false).Should().BeFalse();

        // A slow seek on a spinning disk keeps the disk, up to a bound.
        AudioLevelService.ShouldYieldToSeeks(AudioLevelService.SeekQuietMs * 2, true).Should().BeTrue();
        AudioLevelService.ShouldYieldToSeeks(AudioLevelService.MaxSeekYieldMs, true).Should().BeFalse();
    }

    private static byte[] Loudness(int seconds, int speech, params (int At, double Seconds, int Level)[] dips)
    {
        var values = Enumerable.Repeat((byte)(speech + 120), seconds * AudioLevelService.LoudnessPerSecond).ToArray();
        foreach (var (at, length, level) in dips)
            Array.Fill(values, (byte)(level + 120), at * AudioLevelService.LoudnessPerSecond, (int)(length * AudioLevelService.LoudnessPerSecond));
        return values;
    }

    [Fact]
    public void Silences_OnAQuietRecording_MarkOnlyTheDeepDips()
    {
        // Colbert-like: speech near -49 dBFS with -55 dB pauses. The analyzer's -50 dBFS allowance
        // would mark every pause; the timeline marks only what is near the recording's own floor.
        var dips = Enumerable.Range(0, 15).Select(i => (At: 20 + i * 30, Seconds: 1.0, Level: -95))
            .Concat(Enumerable.Range(0, 40).Select(i => (At: 30 + i * 12, Seconds: 0.5, Level: -55)))
            .Append((At: 595, Seconds: 0.1, Level: -95))
            .ToArray();

        var silences = AudioLevelService.Silences(Loudness(600, -49, dips));

        silences.Should().HaveCount(15);
        silences.Select(s => s.Start).Should().Equal(Enumerable.Range(0, 15).Select(i => (20 + i * 30) * 1_000_000L));
        silences.Should().OnlyContain(s => s.End - s.Start == 1_000_000);
    }

    [Fact]
    public void Silences_NoneWhenTheRecordingIsNeverMuchQuieterThanUsual()
    {
        AudioLevelService.Silences(Loudness(600, -30, (100, 2, -40))).Should().BeEmpty();
        AudioLevelService.Silences([]).Should().BeEmpty();
    }

    [Fact]
    public async Task AudioLevelPass_AConsumerThatStopsWaitingDoesNotCancelItForOthers()
    {
        // The analyzer joins the timeline's pass; cancelling analysis must leave the graph's pass running.
        var finish = new TaskCompletionSource<AudioLevels>();
        var pass = new AudioLevelPass("recording.mp4", (_, ct) => finish.Task.WaitAsync(ct));
        var reported = new List<double>();
        using var cancelled = new CancellationTokenSource();

        var analysis = pass.JoinAsync(null, cancelled.Token);
        var timeline = pass.JoinAsync(new SyncProgress(reported.Add), CancellationToken.None);
        pass.Report(.5);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
        finish.SetResult(new([7], []));

        (await timeline).Loudness.Should().Equal(7);
        pass.Completion.IsCompletedSuccessfully.Should().BeTrue();
        reported.Should().Equal(0, .5);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Theory]
    [InlineData("5.1", "ac3", false)]
    [InlineData("5.1", "aac", true)]
    [InlineData("stereo", "ac3", false)]
    [InlineData("mono", "aac", true)]
    public async Task MeasureAsync_MatchesTheSeparateReadsItReplaces(string layout, string codec, bool timestampOffset)
    {
        // Ad-break analysis must see exactly the evidence it did before the reads were combined.
        var runner = Runner();
        var path = Path.Combine(_testDir, $"levels-{layout}.mp4");
        string channel(int i) => $"(0.05+0.04*sin(2*PI*0.3*t+{i}))*sin(2*PI*{300 + 100 * i}*t)*gt(mod(t\\,7)\\,0.6)";
        int channels = layout switch { "5.1" => 6, "stereo" => 2, _ => 1 };
        var source = string.Join("|", Enumerable.Range(0, channels).Select(channel)) + $":s=48000:d=20:c={layout}";
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=gray:s=64x36:r=5:d=20", "-f", "lavfi", "-i", "aevalsrc=" + source,
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", codec, "-b:a", "192k" };
        if (timestampOffset) args.AddRange(["-output_ts_offset", "1.3"]);
        args.Add(path);
        var created = await runner.RunFfmpegAsync(args);
        Assert.True(created.Success, created.Stderr);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var media = await new MediaProbeService(runner).ProbeAsync(path);

        var levels = await new AudioLevelService(runner).MeasureAsync(path, media);

        levels.Loudness.Should().NotBeEmpty().And.Equal(await LegacyAudioReference.LoudnessAsync(runner, path, media));
        levels.CentreDialogue.Should().Equal(await LegacyAudioReference.CentreDialogueAsync(runner, path, media));
        if (layout == "5.1") levels.CentreDialogue.Should().NotBeEmpty();
        SHA256.HashData(await File.ReadAllBytesAsync(path)).Should().Equal(hash);
    }

    [Fact]
    public async Task Analysis_UsesTheSuppliedAudioPassInsteadOfReadingTheAudioAgain()
    {
        var runner = Runner();
        var path = Path.Combine(_testDir, "shared.mp4");
        var created = await runner.RunFfmpegAsync(["-v", "error", "-f", "lavfi", "-i", "color=gray:s=320x180:r=10:d=20",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=20", "-c:v", "libx264", "-preset", "ultrafast",
            "-pix_fmt", "yuv420p", "-c:a", "aac", path]);
        Assert.True(created.Success, created.Stderr);
        var media = await new MediaProbeService(runner).ProbeAsync(path);
        var shared = new AudioLevels(Enumerable.Repeat((byte)7, 1000).ToArray(), [true, false]);
        int calls = 0;

        var evidence = await new SuggestionEvidenceCollector(runner).CollectAsync(path, media, null, CancellationToken.None,
            (_, _) => { calls++; return Task.FromResult(shared); });

        calls.Should().Be(1);
        evidence.Loudness.Should().BeSameAs(shared.Loudness);
        evidence.CentreDialogue.Should().Equal(shared.CentreDialogue);
    }

    [Fact]
    public void TimelineMedia_IsOnForNewUsersButASavedChoiceStands()
    {
        var vm = new MainViewModel();
        vm.ShowWaveform.Should().BeTrue();
        vm.ShowThumbnails.Should().BeTrue();
        var fresh = System.Text.Json.JsonSerializer.Deserialize<UserPreferences>("{}")!;
        fresh.ShowWaveform.Should().BeTrue("a first launch has no saved file");
        fresh.ShowThumbnails.Should().BeTrue();
        var chose = System.Text.Json.JsonSerializer.Deserialize<UserPreferences>("{\"ShowWaveform\":false,\"ShowThumbnails\":false}")!;
        chose.ShowWaveform.Should().BeFalse("someone who turned it off keeps it off");
        chose.ShowThumbnails.Should().BeFalse();
    }

    private static Models.MediaInfo Media() => new(10_000_000, "h264", 320, 180, new(30, 1), [new(1, "ac3", 6, true)], 1);

    internal static FfmpegRunner Runner()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdTrim.sln")))
            {
                Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(directory.FullName, "binaries", "ffmpeg", "win-x64"));
                return new FfmpegRunner();
            }
        throw new DirectoryNotFoundException("Source checkout with bundled media tools required.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }
}
