using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AdTrim.Services;

public sealed record UnexportedFile(string Path, bool Exported, string? Error = null, bool IsFolder = false);

public static class UnexportedFiles
{
    public static async IAsyncEnumerable<UnexportedFile> ScanAsync(string root, [EnumeratorCancellation] CancellationToken ct = default, bool includeFolders = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<UnexportedFile>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                var store = new ProjectStore();
                var folders = new Stack<string>();
                folders.Push(System.IO.Path.GetFullPath(root));
                while (folders.TryPop(out var folder))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (includeFolders) await channel.Writer.WriteAsync(new(folder, false, IsFolder: true), stop.Token);
                    // Enumerators can fail partway through a network directory.
                    IEnumerator<string>? entries = null;
                    try { entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { await channel.Writer.WriteAsync(new(folder, false, ex.Message), stop.Token); }
                    if (entries is null) continue;
                    using (entries)
                        while (true)
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            string? path = null;
                            string? enumerationError = null;
                            try { if (entries.MoveNext()) path = entries.Current; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { enumerationError = ex.Message; }
                            if (enumerationError is not null)
                                await channel.Writer.WriteAsync(new(folder, false, enumerationError), stop.Token);
                            if (path is null) break;
                            UnexportedFile? entry = null;
                            try
                            {
                                var attrs = File.GetAttributes(path);
                                if ((attrs & FileAttributes.Directory) != 0 && System.IO.Path.GetFileName(path).StartsWith('.')) continue;
                                if ((attrs & FileAttributes.ReparsePoint) != 0)
                                { entry = new(path, false, "Filesystem link skipped."); }
                                else if ((attrs & FileAttributes.Directory) != 0) folders.Push(path);
                                else if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".ts")
                                {
                                    if (SourceCommentService.HasPendingUpdate(path))
                                        entry = new(path, false, "An interrupted source comment update needs recovery. Open the recording normally first.");
                                    else
                                    {
                                        var comments = System.IO.Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                                            ? Mp4Comments.Read(path) : Array.Empty<string>();
                                        entry = new(path, comments.Any(c => !string.IsNullOrWhiteSpace(c))
                                            ? Mp4Comments.HasExportLabel(comments) : store.HasExportRecord(path));
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OverflowException)
                            { entry = new(path, false, ex.Message); }
                            if (entry is not null) await channel.Writer.WriteAsync(entry, stop.Token);
                        }
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, stop.Token);
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct)) yield return entry;
        }
        finally
        {
            stop.Cancel();
            try { await producer; } catch (OperationCanceledException) { }
        }
    }
}
