using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdTrim.Models;

namespace AdTrim.Services;

public enum SidecarLoadStatus
{
    Loaded,
    LoadedWithMtimeWarning,
    FingerprintMismatch,
    Missing,
    Corrupt,
}

public sealed record SidecarLoadResult(SidecarLoadStatus Status, AdTrimProject? Project, string? Message);

/// <summary>
/// Sidecar reader/writer. Atomic write (tmp + rename). Identity = exact
/// match on (sizeBytes, durationUs); mtime drift is a soft warning, not a
/// reject. Falls back to %LOCALAPPDATA%\AdTrim\projects\{hash}.adt.json
/// when the next-to-source location is unwritable.
/// </summary>
public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string SidecarPathFor(string sourcePath) => sourcePath + ".adt.json";

    public string AppDataFallbackPathFor(string sourcePath)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdTrim", "projects");
        Directory.CreateDirectory(dir);
        var hash = FingerprintHash(sourcePath);
        return Path.Combine(dir, hash + ".adt.json");
    }

    public IEnumerable<string> CandidateSidecarPathsFor(string sourcePath)
    {
        yield return SidecarPathFor(sourcePath);
        yield return AppDataFallbackPathFor(sourcePath);
    }

    /// <summary>
    /// Load + validate against the source file's current size+duration.
    /// Caller supplies the freshly-probed MediaInfo so we don't probe twice.
    /// </summary>
    public SidecarLoadResult Load(string sourcePath, MediaInfo current)
    {
        var path = SidecarPathFor(sourcePath);
        if (!File.Exists(path))
        {
            path = AppDataFallbackPathFor(sourcePath);
            if (!File.Exists(path))
                return new SidecarLoadResult(SidecarLoadStatus.Missing, null, null);
        }

        AdTrimProject? proj;
        try
        {
            using var stream = File.OpenRead(path);
            proj = JsonSerializer.Deserialize<AdTrimProject>(stream, JsonOpts);
        }
        catch (Exception ex)
        {
            return new SidecarLoadResult(SidecarLoadStatus.Corrupt, null, ex.Message);
        }
        if (proj is null)
            return new SidecarLoadResult(SidecarLoadStatus.Corrupt, null, "Sidecar deserialized to null.");

        // Identity check: size + duration must match exactly.
        var fileInfo = new FileInfo(sourcePath);
        var sizeOk = proj.Fingerprint.SizeBytes == fileInfo.Length;
        var durationOk = proj.Fingerprint.DurationUs == current.DurationUs;
        if (!sizeOk || !durationOk)
        {
            return new SidecarLoadResult(
                SidecarLoadStatus.FingerprintMismatch, null,
                $"Sidecar fingerprint mismatch (size: {sizeOk}, duration: {durationOk}).");
        }

        // mtime drift is a soft warning.
        var nowMtime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        if (proj.Fingerprint.ModifiedUnixMs != nowMtime)
        {
            return new SidecarLoadResult(
                SidecarLoadStatus.LoadedWithMtimeWarning, proj,
                "Source modified time changed - verify your project state.");
        }
        return new SidecarLoadResult(SidecarLoadStatus.Loaded, proj, null);
    }

    /// <summary>Atomic save: write to .tmp, fsync, rename. Falls back to AppData if next-to-source fails.</summary>
    public string Save(AdTrimProject project)
    {
        // Try next-to-source first.
        var primary = SidecarPathFor(project.SourcePath);
        try
        {
            WriteAtomic(primary, project);
            return primary;
        }
        catch (UnauthorizedAccessException) { /* fall through */ }
        catch (IOException) { /* fall through (read-only NAS, etc.) */ }

        var fallback = AppDataFallbackPathFor(project.SourcePath);
        WriteAtomic(fallback, project with { SidecarLocation = SidecarLocation.AppdataFallback });
        return fallback;
    }

    private static void WriteAtomic(string path, AdTrimProject project)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, project, JsonOpts);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    private static string FingerprintHash(string sourcePath)
    {
        // 16-char hex hash of absolute path - stable per-path, no source-content read.
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath)));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    public static SourceFingerprint FingerprintOf(string sourcePath, long durationUs)
    {
        var fi = new FileInfo(sourcePath);
        return new SourceFingerprint(
            SizeBytes: fi.Length,
            ModifiedUnixMs: new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            DurationUs: durationUs);
    }
}
