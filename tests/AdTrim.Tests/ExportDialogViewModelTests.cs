using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.Models;
using AdTrim.ViewModels;
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

    // The progress list is the user's guide from 0% to 100%: every step that will run is there from the
    // start, in order, and a step that will not run is never shown.
    [Theory]
    [InlineData(AfterExportStep.None, new[] { "Part 1", "Part 2", "Stitch parts", "Check the video" })]
    [InlineData(AfterExportStep.RecordExport, new[] { "Part 1", "Part 2", "Stitch parts", "Check the video", "Save export status" })]
    [InlineData(AfterExportStep.SourceComment, new[] { "Part 1", "Part 2", "Stitch parts", "Check the video", "Add comment to source file" })]
    public void TheProgressListShowsEveryStepThatWillRunFromTheStart(AfterExportStep after, string[] rows)
    {
        var vm = Setup();
        vm.BeginExport(vm.BuildPlan()!, after);
        Assert.Equal(rows, vm.Parts.Select(p => p.Label));
        Assert.All(vm.Parts, p => Assert.True(p.IsQueued));
        Assert.Equal(0, vm.ProgressPercent);
    }

    // Replays what the export and the step after it report. The ring must never move back, must not read
    // 100% while a row is unchecked, and must not have every row checked while it still reads below 100%.
    [Theory]
    [InlineData(AfterExportStep.None)]
    [InlineData(AfterExportStep.RecordExport)]
    [InlineData(AfterExportStep.SourceComment)]
    public void TheRingAndTheChecklistAgreeThroughEveryPhase(AfterExportStep after)
    {
        var vm = Setup();
        vm.BeginExport(vm.BuildPlan()!, after);
        var events = new List<(ExportProgress Progress, string Running)>
        {
            (new(ExportPhase.Planning, 0, 2, 0, 0, "Preparing"), ""),
            (new(ExportPhase.EncodingSegment, 1, 2, 0, 0, "Encoding"), "Part 1"),
            (new(ExportPhase.EncodingSegment, 1, 2, 0.5, 0.24, "Encoding"), "Part 1"),
            (new(ExportPhase.EncodingSegment, 2, 2, 0, 0.475, "Encoding"), "Part 2"),
            (new(ExportPhase.EncodingSegment, 2, 2, 1, 0.95, "Encoding"), "Part 2"),
            (new(ExportPhase.Concatenating, 2, 2, 0, 0.95, "Preparing parts"), "Stitch parts"),
            (new(ExportPhase.Concatenating, 2, 2, 0.5, 0.965, "Stitching parts"), "Stitch parts"),
            (new(ExportPhase.Finalizing, 2, 2, 1, 0.98, "Finalizing MP4"), "Stitch parts"),
            (new(ExportPhase.Validating, 2, 2, 0, 0.99, "Validating output"), "Check the video"),
            (new(ExportPhase.Done, 2, 2, 1, 1, "Export complete"), ""),
        };
        string? afterRow = vm.Parts.Last().Index == AdTrim.ViewModels.ExportPartItem.AfterExportIndex ? vm.Parts.Last().Label : null;
        if (afterRow is not null)
            events.Add((new(ExportPhase.RecordingExport, 0, 0, 0, 0.99, "Recording the successful export..."), afterRow));
        if (after == AfterExportStep.SourceComment)
            foreach (var (step, fraction) in new[] { ("", 0.0), (SourceCommentService.CopyingStep, 0.3), (SourceCommentService.VerifyingStep, 0.99), (SourceCommentService.VerifyingStep, 1.0) })
                events.Add((new(ExportPhase.RecordingExport, 0, 0, fraction, 0.99, "Adding the export comment to the source file",
                    Detail: step == "" ? null : step), afterRow!));

        double previous = 0;
        foreach (var (progress, running) in events)
        {
            vm.UpdateProgress(progress);
            string at = $"{progress.Phase} {progress.SegmentPercent}";
            Assert.True(vm.ProgressPercent >= previous, $"The ring moved back at {at}");
            previous = vm.ProgressPercent;
            Assert.Equal(running, string.Join(",", vm.Parts.Where(p => p.IsInProgress).Select(p => p.Label)));
            bool allDone = vm.Parts.All(p => p.IsDone);
            Assert.True(allDone == (vm.ProgressPercent == 1), $"Rows and ring disagree at {at}: {vm.ProgressPercentText}, all done {allDone}");
            Assert.True(vm.ProgressPercentText != "100%" || allDone, $"The ring reads 100% with work left at {at}");
            // Rows after the running one have not started.
            int active = vm.Parts.ToList().FindIndex(p => p.IsInProgress);
            if (active >= 0) Assert.All(vm.Parts.Skip(active + 1), p => Assert.True(p.IsQueued, $"{p.Label} started early at {at}"));
        }
        vm.MarkComplete();
        Assert.Equal(1, vm.ProgressPercent);
        Assert.All(vm.Parts, p => Assert.True(p.IsDone));
    }

    // A long source comment gets a real share of the ring instead of all of it happening at 99%.
    [Fact]
    public void ALongSourceCommentHasItsOwnShareOfTheRing()
    {
        var vm = Setup();
        vm.BeginExport(vm.BuildPlan()!, AfterExportStep.SourceComment);
        vm.UpdateProgress(new(ExportPhase.Done, 2, 2, 1, 1, "Export complete"));
        double exported = vm.ProgressPercent;
        Assert.InRange(exported, 0.05, 0.9);
        vm.UpdateProgress(new(ExportPhase.RecordingExport, 0, 0, 0.5, 0.99, "Adding the export comment to the source file"));
        Assert.Equal(exported + (1 - exported) * 0.5, vm.ProgressPercent, 6);

        // A 5 GB source on a disk at the assumed rate: an export projected at two minutes leaves the comment most of the ring.
        double comment = AdTrim.ViewModels.ExportDialogViewModel.SourceCommentSeconds(5_000_000_000);
        Assert.Equal(250, comment, 6);
        Assert.Equal(120 / 370.0, AdTrim.ViewModels.ExportDialogViewModel.ExportShare(TimeSpan.FromSeconds(60), 0.5, comment, 0.75), 6);
        // Too early to project: the current share stands.
        Assert.Equal(0.75, AdTrim.ViewModels.ExportDialogViewModel.ExportShare(TimeSpan.FromMilliseconds(500), 0.5, comment, 0.75));
        Assert.Equal(0.75, AdTrim.ViewModels.ExportDialogViewModel.ExportShare(TimeSpan.FromSeconds(60), 0.001, comment, 0.75));
    }

    // "~00:00 remaining" while the source is still being verified claims the step already finished.
    [Fact]
    public void AStepStillRunningNeverClaimsNoTimeRemains()
    {
        var vm = Setup();
        vm.BeginExport(vm.BuildPlan()!, AfterExportStep.SourceComment);
        vm.UpdateProgress(new(ExportPhase.RecordingExport, 0, 0, 0, 0.99, "Adding the export comment to the source file"));
        Thread.Sleep(1100);
        vm.UpdateProgress(new(ExportPhase.RecordingExport, 0, 0, 0.99, 0.99, "Adding the export comment to the source file",
            Detail: SourceCommentService.VerifyingStep));
        Assert.DoesNotContain("00:00", vm.TimeHeadline);
        Assert.Equal("Finishing up…", vm.TimeHeadline);
        Assert.True(vm.Parts.Last().IsInProgress);
    }

    // Setup shows the whole job before it starts; the progress list must then be the same rows,
    // so nothing appears or disappears when Export is pressed.
    [Theory]
    [InlineData(AdTrim.ViewModels.AfterExportStep.SourceComment)]
    [InlineData(AdTrim.ViewModels.AfterExportStep.RecordExport)]
    public void TheSetupPlanNamesEveryRowTheProgressListThenShows(AdTrim.ViewModels.AfterExportStep after)
    {
        var vm = Setup();
        vm.PlannedAfterExport = after;
        var planned = vm.PlannedParts.Select(p => p.Name).Concat(vm.PlannedSteps).ToArray();
        vm.BeginExport(vm.BuildPlan()!, after);
        Assert.Equal(planned, vm.Parts.Select(p => p.Label));
        Assert.Equal("What is being exported", vm.JobTitle);
    }
}
