using System.Globalization;
using System.Text.Json;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>
/// Wraps `ffprobe -of json` parsing into a strongly-typed MediaInfo.
/// Audio stream selection is probe-driven: primary = (highest channel count)
/// then (disposition.default == 1) then (lowest index). No hard-coded 0:a:0.
/// </summary>
public sealed class MediaProbeService
{
    private readonly FfmpegRunner _runner;

    public MediaProbeService(FfmpegRunner runner) => _runner = runner;

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success)
            throw new InvalidOperationException($"ffprobe failed (exit {r.ExitCode}): {r.Stderr}");

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;

        // Duration: prefer format.duration (seconds), convert to µs
        long durationUs = 0;
        if (root.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("duration", out var durProp)
            && double.TryParse(durProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durSec))
        {
            durationUs = (long)Math.Round(durSec * 1_000_000.0);
        }

        // Streams
        string vCodec = "";
        int width = 0, height = 0;
        Rational fps = new(0, 1);
        long videoStartTimeUs = 0;
        var audio = new List<AudioStream>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var index = s.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";

                if (type == "video" && string.IsNullOrEmpty(vCodec))
                {
                    vCodec = codec;
                    if (s.TryGetProperty("width", out var w))  width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h)) height = h.GetInt32();
                    // Prefer r_frame_rate (canonical CFR target, e.g. 60000/1001)
                    // over avg_frame_rate (computed from PTS spans and often a
                    // weird non-canonical ratio like 1099413813/18341887, which
                    // makes FrameSnap's grid drift relative to the file's real
                    // frame PTS). Fall back if r_frame_rate is missing or 0/0.
                    Rational? rfr = null;
                    if (s.TryGetProperty("r_frame_rate", out var rRate))
                    {
                        var parsed = Rational.Parse(rRate.GetString() ?? "0/0");
                        if (parsed.Numerator > 0 && parsed.Denominator > 0) rfr = parsed;
                    }
                    if (rfr is null && s.TryGetProperty("avg_frame_rate", out var afr))
                        rfr = Rational.Parse(afr.GetString() ?? "0/1");
                    fps = rfr ?? new Rational(0, 1);

                    // Video stream's start_time is the PTS of the first video
                    // frame. For Plex .ts captures preserved through autoconvert,
                    // this is often non-zero (1.8 s on BBT, 0 s on Rookie).
                    // FrameSnap needs it as a phase offset so its frame grid
                    // aligns with the file's actual PTS values.
                    if (s.TryGetProperty("start_time", out var stProp)
                        && double.TryParse(stProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stSec))
                    {
                        videoStartTimeUs = (long)Math.Round(stSec * 1_000_000.0);
                    }
                }
                else if (type == "audio")
                {
                    var channels = s.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                    var isDefault = false;
                    if (s.TryGetProperty("disposition", out var disp)
                        && disp.TryGetProperty("default", out var df))
                        isDefault = df.GetInt32() == 1;
                    audio.Add(new AudioStream(index, codec, channels, isDefault));
                }
            }
        }

        var primary = SelectPrimaryAudio(audio);
        return new MediaInfo(durationUs, vCodec, width, height, fps, audio, primary, videoStartTimeUs);
    }

    /// <summary>
    /// Highest channel count → default disposition → lowest index.
    /// Returns the stream index (`s.index` from ffprobe), or -1 if no audio.
    /// </summary>
    public static int SelectPrimaryAudio(IReadOnlyList<AudioStream> audio)
    {
        if (audio.Count == 0) return -1;
        return audio
            .OrderByDescending(a => a.Channels)
            .ThenByDescending(a => a.Default)
            .ThenBy(a => a.Index)
            .First().Index;
    }
}
