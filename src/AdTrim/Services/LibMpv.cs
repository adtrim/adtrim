using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AdTrim.Services;

/// <summary>
/// Minimal P/Invoke surface against <c>libmpv-2.dll</c> (mpv ≥ 0.34).
/// Just the entry points we need; the full API is at
/// https://github.com/mpv-player/mpv/blob/master/include/client.h.
///
/// <para>The bundled DLL lives at <c>binaries/mpv/win-x64/libmpv-2.dll</c>
/// relative to <see cref="AppContext.BaseDirectory"/>. We pre-load it via
/// <see cref="EnsureLoaded"/> so the unqualified P/Invoke target resolves
/// from that exact path rather than searching the Windows DLL paths.</para>
/// </summary>
internal static class LibMpv
{
    private const string DllName = "libmpv-2.dll";

    public enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6,
    }

    public enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        ClientMessage = 16,
        VideoReconfig = 17,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
        QueueOverflow = 24,
        Hook = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public MpvEventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr Name;     // const char*
        public MpvFormat Format;
        public IntPtr Data;     // depends on Format
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, ref double data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, ref int data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, out double data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, out int data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    private static bool _loaded;
    private static readonly object _loadLock = new();

    /// <summary>
    /// Pre-load libmpv from the bundled path. Without this, .NET's P/Invoke
    /// would search Windows DLL paths and might pick up an unrelated mpv
    /// install (or fail entirely). Returns the resolved path, or throws.
    /// </summary>
    public static string EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_loaded) return ResolveBundledPath();

            var path = ResolveBundledPath();
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"libmpv-2.dll not found at '{path}'. " +
                    "See binaries/README.md (mpv section) for install instructions.",
                    path);

            // LoadLibraryW pins the DLL into the process so subsequent P/Invokes
            // resolve against it.
            var handle = LoadLibraryW(path);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"LoadLibrary failed for '{path}' (Win32 error {Marshal.GetLastWin32Error()}).");

            _loaded = true;
            return path;
        }
    }

    private static string ResolveBundledPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "binaries", "mpv", "win-x64", "libmpv-2.dll");
        if (File.Exists(bundled)) return bundled;

        var devOverride = Environment.GetEnvironmentVariable("ADTRIM_MPV_DIR");
        if (!string.IsNullOrEmpty(devOverride))
        {
            var p = Path.Combine(devOverride, "libmpv-2.dll");
            if (File.Exists(p)) return p;
        }
        return bundled;   // path that the error message refers to
    }

    /// <summary>Send a string-array command to mpv (e.g. ["loadfile", path]).</summary>
    public static int Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        var allocated = new List<IntPtr>(args.Length);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                var ptr = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                ptrs[i] = ptr;
                allocated.Add(ptr);
            }
            ptrs[args.Length] = IntPtr.Zero;
            return mpv_command(ctx, ptrs);
        }
        finally
        {
            foreach (var p in allocated) Marshal.FreeHGlobal(p);
        }
    }

    public static string? UnmarshalUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        if (len == 0) return string.Empty;
        var buf = new byte[len];
        Marshal.Copy(ptr, buf, 0, len);
        return Encoding.UTF8.GetString(buf);
    }
}
