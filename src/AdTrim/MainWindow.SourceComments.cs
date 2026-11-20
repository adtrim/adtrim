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
        bool busy = vm.IsBusy;
        _autosaveTimer.Stop();
        vm.IsBusy = true;
        view.SourceCommentFailed = true;
        try
        {
            if (view.AcceptedPlan is not { } plan || _sourceFingerprint is null || _store is null)
                throw new IOException("The source project is unavailable.");
            if (SourceCommentService.HasPendingUpdate(plan.SourcePath))
                throw new IOException("Source comment recovery must finish before the project can be updated.");
            // The video is already published. Cancelling the comment step must not lose its record.
            while (_saveInProgress) await Task.Delay(25);
            if (ProjectStore.FingerprintOf(plan.SourcePath, _sourceFingerprint.DurationUs) != _sourceFingerprint)
                throw new IOException("The source changed before its export could be recorded.");
            vm.LastExport = new("AdTrim", AppVersion.Numeric, DateTimeOffset.UtcNow, _sourceFingerprint);
            if (!await SaveProjectAsync(vm)) throw new IOException("The project sidecar could not be saved.");
            view.SourceCommentFailed = commentFailed;
            return (commentNote is null ? "" : commentNote + " ") + "Export record saved to project sidecar.";
        }
        catch (Exception ex)
        {
            return (commentNote is null ? "" : commentNote + " ") + "Export record was not saved: " + ex.Message;
        }
        finally { vm.IsBusy = busy; }
    }

    /// <summary>Only MP4 recordings get the Windows-visible export comment.</summary>
    private static bool CanCommentSource(string sourcePath)
        => Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    // Read when the export starts, so its progress list shows the comment step exactly when it will run.
    private async Task<SourceCommentChoice> ReadSourceCommentChoiceAsync(ExportPlan plan)
    {
        if (!CanCommentSource(plan.SourcePath)) return default;
        UserPreferences preferences;
        try { preferences = await UserPreferences.ReadAsync(); }
        // No settings file yet (first launch): the File menu's current choice applies.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { preferences = _preferences; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex is DamagedPreferencesException) ShowDamagedPreferencesBanner();
            return new(false, "Source comment was not added because saved preferences could not be read. " + ex.Message);
        }
        return new(preferences.AddSourceExportComment);
    }

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
        _autosaveTimer.Stop();
        vm.IsBusy = true;
        bool stopped = false;
        try
        {
            while (_saveInProgress) await Task.Delay(25, ct);
            if (!await SaveProjectAsync(vm)) return "Source comment was not added because project edits could not be saved.";
            if (_audioPass is { Completion.IsCompleted: false } pass)
            {
                pass.Cancel();
                try { await pass.Completion; } catch (OperationCanceledException) { }
                _audioPass = null;
            }
            CancelThumbnailLoad(vm, false);
            _previewAfter.Stop();
            _previewBefore.Stop();
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
                Preserve("ad-break suggestions", () => _suggestionStore.PreserveAfterMetadataUpdate(plan.SourcePath, expected, updated));
                Preserve("thumbnails", () => _thumbnails?.PreserveAfterMetadataUpdate(plan.SourcePath, expected));
                if (_media is not null)
                    Preserve("audio levels", () => (_audioLevels ?? new AudioLevelService(_ffmpeg, AudioLevelService.DefaultCacheRoot))
                        .PreserveAfterMetadataUpdate(plan.SourcePath, _media.PrimaryAudioIndex, expected));
                return failures;
            });
            vm.HasAdTrimAnalysis = _suggestionStore.Load(_suggestionStore.Key(plan.SourcePath, updated), updated.DurationUs) is not null;
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
                if (vm.IsExportScreen) _reopenPreviewsOnReturn = () => ReopenPreviewsAsync(vm, path, position);
                else _ = ReopenPreviewsAsync(vm, path, position);
            }
        }
    }

    private Func<Task>? _reopenPreviewsOnReturn;

    private async Task ReopenPreviewsAsync(MainViewModel vm, string path, long position)
    {
        if (vm.SourcePath != path) return;
        try
        {
            if (vm.IsCollapsed)
            {
                if (vm.KeptTimeline.DurationUs > 0)
                    _previewAfter.Open(vm.KeptTimeline.ToMpvEdl(path, _media?.TimelineStartTimeUs ?? 0), vm.KeptTimeline.ToOutput(position), buffered: true);
            }
            else
            {
                _previewAfter.Open(path, position);
                for (int i = 0; i < 200 && _previewAfter.IsLoading; i++) await Task.Delay(25);
                if (vm.SourcePath != path) return;
                _previewBefore.Open(path, Math.Max(0, position - FrameDurationUs(vm)));
            }
            if (_media is not null)
            {
                _previewAfter.SelectAudioStream(_media.PrimaryAudioIndex);
                _previewBefore.SelectAudioStream(_media.PrimaryAudioIndex);
            }
        }
        catch (InvalidOperationException ex)
        {
            vm.Banner = new BannerInfo(StatusKind.Warning, "The preview could not reopen the recording.", ex.Message, Array.Empty<BannerAction>());
        }
    }
}
