using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

namespace AdTrim;

public partial class MainWindow
{
    /// <summary>What the smoke tool drives in a recording window.</summary>
    internal TestSurface Test => new(this);

    /// <summary>
    /// The smoke tool's only way into a recording window's private state. When code moves out of
    /// the window, these members follow it and keep their names, so the smoke tool does not change
    /// and a missing member fails the build instead of a smoke run.
    /// </summary>
    internal sealed class TestSurface(MainWindow window)
    {
        // State
        public MpvPreviewViewModel PreviewAfter => window._previewAfter;
        public MpvPreviewViewModel PreviewBefore => window._previewBefore;
        public MediaInfo? Media => window._media;
        public ProjectStore? Store => window._store;
        public UserPreferences Preferences => window._preferences;
        public ExportView? ActiveExport => window._activeExportDialog;
        public DispatcherTimer? RewindTimer => window._rewindTimer;
        public AudioLevelPass? AudioPass => window._audioPass;
        public ThumbnailLoader? ThumbnailLoader => window._thumbnailLoader;
        public bool FrameRecoveryPending => window._frameRecovery is not null;
        public ThumbnailService? Thumbnails { set => window._thumbnails = value; }
        public AudioLevelService? AudioLevels { set => window._audioLevels = value; }
        public Func<string, string, bool> AskDiscardEdits { get => window._askDiscardEdits; set => window._askDiscardEdits = value; }
        /// <summary>Lets the window close without asking, as at the end of a check.</summary>
        public void AllowClose() => window._allowClose = true;

        // Saving and preferences
        public Task<bool> SaveProjectAsync(MainViewModel vm) => window._saves.SaveAsync(vm);
        public Task<bool> SavePreferencesAsync(MainViewModel vm) => window.SavePreferencesAsync(vm);
        public Task<bool> SavePreferencesSnapshotAsync(MainViewModel vm, bool saveMarkerSource) => window.SavePreferencesSnapshotAsync(vm, saveMarkerSource);
        public Task<UserPreferences?> ChangeSettingsAsync(Func<UserPreferences, UserPreferences> change) => window.ChangeSettingsAsync(change);
        public Task<(UserPreferences Saved, string? Kept)?> ResetSettingsAsync() => window.ResetSettingsAsync();

        // Playback
        public void SeekTo(MainViewModel vm, long timeUs, bool exact = true) => window.SeekTo(vm, timeUs, exact);
        public void PlayPauseBoth() => window.PlayPauseBoth();
        public void StepFrame(MainViewModel vm, int direction) => window.StepFrame(vm, direction);
        public void Shuttle(MainViewModel vm, Key key) => window.Shuttle(vm, key);
        public void RewindTick() => window.RewindTick();
        public void StopRewind() => window.StopRewind();
        public void SetCollapsed(MainViewModel vm, bool collapsed) => window.SetCollapsed(vm, collapsed);
        public (long From, long To) VisibleTimelineRange(MainViewModel vm) => window.VisibleTimelineRange(vm);

        // Timeline media
        public void CancelThumbnailLoad(MainViewModel vm, bool clearThumbnails) => window.CancelThumbnailLoad(vm, clearThumbnails);
        public void ReleaseAudioPass(MainViewModel vm) => window.ReleaseAudioPass(vm);

        // Markers
        public void JumpToMarker(MainViewModel vm, int direction) => window.JumpToMarker(vm, direction);
        public void AddSplitAtPlayhead(MainViewModel vm) => window.AddSplitAtPlayhead(vm);
        public void ApplyMarkerSet(MainViewModel vm, MarkerSetState state, string action, bool confirm) => window.ApplyMarkerSet(vm, state, action, confirm);
        public Task<MarkerSetState?> AnalyzeCommercialsAsync(MainViewModel vm, string key, CancellationToken cancellationToken)
            => window.AnalyzeCommercialsAsync(vm, key, cancellationToken);
        public static string? TimeLeftText(TimeSpan elapsed, double done, double remaining) => MainWindow.TimeLeftText(elapsed, done, remaining);

        // Export and source comments
        public Task<string?> AddSourceCommentAsync(MainViewModel vm, ExportView view, CancellationToken ct) => window.AddSourceCommentAsync(vm, view, ct);
        public Task<SourceCommentChoice> ReadSourceCommentChoiceAsync(ExportPlan plan) => window.ReadSourceCommentChoiceAsync(plan);

        // Commands as the menus, toolbar, keyboard and drag-and-drop send them
        public void Export() => window.OnExport(window, new RoutedEventArgs());
        public void CloseProject() => window.OnCloseProject(window, new RoutedEventArgs());
        public void ToggleCollapsed() => window.OnToggleCollapsed(window, new RoutedEventArgs());
        public void FitTimeline() => window.OnFitTimeline(window, new RoutedEventArgs());
        public void MoveSelectedToPlayhead() => window.OnMoveSelectedToPlayhead(window, new RoutedEventArgs());
        public void PreviousSplit() => window.OnPreviousSplit(window, new RoutedEventArgs());
        public void SelectAllMarkers() => window.OnSelectAllMarkers(window, new RoutedEventArgs());
        public void SelectVisibleMarkers() => window.OnSelectVisibleMarkers(window, new RoutedEventArgs());
        public void UseAdTrimSuggestions() => window.OnUseAdTrimSuggestions(window, new RoutedEventArgs());
        public void PreviewKeyDown(KeyEventArgs e) => window.OnPreviewKeyDown(window, e);
        public void Drop(DragEventArgs e) => window.OnDrop(window, e);
    }
}
