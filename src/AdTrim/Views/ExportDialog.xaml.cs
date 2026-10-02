using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

namespace AdTrim.Views;

public partial class ExportDialog : Window
{
    private ExportDialogViewModel? _vm;

    public ExportPlan? AcceptedPlan { get; private set; }

    /// <summary>
    /// The export task, exposed so the caller (MainWindow) can <c>await</c> it
    /// after the dialog closes - covers the "Run in background" case, where
    /// the dialog disappears but the export should keep running to completion
    /// with progress still reflected in the main window's status bar.
    /// </summary>
    public Task? ExportTask { get; private set; }

    /// <summary>Outcome of the dialog. Lets the caller distinguish a clean
    /// completion ("export done while you waited") from a backgrounded run
    /// ("dialog hidden, task is still in flight").</summary>
    public ExportDialogOutcome Outcome { get; private set; } = ExportDialogOutcome.Cancelled;

    /// <summary>True if the user ticked "delete sidecar after export". Read
    /// by MainWindow on the success path; the dialog itself does not delete.</summary>
    public bool ShouldDeleteSidecarAfterSuccess => _vm?.DeleteSidecarAfterExport ?? false;

    /// <summary>True while an export task is running (regardless of whether
    /// the dialog itself is currently visible - "Run in background" hides it
    /// but the task keeps going). Drives MainWindow's "reuse existing dialog
    /// vs create new one" decision on a second Export click.</summary>
    public bool IsExportInFlight { get; private set; }

    /// <summary>Fired when the export task reaches a terminal state
    /// (Completed / Failed / Cancelled). NOT fired on "Run in background" -
    /// the task is still running then. MainWindow subscribes to drive the
    /// post-export banner and optional sidecar deletion.</summary>
    public event EventHandler? ExportFinished;

    private FfmpegRunner? _runner;
    private readonly CancellationTokenSource _detectionCts = new();
    private string? _preferredAcceleration;
    private IProgress<ExportProgress>? _externalProgress;
    private CancellationTokenSource? _exportCts;
    private bool _heightResized;
    private bool _progressPositioned;

