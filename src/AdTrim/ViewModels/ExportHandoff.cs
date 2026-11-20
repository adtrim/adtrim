using System.IO;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>
/// What a recording window does around an export that is not the export screen itself: whether the
/// source gets a comment, recording the export in the project, and deleting the project afterwards.
/// </summary>
public static class ExportHandoff
{
    /// <summary>Only MP4 recordings get the Windows-visible export comment.</summary>
    public static bool CanCommentSource(string sourcePath)
        => Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read when the export starts, so its progress list shows the comment step exactly when it will run.
    /// A missing settings file (first launch) follows the window's known choice; an unreadable one blocks
    /// the comment and says why.
    /// </summary>
    public static async Task<SourceCommentChoice> ChooseSourceCommentAsync(ExportPlan plan, Func<Task<UserPreferences>> readSaved,
        UserPreferences known, Action damaged)
    {
        if (!CanCommentSource(plan.SourcePath)) return default;
        UserPreferences preferences;
        try { preferences = await readSaved(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { preferences = known; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex is DamagedPreferencesException) damaged();
            return new(false, "Source comment was not added because saved preferences could not be read. " + ex.Message);
        }
        return new(preferences.AddSourceExportComment);
    }

    /// <summary>
    /// Saves the export in the project (<see cref="MainViewModel.LastExport"/>). The video is already
    /// published, so this runs even when the comment step was cancelled. Returns the note for the finished
    /// banner and whether the record was saved.
    /// </summary>
    public static async Task<(string Note, bool Recorded)> RecordExportAsync(MainViewModel vm, ExportPlan? plan,
        SourceFingerprint? fingerprint, ProjectStore? store, ProjectSaveCoordinator saves, string? commentNote)
    {
        bool busy = vm.IsBusy;
        saves.StopAutosave();
        vm.IsBusy = true;
        try
        {
            if (plan is null || fingerprint is null || store is null)
                throw new IOException("The source project is unavailable.");
            if (SourceCommentService.HasPendingUpdate(plan.SourcePath))
                throw new IOException("Source comment recovery must finish before the project can be updated.");
            // The video is already published. Cancelling the comment step must not lose its record.
            await saves.WaitForIdleAsync();
            if (ProjectStore.FingerprintOf(plan.SourcePath, fingerprint.DurationUs) != fingerprint)
                throw new IOException("The source changed before its export could be recorded.");
            vm.LastExport = new("AdTrim", AppVersion.Numeric, DateTimeOffset.UtcNow, fingerprint);
            if (!await saves.SaveAsync(vm)) throw new IOException("The project sidecar could not be saved.");
            return ((commentNote is null ? "" : commentNote + " ") + "Export record saved to project sidecar.", true);
        }
        catch (Exception ex)
        {
            return ((commentNote is null ? "" : commentNote + " ") + "Export record was not saved: " + ex.Message, false);
        }
        finally { vm.IsBusy = busy; }
    }

    /// <summary>
    /// When <paramref name="shouldDelete"/>, deletes the project files this window could have loaded for the
    /// source. Failures are non-fatal (the export already succeeded); returns a short trailing fragment for the
    /// success banner so the user can see whether the deletion happened. Empty when there is nothing to do.
    /// </summary>
    public static string DeleteSidecars(ProjectStore store, string sourcePath, SourceFingerprint? fingerprint, bool shouldDelete)
    {
        if (!shouldDelete) return string.Empty;
        int present = store.CandidateSidecarPathsFor(sourcePath).Count(File.Exists);
        if (present == 0) return " Sidecar already absent.";
        // Only projects this window could have loaded; anything else was never part of this export.
        var existing = fingerprint is null ? Array.Empty<string>() : store.DeletableSidecarsFor(sourcePath, fingerprint);
        string keptNote = present > existing.Count ? " A project that could not be read was kept." : "";
        if (existing.Count == 0) return keptNote;
        try
        {
            foreach (var sidecar in existing) File.Delete(sidecar);
            return (existing.Count == 1 ? " Sidecar deleted." : $" {existing.Count} sidecars deleted.") + keptNote;
        }
        catch (Exception ex)
        {
            return $" Sidecar delete failed: {ex.Message}";
        }
    }

    /// <summary>Why opening or closing a project waits while the export screen is open.</summary>
    public static BannerInfo ExportOpenBanner(bool exportInFlight) => new(StatusKind.Warning,
        "Export is open.",
        exportInFlight
            ? "Cancel or finish the export before opening or closing a project."
            : "Return to editing before opening or closing a project.",
        Array.Empty<BannerAction>());
}
