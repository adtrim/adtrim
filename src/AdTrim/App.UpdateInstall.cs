using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using AdTrim.Services;
using AdTrim.Views;

namespace AdTrim;

public partial class App
{
    private MainWindow? _updateOwner;
    private bool _updatePreparing;
    private bool _updateStartupProtected;
    private readonly List<string> _updateDeferredPaths = new();

    internal async Task ProtectUpdateStartupAsync(string? marker, CancellationToken ct)
    {
        if (marker is null || !UpdateInstallation.IsUpdating(UserPreferences.DataDirectory)) return;
        _updateStartupProtected = true;
        foreach (var window in Windows.OfType<MainWindow>()) window.IsEnabled = false;
        try
        {
            // Ready is not enough: the helper may still offer rollback until it releases the gate.
            while (UpdateInstallation.IsUpdating(UserPreferences.DataDirectory)) await Task.Delay(100, ct);
            ct.ThrowIfCancellationRequested();
            _updateStartupProtected = false;
            foreach (var window in Windows.OfType<MainWindow>()) window.IsEnabled = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { WriteCrashLog("Update startup protection", ex); }
    }

    public static string UpdatePublicKey()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("AdTrim.Services.update-public-key.pem")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>While restarting for an update, recordings opened from outside wait for the restart.</summary>
    private bool DeferForUpdate(string? path)
    {
        if (!_updatePreparing) return false;
        if (!string.IsNullOrEmpty(path)) _updateDeferredPaths.Add(Path.GetFullPath(path));
        return true;
    }

    public async Task RestartAndInstallUpdateAsync(DownloadedUpdate update)
    {
        if (_updatePreparing) throw new IOException("Another update is already preparing to restart.");
        var directory = UpdateInstallation.InstalledDirectoryForCurrentApp();
        var windows = Windows.OfType<MainWindow>().ToArray();
        if (windows.Any(w => !w.CanRestartForUpdate || w.IsBlockedByDialog))
            throw new IOException("Finish exports, close export setup and other dialogs, and wait for analysis or file opening to finish before restarting.");
        UpdateStaging.ValidateDirectory(UserPreferences.DataDirectory, update.Directory);
        Process? worker = null;
        using var gate = new Mutex(false, UpdateInstallation.GateName(UserPreferences.DataDirectory), out var created);
        if (!created) throw new IOException("An AdTrim update is already running.");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateInstallation.ReadyName(update.Directory));
        _updatePreparing = true;
        _updateOwner = windows.FirstOrDefault();
        _updateDeferredPaths.Clear();
        bool closing = false;
        try
        {
            foreach (var window in windows) window.IsEnabled = false;
            foreach (var window in windows)
                if (!await window.SaveForUpdateAsync()) throw new IOException("An open recording could not be saved. Your windows have been kept open; save the edits and try again.");
            UpdateInstallation.WriteJob(update, directory);
            // Copy only the self-contained application. Helper mode never opens a media player.
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "AdTrim.dll")))
                throw new IOException("Automatic installation requires the packaged AdTrim application.");
            var helper = Path.Combine(update.Directory, "AdTrim.Update.exe");
            await Task.Run(() => File.Copy(Environment.ProcessPath!, helper, true));
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = update.Directory };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(update.Directory);
            worker = Process.Start(start) ?? throw new IOException("The update helper could not be started.");
            var deadline = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                if (worker.HasExited) throw new IOException("The update helper could not prepare the verified installer.");
                if (deadline.Elapsed > TimeSpan.FromSeconds(60)) throw new IOException("The update helper did not become ready. AdTrim has stayed open.");
                await Task.Delay(50);
            }
            UpdateInstallation.WriteResume(update, windows.Select(w => (w.DataContext as ViewModels.MainViewModel)?.SourcePath).OfType<string>().Concat(_updateDeferredPaths));
            closing = true;
            foreach (var window in windows) window.CloseForUpdate();
            Shutdown();
        }
        finally
        {
            if (!closing)
            {
                // The helper cannot start the installer until this process exits.
                try { if (worker is { HasExited: false }) worker.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* it exited first */ }
                if (worker is not null) await worker.WaitForExitAsync();
                foreach (var window in windows) window.CancelUpdatePreparation();
                var deferred = _updateDeferredPaths.ToArray();
                _updateDeferredPaths.Clear();
                _updateOwner = null;
                _updatePreparing = false;
                foreach (var path in deferred) _ = OpenRecordingAsync(path);
            }
            worker?.Dispose();
        }
    }

    // --- Update helper process (AdTrim.Update.exe --apply-update <folder>) ---

    private async Task<bool> RunUpdateModeAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--apply-update") return false;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new UpdateProgressWindow(AppVersion.Numeric);
        window.Show();
        UpdateWorker worker;
        try
        {
            var registered = UpdateInstallation.RegisteredDirectory() ?? throw new IOException("The AdTrim installation could not be located.");
            worker = new UpdateWorker(UserPreferences.DataDirectory, args[1], UpdatePublicKey(), AppVersion.Numeric, registered);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Automatic update", ex);
            await window.ShowErrorAsync(ex.Message + " Nothing has been changed. Start AdTrim from the Start menu.", null,
                UpdateProgressWindow.FormatDetails(AppVersion.Numeric, null, window.Step, ex), canRetry: false);
            Shutdown(1);
            return true;
        }
        using var stopPipe = new CancellationTokenSource();
        worker.StepChanged += step => Dispatcher.BeginInvoke(() => window.ShowStep(step));
        window.StopRequested += (_, _) => worker.StopInstaller();
        _ = RunUpdatePipeAsync(worker, window, stopPipe.Token);
        bool finished = await RunUpdateStepsAsync(worker, window);
        stopPipe.Cancel();
        worker.Dispose();
        Shutdown(finished ? 0 : 1);
        return true;
    }

    /// <summary>Runs each step; a failure waits for Retry or Cancel. Returns true when AdTrim is running again.</summary>
    private async Task<bool> RunUpdateStepsAsync(UpdateWorker worker, UpdateProgressWindow window)
    {
        async Task<UpdateChoice> Failed(Exception ex, string? cancelLabel, string? message = null)
        {
            WriteCrashLog("Automatic update: " + worker.Step, ex);
            return await window.ShowErrorAsync(message ?? ex.Message, cancelLabel,
                UpdateProgressWindow.FormatDetails(worker.CurrentVersion, worker.TargetVersion, worker.Step, ex));
        }
        while (true)
        {
            try { await worker.WaitForParentAsync(TimeSpan.FromSeconds(90), CancellationToken.None); break; }
            catch (Exception ex)
            {
                // Nothing has been changed yet; closing just reopens AdTrim if it already closed.
                if (await Failed(ex, "Cancel update") == UpdateChoice.Retry) continue;
                return await StartPreviousAsync(worker, window, Failed);
            }
        }
        window.SetTarget(worker.TargetVersion);
        while (true)
        {
            try { await worker.InstallAsync(CancellationToken.None); break; }
            catch (Exception ex)
            {
                if (await Failed(ex, "Cancel update") == UpdateChoice.Retry) continue;
                return await RestorePreviousAsync(worker, window, Failed);
            }
        }
        while (true)
        {
            try { await worker.StartAsync(TimeSpan.FromSeconds(90), CancellationToken.None); break; }
            catch (Exception ex)
            {
                if (await Failed(ex, "Restore previous version", $"The new version was installed but did not start. {ex.Message}") == UpdateChoice.Retry) continue;
                return await RestorePreviousAsync(worker, window, Failed);
            }
        }
        ForwardDeferred(worker);
        worker.Finish();
        window.CloseWhenFinished();
        return true;
    }

    private async Task<bool> RestorePreviousAsync(UpdateWorker worker, UpdateProgressWindow window,
        Func<Exception, string?, string?, Task<UpdateChoice>> failed)
    {
        while (true)
        {
            try { await worker.RestoreAsync(); break; }
            catch (Exception ex)
            {
                string backup = UpdateProgressWindow.Sanitize(worker.BackupDirectory);
                if (await failed(ex, "Close", $"{ex.Message} The previous version's files are kept in {backup}. "
                    + "Retry, or reinstall AdTrim from adtrim.github.io; your settings and projects are not affected.") == UpdateChoice.Retry) continue;
                return false;
            }
        }
        return await StartPreviousAsync(worker, window, failed);
    }

    private async Task<bool> StartPreviousAsync(UpdateWorker worker, UpdateProgressWindow window,
        Func<Exception, string?, string?, Task<UpdateChoice>> failed)
    {
        if (worker.TargetVersion is null) { window.CloseWhenFinished(); return false; }
        while (true)
        {
            try { await worker.StartAsync(TimeSpan.FromSeconds(90), CancellationToken.None); break; }
            catch (Exception ex)
            {
                if (await failed(ex, "Close", $"{ex.Message} Start AdTrim from the Start menu; your recordings and projects are kept.") == UpdateChoice.Retry) continue;
                return false;
            }
        }
        ForwardDeferred(worker);
        worker.Finish();
        window.CloseWhenFinished();
        return true;
    }

    // Recordings opened after the restarted app took over go to it directly.
    private static void ForwardDeferred(UpdateWorker worker)
    {
        foreach (var path in worker.TakeDeferred()) TrySendPathToExistingInstance(path);
    }

    /// <summary>
    /// Answers AdTrim launches made while files are being replaced: the launch hands over its recording
    /// and exits at once, and this window comes to the front.
    /// </summary>
    private async Task RunUpdatePipeAsync(UpdateWorker worker, UpdateProgressWindow window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(UpdateInstallation.HelperPipeName(UserPreferences.DataDirectory),
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var path = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) worker.Defer(path);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("OK");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200, CancellationToken.None); }
        }
    }
}
