using System.Runtime.CompilerServices;

namespace AdTrim.Tests;

internal static class TestEnvironment
{
    // Every media test must run the FFmpeg that ships, whichever test runs first. The test output
    // has no bundled copy, so FfmpegRunner would otherwise use whatever ADTRIM_FFMPEG_DIR names
    // on this machine. Without the bundled tools the media tests skip or fail; they never use another build.
    [ModuleInitializer]
    internal static void UseBundledFfmpeg()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdTrim.sln")))
            {
                Environment.SetEnvironmentVariable("ADTRIM_FFMPEG_DIR", Path.Combine(directory.FullName, "binaries", "ffmpeg", "win-x64"));
                return;
            }
    }
}
