using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class UpdateBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adtrim-backup-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "install");
    private string Backup => Path.Combine(_root, "backup");

    public UpdateBackupTests()
    {
        // An older installation shares its folder with user data, so files outside the list must never move.
        Write("AdTrim.exe", "old app");
        Write(@"binaries\mpv\libmpv-2.dll", "old player");
        Write("Uninstall.exe", "old uninstaller");
        Write("preferences.json", "user settings");
        Write(@"updates\job\AdTrim-Setup-v9.9.9.exe", "downloaded installer");
        Write(UpdateBackup.ListName, "AdTrim.exe\r\nbinaries\\mpv\\libmpv-2.dll\r\n");
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Install, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private string ReadInstalled(string relative) => File.ReadAllText(Path.Combine(Install, relative));

    [Fact]
    public void AFailedUpdateIsUndoneToTheExactPreviousInstallation()
    {
        UpdateBackup.Create(Install, Backup);
        // The update replaced files, removed one, added one and listed it.
        Write("AdTrim.exe", "new app");
        File.Delete(Path.Combine(Install, @"binaries\mpv\libmpv-2.dll"));
        Write(@"binaries\new.dll", "added by the update");
        Write("Uninstall.exe", "new uninstaller");
        Write(UpdateBackup.ListName, "AdTrim.exe\r\nbinaries\\new.dll\r\n");

        UpdateBackup.Restore(Install, Backup);

        Assert.Equal("old app", ReadInstalled("AdTrim.exe"));
        Assert.Equal("old player", ReadInstalled(@"binaries\mpv\libmpv-2.dll"));
        Assert.Equal("old uninstaller", ReadInstalled("Uninstall.exe"));
        Assert.Equal("AdTrim.exe\r\nbinaries\\mpv\\libmpv-2.dll\r\n", ReadInstalled(UpdateBackup.ListName));
        Assert.False(File.Exists(Path.Combine(Install, @"binaries\new.dll")));
        Assert.Equal("user settings", ReadInstalled("preferences.json"));
        Assert.Equal("downloaded installer", ReadInstalled(@"updates\job\AdTrim-Setup-v9.9.9.exe"));
    }

    [Fact]
    public void TheBackupHoldsOnlyTheInstallationsOwnFiles()
    {
        UpdateBackup.Create(Install, Backup);
        var copied = Directory.GetFiles(Path.Combine(Backup, "files"), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Path.Combine(Backup, "files"), p)).Order().ToArray();
        Assert.Equal(new[] { "AdTrim.exe", @"binaries\mpv\libmpv-2.dll", UpdateBackup.ListName, "Uninstall.exe" }.Order(), copied);
    }

    [Fact]
    public void AnUpdateStoppedBeforeWritingItsListRemovesNothing()
    {
        UpdateBackup.Create(Install, Backup);
        Write("AdTrim.exe", "half written");
        File.Delete(Path.Combine(Install, UpdateBackup.ListName));
        UpdateBackup.Restore(Install, Backup);
        Assert.Equal("old app", ReadInstalled("AdTrim.exe"));
        Assert.Equal("user settings", ReadInstalled("preferences.json"));
    }

    [Fact]
    public void AnInstallationWithoutItsFileListIsNotBackedUpByGuesswork()
    {
        File.Delete(Path.Combine(Install, UpdateBackup.ListName));
        Assert.Throws<IOException>(() => UpdateBackup.Create(Install, Backup));
        Assert.False(Directory.Exists(Backup));
    }

    [Theory]
    [InlineData(@"..\outside.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"\rooted.exe")]
    [InlineData("forward/slash.exe")]
    [InlineData(@"binaries\..\..\escape.exe")]
    [InlineData("file.exe:stream")]
    public void PathsOutsideTheInstallationAreRejected(string line)
    {
        Write(UpdateBackup.ListName, "AdTrim.exe\r\n" + line + "\r\n");
        Assert.Throws<InvalidDataException>(() => UpdateBackup.Create(Install, Backup));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // Retry runs the installer whenever a backup folder exists, so a folder that exists must be whole.
    [Fact]
    public void ABackupInterruptedPartwayIsNeverMistakenForACompleteOne()
    {
        using (new FileStream(Path.Combine(Install, @"binaries\mpv\libmpv-2.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => UpdateBackup.Create(Install, Backup));
        Assert.False(Directory.Exists(Backup));

        UpdateBackup.Create(Install, Backup);
        Write("AdTrim.exe", "new app");
        UpdateBackup.Restore(Install, Backup);
        Assert.Equal("old app", ReadInstalled("AdTrim.exe"));
        Assert.Equal("old player", ReadInstalled(@"binaries\mpv\libmpv-2.dll"));
        Assert.False(Directory.Exists(Backup + ".partial"));
    }
}
