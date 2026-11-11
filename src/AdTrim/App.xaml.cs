using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AdTrim;

public partial class App : Application
{
    private static readonly string InstanceKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(Services.UserPreferences.DataDirectory.ToUpperInvariant())))[..20];
    private static string MutexName => "Local\\AdTrim.Windows." + InstanceKey;
    private static string PipeName => "AdTrim.Windows." + InstanceKey;

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeServerCts;

    private readonly Dictionary<Views.ExportView, Models.ExportPlan> _exports = new();
    private Services.UpdateService? _updates;
    public bool UpdateStartupInterrupted { get; set; }
    /// <summary>The single update window, shared by all recording windows.</summary>
    public Views.UpdateDialog? OpenUpdateDialog { get; set; }
    public bool UpdateDialogOpen => OpenUpdateDialog is not null;
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private string? _resumeDirectory;
    private CancellationTokenSource? _automaticUpdateCts;
    public bool AutomaticUpdatesEnabled => Services.UpdatePreferences.Load(Services.UserPreferences.DataDirectory);
    public event EventHandler? UpdatePreferenceChanged;

    public void SetAutomaticUpdates(bool enabled)
    {
        Services.UpdatePreferences.Save(Services.UserPreferences.DataDirectory, enabled);
        if (!enabled) _automaticUpdateCts?.Cancel();
        UpdatePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }
    public Services.UpdateService Updates
    {
        get
        {
            if (_updates is not null) return _updates;
            using var stream = typeof(App).Assembly.GetManifestResourceStream("AdTrim.Services.update-public-key.pem")!;
            using var reader = new StreamReader(stream);
            return _updates = new Services.UpdateService(Services.UserPreferences.DataDirectory, reader.ReadToEnd());
        }
    }

    public string? ReserveExport(Views.ExportView view, Models.ExportPlan plan)
    {
        if (_updatePreparing) return "AdTrim is preparing to restart for an update.";
        if (FindRecordingWindow(plan.OutputPath) is not null)
            return "The output cannot replace a recording open in another AdTrim window.";
        if (_exports.Any(e => e.Key != view && Services.ExportSafety.SameFile(e.Value.OutputPath, plan.OutputPath)))
            return "Another window is exporting to this filename. Choose a different filename.";
        _exports[view] = plan;
        return null;
    }

    public bool IsExportDestination(string path) => _exports.Values.Any(e => Services.ExportSafety.SameFile(e.OutputPath, path));

    public void ReleaseExport(Views.ExportView view) => _exports.Remove(view);

    public MainWindow NewWindow(string? path = null)
    {
        if (_updatePreparing && _updateOwner is not null)
        {
            if (path is not null) _updateDeferredPaths.Add(Path.GetFullPath(path));
            return _updateOwner;
        }
        var window = new MainWindow(path);
        if (_updateStartupProtected) window.IsEnabled = false;
        window.Closed += (_, _) =>
        {
            if (MainWindow == window) MainWindow = Windows.OfType<MainWindow>().FirstOrDefault(w => w != window);
        };
        window.Show();
        return window;
    }

    public async Task<MainWindow> OpenRecordingAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (_updatePreparing && _updateOwner is not null) { _updateDeferredPaths.Add(path); return _updateOwner; }
        var existing = FindRecordingWindow(path);
        if (existing is not null) { existing.BringForward(); return existing; }
        var empty = Windows.OfType<MainWindow>().FirstOrDefault(w => w.CanReceiveRecording);
        if (empty is null)
        {
            empty = NewWindow(path);
            await empty.Ready;
        }
        else await empty.OpenFileAsync(path);
        empty.BringForward();
        return empty;
    }

    public MainWindow? FindRecordingWindow(string path, MainWindow? except = null)
        => Windows.OfType<MainWindow>().FirstOrDefault(w => w != except && w.OwnsRecording(path));

    public App()
    {
        // Capture unhandled exceptions to a log file. WinExe has no console
        // attached, so without this, startup crashes vanish silently and
        // the process just exits with code 0xE0434352.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("Dispatcher.UnhandledException", args.Exception);
            // One failed action must not close every recording window and end their exports.
            // Startup failures and a run of failures still end the app.
            var now = DateTime.UtcNow;
            _recentFailures.RemoveAll(t => now - t > TimeSpan.FromSeconds(30));
            _recentFailures.Add(now);
            var window = Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive) ?? Windows.OfType<MainWindow>().FirstOrDefault();
            if (_recentFailures.Count > 3 || window?.DataContext is not ViewModels.MainViewModel vm) return;
            vm.Banner = new Models.BannerInfo(Models.StatusKind.Danger, "That action did not finish.",
                $"AdTrim ran into an unexpected problem: {args.Exception.Message} Your saved edits are not affected. "
                + "If something looks wrong, close and reopen this recording.",
                new[] { new Models.BannerAction("Show log", () => RevealCrashLog()) });
            args.Handled = true;
        };
        SessionEnding += (_, _) => { foreach (var window in Windows.OfType<MainWindow>()) window.SaveForSessionEnd(); };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
    }

    internal static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdTrim", "crash-log.txt");

    private readonly List<DateTime> _recentFailures = new();

    private static void RevealCrashLog()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{CrashLogPath}\""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
    }

    internal static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            // Keep the newest entries only; the previous log stays beside it once.
            if (File.Exists(CrashLogPath) && new FileInfo(CrashLogPath).Length > 1_048_576)
                File.Move(CrashLogPath, CrashLogPath + ".old", overwrite: true);
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(CrashLogPath, msg);
        }
        catch { /* nothing else to do */ }
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            if (await RunUpdateModeAsync(e.Args)) return;
            await OnStartupInnerAsync(e);
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnStartup", ex);
            MessageBox.Show(
                $"AdTrim failed to start.\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Full details written to:\n{CrashLogPath}",
                "Startup failure", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task OnStartupInnerAsync(StartupEventArgs e)
    {
        Services.UpdateResume? resume = null;
        string? resumeDirectory = null;
        if (e.Args.Length == 2 && e.Args[0] == "--resume-update")
        {
            resumeDirectory = Services.UpdateStaging.ValidateDirectory(Services.UserPreferences.DataDirectory, e.Args[1]);
            resume = Services.UpdateInstallation.ReadResume(Services.UserPreferences.DataDirectory, resumeDirectory, AppVersion.Numeric);
        }
        var path = resume is not null ? resume.Recordings.FirstOrDefault() : e.Args.Length > 0 ? e.Args[0] : null;
        // A relative path means this folder, not the folder of an AdTrim that is already running.
        if (resume is null && !string.IsNullOrWhiteSpace(path))
            try { path = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        // Keep window routing and shared preferences in one process.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (!createdNew)
        {
            TrySendPathToExistingInstance(path ?? "");
            if (resume is not null)
            {
                foreach (var extra in resume.Recordings.Skip(1)) TrySendPathToExistingInstance(extra);
                // This copy is not the app that opens them: the running copy confirms once its windows are ready.
                if (Services.UpdateInstallation.TakeStartedMarkerRequest() is { } marker)
                    TrySendPathToExistingInstance(Services.UpdateInstallation.StartedRequestPrefix + marker);
            }
            Shutdown();
            return;
        }

        // While files are being replaced, hand the request to the update window and exit at once:
        // this process must not keep AdTrim.exe open while Setup replaces it.
        if (resume is null && Services.UpdateInstallation.IsUpdating(Services.UserPreferences.DataDirectory))
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            if (!Services.UpdateInstallation.TrySendToHelper(Services.UserPreferences.DataDirectory, path))
                WriteCrashLog("Update", new IOException("AdTrim was opened during an update, and the update window did not answer."));
            Shutdown();
            return;
        }
        _resumeDirectory = resumeDirectory;


        // Start the pipe server so future launches can hand off to us.
        _pipeServerCts = new CancellationTokenSource();
        var startedMarker = resume is null ? null : Services.UpdateInstallation.TakeStartedMarkerRequest();
        _ = ProtectUpdateStartupAsync(startedMarker, _pipeServerCts.Token);
        _ = Task.Run(() => RunPipeServerAsync(_pipeServerCts.Token));

        // Open MainWindow - StartupUri is replaced because we want to control bootstrap order.
        var win = NewWindow(path);
        if (resume is not null)
        {
            await win.Ready;
            foreach (var extra in resume.Recordings.Skip(1)) await OpenRecordingAsync(extra);
            // Ready windows prove this version starts; the update window can now finish.
            await ConfirmStartedWhenReadyAsync(startedMarker);
        }
        if (resumeDirectory is not null) _ = CleanUpCompletedUpdateAsync(resumeDirectory);
        _ = Task.Run(() => RecoverExportsAsync(win, _pipeServerCts.Token));
        _ = CheckUpdatesAtStartupAsync(win, _pipeServerCts.Token);
        _ = Task.Run(CleanUpMediaCaches);
    }

    /// <summary>Writes the update's started marker once every recording window of this process has finished starting.</summary>
    internal async Task ConfirmStartedWhenReadyAsync(string? marker)
    {
        if (marker is null) return;
        try
        {
            await Task.WhenAll(Windows.OfType<MainWindow>().Select(w => w.Ready)).WaitAsync(TimeSpan.FromMinutes(10));
            if (Windows.OfType<MainWindow>().Any()) Services.UpdateInstallation.WriteStartedMarker(marker);
        }
        catch (Exception ex) { WriteCrashLog("Update start confirmation", ex); }
    }

    private static async Task CleanUpCompletedUpdateAsync(string directory)
    {
        try
        {
            // The update window still runs from this folder until it has removed its backup and closed.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (Services.UpdateInstallation.IsUpdating(Services.UserPreferences.DataDirectory) && waited.Elapsed < TimeSpan.FromMinutes(10))
                await Task.Delay(500);
            await Services.UpdateStaging.RemoveDirectoryAsync(Services.UserPreferences.DataDirectory, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteCrashLog("Update cleanup", ex); }
    }

    // Thumbnail and audio-level caches for recordings not opened in months, and formats this
    // version no longer reads. Small, local, and never touches recordings or projects.
    private void CleanUpMediaCaches()
    {
        try
        {
            // Downloads and jobs no update window is using; a running update keeps its folder.
            if (!Services.UpdateInstallation.IsUpdating(Services.UserPreferences.DataDirectory))
                Services.UpdateStaging.CleanUpAbandoned(Services.UserPreferences.DataDirectory, StartedUtc, _resumeDirectory);
        }
        catch (Exception ex) { WriteCrashLog("Update cleanup", ex); }
        try
        {
            Services.MediaCacheCleanup.Run(Services.ThumbnailService.DefaultCacheRoot, Services.ThumbnailService.CurrentFolderSuffix,
                Services.AudioLevelService.DefaultCacheRoot, Services.AudioLevelService.CacheVersion,
                Services.MediaCacheCleanup.LegacyWaveformRoot, DateTime.UtcNow);
        }
        catch (Exception ex) { WriteCrashLog("Cache cleanup", ex); }
    }

    private async Task CheckUpdatesAtStartupAsync(MainWindow window, CancellationToken ct)
    {
        using var automatic = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _automaticUpdateCts = automatic;
        ct = automatic.Token;
        try
        {
            // Yield first so window creation never waits on this, and turning checks off at once
            // still cancels before any request.
            await Task.Yield();
            if (!AutomaticUpdatesEnabled) return;
            // The last verified result shows immediately; the fresh check (two small files, about
            // 0.2 s) runs alongside opening the recording rather than after it.
            await Updates.LoadCachedAsync(ct);
            if (!AutomaticUpdatesEnabled) return;
            await Updates.CheckAsync(ct);
            // Only an announcement waits for the window: never mid-open, never once the user has started.
            await window.Ready.WaitAsync(ct);
            if (ct.IsCancellationRequested || !AutomaticUpdatesEnabled || UpdateStartupInterrupted || UpdateDialogOpen || !Updates.AnnouncementDue) return;
            var active = Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive && w.CanAnnounceUpdate);
            if (active is not null && Windows.OfType<MainWindow>().All(w => w.CanAnnounceUpdate))
                await active.ShowUpdateAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { WriteCrashLog("Update check", ex); }
        finally { _automaticUpdateCts = null; }
    }

    private async Task RecoverExportsAsync(MainWindow window, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var remaining = await Services.ExportWorkspace.RecoverAsync(ct: ct);
            if (remaining == 0 || ct.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (window.DataContext is ViewModels.MainViewModel vm)
                    vm.Banner = new Models.BannerInfo(Models.StatusKind.Warning,
                        "Temporary export files remain.",
                        "Some files could not be cleaned up. AdTrim will retry the next time it starts.",
                        Array.Empty<Models.BannerAction>());
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            WriteCrashLog("Export cleanup", ex);
            if (!ct.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (window.DataContext is ViewModels.MainViewModel vm)
                        vm.Banner = new Models.BannerInfo(Models.StatusKind.Warning,
                            "Temporary export cleanup could not finish.",
                            "AdTrim will retry the next time it starts.", Array.Empty<Models.BannerAction>());
                });
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _pipeServerCts?.Cancel();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owner */ }
        _singleInstanceMutex?.Dispose();
    }

    private static void TrySendPathToExistingInstance(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(path);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            var acknowledgement = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (acknowledgement != "OK") throw new IOException("The running app did not accept the request.");
        }
        catch
        {
            MessageBox.Show("AdTrim could not open a window. Please try again.", "Could not open AdTrim", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var path = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (path is null) continue;

                _ = Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var app = (App)Current;
                    if (path.StartsWith(Services.UpdateInstallation.StartedRequestPrefix, StringComparison.Ordinal))
                    {
                        _ = app.ConfirmStartedWhenReadyAsync(path[Services.UpdateInstallation.StartedRequestPrefix.Length..]);
                        return;
                    }
                    if (app.DeferForUpdate(path)) return;
                    if (string.IsNullOrEmpty(path)) app.NewWindow();
                    else _ = app.OpenRecordingAsync(path);

                }));
                using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("OK");
            }
            catch (OperationCanceledException) { return; }
            catch { /* per-iteration failure: log later, keep listening */ }
        }
    }
}
