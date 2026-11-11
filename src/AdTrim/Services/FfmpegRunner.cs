using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AdTrim.Services;

public sealed class FfmpegResult
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool Success => ExitCode == 0;

    /// <summary>
    /// The end of the tool's log, short enough for a message on screen. Analysis passes log a line
    /// per frame, so the whole log can be hundreds of kilobytes with the reason on its last lines.
    /// </summary>
    public string ErrorSummary
    {
        get
        {
            var lines = Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string text = string.Join(" ", lines.Skip(Math.Max(0, lines.Length - 3)));
            return text.Length == 0 ? "No details were reported." : text.Length <= 400 ? text : "..." + text[^400..];
        }
    }
}

/// <summary>
/// Ends ffmpeg and ffprobe with AdTrim, including when AdTrim crashes or is ended from Task
/// Manager. Without it an export's encoder keeps running and holds the recording open.
/// </summary>
internal static class ChildProcessJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    // Never closed: Windows closes it when this process ends, which ends the children.
    private static readonly IntPtr Job = Create();

    public static void Add(Process process)
    {
        if (Job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Job, process.Handle); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        CloseHandle(job);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Resolves the bundled ffmpeg/ffprobe binaries and runs them with async
/// stdout/stderr capture and cancellation. Bundled path first, env-var
/// dev override second. Never falls back to PATH - at runtime we want a
/// known, license-audited binary.
/// </summary>
public sealed class FfmpegRunner
{
    // MP4 retains its original editor clock; TS uses timestamps relative to the container start.
    internal static string[] SourceTimestampArgs(long timelineStartUs) => timelineStartUs == 0
        ? ["-copyts"]
        : ["-copyts", "-start_at_zero"];

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
        ChildProcessJob.Add(process);
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
        ChildProcessJob.Add(p);
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
