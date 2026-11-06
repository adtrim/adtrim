using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdTrim.Services;

namespace AdTrim.Views;

public enum UpdateChoice { Retry, Cancel }

/// <summary>
/// The update helper's window: shows each installation step as it happens and, when one fails, explains
/// what happened and offers Retry or Cancel update, with details that can be copied into a GitHub issue.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public static readonly Uri IssuesUri = new("https://github.com/adtrim/adtrim/issues/new");
    public static TimeSpan InstallerWarningAfter { get; set; } = TimeSpan.FromMinutes(5);

    public sealed record StepRow(string Glyph, string Text, Brush Brush);

    private static readonly (UpdateStep Step, string Text)[] UpdateSteps =
    [
        (UpdateStep.WaitingForClose, "Waiting for AdTrim to close"),
        (UpdateStep.BackingUp, "Backing up the current version"),
        (UpdateStep.Verifying, "Checking the downloaded installer"),
        (UpdateStep.Installing, "Installing the update"),
        (UpdateStep.CheckingVersion, "Checking the new version"),
        (UpdateStep.Starting, "Starting AdTrim and reopening your recordings"),
    ];
    private static readonly (UpdateStep Step, string Text)[] RestoreSteps =
    [
        (UpdateStep.Restoring, "Restoring the previous version"),
        (UpdateStep.StartingPrevious, "Starting AdTrim and reopening your recordings"),
    ];

    private readonly ObservableCollection<StepRow> _rows = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _stepTimer = new();
    private readonly string _fromVersion;
    private string? _toVersion;
    private UpdateStep _step = UpdateStep.WaitingForClose;
    private bool _restoring;
    private bool _failed;
    private TaskCompletionSource<UpdateChoice>? _choice;
    private string _details = "";
    private bool _allowClose;

    /// <summary>Raised when the user asks to stop an installer that has stopped responding.</summary>
    public event EventHandler? StopRequested;

    public UpdateProgressWindow(string fromVersion)
    {
        InitializeComponent();
        _fromVersion = fromVersion;
        StepList.ItemsSource = _rows;
        Versions.Text = $"Installed: v{fromVersion}";
        _clock.Tick += (_, _) => RefreshStatus();
        // The window stays until recovery is finished: closing it while a step has failed means Cancel update,
        // and later steps or errors still need it. Only CloseWhenFinished closes it.
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_failed) _choice?.TrySetResult(UpdateChoice.Cancel);
        };
        Closed += (_, _) => { _clock.Stop(); _choice?.TrySetResult(UpdateChoice.Cancel); };
        ShowStep(UpdateStep.WaitingForClose);
        _clock.Start();
    }

    public UpdateStep Step => _step;
    public string Details => _details;

    public void SetTarget(string? version)
    {
        _toVersion = version;
        if (version is not null) Versions.Text = $"v{_fromVersion}  →  v{version}";
    }

    public void ShowStep(UpdateStep step)
    {
        if (step is UpdateStep.Restoring or UpdateStep.StartingPrevious && !_restoring)
        {
            _restoring = true;
            Heading.Text = "Restoring AdTrim";
            Explanation.Text = "The update is being undone. Your projects and settings were saved; your recordings reopen in the previous version.";
        }
        if (step == UpdateStep.Done) Heading.Text = _restoring ? "AdTrim was restored" : "AdTrim was updated";
        _step = step;
        _failed = false;
        _stepTimer.Restart();
        ErrorPanel.Visibility = ReportButton.Visibility = CopyButton.Visibility = RetryButton.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        RenderSteps(failed: false);
        RefreshStatus();
    }

    /// <summary>Shows what failed and waits for Retry or Cancel. Cancel restores the previous version when anything was installed.</summary>
    public Task<UpdateChoice> ShowErrorAsync(string message, string? cancelLabel, string details, bool canRetry = true)
    {
        _failed = true;
        _details = Sanitize(details);
        _stepTimer.Stop();
        RenderSteps(failed: true);
        ErrorTitle.Text = StepText(_step) + " did not finish.";
        ErrorMessage.Text = Sanitize(message);
        ErrorHelp.Text = "Details were also saved to " + Sanitize(App.CrashLogPath)
            + ". To report the problem, choose Copy details, then Report a problem and paste them into a new GitHub issue.";
        ErrorPanel.Visibility = ReportButton.Visibility = CopyButton.Visibility = Visibility.Visible;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = cancelLabel ?? "Close";
        CancelButton.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        Status.Text = "";
        _choice = new TaskCompletionSource<UpdateChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        Activate();
        return _choice.Task;
    }

    public void CloseWhenFinished()
    {
        _allowClose = true;
        Close();
    }

    private void RenderSteps(bool failed)
    {
        var steps = _restoring ? RestoreSteps : UpdateSteps;
        int current = Array.FindIndex(steps, s => s.Step == _step);
        var done = (Brush)FindResource("State.Success");
        var active = (Brush)FindResource("Text.Primary");
        var pending = (Brush)FindResource("Text.Tertiary");
        var danger = (Brush)FindResource("State.Danger");
        _rows.Clear();
        for (int i = 0; i < steps.Length; i++)
        {
            bool finished = _step == UpdateStep.Done || i < current;
            _rows.Add(finished ? new("✓", steps[i].Text, done)
                : i == current ? (failed ? new("✕", steps[i].Text, danger) : new("▶", steps[i].Text + "…", active))
                : new("○", steps[i].Text, pending));
        }
    }

    private void RefreshStatus()
    {
        if (_failed || _step == UpdateStep.Done) return;
        var elapsed = _stepTimer.Elapsed;
        Status.Text = _step == UpdateStep.Installing && elapsed > TimeSpan.FromSeconds(10)
            ? $"The installer has been running for {elapsed:m\\:ss}."
            : "";
        if (_step == UpdateStep.Installing && elapsed > InstallerWarningAfter && StopButton.Visibility != Visibility.Visible)
        {
            Status.Text = $"The installer has been running for {elapsed:m\\:ss}, which is longer than expected. "
                + "You can keep waiting, or stop it and restore the previous version.";
            StopButton.Visibility = Visibility.Visible;
        }
    }

    private static string StepText(UpdateStep step) => step switch
    {
        UpdateStep.WaitingForClose => "Closing AdTrim",
        UpdateStep.Verifying => "Checking the downloaded installer",
        UpdateStep.BackingUp => "Backing up the current version",
        UpdateStep.Installing => "Installing the update",
        UpdateStep.CheckingVersion => "Checking the new version",
        UpdateStep.Starting => "Starting the new version",
        UpdateStep.Restoring => "Restoring the previous version",
        UpdateStep.StartingPrevious => "Starting the previous version",
        _ => "The update",
    };

    /// <summary>Text for a public issue: personal folders replaced by their variable names.</summary>
    public static string Sanitize(string text)
    {
        foreach (var (folder, name) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        })
            if (!string.IsNullOrEmpty(folder)) text = text.Replace(folder, name, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _choice?.TrySetResult(UpdateChoice.Retry);
    private void OnCancel(object sender, RoutedEventArgs e) => _choice?.TrySetResult(UpdateChoice.Cancel);
    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Status.Text = "Stopping the installer…";
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_details); CopyButton.Content = "Copied"; }
        catch (System.Runtime.InteropServices.ExternalException) { CopyButton.Content = "Copy failed; try again"; }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(IssuesUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ErrorHelp.Text = "The browser could not be opened. Report the problem at " + IssuesUri.AbsoluteUri; }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    /// <summary>The text placed on the clipboard for an issue report.</summary>
    public static string FormatDetails(string fromVersion, string? toVersion, UpdateStep step, Exception error) => string.Join(Environment.NewLine,
        "AdTrim update problem",
        $"From version: {fromVersion}",
        $"To version: {toVersion ?? "unknown"}",
        $"Step: {StepText(step)}",
        $"Error: {error.GetType().Name}: {error.Message}",
        $"Time (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}",
        $"Windows: {Environment.OSVersion.VersionString}");
}
