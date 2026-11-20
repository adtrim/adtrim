using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.Models;
using Xunit;

namespace AdTrim.Tests;

public class ExportNamingTests
{
    [Fact]
    public void TsNamesAvoidExistingExportsAndNeverReuseTheSource()
    {
        string folder = Path.Combine(Path.GetTempPath(), "adtrim-ts-naming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "Show.ts");
            File.WriteAllText(source, "source");
            File.WriteAllText(Path.Combine(folder, "Show [AdTrim].ts"), "existing export");
            Assert.Equal("Show [AdTrim 2].ts", ExportNaming.DeriveDefaultFilename(source, folder, ExportFormat.Ts));
            Assert.Equal("Show [AdTrim].mp4", ExportNaming.DeriveDefaultFilename(source, folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void DefaultFilename_LowercasesS00E00Token()
    {
        var name = ExportNaming.DeriveDefaultFilename(
            "The Rookie (2018) - S08E18 - The Bandit.mp4");
        name.Should().Be("The Rookie (2018) - s08e18 - The Bandit [AdTrim].mp4");
    }

    [Fact]
    public void DefaultFilename_NoSourceFallsBackToPlaceholder()
    {
        ExportNaming.DeriveDefaultFilename(null).Should().Be("export [AdTrim].mp4");
    }

    [Fact]
    public void RepeatExports_SkipExistingFilesAndDirectoriesWithoutChangingThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), "adtrim-naming-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "Show - s01e02 - Title [AdTrim].mp4");
            File.WriteAllText(first, "original export");
            Directory.CreateDirectory(Path.Combine(folder, "Show - s01e02 - Title [AdTrim 2].mp4"));
            ExportNaming.DeriveDefaultFilename("Show - S01E02 - Title.mp4", folder)
                .Should().Be("Show - s01e02 - Title [AdTrim 3].mp4");
            ExportNaming.DeriveDefaultFilename(first, folder)
                .Should().Be("Show - s01e02 - Title [AdTrim 3].mp4");
            File.ReadAllText(first).Should().Be("original export");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("Z:")]
    [InlineData("not\0a-folder")]
    public void IncompleteDestination_DoesNotInterruptFilenameEditing(string folder)
    {
        ExportNaming.DeriveDefaultFilename("Show.mp4", folder).Should().Be("Show [AdTrim].mp4");
    }

    [Theory]
    [InlineData("clean.mp4", true)]
    [InlineData("with spaces.mp4", true)]
    [InlineData("bad<char.mp4", false)]
    [InlineData("bad/slash.mp4", false)]
    [InlineData("", false)]
    public void IsValidFilename_HonorsWindowsRules(string name, bool expected)
    {
        ExportNaming.IsValidFilename(name).Should().Be(expected);
    }
}

// Export setup decides what may be written and where. These rules stand between an edit and a
// recording: a wrong one either blocks a valid export or lets one replace a file it must not.
public sealed class ExportDialogViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "adtrim-export-setup-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private static readonly MediaInfo Media = new(30_000_000, "h264", 1920, 1080, new Rational(30000, 1001),
        new[] { new AudioStream(1, "ac3", 6, true) }, 1);

