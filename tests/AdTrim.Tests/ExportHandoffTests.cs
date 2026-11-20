using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// After an export: the record of it must survive, the source is commented only when the user's saved
// choice says so, and deleting the project never removes one this AdTrim cannot read.
public sealed class ExportHandoffTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly ProjectStore _store;
    private static readonly MediaInfo Media = new(30_000_000, "h264", 1920, 1080, new Rational(30, 1), [new AudioStream(1, "ac3", 2, true)], 1);

    public ExportHandoffTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "Show.mp4");
        File.WriteAllBytes(_source, new byte[2048]);
        _store = new ProjectStore(Path.Combine(_directory, "data", "projects"));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private SourceFingerprint Fingerprint => ProjectStore.FingerprintOf(_source, Media.DurationUs);
    private ExportPlan Plan(string source) => new ExportDialogViewModel(Recording(source), Media).BuildPlan()!;

    private static MainViewModel Recording(string source)
    {
        var vm = EditingRegressionTests.Project();
        vm.SourcePath = source;
        return vm;
    }

    private ProjectSaveCoordinator Saves() => new(new ProjectSession(), () => new SaveTarget(_store, Media, Fingerprint),
        new NoAutosave(), (_, _) => { }, () => { });

    private sealed class NoAutosave : IAutosave { public void Start() { } public void Restart() { } public void Stop() { } }

    [Theory]
    [InlineData("show.mp4", true)]
    [InlineData("SHOW.MP4", true)]
    [InlineData("show.ts", false)]
    public void OnlyMp4SourcesTakeTheComment(string name, bool expected) => Assert.Equal(expected, ExportHandoff.CanCommentSource(name));

    [Fact]
    public async Task TheSavedChoiceDecidesTheComment()
    {
        bool damaged = false;
        var on = await ExportHandoff.ChooseSourceCommentAsync(Plan(_source), () => Task.FromResult(new UserPreferences { AddSourceExportComment = true }),
            new UserPreferences { AddSourceExportComment = false }, () => damaged = true);
        Assert.True(on.Add);
        var ts = Path.ChangeExtension(_source, ".ts");
        File.Copy(_source, ts);
        var tsChoice = await ExportHandoff.ChooseSourceCommentAsync(Plan(ts), () => Task.FromResult(new UserPreferences()), new(), () => damaged = true);
        Assert.False(tsChoice.Add);
        Assert.Null(tsChoice.Failure);
        Assert.False(damaged);
    }

    [Fact]
    public async Task WithoutASettingsFileTheWindowsKnownChoiceApplies()
    {
        var choice = await ExportHandoff.ChooseSourceCommentAsync(Plan(_source), () => throw new FileNotFoundException(),
            new UserPreferences { AddSourceExportComment = false }, () => { });
        Assert.False(choice.Add);
        Assert.Null(choice.Failure);
    }

    // A damaged settings file may hold "off"; writing to the user's recording on a guess is not allowed.
    [Fact]
    public async Task UnreadableSettingsBlockTheCommentAndSayWhy()
    {
        bool damaged = false;
        var choice = await ExportHandoff.ChooseSourceCommentAsync(Plan(_source), () => throw new DamagedPreferencesException(),
            new UserPreferences { AddSourceExportComment = true }, () => damaged = true);
        Assert.False(choice.Add);
        Assert.StartsWith("Source comment was not added because saved preferences could not be read.", choice.Failure);
        Assert.True(damaged);
    }

    [Fact]
    public async Task TheExportIsRecordedInTheProject()
    {
        var vm = Recording(_source);
        var (note, recorded) = await ExportHandoff.RecordExportAsync(vm, Plan(_source), Fingerprint, _store, Saves(), "Source export comment added.");
        Assert.True(recorded);
        Assert.Equal("Source export comment added. Export record saved to project sidecar.", note);
        Assert.False(vm.IsBusy);
        var saved = _store.Load(_source, Media).Project!;
        Assert.Equal(AppVersion.Numeric, saved.LastExport!.Version);
        Assert.Equal(Fingerprint, saved.LastExport.SourceFingerprint);
    }

    [Fact]
    public async Task AChangedSourceIsNotRecordedAsExported()
    {
        var vm = Recording(_source);
        var before = Fingerprint;
        File.AppendAllText(_source, "changed");
        var (note, recorded) = await ExportHandoff.RecordExportAsync(vm, Plan(_source), before, _store, Saves(), null);
        Assert.False(recorded);
        Assert.Equal("Export record was not saved: The source changed before its export could be recorded.", note);
        Assert.Null(vm.LastExport);
        var (missing, _) = await ExportHandoff.RecordExportAsync(vm, null, before, _store, Saves(), null);
        Assert.Equal("Export record was not saved: The source project is unavailable.", missing);
    }

    [Fact]
    public void DeletingTheProjectKeepsOneThisVersionCannotRead()
    {
        Assert.Equal("", ExportHandoff.DeleteSidecars(_store, _source, Fingerprint, shouldDelete: false));
        Assert.Equal(" Sidecar already absent.", ExportHandoff.DeleteSidecars(_store, _source, Fingerprint, shouldDelete: true));

        var project = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, _source, Fingerprint, Media, new(), new(), SidecarLocation.NextToSource);
        var saved = _store.Save(project);
        Assert.Equal(" Sidecar deleted.", ExportHandoff.DeleteSidecars(_store, _source, Fingerprint, shouldDelete: true));
        Assert.False(File.Exists(saved));

        File.WriteAllText(_store.SidecarPathFor(_source), "{\"schema_version\":999}");
        Assert.Equal(" A project that could not be read was kept.", ExportHandoff.DeleteSidecars(_store, _source, Fingerprint, shouldDelete: true));
        Assert.True(File.Exists(_store.SidecarPathFor(_source)));
    }

    [Fact]
    public void AnOpenExportExplainsWhatToDoFirst()
    {
        Assert.Equal("Cancel or finish the export before opening or closing a project.", ExportHandoff.ExportOpenBanner(true).Body);
        Assert.Equal("Return to editing before opening or closing a project.", ExportHandoff.ExportOpenBanner(false).Body);
    }
}
