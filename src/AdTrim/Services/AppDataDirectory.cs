using System.IO;

namespace AdTrim.Services;

/// <summary>Per-user data folder for settings and caches. ADTRIM_DATA_DIR isolates test runs.</summary>
public static class AppDataDirectory
{
    public static string Path => Environment.GetEnvironmentVariable("ADTRIM_DATA_DIR")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdTrim");
}
