using AdTrim.Services;
using Xunit;
using System.Runtime.InteropServices;

namespace AdTrim.Tests;

public class ExportSafetyTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [SkippableFact]
    public void HardLink_CannotReplaceSourceUnderAnotherName()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows file identity check.");
        var directory = Path.Combine(AppContext.BaseDirectory, "hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.mp4");
            var alias = Path.Combine(directory, "alias.mp4");
            File.WriteAllText(source, "source data");
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            Assert.Throws<ExportException>(() => ExportSafety.EnsureDifferentFiles(source, alias));
            Assert.Equal("source data", File.ReadAllText(source));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EquivalentPaths_CannotReplaceSource()
    {
        string source = Path.Combine(Path.GetTempPath(), "recording.mp4");
        Assert.Throws<ExportException>(() => ExportSafety.EnsureDifferentFiles(source, source.ToUpperInvariant()));
        Assert.Throws<ExportException>(() => ExportSafety.EnsureDifferentFiles(source, Path.Combine(Path.GetDirectoryName(source)!, ".", "recording.mp4")));
    }
}
