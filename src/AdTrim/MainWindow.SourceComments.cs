using System.IO;
using System.Windows;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

namespace AdTrim;

public partial class MainWindow
{
    private void OnViewUnexportedFiles(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || BlockProjectSwitchForExport(vm) || vm.IsBusy) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a source recordings folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        var dialog = new UnexportedFilesDialog(picker.FolderName) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedPath is { } path) _ = OpenFileAsync(path);
    }

    private async Task<string?> RecordSuccessfulExportAsync(MainViewModel vm, ExportView view, CancellationToken ct)
    {
        string? commentNote = await AddSourceCommentAsync(vm, view, ct);
        bool commentFailed = view.SourceCommentFailed;
        view.SourceCommentFailed = true;
        var (note, recorded) = await ExportHandoff.RecordExportAsync(vm, view.AcceptedPlan, _sourceFingerprint, _store, _saves, commentNote);
        if (recorded) view.SourceCommentFailed = commentFailed;
        return note;
    }

    // Read when the export starts, so its progress list shows the comment step exactly when it will run.
    private Task<SourceCommentChoice> ReadSourceCommentChoiceAsync(ExportPlan plan)
        => ExportHandoff.ChooseSourceCommentAsync(plan, () => UserPreferences.ReadAsync(), _preferences, ShowDamagedPreferencesBanner);

    private async Task<string?> AddSourceCommentAsync(MainViewModel vm, ExportView view, CancellationToken ct)
    {
        if (view.AcceptedPlan is not { } plan) return null;
        if (view.SourceComment.Failure is { } failure)
        {
            view.SourceCommentFailed = true;
            return failure;
        }
        if (!view.SourceComment.Add) return null;
        view.SourceCommentFailed = true;
        if (_sourceFingerprint is null || _store is null) return "Source comment was not added: the project is unavailable.";
        bool busy = vm.IsBusy;
        long position = vm.PlayheadUs;
        _saves.StopAutosave();
        vm.IsBusy = true;
        bool stopped = false;
        try
        {
            await _saves.WaitForIdleAsync(ct);
            if (!await _saves.SaveAsync(vm)) return "Source comment was not added because project edits could not be saved.";
            await _timelineMedia.StopUnfinishedAudioPassAsync();
            _timelineMedia.CancelThumbnailLoad(vm, false);
            _playback.After.Stop();
            _playback.Before.Stop();
            stopped = true;
            vm.StatusOverride = "Adding the source export comment...";
            view.ReportSourceCommentProgress(new(false, "", 0));
            var commentProgress = new Progress<SourceCommentProgress>(view.ReportSourceCommentProgress);
            var expected = _sourceFingerprint;
            var updated = await Task.Run(() => SourceCommentService.MarkAsync(plan.SourcePath, expected, _store, ct, commentProgress));
            _sourceFingerprint = updated;
            vm.SourceFingerprint = updated;
            var cacheFailures = await Task.Run(() =>
            {
                var failures = new List<string>();
                void Preserve(string name, Action copy)
                {
                    try { copy(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { failures.Add(name); }
                }
                Preserve("ad-break suggestions", () => _markers.Suggestions.PreserveAfterMetadataUpdate(plan.SourcePath, expected, updated));
                Preserve("thumbnails", () => _timelineMedia.Thumbnails?.PreserveAfterMetadataUpdate(plan.SourcePath, expected));
                if (_media is not null)
                    Preserve("audio levels", () => (_timelineMedia.AudioLevels ?? new AudioLevelService(_ffmpeg, AudioLevelService.DefaultCacheRoot))
                        .PreserveAfterMetadataUpdate(plan.SourcePath, _media.PrimaryAudioIndex, expected));
                return failures;
            });
            vm.HasAdTrimAnalysis = _markers.Suggestions.Load(_markers.Suggestions.Key(plan.SourcePath, updated), updated.DurationUs) is not null;
            view.SourceCommentFailed = false;
            return (updated == expected ? "Source export comment was already current." : "Source export comment added.") + (cacheFailures.Count == 0 ? ""
                : " Cached " + string.Join(", ", cacheFailures) + " could not be retained and may need to be regenerated.");
        }
        catch (Exception ex)
        {
            view.SourceCommentFailed = true;
            return "Source comment was not added: " + ex.Message;
        }
        finally
        {
            vm.IsBusy = busy;
            if (stopped && !SourceCommentService.HasPendingUpdate(plan.SourcePath))
            {
                // Reopening creates each player's Direct3D video output again. An AMD driver crashed doing
                // that for both players at once behind the export screen, so wait for the editor and
                // open them one at a time.
                string path = plan.SourcePath;
                if (vm.IsExportScreen) _reopenPreviewsOnReturn = () => _playback.ReopenAsync(vm, path, position);
                else _ = _playback.ReopenAsync(vm, path, position);
            }
        }
    }

    private Func<Task>? _reopenPreviewsOnReturn;
}
