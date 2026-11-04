using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AdTrim;
using AdTrim.Services;

internal static partial class Program
{
    private static int RunWindowStateSmoke(string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", Path.GetFullPath(directory));
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
                    await Task.Delay(150);
                    return window;
                }
                async Task ClickMax(MainWindow window)
                {
                    ((Button)window.FindName("MaxButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(150);
                }
                async Task Close(MainWindow window)
                {
                    window.Close();
                    for (int i = 0; i < 100 && window.IsVisible; i++) await Task.Delay(20);
                    Require(!window.IsVisible, "Window closes after saving preferences");
                }
                var first = await Open();
                Require(first.WindowState == WindowState.Normal, "Fresh preferences start with a normal window");
                var normal = new Rect(first.Left, first.Top, first.Width, first.Height);
                for (int i = 0; i < 3; i++)
                {
                    await ClickMax(first);
                    RequireMaximized(first);
                    await ClickMax(first);
                    Require(first.WindowState == WindowState.Normal && Math.Abs(first.Width - normal.Width) < 1
                        && Math.Abs(first.Height - normal.Height) < 1, "Restore returns to the original window size");
                }
                var hwnd = new WindowInteropHelper(first).Handle;
                WindowStateSendMessage(hwnd, 0x00A3, new IntPtr(2), IntPtr.Zero);
                await Task.Delay(150);
                RequireMaximized(first);
                WindowStateSendMessage(hwnd, 0x00A3, new IntPtr(2), IntPtr.Zero);
                await Task.Delay(150);
                Require(first.WindowState == WindowState.Normal, "Title-bar double-click restores native maximized state");
                WindowStateSendMessage(hwnd, 0x0112, new IntPtr(0xF030), IntPtr.Zero);
                await Task.Delay(150);
                RequireMaximized(first);
                WindowStateSendMessage(hwnd, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                await Task.Delay(150);
                Require(first.WindowState == WindowState.Minimized, "Native minimize minimizes the window");
                first.BringForward();
                await Task.Delay(150);
                RequireMaximized(first);
                Capture(first, Path.Combine(directory, "maximized.png"));
                SystemCommands.MinimizeWindow(first);
                await Task.Delay(150);
                await Close(first);
                var saved = await UserPreferences.LoadAsync();
                Require(saved.IsMaximized == true && Math.Abs(saved.Width - normal.Width) < 1
                    && Math.Abs(saved.Height - normal.Height) < 1, "Save keeps maximized state separate from restore dimensions");
                var reopened = await Open();
                RequireMaximized(reopened);
                await ClickMax(reopened);
                Require(reopened.WindowState == WindowState.Normal && Math.Abs(reopened.Width - normal.Width) < 1,
                    "Reopened maximized window restores on the first button click");
                await Close(reopened);
                var restored = await Open();
                Require(restored.WindowState == WindowState.Normal, "Explicitly restored state survives reopening");
                WindowStateSendMessage(new WindowInteropHelper(restored).Handle, 0x0112, new IntPtr(0xF030), IntPtr.Zero);
                await Task.Delay(150);
                var info = GetWindowMonitor(restored);
                await Close(restored);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(restored);
                await (new UserPreferences { Left = info.Work.Left / dpi.DpiScaleX, Top = info.Work.Top / dpi.DpiScaleY,
                    Width = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                    Height = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY }).SaveAsync();
                var legacy = await Open();
                RequireMaximized(legacy);
                await ClickMax(legacy);
                Require(legacy.WindowState == WindowState.Normal && Math.Abs(legacy.Width - normal.Width) < 1,
                    "Legacy fake maximize migrates and restores to a usable size");
                await Close(legacy);
                var monitors = new List<WindowRect>();
                WindowMonitorCallback callback = (IntPtr monitor, IntPtr dc, ref WindowRect bounds, IntPtr data) =>
                {
                    var monitorInfo = new WindowMonitorInfo { Size = Marshal.SizeOf<WindowMonitorInfo>() };
                    if (WindowStateGetMonitorInfo(monitor, ref monitorInfo)) monitors.Add(monitorInfo.Work);
                    return true;
                };
                WindowStateEnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                var moving = await Open();
                foreach (var work in monitors)
                {
                    WindowStateSetWindowPos(new WindowInteropHelper(moving).Handle, IntPtr.Zero,
                        work.Left + 30, work.Top + 30, 1280, 720, 0x0014);
                    await Task.Delay(150);
                    await ClickMax(moving);
                    RequireMaximized(moving);
                    await ClickMax(moving);
                }
                Console.WriteLine($"Verified monitors: {monitors.Count}");
                await Close(moving);
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return result;
    }

    private static void RequireMaximized(MainWindow window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Require(window.WindowState == WindowState.Maximized && WindowStateIsZoomed(hwnd),
            "Windows recognizes the window as maximized");
        var info = GetWindowMonitor(window);
        WindowStateGetWindowRect(hwnd, out var rect);
        Console.WriteLine($"Window: {rect.Left},{rect.Top} {rect.Right},{rect.Bottom}; work: {info.Work.Left},{info.Work.Top} {info.Work.Right},{info.Work.Bottom}");
        var layout = (FrameworkElement)window.FindName("WindowLayoutGrid");
        var topLeft = layout.PointToScreen(new Point());
        var bottomRight = layout.PointToScreen(new Point(layout.ActualWidth, layout.ActualHeight));
        Require(Math.Abs(topLeft.X - info.Work.Left) <= 1 && Math.Abs(topLeft.Y - info.Work.Top) <= 1
            && Math.Abs(bottomRight.X - info.Work.Right) <= 1 && Math.Abs(bottomRight.Y - info.Work.Bottom) <= 1,
            "Title bar and status bar fill the visible work area without clipping or a border gap");
        Require(((Button)window.FindName("MaxButton")).ToolTip?.ToString() == "Restore down",
            "Button reflects native maximized state");
    }

    private static WindowMonitorInfo GetWindowMonitor(Window window)
    {
        var info = new WindowMonitorInfo { Size = Marshal.SizeOf<WindowMonitorInfo>() };
        Require(WindowStateGetMonitorInfo(WindowStateMonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref info),
            "Current monitor is available");
        return info;
    }

    private delegate bool WindowMonitorCallback(IntPtr monitor, IntPtr dc, ref WindowRect bounds, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowStateEnumDisplayMonitors(IntPtr dc, IntPtr clip, WindowMonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowStateSetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowMonitorInfo { public int Size; public WindowRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr WindowStateSendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "IsZoomed")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowStateIsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowStateGetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")] private static extern IntPtr WindowStateMonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WindowStateGetMonitorInfo(IntPtr monitor, ref WindowMonitorInfo info);
}
