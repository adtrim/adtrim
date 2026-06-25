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
    private static readonly Regex S00E00Rx = new(
        @"\b(S\d{2}E\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidCharsRx = new(
        @"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);

    /// <summary>
    /// Default filename:
    ///   `{source base name with S00E00 lowercased}-ADT-{unix-timestamp}.mp4`.
    /// </summary>
    public static string DeriveDefaultFilename(string? sourcePath, long? unixTimestamp = null)
    {
        if (string.IsNullOrEmpty(sourcePath)) return "export-ADT.mp4";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        name = S00E00Rx.Replace(name, m => m.Value.ToLowerInvariant());
        var ts = unixTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{name}-ADT-{ts}.mp4";
    }

    /// <summary>Returns true if the filename has no characters disallowed by Windows.</summary>
    public static bool IsValidFilename(string filename)
        => !string.IsNullOrWhiteSpace(filename) && !InvalidCharsRx.IsMatch(filename);
}
