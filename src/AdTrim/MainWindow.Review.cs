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
    private UserPreferences _preferences = new();
    private UserPreferences _savedPreferences = new();
    private async Task LoadPreferencesAsync(MainViewModel vm)
    {
        _preferences = await UserPreferences.LoadAsync();
        _savedPreferences = _preferences;
        if (await UserPreferences.IsDamagedAsync()) ShowDamagedPreferencesBanner();
        // Start muted applies when the window opens; the mute button then switches it for the session.
        _previewBefore.SetMuted(_preferences.StartMuted);
        ShowMuteState(_preferences.StartMuted);
        vm.ShowWaveform = _preferences.ShowWaveform;
        vm.ShowThumbnails = _preferences.ShowThumbnails;
        vm.ShowRulerTicks = _preferences.ShowRulerTicks;
        vm.SectionNameTemplate = _preferences.SectionNameTemplate;
        var desired = new Rect(double.IsFinite(_preferences.Left) ? _preferences.Left : 0,
            double.IsFinite(_preferences.Top) ? _preferences.Top : 0,
            Math.Max(1, double.IsFinite(_preferences.Width) ? _preferences.Width : 1280),
            Math.Max(1, double.IsFinite(_preferences.Height) ? _preferences.Height : 720));
        var area = MonitorBounds.WorkArea(this, desired);
        // Older builds saved a work-area-sized normal window as fake maximize.
        bool legacyMaximized = _preferences.IsMaximized is null
            && Math.Abs(desired.Left - area.Left) <= 2 && Math.Abs(desired.Top - area.Top) <= 2
            && Math.Abs(desired.Width - area.Width) <= 2 && Math.Abs(desired.Height - area.Height) <= 2;
        if (legacyMaximized) _preferences = _preferences with { Width = 1280, Height = 720 };
        Width = Math.Clamp(double.IsFinite(_preferences.Width) ? _preferences.Width : 1280, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(double.IsFinite(_preferences.Height) ? _preferences.Height : 720, MinHeight, Math.Max(MinHeight, area.Height));
        Left = Math.Clamp(double.IsFinite(_preferences.Left) ? _preferences.Left : area.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(double.IsFinite(_preferences.Top) ? _preferences.Top : area.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        if (_preferences.IsMaximized == true || legacyMaximized) { SystemCommands.MaximizeWindow(this); return; }
        // Every window restores the same saved position; step aside instead of hiding the one already there.
        const double step = 28;
        while (Left + step + Width <= area.Right && Top + step + Height <= area.Bottom
            && Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && w.WindowState == WindowState.Normal
                && Math.Abs(w.Left - Left) < 1 && Math.Abs(w.Top - Top) < 1))
        {
            Left += step;
            Top += step;
        }
    }
    private Task<bool> SavePreferencesAsync(MainViewModel vm) => SavePreferencesSnapshotAsync(vm, false);

    private async Task<bool> SavePreferencesSnapshotAsync(MainViewModel vm, bool saveMarkerSource)
    {
        var positions = new Dictionary<string, long>(_preferences.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
        if (vm.SourcePath is { } source)
        {
            positions.Remove(source);
            positions[source] = vm.PlayheadUs;
        }
        while (positions.Count > 20) positions.Remove(positions.Keys.First());
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _preferences = _preferences with { ShowWaveform = vm.ShowWaveform, ShowThumbnails = vm.ShowThumbnails,
            ShowRulerTicks = vm.ShowRulerTicks,
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            IsMaximized = _lastNonMinimizedState == WindowState.Maximized, Positions = positions };
        try
        {
            var snapshot = _preferences;
            await snapshot.SaveChangesAsync(_savedPreferences, vm.SourcePath, saveMarkerSource);
            _savedPreferences = snapshot;
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
        vm.Banner = new BannerInfo(StatusKind.Warning, "Saved settings are damaged.",
            "Settings changes cannot be saved until they are reset. Reset keeps a copy of the damaged file and saves the settings this window is using now"
            + (_preferences.AddSourceExportComment ? "." : ", with source export comments off."),
            new[] { new BannerAction("Reset settings", () => _ = ResetDamagedPreferencesAsync(), IsPrimary: true) });
    }

    private async Task ResetDamagedPreferencesAsync()
    {
        try
        {
            var snapshot = _preferences;
            var kept = await UserPreferences.ResetDamagedAsync(snapshot);
            _savedPreferences = snapshot;
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
        var latest = await UserPreferences.LoadAsync(_preferences);
        var settings = new SettingsWindow(_preferences.WithSettingsFrom(latest), ChangeSettingsAsync, ResetSettingsAsync) { Owner = this };
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
        _preferences = _preferences.WithSettingsFrom(saved);
        _savedPreferences = _savedPreferences.WithSettingsFrom(saved);
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
        _playUntilUs = null;
        var sourceUs = vm.PlayheadUs;
        var playing = _previewAfter.IsPlaying;
        var muted = vm.IsCollapsed ? _previewAfter.IsMuted : _previewBefore.IsMuted;
        _previewAfter.Pause();
        _previewBefore.Stop();
        vm.ClearSelection();
        vm.RefreshKeptTimeline();
        vm.IsCollapsed = collapsed;
        SetRecordingPaneLayout(collapsed);
        if (collapsed)
        {
            _previewAfter.SetMuted(muted);
            var map = vm.KeptTimeline;
            var outputUs = map.ToOutput(sourceUs);
            vm.PlayheadUs = map.ToSource(outputUs);
            if (map.DurationUs == 0) _previewAfter.Stop();
            else _previewAfter.Open(map.ToMpvEdl(vm.SourcePath, _media?.TimelineStartTimeUs ?? 0), outputUs, playing, buffered: true);
            vm.StatusOverride = map.DurationUs == 0 ? "All scenes are excluded." : "Collapsed preview. Click an eye icon to review that cut.";
        }
        else
        {
            _previewAfter.SetMuted(true);
            _previewBefore.SetMuted(muted);
            _previewAfter.Open(vm.SourcePath, sourceUs, playing);
            _previewBefore.Open(vm.SourcePath, Math.Max(0, sourceUs - FrameDurationUs(vm)), playing);
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
