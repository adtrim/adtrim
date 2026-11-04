using System.IO;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

/// <summary>
/// Removes timeline media caches nobody has used for a while. Opening a recording touches its
/// thumbnail folder and audio-levels file, so their last-write time is their last use.
/// Formats no current version reads are removed at once. Only entries whose names match
/// AdTrim's own cache naming, directly inside the given roots, are ever considered.
/// </summary>
public static class MediaCacheCleanup
{
    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(90);

    private static readonly Regex ThumbnailFolder = new(@"^[0-9a-f]{16}_d\d+_", RegexOptions.CultureInvariant);
    private static readonly Regex LevelsFile = new(@"^[0-9a-f]{16}_a\d+_d\d+_v(?<v>\d+)\.adtlevels$", RegexOptions.CultureInvariant);
    private static readonly Regex WaveformFile = new(@"^[0-9a-f]{16}_a\d+_d\d+_.*\.adtwave$", RegexOptions.CultureInvariant);

    public static string LegacyWaveformRoot => Path.Combine(AppDataDirectory.Path, "waveforms");

    /// <summary>Marks a cache entry as used now. Failures only mean it may be cleaned up sooner.</summary>
    public static void Touch(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            else if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Returns how many cache entries were removed. <paramref name="currentThumbnails"/> is
    /// the folder-name suffix of the thumbnail format in use; older formats go at once.</summary>
    public static int Run(string thumbnailsRoot, string currentThumbnails, string audioLevelsRoot, int currentLevelsVersion,
        string legacyWaveformRoot, DateTime nowUtc)
    {
        int removed = 0;
        var cutoff = nowUtc - UnusedFor;
        foreach (var dir in Entries(thumbnailsRoot, directories: true))
        {
            var name = Path.GetFileName(dir);
            if (!ThumbnailFolder.IsMatch(name)) continue;
            if (!name.EndsWith(currentThumbnails, StringComparison.Ordinal) || Directory.GetLastWriteTimeUtc(dir) < cutoff)
                removed += Try(() => Directory.Delete(dir, recursive: true));
        }
        foreach (var file in Entries(audioLevelsRoot, directories: false))
        {
            var match = LevelsFile.Match(Path.GetFileName(file));
            if (!match.Success) continue;
            if (match.Groups["v"].Value != currentLevelsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || File.GetLastWriteTimeUtc(file) < cutoff)
                removed += Try(() => File.Delete(file));
        }
        // The pre-2.0 waveform cache: nothing reads it any more.
        foreach (var file in Entries(legacyWaveformRoot, directories: false))
            if (WaveformFile.IsMatch(Path.GetFileName(file))) removed += Try(() => File.Delete(file));
        if (Directory.Exists(legacyWaveformRoot) && !Directory.EnumerateFileSystemEntries(legacyWaveformRoot).Any())
            Try(() => Directory.Delete(legacyWaveformRoot));
        return removed;
    }

    private static IEnumerable<string> Entries(string root, bool directories)
    {
        if (!Directory.Exists(root)) return [];
        try { return (directories ? Directory.GetDirectories(root) : Directory.GetFiles(root)).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static int Try(Action delete)
    {
        try { delete(); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
