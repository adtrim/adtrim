using System.IO;

namespace AdTrim.Services;

/// <summary>
/// Paths for native code that opens files through the classic Win32 limit (libmpv). At 260
/// characters or more, the extended-length form opens the file whether or not Windows'
/// long-path setting is on. Shorter paths pass through unchanged.
/// </summary>
public static class NativePath
{
    private const int MaxPath = 260;

    public static string ForNative(string path)
    {
        if (path.Length < MaxPath || path.StartsWith(@"\\?\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return path;
        // The extended form skips normalization, so separators and dot segments are resolved first.
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
