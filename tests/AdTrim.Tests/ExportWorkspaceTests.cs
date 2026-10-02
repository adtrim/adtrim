using System.Text.Json.Nodes;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class ExportWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "cleanup-test-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(_root, "registry");
    private string Temp => Path.Combine(_root, "temp");
    private ExportWorkspace Create()
    {
        Directory.CreateDirectory(_root);
        return ExportWorkspace.Create(Path.Combine(_root, "finished.mp4"), Registry, Temp);
    }

    [Fact]
    public async Task RecoverySkipsActiveExportThenRemovesItsAbandonedFiles()
    {
        var workspace = Create();
        File.WriteAllText(Path.Combine(workspace.TempDirectory, "seg_000.mp4"), "temporary video");
        File.WriteAllText(workspace.StagingPath, "unfinished output");
        var source = Path.Combine(_root, "recording.mp4");
        File.WriteAllText(source, "source sentinel");
        Assert.Equal(0, await ExportWorkspace.RecoverAsync(Registry, tempRoot: Temp));
        Assert.True(File.Exists(workspace.StagingPath));
        workspace.Dispose(); // Simulate the process lease being released without normal cleanup.
        Assert.Equal(0, await ExportWorkspace.RecoverAsync(Registry, tempRoot: Temp));
        Assert.False(Directory.Exists(workspace.TempDirectory));
        Assert.False(File.Exists(workspace.StagingPath));
        Assert.Empty(Directory.EnumerateDirectories(Registry));
        Assert.Equal("source sentinel", File.ReadAllText(source));
    }

    [Fact]
    public async Task LockedFilesKeepRecoveryRecordUntilNextSuccessfulAttempt()
    {
        var workspace = Create();
        var segment = Path.Combine(workspace.TempDirectory, "seg_000.mp4");
        using (var locked = new FileStream(segment, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await workspace.CleanupAsync());
            Assert.Single(Directory.EnumerateFiles(Registry, "record.json", SearchOption.AllDirectories));
        }
        workspace.Dispose();
        Assert.Equal(0, await ExportWorkspace.RecoverAsync(Registry, tempRoot: Temp));
        Assert.False(File.Exists(segment));
    }

    [Fact]
    public async Task UnknownFilesAndNestedDirectoriesAreNeverRecursivelyDeleted()
    {
        using var workspace = Create();
        var sentinel = Path.Combine(workspace.TempDirectory, "personal.mp4");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateDirectory(Path.Combine(workspace.TempDirectory, "nested"));
        Assert.False(await workspace.CleanupAsync());
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(Path.Combine(workspace.TempDirectory, "nested")));
    }

    [Theory]
    [InlineData("StagingPath")]
    [InlineData("TempDirectory")]
    public async Task AlteredRecordCannotDeleteUnrelatedPaths(string property)
    {
        var workspace = Create();
        workspace.Dispose();
        var manifest = Directory.GetFiles(Registry, "record.json", SearchOption.AllDirectories).Single();
        var record = JsonNode.Parse(File.ReadAllText(manifest))!;
        var protectedPath = Path.Combine(_root, property == "StagingPath" ? "original.mp4" : "outside");
        if (property == "StagingPath") File.WriteAllText(protectedPath, "keep");
        else Directory.CreateDirectory(protectedPath);
        record[property] = protectedPath;
        File.WriteAllText(manifest, record.ToJsonString());
        Assert.Equal(1, await ExportWorkspace.RecoverAsync(Registry, tempRoot: Temp));
        Assert.True(File.Exists(protectedPath) || Directory.Exists(protectedPath));
        Assert.True(File.Exists(manifest));
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test path");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
