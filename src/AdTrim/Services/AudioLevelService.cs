using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Primary-audio levels on the source clock: loudness every 20 ms (dBFS + 120,
/// 0 = -120 dB or quieter) and, for 5.1 recordings, whether each second is centre dialogue.</summary>
public sealed record AudioLevels(byte[] Loudness, bool[] CentreDialogue);

/// <summary>
/// Measures <see cref="AudioLevels"/> in one decode of the primary audio stream. The timeline
/// waveform and ad-break analysis share the result, so opening and analyzing a recording reads
/// its audio once. Recordings usually live on a spinning disk where a full audio pass streams
/// the whole file, so a pass can yield to playback seeks and publishes partial loudness as it goes.
/// Results are cached under local app data when a cache root is given.
/// </summary>
public sealed class AudioLevelService(FfmpegRunner? runner, string? cacheRoot = null)
{
    public const int LoudnessPerSecond = 50;
    private const int SampleRate = 8000, LoudnessBlock = SampleRate / LoudnessPerSecond, DialogueBlock = SampleRate;

    private const string CacheExtension = ".adtlevels";
    private const string CacheMagic = "ADTLEVELS";
    public const int CacheVersion = 2;

    /// <summary>How often a pass publishes the loudness measured so far.</summary>
    public static readonly TimeSpan PartialInterval = TimeSpan.FromMilliseconds(500);

    // Dragging the playhead issues a burst of seeks; resume between bursts, not between seeks.
    public const long SeekQuietMs = 400;
    // A preview that never reports its seek finished must not stall the pass forever.
    public const long MaxSeekYieldMs = 3000;

    /// <summary>Whether a pass should keep leaving the disk to playback seeks.</summary>
    public static bool ShouldYieldToSeeks(long msSinceSeekActivity, bool seekInFlight)
        => msSinceSeekActivity < (seekInFlight ? MaxSeekYieldMs : SeekQuietMs);

    /// <summary>Loudness range the timeline graph spans, dBFS. Fixed so a partial graph keeps its shape.</summary>
    public const int GraphFloorDb = -80, GraphCeilingDb = -10;
    private const int MinimumSilenceValues = 15; // 0.3 s

    /// <summary>
    /// Silences to highlight on the timeline: runs of at least 0.3 s at or below 6 dB above the
    /// recording's quietest 1%. That is the analyzer's recording-relative floor (see
    /// <see cref="LogoFreeSegmenter.QuietRuns"/>) without its -50 dBFS allowance, which on quiet
    /// recordings sits near the median and would mark half the show. None when the quietest 1% is
    /// within 15 dB of the median (constant sound). Accepts the partial array of a running pass.
    /// </summary>
    public static List<(long Start, long End)> Silences(byte[] loudness)
    {
        var runs = new List<(long Start, long End)>();
        if (loudness.Length == 0) return runs;
        var histogram = new int[256];
        foreach (var value in loudness) histogram[value]++;
        int Rank(int k)
        {
            for (int value = 0, seen = 0; value < histogram.Length; value++)
                if ((seen += histogram[value]) > k) return value;
            return histogram.Length - 1;
        }
        int floor = Rank((int)(.01 * (loudness.Length - 1))), median = Rank(loudness.Length / 2);
        if (floor > median - 15) return runs;
        int level = floor + 6;
        for (int i = 0; i < loudness.Length;)
        {
            if (loudness[i] > level) { i++; continue; }
            int j = i;
            while (j < loudness.Length && loudness[j] <= level) j++;
            if (j - i >= MinimumSilenceValues)
                runs.Add((i * 1_000_000L / LoudnessPerSecond, j * 1_000_000L / LoudnessPerSecond));
            i = j;
        }
        return runs;
    }

    public static string DefaultCacheRoot => Path.Combine(AppDataDirectory.Path, "audio-levels");

    public static string CachePathFor(string sourcePath, int audioStreamIndex, long durationUs,
        string cacheRoot, int cacheVersion = CacheVersion)
    {
        var hash = FrameCacheService.ComputeSourceHash(sourcePath);
        return Path.Combine(cacheRoot, $"{hash}_a{audioStreamIndex}_d{durationUs}_v{cacheVersion}{CacheExtension}");
    }

    public void PreserveAfterMetadataUpdate(string sourcePath, int audioStreamIndex, SourceFingerprint before)
    {
        if (cacheRoot is null) return;
        var oldPath = Path.Combine(cacheRoot,
            $"{FrameCacheService.ComputeSourceHash(sourcePath, before)}_a{audioStreamIndex}_d{before.DurationUs}_v{CacheVersion}{CacheExtension}");
        var newPath = CachePathFor(sourcePath, audioStreamIndex, before.DurationUs, cacheRoot);
        if (oldPath != newPath && File.Exists(oldPath)) File.Copy(oldPath, newPath, overwrite: true);
    }

    /// <summary>Starts a pass that several consumers can join; see <see cref="AudioLevelPass"/>.</summary>
    public AudioLevelPass Start(string sourcePath, MediaInfo media,
        Func<CancellationToken, ValueTask>? waitForIdle = null, Action<byte[]>? partialLoudness = null)
        => new(sourcePath, (pass, ct) => GetOrMeasureAsync(sourcePath, media, ct, waitForIdle, partialLoudness, pass.Report));

    public async Task<AudioLevels> GetOrMeasureAsync(string sourcePath, MediaInfo media, CancellationToken ct = default,
        Func<CancellationToken, ValueTask>? waitForIdle = null, Action<byte[]>? partialLoudness = null,
        Action<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (cacheRoot is not null && TryLoadCached(sourcePath, media.PrimaryAudioIndex, media.DurationUs, out var cached))
        {
            progress?.Invoke(1);
            return cached;
        }
        var levels = await MeasureAsync(sourcePath, media, ct, waitForIdle, partialLoudness, progress).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (cacheRoot is not null) SaveCached(sourcePath, media.PrimaryAudioIndex, media.DurationUs, levels);
        return levels;
    }

    public async Task<AudioLevels> MeasureAsync(string sourcePath, MediaInfo media, CancellationToken ct = default,
        Func<CancellationToken, ValueTask>? waitForIdle = null, Action<byte[]>? partialLoudness = null,
        Action<double>? progress = null)
    {
        if (media.PrimaryAudio is not { } audio) return new([], []);
        var ffmpeg = runner ?? throw new InvalidOperationException("FFmpeg is required to measure audio levels.");
        if (waitForIdle is not null) await waitForIdle(ct).ConfigureAwait(false);
        var stream = media.PrimaryAudioIndex.ToString(CultureInfo.InvariantCulture);
        bool dialogue = audio.Channels == 6 && await HasSurroundLayoutAsync(ffmpeg, sourcePath, stream, ct).ConfigureAwait(false);
        // The mixes the analysis has always used: front left/right for loudness (the same picture
        // for stereo and 5.1 broadcasts) and front left/right/centre for dialogue. In the 5.1 case
        // the loudness mix rides along as a fourth channel. pan cannot mix channel names and numbers;
        // both accepted 5.1 layouts start FL, FR, FC.
        string mix = dialogue ? "pan=4c|c0=c0|c1=c1|c2=c2|c3=0.5*c0+0.5*c1,"
            : audio.Channels >= 2 ? "pan=mono|c0=0.5*c0+0.5*c1," : "";
        List<string> args = ["-nostdin", "-hide_banner", "-v", "error", .. FfmpegRunner.SourceTimestampArgs(media.TimelineStartTimeUs), "-i", sourcePath,
            "-map", "0:" + stream, "-vn", "-af", mix + "aresample=" + SampleRate.ToString(CultureInfo.InvariantCulture) + ":first_pts=0"];
        if (!dialogue) args.AddRange(["-ac", "1"]);
        args.AddRange(["-f", "f32le", "pipe:1"]);

        AudioLevels? levels = null;
        var result = await ffmpeg.RunFfmpegBinaryAsync(args, async pcm => levels = await ReadLevelsAsync(pcm, dialogue,
            media.DurationUs, waitForIdle, partialLoudness, progress, PartialInterval, ct).ConfigureAwait(false), null, ct).ConfigureAwait(false);
        if (!result.Success || levels is null) throw new InvalidOperationException("Primary audio analysis failed: " + result.Stderr);
        return levels;
    }

    private static async Task<bool> HasSurroundLayoutAsync(FfmpegRunner ffmpeg, string path, string stream, CancellationToken ct)
    {
        var probe = await ffmpeg.RunFfprobeAsync(["-v", "error", "-select_streams", stream,
            "-show_entries", "stream=channel_layout", "-of", "json", path], ct).ConfigureAwait(false);
        if (!probe.Success) throw new InvalidOperationException("Cannot inspect primary audio layout: " + probe.Stderr);
        using var json = JsonDocument.Parse(probe.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        return streams.Length == 1 && streams[0].TryGetProperty("channel_layout", out var layout)
            && layout.GetString() is "5.1" or "5.1(side)";
    }

    /// <summary>Measures 8 kHz f32le frames: one loudness channel, or FL/FR/FC plus the loudness mix
    /// when <paramref name="dialogue"/>. Awaits <paramref name="waitForIdle"/> before every read; while
    /// it is pending ffmpeg blocks on the full pipe and stops reading the source.</summary>
    internal static async Task<AudioLevels> ReadLevelsAsync(Stream pcm, bool dialogue, long durationUs,
        Func<CancellationToken, ValueTask>? waitForIdle, Action<byte[]>? partialLoudness, Action<double>? progress,
        TimeSpan partialInterval, CancellationToken ct)
    {
        const int Sample = sizeof(float);
        int frameBytes = (dialogue ? 4 : 1) * Sample, mixAt = frameBytes - Sample;
        var loudness = new List<byte>();
        var centre = new List<bool>();
        var mix = new byte[LoudnessBlock * Sample];
        var front = new byte[DialogueBlock * 3 * Sample];
        int mixFill = 0, frontFill = 0, carried = 0, published = 0;
        long frames = 0, expected = Math.Max(1, durationUs * SampleRate / 1_000_000);
        var buffer = new byte[64 * 1024];
        var sinceReport = Stopwatch.StartNew();

        while (true)
        {
            if (waitForIdle is not null) await waitForIdle(ct).ConfigureAwait(false);
            int read = await pcm.ReadAsync(buffer.AsMemory(carried), ct).ConfigureAwait(false);
            if (read <= 0) break;
            int available = carried + read, offset = 0;
            for (; offset + frameBytes <= available; offset += frameBytes)
            {
                buffer.AsSpan(offset + mixAt, Sample).CopyTo(mix.AsSpan(mixFill));
                if ((mixFill += Sample) == mix.Length)
                {
                    if (loudness.Count >= SuggestionEvidenceCollector.MaximumSamples * LoudnessPerSecond)
                        throw new InvalidOperationException("Recording exceeds the bounded analysis capacity.");
                    loudness.Add(SuggestionEvidenceCollector.LoudnessByte(mix));
                    mixFill = 0;
                }
                if (!dialogue) continue;
                buffer.AsSpan(offset, 3 * Sample).CopyTo(front.AsSpan(frontFill));
                if ((frontFill += 3 * Sample) == front.Length)
                {
                    AddDialogue(centre, front);
                    frontFill = 0;
                }
            }
            frames += offset / frameBytes;
            carried = available - offset;
            buffer.AsSpan(offset, carried).CopyTo(buffer);

            if (sinceReport.Elapsed >= partialInterval)
            {
                progress?.Invoke(Math.Min(1, frames / (double)expected));
                if (partialLoudness is not null && loudness.Count > published)
                {
                    partialLoudness(loudness.ToArray());
                    published = loudness.Count;
                }
                sinceReport.Restart();
            }
        }
        if (carried != 0) throw new InvalidDataException("Incomplete analysis audio sample.");
        // A trailing partial second still counts for dialogue; a partial 20 ms block does not.
        if (frontFill > 0) AddDialogue(centre, front.AsSpan(0, frontFill));
        progress?.Invoke(1);
        return new(loudness.ToArray(), centre.ToArray());
    }

    private static void AddDialogue(List<bool> centre, ReadOnlySpan<byte> second)
    {
        if (centre.Count >= SuggestionEvidenceCollector.MaximumSamples)
            throw new InvalidOperationException("Recording exceeds the bounded analysis capacity.");
        centre.Add(SuggestionEvidenceCollector.IsCentreDialogue(second));
    }

    public bool TryLoadCached(string sourcePath, int audioStreamIndex, long durationUs, out AudioLevels levels)
    {
        levels = new([], []);
        if (cacheRoot is null) return false;
        var path = CachePathFor(sourcePath, audioStreamIndex, durationUs, cacheRoot);
        if (!File.Exists(path)) return false;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8, leaveOpen: false);
            if (reader.ReadString() != CacheMagic || reader.ReadInt32() != CacheVersion
                || reader.ReadInt32() != audioStreamIndex || reader.ReadInt64() != durationUs) return false;
            int loudnessCount = reader.ReadInt32();
            if (loudnessCount < 0 || loudnessCount > SuggestionEvidenceCollector.MaximumSamples * LoudnessPerSecond) return false;
            var loudness = reader.ReadBytes(loudnessCount);
            int dialogueCount = reader.ReadInt32();
            if (loudness.Length != loudnessCount || dialogueCount < 0 || dialogueCount > SuggestionEvidenceCollector.MaximumSamples) return false;
            var dialogue = reader.ReadBytes(dialogueCount);
            if (dialogue.Length != dialogueCount) return false;
            levels = new(loudness, dialogue.Select(b => b != 0).ToArray());
            reader.Dispose();
            MediaCacheCleanup.Touch(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SaveCached(string sourcePath, int audioStreamIndex, long durationUs, AudioLevels levels)
    {
        if (cacheRoot is null) return;
        try
        {
            Directory.CreateDirectory(cacheRoot);
            var path = CachePathFor(sourcePath, audioStreamIndex, durationUs, cacheRoot);
            var tmp = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(tmp), Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheVersion);
                writer.Write(audioStreamIndex);
                writer.Write(durationUs);
                writer.Write(levels.Loudness.Length);
                writer.Write(levels.Loudness);
                writer.Write(levels.CentreDialogue.Length);
                writer.Write(levels.CentreDialogue.Select(d => (byte)(d ? 1 : 0)).ToArray());
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Cache failures should never block editing or analysis.
        }
    }
}

/// <summary>
/// One running measurement for a recording. The timeline and ad-break analysis join the same
/// pass; a consumer that stops waiting does not cancel it for the others.
/// </summary>
public sealed class AudioLevelPass
{
    private readonly CancellationTokenSource _cancellation = new();
    private double _fraction;

    internal AudioLevelPass(string sourcePath, Func<AudioLevelPass, CancellationToken, Task<AudioLevels>> run)
    {
        SourcePath = sourcePath;
        Completion = Task.Run(() => run(this, _cancellation.Token));
    }

    public string SourcePath { get; }
    public Task<AudioLevels> Completion { get; }
    public double Fraction => Volatile.Read(ref _fraction);
    public event Action<double>? Progress;

    internal void Report(double fraction)
    {
        Volatile.Write(ref _fraction, fraction);
        Progress?.Invoke(fraction);
    }

    public void Cancel() => _cancellation.Cancel();

    /// <summary>Waits for the shared result, reporting the pass's progress meanwhile.</summary>
    public async Task<AudioLevels> JoinAsync(IProgress<double>? progress, CancellationToken ct)
    {
        void Forward(double fraction) => progress?.Report(fraction);
        Progress += Forward;
        try
        {
            progress?.Report(Fraction);
            return await Completion.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { Progress -= Forward; }
    }
}
