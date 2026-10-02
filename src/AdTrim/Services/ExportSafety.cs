using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AdTrim.Services;

public static class ExportSafety
{
    public static void EnsureDifferentFiles(string source, string destination)
    {
        if (SameFile(source, destination))
            throw new ExportException("The output cannot replace the source recording, including through another path.");
    }

    public static bool SameFile(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return true;
        if (!File.Exists(source) || !File.Exists(destination) || !OperatingSystem.IsWindows()) return false;
        using var a = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = File.OpenHandle(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(a, out var ai) || !GetFileInformationByHandle(b, out var bi))
            throw new ExportException("Could not verify file identities.");
        return ai.Volume == bi.Volume && ai.IndexHigh == bi.IndexHigh && ai.IndexLow == bi.IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
