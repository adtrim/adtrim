using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AdTrim.Commands;
using AdTrim.Models;

namespace AdTrim.ViewModels;

public enum SelectionKind { None, Marker, Segment }

/// <summary>
/// Main editor state. All times are integer microseconds - the authoritative
/// unit (invariant: times are integer microseconds). Seconds appear only in UI bindings
/// via TimeFormatConverter.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private string? _fileName;
    public string? FileName
    {
        get => _fileName;
        set => Set(ref _fileName, value);
    }

    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        set { if (Set(ref _sourcePath, value)) Notify(nameof(IsFileLoaded)); }
    }

    public string MediaInfoLine { get; set; } = "";

    private long _durationUs;
    public long DurationUs
    {
        get => _durationUs;
        set { if (Set(ref _durationUs, value)) Notify(nameof(IsFileLoaded)); }
    }

    /// <summary>True once a media file has been opened (probe complete, splits populated).</summary>
    public bool IsFileLoaded => _durationUs > 0 && !string.IsNullOrEmpty(_sourcePath);

    private long _playheadUs;
    public long PlayheadUs
    {
        get => _playheadUs;
        set => Set(ref _playheadUs, value);
    }

    // Default zoom = 1.0× - whole video fits in the timeline viewport when a
    // file first opens. Zoom > 1 widens the timeline content and scrolls
    // (Ctrl+Wheel or View ▸ Zoom). Zoom < 1 lets the user see more, e.g.
    // on a very narrow window.
    private double _zoomFactor = 1.0;
    public double ZoomFactor
    {
        get => _zoomFactor;
        set => Set(ref _zoomFactor, value);
    }

    // Keep timeline media analysis off by default while performance is being
    // evaluated. Both waveforms and thumbnails can trigger background ffmpeg
    // work, so users should explicitly opt in from the View menu for now.
    private bool _showWaveform = false;
    public bool ShowWaveform
    {
        get => _showWaveform;
        set => Set(ref _showWaveform, value);
    }

    private bool _showThumbnails = false;
    public bool ShowThumbnails
    {
        get => _showThumbnails;
        set => Set(ref _showThumbnails, value);
    }

    private bool _showRulerTicks = true;
    public bool ShowRulerTicks
    {
        get => _showRulerTicks;
        set => Set(ref _showRulerTicks, value);
    }

    /// <summary>
    /// Frame rate from probe - used by frame-snap math. Defaults to 30000/1001
    /// (29.97) until a probe lands; that's the dominant US OTA rate.
    /// </summary>
    public Rational FrameRate { get; set; } = new(30000, 1001);

    /// <summary>
    /// PTS of the video's first frame (often non-zero for Plex DVR captures -
    /// the autoconverter preserves the .ts mid-GOP start offset). FrameSnap
    /// uses this as a phase so its grid aligns with the file's actual frames.
    /// </summary>
    public long FrameStartPhaseUs { get; set; } = 0;

    public ObservableCollection<Split> Markers { get; } = new();
    public ObservableCollection<Segment> Segments { get; } = new();
    public ObservableCollection<TimelineThumbnail> Thumbnails { get; } = new();

    /// <summary>
    /// Per-bin audio peak levels (0..1) for the timeline waveform. Populated
    /// asynchronously after a file opens; null until extraction completes (or
    /// if it fails). Length is the bin count chosen at extraction time.
    /// </summary>
    private float[]? _waveformPeaks;
    public float[]? WaveformPeaks
    {
        get => _waveformPeaks;
        set => Set(ref _waveformPeaks, value);
    }

    /// <summary>IDs of segments marked excluded. Persisted; survives marker moves
    /// because segment IDs are derived from start/end µs.</summary>
    public HashSet<string> ExcludedSegmentIds { get; } = new();

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set => Set(ref _isDirty, value);
    }

    // ---- Transient status (overrides the selection-aware default chip) ----
    private string? _statusOverride;
    public string? StatusOverride
    {
        get => _statusOverride;
        set { if (Set(ref _statusOverride, value)) Notify(nameof(HasStatusOverride)); }
    }
    public bool HasStatusOverride => !string.IsNullOrEmpty(_statusOverride);

    private StatusKind _statusKind = StatusKind.Info;
    public StatusKind StatusKind
    {
        get => _statusKind;
        set => Set(ref _statusKind, value);
    }

    /// <summary>0.0..1.0 progress for the thin status-bar progress bar. Null hides it.</summary>
    private double? _progressPercent;
    public double? ProgressPercent
    {
        get => _progressPercent;
        set { if (Set(ref _progressPercent, value)) Notify(nameof(HasProgress), nameof(ProgressPercentValue)); }
    }
    public bool HasProgress => _progressPercent.HasValue;
    public double ProgressPercentValue => _progressPercent ?? 0.0;

    // ---- Banner above transport ----
    private BannerInfo? _banner;
    public BannerInfo? Banner
    {
        get => _banner;
        set { if (Set(ref _banner, value)) Notify(nameof(HasBanner)); }
    }
    public bool HasBanner => _banner is not null;

    // ---- Refine summary chip (persists until next refine) ----
    // Lives alongside the dismissible banner: the banner is event-driven and
    // goes away; this chip stays as a quiet "last result" reference until the
    // next pass replaces it.
    private string? _refineSummary;
    public string? RefineSummary
    {
        get => _refineSummary;
        set { if (Set(ref _refineSummary, value)) Notify(nameof(HasRefineSummary)); }
    }
    public bool HasRefineSummary => !string.IsNullOrEmpty(_refineSummary);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) Notify(nameof(CanEdit)); }
    }

    public bool CanEdit => !IsBusy;

    private string? _busyOperation;
    public string? BusyOperation
    {
        get => _busyOperation;
        set => Set(ref _busyOperation, value);
    }

    public void ClearStatus()
    {
        StatusOverride = null;
        ProgressPercent = null;
        StatusKind = StatusKind.Info;
    }

    // ---- Selection ---------------------------------------------------------
    private SelectionKind _selectionKind = SelectionKind.None;
    public SelectionKind SelectionKind
    {
        get => _selectionKind;
        private set => Set(ref _selectionKind, value);
    }

    private Split? _selectedMarker;
    public Split? SelectedMarker
    {
        get => _selectedMarker;
        private set => Set(ref _selectedMarker, value);
    }

    private Segment? _selectedSegment;
    public Segment? SelectedSegment
    {
        get => _selectedSegment;
        private set => Set(ref _selectedSegment, value);
    }

    public int ConfirmedCount => Markers.Count(m => m.Confirmed && !m.IsBookend);
    public int InternalSplitCount => Markers.Count(m => !m.IsBookend);
    public string ConfirmProgressLabel => $"{ConfirmedCount} / {InternalSplitCount}";
    public double ConfirmProgressPercent => InternalSplitCount == 0 ? 0 : ConfirmedCount / (double)InternalSplitCount;
    public long ExpectedOutputDurationUs => Segments.Where(s => !s.IsExcluded).Sum(s => s.DurationUs);

    /// <summary>Undo/redo command stack. In-memory only (not persisted to the sidecar).</summary>
    public CommandStack CommandStack { get; } = new();

    // ---- Construction ------------------------------------------------------
    public MainViewModel()
    {
        Markers.CollectionChanged += OnMarkersCollectionChanged;
        Segments.CollectionChanged += OnSegmentsCollectionChanged;
        CommandStack.Changed += (_, _) => MarkDirty();
    }

    private void OnMarkersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (Split m in e.NewItems) m.PropertyChanged += OnMarkerPropertyChanged;
        if (e.OldItems is not null)
            foreach (Split m in e.OldItems) m.PropertyChanged -= OnMarkerPropertyChanged;
        NotifyConfirmProgress();
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (Segment s in e.NewItems) s.PropertyChanged += OnSegmentPropertyChanged;
        if (e.OldItems is not null)
            foreach (Segment s in e.OldItems) s.PropertyChanged -= OnSegmentPropertyChanged;
        Notify(nameof(ExpectedOutputDurationUs));
    }

    private void OnMarkerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection-only changes don't dirty the project.
        if (e.PropertyName is nameof(Split.IsSelected) or nameof(Split.ShowAudition)) return;
        if (e.PropertyName is nameof(Split.Confirmed))
            NotifyConfirmProgress();
        MarkDirty();
    }

    private void NotifyConfirmProgress()
        => Notify(nameof(ConfirmedCount), nameof(InternalSplitCount),
            nameof(ConfirmProgressLabel), nameof(ConfirmProgressPercent));

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Segment.State))
        {
            // Selection state changes don't dirty. Excluded-state changes do.
            if (sender is Segment s)
            {
                var nowExcluded = s.IsExcluded;
                var wasExcluded = ExcludedSegmentIds.Contains(s.Id);
                if (nowExcluded != wasExcluded)
                {
                    if (nowExcluded) ExcludedSegmentIds.Add(s.Id);
                    else ExcludedSegmentIds.Remove(s.Id);
                    MarkDirty();
                    Notify(nameof(ExpectedOutputDurationUs));
                }
            }
            return;
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? DirtyChanged;

    // ---- Selection helpers -------------------------------------------------
    public void SelectMarker(Split marker)
    {
        ClearSelection();
        if (marker.IsBookend) return;
        SelectedMarker = marker;
        marker.IsSelected = true;
        marker.ShowAudition = true;
        SelectionKind = SelectionKind.Marker;
        PlayheadUs = marker.TimeUs;
    }

    public void SelectSegment(Segment segment)
    {
        ClearSelection();
        SelectedSegment = segment;
        segment.State = segment.IsExcluded ? SegmentState.SelectedExcluded : SegmentState.Selected;
        SelectionKind = SelectionKind.Segment;
    }

    public void ClearSelection()
    {
        if (SelectedMarker is { } m)
        {
            m.IsSelected = false;
            m.ShowAudition = false;
            SelectedMarker = null;
        }
        if (SelectedSegment is { } s)
        {
            s.State = s.State == SegmentState.SelectedExcluded ? SegmentState.Excluded : SegmentState.Default;
            SelectedSegment = null;
        }
        SelectionKind = SelectionKind.None;
    }

    // ---- Segment derivation ------------------------------------------------
    /// <summary>
    /// Rebuild segments from sorted splits + [0, duration] bookends. Replays
    /// `ExcludedSegmentIds` so excluded state survives marker moves.
    /// </summary>
    public void RebuildSegmentsFromSplits()
    {
        // Marker collection is the source of truth - assume it's sorted by TimeUs.
        var sorted = Markers.OrderBy(m => m.TimeUs).ToList();
        Segments.Clear();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var seg = new Segment
            {
                StartUs = sorted[i].TimeUs,
                EndUs = sorted[i + 1].TimeUs,
                Label = $"Part {i + 1}",
            };
            seg.State = ExcludedSegmentIds.Contains(seg.Id) ? SegmentState.Excluded : SegmentState.Default;
            Segments.Add(seg);
        }
    }

    public Split? NextUnconfirmedAfter(long timeUs)
    {
        return Markers
            .Where(m => !m.IsBookend && !m.Confirmed && m.TimeUs > timeUs)
            .OrderBy(m => m.TimeUs)
            .FirstOrDefault()
            ?? Markers.Where(m => !m.IsBookend && !m.Confirmed)
                      .OrderBy(m => m.TimeUs)
                      .FirstOrDefault();
    }

    // ---- Sample-data factory (used by the WPF designer only) -------------
    public static MainViewModel CreateDesignSample()
    {
        var vm = new MainViewModel
        {
            FileName = "Living-Room_2026-05-04_KQED-9-1_News_at_11.mp4",
            SourcePath = @"C:\fake\Living-Room.mp4",   // non-null so IsFileLoaded is true
            MediaInfoLine = "29.97 fps · 1920×1080 · H.264",
            DurationUs = 1_830_000_000L,
            PlayheadUs = 387_400_000L,
            ZoomFactor = 2.0,
        };
        long Us(double sec) => (long)Math.Round(sec * 1_000_000.0);

        vm.Markers.Add(new Split { TimeUs = Us(0),    Confidence = null, Confirmed = true,  Label = "Start" });
        vm.Markers.Add(new Split { TimeUs = Us(312),  Confidence = null, Confirmed = true });
        vm.Markers.Add(new Split { TimeUs = Us(468),  Confidence = null, Confirmed = true });
        vm.Markers.Add(new Split { TimeUs = Us(762),  Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(918),  Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(1212), Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(1368), Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(1602), Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(1758), Confidence = null, Confirmed = false });
        vm.Markers.Add(new Split { TimeUs = Us(1830), Confidence = null, Confirmed = true,  Label = "End" });

        // Excluded by ID - survives later marker moves.
        vm.ExcludedSegmentIds.Add($"segment-{Us(312)}-{Us(468)}");
        vm.ExcludedSegmentIds.Add($"segment-{Us(762)}-{Us(918)}");
        vm.ExcludedSegmentIds.Add($"segment-{Us(1212)}-{Us(1368)}");
        vm.ExcludedSegmentIds.Add($"segment-{Us(1602)}-{Us(1758)}");

        vm.RebuildSegmentsFromSplits();
        vm.IsDirty = false;
        return vm;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
