using System.IO;

namespace AdTrim.Services;

/// <summary>
/// Backs up and restores the files an AdTrim installation owns, so a failed or cancelled update can
/// put the previous version back. Only files named in the installation's own install-files.txt
/// (written by Setup) are touched. Older installations share their folder with user data, so the
/// folder itself is never copied, emptied or deleted.
/// </summary>
public static class UpdateBackup
{
    public const string ListName = "install-files.txt";
    private const string BackupListName = "backed-up-files.txt";
    // Setup writes these beside the listed payload; they belong to the installation too.
    private static readonly string[] SetupFiles = ["Uninstall.exe", ListName];

    /// <summary>The installation's own files, relative to its folder. Throws when Setup did not record them.</summary>
    public static IReadOnlyList<string> InstalledFiles(string installDirectory)
    {
        var list = Path.Combine(installDirectory, ListName);
        if (!File.Exists(list))
            throw new IOException("This installation does not list its own files, so it cannot be backed up. Install the update manually from See what's new.");
        if (new FileInfo(list).Length > 1024 * 1024) throw new IOException("The installation's file list is too large.");
        return Parse(File.ReadAllLines(list)).Concat(SetupFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Validates relative paths: no roots, drive letters, parent segments, streams or empty names.</summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var segments = line.Split('\\');
            if (Path.IsPathRooted(line) || line.Contains(':') || line.Contains('/')
                || segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("The installation's file list contains an invalid path.");
            result.Add(line);
        }
        if (result.Count == 0 || result.Count > 10_000) throw new InvalidDataException("The installation's file list is empty or too long.");
        return result;
    }

    /// <summary>Copies the installation's existing files into <paramref name="backupDirectory"/> and records which ones existed.</summary>
    public static void Create(string installDirectory, string backupDirectory)
    {
        var files = InstalledFiles(installDirectory);
        if (Directory.Exists(backupDirectory)) throw new IOException("A previous backup is already present.");
        // Built under another name and renamed when complete, so a backup folder that exists is whole.
        string complete = backupDirectory;
        backupDirectory += ".partial";
        if (Directory.Exists(backupDirectory))
        {
            if (File.GetAttributes(backupDirectory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("An unfinished backup is a link and was not changed: " + backupDirectory);
            Directory.Delete(backupDirectory, true);
        }
        Directory.CreateDirectory(backupDirectory);
        var copied = new List<string>();
        foreach (var relative in files)
        {
            var source = Path.Combine(installDirectory, relative);
            if (!File.Exists(source)) continue;
            RejectLink(source);
            var target = Path.Combine(backupDirectory, "files", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            if (new FileInfo(target).Length != new FileInfo(source).Length) throw new IOException("A backup copy is incomplete: " + relative);
            copied.Add(relative);
        }
        File.WriteAllLines(Path.Combine(backupDirectory, BackupListName), copied);
        Directory.Move(backupDirectory, complete);
    }

    /// <summary>
    /// Puts the backed-up files back. Files that the update added (listed by the new installation but not
    /// present before) are removed. Nothing outside either list is touched.
    /// </summary>
    public static void Restore(string installDirectory, string backupDirectory)
    {
        var backedUp = Parse(File.ReadAllLines(Path.Combine(backupDirectory, BackupListName)));
        var before = new HashSet<string>(backedUp, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> added = [];
        try { added = InstalledFiles(installDirectory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { /* the update did not get far enough to list its files */ }
        foreach (var relative in added.Where(p => !before.Contains(p)))
        {
            var path = Path.Combine(installDirectory, relative);
            if (!File.Exists(path)) continue;
            RejectLink(path);
            File.Delete(path);
        }
        foreach (var relative in backedUp)
        {
            var source = Path.Combine(backupDirectory, "files", relative);
            var target = Path.Combine(installDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) RejectLink(target);
            File.Copy(source, target, overwrite: true);
            if (new FileInfo(target).Length != new FileInfo(source).Length) throw new IOException("A restored file is incomplete: " + relative);
        }
    }

    private static void RejectLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("An installation file is a link and was not changed: " + path);
    }
}
