using System.Windows;
using System.Windows.Media;
using AdTrim.ViewModels;
using AdTrim.Views;

namespace AdTrim;

public partial class MainWindow
{
    public bool CanAnnounceUpdate => IsLoaded && !_switching && _openingPath is null && !_hydrating
        && !_previewAfter.IsPlaying && _activeExportDialog is null && OwnedWindows.Count == 0
        && DataContext is MainViewModel { IsBusy: false };

    private void InitializeUpdates()
    {
        if (Application.Current is not App app) return;
        app.Updates.Changed += OnUpdatesChanged;
        app.UpdatePreferenceChanged += OnUpdatesChanged;
        Closed += (_, _) => { app.Updates.Changed -= OnUpdatesChanged; app.UpdatePreferenceChanged -= OnUpdatesChanged; };
        PreviewMouseDown += (_, _) => app.UpdateStartupInterrupted = true;
        PreviewKeyDown += (_, _) => app.UpdateStartupInterrupted = true;
        PreviewMouseWheel += (_, _) => app.UpdateStartupInterrupted = true;
        OnUpdatesChanged(this, EventArgs.Empty);
    }

    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        var updates = ((App)Application.Current).Updates;
        AutomaticUpdatesMenu.IsChecked = ((App)Application.Current).AutomaticUpdatesEnabled;
        AutomaticUpdatesMenu.Icon = AutomaticUpdatesMenu.IsChecked ? "\u2713" : null;
        var notice = updates.Notice;
        var brush = (Brush)FindResource(notice is { Importance: not "regular" } ? "State.Warning" : "Accent.Base");
        UpdateHelpDot.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatusButton.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatusButton.Content = notice?.Label;
        UpdateStatusButton.Foreground = brush;
        CheckUpdatesMenu.Header = updates.IsChecking ? "Checking for updates…" : notice is null ? "Check for updates…" : notice.Label + "…";
        CheckUpdatesMenu.IsEnabled = !updates.IsChecking;
        CheckUpdatesMenu.Foreground = notice is null ? (Brush)FindResource("Text.Primary") : brush;
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.OpenUpdateDialog is { } open) { BringToFront(open); return; }
        if (app.Updates.IsChecking) return;
        // A notice shown from the saved copy after a failed check may be out of date.
        if (app.Updates.Notice is null || app.Updates.Error is not null) await app.Updates.CheckAsync();
        if (!IsLoaded) return;
        await ShowUpdateAsync();
    }

    private void OnAutomaticUpdates(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        try { app.SetAutomaticUpdates(AutomaticUpdatesMenu.IsChecked); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            AutomaticUpdatesMenu.IsChecked = app.AutomaticUpdatesEnabled;
            MessageBox.Show(this, "The update preference could not be saved. Please try again.", "Update settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ShowUpdateAsync()
    {
        var app = (App)Application.Current;
        if (app.OpenUpdateDialog is { } open) { BringToFront(open); return; }
        var updates = app.Updates;
        var notice = updates.Notice;
        var dialog = new UpdateDialog(notice, updates.Error, updates.IsReminder) { Owner = this };
        app.OpenUpdateDialog = dialog;
        try
        {
            // Persist only announcements actually presented, not deferred startup checks.
            var presented = false;
            dialog.ContentRendered += (_, _) => presented = true;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            // Opened over this window, but owned by none: closing a recording window must not
            // close the update window or discard its download.
            dialog.Owner = null;
            await closed.Task;
            if (presented && notice is not null) await updates.AcknowledgeAsync();
        }
        finally { if (app.OpenUpdateDialog == dialog) app.OpenUpdateDialog = null; }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
