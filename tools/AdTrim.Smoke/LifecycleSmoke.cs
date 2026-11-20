using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AdTrim;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    private static int RunLifecycleSmoke()
    {
        var source = PrepareFixture().GetAwaiter().GetResult();
        var directory = Path.Combine(Path.GetDirectoryName(source)!, "lifecycle-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"AddSourceExportComment\":false}");
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(source) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                window.Show();
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                var vm = (MainViewModel)window.DataContext;
                DispatcherTimer Timer() => window.Test.RewindTimer!;
                void StartRewind()
                {
                    vm.PlayheadUs = 8_000_000;
                    window.Test.Shuttle(vm, Key.J);
                    Require(Timer().IsEnabled, "J starts the editor rewind timer");
                }
                void RequireStopped(string reason)
                {
                    var position = vm.PlayheadUs;
                    window.Test.RewindTick();
                    Require(!Timer().IsEnabled && vm.PlayheadUs == position, reason);
                }

                StartRewind();
                window.Test.RewindTick();
                Require(vm.PlayheadUs < 8_000_000, "Rewind still moves the playhead in the editor");
                window.Test.Export();
                await WaitForExportSetupAsync(window);
                RequireStopped("Opening export stops rewind and late callbacks cannot move the hidden editor");
                ((ExportView)((ContentControl)window.FindName("ExportHost")).Content).Close();
                StartRewind();
                vm.IsBusy = true;
                RequireStopped("A busy editor rejects a late rewind callback");
                vm.IsBusy = false;
                StartRewind();
                var nextSource = Path.Combine(Path.GetDirectoryName(source)!, "second.mp4");
                File.Copy(source, nextSource);
                await window.OpenFileAsync(nextSource);
                vm = (MainViewModel)window.DataContext;
                RequireStopped("Switching recordings stops rewind rather than carrying it into the next file");
                StartRewind();
                window.Test.CloseProject();
                for (int i = 0; i < 250 && vm.IsFileLoaded; i++) await Task.Delay(20);
                Require(!vm.IsFileLoaded, "Close project returns to the empty editor");
                RequireStopped("Closing a project stops its rewind timer");
                await window.OpenFileAsync(source);
                vm = (MainViewModel)window.DataContext;
                StartRewind();
                window.Close();
                for (int i = 0; i < 250 && window.IsVisible; i++) await Task.Delay(20);
                Require(!window.IsVisible, "Recording window finishes closing");
                RequireStopped("Closing a recording window stops its timer and ignores late callbacks");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                if (window is not null)
                {
                    window.Test.StopRewind();
                    window.Test.AllowClose();
                    if (window.IsVisible) window.Close();
                }
                app.Shutdown();
            }
        }));
        app.Run();
        return result;
    }
}
