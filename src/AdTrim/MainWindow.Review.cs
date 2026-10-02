using System.Windows;
using System.Windows.Controls;
using AdTrim.Commands;
using AdTrim.ViewModels;
using AdTrim.Services;
using System.IO;

namespace AdTrim;

public partial class MainWindow
{
    private UserPreferences _preferences = new();
    private async Task LoadPreferencesAsync(MainViewModel vm)
    {
        _preferences = await UserPreferences.LoadAsync();
        vm.ShowWaveform = _preferences.ShowWaveform;
        vm.ShowThumbnails = _preferences.ShowThumbnails;
        var desired = new Rect(double.IsFinite(_preferences.Left) ? _preferences.Left : 0,
            double.IsFinite(_preferences.Top) ? _preferences.Top : 0,
            Math.Max(1, double.IsFinite(_preferences.Width) ? _preferences.Width : 1280),
            Math.Max(1, double.IsFinite(_preferences.Height) ? _preferences.Height : 720));
        var area = MonitorBounds.WorkArea(this, desired);
        Width = Math.Clamp(double.IsFinite(_preferences.Width) ? _preferences.Width : 1280, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(double.IsFinite(_preferences.Height) ? _preferences.Height : 720, MinHeight, Math.Max(MinHeight, area.Height));
        Left = Math.Clamp(double.IsFinite(_preferences.Left) ? _preferences.Left : area.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(double.IsFinite(_preferences.Top) ? _preferences.Top : area.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    private async Task SavePreferencesAsync(MainViewModel vm)
    {
        var positions = new Dictionary<string, long>(_preferences.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
        if (vm.SourcePath is { } source)
        {
            positions.Remove(source);
            positions[source] = vm.PlayheadUs;
        }
        while (positions.Count > 20) positions.Remove(positions.Keys.First());
        _preferences = _preferences with { ShowWaveform = vm.ShowWaveform, ShowThumbnails = vm.ShowThumbnails,
            Left = Left, Top = Top, Width = Width, Height = Height, Positions = positions };
        try { await _preferences.SaveAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { vm.StatusOverride = "Preferences could not be saved. Project edits are saved separately."; }
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
        CollapseToggle.IsChecked = collapsed;
        PaneAGrid.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(PaneBGrid, collapsed ? 0 : 1);
        Grid.SetColumnSpan(PaneBGrid, collapsed ? 2 : 1);
        NoFrameOverlay.Visibility = Visibility.Collapsed;
        if (collapsed)
        {
            _previewAfter.SetMuted(muted);
            var map = vm.KeptTimeline;
            var outputUs = map.ToOutput(sourceUs);
            vm.PlayheadUs = map.ToSource(outputUs);
            if (map.DurationUs == 0) _previewAfter.Stop();
            else _previewAfter.Open(map.ToMpvEdl(vm.SourcePath), outputUs, playing, buffered: true);
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

    private void OnConfirmAndNext(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanEdit) return;
        if (vm.SelectedMarker is { Confirmed: false } marker)
            vm.CommandStack.Execute(new SetConfirmedCommand(new[] { marker }, true));
        OnJumpNextUnconfirmed(sender, e);
    }
}
