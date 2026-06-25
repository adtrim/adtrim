using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AdTrim;

public partial class App : Application
{
    private const string MutexName  = "Global\\AdTrim.SingleInstance.v1";
    private const string PipeName   = "AdTrim.OpenFile.v1";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeServerCts;

    /// <summary>Path passed via launch args, picked up by MainWindow.OnLoaded.</summary>
    public static string? PendingOpenPath { get; set; }

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
            if (!string.IsNullOrEmpty(path)) TrySendPathToExistingInstance(path);
            Shutdown();
            return;
        }

        if (!string.IsNullOrEmpty(path)) PendingOpenPath = path;

        // Start the pipe server so future launches can hand off to us.
        _pipeServerCts = new CancellationTokenSource();
        _ = Task.Run(() => RunPipeServerAsync(_pipeServerCts.Token));

        // Open MainWindow - StartupUri is replaced because we want to control bootstrap order.
        var win = new MainWindow();
        win.Show();
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
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            var bytes = Encoding.UTF8.GetBytes(path);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
            // Existing instance died mid-handoff - accept silent drop in V1.
        }
    }

    private static async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var path = (await reader.ReadToEndAsync(ct).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(path)) continue;

                _ = Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Current.MainWindow is MainWindow w)
                    {
                        if (w.WindowState == WindowState.Minimized)
                            w.WindowState = WindowState.Normal;
                        w.Activate();
                        _ = w.OpenFileAsync(path);
                    }
                }));
            }
            catch (OperationCanceledException) { return; }
            catch { /* per-iteration failure: log later, keep listening */ }
        }
    }
}
