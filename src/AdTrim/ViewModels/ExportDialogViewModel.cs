using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

public enum ExportValidationKind { Blocking, Warning }

public sealed record ExportValidation(ExportValidationKind Kind, string Message);

public enum ExportDialogMode { Configuring, Exporting, Completed, Failed, Cancelled }

public enum ExportPartStatus { Queued, InProgress, Done, Failed }

/// <summary>
/// One row in the progress view's parts list. Notifies on Status/Percent so
/// the row icon + right-hand status text reflect live ExportService updates.
/// </summary>
public sealed class ExportPartItem : INotifyPropertyChanged
{
    public int Index { get; init; }            // 1-based; 0 for the synthetic "Concat" tail item
    public string Label { get; init; } = "";
    public string TimeRange { get; init; } = "";

    // Per-part wall-clock. Started when the part enters InProgress, stopped
    // when it enters Done. Stays at zero for parts that go Queued→Done
    // directly (which shouldn't happen in practice but is handled in
    // ElapsedFormatted by rendering an em-dash).
    private readonly Stopwatch _partTimer = new();

    private ExportPartStatus _status;
    public ExportPartStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            var prev = _status;
            _status = value;
            switch (value)
            {
                case ExportPartStatus.InProgress when prev == ExportPartStatus.Queued:
                    _partTimer.Restart();
                    break;
                case ExportPartStatus.Done when prev == ExportPartStatus.InProgress:
                    _partTimer.Stop();
                    break;
                case ExportPartStatus.Failed when prev == ExportPartStatus.InProgress:
                    _partTimer.Stop();
                    break;
                case ExportPartStatus.Queued:
                    _partTimer.Reset();
                    break;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQueued)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFailed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedFormatted)));
        }
    }

    private double _percent;
    public double Percent
    {
        get => _percent;
        set
        {
            if (_percent.Equals(value)) return;
            _percent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public bool IsQueued => Status == ExportPartStatus.Queued;
    public bool IsInProgress => Status == ExportPartStatus.InProgress;
    public bool IsDone => Status == ExportPartStatus.Done;
    public bool IsFailed => Status == ExportPartStatus.Failed;

    public string StatusText => Status switch
    {
        ExportPartStatus.Done => "done",
        ExportPartStatus.Queued => "queued",
        ExportPartStatus.InProgress => $"{Percent * 100:0}%",
        ExportPartStatus.Failed => "stopped",
        _ => "",
    };

    public string ElapsedFormatted
    {
        get
        {
            if (Status == ExportPartStatus.Queued) return "";
            // Done without ever running InProgress - nothing meaningful to show.
            if (Status == ExportPartStatus.Done && _partTimer.Elapsed == TimeSpan.Zero) return "-";
            var t = _partTimer.Elapsed;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    /// <summary>Drive the live readout while InProgress. Called by the dialog's tick timer.</summary>
    public void TickElapsed()
    {
        if (_status == ExportPartStatus.InProgress)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedFormatted)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Builds + validates an export plan from the current `MainViewModel`.
/// Validation rules:
///   Blocking : all-excluded · none-excluded · zero-duration segment ·
///              invalid filename · file already exists w/o overwrite ·
///              source size or duration mismatch.
///   Warning  : source mtime drifted but size + duration match ·
///              splits within 1s but non-zero apart.
/// </summary>
public sealed class ExportDialogViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _project;
    private readonly MediaInfo? _media;

    private string _outputFolder = "";
    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (!Set(ref _outputFolder, value)) return;
            Notify(nameof(FullOutputPath));
            Notify(nameof(OutputFileExists));
            Notify(nameof(ExportButtonText));
        }
    }

    private string _outputFilename = "";
    public string OutputFilename
    {
        get => _outputFilename;
        set
        {
            if (!Set(ref _outputFilename, value)) return;
            Notify(nameof(FullOutputPath));
            Notify(nameof(OutputFileExists));
            Notify(nameof(ExportButtonText));
        }
    }

    /// <summary>True when the resolved output path points at an existing file.
    /// Drives the footer-button label so it reads "Export" by default and
    /// "Export &amp; overwrite" only when the user is actually about to clobber
    /// something.</summary>
    public bool OutputFileExists =>
        !string.IsNullOrEmpty(FullOutputPath) && File.Exists(FullOutputPath);

    public string ExportButtonText => OutputFileExists ? "Export & overwrite" : "Export";

    /// <summary>If true and OutputFolder/OutputFilename point at an existing
    /// file, validation downgrades the "file exists" rule from blocking to a
    /// warning banner - the user has acknowledged.</summary>
    public bool OverwriteConfirmed { get; set; }

    /// <summary>If true, MainWindow deletes the <c>.adt.json</c> sidecar
    /// after a successful export. Off by default so users keep their split
    /// history; turning it on is a "I'm done with this file" affordance.</summary>
    public bool DeleteSidecarAfterExport { get; set; }

    /// <summary>Inline-rendered validation results. Updated by Validate().</summary>
    public ObservableCollection<ExportValidation> ValidationIssues { get; } = new();

    // ---- Progress-view state (driven by Begin/Update/MarkComplete/MarkFailed) ----

    private ExportDialogMode _mode = ExportDialogMode.Configuring;
    public ExportDialogMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            Notify(nameof(IsConfiguring));
            Notify(nameof(IsExporting));
            Notify(nameof(IsCompleted));
            Notify(nameof(IsFailed));
            Notify(nameof(IsTerminal));
        }
    }

    public bool IsConfiguring => Mode == ExportDialogMode.Configuring;
    public bool IsExporting   => Mode == ExportDialogMode.Exporting;
    public bool IsCompleted   => Mode == ExportDialogMode.Completed;
    public bool IsFailed      => Mode == ExportDialogMode.Failed || Mode == ExportDialogMode.Cancelled;
    /// <summary>Done, Failed, or Cancelled - i.e. the export run is no longer in flight.</summary>
    public bool IsTerminal    => IsCompleted || IsFailed;

    /// <summary>Per-part rows for the progress view (one per kept segment + a synthetic Concat tail).</summary>
    public ObservableCollection<ExportPartItem> Parts { get; } = new();

    private string _progressLine = "";
    public string ProgressLine { get => _progressLine; private set => Set(ref _progressLine, value); }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (!Set(ref _progressPercent, value)) return;
            Notify(nameof(ProgressPercentText));
        }
    }
    public string ProgressPercentText => $"{ProgressPercent * 100:0}%";

    private string _elapsedFormatted = "00:00";
    public string ElapsedFormatted { get => _elapsedFormatted; private set => Set(ref _elapsedFormatted, value); }

    private string _remainingFormatted = "";
    public string RemainingFormatted { get => _remainingFormatted; private set => Set(ref _remainingFormatted, value); }

    private string _errorMessage = "";
    public string ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }

    /// <summary>"Cutting N excluded segments and stitching the remaining M parts into a single file."</summary>
    public string ExportSummaryLine
    {
        get
        {
            var excluded = _project.Segments.Count(s => s.IsExcluded);
            var kept = _project.Segments.Count(s => !s.IsExcluded);
            return $"Cutting {excluded} excluded segment{(excluded == 1 ? "" : "s")} and stitching the remaining "
                 + $"{kept} part{(kept == 1 ? "" : "s")} into a single file.";
        }
    }

    private readonly Stopwatch _runTimer = new();

    // Ticks the Elapsed / Remaining display while a long ffmpeg encode is in
    // flight. ExportService only reports progress on phase boundaries (≈ once
    // per kept segment); without this timer, Elapsed appears frozen and
    // Remaining stays blank for the entire first segment. The 500 ms cadence
    // gives a smoothly-updating clock without observable CPU cost.
    private DispatcherTimer? _tickTimer;

    public ExportDialogViewModel(MainViewModel project, MediaInfo? media)
    {
        _project = project;
        _media = media;
        OutputFolder = DeriveDefaultFolder(project.SourcePath);
        OutputFilename = ExportNaming.DeriveDefaultFilename(project.SourcePath);
    }

    // -------- progress-view orchestration --------

    /// <summary>Switch to the progress view and seed the parts list from the plan.</summary>
    public void BeginExport(ExportPlan plan)
    {
        Parts.Clear();
        foreach (var s in plan.KeptSegments)
        {
            Parts.Add(new ExportPartItem
            {
                Index = s.Index,
                Label = s.PartTitle,
                TimeRange = $"{FormatHms(s.StartUs)} → {FormatHms(s.EndUs)}",
                Status = ExportPartStatus.Queued,
            });
        }
        // Synthetic tail row - the concat-mux + validate phase. Index 0 marks it
        // as "not a real segment" for the progress mapping below.
        Parts.Add(new ExportPartItem
        {
            Index = 0,
            Label = "Concat",
            TimeRange = $"Stitch parts → {Path.GetFileName(plan.OutputPath)}",
            Status = ExportPartStatus.Queued,
        });

        ProgressPercent = 0;
        ProgressLine = "Preparing…";
        ElapsedFormatted = "00:00";
        RemainingFormatted = "";
        ErrorMessage = "";
        _runTimer.Restart();
        StartTickTimer();
        Mode = ExportDialogMode.Exporting;
    }

    private void StartTickTimer()
    {
        if (_tickTimer is null)
        {
            // DispatcherTimer.Tick fires on the dispatcher this is constructed
            // on - which, in the running app, is the WPF UI thread because
            // BeginExport is called from the dialog. Headless tests that never
            // call BeginExport will never construct it.
            _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            // Tick refreshes Elapsed only - Remaining is computed from
            // ProgressPercent, which only changes on real progress callbacks.
            _tickTimer.Tick += (_, _) =>
            {
                RefreshElapsed();
                foreach (var part in Parts) part.TickElapsed();
            };
        }
        _tickTimer.Start();
    }

    private void StopTickTimer()
    {
        _tickTimer?.Stop();
        // One final refresh so the freeze-frame Elapsed matches the actual stopwatch.
        RefreshElapsed();
        foreach (var part in Parts) part.TickElapsed();
    }

    /// <summary>Fold one <see cref="ExportProgress"/> tick into the parts list and headline.</summary>
    public void UpdateProgress(ExportProgress p)
    {
        if (Mode != ExportDialogMode.Exporting) return;
        ProgressPercent = Math.Clamp(p.OverallPercent, 0, 1);

        var concatIndex = Parts.Count - 1;
        switch (p.Phase)
        {
            case ExportPhase.EncodingSegment:
                // Mark prior parts done, current part in-progress.
                for (int i = 0; i < concatIndex; i++)
                {
                    var part = Parts[i];
                    if (i + 1 < p.CurrentSegment) part.Status = ExportPartStatus.Done;
                    else if (i + 1 == p.CurrentSegment)
                    {
                        part.Status = ExportPartStatus.InProgress;
                        part.Percent = Math.Clamp(p.SegmentPercent, 0, 1);
                    }
                }
                var seg = p.CurrentSegment >= 1 && p.CurrentSegment - 1 < Parts.Count
                    ? Parts[p.CurrentSegment - 1]
                    : null;
                ProgressLine = seg is null
                    ? $"Encoding part {p.CurrentSegment}/{p.TotalSegments}"
                    : $"Encoding part {p.CurrentSegment}/{p.TotalSegments} · {seg.TimeRange}";
                break;
            case ExportPhase.Concatenating:
            case ExportPhase.Validating:
                for (int i = 0; i < concatIndex; i++) Parts[i].Status = ExportPartStatus.Done;
                Parts[concatIndex].Status = ExportPartStatus.InProgress;
                Parts[concatIndex].Percent = p.Phase == ExportPhase.Validating ? 0.95 : 0.5;
                ProgressLine = p.Phase == ExportPhase.Validating
                    ? "Validating output…"
                    : "Concatenating + writing chapters…";
                break;
            case ExportPhase.Done:
                foreach (var part in Parts) part.Status = ExportPartStatus.Done;
                ProgressLine = "Export complete";
                break;
            case ExportPhase.Failed:
                ProgressLine = p.Message;
                break;
            case ExportPhase.Planning:
                ProgressLine = p.Message;
                break;
        }

        RefreshElapsed();
        RefreshRemaining();
    }

    /// <summary>
    /// Update the Elapsed clock from the stopwatch. Cheap, monotonically
    /// increasing, safe to call from a 500ms timer.
    /// </summary>
    private void RefreshElapsed()
    {
        ElapsedFormatted = FormatStopwatch(_runTimer.Elapsed);
    }

    /// <summary>
    /// Recompute the Remaining ETA. <b>Only call this when
    /// <see cref="ProgressPercent"/> has actually changed</b> - recomputing
    /// it on a wall-clock timer while pct is stale makes the ETA climb at
    /// rate <c>(1 − pct)/pct</c> per real second (the formula extrapolates
    /// "we are now overdue"). Once ffmpeg's `-progress` output drives
    /// per-frame pct updates, this will refresh organically several times
    /// per second.
    /// </summary>
    private void RefreshRemaining()
    {
        if (ProgressPercent >= 0.01 && ProgressPercent < 1.0)
        {
            var remainingSec = _runTimer.Elapsed.TotalSeconds * (1 - ProgressPercent) / ProgressPercent;
            if (remainingSec < 24 * 3600)
                RemainingFormatted = $"~{FormatStopwatch(TimeSpan.FromSeconds(remainingSec))} remaining";
            else
                RemainingFormatted = "";
        }
        else
        {
            RemainingFormatted = "";
        }
    }

    public void MarkComplete()
    {
        _runTimer.Stop();
        StopTickTimer();
        foreach (var part in Parts) part.Status = ExportPartStatus.Done;
        ProgressPercent = 1;
        ProgressLine = "Export complete";
        RemainingFormatted = "";
        Mode = ExportDialogMode.Completed;
    }

    public void MarkFailed(string message)
    {
        _runTimer.Stop();
        StopTickTimer();
        MarkInFlightPartsFailed();
        ErrorMessage = message;
        Mode = ExportDialogMode.Failed;
    }

    public void MarkCancelled()
    {
        _runTimer.Stop();
        StopTickTimer();
        MarkInFlightPartsFailed();
        ErrorMessage = "Export cancelled.";
        Mode = ExportDialogMode.Cancelled;
    }

    private void MarkInFlightPartsFailed()
    {
        foreach (var part in Parts)
            if (part.Status == ExportPartStatus.InProgress)
                part.Status = ExportPartStatus.Failed;
    }

    private static string FormatStopwatch(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";

    private static string FormatHms(long us)
    {
        var totalSec = us / 1_000_000.0;
        var h = (int)(totalSec / 3600);
        var m = (int)((totalSec % 3600) / 60);
        var s = totalSec % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00.00}" : $"{m:00}:{s:00.00}";
    }

    public string FullOutputPath => string.IsNullOrEmpty(OutputFolder) || string.IsNullOrEmpty(OutputFilename)
        ? ""
        : Path.Combine(OutputFolder, OutputFilename);

    public long SourceDurationUs => _project.DurationUs;

    /// <summary>Final-file duration estimate: total source minus excluded.</summary>
    public long ExcludedDurationUs =>
        _project.Segments.Where(s => s.IsExcluded).Sum(s => s.DurationUs);

    public long FinalDurationUs => _project.DurationUs - ExcludedDurationUs;

    /// <summary>
    /// Run all validations. Returns blocking rules first, then warnings.
    /// Empty result = OK to export.
    /// </summary>
    public IReadOnlyList<ExportValidation> Validate()
    {
        var v = new List<ExportValidation>();

        // ---- Source state ----
        if (string.IsNullOrEmpty(_project.SourcePath) || !File.Exists(_project.SourcePath))
        {
            v.Add(new(ExportValidationKind.Blocking, "Source file is missing."));
            return v;
        }

        if (_media is null)
        {
            v.Add(new(ExportValidationKind.Blocking,
                "Media probe information is missing. Close and reopen the source file before exporting."));
            return v;
        }

        var fi = new FileInfo(_project.SourcePath);
        if (fi.Length == 0)
            v.Add(new(ExportValidationKind.Blocking, "Source file is empty."));
        // Size / duration are checked at sidecar-load time; here we only
        // re-check that the in-memory project's duration matches the
        // currently-probed source.
        if (_project.DurationUs != _media.DurationUs)
            v.Add(new(ExportValidationKind.Blocking,
                "Source duration has changed since the project was opened."));
        if (_media.PrimaryAudioIndex < 0 || _media.PrimaryAudio is null)
            v.Add(new(ExportValidationKind.Blocking,
                "No primary audio stream was found. Export currently requires one audio stream."));

        // ---- Segments / splits ----
        var kept = _project.Segments.Where(s => !s.IsExcluded).ToList();
        var excluded = _project.Segments.Where(s => s.IsExcluded).ToList();

        if (excluded.Count == _project.Segments.Count && _project.Segments.Count > 0)
            v.Add(new(ExportValidationKind.Blocking,
                "All segments are excluded - there would be nothing to export."));
        if (excluded.Count == 0 && _project.Segments.Count > 0)
            v.Add(new(ExportValidationKind.Blocking,
                "No segments are excluded - nothing would be removed."));

        foreach (var s in _project.Segments)
        {
            if (s.DurationUs <= 0)
            {
                v.Add(new(ExportValidationKind.Blocking,
                    $"Segment '{s.Label}' has zero (or negative) duration."));
                break;
            }
        }

        // Warning: any two adjacent splits within 1s but >0us apart.
        var sortedMarkerTimes = _project.Markers.Select(m => m.TimeUs).OrderBy(t => t).ToList();
        for (int i = 1; i < sortedMarkerTimes.Count; i++)
        {
            var gap = sortedMarkerTimes[i] - sortedMarkerTimes[i - 1];
            if (gap > 0 && gap < 1_000_000)
            {
                v.Add(new(ExportValidationKind.Warning,
                    "Two splits are less than 1 second apart - verify this is intended."));
                break;
            }
        }

        // ---- Output destination ----
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
            v.Add(new(ExportValidationKind.Blocking, "Output folder does not exist."));

        if (string.IsNullOrWhiteSpace(OutputFilename))
            v.Add(new(ExportValidationKind.Blocking, "Output filename is required."));
        else if (!ExportNaming.IsValidFilename(OutputFilename))
            v.Add(new(ExportValidationKind.Blocking,
                "Output filename contains characters that are not allowed."));
        else if (!OutputFilename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            v.Add(new(ExportValidationKind.Blocking,
                "Output filename must end with '.mp4'."));

        if (!string.IsNullOrEmpty(FullOutputPath) && File.Exists(FullOutputPath))
        {
            if (OverwriteConfirmed)
                v.Add(new(ExportValidationKind.Warning,
                    "A file with this name already exists - exporting will overwrite it."));
            else
                v.Add(new(ExportValidationKind.Blocking,
                    "A file with this name already exists. Confirm overwrite to proceed."));
        }

        // Warning if writing back into the source's folder (not a hard block -
        // user may want it). Source-overwrite itself IS hard-blocked.
        if (!string.IsNullOrEmpty(FullOutputPath)
            && string.Equals(Path.GetFullPath(FullOutputPath),
                             Path.GetFullPath(_project.SourcePath ?? ""),
                             StringComparison.OrdinalIgnoreCase))
        {
            v.Add(new(ExportValidationKind.Blocking,
                "Output path is identical to the source - refusing to overwrite the source MP4."));
        }

        return v;
    }

    /// <summary>Materialize the plan. Caller should call Validate() first and
    /// only proceed if there are no blocking entries.</summary>
    public ExportPlan? BuildPlan()
    {
        if (_media is null) return null;
        if (_media.PrimaryAudioIndex < 0 || _media.PrimaryAudio is null) return null;
        if (string.IsNullOrEmpty(FullOutputPath)) return null;

        var kept = _project.Segments
            .Where(s => !s.IsExcluded)
            .OrderBy(s => s.StartUs)
            .ToList();
        int partNo = 1;
        var planSegments = kept.Select(s =>
            new ExportSegment(partNo, s.StartUs, s.EndUs, $"Part {partNo++}"))
            .ToList();

        return new ExportPlan(
            SourcePath: _project.SourcePath ?? "",
            OutputPath: FullOutputPath,
            SourceDurationUs: _project.DurationUs,
            PrimaryAudioStreamIndex: _media.PrimaryAudioIndex,
            KeptSegments: planSegments,
            FrameRate: _project.FrameRate,
            PrimaryAudioCodec: _media.PrimaryAudio?.Codec ?? "");
    }

    /// <summary>Run validation and refresh ValidationIssues for inline rendering.</summary>
    public void RefreshValidation()
    {
        ValidationIssues.Clear();
        foreach (var v in Validate()) ValidationIssues.Add(v);
    }

    // ---- Defaults ----
    private static string DeriveDefaultFolder(string? sourcePath)
        => string.IsNullOrEmpty(sourcePath) ? "" : Path.GetDirectoryName(sourcePath) ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
