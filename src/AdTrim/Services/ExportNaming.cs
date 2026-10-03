using System.IO;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

/// <summary>
/// Pure helpers for export-output naming. Kept as a top-level static class
/// so the test project can exercise the rules without dragging in the WPF
/// view-model graph.
/// </summary>
public static class ExportNaming
{
    private static readonly Regex EpisodeNameRx = new(
        @"^(?<show>.+?)(?: \(\d{4}\))? - S(?<season>[0-9]{1,3})E(?<episode>[0-9]{1,3}) - (?<title>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Title, string Subtitle) DisplayTitles(string? sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath) ?? "Video";
        var match = EpisodeNameRx.Match(name);
        if (!match.Success) return (name, "");
        return ($"{match.Groups["show"].Value} · Season {int.Parse(match.Groups["season"].Value)}, Episode {int.Parse(match.Groups["episode"].Value)}",
            match.Groups["title"].Value);
    }

    private static readonly Regex S00E00Rx = new(
        @"\b(S\d{2}E\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidCharsRx = new(
        @"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    /// <summary>Choose an unused export name without changing the source file.</summary>
    public static string DeriveDefaultFilename(string? sourcePath, string? outputFolder = null)
    {
        var name = string.IsNullOrEmpty(sourcePath) ? "export" : Path.GetFileNameWithoutExtension(sourcePath);
        name = S00E00Rx.Replace(name, m => m.Value.ToLowerInvariant());
        name = Regex.Replace(name, @" \[AdTrim(?: \d+)?\]$", "", RegexOptions.IgnoreCase);
        var folder = outputFolder ?? Path.GetDirectoryName(sourcePath);
        for (int number = 1; ; number++)
        {
            var suffix = number == 1 ? "AdTrim" : $"AdTrim {number}";
            var candidate = $"{name} [{suffix}].mp4";
            if (string.IsNullOrEmpty(folder)) return candidate;
            var path = Path.Combine(folder, candidate);
            if (!File.Exists(path) && !Directory.Exists(path)
                && (string.IsNullOrEmpty(sourcePath)
                    || !string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>Returns true if the filename has no characters disallowed by Windows.</summary>
    public static bool IsValidFilename(string filename)
        => !string.IsNullOrWhiteSpace(filename) && !InvalidCharsRx.IsMatch(filename);
}
