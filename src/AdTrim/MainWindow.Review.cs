using System.Windows;
using System.Windows.Controls;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.ViewModels;
using AdTrim.Services;
using AdTrim.Views;
using System.IO;

namespace AdTrim;

public partial class MainWindow
{
    private readonly WindowPreferences _prefs = new();

    private async Task LoadPreferencesAsync(MainViewModel vm)
    {
        _prefs.Loaded(await UserPreferences.LoadAsync());
        if (await UserPreferences.IsDamagedAsync()) ShowDamagedPreferencesBanner();
        // Start muted applies when the window opens; the mute button then switches it for the session.
        _playback.Before.SetMuted(_prefs.Current.StartMuted);
        ShowMuteState(_prefs.Current.StartMuted);
        vm.ShowWaveform = _prefs.Current.ShowWaveform;
        vm.ShowThumbnails = _prefs.Current.ShowThumbnails;
        vm.ShowRulerTicks = _prefs.Current.ShowRulerTicks;
        vm.SectionNameTemplate = _prefs.Current.SectionNameTemplate;
        var otherWindows = Application.Current.Windows.OfType<MainWindow>().Where(w => w != this && w.WindowState == WindowState.Normal).ToList();
        WindowPlacement place;
        // Pixels when saved: WPF's Left/Top are stale after a move between monitors with different scaling.
        if (_prefs.Current.WindowPixels is { } pixels && MonitorBounds.PixelWorkArea(pixels) is { } monitor)
        {
            var others = otherWindows.Select(MonitorBounds.NormalPixels).OfType<PixelBounds>().Select(p => new Point(p.Left, p.Top)).ToList();
            place = _prefs.PlacePixels(pixels, monitor.Area, monitor.Scale, MinWidth, MinHeight, others);
            MonitorBounds.SetPixels(this, new((int)place.Left, (int)place.Top, (int)place.Width, (int)place.Height));
        }
        else
        {
            var area = MonitorBounds.WorkArea(this, _prefs.Desired);
            var others = otherWindows.Select(w => new Point(w.Left, w.Top)).ToList();
            place = _prefs.Place(area, MinWidth, MinHeight, others);
            Width = place.Width;
            Height = place.Height;
            Left = place.Left;
            Top = place.Top;
        }
        RememberNormalPixels();
        if (place.Maximize) SystemCommands.MaximizeWindow(this);
    }
    private Task<bool> SavePreferencesAsync(MainViewModel vm) => SavePreferencesSnapshotAsync(vm, false);

