using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AdTrim.Commands;
using AdTrim.Models;

namespace AdTrim.ViewModels;

public enum SelectionKind { None, Marker, Segment, Markers }

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
        set { if (Set(ref _sourcePath, value)) Notify(nameof(WindowTitle), nameof(IsFileLoaded), nameof(CanToggleCollapsed), nameof(CanReplaceMarkers)); }
    }

    public string WindowTitle => string.IsNullOrEmpty(SourcePath) ? "AdTrim" : $"AdTrim - {SourcePath}";

    public string MediaInfoLine { get; set; } = "";

    private long _durationUs;
    public long DurationUs
    {
        get => _durationUs;
        set { if (Set(ref _durationUs, value)) Notify(nameof(IsFileLoaded), nameof(CanToggleCollapsed), nameof(CanReplaceMarkers)); }
    }

    /// <summary>True once a media file has been opened (probe complete, splits populated).</summary>
    public bool IsFileLoaded => _durationUs > 0 && !string.IsNullOrEmpty(_sourcePath);

    private long _playheadUs;
    public long PlayheadUs
    {
        get => _playheadUs;
        set { if (Set(ref _playheadUs, value)) Notify(nameof(TimelinePositionUs)); }
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

    // On by default: thumbnails fetch only on-screen keyframes and the waveform
    // shares ad-break analysis's audio pass, and both yield to scrubbing.
    private bool _showWaveform = true;
    public bool ShowWaveform
    {
        get => _showWaveform;
        set => Set(ref _showWaveform, value);
    }

    private bool _showThumbnails = true;
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
    /// <summary>Filmstrip tiles fetched so far for the open recording. Replaced, not mutated, as tiles arrive.</summary>
    private ThumbnailStrip _thumbnailTiles = ThumbnailStrip.Empty;
    public ThumbnailStrip ThumbnailTiles
    {
        get => _thumbnailTiles;
        set => Set(ref _thumbnailTiles, value);
    }

    /// <summary>
    /// Primary-audio loudness for the timeline graph, <see cref="Services.AudioLevelService.LoudnessPerSecond"/>
    /// values per second as dBFS + 120, on the source clock. Filled by the recording's shared audio
    /// pass; while it runs this holds only the values measured so far. Null before the first arrive.
    /// </summary>
    private byte[]? _audioLoudness;
    public byte[]? AudioLoudness
    {
        get => _audioLoudness;
        set => Set(ref _audioLoudness, value);
    }

    /// <summary>IDs of segments marked excluded. Persisted; survives marker moves
    /// using boundary identity during editing and current timestamps when saved.</summary>
    public HashSet<string> ExcludedSegmentIds { get; } = new();

    public SourceFingerprint? SourceFingerprint { get; set; }
    private ExportProvenance? _lastExport;
    public ExportProvenance? LastExport
    {
        get => _lastExport;
        set { if (Set(ref _lastExport, value)) MarkDirty(); }
    }
    private MarkerSetSource _markerSetSource = MarkerSetSource.Unknown;
    public MarkerSetSource MarkerSetSource
    {
        get => _markerSetSource;
        set { if (Set(ref _markerSetSource, value)) { Notify(nameof(MarkerSetLabel), nameof(IsTimelineEdited)); MarkDirty(); } }
    }

    private string? _markerBaselineId;
    public string? MarkerBaselineId
    {
        get => _markerBaselineId;
        set { if (Set(ref _markerBaselineId, value)) Notify(nameof(IsTimelineEdited), nameof(MarkerSetLabel)); }
    }

    /// <summary>Caption of the toolbar's marker-set button: which set the timeline came from.</summary>
    public string MarkerSetLabel => MarkerSetSource switch
    {
        MarkerSetSource.Imported => "Imported markers",
        MarkerSetSource.AdTrim => "AdTrim suggested markers",
        _ => "Markers",
    };

    public bool HasWorkingMarkings => Markers.Any(m => !m.IsBookend || m.ChapterTitle is not null)
        || Segments.Any(s => s.IsExcluded);

    public bool IsTimelineEdited => MarkerBaselineId is { } baseline
        ? !StringComparer.Ordinal.Equals(baseline, CurrentMarkerSetId())
        : HasWorkingMarkings;

    public string CurrentMarkerSetId()
    {
        return ReplaceMarkerSetCommand.Capture(this).ContentId();
    }
    public long Revision { get; private set; }
    private string _saveStatus = "";
    public string SaveStatus { get => _saveStatus; set => Set(ref _saveStatus, value); }
    private readonly HashSet<Split> _observedMarkers = new();
    private readonly HashSet<Segment> _observedSegments = new();
    private bool _rebuilding;
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
        set { if (Set(ref _statusOverride, value)) Notify(nameof(HasStatusOverride), nameof(ShowsStatusOverride)); }
    }
    public bool HasStatusOverride => !string.IsNullOrEmpty(_statusOverride);
    /// <summary>The status bar shows the override, except during ad-break analysis, whose progress is on the timeline.</summary>
    public bool ShowsStatusOverride => HasStatusOverride && !IsAnalyzingCommercials;

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

    private bool _hasAdTrimAnalysis;
    /// <summary>This recording already has AdTrim ad-break suggestions (cached or currently applied).</summary>
    public bool HasAdTrimAnalysis
    {
        get => _hasAdTrimAnalysis;
        set => Set(ref _hasAdTrimAnalysis, value);
    }

    private string? _analysisStage;
    /// <summary>Named step of the running ad-break analysis, shown over the timeline.</summary>
    public string? AnalysisStage
    {
        get => _analysisStage;
        set => Set(ref _analysisStage, value);
    }

    private string? _analysisTimeLeft;
    /// <summary>Estimated time left for the running ad-break analysis, or null until it can be estimated.</summary>
    public string? AnalysisTimeLeft
    {
        get => _analysisTimeLeft;
        set => Set(ref _analysisTimeLeft, value);
    }

    private bool _isAnalyzingCommercials;
    /// <summary>Commercial analysis is running: markers are locked and the analysis panel shows.</summary>
    public bool IsAnalyzingCommercials
    {
        get => _isAnalyzingCommercials;
        set { if (Set(ref _isAnalyzingCommercials, value)) Notify(nameof(ShowsStatusOverride)); }
    }
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
        set { if (Set(ref _isBusy, value)) Notify(nameof(CanEdit), nameof(CanInteract), nameof(CanToggleCollapsed), nameof(CanReplaceMarkers)); }
    }

    private bool _isExportScreen;
    public bool IsExportScreen
    {
        get => _isExportScreen;
        set { if (Set(ref _isExportScreen, value)) Notify(nameof(CanEdit), nameof(CanInteract), nameof(CanToggleCollapsed), nameof(CanReplaceMarkers)); }
    }
    public bool CanReplaceMarkers => IsFileLoaded && CanInteract;
    public bool CanToggleCollapsed => IsFileLoaded && CanInteract;
    public bool CanInteract => !IsBusy && !IsExportScreen;
    public bool CanEdit => CanInteract && !IsCollapsed;
    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { if (Set(ref _isCollapsed, value)) Notify(nameof(CanEdit), nameof(TimelineDurationUs), nameof(TimelinePositionUs)); }
    }
    public KeptTimeline KeptTimeline { get; private set; } = new(Array.Empty<Segment>());
    public long TimelineDurationUs => IsCollapsed ? KeptTimeline.DurationUs : DurationUs;
    public long TimelinePositionUs => IsCollapsed ? KeptTimeline.ToOutput(PlayheadUs) : PlayheadUs;
    public long ToSourceTime(long displayedUs) => IsCollapsed ? KeptTimeline.ToSource(displayedUs) : displayedUs;
    public long ToDisplayTime(long sourceUs) => IsCollapsed ? KeptTimeline.ToOutput(sourceUs) : sourceUs;
    public void RefreshKeptTimeline()
    {
        KeptTimeline = new KeptTimeline(Segments);
        Notify(nameof(KeptTimeline), nameof(TimelineDurationUs), nameof(TimelinePositionUs));
    }

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

    private readonly List<Split> _selectedMarkers = new();
    /// <summary>Every selected marker: the single selection, or all of a multiple selection.</summary>
    public IReadOnlyList<Split> SelectedMarkers => SelectedMarker is { } one ? [one] : _selectedMarkers;
    public int SelectedMarkerCount => SelectedMarkers.Count;

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
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var marker in _observedMarkers) marker.PropertyChanged -= OnMarkerPropertyChanged;
            _observedMarkers.Clear();
        }
        if (e.OldItems is not null) foreach (Split marker in e.OldItems)
        { marker.PropertyChanged -= OnMarkerPropertyChanged; _observedMarkers.Remove(marker); }
        if (e.NewItems is not null) foreach (Split marker in e.NewItems)
            if (_observedMarkers.Add(marker)) marker.PropertyChanged += OnMarkerPropertyChanged;
        // A multiple selection never outlives its markers (undo, reloads, replaced marker sets).
        var gone = _selectedMarkers.Where(m => !Markers.Contains(m)).ToList();
        foreach (var marker in gone) { marker.IsSelected = false; _selectedMarkers.Remove(marker); }
        if (gone.Count > 0)
        {
            if (_selectedMarkers.Count == 0) SelectionKind = SelectionKind.None;
            Notify(nameof(SelectedMarkers), nameof(SelectedMarkerCount));
        }
        NotifyConfirmProgress();
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var segment in _observedSegments) segment.PropertyChanged -= OnSegmentPropertyChanged;
            _observedSegments.Clear();
        }
        if (e.OldItems is not null) foreach (Segment segment in e.OldItems)
        { segment.PropertyChanged -= OnSegmentPropertyChanged; _observedSegments.Remove(segment); }
        if (e.NewItems is not null) foreach (Segment segment in e.NewItems)
            if (_observedSegments.Add(segment)) segment.PropertyChanged += OnSegmentPropertyChanged;
        Notify(nameof(ExpectedOutputDurationUs));
    }

    private void OnMarkerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection-only changes don't dirty the project.
        if (e.PropertyName is nameof(Split.IsSelected)) return;
        if (e.PropertyName is nameof(Split.Confirmed))
            NotifyConfirmProgress();
        Notify(nameof(IsTimelineEdited), nameof(MarkerSetLabel));
        MarkDirty();
    }

    private void NotifyConfirmProgress()
        => Notify(nameof(ConfirmedCount), nameof(InternalSplitCount),
            nameof(ConfirmProgressLabel), nameof(ConfirmProgressPercent));

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rebuilding) return;
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
                    RefreshKeptTimeline();
                    Notify(nameof(ExpectedOutputDurationUs));
                    Notify(nameof(IsTimelineEdited), nameof(MarkerSetLabel));
                }
            }
            return;
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        Revision++;
        IsDirty = true;
        SaveStatus = "Unsaved changes";
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
        SelectionKind = SelectionKind.Marker;
        SelectionAnchor = marker;
        Notify(nameof(SelectedMarkers), nameof(SelectedMarkerCount));
        PlayheadUs = marker.TimeUs;
    }

    /// <summary>The last marker clicked or selected on its own: the start of a Shift+click range.</summary>
    public Split? SelectionAnchor { get; private set; }

    /// <summary>Selects every marker except the bookends (Ctrl+A).</summary>
    public void SelectAllMarkers() => SetMarkerSelection(Markers.Where(m => !m.IsBookend));

    /// <summary>Selects the markers matching <paramref name="predicate"/> (for example, low-confidence ones).</summary>
    public void SelectMarkersWhere(Func<Split, bool> predicate) => SetMarkerSelection(Markers.Where(m => !m.IsBookend && predicate(m)));

    /// <summary>Ctrl+click: adds or removes one marker without moving the playhead.</summary>
    public void ToggleMarkerSelection(Split marker)
    {
        if (marker.IsBookend) return;
        var next = SelectedMarkers.ToList();
        if (!next.Remove(marker)) next.Add(marker);
        SetMarkerSelection(next);
        SelectionAnchor = marker;
    }

    /// <summary>Shift+click: selects every marker between the anchor and <paramref name="marker"/>.</summary>
    public void SelectMarkerRange(Split marker)
    {
        if (marker.IsBookend) return;
        var anchor = SelectionAnchor is { } a && Markers.Contains(a) ? a : SelectedMarker;
        if (anchor is null) { SetMarkerSelection([marker]); SelectionAnchor = marker; return; }
        long from = Math.Min(anchor.TimeUs, marker.TimeUs), to = Math.Max(anchor.TimeUs, marker.TimeUs);
        SetMarkerSelection(Markers.Where(m => !m.IsBookend && m.TimeUs >= from && m.TimeUs <= to));
        SelectionAnchor = anchor;
    }

    /// <summary>Replaces the selection without moving the playhead; one marker is a normal single selection.</summary>
    private void SetMarkerSelection(IEnumerable<Split> markers)
    {
        var chosen = markers.Distinct().OrderBy(m => m.TimeUs).ToList();
        ClearSelection();
        if (chosen.Count == 1)
        {
            SelectedMarker = chosen[0];
            chosen[0].IsSelected = true;
            SelectionKind = SelectionKind.Marker;
        }
        else if (chosen.Count > 1)
        {
            foreach (var marker in chosen) marker.IsSelected = true;
            _selectedMarkers.AddRange(chosen);
            SelectionKind = SelectionKind.Markers;
        }
        Notify(nameof(SelectedMarkers), nameof(SelectedMarkerCount));
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
            SelectedMarker = null;
        }
        if (SelectedSegment is { } s)
        {
            s.State = s.State == SegmentState.SelectedExcluded ? SegmentState.Excluded : SegmentState.Default;
            SelectedSegment = null;
        }
        foreach (var marker in _selectedMarkers) marker.IsSelected = false;
        _selectedMarkers.Clear();
        SelectionKind = SelectionKind.None;
        Notify(nameof(SelectedMarkers), nameof(SelectedMarkerCount));
    }

    // ---- Segment derivation ------------------------------------------------
    /// <summary>
    /// Rebuild segments from sorted splits + [0, duration] bookends. Replays
    /// Stable boundary keys preserve exclusion when marker timestamps move.
    /// </summary>
    public HashSet<string> CaptureExclusions() => Segments.Where(s => s.IsExcluded)
        .Select(s => s.BoundaryKey).ToHashSet();

    public void RebuildSegmentsFromSplits(HashSet<string>? restoreExclusions = null)
    {
        var sorted = Markers.OrderBy(m => m.TimeUs).ToList();
        var prior = Segments.ToArray();
        var byBoundary = prior.ToDictionary(s => s.BoundaryKey);
        var selected = SelectedSegment?.BoundaryKey;
        _rebuilding = true;
        try
        {
            if (SelectedSegment is not null) ClearSelection();
            Segments.Clear();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var seg = new Segment
                {
                    BoundaryKey = sorted[i].Id + ":" + sorted[i + 1].Id,
                    StartUs = sorted[i].TimeUs,
                    EndUs = sorted[i + 1].TimeUs,
                    Label = sorted[i].ChapterTitle ?? $"Part {i + 1}",
                };
                byBoundary.TryGetValue(seg.BoundaryKey, out var same);
                var overlap = same is null ? prior.Where(s => s.StartUs < seg.EndUs && s.EndUs > seg.StartUs).ToArray() : Array.Empty<Segment>();
                bool excluded = restoreExclusions is not null ? restoreExclusions.Contains(seg.BoundaryKey)
                    : same is not null ? same.IsExcluded
                    : prior.Length == 0 ? ExcludedSegmentIds.Contains(seg.Id)
                    : overlap.Length > 0 && overlap.All(s => s.IsExcluded);
                seg.State = excluded ? SegmentState.Excluded : SegmentState.Default;
                Segments.Add(seg);
            }
            ExcludedSegmentIds.Clear();
            foreach (var seg in Segments.Where(s => s.IsExcluded)) ExcludedSegmentIds.Add(seg.Id);
            if (selected is not null && Segments.FirstOrDefault(s => s.BoundaryKey == selected) is { } selection)
                SelectSegment(selection);
        }
        finally { _rebuilding = false; }
        RefreshKeptTimeline();
        Notify(nameof(ExpectedOutputDurationUs));
        Notify(nameof(IsTimelineEdited), nameof(MarkerSetLabel));
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
