using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AdTrim.Controls;

/// <summary>
/// WPF host for libmpv's video output. Creates a child native window that
/// mpv renders into (via mpv's <c>wid</c> option). WPF manages sizing and
/// positioning; mpv owns what's painted inside the HWND.
///
/// <para>The rendering surface is a child HWND, so HwndHost airspace rules
/// apply: XAML sibling elements in adjacent regions (e.g. the dual-pane
/// layout's "No frame available" overlay) work fine via the sibling-swap
/// pattern. Overlays on top of the video plane are no longer banned.</para>
/// </summary>
public sealed class MpvVideoView : HwndHost
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    public IntPtr Hwnd { get; private set; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Use the built-in `static` window class - it's a benign empty surface
        // that won't paint over what mpv draws. `WS_CLIPCHILDREN` keeps WPF
        // from repainting the area mpv owns.
        Hwnd = CreateWindowExW(
            exStyle: 0,
            className: "static",
            windowName: "AdTrim.MpvVideoView",
            style: WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            x: 0, y: 0, w: 0, h: 0,
            parent: hwndParent.Handle,
            menu: IntPtr.Zero, instance: IntPtr.Zero, param: IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to create MPV video child window (Win32 error {Marshal.GetLastWin32Error()}).");
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (Hwnd != IntPtr.Zero)
        {
            DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}
