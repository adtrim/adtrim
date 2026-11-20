using System.IO;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>Where the open recording's project is saved.</summary>
public sealed record SaveTarget(ProjectStore Store, MediaInfo Media, SourceFingerprint Fingerprint);

/// <summary>The debounced autosave: restarted by each edit, stopped while something else saves.</summary>
public interface IAutosave
{
    /// <summary>Starts the countdown if it is not already running.</summary>
    void Start();
    /// <summary>Starts the countdown over.</summary>
    void Restart();
    void Stop();
}

/// <summary>The user's answer when edits could not be saved before leaving a recording.</summary>
public enum SaveFailureChoice { Retry, Discard, Stay }

/// <summary>
/// Saving a recording window's project: the "Saved" status, autosave, flushing before the window
/// switches recordings or closes, and a close requested while that is under way.
/// Only a completed save of the current revision reports Saved.
/// </summary>
public sealed class ProjectSaveCoordinator(ProjectSession session, Func<SaveTarget?> target, IAutosave autosave,
    Action<string, Exception> log, Action requestClose, Action? retry = null)
{
    private string? _fallbackNoticeFor;
    private bool _closeAfterSwitch;

    /// <summary>True while a save is writing.</summary>
    public bool InProgress { get; private set; }

    public void StartAutosave() => autosave.Start();
    public void RestartAutosave() => autosave.Restart();
    public void StopAutosave() => autosave.Stop();

    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (InProgress) await Task.Delay(25, ct);
    }

    /// <summary>Saves the edits; false when there is nothing to save to or the save failed.</summary>
    public async Task<bool> SaveAsync(MainViewModel vm)
    {
        if (!vm.IsDirty) return true;
        if (target() is not { } to || vm.SourcePath is null) return false;
        var revision = vm.Revision;
        var source = vm.SourcePath;
        var snapshot = Snapshot(vm, source, to.Fingerprint, to.Media);
        InProgress = true;
        vm.SaveStatus = "Saving";
        try
        {
            var savedTo = await session.SaveAsync(to.Store, snapshot);
            // Said once per recording: the edits are safe, but they are not where the user expects.
            if (vm.SourcePath == source && _fallbackNoticeFor != source && vm.Banner is null
                && !string.Equals(savedTo, to.Store.SidecarPathFor(source), StringComparison.OrdinalIgnoreCase))
            {
                _fallbackNoticeFor = source;
                vm.Banner = new BannerInfo(StatusKind.Info, "Edits are saved on this computer only.",
                    "AdTrim cannot write beside this recording, so the project is kept in AdTrim's data folder and will not move with the recording.",
                    Array.Empty<BannerAction>());
            }
            if (vm.SourcePath == source && vm.Revision == revision)
            { vm.IsDirty = false; vm.SaveStatus = "Saved"; }
            else if (vm.SourcePath == source)
            { vm.SaveStatus = "Unsaved changes"; autosave.Start(); }
            return true;
        }
        catch (Exception ex)
        {
            vm.SaveStatus = "Save failed";
            vm.Banner = new BannerInfo(StatusKind.Danger, "Could not save edits.", ex.Message,
                new[] { new BannerAction("Retry", () => retry?.Invoke()) });
            return false;
        }
        finally { InProgress = false; }
    }

    /// <summary>
    /// Saves every edit before the window leaves the recording. When a save fails, <paramref name="ask"/>
    /// decides: retry, discard the edits, or stay (false).
    /// </summary>
    /// <param name="first">Runs before the project is saved, such as saving window preferences.</param>
    public async Task<bool> FlushAsync(MainViewModel vm, Func<SaveFailureChoice> ask, Func<Task>? first = null)
    {
        autosave.Stop();
        bool busy = vm.IsBusy;
        vm.IsBusy = true;
        if (first is not null) await first();
        try
        {
            while (vm.IsDirty)
            {
                if (await SaveAsync(vm)) continue;
                var choice = ask();
                if (choice == SaveFailureChoice.Stay) return false;
                if (choice == SaveFailureChoice.Discard) { vm.IsDirty = false; return true; }
            }
            return true;
        }
        finally { vm.IsBusy = busy; }
    }

    /// <summary>
    /// Windows is logging off or shutting down and does not wait for the usual close, which saves in
    /// the background. Writes the pending edits now.
    /// </summary>
    public void SaveNow(MainViewModel vm)
    {
        autosave.Stop();
        if (InProgress || !vm.IsDirty) return;
        if (target() is not { } to || vm.SourcePath is null) return;
        try
        {
            to.Store.Save(Snapshot(vm, vm.SourcePath, to.Fingerprint, to.Media));
            vm.IsDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { log("Save at Windows shutdown", ex); }
    }

    /// <summary>Asks for the window to close once the switch or save under way finishes.</summary>
    public void CloseAfterSwitch() => _closeAfterSwitch = true;

    public void DropPendingClose() => _closeAfterSwitch = false;

    // A close asked for while this window was saving waited for the save. It goes ahead once the
    // edits are saved and is dropped when the user chose to stay. Left set, it would close the
    // window after the next recording opened.
    /// <summary>True when a close was pending; it is requested when <paramref name="proceed"/>.</summary>
    public bool TakePendingClose(bool proceed)
    {
        if (!_closeAfterSwitch) return false;
        _closeAfterSwitch = false;
        if (proceed) requestClose();
        return true;
    }

    public static AdTrimProject Snapshot(MainViewModel vm, string source, SourceFingerprint fingerprint, MediaInfo media)
        => new(AdTrimProject.CurrentSchemaVersion, source, fingerprint, media,
            vm.Markers.Where(m => !m.IsBookend).Select(m => new PersistedSplit(m.Id, m.TimeUs,
                m.Source, m.OriginalTimeUs, m.Confidence, m.Confirmed, m.ChapterTitle)).ToList(),
            vm.ExcludedSegmentIds.ToList(), SidecarLocation.NextToSource, vm.Markers.FirstOrDefault()?.ChapterTitle,
            vm.MarkerSetSource, vm.MarkerBaselineId, vm.LastExport);
}
