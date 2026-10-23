using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Recoverable source/comment and project fingerprint transaction.</summary>
public static class SourceCommentService
{
    private sealed record SavedProject(string Path, byte[] Before, byte[] After);
    private sealed record Transaction(string BeforeHash, string AfterHash, string StageName, List<SavedProject> Projects, bool Committed = false);
    private static string Journal(string source) => source + ".adtrim-export-comment.json";
    private static string Backup(string source) => source + ".adtrim-export-comment.bak";
    public static bool HasPendingUpdate(string source) => File.Exists(Journal(source));

    public static async Task<SourceFingerprint> MarkAsync(string source, SourceFingerprint expected, ProjectStore store, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        Recover(source, store);
        ExportSafety.EnsureMetadataWritable(source);
        var before = ProjectStore.FingerprintOf(source, expected.DurationUs);
        if (before != expected) throw new IOException("The source changed before its comment could be added.");
        if (File.Exists(Backup(source))) throw new IOException("A source comment backup already exists; it was preserved.");
        string stage = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(source) + ".adtrim-comment-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string beforeHash = await HashAsync(source, ct);
            await Mp4Comments.StageAsync(source, stage, AppVersion.Numeric, DateOnly.FromDateTime(DateTime.Now), ct);
            string afterHash = await HashAsync(stage, ct);
            if (afterHash == beforeHash) return before;
            var after = ProjectStore.FingerprintOf(stage, before.DurationUs);
            var updates = store.FingerprintUpdates(source, before, after);
            var projects = updates.Select(p => new SavedProject(p.Key, p.Value.Before, p.Value.After)).ToList();
            var transaction = new Transaction(beforeHash, afterHash, Path.GetFileName(stage), projects);
            ct.ThrowIfCancellationRequested();
            if (await HashAsync(source, ct) != beforeHash || ProjectStore.FingerprintOf(source, before.DurationUs) != before)
                throw new IOException("The source changed during comment preparation.");
            CheckProjects(transaction.Projects);
            var record = JsonSerializer.SerializeToUtf8Bytes(transaction);
            if (record.Length > 48 * 1024 * 1024) throw new IOException("The project recovery record would be too large; the source was not changed.");
            WriteAtomic(Journal(source), record);
            ct.ThrowIfCancellationRequested();
            File.Replace(stage, source, Backup(source));
            if (await HashAsync(source, ct) != transaction.AfterHash) throw new IOException("The updated source could not be verified.");
            foreach (var project in projects)
            {
                ct.ThrowIfCancellationRequested();
                WriteAtomic(project.Path, project.After);
            }
            var actual = ProjectStore.FingerprintOf(source, before.DurationUs);
            if (actual != after) throw new IOException("The updated source fingerprint could not be verified.");
            WriteAtomic(Journal(source), JsonSerializer.SerializeToUtf8Bytes(transaction with { Committed = true }));
            Recover(source, store);
            return actual;
        }
        catch
        {
            if (HasPendingUpdate(source)) Recover(source, store);
            throw;
        }
        finally
        {
            if (!HasPendingUpdate(source) && File.Exists(stage)) File.Delete(stage);
        }
    }

    public static void Recover(string source, ProjectStore store)
    {
        source = Path.GetFullPath(source);
        string journal = Journal(source);
        if (!File.Exists(journal)) return;
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
        string hash = File.Exists(source) ? Hash(source) : "";
        if (hash != transaction.BeforeHash && hash != transaction.AfterHash && hash != "")
            throw new IOException("The recording changed after an interrupted comment update. Its recovery files were preserved.");
        CheckProjects(transaction.Projects);
        if (!transaction.Committed)
        {
            if (File.Exists(Backup(source)))
            {
                if (Hash(Backup(source)) != transaction.BeforeHash) throw new IOException("Source comment rollback backup failed verification.");
                if (File.Exists(source)) File.Replace(Backup(source), source, null);
                else File.Move(Backup(source), source);
            }
            if (!File.Exists(source) || Hash(source) != transaction.BeforeHash)
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
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
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
