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
    UnsupportedVersion,
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
    private static readonly object RollbackManifestGate = new();
    private readonly string _fallbackDirectory;
    private readonly string _rollbackDirectory;
    public ProjectStore(string? fallbackDirectory = null, string? dataDirectory = null)
    {
        dataDirectory ??= fallbackDirectory is not null
            ? Path.GetDirectoryName(Path.GetFullPath(fallbackDirectory))!
            : Environment.GetEnvironmentVariable("ADTRIM_DATA_DIR") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdTrim");
        _fallbackDirectory = fallbackDirectory ?? Path.Combine(dataDirectory, "projects");
        _rollbackDirectory = Path.Combine(dataDirectory, "rollback", "projects");
    }
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
        var dir = _fallbackDirectory;
        var hash = FingerprintHash(sourcePath);
        return Path.Combine(dir, hash + ".adt.json");
    }

    public IEnumerable<string> CandidateSidecarPathsFor(string sourcePath)
    {
        yield return SidecarPathFor(sourcePath);
        yield return AppDataFallbackPathFor(sourcePath);
    }

    public bool HasExportRecord(string sourcePath)
    {
        var source = new FileInfo(sourcePath);
        Exception? failure = null;
        foreach (var path in CandidateSidecarPathsFor(sourcePath).Where(File.Exists))
        {
            try
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("The project is too large to check safely.");
                var project = JsonSerializer.Deserialize<AdTrimProject>(File.ReadAllBytes(path), JsonOpts);
                if (project is null || project.SchemaVersion > AdTrimProject.CurrentSchemaVersion || Validate(project) is not null)
                    throw new InvalidDataException("The project export record could not be verified.");
                if (project.LastExport is { } export && export.SourceFingerprint == project.Fingerprint
                    && export.SourceFingerprint.SizeBytes == source.Length
                    && export.SourceFingerprint.ModifiedUnixMs == new DateTimeOffset(source.LastWriteTimeUtc).ToUnixTimeMilliseconds())
                    return true;
            }
            // InvalidDataException is not an IOException; the fallback save may still hold the record.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { failure ??= ex; }
        }
        if (failure is not null) throw new InvalidDataException("A project sidecar's export status could not be checked. " + failure.Message, failure);
        return false;
    }

    /// <summary>
    /// Project files for this recording that this version can read and that describe the recording as
    /// it is now. Unreadable, newer and mismatched projects may be someone's only copy and are not listed.
    /// </summary>
    public IReadOnlyList<string> DeletableSidecarsFor(string sourcePath, SourceFingerprint current)
    {
        var result = new List<string>();
        foreach (var path in CandidateSidecarPathsFor(sourcePath).Where(File.Exists))
        {
            try
            {
                var project = JsonSerializer.Deserialize<AdTrimProject>(File.ReadAllBytes(path), JsonOpts);
                if (project is not null && project.SchemaVersion <= AdTrimProject.CurrentSchemaVersion && Validate(project) is null
                    && project.Fingerprint.SizeBytes == current.SizeBytes && project.Fingerprint.DurationUs == current.DurationUs)
                    result.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }

    internal Dictionary<string, (byte[] Before, byte[] After)> FingerprintUpdates(string sourcePath, SourceFingerprint before, SourceFingerprint after)
    {
        var result = new Dictionary<string, (byte[], byte[])>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidateSidecarPathsFor(sourcePath).Where(File.Exists))
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("The project is too large to update safely.");
            var bytes = File.ReadAllBytes(path);
            var project = JsonSerializer.Deserialize<AdTrimProject>(bytes, JsonOpts);
            if (project is null || project.SchemaVersion > AdTrimProject.CurrentSchemaVersion || Validate(project) is not null
                || project.Fingerprint.SizeBytes != before.SizeBytes || project.Fingerprint.DurationUs != before.DurationUs)
                throw new IOException("An existing project cannot be safely updated for the source comment.");
            var json = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
            var fingerprint = json["fingerprint"]!;
            fingerprint["size_bytes"] = after.SizeBytes;
            fingerprint["modified_unix_ms"] = after.ModifiedUnixMs;
            result[path] = (bytes, Encoding.UTF8.GetBytes(json.ToJsonString(JsonOpts)));
        }
        return result;
    }

    /// <summary>
    /// Load + validate against the source file's current size+duration.
    /// Caller supplies the freshly-probed MediaInfo so we don't probe twice.
    /// </summary>
    public SidecarLoadResult Load(string sourcePath, MediaInfo current)
    {
        var candidates = CandidateSidecarPathsFor(sourcePath).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        if (candidates.Length == 0) return new(SidecarLoadStatus.Missing, null, null);
        SidecarLoadResult? failure = null;
        foreach (var path in candidates)
        {
            var result = LoadCandidate(path, sourcePath, current);
            if (result.Project is not null)
                return path == SidecarPathFor(sourcePath) ? result
                    : result with { Message = "Recovered project from the fallback save. " + result.Message };
            failure ??= result;
        }
        return failure!;
    }

    private static SidecarLoadResult LoadCandidate(string path, string sourcePath, MediaInfo current)
    {
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

        if (proj.SchemaVersion > AdTrimProject.CurrentSchemaVersion)
            return new(SidecarLoadStatus.UnsupportedVersion, null, "This project was saved by a newer AdTrim version.");
        var error = Validate(proj);
        if (error is not null) return new(SidecarLoadStatus.Corrupt, null, error);

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

    internal static string? Validate(AdTrimProject project)
    {
        if (project.SchemaVersion < 1 || project.Fingerprint is null || project.Media is null
            || project.Splits is null || project.ExcludedSegmentIds is null || string.IsNullOrWhiteSpace(project.SourcePath))
            return "Project is missing required data.";
        if (!Enum.IsDefined(project.MarkerSetSource)) return "Invalid marker source.";
        if (project.LastExport is { } export && (export.Application != "AdTrim"
            || !Version.TryParse(export.Version, out _) || export.ExportedAtUtc <= DateTimeOffset.UnixEpoch
            || export.SourceFingerprint is null || export.SourceFingerprint.SizeBytes < 0 || export.SourceFingerprint.DurationUs <= 0))
            return "Invalid export record.";
        long duration = project.Fingerprint.DurationUs;
        if (duration <= 0 || project.Media.DurationUs != duration || project.Fingerprint.SizeBytes < 0)
            return "Invalid source metadata.";
        var ids = new HashSet<string>();
        long previous = 0;
        var segments = new HashSet<string>();
        foreach (var split in project.Splits)
        {
            if (split is null || string.IsNullOrWhiteSpace(split.Id) || !ids.Add(split.Id)
                || split.TimeUs <= previous || split.TimeUs >= duration || !Enum.IsDefined(split.Source)
                || (split.Confidence is { } confidence && !Enum.IsDefined(confidence)))
                return "Invalid or unordered split boundaries.";
            segments.Add($"segment-{previous}-{split.TimeUs}");
            previous = split.TimeUs;
        }
        segments.Add($"segment-{previous}-{duration}");
        return project.ExcludedSegmentIds.Any(id => !segments.Contains(id)) ? "Unknown excluded segment." : null;
    }

    /// <summary>Atomic save: write to .tmp, fsync, rename. Falls back to AppData if next-to-source fails.</summary>
    public string Save(AdTrimProject project)
    {
        var error = Validate(project);
        if (error is not null) throw new InvalidDataException(error);
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
        Directory.CreateDirectory(_fallbackDirectory);
        WriteAtomic(fallback, project with { SidecarLocation = SidecarLocation.AppdataFallback });
        return fallback;
    }

    private void WriteAtomic(string path, AdTrimProject project)
    {
        EnsureReadableAndBackedUp(path, project);
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, project, JsonOpts);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        finally
        {
            // A failed replace can move the original away; then the temp file is the only copy on disk.
            if (File.Exists(tmp) && File.Exists(path)) File.Delete(tmp);
        }
    }

    private void EnsureReadableAndBackedUp(string path, AdTrimProject replacement)
    {
        if (!File.Exists(path)) return;
        AdTrimProject? old;
        try { old = JsonSerializer.Deserialize<AdTrimProject>(File.ReadAllText(path), JsonOpts); }
        catch (JsonException ex) { throw new IOException("Existing project cannot be safely overwritten: " + path, ex); }
        if (old is null || Validate(old) is not null)
            throw new IOException("Existing project cannot be safely overwritten: " + path);
        if (old.SchemaVersion > AdTrimProject.CurrentSchemaVersion)
            throw new IOException("This project was saved by a newer AdTrim version and cannot be overwritten.");
        // A project for a different size or duration was not loaded, so its markers are not in the
        // replacement. Keep a copy: they may still be valid for a retagged or restored recording.
        bool sameSource = old.Fingerprint.SizeBytes == replacement.Fingerprint.SizeBytes
            && old.Fingerprint.DurationUs == replacement.Fingerprint.DurationUs;
        if (old.SchemaVersion >= replacement.SchemaVersion && sameSource) return;

        Directory.CreateDirectory(_rollbackDirectory);
        var originalBytes = File.ReadAllBytes(path);
        var contentHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        var backup = Path.Combine(_rollbackDirectory,
            $"{FingerprintHash(replacement.SourcePath)}-schema{old.SchemaVersion}-{contentHash}.adt.json");
        if (!File.Exists(backup))
        {
            var tempBackup = backup + ".tmp";
            try
            {
                using (var fs = new FileStream(tempBackup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { fs.Write(originalBytes); fs.Flush(flushToDisk: true); }
                File.Move(tempBackup, backup);
            }
            finally { if (File.Exists(tempBackup)) File.Delete(tempBackup); }
        }
        if (!SHA256.HashData(File.ReadAllBytes(backup)).SequenceEqual(SHA256.HashData(originalBytes)))
            throw new IOException("The rollback backup could not be verified. Existing project was preserved.");
        RecordRollbackBackup(replacement.SourcePath, path, backup, old.SchemaVersion);
    }

    private void RecordRollbackBackup(string sourcePath, string originalPath, string backupPath, int schemaVersion)
    {
        lock (RollbackManifestGate)
        {
            var manifestPath = Path.Combine(_rollbackDirectory, "manifest.json");
            var records = new List<RollbackRecord>();
            if (File.Exists(manifestPath))
            {
                try { records = JsonSerializer.Deserialize<List<RollbackRecord>>(File.ReadAllText(manifestPath)) ?? new(); }
                catch (JsonException)
                {
                    // The list is only an index of the backups beside it. A damaged one (power loss
                    // mid-write) is set aside so it cannot block every later save.
                    File.Move(manifestPath, Path.Combine(_rollbackDirectory, $"manifest.damaged-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"));
                }
            }
            if (records.Any(r => StringComparer.OrdinalIgnoreCase.Equals(r.BackupPath, backupPath))) return;
            records.Add(new RollbackRecord(Path.GetFullPath(sourcePath), Path.GetFullPath(originalPath),
                Path.GetFullPath(backupPath), schemaVersion, DateTimeOffset.UtcNow));
            var temp = manifestPath + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(manifestPath)) File.Replace(temp, manifestPath, null);
                else File.Move(temp, manifestPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private sealed record RollbackRecord(string SourcePath, string OriginalPath, string BackupPath,
        int SchemaVersion, DateTimeOffset SavedUtc);

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