    public ExportDialog()
    {
        InitializeComponent();
        // Escape is the dialog-wide "back out" key - but the right action
        // depends on which state we're in. Configuring → cancel; exporting →
        // run-in-background (same as the footer button, per the spec - the
        // export task should keep running); terminal → close.
        KeyDown += OnDialogKeyDown;
        Loaded += async (_, _) =>
        {
            if (_vm is { IsConfiguring: false }) InitializeProgressSize();
            CenterWindow();
            _progressPositioned = _vm is { IsConfiguring: false };
            await LoadOptionsAsync();
        };
        LocationChanged += (_, _) => UpdateProgressSizing();
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded || _vm is null || _vm.IsConfiguring) return;
            var area = MonitorBounds.WorkArea(this);
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        };
        Closed += (_, _) => { _detectionCts.Cancel(); _detectionCts.Dispose(); };
        Closing += (_, e) => { if (IsExportInFlight) { e.Cancel = true; Hide(); } };
    }

    private void OnDialogKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (_vm is null) { Close(); e.Handled = true; return; }
        if (_vm.IsExporting) OnRunInBackground(this, new RoutedEventArgs());
        else if (_vm.IsConfiguring) OnCancel(this, new RoutedEventArgs());
        else OnCloseTerminal(this, new RoutedEventArgs());
        e.Handled = true;
    }

    public void Bind(MainViewModel project, MediaInfo? media, string? preferredAcceleration = null)
    {
        _preferredAcceleration = preferredAcceleration;
        _vm = new ExportDialogViewModel(project, media);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExportDialogViewModel.IsConfiguring))
            {
                UpdateProgressSizing();
                if (IsLoaded && !_vm.IsConfiguring && !_progressPositioned)
                {
                    _progressPositioned = true;
                    InitializeProgressSize();
                    CenterWindow();
                }
            }
        };
        _vm.RefreshValidation();
    }

    private void UpdateProgressSizing()
    {
        if (!IsLoaded || _vm is null || _vm.IsConfiguring) return;
        MaxHeight = MonitorBounds.WorkArea(this).Height * (_heightResized ? 1 : 0.75);

    }

    private void InitializeProgressSize()
    {
        var area = MonitorBounds.WorkArea(this);
        MaxHeight = area.Height * 0.75;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Min(1060, area.Width);
        Height = Math.Min(751, MaxHeight);
    }

    private void CenterWindow()
    {
        UpdateLayout();
        var area = MonitorBounds.WorkArea(this);
        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
    }

    private void OnResizeWindow(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_vm is null || _vm.IsConfiguring || sender is not FrameworkElement { Tag: string edge }) return;
        var area = MonitorBounds.WorkArea(this);
        var left = Left;
        var top = Top;
        var right = left + ActualWidth;
        var bottom = top + ActualHeight;
        var minimumWidth = Math.Min(1060, area.Width);
        if (edge.Contains("Left"))
            left = Math.Clamp(left + e.HorizontalChange, area.Left, Math.Max(area.Left, right - minimumWidth));
        if (edge.Contains("Right"))
            right = Math.Clamp(right + e.HorizontalChange, Math.Min(area.Right, left + minimumWidth), area.Right);
        Width = right - left;
        Left = left;
        UpdateLayout();

        // Keep the summary and actions visible while the details scroll.
        var minimumHeight = Math.Min(area.Height, ProgressSummary.ActualHeight + ProgressSummary.Margin.Top
            + ProgressFooter.ActualHeight + 88
            + (ProgressError.IsVisible ? ProgressError.ActualHeight + 12 : 0));
        if (edge.Contains("Top") || edge.Contains("Bottom"))
        {
            _heightResized = true;
            MaxHeight = area.Height;
            SizeToContent = SizeToContent.Manual;
            if (edge.Contains("Top"))
                top = Math.Clamp(top + e.VerticalChange, area.Top, Math.Max(area.Top, bottom - minimumHeight));
            else
                bottom = Math.Clamp(bottom + e.VerticalChange, Math.Min(area.Bottom, top + minimumHeight), area.Bottom);
            Height = bottom - top;
            Top = top;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Inject the export service + a progress sink that lives outside the
    /// dialog (typically MainWindow's status bar updater). The dialog drives
    /// the export itself so the progress view stays live; tee'd updates keep
    /// MainWindow's status bar coherent for the "Run in background" case
    /// where the dialog closes mid-flight.
    /// </summary>
    public void AttachExportRunner(FfmpegRunner runner, IProgress<ExportProgress> externalProgress)
    {
        _runner = runner;
        _externalProgress = externalProgress;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // The dialog is shown modeless (MainWindow calls Show(), not ShowDialog()),
        // so DialogResult must NOT be assigned here - that throws on a non-modal
        // window. Outcome + Close() is what callers read.
        Outcome = ExportDialogOutcome.Cancelled;
        Close();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = _vm?.OutputFolder ?? "",
        };
        if (dlg.ShowDialog(this) == true && _vm is not null)
        {
            _vm.OutputFolder = dlg.FolderName;
            _vm.RefreshValidation();
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        // Modeless dialog - do not set DialogResult anywhere (it throws on
        // a non-modal window). See OnCancel for the same constraint.
        if (_vm is null) { Outcome = ExportDialogOutcome.Cancelled; Close(); return; }
        if (!_vm.CanCheckHardware || IsExportInFlight) return;

        _vm.RefreshValidation();
        var blocking = _vm.ValidationIssues.Where(i => i.Kind == ExportValidationKind.Blocking).ToList();

        // Single-click overwrite acknowledgement: if the *only* blocker is the
        // file-exists rule, flip OverwriteConfirmed and re-validate. Button
        // text already reads "Export & overwrite" via OutputFileExists, so the
        // user has seen the warning before clicking.
        if (blocking.Count == 1
            && blocking[0].Message.StartsWith("A file with this name", StringComparison.Ordinal)
            && !_vm.OverwriteConfirmed)
        {
            _vm.OverwriteConfirmed = true;
            _vm.RefreshValidation();
            blocking = _vm.ValidationIssues.Where(i => i.Kind == ExportValidationKind.Blocking).ToList();
        }

        if (blocking.Count > 0) return;   // inline banner already showing

        AcceptedPlan = _vm.BuildPlan();
        if (AcceptedPlan is null || _runner is null)
        {
            // Should not happen - MainWindow attaches the runner before showing
            // the dialog. Fall back to legacy "close + caller runs export".
            Outcome = AcceptedPlan is not null ? ExportDialogOutcome.LegacyAcceptedPlan : ExportDialogOutcome.Cancelled;
            Close();
            return;
        }

        await StartExportAsync();
    }

    private async Task StartExportAsync()
    {
        if (_vm is null || _runner is null || AcceptedPlan is null || IsExportInFlight) return;

        // Switch to the progress view and kick off the export. The dialog
        // *stays open* while it runs; cancel / run-in-background buttons drive
        // the lifecycle from here on.
        _vm.BeginExport(AcceptedPlan);
        _exportCts = new CancellationTokenSource();

        var dialogProgress = new Progress<ExportProgress>(p =>
        {
            _vm.UpdateProgress(p);
            // Tee the same tick to MainWindow's status bar updater so
            // "Run in background" → "close dialog" doesn't lose progress.
            _externalProgress?.Report(p);
        });

        IsExportInFlight = true;
        ExportTask = RunExportWithRecoveryAsync(dialogProgress, _exportCts.Token);
        try
        {
            await ExportTask;
            _vm.MarkComplete();
            Outcome = ExportDialogOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            _vm.MarkCancelled();
            Outcome = ExportDialogOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _vm.MarkFailed(ex.Message);
            _vm.CanRetrySoftware = ex is HardwareExportException;
            Outcome = ExportDialogOutcome.Failed;
        }
        finally
        {
            IsExportInFlight = false;
            _exportCts.Dispose();
            _exportCts = null;
            // Re-show in case the user backgrounded mid-flight: terminal state
            // is more important than their previous hide gesture. They can
            // close from here. (No-op if already visible.)
            if (Visibility != Visibility.Visible) Show();
            ExportFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadOptionsAsync()
    {
        if (_vm is null || _runner is null) return;
        try
        {
            await _vm.LoadOptionsAsync(new HardwareEncoderDetection(_runner, UserPreferences.DataDirectory),
                _preferredAcceleration, _detectionCts.Token);
            _preferredAcceleration = null;
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunExportWithRecoveryAsync(IProgress<ExportProgress> progress, CancellationToken ct)
    {
        var vm = _vm!;
        var detector = new HardwareEncoderDetection(_runner!, UserPreferences.DataDirectory);
        if (vm.SelectedAcceleration.Id == "automatic")
        {
            progress.Report(new(ExportPhase.Planning, 0, 0, 0, 0, "Evaluating export options..."));
            await vm.CheckHardwareAsync(detector, null, false, ct);
            if (vm.Evaluation is { Preferred: null, SoftwareMilliseconds: null })
                throw new ExportException("No encoder passed evaluation. Use Evaluate options for details.");
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await new ExportService(_runner!, vm.CreateEncoder()).RunExportAsync(AcceptedPlan!, progress, ct);
                return;
            }
            catch (HardwareExportException ex)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new(ExportPhase.Restarting, 0, 0, 0, 0, "Encoder failed. Evaluating available options..."));
                await vm.CheckHardwareAsync(detector, null, true, ct);
                ct.ThrowIfCancellationRequested();
                if (!IsVisible) Show();
                var choice = new EncoderOptionsDialog(vm.Evaluation!, ex.Message) { Owner = this };
                if (choice.ShowDialog() != true || choice.SelectedOption is null)
                    throw new OperationCanceledException("Export restart cancelled.");
                vm.SelectedAcceleration = vm.AccelerationOptions.FirstOrDefault(o => o.Id == choice.SelectedOption.Id) ?? choice.SelectedOption;
                vm.BeginExport(AcceptedPlan!);
            }
        }
    }

    private async void OnRecheckHardware(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _runner is null || !_vm.IsConfiguring || IsExportInFlight || !_vm.CanCheckHardware) return;
        try
        {
            var ct = _detectionCts.Token;
            ShowEvaluationResults();
            await _vm.CheckHardwareAsync(new HardwareEncoderDetection(_runner, UserPreferences.DataDirectory),
                null, true, ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { }
    }
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }
    private void OnTitleClose(object sender, RoutedEventArgs e)
    {
        if (IsExportInFlight) OnRunInBackground(sender, e);
        else Close();
    }
    private void OnShowEvaluation(object sender, RoutedEventArgs e)
    {
        if (_vm?.CanOpenResults == true) ShowEvaluationResults();
    }
    private void ShowEvaluationResults()
    {
        _vm!.ShowEvaluation = true;
        var area = MonitorBounds.WorkArea(this);
        Width = Math.Min(970, area.Width);
        CenterWindow();
    }
    private void OnHideEvaluation(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ShowEvaluation = false;
        Width = 640;
        CenterWindow();
    }
    private async void OnRetrySoftware(object sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.CanRetrySoftware) return;
        _vm.SelectedAcceleration = _vm.AccelerationOptions.FirstOrDefault(o => o.Id == "software") ?? ExportAccelerationOption.Software;
        await StartExportAsync();
    }

    private void OnCancelExport(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        // The OnExport await will resolve to OperationCanceledException, which
        // flips VM into Cancelled mode and shows the Close button. Don't close
        // here - leave the terminal state visible so the user knows it stopped.
    }

    private void OnRunInBackground(object sender, RoutedEventArgs e)
    {
        // The export task continues; status bar keeps live via the tee'd
        // external progress sink. We Hide() rather than Close() so the user
        // can click the status bar (or Export again) to bring this same
        // dialog instance - with its parts list and live progress - back.
        Outcome = ExportDialogOutcome.RunInBackground;
        Hide();
    }

    private void OnCloseTerminal(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public enum ExportDialogOutcome
{
    /// <summary>User clicked Cancel before export started, or cancel mid-export.</summary>
    Cancelled,
    /// <summary>Export ran to completion inside the dialog; user clicked Close.</summary>
    Completed,
    /// <summary>Export threw mid-flight; user clicked Close on the failure state.</summary>
    Failed,
    /// <summary>User clicked "Run in background" - export task continues, MainWindow inherits.</summary>
    RunInBackground,
    /// <summary>No ExportService was attached - caller is expected to run the AcceptedPlan itself.</summary>
    LegacyAcceptedPlan,
}
