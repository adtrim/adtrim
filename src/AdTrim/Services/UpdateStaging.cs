using System.IO;

namespace AdTrim.Services;

public static class UpdateStaging
{
    /// <summary>Suffix of an installer still being downloaded or checked.</summary>
    public const string PartialSuffix = ".partial";
    public static string Root(string dataDirectory) => Path.GetFullPath(Path.Combine(dataDirectory, "updates"));

    public static string CreateDirectory(string dataDirectory)
    {
        var root = Root(dataDirectory);
        Directory.CreateDirectory(root);
        RejectLink(new DirectoryInfo(root));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string ValidateDirectory(string dataDirectory, string directory)
    {
        var root = Root(dataDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new IOException("The update staging path is invalid.");
        RejectLink(new DirectoryInfo(root));
        RejectLink(new DirectoryInfo(full));
        return full;
    }

    public static void RemoveDirectory(string dataDirectory, string directory)
    {
        var full = ValidateDirectory(dataDirectory, directory);
        if (!Directory.Exists(full)) return;
        var remaining = new Stack<DirectoryInfo>();
        remaining.Push(new(full));
        while (remaining.TryPop(out var folder))
            foreach (var item in folder.EnumerateFileSystemInfos())
            {
                RejectLink(item);
                if (item is DirectoryInfo child) remaining.Push(child);
            }
        Directory.Delete(full, true);
    }

    public static async Task RemoveDirectoryAsync(string dataDirectory, string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { RemoveDirectory(dataDirectory, directory); return; }
            catch (Exception ex) when (attempt < 9 && ex is IOException or UnauthorizedAccessException)
            { await Task.Delay(200); }
        }
    }

    /// <summary>
    /// Removes every update folder no one is using: finished or partial downloads from closed update
    /// windows, and jobs left by an interruption. Call only while no update is installing. Folders
    /// created after <paramref name="createdBeforeUtc"/> belong to this session and are kept, as is
    /// <paramref name="keep"/>. Returns the folders that could not be removed yet.
    /// </summary>
    public static int CleanUpAbandoned(string dataDirectory, DateTime createdBeforeUtc, string? keep = null)
    {
        var root = Root(dataDirectory);
        if (!Directory.Exists(root)) return 0;
        RejectLink(new DirectoryInfo(root));
        string? kept = keep is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(keep));
        int remaining = 0;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.CreationTimeUtc >= createdBeforeUtc
                || string.Equals(directory.FullName, kept, StringComparison.OrdinalIgnoreCase)) continue;
            // A backup left by an update that could not restore it stays for manual recovery for 30 days.
            if (Directory.Exists(Path.Combine(directory.FullName, "backup")) && directory.CreationTimeUtc > createdBeforeUtc.AddDays(-30)) continue;
            try { RemoveDirectory(dataDirectory, directory.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { remaining++; }
        }
        return remaining;
    }

    public const string AcknowledgedName = "interruption-acknowledged";
    public sealed record InterruptedInstall(string Directory, string Version);

    /// <summary>
    /// An update to a newer version that backed up the installation and never recorded finishing:
    /// the update window was closed or ended while files were being replaced, or could not put the
    /// previous version back. Call only while no update is installing.
    /// </summary>
    public static InterruptedInstall? FindInterruptedInstall(string dataDirectory, string currentVersion, string? keep = null)
    {
        var root = Root(dataDirectory);
        if (!Directory.Exists(root)) return null;
        string? kept = keep is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(keep));
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            try
            {
                if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || string.Equals(directory.FullName, kept, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(Path.Combine(directory.FullName, "backup"))
                    || File.Exists(Path.Combine(directory.FullName, "completed.json"))
                    || File.Exists(Path.Combine(directory.FullName, AcknowledgedName))) continue;
                var job = new FileInfo(Path.Combine(directory.FullName, "job.json"));
                if (job.Length > 16 * 1024) continue;
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(job.FullName));
                if (json.RootElement.GetProperty("Version").GetString() is not { } version) continue;
                // Running the version it was installing means the installer did finish.
                if (UpdateFeedReader.ParseVersion(version) > UpdateFeedReader.ParseVersion(currentVersion))
                    return new(directory.FullName, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or InvalidDataException or FormatException or ArgumentException or KeyNotFoundException or InvalidOperationException) { }
        }
        return null;
    }

    public static void AcknowledgeInterruptedInstall(string dataDirectory, string directory)
        => File.WriteAllText(Path.Combine(ValidateDirectory(dataDirectory, directory), AcknowledgedName), "");

    private static void RejectLink(FileSystemInfo item)
    {
        if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The update staging path contains a link.");
    }
}
