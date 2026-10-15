using System.Globalization;
using System.IO;
using System.Text.Json;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.Tests;

/// <summary>
/// The two separate audio reads ad-break analysis made before v1.1.21, kept verbatim (minus
/// progress) as the reference the shared single pass must reproduce exactly.
/// </summary>
internal static class LegacyAudioReference
{
    public static async Task<IReadOnlyList<bool>> CentreDialogueAsync(FfmpegRunner runner, string path, MediaInfo media, CancellationToken ct = default)
    {
        if (media.PrimaryAudio?.Channels != 6) return [];
        var probe = await runner.RunFfprobeAsync(["-v", "error", "-select_streams", media.PrimaryAudioIndex.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "stream=channel_layout", "-of", "json", path], ct).ConfigureAwait(false);
        if (!probe.Success) throw new InvalidOperationException("Cannot inspect primary audio layout: " + probe.Stderr);
        using var json = JsonDocument.Parse(probe.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (streams.Length != 1 || !streams[0].TryGetProperty("channel_layout", out var layout)
            || layout.GetString() is not ("5.1" or "5.1(side)")) return [];
        var dialogue = new List<bool>();
        var result = await runner.RunFfmpegBinaryAsync(["-nostdin", "-hide_banner", "-v", "error", "-copyts", "-i", path,
            "-map", "0:" + media.PrimaryAudioIndex.ToString(CultureInfo.InvariantCulture), "-vn",
            "-af", "pan=3c|c0=FL|c1=FR|c2=FC,aresample=8000:first_pts=0", "-f", "f32le", "pipe:1"], async stream =>
        {
            var buffer = new byte[8000 * 3 * sizeof(float)];
            while (true)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int next = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (next == 0) break;
                    read += next;
                }
                if (read == 0) break;
                if (read % (3 * sizeof(float)) != 0) throw new InvalidDataException("Incomplete analysis audio sample.");
                dialogue.Add(SuggestionEvidenceCollector.IsCentreDialogue(buffer.AsSpan(0, read)));
            }
        }, null, ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Primary audio analysis failed: " + result.Stderr);
        return dialogue;
    }

    public static async Task<byte[]> LoudnessAsync(FfmpegRunner runner, string path, MediaInfo media, CancellationToken ct = default)
    {
        if (media.PrimaryAudio is not { } audio) return [];
        string mix = audio.Channels >= 2 ? "pan=mono|c0=0.5*c0+0.5*c1," : "";
        var levels = new List<byte>();
        var result = await runner.RunFfmpegBinaryAsync(["-nostdin", "-hide_banner", "-v", "error", "-copyts", "-i", path,
            "-map", "0:" + media.PrimaryAudioIndex.ToString(CultureInfo.InvariantCulture), "-vn",
            "-af", mix + "aresample=8000:first_pts=0", "-ac", "1", "-f", "f32le", "pipe:1"], async stream =>
        {
            var buffer = new byte[160 * sizeof(float)];
            while (true)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int next = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                    if (next == 0) break;
                    read += next;
                }
                if (read < buffer.Length) break;
                levels.Add(SuggestionEvidenceCollector.LoudnessByte(buffer));
            }
        }, null, ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Primary audio loudness analysis failed: " + result.Stderr);
        return levels.ToArray();
    }
}
