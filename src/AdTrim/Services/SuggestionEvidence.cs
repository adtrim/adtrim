using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdTrim.Models;

namespace AdTrim.Services;

public sealed record SuggestionEvidence(IReadOnlyList<(long Start, long End)> Black,
    IReadOnlyList<(long Time, double Score)> Scenes, IReadOnlyList<double> GraphicMatches,
    int GraphicPixels, bool FieldTransitions)
{
    public IReadOnlyList<bool> CentreDialogue { get; init; } = [];
    /// <summary>Per-second compact color descriptors, <see cref="ShowProfile.Width"/> bytes each.</summary>
    public byte[] Appearance { get; init; } = [];
    /// <summary>Primary-audio loudness every 20 ms, stored as dBFS + 120 (0 = -120 dB or quieter).</summary>
    public byte[] Loudness { get; init; } = [];
}

public sealed class SuggestionEvidenceCollector(FfmpegRunner runner)
{
    internal const int MaximumSamples = 172_800;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private Func<CancellationToken, ValueTask>? _waitForIdle;
    private static readonly Regex Pts = new(@"pts_time:(?<t>[\d.]+)");
    private static readonly Regex Score = new(@"lavfi\.scene_score=(?<v>[\d.]+)");

    /// <param name="audio">Supplies the recording's <see cref="AudioLevels"/>, reporting progress
    /// from 0 to 1. The editor passes its already-running timeline pass so the audio is read once;
    /// without it the collector measures the audio itself.</param>
    /// <param name="waitForIdle">Awaited before reading each analysis frame. While it is pending,
    /// ffmpeg blocks on the full pipe and stops reading the recording, so playback seeks
    /// (scrubbing) get the disk to themselves.</param>
    public async Task<SuggestionEvidence> CollectAsync(string path, MediaInfo media,
        IProgress<CommercialDetectionProgress>? progress, CancellationToken ct,
        Func<IProgress<double>, CancellationToken, Task<AudioLevels>>? audio = null,
        Func<CancellationToken, ValueTask>? waitForIdle = null)
    {
        // "Waiting for another recording" only when another analysis really holds the slot.
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            progress?.Report(new(0, media.DurationUs, true));
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        _waitForIdle = waitForIdle;
        try
        {
            // Audio first: a shared pass started when the recording opened is usually under way,
            // and finishing it before the picture passes keeps one full-file read on the disk at a time.
            audio ??= (stage, token) => new AudioLevelService(runner).MeasureAsync(path, media, token, progress: stage.Report);
            var levels = await audio(new StageProgress(f => progress?.Report(SuggestionEngine.StageProgress(0, f, media.DurationUs))), ct).ConfigureAwait(false);
            var probe = await runner.RunFfprobeAsync(["-v", "error", "-select_streams", "v:0",
                "-read_intervals", "%+3", "-show_entries", "frame=interlaced_frame", "-of", "json", path], ct).ConfigureAwait(false);
            if (!probe.Success) throw new InvalidOperationException("Cannot inspect video field order: " + probe.ErrorSummary);
            using var json = JsonDocument.Parse(probe.Stdout);
            bool fields = json.RootElement.GetProperty("frames").EnumerateArray()
                .Any(f => f.TryGetProperty("interlaced_frame", out var flag) && flag.GetInt32() != 0);
            var black = new List<(long Start, long End)>();
            var scenes = new List<(long Time, double Score)>();
            var graphic = new RecurringGraphic();
            long? pendingBlack = null, pendingTime = null;
            bool limited = false;
            void Diagnostic(string line)
            {
                if (black.Count + scenes.Count >= 200_000) { limited = true; return; }
                if (CommercialBreakDetector.TryParseBlackEvent(line, ref pendingBlack, out var interval)) black.Add(interval);
                var pts = Pts.Match(line);
                if (pts.Success) pendingTime = Seconds(pts.Groups["t"].Value);
                var score = Score.Match(line);
                if (score.Success && pendingTime is { } time)
                {
                    var value = double.Parse(score.Groups["v"].Value, CultureInfo.InvariantCulture);
                    if (value > .32) scenes.Add((time, value));
                    pendingTime = null;
                }
            }
            var graph = "[0:v:0]split=3[dark][scene][sample];"
                + "[dark]scale=320:180,blackdetect=d=0.06:pix_th=0.10[blackout];"
                + "[scene]" + (fields ? "separatefields," : "")
                + "scale=320:180,select='eq(n,0)+gt(scene\\,0.32)',metadata=print[sceneout];"
                + "[sample]fps=1:start_time=0,scale=320:180,format=gray[samples]";
            List<string> args = ["-nostdin", "-hide_banner", "-nostats", "-v", "info", .. FfmpegRunner.SourceTimestampArgs(media.TimelineStartTimeUs), "-i", path,
                "-filter_complex", graph, "-map", "[blackout]", "-map", "[sceneout]", "-an", "-f", "null", "-",
                "-map", "[samples]", "-an", "-f", "rawvideo", "pipe:1"];
            await ReadAsync(args, (frame, index) => { if (index % 5 == 0) graphic.Observe(frame); }, Diagnostic,
                media.DurationUs, progress, ct, stage: 1).ConfigureAwait(false);
            if (limited) throw new InvalidOperationException("Too many transitions to analyze safely.");
            var matches = new List<double>();
            var appearance = new List<byte>();
            // Runs without a station graphic too: the recording's own appearance is still evidence.
            bool learned = graphic.Complete();
            await ReadAsync(["-nostdin", "-hide_banner", "-nostats", "-v", "error", .. FfmpegRunner.SourceTimestampArgs(media.TimelineStartTimeUs), "-i", path,
                "-map", "0:v:0", "-vf", "fps=1:start_time=0,scale=320:180,format=yuv444p", "-an", "-f", "rawvideo", "pipe:1"],
                (frame, _) =>
                {
                    if (learned) matches.Add(graphic.Match(frame.AsSpan(0, RecurringGraphic.FrameBytes)));
                    ShowProfile.Describe(frame, appearance);
                }, null, media.DurationUs, progress, ct, 3 * RecurringGraphic.FrameBytes, stage: 2).ConfigureAwait(false);
            return new(black.OrderBy(w => w.Start).ToArray(), scenes.OrderBy(s => s.Time).ToArray(), matches, graphic.PatternPixels, fields)
                { CentreDialogue = levels.CentreDialogue, Appearance = appearance.ToArray(), Loudness = levels.Loudness };
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reports synchronously, so one step's fractions reach the caller in order.</summary>
    private sealed class StageProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    internal static byte LoudnessByte(ReadOnlySpan<byte> samples)
    {
        double sum = 0;
        int count = samples.Length / sizeof(float);
        for (int i = 0; i < count; i++)
        {
            float v = BitConverter.ToSingle(samples.Slice(i * sizeof(float), sizeof(float)));
            if (float.IsFinite(v)) sum += v * (double)v;
        }
        double db = 20 * Math.Log10(Math.Max(Math.Sqrt(sum / Math.Max(1, count)), 1e-6));
        return (byte)Math.Clamp(Math.Round(db + 120), 0, 120);
    }

    internal static bool IsCentreDialogue(ReadOnlySpan<byte> samples)
    {
        double left = 0, right = 0, centre = 0;
        for (int i = 0; i < samples.Length; i += 3 * sizeof(float))
        {
            float l = BitConverter.ToSingle(samples.Slice(i, 4));
            float r = BitConverter.ToSingle(samples.Slice(i + 4, 4));
            float c = BitConverter.ToSingle(samples.Slice(i + 8, 4));
            if (!float.IsFinite(l) || !float.IsFinite(r) || !float.IsFinite(c)) return false;
            left += l * (double)l; right += r * (double)r; centre += c * (double)c;
        }
        return samples.Length > 0 && centre > samples.Length / 12 * .002 * .002
            && Math.Sqrt(centre) > 2 * (Math.Sqrt(left) + Math.Sqrt(right));
    }

    private async Task ReadAsync(IEnumerable<string> args, Action<byte[], int> consume,
        Action<string>? diagnostic, long durationUs, IProgress<CommercialDetectionProgress>? progress, CancellationToken ct,
        int frameBytes = RecurringGraphic.FrameBytes, int stage = 1)
    {
        // Each video pass is one named step of a single, forward-only progress bar.
        var result = await runner.RunFfmpegBinaryAsync(args, async stream =>
        {
            var buffer = new byte[frameBytes];
            int index = 0;
            while (true)
            {
                if (_waitForIdle is not null) await _waitForIdle(ct).ConfigureAwait(false);
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                while (read < buffer.Length)
                {
                    int next = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (next == 0) throw new InvalidDataException("Incomplete analysis frame.");
                    read += next;
                }
                if (index >= MaximumSamples) throw new InvalidOperationException("Recording exceeds the bounded analysis capacity.");
                consume(buffer, index);
                if (index % 5 == 0) progress?.Report(SuggestionEngine.StageProgress(stage, index * 1_000_000.0 / Math.Max(1, durationUs), durationUs));
                index++;
            }
        }, diagnostic, ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Suggestion analysis failed: " + result.ErrorSummary);
    }

    private static long Seconds(string value) => (long)Math.Round(double.Parse(value, CultureInfo.InvariantCulture) * 1_000_000);
}
