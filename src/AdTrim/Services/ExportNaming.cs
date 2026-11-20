using System.IO;
using AdTrim.Models;
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

    /// <summary>The export filename pattern AdTrim has always used: the recording's name with [AdTrim].</summary>
    public const string DefaultTemplate = "{title} [AdTrim]";
    private static readonly string[] TemplateTokens = ["{title}", "{version}", "{date}"];

    /// <summary>Why a filename pattern cannot be used, or null when it can.</summary>
    public static string? TemplateError(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Enter a filename pattern.";
        var text = template.Trim();
        if (text.Length > 120) return "Keep the pattern under 120 characters.";
        if (!text.Contains("{title}", StringComparison.Ordinal)) return "Include {title}, so each recording gets its own name.";
        var literal = TemplateTokens.Aggregate(text, (s, token) => s.Replace(token, "", StringComparison.Ordinal));
        if (literal.Contains('{') || literal.Contains('}')) return "Use only {title}, {version} and {date}.";
        if (InvalidCharsRx.IsMatch(literal)) return "Remove characters Windows does not allow in filenames: < > : \" / \\ | ? *";
        if (text.EndsWith('.')) return "A filename cannot end with a period.";
        return null;
    }

    public static string NormalizeTemplate(string? template) => TemplateError(template) is null ? template!.Trim() : DefaultTemplate;

    /// <summary>The pattern filled in, without an extension.</summary>
    public static string ApplyTemplate(string? template, string title, DateOnly date)
        => NormalizeTemplate(template).Replace("{title}", title, StringComparison.Ordinal)
            .Replace("{version}", AppVersion.Numeric, StringComparison.Ordinal)
            .Replace("{date}", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Choose an unused export name without changing the source file.</summary>
    public static string DeriveDefaultFilename(string? sourcePath, string? outputFolder = null, ExportFormat format = ExportFormat.Mp4,
        string? template = null)
    {
        var name = string.IsNullOrEmpty(sourcePath) ? "export" : Path.GetFileNameWithoutExtension(sourcePath);
        name = S00E00Rx.Replace(name, m => m.Value.ToLowerInvariant());
        // Exporting an export again does not stack AdTrim markers, including one with a version number.
        name = Regex.Replace(name, @" \[AdTrim(?: [0-9.]+)?\]$", "", RegexOptions.IgnoreCase);
        var filled = ApplyTemplate(template, name, DateOnly.FromDateTime(DateTime.Now));
        // [AdTrim] takes the number inside its brackets, as always; any other pattern gets (2), (3).
        bool bracketed = filled.Contains("[AdTrim]", StringComparison.Ordinal);
        var folder = outputFolder ?? Path.GetDirectoryName(sourcePath);
        for (int number = 1; ; number++)
        {
            var stem = number == 1 ? filled
                : bracketed ? filled.Replace("[AdTrim]", $"[AdTrim {number}]", StringComparison.Ordinal) : $"{filled} ({number})";
            var candidate = stem + format.Extension();
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
