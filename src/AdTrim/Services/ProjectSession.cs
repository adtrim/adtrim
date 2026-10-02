using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Serializes immutable save snapshots. The caller owns UI state.</summary>
public sealed class ProjectSession
{
    private readonly Func<AdTrimProject, string>? _write;
    public ProjectSession(Func<AdTrimProject, string>? write = null) => _write = write;
    private readonly SemaphoreSlim _writer = new(1, 1);
    public async Task<string> SaveAsync(ProjectStore store, AdTrimProject snapshot)
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => _write is null ? store.Save(snapshot) : _write(snapshot)).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }
}
