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
    public bool UpdateDialogOpen { get; set; }
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
        var window = new MainWindow(path);
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
            // Let WPF still terminate; we've captured what we need.
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
    }

    internal static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdTrim", "crash-log.txt");

    internal static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(CrashLogPath, msg);
        }
        catch { /* nothing else to do */ }
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            OnStartupInner(e);
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

    private void OnStartupInner(StartupEventArgs e)
    {
        var path = e.Args.Length > 0 ? e.Args[0] : null;
        // Keep window routing and shared preferences in one process.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        if (!createdNew)
        {
            TrySendPathToExistingInstance(path ?? "");
            Shutdown();
            return;
        }


        // Start the pipe server so future launches can hand off to us.
        _pipeServerCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeServerAsync(_pipeServerCts.Token));

        // Open MainWindow - StartupUri is replaced because we want to control bootstrap order.
        var win = NewWindow(path);
        _ = Task.Run(() => RecoverExportsAsync(win, _pipeServerCts.Token));
        _ = CheckUpdatesAtStartupAsync(win, _pipeServerCts.Token);
    }

    private async Task CheckUpdatesAtStartupAsync(MainWindow window, CancellationToken ct)
    {
        using var automatic = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _automaticUpdateCts = automatic;
        ct = automatic.Token;
        try
        {
            if (!AutomaticUpdatesEnabled) return;
            await window.Ready.WaitAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!AutomaticUpdatesEnabled) return;
            await Updates.CheckAsync(ct);
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
