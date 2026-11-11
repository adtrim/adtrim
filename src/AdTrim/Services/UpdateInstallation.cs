using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AdTrim.Services;

public sealed record UpdateInstallJob(string Version, string InstallDirectory, int ParentId, long ParentStartedTicks);
public sealed record UpdateResume(string Version, string[] Recordings);

public static class UpdateInstallation
{
    /// <summary>Set for the restarted app: the file it writes once its first window is ready.</summary>
    public const string StartedMarkerVariable = "ADTRIM_UPDATE_STARTED";
    public const string StartedMarkerName = "started.txt";

    private static string Key(string dataDirectory) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..20];
    public static string GateName(string dataDirectory) => "Local\\AdTrim.Update." + Key(dataDirectory);
    public static string HelperPipeName(string dataDirectory) => "AdTrim.Update." + Key(dataDirectory);
    public static string ReadyName(string directory) => "Local\\AdTrim.Update.Ready." + Path.GetFileName(directory);

    public static bool IsUpdating(string dataDirectory)
    {
        if (!Mutex.TryOpenExisting(GateName(dataDirectory), out var gate)) return false;
        gate.Dispose();
        return true;
    }

    public static string? RegisteredDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Software\\AdTrim");
        var path = key?.GetValue("InstallDir") as string;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    public static string InstalledDirectoryForCurrentApp()
    {
        var directory = RegisteredDirectory();
        if (directory is null || Environment.ProcessPath is not { } exe
            || !ExportSafety.SameFile(exe, Path.Combine(directory, "AdTrim.exe")))
            throw new IOException("Run the installed copy of AdTrim to install an update automatically. Use See what's new for a manual download.");
        return directory;
    }

    /// <summary>
    /// Hands a recording (or an empty request) to the running update window instead of starting while
    /// files are being replaced. Returns false when no update window answered.
    /// </summary>
    public static bool TrySendToHelper(string dataDirectory, string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", HelperPipeName(dataDirectory), PipeDirection.InOut);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(path ?? "");
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            return reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() == "OK";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Pipe request asking a running AdTrim to confirm, once its windows are ready, that an update finished.</summary>
    public const string StartedRequestPrefix = "::started::";

    /// <summary>The marker the update window gave this process, removed from its environment so children do not inherit it.</summary>
    public static string? TakeStartedMarkerRequest()
    {
        var marker = Environment.GetEnvironmentVariable(StartedMarkerVariable);
        Environment.SetEnvironmentVariable(StartedMarkerVariable, null);
        return !string.IsNullOrEmpty(marker) && Path.GetFileName(marker) == StartedMarkerName ? marker : null;
    }

    /// <summary>Called by a restarted app once its windows are ready, so the update window can finish.</summary>
    public static void MarkStarted()
    {
        if (TakeStartedMarkerRequest() is { } marker) WriteStartedMarker(marker);
    }

    /// <summary>Records this process as the ready app. The update window checks the process before trusting it.</summary>
    public static void WriteStartedMarker(string marker)
    {
        if (Path.GetFileName(marker) != StartedMarkerName) return;
        try { File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True only when the marker names a running process whose image is <paramref name="installedExe"/>:
    /// a launch that merely forwarded to another copy, or any unrelated process, cannot confirm a start.
    /// </summary>
    public static bool ConfirmsStart(string marker, string installedExe)
    {
        try
        {
            if (!File.Exists(marker) || !int.TryParse(File.ReadAllText(marker).Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid)) return false;
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || ImagePath(pid) is not { } image) return false;
            // Windows reports the resolved path. An installation reached through a junction, SUBST or
            // mapped drive is the same file under another name.
            return string.Equals(Path.GetFullPath(image), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase)
                || ExportSafety.SameFile(image, installedExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or ExportException) { return false; }
    }

    private static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(32_768);
        int size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(int access, bool inherit, int processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    /// <summary>Reads the job and checks its target, without trusting the downloaded installer yet.</summary>
    public static UpdateInstallJob ReadJob(string dataDirectory, string directory, string currentVersion, string registeredDirectory)
    {
        directory = UpdateStaging.ValidateDirectory(dataDirectory, directory);
        var job = Read<UpdateInstallJob>(directory, "job.json", 16 * 1024);
        if (!Path.IsPathFullyQualified(job.InstallDirectory)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.InstallDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(registeredDirectory)), StringComparison.OrdinalIgnoreCase)
            || UpdateFeedReader.ParseVersion(job.Version) <= UpdateFeedReader.ParseVersion(currentVersion))
            throw new InvalidDataException("The update installation target is invalid.");
        return job;
    }

    public static async Task<UpdateInstallJob> ReadAndVerifyJobAsync(string dataDirectory, string directory,
        string publicKey, string currentVersion, string registeredDirectory, CancellationToken ct)
    {
        directory = UpdateStaging.ValidateDirectory(dataDirectory, directory);
        var job = ReadJob(dataDirectory, directory, currentVersion, registeredDirectory);
        var manifest = UpdatePackageManifest.Verify(ReadBytes(directory, "update-package.json", UpdateFeedReader.MaximumBytes),
            ReadBytes(directory, "update-package.json.sig", UpdateFeedReader.MaximumSignatureBytes), publicKey, job.Version, DateTimeOffset.UtcNow);
        await manifest.VerifyInstallerAsync(Path.Combine(directory, manifest.InstallerName), ct);
        return job;
    }

    public static void WriteJob(DownloadedUpdate update, string installDirectory)
    {
        using var parent = Process.GetCurrentProcess();
        Write(update.Directory, "job.json", new UpdateInstallJob(update.Manifest.Version, installDirectory,
            parent.Id, parent.StartTime.ToUniversalTime().Ticks));
    }

    public static void WriteResume(DownloadedUpdate update, IEnumerable<string> recordings)
        => WriteResume(update.Directory, update.Manifest.Version, recordings);

    /// <summary>Records the recordings to reopen, for the version that will read them.</summary>
    public static void WriteResume(string directory, string version, IEnumerable<string> recordings)
    {
        var paths = recordings.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 128) throw new IOException("Too many recording windows are open to restart automatically.");
        var resume = new UpdateResume(version, paths);
        if (JsonSerializer.SerializeToUtf8Bytes(resume).Length > 128 * 1024)
            throw new IOException("The open recording paths are too long to restart automatically. Close some windows and try again.");
        Write(directory, "resume.json", resume);
    }

    public static UpdateResume ReadResume(string dataDirectory, string directory, string currentVersion)
    {
        directory = UpdateStaging.ValidateDirectory(dataDirectory, directory);
        var resume = Read<UpdateResume>(directory, "resume.json", 128 * 1024);
        if (resume.Version != currentVersion || resume.Recordings is null || resume.Recordings.Length > 128
            || resume.Recordings.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
            throw new IOException("The update restart information is invalid.");
        return resume;
    }

    public static void Write<T>(string directory, string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var path = Path.Combine(directory, name);
        var temporary = path + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static T Read<T>(string directory, string name, int maximum)
        => JsonSerializer.Deserialize<T>(ReadBytes(directory, name, maximum)) ?? throw new IOException("The update state is empty.");
    private static byte[] ReadBytes(string directory, string name, int maximum)
    {
        using var input = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > maximum) throw new IOException("The update state has an invalid size.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
}

public enum UpdateStep { WaitingForClose, Verifying, BackingUp, Installing, CheckingVersion, Starting, Restoring, StartingPrevious, Done }

/// <summary>
/// The update helper's work, one step at a time so the update window can show progress, retry a
/// failed step, or put the previous version back. Runs from the update folder, never from the
/// installation it replaces, and holds the update gate so the app cannot start mid-install.
/// </summary>
public sealed class UpdateWorker : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _publicKey;
    private readonly string _registeredDirectory;
    private readonly List<string> _deferred = new();
    private UpdateInstallJob? _job;
    private Mutex? _gate;
    private bool _installAttempted;
    private Process? _installer;
    // The copy of AdTrim this helper started and is waiting on; kept so Retry does not start a second
    // copy and Cancel can close it before restoring files it holds open.
    private Process? _starting;
    private string? _startingVersion;
    private volatile bool _stopRequested;

    public UpdateWorker(string dataDirectory, string directory, string publicKey, string currentVersion, string registeredDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory = UpdateStaging.ValidateDirectory(dataDirectory, directory);
        _publicKey = publicKey;
        CurrentVersion = currentVersion;
        _registeredDirectory = registeredDirectory;
    }

    public string Directory { get; }
    public string CurrentVersion { get; }
    public string? TargetVersion => _job?.Version;
    public UpdateStep Step { get; private set; } = UpdateStep.WaitingForClose;
    public event Action<UpdateStep>? StepChanged;
    public string BackupDirectory => Path.Combine(Directory, "backup");
    /// <summary>True once the installer has run: the previous version must be restored to undo the update.</summary>
    public bool NeedsRestore => _installAttempted;
    /// <summary>How many times the installer runs before a failure is reported (a file can be briefly in use).</summary>
    public int InstallerAttempts { get; init; } = 2;
    /// <summary>How long a copy that has shown its window may take to reopen its recordings.</summary>
    public TimeSpan OpeningLimit { get; init; } = TimeSpan.FromMinutes(10);

    private void Enter(UpdateStep step) { Step = step; StepChanged?.Invoke(step); }

    /// <summary>Recordings someone tried to open during the update; they open afterwards.</summary>
    public void Defer(string path) { lock (_deferred) _deferred.Add(Path.GetFullPath(path)); }
    public string[] TakeDeferred() { lock (_deferred) { var paths = _deferred.ToArray(); _deferred.Clear(); return paths; } }

    /// <summary>Verifies the job, takes the update gate, tells AdTrim it may close, and waits for it to exit.</summary>
    public async Task WaitForParentAsync(TimeSpan timeout, CancellationToken ct)
    {
        Enter(UpdateStep.WaitingForClose);
        // The job is kept even if the installer then fails its checks, so Cancel can reopen the installed version.
        _job ??= UpdateInstallation.ReadJob(_dataDirectory, Directory, CurrentVersion, _registeredDirectory);
        await UpdateInstallation.ReadAndVerifyJobAsync(_dataDirectory, Directory, _publicKey, CurrentVersion, _registeredDirectory, ct);
        Process? parent = null;
        try { parent = Process.GetProcessById(_job.ParentId); }
        catch (ArgumentException) { /* already closed */ }
        using (parent)
        {
            // The gate belongs to the AdTrim that asked for this update. If that copy is gone (closed,
            // crashed, or its process id now belongs to something else) there is nothing to hand over.
            const string gone = "AdTrim closed before the update could start. Nothing has been changed. Open AdTrim and start the update again.";
            if (parent is not null && parent.StartTime.ToUniversalTime().Ticks != _job.ParentStartedTicks)
                throw new IOException(gone);
            // Keep the gate across the parent's exit, so another AdTrim cannot open files being replaced.
            try { _gate ??= Mutex.OpenExisting(UpdateInstallation.GateName(_dataDirectory)); }
            catch (WaitHandleCannotBeOpenedException ex) { throw new IOException(gone, ex); }
            if (EventWaitHandle.TryOpenExisting(UpdateInstallation.ReadyName(Directory), out var ready))
                using (ready) ready.Set();
            if (parent is null) return;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout);
            try { await parent.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("AdTrim did not close. Nothing has been changed yet."); }
        }
    }

    /// <summary>Re-verifies the package, backs up the installation once, runs the installer and checks the result.</summary>
    public async Task InstallAsync(CancellationToken ct)
    {
        var job = _job ?? throw new InvalidOperationException("The update job has not been read.");
        var path = Path.Combine(Directory, $"AdTrim-Setup-v{job.Version}.exe");
        if (!System.IO.Directory.Exists(BackupDirectory))
        {
            Enter(UpdateStep.BackingUp);
            try { await Task.Run(() => UpdateBackup.Create(job.InstallDirectory, BackupDirectory), ct); }
            catch
            {
                // An incomplete backup is never used; the next attempt starts a new one.
                if (System.IO.Directory.Exists(BackupDirectory)) System.IO.Directory.Delete(BackupDirectory, true);
                throw;
            }
        }
        _stopRequested = false;
        for (int attempt = 1; ; attempt++)
        {
            Enter(UpdateStep.Verifying);
            Process installer;
            // Re-read the signed bytes; hold a read-only handle until the installer has started.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await UpdateInstallation.ReadAndVerifyJobAsync(_dataDirectory, Directory, _publicKey, CurrentVersion, _registeredDirectory, ct);
                Enter(UpdateStep.Installing);
                // NSIS requires /D last and without quotes; this is a direct process launch, not a shell.
                installer = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    Arguments = "/S /D=" + job.InstallDirectory,
                    WorkingDirectory = Directory,
                }) ?? throw new IOException("The update installer could not be started.");
                _installAttempted = true;
            }
            using (installer)
            {
                _installer = installer;
                try { await installer.WaitForExitAsync(ct); }
                finally { _installer = null; }
                if (installer.ExitCode == 0) break;
                if (_stopRequested) throw new IOException("The installer was stopped before it finished.");
                if (attempt >= InstallerAttempts)
                    throw new IOException($"The installer stopped with error code {installer.ExitCode}. A file may have been in use.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        Enter(UpdateStep.CheckingVersion);
        string installed = Path.Combine(job.InstallDirectory, "AdTrim.exe");
        if (!System.Version.TryParse(FileVersionInfo.GetVersionInfo(installed).FileVersion, out var actual)
            || actual != new System.Version(job.Version + ".0"))
            throw new IOException("The installed application did not report the expected version.");
        UpdateInstallation.Write(Directory, "completed.json", new { version = job.Version, completedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>Stops an installer that has stopped responding. The previous version can then be restored.</summary>
    public void StopInstaller()
    {
        _stopRequested = true;
        try { if (_installer is { HasExited: false } running) running.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Starts the installed app with the recordings to reopen and waits until that installed copy reports its
    /// windows are ready. If an earlier attempt is still starting, waits for it again instead of starting another.
    /// </summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var job = _job ?? throw new InvalidOperationException("The update job has not been read.");
        bool previous = !File.Exists(Path.Combine(Directory, "completed.json"));
        Enter(previous ? UpdateStep.StartingPrevious : UpdateStep.Starting);
        string version = previous ? CurrentVersion : job.Version;
        string exe = Path.Combine(job.InstallDirectory, "AdTrim.exe");
        string marker = Path.Combine(Directory, UpdateInstallation.StartedMarkerName);
        if (_starting is not { HasExited: false } || _startingVersion != version)
        {
            _starting?.Dispose();
            var resume = File.Exists(Path.Combine(Directory, "resume.json"))
                ? UpdateInstallation.Read<UpdateResume>(Directory, "resume.json", 128 * 1024).Recordings : [];
            UpdateInstallation.WriteResume(Directory, version, resume.Concat(TakeDeferred()));
            if (File.Exists(marker)) File.Delete(marker);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = job.InstallDirectory };
            start.ArgumentList.Add("--resume-update");
            start.ArgumentList.Add(Directory);
            start.Environment[UpdateInstallation.StartedMarkerVariable] = marker;
            _starting = Process.Start(start) ?? throw new IOException("AdTrim could not be started.");
            _startingVersion = version;
        }
        var timer = Stopwatch.StartNew();
        // The short limit is for a copy that never shows a window. One that has is running: reopening
        // several recordings, or analysing new ones, takes minutes and is not a failed start.
        bool windowShown = false;
        while (!UpdateInstallation.ConfirmsStart(marker, exe))
        {
            if (!windowShown && _starting is { HasExited: false } running)
            {
                running.Refresh();
                windowShown = running.MainWindowHandle != IntPtr.Zero;
            }
            // A copy that handed its recordings to an AdTrim already running exits normally; that copy confirms instead.
            if (_starting is { HasExited: true } exited && exited.ExitCode != 0)
            {
                int code = exited.ExitCode;
                exited.Dispose();
                _starting = null;
                throw new IOException($"AdTrim {version} closed while starting (exit code {code}).");
            }
            if (timer.Elapsed > (windowShown ? OpeningLimit : timeout)) throw new TimeoutException($"AdTrim {version} did not finish starting.");
            await Task.Delay(100, ct);
        }
        _starting?.Dispose();
        _starting = null;
    }

    /// <summary>Closes a copy this helper started that never finished starting, so its files can be restored.</summary>
    public async Task StopStartingAppAsync()
    {
        var app = _starting;
        _starting = null;
        if (app is null) return;
        using (app)
        {
            if (app.HasExited) return;
            // The restarted app keeps its recording windows disabled while this helper holds the update gate.
            try { app.CloseMainWindow(); } catch (InvalidOperationException) { }
            try { await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            if (!app.HasExited)
            {
                try { app.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                await app.WaitForExitAsync();
            }
        }
    }

    /// <summary>Puts the backed-up version back after a failed or cancelled update.</summary>
    public async Task RestoreAsync()
    {
        var job = _job ?? throw new InvalidOperationException("The update job has not been read.");
        Enter(UpdateStep.Restoring);
        await StopStartingAppAsync();
        if (_installAttempted && System.IO.Directory.Exists(BackupDirectory))
        {
            // Windows can take a moment to release a program file after its process exits.
            for (int attempt = 1; ; attempt++)
            {
                try { await Task.Run(() => UpdateBackup.Restore(job.InstallDirectory, BackupDirectory)); break; }
                catch (IOException ex) when (attempt < 10) { System.Diagnostics.Debug.WriteLine("[update] restore retry: " + ex.Message); await Task.Delay(500); }
                catch (IOException ex)
                { throw new IOException("A file of the installed AdTrim is in use. Close every AdTrim window, then choose Retry. " + ex.Message, ex); }
            }
            // Setup recorded the new version in Installed apps; put the old one back, but only for this installation.
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AdTrim", writable: true);
            if (key?.GetValue("InstallLocation") is string location && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.InstallDirectory)), StringComparison.OrdinalIgnoreCase))
                key.SetValue("DisplayVersion", CurrentVersion);
        }
        string completed = Path.Combine(Directory, "completed.json");
        if (File.Exists(completed)) File.Delete(completed);
        _installAttempted = false;
    }

    /// <summary>Removes the backup once the app has started, and releases the update gate.</summary>
    public void Finish()
    {
        Enter(UpdateStep.Done);
        try { if (System.IO.Directory.Exists(BackupDirectory)) System.IO.Directory.Delete(BackupDirectory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* startup cleanup removes it later */ }
        Dispose();
    }

    public void Dispose()
    {
        _gate?.Dispose();
        _gate = null;
        _starting?.Dispose();
        _starting = null;
    }
}
