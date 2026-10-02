using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace AdTrim.Services;

public sealed class ExportDiskGuard : IAsyncDisposable
{
    public const long ReserveBytes = 512L * 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _export;
    private readonly Task _monitor;
    public CancellationToken Token => _export.Token;
    public string? Failure { get; private set; }

    public ExportDiskGuard(IEnumerable<string> directories, CancellationToken ct,
        Func<string, long>? availableBytes = null, TimeSpan? interval = null)
    {
        var paths = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var read = availableBytes ?? AvailableBytes;
        foreach (var path in paths) Check(path, read);
        _export = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _monitor = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(interval ?? TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                    foreach (var path in paths) Check(path, read);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExportException or Win32Exception)
            {
                Failure = ex.Message;
                _export.Cancel();
            }
        });
    }

    private static void Check(string path, Func<string, long> read)
    {
        if (read(path) < ReserveBytes)
            throw new ExportException($"Export stopped because disk space is low at {path}. Free space and try again.");
    }

    private static long AvailableBytes(string path)
    {
        if (!GetDiskFreeSpaceEx(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, out var available, out _, out _))
            throw new ExportException($"Cannot check available disk space at {path}.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (long)Math.Min(available, (ulong)long.MaxValue);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _monitor.ConfigureAwait(false);
        _export.Dispose();
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
