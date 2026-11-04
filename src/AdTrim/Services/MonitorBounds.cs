using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AdTrim.Services;

internal static class MonitorBounds
{
    public static Rect WorkArea(Window window, Rect? desired = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var bounds = desired ?? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        var a = transform.Transform(bounds.TopLeft);
        var b = transform.Transform(bounds.BottomRight);
        var rectangle = new NativeRect { Left = (int)a.X, Top = (int)a.Y, Right = (int)b.X, Bottom = (int)b.Y };
        var monitor = desired is null ? MonitorFromWindow(handle, 2) : MonitorFromRect(ref rectangle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        transform.Invert();
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
            transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
    // Windows keeps an invisible resize frame outside the maximized work area.
    // Custom chrome extends content into it, so inset content by that frame.
    public static Thickness MaximizedInsets(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) || !GetWindowRect(handle, out var bounds))
            return new Thickness(0);
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Thickness(Math.Max(0, info.Work.Left - bounds.Left) / dpi.DpiScaleX,
            Math.Max(0, info.Work.Top - bounds.Top) / dpi.DpiScaleY,
            Math.Max(0, bounds.Right - info.Work.Right) / dpi.DpiScaleX,
            Math.Max(0, bounds.Bottom - info.Work.Bottom) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