    public ExportDialogViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _source = Path.Combine(_folder, "Show.mp4");
        File.WriteAllText(_source, "recording");
    }
    public void Dispose() => Directory.Delete(_folder, true);

    // Three sections of 10 s; the middle one is the ad break.
    private AdTrim.ViewModels.ExportDialogViewModel Setup(bool excludeMiddle = true, MediaInfo? media = null)
    {
        var project = EditingRegressionTests.Project();
        project.SourcePath = _source;
        if (excludeMiddle) project.CommandStack.Execute(new AdTrim.Commands.ToggleExcludedCommand(project, project.Segments[1]));
        return new AdTrim.ViewModels.ExportDialogViewModel(project, media ?? Media);
    }
    private static IEnumerable<string> Blocking(AdTrim.ViewModels.ExportDialogViewModel vm)
        => vm.Validate().Where(v => v.Kind == AdTrim.ViewModels.ExportValidationKind.Blocking).Select(v => v.Message);

    [Fact]
    public void AReadyProjectExportsOnlyItsKeptSectionsInOrder()
    {
        var vm = Setup();
        Assert.Empty(Blocking(vm));
        Assert.Equal(Path.Combine(_folder, "Show [AdTrim].mp4"), vm.FullOutputPath);
        var plan = vm.BuildPlan()!;
        Assert.Equal(new[] { (1, 0L, 10_000_000L), (2, 20_000_000L, 30_000_000L) },
            plan.KeptSegments.Select(s => (s.Index, s.StartUs, s.EndUs)));
        Assert.Equal(20_000_000, vm.FinalDurationUs);
        Assert.Equal(_source, plan.SourcePath);
        Assert.False(plan.Overwrite);
    }

    [Fact]
    public void TheSourceRecordingCanNeverBeTheOutput()
    {
        var vm = Setup();
        vm.OutputFilename = "SHOW.MP4";
        // Confirming an overwrite is for an earlier export, never for the recording itself.
        vm.OverwriteConfirmed = true;
        Assert.Contains(Blocking(vm), m => m.Contains("identical to the source"));
    }

    [Fact]
    public void AnExistingFileBlocksUntilConfirmedAndTheConfirmationDoesNotFollowANewName()
    {
        File.WriteAllText(Path.Combine(_folder, "Earlier.mp4"), "earlier export");
        File.WriteAllText(Path.Combine(_folder, "Other.mp4"), "something else");
        var vm = Setup();
        vm.OutputFilename = "Earlier.mp4";
        Assert.Contains(Blocking(vm), m => m.Contains("already exists"));
        Assert.Equal("Export & overwrite", vm.ExportButtonText);

        vm.OverwriteConfirmed = true;
        Assert.Empty(Blocking(vm));
        Assert.True(vm.BuildPlan()!.Overwrite);

        vm.OutputFilename = "Other.mp4";
        Assert.Contains(Blocking(vm), m => m.Contains("already exists"));
        Assert.False(vm.BuildPlan()!.Overwrite);
    }

    [Fact]
    public void AnExportThatWouldRemoveNothingOrEverythingIsBlocked()
    {
        Assert.Contains(Blocking(Setup(excludeMiddle: false)), m => m.Contains("nothing would be removed"));
        var all = EditingRegressionTests.Project();
        all.SourcePath = _source;
        foreach (var segment in all.Segments.ToArray())
            all.CommandStack.Execute(new AdTrim.Commands.ToggleExcludedCommand(all, segment));
        Assert.Contains(Blocking(new AdTrim.ViewModels.ExportDialogViewModel(all, Media)), m => m.Contains("nothing to export"));
    }

    [Fact]
    public void ARecordingThatChangedOrHasNoAudioIsBlocked()
    {
        Assert.Contains(Blocking(Setup(media: Media with { DurationUs = 31_000_000 })), m => m.Contains("duration has changed"));
        Assert.Contains(Blocking(Setup(media: Media with { AudioStreams = Array.Empty<AudioStream>(), PrimaryAudioIndex = -1 })),
            m => m.Contains("No primary audio"));
        File.Delete(_source);
        Assert.Equal(new[] { "Source file is missing." }, Blocking(Setup()));
    }

    [Fact]
    public void ChoosingTsRenamesTheOutputAndKeepsANameTheUserTyped()
    {
        var vm = Setup();
        vm.SelectedFormat = ExportFormat.Ts;
        Assert.Equal("Show [AdTrim].ts", vm.OutputFilename);
        Assert.Equal(ExportFormat.Ts, vm.BuildPlan()!.Format);
        Assert.Empty(Blocking(vm));

        vm.OutputFilename = "My cut.ts";
        vm.SelectedFormat = ExportFormat.Mp4;
        Assert.Equal("My cut.mp4", vm.OutputFilename);
        vm.OutputFilename = "My cut.mkv";
        Assert.Contains(Blocking(vm), m => m.Contains("must end with '.mp4'"));
    }

    // The setup screen lists what will be exported before the user commits to an hour of encoding;
    // it must be the same parts, names and order the export then uses.
    [Fact]
    public void TheSetupScreenListsExactlyThePartsThePlanExports()
    {
        var vm = Setup();
        var plan = vm.BuildPlan()!;
        Assert.Equal(new[] { "Part 1", "Part 2" }, plan.KeptSegments.Select(s => s.PartTitle));
        Assert.Equal(plan.KeptSegments.Select(s => (s.Index, s.PartTitle)), vm.PlannedParts.Select(p => (p.Number, p.Name)));
        Assert.Equal("00:00.00 → 00:10.00", vm.PlannedParts[0].TimeRange);
        Assert.Equal("00:10.00", vm.PlannedParts[1].Length);
    }
}
