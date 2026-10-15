using System.Diagnostics;
using System.IO;
using System.Text;

namespace AdTrim.Services;

public sealed class FfmpegResult
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Resolves the bundled ffmpeg/ffprobe binaries and runs them with async
/// stdout/stderr capture and cancellation. Bundled path first, env-var
/// dev override second. Never falls back to PATH - at runtime we want a
/// known, license-audited binary.
/// </summary>
public sealed class FfmpegRunner
{
    private readonly string _ffmpegDir;

    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public FfmpegRunner()
    {
        _ffmpegDir = ResolveBinariesDir();
        FfmpegPath = Path.Combine(_ffmpegDir, "ffmpeg.exe");
        FfprobePath = Path.Combine(_ffmpegDir, "ffprobe.exe");
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            throw new FileNotFoundException(
                $"ffmpeg.exe and/or ffprobe.exe not found in '{_ffmpegDir}'. " +
                "See binaries/README.md for install instructions.");
        }
    }

    private static string ResolveBinariesDir()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "binaries", "ffmpeg", "win-x64");
        if (File.Exists(Path.Combine(bundled, "ffmpeg.exe"))) return bundled;

        var devOverride = Environment.GetEnvironmentVariable("ADTRIM_FFMPEG_DIR");
        if (!string.IsNullOrEmpty(devOverride)
            && File.Exists(Path.Combine(devOverride, "ffmpeg.exe")))
            return devOverride;

        return bundled;
    }

    public Task<FfmpegResult> RunFfprobeAsync(IEnumerable<string> args, CancellationToken ct = default)
        => RunAsync(FfprobePath, args, onStdoutLine: null, ct);

    public Task<FfmpegResult> RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct = default)
        => RunAsync(FfmpegPath, args, onStdoutLine: null, ct);

    /// <summary>
    /// Stream-aware ffmpeg run. <paramref name="onStdoutLine"/> is invoked
    /// per stdout line *as it arrives* (on a threadpool thread) and those
    /// lines are NOT appended to the buffered <see cref="FfmpegResult.Stdout"/>
    /// - this avoids unbounded buffer growth on long encodes that emit
    /// `-progress pipe:1` continuously. Stderr is still buffered for error
    /// diagnostics. Pass <c>null</c> for the legacy buffer-everything behaviour.
    /// </summary>
    public Task<FfmpegResult> RunFfmpegAsync(
        IEnumerable<string> args, Action<string>? onStdoutLine, CancellationToken ct = default,
        Action<string>? onStderrLine = null)
        => RunAsync(FfmpegPath, args, onStdoutLine, ct, onStderrLine);

    public async Task<FfmpegResult> RunFfmpegBinaryAsync(IEnumerable<string> args,
        Func<Stream, Task> consume, Action<string>? onDiagnostic, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        var diagnostics = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onDiagnostic?.Invoke(e.Data);
            diagnostics.AppendLine(e.Data);
            if (diagnostics.Length > 1_048_576) diagnostics.Remove(0, diagnostics.Length - 524_288);
        };
        process.Start();
        process.BeginErrorReadLine();
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* exit race */ }
        }
        using var registration = ct.Register(Kill);
        try { await consume(process.StandardOutput.BaseStream).ConfigureAwait(false); }
        catch
        {
            Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();
            throw;
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();
        return new FfmpegResult { ExitCode = process.ExitCode, Stdout = "", Stderr = diagnostics.ToString() };
    }

    public async Task<FfmpegResult> RunAsync(
        string exe, IEnumerable<string> args, Action<string>? onStdoutLine, CancellationToken ct,
        Action<string>? onStderrLine = null)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onStdoutLine is not null) onStdoutLine(e.Data);
            else stdout.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            onStderrLine?.Invoke(e.Data);
            stderr.AppendLine(e.Data);
            if (stderr.Length > 1_048_576) stderr.Remove(0, stderr.Length - 524_288);
        };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await using var _ = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* race */ }
        });

        // Wait for the kill (or natural exit) WITHOUT honoring ct here - we
        // want the process to be fully exited before we return so the stdout
        // and stderr buffers reflect everything ffmpeg actually emitted.
        // Bailing on cancellation before the readers settle leaves stderr
        // possibly truncated mid-line, which matters when callers (e.g.
        // ExportService, RefineService) parse the tail for diagnostics.
        await p.WaitForExitAsync().ConfigureAwait(false);

        // WaitForExitAsync signals the exit event but does NOT guarantee that
        // the async OutputDataReceived/ErrorDataReceived callbacks have
        // drained. The synchronous overload, per .NET docs, "ensures all
        // asynchronous event handlers... have been processed" - that's the
        // one operation that actually flushes the readers. It returns
        // immediately for an already-exited process aside from this drain.
        p.WaitForExit();

        ct.ThrowIfCancellationRequested();

        return new FfmpegResult
        {
            ExitCode = p.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
        };
    }
}
