using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

namespace AdTrim.Views;

public partial class ExportView : System.Windows.Controls.UserControl, IDisposable
{
    private ExportDialogViewModel? _vm;

    public ExportPlan? AcceptedPlan { get; private set; }

    public Task? ExportTask { get; private set; }

    public ExportDialogOutcome Outcome { get; private set; } = ExportDialogOutcome.Cancelled;

    public bool ShouldDeleteSidecarAfterSuccess => _vm?.DeleteSidecarAfterExport ?? false;

    public bool IsExportInFlight { get; private set; }

    public event EventHandler? ExportFinished;

    private FfmpegRunner? _runner;
    private readonly CancellationTokenSource _detectionCts = new();
    private string? _preferredAcceleration;
    private IProgress<ExportProgress>? _externalProgress;
    private CancellationTokenSource? _exportCts;
    public event EventHandler? Closed;
    private bool _disposed;

    public ExportView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadOptionsAsync();
        KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || IsExportInFlight) return;
            Close();
            e.Handled = true;
        };
    }

    public void Close()
    {
        if (IsExportInFlight || _disposed) return;
        Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detectionCts.Cancel();
        _detectionCts.Dispose();
    }

    public async Task CancelAndWaitAsync()
    {
        _exportCts?.Cancel();
        while (IsExportInFlight) await Task.Delay(25);
    }

    public void Bind(MainViewModel project, MediaInfo? media, string? preferredAcceleration = null)
    {
        _preferredAcceleration = preferredAcceleration;
        _vm = new ExportDialogViewModel(project, media);
        DataContext = _vm;
        _vm.RefreshValidation();
    }

    public void AttachExportRunner(FfmpegRunner runner, IProgress<ExportProgress> externalProgress)
    {
        _runner = runner;
        _externalProgress = externalProgress;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Outcome = ExportDialogOutcome.Cancelled;
        Close();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = _vm?.OutputFolder ?? "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true && _vm is not null)
        {
            _vm.OutputFolder = dlg.FolderName;
            _vm.RefreshValidation();
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_vm is null) { Outcome = ExportDialogOutcome.Cancelled; Close(); return; }
        if (!_vm.CanCheckHardware || IsExportInFlight) return;

        _vm.RefreshDefaultOutputFilename();
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

        if (Application.Current is App app && app.ReserveExport(this, AcceptedPlan) is { } issue)
        {
            _vm.ValidationIssues.Add(new(ExportValidationKind.Blocking, issue));
            return;
        }
        _vm.ShowEvaluation = false;
        _vm.BeginExport(AcceptedPlan);
        _exportCts = new CancellationTokenSource();

        var dialogProgress = new Progress<ExportProgress>(p =>
        {
            _vm.UpdateProgress(p);
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
            if (Application.Current is App currentApp) currentApp.ReleaseExport(this);
            IsExportInFlight = false;
            _exportCts.Dispose();
            _exportCts = null;
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
                var choice = new EncoderOptionsDialog(vm.Evaluation!, ex.Message) { Owner = Window.GetWindow(this) };
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
    private void OnShowEvaluation(object sender, RoutedEventArgs e)
    {
        if (_vm?.CanOpenResults == true) ShowEvaluationResults();
    }
    private void ShowEvaluationResults()
    {
        _vm!.ShowEvaluation = true;

    }
    private void OnHideEvaluation(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ShowEvaluation = false;
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

    private void OnOpenVideo(object sender, RoutedEventArgs e) => OpenResult(false);
    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenResult(true);
    private void OpenResult(bool folder)
    {
        if (_vm?.IsCompleted != true || AcceptedPlan is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                folder ? System.IO.Path.GetDirectoryName(AcceptedPlan.OutputPath)! : AcceptedPlan.OutputPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        { MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not open export", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnCloseTerminal(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public enum ExportDialogOutcome
{
    Cancelled,
    Completed,
    Failed,
    LegacyAcceptedPlan,
}
