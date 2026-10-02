using AdTrim.Services;

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
    public string TimeHeadline => Mode switch
    {
        ExportDialogMode.Completed => "Your video is ready",
        ExportDialogMode.Cancelled => "Export cancelled",
        ExportDialogMode.Failed => "Export could not finish",
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
        Notify(nameof(CurrentPart));
        Notify(nameof(CurrentPartHeading));
    }
}
