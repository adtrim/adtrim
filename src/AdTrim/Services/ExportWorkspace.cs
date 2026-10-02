using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdTrim.Services;

public sealed class ExportWorkspace : IDisposable
{
    private sealed record Record(int Version, string Id, string TempDirectory, string StagingPath);
    private readonly string _recordDirectory;
    private readonly Record _record;
    private readonly FileStream _lease;
    private readonly string _tempRoot;
    private static string DefaultTempRoot => Path.Combine(Path.GetTempPath(), "AdTrim");
    public string TempDirectory => _record.TempDirectory;
    public string StagingPath => _record.StagingPath;
    private static string Registry => Path.Combine(UserPreferences.DataDirectory, "export-cleanup");

    private ExportWorkspace(string directory, Record record, FileStream lease, string tempRoot)
        => (_recordDirectory, _record, _lease, _tempRoot) = (directory, record, lease, Path.GetFullPath(tempRoot));

    public static ExportWorkspace Create(string destination, string? registry = null, string? tempRoot = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(registry ?? Registry, id);
        var record = new Record(1, id,
            Path.GetFullPath(Path.Combine(tempRoot ?? DefaultTempRoot, "exp-" + id)),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".adtrim-" + id + ".mp4"));
        Directory.CreateDirectory(directory);
        var lease = new FileStream(Path.Combine(directory, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using (var manifest = new FileStream(Path.Combine(directory, "record.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(manifest, record);
                manifest.Flush(true);
            }
            Directory.CreateDirectory(record.TempDirectory);
            return new ExportWorkspace(directory, record, lease, tempRoot ?? DefaultTempRoot);
        }
        catch { lease.Dispose(); throw; }
    }

    public async Task<bool> CleanupAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (DeleteOwnedFiles(_record, _tempRoot))
                {
                    File.Delete(Path.Combine(_recordDirectory, "record.json"));
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (attempt < 2) await Task.Delay(250 * (attempt + 1)).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        _lease.Dispose();
        if (File.Exists(Path.Combine(_recordDirectory, "record.json"))) return;
        try
        {
            File.Delete(Path.Combine(_recordDirectory, "lease"));
            Directory.Delete(_recordDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<int> RecoverAsync(string? registry = null, CancellationToken ct = default, string? tempRoot = null)
    {
        var root = registry ?? Registry;
        if (!Directory.Exists(root)) return 0;
        int pending = 0;
        // Rotate attempted entries so persistent failures cannot starve newer records.
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(Directory.GetLastWriteTimeUtc).Take(100))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || HasReparsePoint(directory)) continue;
            try { Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { pending++; continue; }
            var path = Path.Combine(directory, "record.json");
            if (!File.Exists(path)) continue;
            FileStream lease;
            try { lease = new FileStream(Path.Combine(directory, "lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; } // Another export or cleanup owns it.
            catch (UnauthorizedAccessException) { pending++; continue; }
            using (lease)
            {
                try
                {
                    if (new FileInfo(path).Length > 16_384 || HasReparsePoint(path)) { pending++; continue; }
                    var record = JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                    if (record is null || record.Id != Path.GetFileName(directory) || !Valid(record, tempRoot ?? DefaultTempRoot)) { pending++; continue; }
                    using var workspace = new ExportWorkspace(directory, record, lease, tempRoot ?? DefaultTempRoot);
                    if (!await workspace.CleanupAsync().ConfigureAwait(false)) pending++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { pending++; }
            }
        }
        return pending;
    }

    private static bool Valid(Record record, string tempRoot)
        => record.Version == 1 && Guid.TryParseExact(record.Id, "N", out _)
            && Path.IsPathFullyQualified(record.TempDirectory) && Path.IsPathFullyQualified(record.StagingPath)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(record.TempDirectory)), Path.GetFullPath(tempRoot), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(record.TempDirectory) == "exp-" + record.Id
            && Path.GetFileName(record.StagingPath) == ".adtrim-" + record.Id + ".mp4";

    private static bool DeleteOwnedFiles(Record record, string tempRoot)
    {
        if (!Valid(record, tempRoot) || HasReparsePoint(record.TempDirectory) || HasReparsePoint(record.StagingPath)) return false;
        // Only recognized direct children are removed. Never follow links or recurse.
        File.Delete(record.StagingPath);
        if (Directory.Exists(record.TempDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(record.TempDirectory))
            {
                var name = Path.GetFileName(file);
                if (HasReparsePoint(file) || !(name is "concat.txt" or "chapters.ffmetadata"
                    || Regex.IsMatch(name, @"^seg_[0-9]+\.mp4$"))) continue;
                File.Delete(file);
            }
            if (Directory.EnumerateFileSystemEntries(record.TempDirectory).Any()) return false;
            Directory.Delete(record.TempDirectory);
        }
        return true;
    }

    private static bool HasReparsePoint(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}
