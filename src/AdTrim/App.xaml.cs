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

    public MainWindow NewWindow(string? path = null)
    {
        var window = new MainWindow(path);
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
        // If invoked with a path and a previous instance owns the mutex, hand off and exit.
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
