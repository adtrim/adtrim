using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Source-comment progress: the step under way and the fraction of the whole operation done.
/// Restoring is set while a failed or cancelled update puts the original back.</summary>
public readonly record struct SourceCommentProgress(bool Restoring, string Step, double Fraction);

/// <summary>Recoverable source/comment and project fingerprint transaction.</summary>
public static class SourceCommentService
{
    private sealed record SavedProject(string Path, byte[] Before, byte[] After);
    private sealed record Transaction(string BeforeHash, string AfterHash, string StageName, List<SavedProject> Projects, bool Committed = false);
    private static string Journal(string source) => source + ".adtrim-export-comment.json";
    private static string Backup(string source) => source + ".adtrim-export-comment.bak";
    public static bool HasPendingUpdate(string source) => File.Exists(Journal(source));

    // Progress step names, in order. Restoring replaces them if a failure or cancellation needs a rollback.
    public const string CopyingStep = "Writing a copy with the new comment";
    public const string CheckingStep = "Checking the copy";
    public const string RecheckingStep = "Rechecking the original";
    public const string VerifyingStep = "Verifying the updated file";
    public const string RestoringStep = "Restoring the original file";

    /// <summary>
    /// Marks the source with the export comment. On a change, the source-sized passes are: one copy
    /// (read and write) that also hashes the source, one read-back of the copy, one recheck of the
    /// original just before replacing it, and one verification of the replaced file. Progress stays
    /// below 100% until the replacement is verified and committed.
    /// </summary>
    public static async Task<SourceFingerprint> MarkAsync(string source, SourceFingerprint expected, ProjectStore store, CancellationToken ct,
        IProgress<SourceCommentProgress>? progress = null)
    {
        source = Path.GetFullPath(source);
        Recover(source, store);
        ExportSafety.EnsureMetadataWritable(source);
        var before = ProjectStore.FingerprintOf(source, expected.DurationUs);
        if (before != expected) throw new IOException("The source changed before its comment could be added.");
        if (File.Exists(Backup(source))) throw new IOException("A source comment backup already exists; it was preserved.");
        var date = DateOnly.FromDateTime(DateTime.Now);
        var passes = new PassProgress(before.SizeBytes * 5, progress);
        // The same label for the same version and date would rewrite the file unchanged.
        if (!Mp4Comments.NeedsUpdate(source, AppVersion.Numeric, date))
        {
            passes.Complete();
            return before;
        }
        // Both are known before the copy, which takes minutes and fills the recordings disk.
        store.FingerprintUpdates(source, before, before);
        EnsureRoomForCopy(source, before.SizeBytes, AvailableBytes);
        string stage = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(source) + ".adtrim-comment-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            passes.Step(CopyingStep);
            var staged = await Mp4Comments.StageAsync(source, stage, AppVersion.Numeric, date, ct, passes.Add, () => passes.Step(CheckingStep));
            string beforeHash = staged.SourceHash, afterHash = staged.StagedHash;
            if (afterHash == beforeHash)
            {
                passes.Complete();
                return before;
            }
            var after = ProjectStore.FingerprintOf(stage, before.DurationUs);
            var updates = store.FingerprintUpdates(source, before, after);
            var projects = updates.Select(p => new SavedProject(p.Key, p.Value.Before, p.Value.After)).ToList();
            var transaction = new Transaction(beforeHash, afterHash, Path.GetFileName(stage), projects);
            ct.ThrowIfCancellationRequested();
            // An independent read: catches a change since staging and a faulty read during the copy.
            passes.Step(RecheckingStep);
            if (await HashAsync(source, ct, passes.Add) != beforeHash || ProjectStore.FingerprintOf(source, before.DurationUs) != before)
                throw new IOException("The source changed during comment preparation.");
            CheckProjects(transaction.Projects);
            var record = JsonSerializer.SerializeToUtf8Bytes(transaction);
            if (record.Length > 48 * 1024 * 1024) throw new IOException("The project recovery record would be too large; the source was not changed.");
            WriteAtomic(Journal(source), record);
            ct.ThrowIfCancellationRequested();
            File.Replace(stage, source, Backup(source));
            passes.Step(VerifyingStep);
            if (await HashAsync(source, ct, passes.Add) != transaction.AfterHash) throw new IOException("The updated source could not be verified.");
            foreach (var project in projects)
            {
                ct.ThrowIfCancellationRequested();
                WriteAtomic(project.Path, project.After);
            }
            var actual = ProjectStore.FingerprintOf(source, before.DurationUs);
            if (actual != after) throw new IOException("The updated source fingerprint could not be verified.");
            WriteAtomic(Journal(source), JsonSerializer.SerializeToUtf8Bytes(transaction with { Committed = true }));
            Finish(source, transaction, after);
            passes.Complete();
            return actual;
        }
        catch
        {
            if (HasPendingUpdate(source))
            {
                // Up to three source-sized reads: the current file, the backup and the restored file.
                var restoring = new PassProgress(before.SizeBytes * 3, progress, restoring: true);
                restoring.Step(RestoringStep);
                Recover(source, store, restoring.Add);
            }
            throw;
        }
        finally
        {
            if (!HasPendingUpdate(source) && File.Exists(stage)) File.Delete(stage);
        }
    }

    // The replaced file was hashed against the journal moments ago; its fingerprint confirms it is
    // still that file before the backup of the original is deleted.
    private static void Finish(string source, Transaction transaction, SourceFingerprint after)
    {
        if (ProjectStore.FingerprintOf(source, after.DurationUs) != after
            || transaction.Projects.Any(p => !File.ReadAllBytes(p.Path).SequenceEqual(p.After)))
            throw new IOException("Completed source comment recovery could not be verified.");
        if (File.Exists(Backup(source))) File.Delete(Backup(source));
        string stage = Path.Combine(Path.GetDirectoryName(source)!, transaction.StageName);
        if (File.Exists(stage)) File.Delete(stage);
        File.Delete(Journal(source));
    }

    public static void Recover(string source, ProjectStore store, Action<long>? advance = null)
    {
        source = Path.GetFullPath(source);
        string journal = Journal(source);
        if (!File.Exists(journal)) { RemoveAbandonedCopies(source); return; }
        if (new FileInfo(journal).Length > 48 * 1024 * 1024) throw new IOException("Source comment recovery record is too large.");
        var transaction = JsonSerializer.Deserialize<Transaction>(File.ReadAllBytes(journal))
            ?? throw new IOException("Source comment recovery record is unreadable.");
        var allowed = store.CandidateSidecarPathsFor(source).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string prefix = Path.GetFileName(source) + ".adtrim-comment-";
        if (transaction.Projects is null || transaction.Projects.Count > 2
            || transaction.Projects.Any(p => !allowed.Contains(Path.GetFullPath(p.Path)))
            || Path.GetFileName(transaction.StageName) != transaction.StageName
            || !transaction.StageName.StartsWith(prefix, StringComparison.Ordinal)
            || !transaction.StageName.EndsWith(".tmp", StringComparison.Ordinal)
            || !Guid.TryParseExact(transaction.StageName[prefix.Length..^4], "N", out _))
            throw new IOException("Source comment recovery paths could not be verified.");
        string hash = File.Exists(source) ? Hash(source, advance) : "";
        if (hash != transaction.BeforeHash && hash != transaction.AfterHash && hash != "")
            throw new IOException("The recording changed after an interrupted comment update. Its recovery files were preserved.");
        CheckProjects(transaction.Projects);
        if (!transaction.Committed)
        {
            if (File.Exists(Backup(source)))
            {
                if (Hash(Backup(source), advance) != transaction.BeforeHash) throw new IOException("Source comment rollback backup failed verification.");
                if (File.Exists(source)) File.Replace(Backup(source), source, null);
                else File.Move(Backup(source), source);
            }
            if (!File.Exists(source) || Hash(source, advance) != transaction.BeforeHash)
                throw new IOException("The original recording could not be recovered. Recovery files were preserved.");
            foreach (var project in transaction.Projects) WriteAtomic(project.Path, project.Before);
        }
        else if (hash != transaction.AfterHash || transaction.Projects.Any(p => !File.ReadAllBytes(p.Path).SequenceEqual(p.After)))
            throw new IOException("Completed source comment recovery could not be verified.");
        if (File.Exists(Backup(source))) File.Delete(Backup(source));
        string stage = Path.Combine(Path.GetDirectoryName(source)!, transaction.StageName);
        if (File.Exists(stage)) File.Delete(stage);
        File.Delete(journal);
    }

    /// <summary>Free bytes on the volume holding <paramref name="directory"/>, or null when it cannot be read.</summary>
    private static long? AvailableBytes(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        // Network shares have no drive information; the copy itself still fails safely when full.
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    // The copy sits beside the recording. A DVR writing to the same disk must keep its reserve.
    internal static void EnsureRoomForCopy(string source, long sourceBytes, Func<string, long?> available)
    {
        if (available(Path.GetDirectoryName(source)!) is { } free && free - sourceBytes < ExportDiskGuard.ReserveBytes)
            throw new IOException("There is not enough free space beside the recording to add the comment safely. The source was not changed.");
    }

    // A copy left by a crash or power loss before its recovery record existed. Nothing else names it,
    // and it is as large as the recording.
    private static void RemoveAbandonedCopies(string source)
    {
        string? folder = Path.GetDirectoryName(source);
        string prefix = Path.GetFileName(source) + ".adtrim-comment-";
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, prefix + "*.tmp"))
            {
                string name = Path.GetFileName(path);
                if (name.Length != prefix.Length + 36 || !name.StartsWith(prefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(name[prefix.Length..^4], "N", out _)
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next open tries again */ }
    }

    private static void CheckProjects(IEnumerable<SavedProject> projects)
    {
        foreach (var p in projects)
        {
            if (!File.Exists(p.Path)) throw new IOException("A project changed during source commenting.");
            var current = File.ReadAllBytes(p.Path);
            if (!current.SequenceEqual(p.Before) && !current.SequenceEqual(p.After))
                throw new IOException("A project changed during source commenting. Recovery files were preserved.");
        }
    }
    private static string Hash(string path, Action<long>? advance = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (advance is null) return Convert.ToHexString(SHA256.HashData(stream));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = stream.Read(buffer)) > 0) { sha.AppendData(buffer, 0, n); advance(n); }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct, Action<long>? advance = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        if (advance is null) return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0) { sha.AppendData(buffer, 0, n); advance(n); }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    // Reports the share of source-sized work done, in 0.1% steps, with the current step's name.
    // Byte counts stop at 99%: only Complete, after verification and commit, reports 100%.
    private sealed class PassProgress(long total, IProgress<SourceCommentProgress>? progress, bool restoring = false)
    {
        private long _done;
        private long _reported = -1;
        private string _step = "";
        public void Step(string name)
        {
            _step = name;
            Report(force: true);
        }
        public void Add(long bytes)
        {
            _done += bytes;
            Report(force: false);
        }
        public void Complete()
        {
            if (progress is null) return;
            _reported = 1000;
            progress.Report(new(restoring, _step, 1));
        }
        private void Report(bool force)
        {
            if (progress is null) return;
            long permille = total <= 0 ? 0 : Math.Min(990, _done * 1000 / total);
            if (permille == _reported && !force) return;
            _reported = permille;
            progress.Report(new(restoring, _step, permille / 1000.0));
        }
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(path) && File.Exists(temp)) File.Delete(temp); }
    }
}
