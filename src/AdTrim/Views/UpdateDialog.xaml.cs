using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AdTrim.Services;

namespace AdTrim.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateNotice? _notice;
    public UpdateDialog(UpdateNotice? notice, string? error = null, bool reminder = false)
    {
        InitializeComponent();
        _notice = notice;
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
            ReleaseButton.Visibility = ManualInstall.Visibility = Visibility.Collapsed;
            LaterButton.Content = "Close";
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OnRelease(object sender, RoutedEventArgs e)
    {
        if (_notice is null) return;
        try { Process.Start(new ProcessStartInfo(_notice.ReleaseUri.AbsoluteUri) { UseShellExecute = true }); Close(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Explanation.Text = "The browser could not be opened. Please try again."; }
    }
}
