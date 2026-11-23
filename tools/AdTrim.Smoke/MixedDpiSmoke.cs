using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AdTrim;

internal static partial class Program
{
    // Review item C43: a window closed on one monitor must reopen there, at the same size and place,
    // when the monitors use different scaling. Everything is measured in physical pixels, which is
    // what the user sees; WPF's own coordinates change meaning between monitors.
    private static int RunMixedDpiSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", Path.GetFullPath(directory));
        var monitors = DpiMonitors();
        foreach (var m in monitors)
            Console.WriteLine($"Monitor: work {m.Work.Left},{m.Work.Top} {m.Work.Right},{m.Work.Bottom} at {m.Dpi} dpi ({m.Dpi * 100 / 96}%)");
        if (monitors.Count < 2 || monitors.Select(m => m.Dpi).Distinct().Count() < 2)
        {
            Console.Error.WriteLine("Needs two monitors with different scaling (for example 100% and 150%).");
            return 2;
        }
        var app = new App();
        app.InitializeComponent();
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                async Task<MainWindow> Open()
                {
                    var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                    await Task.Delay(300);
                    return window;
                }
                async Task Close(MainWindow window)
                {
                    window.Close();
                    for (int i = 0; i < 100 && window.IsVisible; i++) await Task.Delay(20);
                    Require(!window.IsVisible, "Window closes after saving its place");
                }
                // A normal window on every monitor, coming from every other monitor.
                foreach (var from in monitors)
                foreach (var to in monitors.Where(m => m.Handle != from.Handle))
                {
                    string route = $"{from.Dpi * 100 / 96}% to {to.Dpi * 100 / 96}%";
                    var window = await Open();
                    var hwnd = new WindowInteropHelper(window).Handle;
                    // Start on the first monitor, then move to the second as a user would drag it.
                    await PlaceOn(hwnd, from, 0.55);
                    var target = await PlaceOn(hwnd, to, 0.6);
                    Require(DpiMonitorOf(hwnd) == to.Handle, $"{route}: the window moved onto the target monitor");
                    await Close(window);

                    var reopened = await Open();
                    var reopenedHwnd = new WindowInteropHelper(reopened).Handle;
                    DpiGetWindowRect(reopenedHwnd, out var rect);
                    Console.WriteLine($"{route}: saved at {target.Left},{target.Top} {target.Width}x{target.Height}; reopened at {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
                    Require(reopened.WindowState == WindowState.Normal && DpiMonitorOf(reopenedHwnd) == to.Handle,
                        $"{route}: the window reopens on the monitor it was closed on");
                    Require(rect.Left >= to.Work.Left && rect.Top >= to.Work.Top && rect.Right <= to.Work.Right && rect.Bottom <= to.Work.Bottom,
                        $"{route}: the reopened window is fully inside that monitor's work area");
                    Require(Math.Abs(rect.Right - rect.Left - target.Width) <= 4 && Math.Abs(rect.Bottom - rect.Top - target.Height) <= 4,
                        $"{route}: the reopened window has the size it was closed at");
                    Require(Math.Abs(rect.Left - target.Left) <= 4 && Math.Abs(rect.Top - target.Top) <= 4,
                        $"{route}: the reopened window is where it was closed");
                    await Close(reopened);
                }
                // Maximized on each monitor.
                foreach (var to in monitors)
                {
                    string where = $"{to.Dpi * 100 / 96}%";
                    var window = await Open();
                    var hwnd = new WindowInteropHelper(window).Handle;
                    await PlaceOn(hwnd, to, 0.6);
                    ((Button)window.FindName("MaxButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(300);
                    RequireMaximized(window);
                    await Close(window);
                    var reopened = await Open();
                    var reopenedHwnd = new WindowInteropHelper(reopened).Handle;
                    Require(DpiMonitorOf(reopenedHwnd) == to.Handle, $"Maximized at {where}: the window reopens on that monitor");
                    RequireMaximized(reopened);
                    // Restoring returns to a normal window on the same monitor, not a full-screen-sized one.
                    ((Button)reopened.FindName("MaxButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(300);
                    DpiGetWindowRect(reopenedHwnd, out var restored);
                    Require(reopened.WindowState == WindowState.Normal && DpiMonitorOf(reopenedHwnd) == to.Handle
                        && restored.Right - restored.Left < to.Work.Right - to.Work.Left,
                        $"Maximized at {where}: restoring gives a smaller window on the same monitor");
                    await Close(reopened);
                }
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return result;
    }

    private sealed record DpiMonitor(IntPtr Handle, DpiRect Work, uint Dpi);
    private sealed record Placement(int Left, int Top, int Width, int Height);

    // Puts the window inside the monitor's work area at the given fraction of its size, in physical pixels.
    private static async Task<Placement> PlaceOn(IntPtr hwnd, DpiMonitor monitor, double fraction)
    {
        int width = (int)((monitor.Work.Right - monitor.Work.Left) * fraction), height = (int)((monitor.Work.Bottom - monitor.Work.Top) * fraction);
        int left = monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left - width) / 3, top = monitor.Work.Top + (monitor.Work.Bottom - monitor.Work.Top - height) / 3;
        DpiSetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010);
        await Task.Delay(400);
        // Crossing to a monitor with other scaling resizes the window once; place it again on arrival.
        DpiSetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010);
        await Task.Delay(400);
        DpiGetWindowRect(hwnd, out var rect);
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static List<DpiMonitor> DpiMonitors()
    {
        var list = new List<DpiMonitor>();
        DpiEnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new DpiMonitorInfo { Size = Marshal.SizeOf<DpiMonitorInfo>() };
            DpiGetMonitorInfo(monitor, ref info);
            DpiGetDpiForMonitor(monitor, 0, out var dpi, out _);
            list.Add(new(monitor, info.Work, dpi));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IntPtr DpiMonitorOf(IntPtr hwnd) => DpiMonitorFromWindow(hwnd, 2);

    [StructLayout(LayoutKind.Sequential)] private struct DpiRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct DpiMonitorInfo { public int Size; public DpiRect Monitor, Work; public uint Flags; }
    private delegate bool DpiMonitorEnum(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")] private static extern bool DpiEnumDisplayMonitors(IntPtr hdc, IntPtr clip, DpiMonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool DpiGetMonitorInfo(IntPtr monitor, ref DpiMonitorInfo info);
    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")] private static extern int DpiGetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")] private static extern IntPtr DpiMonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool DpiSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool DpiGetWindowRect(IntPtr hwnd, out DpiRect rect);
}
