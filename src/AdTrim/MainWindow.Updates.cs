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
        Closed += (_, _) => app.Updates.Changed -= OnUpdatesChanged;
        PreviewMouseDown += (_, _) => app.UpdateStartupInterrupted = true;
        PreviewKeyDown += (_, _) => app.UpdateStartupInterrupted = true;
        PreviewMouseWheel += (_, _) => app.UpdateStartupInterrupted = true;
        OnUpdatesChanged(this, EventArgs.Empty);
    }

    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        var updates = ((App)Application.Current).Updates;
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
        if (app.UpdateDialogOpen || app.Updates.IsChecking) return;
        if (app.Updates.Notice is null) await app.Updates.CheckAsync();
        if (!IsLoaded) return;
        await ShowUpdateAsync();
    }

    public async Task ShowUpdateAsync()
    {
        var app = (App)Application.Current;
        if (app.UpdateDialogOpen) return;
        app.UpdateDialogOpen = true;
        try
        {
            var updates = app.Updates;
            var notice = updates.Notice;
            var dialog = new UpdateDialog(notice, updates.Error, updates.IsReminder) { Owner = this };
            // Persist only announcements actually presented, not deferred startup checks.
            var presented = false;
            dialog.ContentRendered += (_, _) => presented = true;
            dialog.ShowDialog();
            if (presented && notice is not null) await updates.AcknowledgeAsync();
        }
        finally { app.UpdateDialogOpen = false; }
    }
}
