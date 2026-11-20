using AdTrim.Services;
using AdTrim.Models;

namespace AdTrim.ViewModels;

public sealed partial class ExportDialogViewModel
{
    private string _cleanupWarning = "";
    public string CleanupWarning { get => _cleanupWarning; private set => Set(ref _cleanupWarning, value); }

    public string SourceDisplayName => ExportNaming.DisplayTitles(_project.SourcePath).Title;
    public string SourceSubtitle => ExportNaming.DisplayTitles(_project.SourcePath).Subtitle;
    public string ProgressHeading => Mode switch
    {
        ExportDialogMode.Completed => "EXPORT COMPLETE",
        ExportDialogMode.Cancelled => "EXPORT CANCELLED",
        ExportDialogMode.Failed => "EXPORT STOPPED",
        _ => "EXPORTING YOUR VIDEO",
    };
    public string ProgressCaption => IsCompleted ? "complete" : IsFailed ? "stopped" : "exported";
    public bool IsProgressIndeterminate => IsExporting && (_progressPhase is ExportPhase.Planning or ExportPhase.Finalizing or ExportPhase.Validating
        || _progressPhase == ExportPhase.Concatenating && string.IsNullOrEmpty(RemainingFormatted));
    public double ProgressDialValue => IsProgressIndeterminate ? 0.08 : ProgressPercent;
    public string TimeHeadline => Mode switch
    {
        ExportDialogMode.Completed => "Your video is ready",
        ExportDialogMode.Cancelled => "Export cancelled",
        ExportDialogMode.Failed => "Export could not finish",
        _ when _progressPhase == ExportPhase.Finalizing => "Finalizing video…",
        _ when _progressPhase == ExportPhase.Validating => "Checking your video…",
        _ when _progressPhase == ExportPhase.RecordingExport => string.IsNullOrEmpty(RemainingFormatted) ? "Finishing up…" : RemainingFormatted,
        _ when _progressPhase == ExportPhase.Concatenating && string.IsNullOrEmpty(RemainingFormatted) => ProgressLine,
        _ => string.IsNullOrEmpty(RemainingFormatted) ? "Estimating time remaining..." : RemainingFormatted,
    };
    public ExportPartItem? CurrentPart => IsExporting ? Parts.FirstOrDefault(p => p.IsInProgress) : null;
    public string CurrentPartHeading => _progressPhase == ExportPhase.RecordingExport ? _recordingHeading
        : CurrentPart is { Index: > 0 } part ? $"Encoding {part.Label}" : ProgressLine;
    public sealed record PlannedPart(int Number, string Name, string TimeRange, string Length);

    /// <summary>The step after the video is saved, as export setup expects it. The export decides again when it starts.</summary>
    public AfterExportStep PlannedAfterExport { get; set; } = AfterExportStep.RecordExport;

    /// <summary>The steps after the parts, named as the progress list will name them.</summary>
    public IReadOnlyList<string> PlannedSteps => PlannedAfterExport == AfterExportStep.None
        ? [ExportPartItem.StitchLabel, ExportPartItem.CheckLabel]
        : [ExportPartItem.StitchLabel, ExportPartItem.CheckLabel, ExportPartItem.AfterExportLabel(PlannedAfterExport)];

    public string PageTitle => Mode switch
    {
        ExportDialogMode.Configuring => "Export video",
        ExportDialogMode.Completed => "Export complete",
        ExportDialogMode.Cancelled => "Export cancelled",
        ExportDialogMode.Failed => "Export stopped",
        _ => "Exporting video",
    };

    /// <summary>Title of the pane that lists the job: the same pane, before, during and after the export.</summary>
    public string JobTitle => Mode switch
    {
        ExportDialogMode.Configuring => "What will be exported",
        ExportDialogMode.Exporting => "What is being exported",
        ExportDialogMode.Completed => "What was exported",
        _ => "What was being exported",
    };

    /// <summary>What an export started now would contain, in order: the rows of the setup screen's parts list.</summary>
    public IReadOnlyList<PlannedPart> PlannedParts => _project.Segments.Where(s => !s.IsExcluded).OrderBy(s => s.StartUs)
        .Select((s, index) => new PlannedPart(index + 1, s.Label ?? SectionNames.Format(null, index + 1),
            $"{FormatHms(s.StartUs)} → {FormatHms(s.EndUs)}", FormatHms(s.DurationUs))).ToList();

    public string PartsSummary => $"{_project.Segments.Count(s => !s.IsExcluded)} parts kept · {_project.Segments.Count(s => s.IsExcluded)} removed";

    private void RefreshPresentation()
    {
        Notify(nameof(ProgressHeading));
        Notify(nameof(PageTitle));
        Notify(nameof(JobTitle));
        Notify(nameof(ProgressCaption));
        Notify(nameof(TimeHeadline));
        Notify(nameof(IsProgressIndeterminate));
        Notify(nameof(ProgressDialValue));
        Notify(nameof(CurrentPart));
        Notify(nameof(CurrentPartHeading));
    }
}
