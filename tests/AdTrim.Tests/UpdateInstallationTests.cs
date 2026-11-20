using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

// The update helper replaces the installed program. What it reads from the update folder decides
// which folder it writes to, which version it accepts and which process it believes.
public sealed class UpdateInstallationTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "adtrim-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _job;

    public UpdateInstallationTests()
    {
        _install = Path.Combine(_data, "Programs", "AdTrim");
        Directory.CreateDirectory(_install);
        _job = UpdateStaging.CreateDirectory(_data);
    }
    public void Dispose() => Directory.Delete(_data, true);

    private void WriteJob(string version, string installDirectory)
        => UpdateInstallation.Write(_job, "job.json", new UpdateInstallJob(version, installDirectory, 1, 1));

    [Fact]
    public void AJobIsAcceptedOnlyForTheRegisteredInstallationAndANewerVersion()
    {
        WriteJob("2.5.0", _install);
        var job = UpdateInstallation.ReadJob(_data, _job, "2.4.9", _install);
        Assert.Equal("2.5.0", job.Version);
        // The same folder spelled differently is still the registered one.
        UpdateInstallation.ReadJob(_data, _job, "2.4.9", _install.ToUpperInvariant() + Path.DirectorySeparatorChar);

        // Never install over the same or a newer version.
        Assert.Throws<InvalidDataException>(() => UpdateInstallation.ReadJob(_data, _job, "2.5.0", _install));
        Assert.Throws<InvalidDataException>(() => UpdateInstallation.ReadJob(_data, _job, "3.0.0", _install));
        // A job cannot point the installer at any folder but the one AdTrim is registered in.
        Assert.Throws<InvalidDataException>(() => UpdateInstallation.ReadJob(_data, _job, "2.4.9", Path.Combine(_data, "Elsewhere")));
        WriteJob("2.5.0", @"Programs\AdTrim");
        Assert.Throws<InvalidDataException>(() => UpdateInstallation.ReadJob(_data, _job, "2.4.9", _install));
    }

    [Fact]
    public void AJobOutsideTheUpdateFolderIsRefused()
    {
        var outside = Path.Combine(_data, "not-updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        UpdateInstallation.Write(outside, "job.json", new UpdateInstallJob("2.5.0", _install, 1, 1));
        Assert.Throws<IOException>(() => UpdateInstallation.ReadJob(_data, outside, "2.4.9", _install));
        Assert.Throws<IOException>(() => UpdateInstallation.ReadJob(_data, Path.Combine(UpdateStaging.Root(_data), "named-folder"), "2.4.9", _install));
    }

    [Fact]
    public void AnEmptyOrOversizedJobIsRefused()
    {
        File.WriteAllText(Path.Combine(_job, "job.json"), "");
        Assert.Throws<IOException>(() => UpdateInstallation.ReadJob(_data, _job, "2.4.9", _install));
        File.WriteAllText(Path.Combine(_job, "job.json"), new string(' ', 16 * 1024 + 1));
        Assert.Throws<IOException>(() => UpdateInstallation.ReadJob(_data, _job, "2.4.9", _install));
    }

    // After an update AdTrim reopens the recordings that were open. The list is read by the new
    // version, so only that version may use it.
    [Fact]
    public void TheRecordingsToReopenAreKeptOnceEachForTheVersionBeingInstalled()
    {
        string first = Path.Combine(_data, "tv", "One.ts"), second = Path.Combine(_data, "tv", "Two.mp4");
        UpdateInstallation.WriteResume(_job, "2.5.0", [first, second, first.ToUpperInvariant()]);
        var resume = UpdateInstallation.ReadResume(_data, _job, "2.5.0");
        Assert.Equal(new[] { first, second }, resume.Recordings);
        Assert.Throws<IOException>(() => UpdateInstallation.ReadResume(_data, _job, "2.4.9"));

        UpdateInstallation.Write(_job, "resume.json", new UpdateResume("2.5.0", [@"tv\relative.ts"]));
        Assert.Throws<IOException>(() => UpdateInstallation.ReadResume(_data, _job, "2.5.0"));
    }

    [Fact]
    public void TooManyOpenRecordingsStopTheRestartInsteadOfBeingDropped()
    {
        var many = Enumerable.Range(0, 129).Select(i => Path.Combine(_data, $"show{i}.ts"));
        Assert.Throws<IOException>(() => UpdateInstallation.WriteResume(_job, "2.5.0", many));
        Assert.False(File.Exists(Path.Combine(_job, "resume.json")));
    }

    // The update window finishes only when the installed AdTrim itself reports that it started.
    // A marker written by anything else, or naming a process that is not that program, must not count.
    [Fact]
    public void OnlyARunningCopyOfTheInstalledProgramConfirmsAStart()
    {
        string marker = Path.Combine(_job, UpdateInstallation.StartedMarkerName);
        string thisProgram = Environment.ProcessPath!;
        Assert.False(UpdateInstallation.ConfirmsStart(marker, thisProgram));

        UpdateInstallation.WriteStartedMarker(marker);
        Assert.True(UpdateInstallation.ConfirmsStart(marker, thisProgram));
        Assert.False(UpdateInstallation.ConfirmsStart(marker, Path.Combine(_install, "AdTrim.exe")));

        File.WriteAllText(marker, "not a process id");
        Assert.False(UpdateInstallation.ConfirmsStart(marker, thisProgram));
        File.WriteAllText(marker, "-1");
        Assert.False(UpdateInstallation.ConfirmsStart(marker, thisProgram));

        // The marker has one fixed name; a request to write anywhere else is ignored.
        string elsewhere = Path.Combine(_job, "job.json");
        UpdateInstallation.WriteStartedMarker(elsewhere);
        Assert.False(File.Exists(elsewhere));
    }

    [Fact]
    public void RecordingsOpenedDuringAnUpdateAreHandedOverOnce()
    {
        using var worker = new UpdateWorker(_data, _job, "unused public key", "2.4.9", _install);
        string recording = Path.Combine(_data, "tv", "Late.ts");
        worker.Defer(recording);
        worker.Defer(Path.Combine(_data, "tv", ".", "Other.ts"));
        Assert.Equal(new[] { recording, Path.Combine(_data, "tv", "Other.ts") }, worker.TakeDeferred());
        Assert.Empty(worker.TakeDeferred());
        Assert.False(worker.NeedsRestore);
    }

    [Fact]
    public async Task InstallingWithoutAVerifiedJobIsAProgrammingErrorNotASilentNoOp()
    {
        using var worker = new UpdateWorker(_data, _job, "unused public key", "2.4.9", _install);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.InstallAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RestoreAsync());
        Assert.False(Directory.Exists(worker.BackupDirectory));
    }
}
