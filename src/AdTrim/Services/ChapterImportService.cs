using System.Globalization;
using System.Text.Json;
using AdTrim.Models;

namespace AdTrim.Services;

public sealed record ChapterBoundary(long TimeUs, string? Title);

/// <summary>
/// Imports MP4 chapter markers (written by ComSkip post-processing) into splits.
/// V1 policy: do not auto-exclude `Commercial*` chapters - neutral import is what
/// the user asked for. Dedupes boundaries within 250ms; drops the 0/duration bookends.
/// </summary>
public sealed class ChapterImportService
{
    private const long DedupeWindowUs = 250_000; // 250ms

    private readonly FfmpegRunner _runner;

    public ChapterImportService(FfmpegRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<ChapterBoundary>> ImportAsync(
        string sourcePath, long durationUs, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_chapters",
            sourcePath,
        };
        var r = await _runner.RunFfprobeAsync(args, ct).ConfigureAwait(false);
        if (!r.Success)
            throw new InvalidOperationException($"ffprobe failed (exit {r.ExitCode}): {r.Stderr}");

        var raw = ParseChapters(r.Stdout);
        return Normalize(raw, durationUs);
    }

    internal static IReadOnlyList<ChapterBoundary> ParseChapters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chapters", out var arr)) return Array.Empty<ChapterBoundary>();

        var list = new List<ChapterBoundary>();
        foreach (var c in arr.EnumerateArray())
        {
            // chapter timing: prefer start_time (string seconds), fall back to start + time_base
            long startUs = 0;
            if (c.TryGetProperty("start_time", out var st)
                && double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                startUs = (long)Math.Round(sec * 1_000_000.0);
            }

            string? title = null;
            if (c.TryGetProperty("tags", out var tags)
                && tags.TryGetProperty("title", out var t))
                title = t.GetString();

            list.Add(new ChapterBoundary(startUs, title));
        }
        return list;
    }

    /// <summary>Drop 0 and duration bookends, dedupe within 250ms, sort ascending.</summary>
    internal static IReadOnlyList<ChapterBoundary> Normalize(
        IReadOnlyList<ChapterBoundary> raw, long durationUs)
    {
        var sorted = raw
            .Where(b => b.TimeUs > 0 && b.TimeUs < durationUs)
            .OrderBy(b => b.TimeUs)
            .ToList();

        var deduped = new List<ChapterBoundary>(sorted.Count);
        foreach (var b in sorted)
        {
            if (deduped.Count > 0 && b.TimeUs - deduped[^1].TimeUs < DedupeWindowUs) continue;
            deduped.Add(b);
        }
        return deduped;
    }
}
