using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AdTrim.Services;

namespace AdTrim.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateNotice? _notice;
    private readonly UpdatePackageService _packages;
    private DownloadedUpdate? _download;
    private CancellationTokenSource? _downloadCancellation;
    private Task _downloadTask = Task.CompletedTask;
    private bool _installing;
    public UpdateDialog(UpdateNotice? notice, string? error = null, bool reminder = false, UpdatePackageService? packages = null)
    {
        InitializeComponent();
        _notice = notice;
        _packages = packages ?? new UpdatePackageService(UserPreferences.DataDirectory, App.UpdatePublicKey());
        Heading.Text = notice?.Title ?? (error is null ? "You’re up to date" : "Unable to check for updates");
        Versions.Text = notice is null ? $"Installed: {AppVersion.Display}" : $"Available: v{notice.Version}   ·   Installed: {AppVersion.Display}";
        Explanation.Text = notice?.Summary ?? error ?? "You have the latest available version of AdTrim.";
        if (notice is { Importance: not "regular" }) Heading.Foreground = (Brush)FindResource("State.Warning");
        if (reminder && notice is { Importance: not "regular" })
        {
            Reminder.Text = "You previously postponed this security update. Your installed version is still affected.";
            Reminder.Visibility = Visibility.Visible;
        }
        if (notice is null)
        {
            ReleaseButton.Visibility = UpdateButton.Visibility = ManualInstall.Visibility = Visibility.Collapsed;
            LaterButton.Content = "Close";
        }
        Closing += (_, e) => { if (_installing) e.Cancel = true; else _downloadCancellation?.Cancel(); };
        Closed += (_, _) =>
        {
            _downloadCancellation?.Cancel();
            // Removed now, not after an await: closing the last window ends the app. Partial
            // downloads that cannot finish cancelling are removed at the next startup.
            if (_download is not null && !_installing)
            {
                try { UpdateStaging.RemoveDirectory(UserPreferences.DataDirectory, _download.Directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.WriteCrashLog("Update cleanup", ex); }
            }
            _ = DisposeAfterDownloadAsync();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; OnClose(this, new RoutedEventArgs()); } };
    }
    private async Task DisposeAfterDownloadAsync()
    {
        try { await _downloadTask; }
        finally
        {
            _downloadCancellation?.Dispose();
            _packages.Dispose();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_notice is null || _installing || !_downloadTask.IsCompleted) return;
        if (_download is null)
        {
            _downloadTask = DownloadAsync();
            await _downloadTask;
            return;
        }
        _installing = true;
        UpdateButton.IsEnabled = LaterButton.IsEnabled = ReleaseButton.IsEnabled = false;
        DownloadStatus.Text = "Saving open projects and preparing to restart...";
        try { await ((App)Application.Current).RestartAndInstallUpdateAsync(_download); }
        catch (Exception ex)
        {
            DownloadStatus.Text = ex.Message;
            _installing = false;
            UpdateButton.IsEnabled = LaterButton.IsEnabled = ReleaseButton.IsEnabled = true;
        }
    }

    private async Task DownloadAsync()
    {
        _downloadCancellation?.Dispose();
        _downloadCancellation = new();
        UpdateButton.IsEnabled = ReleaseButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        DownloadStatus.Text = "Connecting to GitHub...";
        try
        {
            _download = await _packages.DownloadAsync(_notice!.Version, AppVersion.Numeric,
                new Progress<UpdateDownloadProgress>(ShowDownloadProgress), _downloadCancellation.Token);
            UpdateButton.Content = "Restart and update";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 1;
            DownloadStatus.Text = "Update downloaded and verified. Ready to restart.";
        }
        catch (OperationCanceledException) { DownloadStatus.Text = "Download cancelled. AdTrim has not been changed."; }
        catch (UpdatePackageUnavailableException ex) { DownloadStatus.Text = ex.Message; UpdateButton.Visibility = Visibility.Collapsed; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException or FormatException)
        { DownloadStatus.Text = "The update could not be verified or downloaded. " + ex.Message; }
        finally
        {
            UpdateButton.IsEnabled = ReleaseButton.IsEnabled = true;
            if (_download is null) DownloadProgress.IsIndeterminate = false;
        }
    }

    // Names the step under way, so a stall or failure says where it happened.
    private void ShowDownloadProgress(UpdateDownloadProgress p)
    {
        if (_download is not null) return;
        DownloadProgress.IsIndeterminate = p.Fraction is null;
        if (p.Fraction is { } fraction) DownloadProgress.Value = fraction;
        DownloadStatus.Text = p.Step switch
        {
            UpdateDownloadStep.GettingInformation => "Getting the update information from GitHub...",
            UpdateDownloadStep.VerifyingSignature => "Verifying the update's signature...",
            UpdateDownloadStep.Downloading => $"Downloading the installer: {(p.Fraction ?? 0) * 100:0}%",
            UpdateDownloadStep.CheckingDownload => "Checking the downloaded installer against its signature...",
            UpdateDownloadStep.CheckingVersion => "Checking the installer's version...",
            _ => "Update downloaded and verified. Ready to restart.",
        };
    }
    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OnRelease(object sender, RoutedEventArgs e)
    {
        if (_notice is null) return;
        // The window stays open, so a finished download is still available after reading the notes.
        try { Process.Start(new ProcessStartInfo(_notice.ReleaseUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Explanation.Text = "The browser could not be opened. Please try again."; }
    }
}