    private async Task<bool> SavePreferencesSnapshotAsync(MainViewModel vm, bool saveMarkerSource)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var snapshot = _prefs.Snapshot(vm, bounds, _lastNonMinimizedState == WindowState.Maximized,
            MonitorBounds.NormalPixels(this) ?? _lastNormalPixels);
        try
        {
            await _prefs.SaveAsync(snapshot, vm.SourcePath, saveMarkerSource);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.StatusOverride = "Preferences could not be saved. Project edits are saved separately.";
            if (ex is DamagedPreferencesException) ShowDamagedPreferencesBanner();
            return false;
        }
    }

    // Saves refuse to overwrite a damaged settings file; this offers the way out.
    private void ShowDamagedPreferencesBanner()
    {
        if (DataContext is not MainViewModel vm) return;
        vm.Banner = new BannerInfo(StatusKind.Warning, "Saved settings are damaged.", _prefs.DamagedBannerBody,
            new[] { new BannerAction("Reset settings", () => _ = ResetDamagedPreferencesAsync(), IsPrimary: true) });
    }

    private async Task ResetDamagedPreferencesAsync()
    {
        try
        {
            var kept = await _prefs.ResetDamagedAsync();
            if (DataContext is MainViewModel vm)
                vm.Banner = kept is null ? null : new BannerInfo(StatusKind.Success, "Settings were reset.",
                    $"The damaged file was kept as {Path.GetFileName(kept)}.", Array.Empty<BannerAction>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Settings could not be reset. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- Settings window ----
    private void OnSettings(object sender, RoutedEventArgs e) => _ = OpenSettingsAsync();

    internal async Task OpenSettingsAsync()
    {
        if (OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        // Another window may have changed a setting since this one loaded them.
        var latest = await UserPreferences.LoadAsync(_prefs.Current);
        var settings = new SettingsWindow(_prefs.Current.WithSettingsFrom(latest), ChangeSettingsAsync, ResetSettingsAsync, CheckForUpdatesAsync) { Owner = this };
        settings.ShowDialog();
    }

    /// <summary>Saves one settings change and shows it in every recording window. Null when it could not be saved.</summary>
    internal async Task<UserPreferences?> ChangeSettingsAsync(Func<UserPreferences, UserPreferences> change)
    {
        try
        {
            var saved = await UserPreferences.UpdateAsync(change);
            foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.ApplySharedSettings(saved);
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex is DamagedPreferencesException) ShowDamagedPreferencesBanner();
            MessageBox.Show(OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() ?? (Window)this,
                ex is DamagedPreferencesException
                    ? "The settings file is damaged, so changes cannot be saved. Choose Restore defaults under Storage and save, or close Settings and choose Reset settings in the banner."
                    : "The setting could not be saved. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private async Task<(UserPreferences Saved, string? Kept)?> ResetSettingsAsync()
    {
        try
        {
            var reset = await UserPreferences.ResetAllAsync();
            foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.ApplySharedSettings(reset.Saved);
            if (DataContext is MainViewModel { Banner.Title: "Saved settings are damaged." } vm) vm.Banner = null;
            return reset;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() ?? (Window)this,
                "Settings could not be reset. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Shows settings another window saved. They are saved already, so this window's later saves must not undo them.</summary>
    private void ApplySharedSettings(UserPreferences saved)
    {
        _prefs.ApplyShared(saved);
        if (DataContext is not MainViewModel vm) return;
        vm.ShowWaveform = saved.ShowWaveform;
        vm.ShowThumbnails = saved.ShowThumbnails;
        vm.ShowRulerTicks = saved.ShowRulerTicks;
        vm.SectionNameTemplate = saved.SectionNameTemplate;
    }

    private void OnToggleCollapsed(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsFileLoaded && !vm.IsBusy)
            SetCollapsed(vm, !vm.IsCollapsed);
    }

    private void SetCollapsed(MainViewModel vm, bool collapsed)
    {
        if (collapsed == vm.IsCollapsed || vm.SourcePath is null) return;
        _playback.PlayUntilUs = null;
        var sourceUs = vm.PlayheadUs;
        var playing = _playback.After.IsPlaying;
        var muted = vm.IsCollapsed ? _playback.After.IsMuted : _playback.Before.IsMuted;
        _playback.After.Pause();
        _playback.Before.Stop();
        vm.ClearSelection();
        vm.RefreshKeptTimeline();
        vm.IsCollapsed = collapsed;
        SetRecordingPaneLayout(collapsed);
        if (collapsed)
        {
            _playback.After.SetMuted(muted);
            var map = vm.KeptTimeline;
            var outputUs = map.ToOutput(sourceUs);
            vm.PlayheadUs = map.ToSource(outputUs);
            if (map.DurationUs == 0) _playback.After.Stop();
            else _playback.After.Open(map.ToMpvEdl(vm.SourcePath, _media?.TimelineStartTimeUs ?? 0), outputUs, playing, buffered: true);
            vm.StatusOverride = map.DurationUs == 0 ? "All scenes are excluded." : "Collapsed preview. Click an eye icon to review that cut.";
        }
        else
        {
            _playback.After.SetMuted(true);
            _playback.Before.SetMuted(muted);
            _playback.After.Open(vm.SourcePath, sourceUs, playing);
            _playback.Before.Open(vm.SourcePath, Math.Max(0, sourceUs - PlaybackController.FrameDurationUs(vm)), playing);
            vm.PlayheadUs = sourceUs;
            MpvViewBefore.Visibility = Visibility.Visible;
            UpdateNoFrameOverlay(vm);
            vm.StatusOverride = null;
        }
        UpdateTimelineLayout(true);
        ApplyAspectFit();
    }

    private void SetRecordingPaneLayout(bool collapsed)
    {
        CollapseToggle.IsChecked = collapsed;
        PaneAGrid.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(PaneBGrid, collapsed ? 0 : 1);
        Grid.SetColumnSpan(PaneBGrid, collapsed ? 2 : 1);
        NoFrameOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnConfirmAndNext(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanEdit) return;
        if (vm.SelectedMarker is { Confirmed: false } marker)
            vm.CommandStack.Execute(new SetConfirmedCommand(new[] { marker }, true));
        OnJumpNextUnconfirmed(sender, e);
    }
}
