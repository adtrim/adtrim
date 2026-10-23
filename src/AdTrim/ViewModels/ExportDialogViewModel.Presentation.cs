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
        _ when _progressPhase == ExportPhase.Concatenating && string.IsNullOrEmpty(RemainingFormatted) => ProgressLine,
        _ => string.IsNullOrEmpty(RemainingFormatted) ? "Estimating time remaining..." : RemainingFormatted,
    };
    public ExportPartItem? CurrentPart => IsExporting ? Parts.FirstOrDefault(p => p.IsInProgress) : null;
    public string CurrentPartHeading => CurrentPart is { Index: > 0 } part ? $"Encoding {part.Label}" : ProgressLine;
    public string PartsSummary => $"{_project.Segments.Count(s => !s.IsExcluded)} parts kept · {_project.Segments.Count(s => s.IsExcluded)} removed";

    private void RefreshPresentation()
    {
        Notify(nameof(ProgressHeading));
        Notify(nameof(ProgressCaption));
        Notify(nameof(TimeHeadline));
        Notify(nameof(IsProgressIndeterminate));
        Notify(nameof(ProgressDialValue));
        Notify(nameof(CurrentPart));
        Notify(nameof(CurrentPartHeading));
    }
}
