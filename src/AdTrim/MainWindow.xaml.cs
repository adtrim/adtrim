using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using AdTrim.Commands;
using AdTrim.Controls;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

namespace AdTrim;

public partial class MainWindow : Window, IPlaybackScreen
{
    // Dual-pane preview. `_playback.After` is the canonical playhead (Pane B,
    // visible on the right, muted). `_playback.Before` follows at playhead − 1
    // frame (Pane A, visible on the left, has audio). Only `_playback.After`
    // emits PositionUs back into the VM's PlayheadUs; Pane A is a slave that
    // gets seeked alongside Pane B whenever the playhead moves while paused,
    // and is re-synced at every play-start so they begin playback exactly
    // one frame apart. A few frames of drift during free playback is
    // acceptable per the design discussion.
    private readonly PlaybackController _playback;
    private FfmpegRunner? _ffmpeg;
    private RecordingLoader? _loader;
    private ProjectStore? _store;
    private readonly RefineCoordinator _refines;
    private readonly TimelineMediaLoader _timelineMedia;
    private readonly MarkerSetController _markers = new(new MarkerSuggestionStore());
    private MediaInfo? _media;
    private SourceFingerprint? _sourceFingerprint;
    private bool _hydrating;
    private bool _allowClose;
    private bool _switching;

    private ExportView? _activeExportDialog;
    private bool _closingExport;


    private readonly DispatcherTimer _autosaveTimer;
    private readonly ProjectSaveCoordinator _saves;

    private readonly TaskCompletionSource _ready = new();
    public Task Ready => _ready.Task;
    private string? _openingPath;
    private readonly string? _startupPath;
    public bool CanReceiveRecording => _ready.Task.IsCompletedSuccessfully && !_switching && _openingPath is null
        && _activeExportDialog is null && DataContext is MainViewModel { IsFileLoaded: false, IsBusy: false };
    public bool OwnsRecording(string path) => new[] { _openingPath, (DataContext as MainViewModel)?.SourcePath }
        .Any(p => p is not null && ExportSafety.SameFile(p, path));
    public void BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = _lastNonMinimizedState;
        Activate();
    }
    public MainWindow() : this(null) { }
    public MainWindow(string? startupPath)
    {
        _playback = new PlaybackController(this);
        _startupPath = startupPath;
        _openingPath = startupPath;
        InitializeComponent();
        // Runtime starts empty - the WPF designer uses the design-time
        // `<vm:MainViewModel/>` in XAML for live-preview only.
        DataContext = new MainViewModel();

        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _autosaveTimer.Tick += OnAutosaveTick;
        _saves = new ProjectSaveCoordinator(new ProjectSession(),
            () => _store is not null && _media is not null && _sourceFingerprint is not null ? new SaveTarget(_store, _media, _sourceFingerprint) : null,
            new TimerAutosave(_autosaveTimer), App.WriteCrashLog, () => _ = Dispatcher.BeginInvoke(new Action(Close)),
            () => OnAutosaveTick(null, EventArgs.Empty));
        _refines = new RefineCoordinator(() => _ffmpeg is null ? null : (_refine ??= new RefineService(_ffmpeg)).RefineOneAsync,
            () => _media?.PrimaryAudioIndex);
        _timelineMedia = new TimelineMediaLoader(() => _ffmpeg,
            () => _playback.After.IsSeeking || _playback.After.IsLoading || _playback.Before.IsSeeking || _playback.Before.IsLoading,
            update => Dispatcher.InvokeAsync(update), () => Timeline.RefreshThumbnails());
        Timeline.ThumbnailsNeeded += times => _timelineMedia.RequestThumbnails(times);

        WireViewModelEvents((MainViewModel)DataContext);

        Loaded += OnLoaded;
        InitializeUpdates();
        Closing += OnWindowClosing;
        Timeline.ReviewJoinRequested += (_, outputUs) =>
        {
            if (DataContext is not MainViewModel vm || !vm.IsCollapsed || vm.IsBusy) return;
            _playback.PlayUntilUs = null;
            _playback.SeekTo(vm, vm.KeptTimeline.ToSource(Math.Max(0, outputUs - 2_000_000)));
            _playback.After.Play();
        };
        _playback.After.PlaybackFailed += (_, message) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (vm.IsCollapsed) SetCollapsed(vm, false);
            vm.Banner = new BannerInfo(StatusKind.Warning, "Preview unavailable.", message, Array.Empty<BannerAction>());
        }));
        PreviewKeyDown += OnPreviewKeyDown;
        _askDiscardEdits = (text, title) => MessageBox.Show(this, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel, MessageBoxOptions.None) == MessageBoxResult.OK;
        // PreviewMouseWheel tunnels down before children handle the event -
        // necessary because the timeline's ScrollViewer would otherwise eat
        // every wheel event for horizontal scrolling, so Ctrl+Wheel zoom
        // never reaches a normal MouseWheel handler.
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void WireViewModelEvents(MainViewModel vm)
    {
        vm.DirtyChanged += (_, _) =>
        {
            if (_hydrating) return;
            // Debounced autosave: reset timer on every dirty event.
            _saves.RestartAutosave();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ZoomFactor))
                UpdateTimelineLayout(forceRecenter: true);
            else if (e.PropertyName == nameof(MainViewModel.CanReplaceMarkers))
                RefreshImportedMarkersItem(vm);
            else if (e.PropertyName == nameof(MainViewModel.HasAdTrimAnalysis))
            {
                // A first analysis is the main action, so it gets the primary (blue) style;
                // "Analyze ad breaks again" falls back to the plain button style.
                if (vm.HasAdTrimAnalysis) AnalyzeAdBreaksButton.ClearValue(StyleProperty);
                else AnalyzeAdBreaksButton.Style = (Style)FindResource("Button.Primary");
            }
            else if (e.PropertyName == nameof(MainViewModel.ShowWaveform))
            {
                if (vm.ShowWaveform)
                {
                    if (vm.SourcePath is { } path && _media is not null)
                        _timelineMedia.EnsureAudioPass(vm, path, _media);
                }
                else if (!vm.IsAnalyzingCommercials)
                {
                    // Analysis still needs a running pass; the hidden graph just stops drawing.
                    _timelineMedia.ReleaseAudioPass(vm);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.ShowThumbnails))
            {
                if (vm.ShowThumbnails)
                {
                    if (vm.SourcePath is { } path && _media is not null)
                        _timelineMedia.StartThumbnailLoad(vm, path, _media);
                }
                else
                {
                    _timelineMedia.CancelThumbnailLoad(vm, clearThumbnails: true);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.PlayheadUs))
            {
                UpdateTimelineLayout(forceRecenter: false);
                UpdateNoFrameOverlay(vm);
            }
        };
    }

    /// <summary>
    /// Pane A shows playhead − 1 frame; when the playhead sits on frame 0
    /// (or close enough that t − 1f is before the first decodable frame),
    /// there's no frame to render. Sibling-swap the MpvViewBefore HwndHost
    /// out and the black "No frame available" placeholder in.
    ///
    /// <para>"Frame 0" lives at <see cref="MainViewModel.FrameStartPhaseUs"/>,
    /// NOT at t=0. Plex DVR `.ts` captures land their first decoded frame
    /// mid-GOP - e.g. 1.301 s on the BBT fixture. The half-frame threshold
    /// is anti-flicker so we don't toggle around the boundary on fractional
    /// PTS values.</para>
    /// </summary>
    MainViewModel? IPlaybackScreen.Current => DataContext as MainViewModel;
    MediaInfo? IPlaybackScreen.Media => _media;
    FfmpegRunner? IPlaybackScreen.Ffmpeg => _ffmpeg;
    bool IPlaybackScreen.Closing => _allowClose;
    bool IPlaybackScreen.Switching => _switching;
    bool IPlaybackScreen.Analyzing => _markers.IsAnalyzing;
    void IPlaybackScreen.Notice(MainViewModel vm, StatusKind kind, string message) => Notice(vm, kind, message);
    void IPlaybackScreen.UpdateNoFrameOverlay(MainViewModel vm) => UpdateNoFrameOverlay(vm);
    void IPlaybackScreen.ShowFrameFailure(MainViewModel vm, string status)
    {
        NoFrameOverlay.Visibility = Visibility.Visible;
        NoFrameMessage.Text = "Could not load the preceding frame";
        MpvViewBefore.Visibility = Visibility.Collapsed;
        vm.StatusOverride = status;
    }

    private void UpdateNoFrameOverlay(MainViewModel vm)
    {
        if (vm.IsCollapsed) return;
        var frameUs = PlaybackController.FrameDurationUs(vm);
        var paneATimeUs = vm.PlayheadUs - frameUs;
        var needsOverlay = paneATimeUs < vm.FrameStartPhaseUs - (frameUs / 2);
        NoFrameMessage.Text = "No frame available";
        var currentlyShown = NoFrameOverlay.Visibility == Visibility.Visible;
        if (needsOverlay == currentlyShown) return;
        NoFrameOverlay.Visibility = needsOverlay ? Visibility.Visible : Visibility.Collapsed;
        MpvViewBefore.Visibility = needsOverlay ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTimelineScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Width changed on the ScrollViewer - Timeline.Width depends on its
        // ViewportWidth, so push the layout pass through. Defer to Render
        // priority so ViewportWidth has fully settled to its new value
        // before we read it (SizeChanged can fire mid-arrange). forceRecenter
        // stays false here: a casual window resize shouldn't yank the user's
        // scroll position around - UpdateTimelineLayout still re-scrolls if
        // the playhead is offscreen.
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        Dispatcher.BeginInvoke(new Action(() => UpdateTimelineLayout(forceRecenter: false)),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowFrame();
        // Backstop in case the ScrollViewer's own SizeChanged misses a
        // window-driven resize. Same deferred-Render trick so ViewportWidth
        // has updated before UpdateTimelineLayout reads it.
        if (e.NewSize.Width == e.PreviousSize.Width) return;
        Dispatcher.BeginInvoke(new Action(() => UpdateTimelineLayout(forceRecenter: false)),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Resize the inner timeline to viewport × zoom and (optionally) re-scroll
    /// so the playhead lands at ~25% of the viewport. By default this only
    /// scrolls when the playhead is *outside* the visible region, so playback
    /// doesn't fight the user's scroll.
    /// </summary>
    private void UpdateTimelineLayout(bool forceRecenter = false)
    {
        if (DataContext is not MainViewModel vm) return;
        if (TimelineScroll is null || Timeline is null) return;
        var viewport = TimelineScroll.ViewportWidth;
        if (viewport <= 0) return;
        var contentWidth = viewport * vm.ZoomFactor;
        Timeline.Width = contentWidth;

        if (vm.TimelineDurationUs <= 0) return;
        var playheadX = (vm.TimelinePositionUs / (double)vm.TimelineDurationUs) * contentWidth;
        var leftEdge = TimelineScroll.HorizontalOffset;
        var rightEdge = leftEdge + viewport;
        bool offscreen = playheadX < leftEdge + 8 || playheadX > rightEdge - 8;
        if (forceRecenter || offscreen)
        {
            var target = playheadX - viewport * 0.25;
            target = Math.Max(0, Math.Min(contentWidth - viewport, target));
            TimelineScroll.ScrollToHorizontalOffset(target);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pane B drives the playhead; Pane A is a passive follower seeked
        // alongside. Only Pane B updates PlayheadUs (see OnPreviewPropertyChanged),
        // but we subscribe to both so the on-pane timestamp overlays update
        // independently and we can detect Before/After drift for resync.
        _playback.After.PropertyChanged += OnPreviewPropertyChanged;
        _playback.Before.PropertyChanged += OnPreviewPropertyChanged;

        // WPF doesn't surface horizontal wheel (WM_MOUSEHWHEEL = 0x020E) -
        // hook the raw window message so side-scroll wheels and trackpad
        // horizontal gestures translate into horizontal timeline scrolling.
        var src = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        src?.AddHook(OnWindowMessage);

        // HwndHost-timing dance: mpv's `wid` option must be set BEFORE
        // mpv_initialize. Wait for each HwndHost to provide its child HWND,
        // then pass it to its preview viewmodel, then initialize. Try
        // immediately in case the hosts are already loaded; also subscribe
        // to Loaded so we attach if they weren't ready yet.
        AttachMpvHwnd();
        MpvViewBefore.Loaded += (_, _) => AttachMpvHwnd();
        MpvViewAfter.Loaded += (_, _) => AttachMpvHwnd();

        try
        {
            _ffmpeg = new FfmpegRunner();
            _store = new ProjectStore();
            _loader = new RecordingLoader(new MediaProbeService(_ffmpeg), new ChapterImportService(_ffmpeg), _store, _markers.Suggestions);
            _timelineMedia.Thumbnails = new ThumbnailService(_ffmpeg);
        }
        catch (FileNotFoundException)
        {
            // FFmpeg not yet installed - surfaces on open.
        }

        if (DataContext is MainViewModel initialVm) await LoadPreferencesAsync(initialVm);
        if (_startupPath is { } path) await OpenFileAsync(path);
        _ready.TrySetResult();
    }

    private void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_allowClose) return;
        // mpv fires property-change events from its own thread; marshal to the
        // UI dispatcher before touching DependencyObjects (binding sources) or
        // running any command-stack logic.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnPreviewPropertyChanged(sender, e)));
            return;
        }

        // Video aspect (file load / params change) → resize both panes so the
        // HwndHost matches the video frame and the chip sits flush under it.
        if (e.PropertyName == nameof(MpvPreviewViewModel.VideoAspect))
        {
            ApplyAspectFit();
            return;
        }

        if (e.PropertyName == nameof(MpvPreviewViewModel.PositionUs)
            && DataContext is MainViewModel vm)
        {
            // Update the on-pane timestamp + frame index for whichever pane
            // fired. Position is the live mpv value; frame index uses the
            // same phase-aware math as FrameSnap (Plex .ts captures start
            // mid-GOP so frame 0 doesn't sit at t=0).
            if (ReferenceEquals(sender, _playback.Before))
            {
                TimestampBeforePos.Text = FormatTimestampMs(_playback.Before.PositionUs);
                TimestampBeforeFrame.Text = FormatFrameIndex(_playback.Before.PositionUs, vm);
            }
            else if (ReferenceEquals(sender, _playback.After))
            {
                TimestampAfterPos.Text = FormatTimestampMs(_playback.After.PositionUs);
                TimestampAfterFrame.Text = FormatFrameIndex(_playback.After.PositionUs, vm);
            }

            if (_playback.After.IsLoading) return;
            // Only Pane B drives PlayheadUs.
            if (!ReferenceEquals(sender, _playback.After))
            {
                _playback.MaybeResyncPanes(vm);
                return;
            }

            if (vm.IsCollapsed)
            {
                vm.PlayheadUs = vm.KeptTimeline.ToSource(_playback.After.PositionUs);
                return;
            }
            vm.PlayheadUs = _playback.After.PositionUs;
            _playback.MaybeResyncPanes(vm);

            if (_playback.PlayUntilUs is { } stopUs && _playback.After.PositionUs >= stopUs)
            {
                _playback.After.Pause();
                _playback.Before.Pause();
                _playback.PlayUntilUs = null;
            }
        }
        else if (e.PropertyName == nameof(MpvPreviewViewModel.IsSeeking))
        {
            if (DataContext is MainViewModel currentVm)
            {
                // A repeated seek can finish at the same native position, so no
                // PositionUs notification replaces the optimistic scrub position.
                if (ReferenceEquals(sender, _playback.After) && !_playback.After.IsSeeking && !_playback.After.IsLoading)
                    currentVm.PlayheadUs = currentVm.IsCollapsed
                        ? currentVm.KeptTimeline.ToSource(_playback.After.PositionUs) : _playback.After.PositionUs;
                _playback.MaybeResyncPanes(currentVm);
            }
        }
        else if (e.PropertyName == nameof(MpvPreviewViewModel.IsPlaying))
        {
            // Swap the play/pause icon visibility.
            PauseGlyph.Visibility = _playback.After.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = _playback.After.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            if (DataContext is MainViewModel currentVm) _playback.MaybeResyncPanes(currentVm);
        }
    }

    /// <summary>
    /// Resize HwndHost to match video aspect, bottom-aligned, so the chip
    /// in row 1 sits flush under the video frame instead of below the
    /// HwndHost's bottom letterbox. Triggered by VideoAspect change and by
    /// SizeChanged on each pane Grid (window resize).
    /// </summary>
    private void ApplyAspectFit()
    {
        // Either pane's aspect works - both panes play the same source.
        var aspect = _playback.After.VideoAspect > 0
            ? _playback.After.VideoAspect
            : _playback.Before.VideoAspect;
        if (aspect <= 0) return;
        ApplyAspectFitToPane(MpvViewBefore, PaneAGrid, aspect);
        ApplyAspectFitToPane(MpvViewAfter, PaneBGrid, aspect);
    }

    private static void ApplyAspectFitToPane(MpvVideoView pane, Grid paneGrid, double aspect)
    {
        var availW = paneGrid.ActualWidth;
        // Reserve room for the chip below the video so the (video + chip)
        // StackPanel doesn't overflow vertically. Hardcoded estimate is fine
        // here - the chip is 14px line-height + 6px padding + 4px top margin
        // ≈ 24px; round up so the StackPanel always fits without WPF needing
        // to clip it during VerticalAlignment=Center reflow.
        const double chipReserve = 32;
        var availH = paneGrid.ActualHeight - chipReserve;
        if (availW <= 0 || availH <= 0) return;

        double w, h;
        if (availW / aspect <= availH)
        {
            // Constrained by width - full width, height derived.
            w = availW;
            h = availW / aspect;
        }
        else
        {
            // Constrained by height - full available height, width derived.
            h = availH;
            w = availH * aspect;
        }
        pane.Width = w;
        pane.Height = h;
    }

    private void OnPaneSizeChanged(object sender, SizeChangedEventArgs e) => ApplyAspectFit();

    /// <summary>
    /// Frame index for the chip overlays. Mirrors FrameSnap's phase-aware
    /// math so the displayed number matches the frame mpv has actually
    /// decoded. Returns "?" if the file doesn't have a valid frame rate
    /// yet. The "frame: " label is rendered separately in XAML.
    /// </summary>
    private static string FormatFrameIndex(long us, MainViewModel vm)
    {
        var fr = vm.FrameRate;
        if (fr.Numerator <= 0 || fr.Denominator <= 0) return "?";
        var adjusted = us - vm.FrameStartPhaseUs;
        if (adjusted < 0) adjusted = 0;
        var n = (long)System.Math.Round((double)adjusted * fr.Numerator / (1_000_000.0 * fr.Denominator));
        return n.ToString();
    }

    private static string FormatTimestampMs(long us)
    {
        if (us < 0) us = 0;
        var totalMs = us / 1000;
        var ms = totalMs % 1000;
        var s = (totalMs / 1000) % 60;
        var m = (totalMs / 60000) % 60;
        var h = totalMs / 3600000;
        return $"{h:00}:{m:00}:{s:00}.{ms:000}";
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => _playback.PlayPauseBoth();

    /// <summary>
    /// Toggle audio on Pane A ("before"). Pane B stays permanently muted -
    /// unmuting both produces comb-filter artifacts because the two mpv
    /// instances play the same file one frame apart. Icon swap is imperative
    /// here because MpvPreviewViewModel.IsMuted doesn't raise PropertyChanged
    /// (it's not a bindable observable today).
    /// </summary>
    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        var active = DataContext is MainViewModel { IsCollapsed: true } ? _playback.After : _playback.Before;
        var newMuted = !active.IsMuted;
        active.SetMuted(newMuted);
        ShowMuteState(newMuted);
    }

    private void ShowMuteState(bool muted)
    {
        SpeakerOnGlyph.Visibility  = muted ? Visibility.Collapsed : Visibility.Visible;
        SpeakerOffGlyph.Visibility = muted ? Visibility.Visible   : Visibility.Collapsed;
    }

    private bool _mpvAttachedAfter;
    private bool _mpvAttachedBefore;
    private void AttachMpvHwnd()
    {
        // Pane B (After) - the audible, primary preview.
        if (!_mpvAttachedAfter && MpvViewAfter.Hwnd != IntPtr.Zero)
        {
            try
            {
                _playback.After.Wid = MpvViewAfter.Hwnd;
                _playback.After.Initialize();
                // Pane B is muted; we hear audio from Pane A (the "before"
                // pane). Two mpv instances unmuted on the same file 1 frame
                // apart would comb-filter horribly.
                _playback.After.SetMuted(true);
                _mpvAttachedAfter = true;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("AttachMpvHwnd(After)", ex);
                MessageBox.Show(this,
                    $"Failed to initialize MPV preview (after pane).\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    "Check binaries/README.md for libmpv install instructions.",
                    "MPV initialization failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Pane A (Before) - secondary preview, has audio.
        if (!_mpvAttachedBefore && MpvViewBefore.Hwnd != IntPtr.Zero)
        {
            try
            {
                _playback.Before.Wid = MpvViewBefore.Hwnd;
                _playback.Before.Initialize();
                _mpvAttachedBefore = true;
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("AttachMpvHwnd(Before)", ex);
                // Don't pop a second MessageBox if the After pane already
                // failed - the After error is the actionable one.
            }
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path) || !ExportFormats.IsSupportedSource(path))
        {
            _openingPath = null;
            // Explorer's Open with, the command line and the unexported list can all name such a file.
            if (DataContext is MainViewModel told)
                Notice(told, StatusKind.Warning, File.Exists(path)
                    ? $"{Path.GetFileName(path)} was not opened. AdTrim opens MP4 and TS recordings."
                    : $"{Path.GetFileName(path)} was not found. It may have been moved or deleted.");
            return;
        }
        path = Path.GetFullPath(path);
        if (Application.Current is App exportingApp && exportingApp.IsExportDestination(path))
        {
            MessageBox.Show(this, "Another window is exporting to this file. Wait for that export to finish before opening it.",
                "Export in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            _openingPath = null;
            return;
        }
        if (Application.Current is App app && app.FindRecordingWindow(path, this) is { } existing)
        { existing.BringForward(); _openingPath = null; return; }
        if (_switching) return;
        _openingPath = path;
        try { await LoadRecordingAsync(path); }
        finally { _openingPath = null; }
    }

    private async Task LoadRecordingAsync(string path)
    {
        if (!File.Exists(path)) return;
        if (DataContext is not MainViewModel vm) return;
        if (BlockProjectSwitchForExport(vm) || _switching || vm.IsBusy) return;
        _playback.ResetShuttle();
        _switching = true;
        bool flushed;
        try { flushed = await FlushBeforeLeavingAsync(vm); }
        finally { _switching = false; }
        if (_saves.TakePendingClose(flushed) || !flushed) return;

        if (_ffmpeg is null || _loader is null || _store is null)
        {
            try
            {
                _ffmpeg ??= new FfmpegRunner();
                _store ??= new ProjectStore();
                _loader ??= new RecordingLoader(new MediaProbeService(_ffmpeg), new ChapterImportService(_ffmpeg), _store, _markers.Suggestions);
                _timelineMedia.Thumbnails ??= new ThumbnailService(_ffmpeg);
            }
            catch (FileNotFoundException ex)
            {
                MessageBox.Show(this, ex.Message, "FFmpeg not found",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var latestPreferences = await UserPreferences.LoadAsync(_prefs.Current);
        _prefs.UseNewFileMarkerSource(latestPreferences.NewFileMarkerSource);
        var previous = CaptureForRollback(vm);
        _switching = true;
        _hydrating = true;
        vm.IsBusy = true;
        vm.BusyOperation = "Opening";
        _playback.After.Pause();
        _playback.Before.Pause();
        _timelineMedia.DropAudioPass();
        _timelineMedia.CancelThumbnailLoad(vm, clearThumbnails: false);
        _playback.CancelFrameRecovery();
        _refines.Cancel();
        _saves.StopAutosave();
        bool resumeAfterFailure = false, closeRequested = false;
        try
        {
            var loadingVm = vm;
            var loaded = await _loader!.LoadAsync(path, _prefs.Current.NewFileMarkerSource, () =>
            {
                loadingVm.StatusKind = StatusKind.Info;
                loadingVm.StatusOverride = "Probing media…";
            });
            vm = ShowNewEditor(path, loaded, previous);
            await LoadStartingMarkersAsync(vm, path, loaded);
            await FinishOpeningAsync(vm, path, loaded);
        }
        catch (Exception ex)
        {
            resumeAfterFailure = RestoreAfterFailedOpen(vm, previous, ex);
            vm = previous.Vm;
        }
        finally
        {
            _hydrating = false;
            _switching = false;
            vm.IsBusy = false;
            vm.BusyOperation = null;
            closeRequested = _saves.TakePendingClose(true);
        }
        if (resumeAfterFailure && !closeRequested && !_allowClose && !_switching && ReferenceEquals(DataContext, previous.Vm)
            && previous.Vm.SourcePath == previous.Path && !_playback.After.IsPlaying)
        {
            if (previous.Vm.IsCollapsed) { if (previous.Vm.KeptTimeline.DurationUs > 0) _playback.After.Play(); }
            else { _playback.After.Play(); _playback.Before.Play(); }
        }
    }

    /// <summary>Everything a failed open must put back: the previous editor (with its undo history) and the screen around it.</summary>
    private sealed record OpenRollback(MainViewModel Vm, byte[]? AudioLoudness, ThumbnailStrip ThumbnailTiles, MediaInfo? Media,
        SourceFingerprint? Fingerprint, MarkerSetState? ImportedMarkers, bool ImportedAvailable, object? ImportedTooltip,
        StatusKind StatusKind, string? StatusOverride, double? ProgressPercent, string? Path, long Position, double ScrollOffset,
        bool Playing, bool Muted, long? PlayUntil);

    private OpenRollback CaptureForRollback(MainViewModel vm) => new(vm, vm.AudioLoudness, vm.ThumbnailTiles, _media, _sourceFingerprint,
        _markers.Imported, _markers.ImportedAvailable, UseOriginalImportedMarkersMenuItem.ToolTip,
        vm.StatusKind, vm.StatusOverride, vm.ProgressPercent, vm.SourcePath, vm.PlayheadUs, TimelineScroll.HorizontalOffset,
        _playback.After.IsPlaying, vm.IsCollapsed ? _playback.After.IsMuted : _playback.Before.IsMuted, _playback.PlayUntilUs);

    /// <summary>
    /// A new editor for the opened recording, shown in the window with both panes opening it. The previous
    /// editor, including its undo history, is left untouched until opening succeeds.
    /// </summary>
    private MainViewModel ShowNewEditor(string path, LoadedRecording loaded, OpenRollback previous)
    {
        var probed = loaded.Media;
        var fingerprint = loaded.Fingerprint;
        BannerInfo? importWarning = loaded.ImportError is { } importError
            ? new BannerInfo(StatusKind.Warning, "Imported markers are unavailable.", importError, Array.Empty<BannerAction>())
            : null;
        var vm = new MainViewModel
        {
            ShowWaveform = previous.Vm.ShowWaveform, ShowThumbnails = previous.Vm.ShowThumbnails,
            ShowRulerTicks = previous.Vm.ShowRulerTicks, IsBusy = true, BusyOperation = "Opening", Banner = importWarning,
        };
        WireViewModelEvents(vm);
        _markers.SetImported(loaded.ImportedMarkers, loaded.ImportedMarkersAvailable);
        AnalyzeAdBreaksButton.Style = (Style)FindResource("Button.Primary");
        _playback.PlayUntilUs = null;
        vm.HasAdTrimAnalysis = loaded.CachedSuggestions is not null;
        UseOriginalImportedMarkersMenuItem.ToolTip = loaded.ImportedMarkersUnavailableReason;
        RefreshImportedMarkersItem(vm);
        _media = probed;
        _sourceFingerprint = fingerprint;
        vm.SourceFingerprint = fingerprint;
        vm.LastExport = null;
        _playback.After.SelectAudioStream(_media.PrimaryAudioIndex);
        _playback.Before.SelectAudioStream(_media.PrimaryAudioIndex);
        vm.SourcePath = path;
        vm.FileName = Path.GetFileName(path);
        vm.DurationUs = _media.DurationUs;
        vm.FrameRate = _media.FrameRate;
        vm.FrameStartPhaseUs = _media.VideoStartTimeUs;
        vm.ZoomFactor = 1.0;   // start fully zoomed out - whole video visible
        vm.MediaInfoLine =
            $"{_media.FrameRate.AsDouble:0.##} fps · {_media.Width}×{_media.Height} · {_media.VideoCodec}";
        DataContext = vm;
        SetRecordingPaneLayout(false);
        UpdateTimelineLayout(forceRecenter: true);
        _playback.After.SetMuted(true);
        _playback.Before.SetMuted(previous.Muted);
        _playback.After.Open(path);
        _playback.Before.Open(path);
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        MpvViewAfter.Visibility = Visibility.Visible;
        MpvViewBefore.Visibility = Visibility.Visible;
        // The filmstrip goes first: it is the most visible and costs a few seeks, and the
        // waveform's full read yields to it. Starting here keeps it ahead of any analysis.
        if (vm.ShowThumbnails) _timelineMedia.StartThumbnailLoad(vm, path, _media);
        return vm;
    }

    /// <summary>Fills the new editor with the markers the loader chose: the saved project, chapters, nothing or AdTrim's suggestions.</summary>
    private async Task LoadStartingMarkersAsync(MainViewModel vm, string path, LoadedRecording loaded)
    {
        if (loaded.Start == RecordingStart.SavedProject)
        {
            MarkerSetController.HydrateFromSidecar(vm, loaded.Sidecar.Project!);
            if (loaded.Sidecar.Message is not null)
            {
                vm.StatusKind = StatusKind.Warning;
                vm.StatusOverride = loaded.Sidecar.Message ?? "Source modified time changed; verify project state.";
            }
            return;
        }
        if (loaded.SidecarNotLoaded)
        {
            var earlier = loaded.EarlierProject;
            vm.Banner = new BannerInfo(StatusKind.Warning,
                "Project sidecar was not loaded.",
                earlier is null ? loaded.Sidecar.Message ?? "Falling back to embedded chapters."
                    : "The recording's size or duration changed since this project was saved, so its markers were not loaded.",
                earlier is null ? Array.Empty<BannerAction>()
                    : new[] { new BannerAction("Use these markers anyway", () => UseEarlierProject(vm, path, earlier)) });
        }
        vm.StatusOverride = "Importing chapters…";
        if (loaded.Start == RecordingStart.AdTrimSuggestions)
        {
            MarkerSetController.HydrateBlank(vm, MarkerSetSource.AdTrim);
            vm.StatusOverride = "Analyzing ad breaks…";
            if (loaded.CachedSuggestions is { } cached) ApplyMarkerSet(vm, cached, "Load AdTrim suggested markers", confirm: false);
            else
            {
                var initialKey = _markers.Suggestions.Key(path, loaded.Fingerprint);
                var detected = await AnalyzeCommercialsAsync(vm, initialKey, cancellationToken: CancellationToken.None);
                if (detected is not null) ApplyMarkerSet(vm, detected, "Load AdTrim suggested markers", confirm: false);
                else
                {
                    MarkerSetController.HydrateBlank(vm, MarkerSetSource.Blank);
                    vm.StatusOverride = "Analysis did not finish. All video remains included.";
                }
            }
        }
        else if (loaded.Start == RecordingStart.Blank)
            MarkerSetController.HydrateBlank(vm, MarkerSetSource.Blank);
        else
            MarkerSetController.HydrateFromChapters(vm, _media!, loaded.Chapters);
    }

    /// <summary>A clean undo history, the remembered playback position, the timeline graph and the open-time status cleared.</summary>
    private async Task FinishOpeningAsync(MainViewModel vm, string path, LoadedRecording loaded)
    {
        vm.CommandStack.Clear();
        vm.IsDirty = false;
        vm.SaveStatus = loaded.HasSavedProject ? "Saved" : "No saved edits";
        if (_prefs.Current.Positions?.TryGetValue(path, out var position) == true)
        {
            position = Math.Clamp(position, 0, vm.DurationUs);
            _playback.After.Open(path, position);
            _playback.Before.Open(path, Math.Max(0, position - PlaybackController.FrameDurationUs(vm)));
            vm.PlayheadUs = position;
        }
        else vm.PlayheadUs = 0;
        // Ensure Pane A starts 1 frame behind Pane B on initial load
        // (PlayheadUs = 0 means Pane A wants frame −1 → no-frame overlay).
        UpdateNoFrameOverlay(vm);

        if (vm.ShowWaveform)
            _timelineMedia.EnsureAudioPass(vm, path, _media!);
        else if (!vm.IsAnalyzingCommercials)
            _timelineMedia.ReleaseAudioPass(vm);

        if (!vm.ShowThumbnails)
            _timelineMedia.CancelThumbnailLoad(vm, clearThumbnails: true);

        // Opening replaces the editor's banner; keep offering the settings reset while it is needed.
        if (vm.Banner is null && await UserPreferences.IsDamagedAsync()) ShowDamagedPreferencesBanner();

        // Clear our transient open-time status only if it's still showing -
        // a sidecar warning or other update set between the probe and now
        // takes precedence and must not be wiped.
        if (vm.StatusOverride is "Probing media…" or "Importing chapters…")
            vm.StatusOverride = null;
    }

    /// <summary>
    /// Puts the previous editor and recording back after a failed open and shows the error. The restored
    /// preview stays paused behind the error; returns true when playback should resume once it is dismissed.
    /// </summary>
    /// <param name="opening">The editor current when opening failed: the new one if it was already shown.</param>
    private bool RestoreAfterFailedOpen(MainViewModel opening, OpenRollback previous, Exception ex)
    {
        bool resume = false;
        _timelineMedia.ReleaseAudioPass(opening);
        _timelineMedia.CancelThumbnailLoad(opening, clearThumbnails: false);
        _media = previous.Media;
        _sourceFingerprint = previous.Fingerprint;
        _markers.SetImported(previous.ImportedMarkers, previous.ImportedAvailable);
        UseOriginalImportedMarkersMenuItem.ToolTip = previous.ImportedTooltip;
        var vm = previous.Vm;
        DataContext = vm;
        vm.AudioLoudness = previous.AudioLoudness;
        vm.ThumbnailTiles = previous.ThumbnailTiles;
        SetRecordingPaneLayout(vm.IsCollapsed);
        if (vm.HasAdTrimAnalysis) AnalyzeAdBreaksButton.ClearValue(StyleProperty);
        else AnalyzeAdBreaksButton.Style = (Style)FindResource("Button.Primary");
        _playback.PlayUntilUs = previous.PlayUntil;
        vm.PlayheadUs = previous.Position;
        vm.StatusKind = previous.StatusKind;
        vm.StatusOverride = previous.StatusOverride;
        vm.ProgressPercent = previous.ProgressPercent;
        UpdateTimelineLayout(forceRecenter: false);
        TimelineScroll.ScrollToHorizontalOffset(previous.ScrollOffset);
        string message = ex.Message;
        try
        {
            if (previous.Path is { } previousPath && previous.Media is { } previousMedia)
            {
                _playback.After.SetMuted(vm.IsCollapsed ? previous.Muted : true);
                _playback.Before.SetMuted(previous.Muted);
                if (vm.IsCollapsed)
                {
                    _playback.Before.Stop();
                    if (vm.KeptTimeline.DurationUs > 0)
                        _playback.After.Open(vm.KeptTimeline.ToMpvEdl(previousPath, previousMedia.TimelineStartTimeUs),
                            vm.KeptTimeline.ToOutput(previous.Position), buffered: true);
                    else _playback.After.Stop();
                }
                else
                {
                    _playback.After.Open(previousPath, previous.Position);
                    _playback.Before.Open(previousPath, Math.Max(0, previous.Position - PlaybackController.FrameDurationUs(vm)));
                }
                _playback.After.SelectAudioStream(previousMedia.PrimaryAudioIndex);
                _playback.Before.SelectAudioStream(previousMedia.PrimaryAudioIndex);
                VideoPlaceholder.Visibility = Visibility.Collapsed;
                MpvViewAfter.Visibility = Visibility.Visible;
                MpvViewBefore.Visibility = Visibility.Visible;
                UpdateNoFrameOverlay(vm);
                if (vm.ShowWaveform) _timelineMedia.EnsureAudioPass(vm, previousPath, previousMedia);
                if (vm.ShowThumbnails) _timelineMedia.StartThumbnailLoad(vm, previousPath, previousMedia);
                resume = previous.Playing;
            }
            else
            {
                _playback.After.Stop();
                _playback.Before.Stop();
                VideoPlaceholder.Visibility = Visibility.Visible;
                MpvViewAfter.Visibility = Visibility.Collapsed;
                MpvViewBefore.Visibility = Visibility.Collapsed;
                NoFrameOverlay.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception restoreError)
        {
            _playback.After.Stop();
            _playback.Before.Stop();
            message += "\n\nThe previous recording's preview could not be restored: " + restoreError.Message;
        }
        MessageBox.Show(this, message, "Failed to open", MessageBoxButton.OK, MessageBoxImage.Error);
        return resume;
    }

    private async Task<MarkerSetState?> AnalyzeCommercialsAsync(MainViewModel vm, string key,
        CancellationToken cancellationToken)
    {
        if (_ffmpeg is null || _media is null || vm.SourcePath is not { } source || vm.SourceFingerprint is not { } fingerprint) return null;
        var sourceAtStart = source;
        using var linked = _markers.BeginAnalysis(cancellationToken);
        var wasBusy = vm.IsBusy;
        var previousOperation = vm.BusyOperation;
        var previousProgress = vm.ProgressPercent;
        vm.IsBusy = true;
        vm.IsAnalyzingCommercials = true;
        vm.BusyOperation = "Analyzing commercial breaks";
        vm.StatusOverride = "Analyzing ad breaks…";
        vm.AnalysisStage = "Starting analysis…";
        vm.ProgressPercent = 0;
        vm.AnalysisTimeLeft = null;
        var taskbar = TaskbarItemInfo ??= new TaskbarItemInfo();
        taskbar.ProgressState = TaskbarItemProgressState.Normal;
        taskbar.ProgressValue = 0;
        var total = Stopwatch.StartNew();
        Stopwatch? working = null;
        double workingFrom = 0;
        try
        {
            var progress = new Progress<CommercialDetectionProgress>(p =>
            {
                if (_markers.IsCurrent(linked) && !linked.IsCancellationRequested
                    && vm.SourcePath == sourceAtStart && p.DurationUs > 0)
                {
                    double fraction = Math.Clamp(p.TimeUs / (double)p.DurationUs, 0, 1);
                    // Steps report overall progress; never let the bar move backward.
                    if (!p.Queued) fraction = Math.Max(fraction, vm.ProgressPercentValue);
                    vm.ProgressPercent = fraction;
                    taskbar.ProgressState = p.Queued ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
                    taskbar.ProgressValue = fraction;
                    vm.AnalysisStage = p.Queued ? "Waiting for another recording to finish analysis…" : p.Stage ?? "Analyzing ad breaks…";
                    if (p.Queued) return;
                    if (working is null) { working = Stopwatch.StartNew(); workingFrom = fraction; }
                    vm.AnalysisTimeLeft = MarkerSetController.TimeLeftText(working.Elapsed, fraction - workingFrom, 1 - fraction);
                }
            });
            var media = _media;
            // Join the recording's audio pass (the timeline graph's) rather than reading the audio again.
            var result = await new SuggestionEngine(_ffmpeg).AnalyzeAsync(source, media, progress, linked.Token,
                (stage, ct) => Dispatcher.Invoke(() => _timelineMedia.EnsureAudioPass(vm, sourceAtStart, media)).JoinAsync(stage, ct),
                _timelineMedia.WaitForFullReadTurnAsync);
            if (linked.IsCancellationRequested || vm.SourcePath != sourceAtStart || vm.SourceFingerprint != fingerprint) return null;
            var state = MarkerSetController.FromAnalysis(result, _media.DurationUs);
            vm.HasAdTrimAnalysis = true;
            vm.Banner = MarkerSetController.AnalysisFinished(state, total.Elapsed);
            try { _markers.Suggestions.Save(key, state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { vm.Banner = new BannerInfo(StatusKind.Warning, "Suggestions could not be cached.", ex.Message, Array.Empty<BannerAction>()); }
            return state;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            vm.Banner = new BannerInfo(StatusKind.Warning, "Commercial analysis did not finish.",
                ex.Message, Array.Empty<BannerAction>());
            return null;
        }
        finally
        {
            taskbar.ProgressState = TaskbarItemProgressState.None;
            vm.AnalysisTimeLeft = null;
            vm.IsAnalyzingCommercials = false;
            _markers.EndAnalysis(linked);
            // Nothing else wants an unfinished pass once analysis stops with the graph hidden.
            if (!vm.ShowWaveform && _timelineMedia.AudioPass is { Completion.IsCompleted: false }) _timelineMedia.ReleaseAudioPass(vm);
            vm.IsBusy = wasBusy;
            vm.BusyOperation = previousOperation;
            vm.ProgressPercent = previousProgress;
        }
    }

    private void UseEarlierProject(MainViewModel vm, string path, AdTrimProject earlier)
    {
        if (vm.IsBusy || !string.Equals(vm.SourcePath, path, StringComparison.OrdinalIgnoreCase)) return;
        var state = MarkerSetController.FromEarlierProject(earlier);
        vm.Banner = null;
        ApplyMarkerSet(vm, state, "Use markers from the earlier project", confirm: false);
    }

    private void ApplyMarkerSet(MainViewModel vm, MarkerSetState state, string action, bool confirm)
    {
        if (confirm && !ConfirmDiscardEdits(vm, state.Source)) return;
        if (vm.IsCollapsed) SetCollapsed(vm, false);
        vm.CommandStack.Execute(new ReplaceMarkerSetCommand(vm, state, action));
        vm.StatusOverride = MarkerSetController.AppliedStatus(state, action);
        vm.StatusKind = StatusKind.Info;
    }

    /// <summary>Asks before edits are replaced by <paramref name="destination"/> markers. True when there
    /// are no edits to lose or the user agrees. Ask before any analysis work, not after.</summary>
    private bool ConfirmDiscardEdits(MainViewModel vm, MarkerSetSource destination)
        => MarkerSetController.DiscardQuestion(vm, destination) is not { } question || _askDiscardEdits(question.Message, question.Title);

    // Replaceable so the smoke check can answer it and see when it was asked.
    private Func<string, string, bool> _askDiscardEdits;

    /// <summary>The toolbar's Analyze ad breaks / Analyze ad breaks again button.</summary>
    private void OnAnalyzeAdBreaks(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.HasAdTrimAnalysis) OnAnalyzeCommercialsAgain(sender, e);
        else OnUseAdTrimSuggestions(sender, e);
    }

    // The Ad break detection menu stays enabled (for the starting-markers preference), so this
    // item needs both conditions itself: the recording has chapters, and markers can change now.
    private void RefreshImportedMarkersItem(MainViewModel vm)
        => UseOriginalImportedMarkersMenuItem.IsEnabled = _markers.ImportedAvailable && vm.CanReplaceMarkers;

    private void OnOpenMarkersMenu(object sender, RoutedEventArgs e)
    {
        MarkersPopupImported.IsEnabled = UseOriginalImportedMarkersMenuItem.IsEnabled;
        MarkersPopupImported.ToolTip = UseOriginalImportedMarkersMenuItem.ToolTip;
        MarkersPopup.PlacementTarget = MarkersButton;
        MarkersPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MarkersPopup.IsOpen = true;
    }

    private void OnCancelCommercialAnalysis(object sender, RoutedEventArgs e)
    {
        _markers.CancelAnalysis();
        if (DataContext is MainViewModel vm) vm.StatusOverride = "Cancelling commercial analysis...";
    }

    private void OnUseImportedMarkers(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || _markers.Imported is not { } imported) return;
        ApplyMarkerSet(vm, imported, "Revert to original imported markers", confirm: true);
    }

    private async void OnUseAdTrimSuggestions(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || vm.SourcePath is null || vm.SourceFingerprint is null) return;
        var source = vm.SourcePath;
        var key = _markers.Suggestions.Key(source, vm.SourceFingerprint);
        var state = _markers.Suggestions.Load(key, vm.DurationUs);
        bool confirmed = false;
        if (state is null)
        {
            // Analysis takes minutes: ask before doing the work, not after.
            if (!ConfirmDiscardEdits(vm, MarkerSetSource.AdTrim)) return;
            confirmed = true;
            state = await AnalyzeCommercialsAsync(vm, key, CancellationToken.None);
        }
        if (state is null)
        {
            vm.StatusOverride = "AdTrim suggested markers are unavailable; current markings were kept.";
            return;
        }
        if (vm.SourcePath != source || vm.SourceFingerprint is null
            || !StringComparer.Ordinal.Equals(key, _markers.Suggestions.Key(vm.SourcePath, vm.SourceFingerprint))) return;
        ApplyMarkerSet(vm, state, "Revert to original AdTrim suggested markers", confirm: !confirmed);
    }

    private async void OnAnalyzeCommercialsAgain(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || vm.SourcePath is null || vm.SourceFingerprint is null) return;
        var source = vm.SourcePath;
        var key = _markers.Suggestions.Key(source, vm.SourceFingerprint);
        // Ask before the analysis work; markers are locked while it runs, so the answer stays valid.
        if (!ConfirmDiscardEdits(vm, MarkerSetSource.AdTrim)) return;
        var state = await AnalyzeCommercialsAsync(vm, key, CancellationToken.None);
        if (state is null)
        {
            vm.StatusOverride = "Analysis did not finish; current markings were kept.";
            return;
        }
        if (vm.SourcePath == source && vm.SourceFingerprint is not null
            && StringComparer.Ordinal.Equals(key, _markers.Suggestions.Key(vm.SourcePath, vm.SourceFingerprint)))
            ApplyMarkerSet(vm, state, "Analyze ad breaks again", confirm: false);
    }

    private void OnClearAllMarkings(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers) return;
        var hasMarkings = vm.HasWorkingMarkings;
        if (!hasMarkings) return;
        if (MessageBox.Show(this,
                "This deletes all splits, section names, and exclusions. The entire video will be included.",
                "Delete all markings", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel, MessageBoxOptions.None) != MessageBoxResult.OK) return;
        if (vm.IsCollapsed) SetCollapsed(vm, false);
        vm.CommandStack.Execute(new ClearAllMarkingsCommand(vm));
        vm.StatusOverride = "All markings deleted. The entire video is included. Undo restores the previous markings.";
    }

    private async void OnAutosaveTick(object? sender, EventArgs e)
    {
        _saves.StopAutosave();
        if (_hydrating || _saves.InProgress || DataContext is not MainViewModel vm) return;
        await _saves.SaveAsync(vm);
    }

    /// <summary>
    /// Windows is logging off or shutting down and does not wait for the usual close, which saves in
    /// the background. Saves this window's pending edits now.
    /// </summary>
    internal void SaveForSessionEnd()
    {
        _saves.StopAutosave();
        if (_hydrating || DataContext is not MainViewModel vm) return;
        _saves.SaveNow(vm);
    }

    private Task<bool> FlushBeforeLeavingAsync(MainViewModel vm) => _saves.FlushAsync(vm, () =>
        MessageBox.Show(this, "Edits could not be saved. Retry?\nChoose No to discard unsaved edits, or Cancel to stay here.",
            "Save failed", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Cancel => SaveFailureChoice.Stay,
            MessageBoxResult.No => SaveFailureChoice.Discard,
            _ => SaveFailureChoice.Retry,
        }, () => SavePreferencesAsync(vm));

    private sealed class TimerAutosave(DispatcherTimer timer) : IAutosave
    {
        public void Start() => timer.Start();
        public void Restart() { timer.Stop(); timer.Start(); }
        public void Stop() => timer.Stop();
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _playback.ResetShuttle();
        if (_allowClose)
        {
            _saves.StopAutosave();
            _autosaveTimer.Tick -= OnAutosaveTick;
            // mpv must release its video output before WPF destroys the host HWNDs.
            _playback.After.PropertyChanged -= OnPreviewPropertyChanged;
            _playback.Before.PropertyChanged -= OnPreviewPropertyChanged;
            _playback.After.Dispose();
            _playback.Before.Dispose();
            return;
        }
        e.Cancel = true;
        await _markers.WaitForAnalysisToStopAsync();
        if (_switching)
        {
            _saves.CloseAfterSwitch();
            return;
        }
        if (_closingExport || DataContext is not MainViewModel vm) return;
        if (_activeExportDialog is { } export)
        {
            if (export.IsExportInFlight)
            {
                if (MessageBox.Show(this, "Cancel this export and close this window? Other AdTrim windows will stay open.",
                    "Cancel export and close", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                _closingExport = true;
                try { await export.CancelAndWaitAsync(); }
                finally { _closingExport = false; }
            }
            export.Close();
        }
        _switching = true;
        _refines.Cancel();
        if (await FlushBeforeLeavingAsync(vm))
        {
            _refines.Cancel();
            _timelineMedia.ReleaseAudioPass(vm);
            _timelineMedia.CancelThumbnailLoad(vm, false);
            _allowClose = true;
            Close();
        }
        _switching = false;
        _saves.DropPendingClose();
    }

    // -------------------------------------------------------------------
    // Keyboard router
    // -------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        { OnNewWindow(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (_activeExportDialog is not null) return;

        if (DataContext is not MainViewModel vm) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        { OnShowShortcuts(this, new RoutedEventArgs()); e.Handled = true; return; }
        // Before the comma's frame step below.
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        { _ = OpenSettingsAsync(); e.Handled = true; return; }

        if (e.Key == Key.Escape)
        {
            if (vm.IsBusy && vm.BusyOperation == "Refining") _refines.Cancel();
            else if (vm.IsBusy && vm.BusyOperation == "Analyzing commercial breaks")
            {
                _markers.CancelAnalysis();
                vm.StatusOverride = "Cancelling commercial analysis…";
            }
            else if (!vm.IsBusy && vm.SelectionKind != SelectionKind.None) vm.ClearSelection();
            e.Handled = true;
            return;
        }

        if (_markers.IsAnalyzing && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        { _playback.PlayPauseBoth(); e.Handled = true; return; }

        if (vm.IsBusy)
        {
            if (e.Key == Key.Tab) return;
            vm.StatusKind = StatusKind.Warning;
            vm.StatusOverride = $"{vm.BusyOperation ?? "Operation"} in progress.";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        { OnViewUnexportedFiles(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.None)
        { if (!e.IsRepeat) OnToggleCollapsed(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key is Key.Home or Key.End && Keyboard.Modifiers == ModifierKeys.None && vm.IsFileLoaded)
        {
            var positionUs = e.Key == Key.Home ? 0 : vm.TimelineDurationUs;
            _playback.SeekTo(vm, vm.IsCollapsed ? vm.KeptTimeline.ToSource(positionUs) : positionUs);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.J or Key.K or Key.L && vm.IsFileLoaded)
        { _playback.Shuttle(vm, e.Key); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { OnFitTimeline(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { OnZoomIn(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { OnZoomOut(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!ctrl && e.Key == Key.E) { OnExport(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (vm.IsCollapsed)
        {
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) _playback.PlayPauseBoth();
            else if (e.Key is Key.OemComma or Key.OemPeriod) _playback.StepFrame(vm, e.Key == Key.OemComma ? -1 : 1);
            else if (e.Key is Key.Left or Key.Right or Key.OemOpenBrackets or Key.OemCloseBrackets)
            {
                long step = e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets ? 1_000_000 : ArrowStepUs(vm);
                long direction = e.Key is Key.Left or Key.OemOpenBrackets ? -1 : 1;
                _playback.SeekTo(vm, vm.KeptTimeline.ToSource(vm.TimelinePositionUs + direction * step));
            }
            else if (ctrl && e.Key == Key.S) OnAutosaveTick(null, EventArgs.Empty);
            else if (ctrl && e.Key == Key.O) OnOpenFile(this, new RoutedEventArgs());
            else if (ctrl && e.Key == Key.W) OnCloseProject(this, new RoutedEventArgs());
            else if (!ctrl && !shift && (e.Key is >= Key.D0 and <= Key.D9 || e.Key is >= Key.NumPad0 and <= Key.NumPad9))
            {
                var digit = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                _playback.SeekTo(vm, vm.KeptTimeline.ToSource(vm.TimelineDurationUs * digit / 10));
            }
            else if (e.Key == Key.Tab) return;
            else vm.StatusOverride = "Return to Edit view with P to change cuts.";
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && shift && !ctrl) { OnConfirmAndNext(this, new RoutedEventArgs()); e.Handled = true; return; }
        bool typing = Keyboard.FocusedElement is TextBox;
        if (ctrl && shift && e.Key == Key.A && !typing) { OnSelectRedMarkers(this, new RoutedEventArgs()); e.Handled = true; return; }
        // With Alt held, WPF reports the letter as a system key.
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && (e.Key == Key.A || e.Key == Key.System && e.SystemKey == Key.A) && !typing)
        { OnSelectVisibleMarkers(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None) { OnRenameSelectedSegment(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        { JumpToMarker(vm, e.SystemKey == Key.Right ? 1 : -1); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.A && !typing) { OnSelectAllMarkers(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Delete && !typing) { OnClearAllMarkings(this, new RoutedEventArgs()); e.Handled = true; return; }
        // Undo / redo
        if (ctrl && !shift && e.Key == Key.Z) { vm.CommandStack.Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (shift && e.Key == Key.Z))) { vm.CommandStack.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { OnAutosaveTick(null, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { OnOpenFile(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.W) { OnCloseProject(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.R) { OnRefineAll(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.C) { OnConfirmAllRemaining(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!ctrl && e.Key == Key.R && vm.SelectedMarkers.Count > 0)
        { OnRefineSelected(this, new RoutedEventArgs()); e.Handled = true; return; }

        // Frame step (`,` / `.`). Both route through SeekTo so chained taps
        // accumulate against `vm.PlayheadUs` (the synchronous truth) rather
        // than against mpv's reported time-pos (which can lag the latest tap
        // by 100s of ms). SeekTo uses an exact seek, so each press advances
        // by exactly one frame duration regardless of fast-seek/keyframe
        // alignment.
        if (e.Key == Key.OemComma)
        {
            _playback.StepFrame(vm, -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.OemPeriod)
        {
            _playback.StepFrame(vm, +1);
            e.Handled = true;
            return;
        }

        // 1-second step
        if (e.Key == Key.OemOpenBrackets)  { _playback.SeekTo(vm, vm.PlayheadUs - 1_000_000); e.Handled = true; return; }
        if (e.Key == Key.OemCloseBrackets) { _playback.SeekTo(vm, vm.PlayheadUs + 1_000_000); e.Handled = true; return; }

        // Arrow seek (zoom-dependent step)
        if (e.Key is Key.Left or Key.Right)
        {
            long step = ArrowStepUs(vm);
            var dir = e.Key == Key.Left ? -1L : 1L;
            _playback.SeekTo(vm, vm.PlayheadUs + dir * step);
            e.Handled = true;
            return;
        }

        // Percentage seek (YouTube-style): 0 → start, 1 → 10%, ..., 9 → 90%.
        // Top-row digits and numpad both work. Ctrl+0 (Fit timeline) is
        // intentionally excluded by the !ctrl guard.
        if (!ctrl && !shift && vm.DurationUs > 0)
        {
            int? digit = e.Key switch
            {
                Key.D0 or Key.NumPad0 => 0,
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                Key.D7 or Key.NumPad7 => 7,
                Key.D8 or Key.NumPad8 => 8,
                Key.D9 or Key.NumPad9 => 9,
                _ => null,
            };
            if (digit is int d)
            {
                _playback.SeekTo(vm, vm.DurationUs * d / 10);
                e.Handled = true;
                return;
            }
        }

        // Add split at playhead.
        //
        // NOT applying FrameSnap.Snap here: `vm.PlayheadUs` is the exact
        // PTS mpv is currently displaying (it's continuously synced from
        // mpv's time-pos). FrameSnap derives a frame grid from the probed
        // avg_frame_rate, which can be off from the source's actual
        // container PTS values by a few ms. Snapping to FrameSnap's grid
        // can land the marker *between* real frames - meaning the user
        // can't navigate back to it with `,`/`.`, and clicking it shows
        // the wrong frame. Placing exactly at PlayheadUs guarantees the
        // marker sits on a real frame mpv knows about.
        if (e.Key == Key.S && !ctrl)
        {
            AddSplitAtPlayhead(vm);
            e.Handled = true;
            return;
        }

        // Move selected split to playhead (M)
        if (e.Key == Key.M && !ctrl && vm.SelectedMarkers.Count > 0)
        {
            OnMoveSelectedToPlayhead(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Confirm split (C)
        if (e.Key == Key.C && !ctrl && vm.SelectionKind == SelectionKind.Markers)
        { OnConfirmSelectedSplit(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.C && !ctrl && vm.SelectedMarker is { } sel)
        {
            if (sel.Confirmed && !ConfirmUnconfirm(sel)) { e.Handled = true; return; }
            vm.CommandStack.Execute(new ToggleConfirmedCommand(sel));
            e.Handled = true;
            return;
        }

        // Exclude segment (X)
        if (e.Key == Key.X && !ctrl && vm.SelectedSegment is { } seg)
        {
            vm.CommandStack.Execute(new ToggleExcludedCommand(vm, seg));
            e.Handled = true;
            return;
        }

        // Delete - context-sensitive:
        //   segment selected → toggle exclusion (same as X)
        //   marker  selected → delete the split
        if (e.Key == Key.Delete)
        {
            if (vm.SelectedSegment is { } delSeg)
            {
                vm.CommandStack.Execute(new ToggleExcludedCommand(vm, delSeg));
                e.Handled = true;
                return;
            }
            if (vm.SelectionKind == SelectionKind.Markers)
            {
                OnDeleteSelectedSplit(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (vm.SelectedMarker is { } delMarker && !delMarker.IsBookend)
            {
                vm.CommandStack.Execute(new DeleteSplitCommand(vm, delMarker));
                vm.ClearSelection();
                e.Handled = true;
                return;
            }
        }

        // Previous unconfirmed (Shift+N)
        if (e.Key == Key.N && shift && !ctrl)
        {
            OnJumpPreviousUnconfirmed(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Next unconfirmed (N)
        if (e.Key == Key.N && !ctrl)
        {
            var next = vm.NextUnconfirmedAfter(vm.PlayheadUs);
            if (next is not null) { vm.SelectMarker(next); _playback.SeekTo(vm, next.TimeUs); }
            e.Handled = true;
            return;
        }

        // Play/pause (Space)
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            _playback.PlayPauseBoth();
            e.Handled = true;
            return;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_activeExportDialog is not null) return;
        if (DataContext is not MainViewModel vm) return;

        // Ctrl+Wheel = zoom.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var factor = e.Delta > 0 ? 1.25 : 0.8;
            var newZoom = Math.Clamp(vm.ZoomFactor * factor, 0.25, 32.0);
            vm.ZoomFactor = newZoom;
            e.Handled = true;
            return;
        }

        // Plain wheel over the timeline = horizontal scroll. Up = move
        // viewport LEFT (i.e. content scrolls right), down = viewport RIGHT.
        // We use the wheel direction as-is (e.Delta > 0 means wheel-up).
        if (TimelineScroll is null || !IsMouseOverTimeline(e)) return;
        var step = TimelineScroll.ViewportWidth * 0.15;   // 15% of viewport per notch
        var target = TimelineScroll.HorizontalOffset + (e.Delta > 0 ? -step : step);
        target = Math.Max(0, Math.Min(TimelineScroll.ScrollableWidth, target));
        TimelineScroll.ScrollToHorizontalOffset(target);
        e.Handled = true;
    }

    private bool IsMouseOverTimeline(MouseEventArgs e)
    {
        if (TimelineScroll is null) return false;
        var pos = e.GetPosition(TimelineScroll);
        return pos.X >= 0 && pos.X <= TimelineScroll.ActualWidth
            && pos.Y >= 0 && pos.Y <= TimelineScroll.ActualHeight;
    }

    // -------------------------------------------------------------------
    // Middle-click drag to pan the timeline
    // -------------------------------------------------------------------
    private bool _timelineDragging;
    private double _timelineDragStartX;
    private double _timelineDragStartOffset;

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (TimelineScroll is null) return;
        _timelineDragging = true;
        _timelineDragStartX = e.GetPosition(TimelineScroll).X;
        _timelineDragStartOffset = TimelineScroll.HorizontalOffset;
        Mouse.OverrideCursor = Cursors.ScrollAll;
        TimelineScroll.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_timelineDragging || TimelineScroll is null) return;
        var x = e.GetPosition(TimelineScroll).X;
        var dx = x - _timelineDragStartX;
        // Drag right = pan content right = viewport offset DECREASES.
        var target = _timelineDragStartOffset - dx;
        target = Math.Max(0, Math.Min(TimelineScroll.ScrollableWidth, target));
        TimelineScroll.ScrollToHorizontalOffset(target);
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_timelineDragging) return;
        _timelineDragging = false;
        Mouse.OverrideCursor = null;
        TimelineScroll?.ReleaseMouseCapture();
        e.Handled = true;
    }

    private const int WM_MOUSEHWHEEL = 0x020E;
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
        if (TimelineScroll is null) return IntPtr.Zero;

        // High word of wParam is the wheel delta (signed). Positive = scroll right.
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        var step = TimelineScroll.ViewportWidth * 0.15 * (Math.Abs(delta) / 120.0);
        var target = TimelineScroll.HorizontalOffset + (delta > 0 ? step : -step);
        target = Math.Max(0, Math.Min(TimelineScroll.ScrollableWidth, target));
        TimelineScroll.ScrollToHorizontalOffset(target);
        handled = true;
        return IntPtr.Zero;
    }

    private static long ArrowStepUs(MainViewModel vm)
    {
        // Zoom-aware: at higher zoom, smaller steps.
        var z = vm.ZoomFactor;
        if (z >= 8) return PlaybackController.FrameDurationUs(vm);   // 1 frame
        if (z >= 2) return 1_000_000;             // 1 sec
        if (z >= 0.75) return 5_000_000;          // 5 sec
        return 30_000_000;                        // 30 sec
    }

    // -------------------------------------------------------------------
    // Menu / drag-drop / window controls
    // -------------------------------------------------------------------

    private void OnNewWindow(object sender, RoutedEventArgs e) => ((App)Application.Current).NewWindow();

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && BlockProjectSwitchForExport(vm)) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video recordings (*.mp4;*.ts)|*.mp4;*.ts|MP4 video (*.mp4)|*.mp4|MPEG transport stream (*.ts)|*.ts",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) _ = OpenFileAsync(dlg.FileName);
    }

    /// <summary>Public hook for the empty-state view's "Open file…" button.</summary>
    public void OpenFileFromEmptyState() => OnOpenFile(this, new RoutedEventArgs());

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm && BlockProjectSwitchForExport(vm)) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var first = files.FirstOrDefault(f =>
            ExportFormats.IsSupportedSource(f));
        if (first is not null) _ = OpenFileAsync(first);
    }

    private void OnTitlePathScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewport = (ScrollViewer)sender;
        TitlePathEllipsis.Visibility = viewport.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (viewport.HorizontalOffset < viewport.ScrollableWidth) viewport.ScrollToRightEnd();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private WindowState _lastNonMinimizedState = WindowState.Normal;
    private const string MaxIconGeometry = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreIconGeometry = "M2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) _lastNonMinimizedState = WindowState;
        UpdateWindowFrame();
        if (MaxButtonIcon is null) return;
        bool maximized = _lastNonMinimizedState == WindowState.Maximized;
        MaxButtonIcon.Data = System.Windows.Media.Geometry.Parse(maximized ? RestoreIconGeometry : MaxIconGeometry);
        MaxButton.ToolTip = maximized ? "Restore down" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, maximized ? "Restore down" : "Maximize");
    }

    private void UpdateWindowFrame()
    {
        if (WindowLayoutGrid is null) return;
        WindowLayoutGrid.Margin = WindowState == WindowState.Maximized
            ? MonitorBounds.MaximizedInsets(this) : new Thickness(0);
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e) => UpdateWindowFrame();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.AboutDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
        => OnAutosaveTick(null, EventArgs.Empty);

    private async void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (BlockProjectSwitchForExport(vm) || _switching || vm.IsBusy) return;
        _playback.ResetShuttle();
        _switching = true;
        bool flushed;
        try { flushed = await FlushBeforeLeavingAsync(vm); }
        finally { _switching = false; }
        if (_saves.TakePendingClose(flushed) || !flushed) return;
        SetCollapsed(vm, false);
        _timelineMedia.ReleaseAudioPass(vm);
        _timelineMedia.CancelThumbnailLoad(vm, clearThumbnails: true);
        _refines.Cancel();
        _saves.StopAutosave();
        _playback.After.Pause();
        _playback.Before.Pause();
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();
        vm.SourcePath = null;
        vm.HasAdTrimAnalysis = false;
        vm.FileName = null;
        vm.DurationUs = 0;
        vm.MediaInfoLine = "";
        vm.PlayheadUs = 0;
        vm.Banner = null;
        vm.ClearStatus();
        vm.IsDirty = false;
        vm.IsCollapsed = false;
        _playback.After.Stop();
        _playback.Before.Stop();
        _media = null;
        _sourceFingerprint = null;
        _markers.SetImported(null, _markers.ImportedAvailable);
        vm.MarkerSetSource = MarkerSetSource.Unknown;
        vm.MarkerBaselineId = null;
        vm.SourceFingerprint = null;
        vm.LastExport = null;
        vm.SaveStatus = "";
        vm.CommandStack.Clear();
        vm.IsDirty = false;
        VideoPlaceholder.Visibility = Visibility.Visible;
        MpvViewAfter.Visibility = Visibility.Collapsed;
        MpvViewBefore.Visibility = Visibility.Collapsed;
        NoFrameOverlay.Visibility = Visibility.Collapsed;
    }

    private bool BlockProjectSwitchForExport(MainViewModel vm)
    {
        if (_activeExportDialog is null) return false;
        _activeExportDialog.Focus();
        vm.Banner = ExportHandoff.ExportOpenBanner(_activeExportDialog.IsExportInFlight);
        return true;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is MainViewModel vm && !vm.IsBusy) vm.CommandStack.Undo();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is MainViewModel vm && !vm.IsBusy) vm.CommandStack.Redo();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { CanInteract: true } vm) vm.ZoomFactor = Math.Clamp(vm.ZoomFactor * 1.25, 0.25, 32.0);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { CanInteract: true } vm) vm.ZoomFactor = Math.Clamp(vm.ZoomFactor * 0.8, 0.25, 32.0);
    }

    private void OnFitTimeline(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { CanInteract: true } vm) vm.ZoomFactor = 1.0;
    }

    // Status-bar shortcuts with the same effect as View > Show thumbnails / Show waveform.
    private void OnShowThumbnailsFromStatus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowThumbnails = true;
    }

    private void OnShowWaveformFromStatus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowWaveform = true;
    }

    private void OnAddSplitAtPlayhead(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanEdit || vm.DurationUs <= 0) return;
        AddSplitAtPlayhead(vm);
    }

    private void AddSplitAtPlayhead(MainViewModel vm)
    {
        if (_playback.After.IsSeeking || _playback.After.IsLoading) return;
        var timeUs = _playback.After.PositionUs;
        var minDelta = PlaybackController.FrameDurationUs(vm);
        if (!vm.Markers.Any(m => Math.Abs(m.TimeUs - timeUs) < minDelta))
            vm.CommandStack.Execute(new AddSplitCommand(vm, timeUs, SplitSource.Manual));
    }

    private void OnConfirmAllRemaining(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is not MainViewModel vm || vm.IsBusy) return;
        var targets = vm.Markers.Where(m => !m.IsBookend && !m.Confirmed).ToList();
        if (targets.Count > 0) vm.CommandStack.Execute(new SetConfirmedCommand(targets, confirmed: true));
    }

    private void OnAutoConfirmHighConfidence(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is not MainViewModel vm || vm.IsBusy) return;
        var targets = vm.Markers
            .Where(m => !m.IsBookend && !m.Confirmed && m.Confidence == Confidence.High)
            .ToList();
        if (targets.Count > 0) vm.CommandStack.Execute(new SetConfirmedCommand(targets, confirmed: true));
    }

    private void OnJumpNextUnconfirmed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is not MainViewModel vm) return;
        var next = vm.NextUnconfirmedAfter(vm.PlayheadUs);
        if (next is not null) { vm.SelectMarker(next); _playback.SeekTo(vm, next.TimeUs); }
    }

    private void OnPreviousSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        long near = NearPlayheadUs(vm);
        var previous = vm.Markers.Select(m => vm.ToDisplayTime(m.TimeUs))
            .Where(t => t < vm.TimelinePositionUs - near).DefaultIfEmpty(0).Max();
        _playback.SeekTo(vm, vm.ToSourceTime(previous));
    }

    private void OnNextSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        long near = NearPlayheadUs(vm);
        var next = vm.Markers.Select(m => vm.ToDisplayTime(m.TimeUs))
            .Where(t => t > vm.TimelinePositionUs + near).DefaultIfEmpty(vm.TimelineDurationUs).Min();
        _playback.SeekTo(vm, vm.ToSourceTime(next));
    }

    private void OnStepFrameBack(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) _playback.StepFrame(vm, -1);
    }

    private void OnStepFrameForward(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) _playback.StepFrame(vm, +1);
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (_activeExportDialog is not null)
        {
            _activeExportDialog.Focus();
            return;
        }

        if (!vm.IsFileLoaded || vm.IsBusy) return;
        if (_ffmpeg is null)
        {
            vm.Banner = new BannerInfo(StatusKind.Danger,
                "FFmpeg is not available.",
                "See binaries/README.md for install instructions.",
                Array.Empty<BannerAction>());
            return;
        }


        var statusBarProgress = new Progress<ExportProgress>(p =>
        {
            if (p.Phase == ExportPhase.Cleanup) return;
            vm.StatusOverride = p.Message;
            vm.StatusKind = p.Phase == ExportPhase.Failed ? StatusKind.Danger : StatusKind.Info;
            vm.ProgressPercent = p.OverallPercent;
        });

        vm.Banner = null;

        _playback.ResetShuttle();
        _playback.After.Pause();
        _playback.Before.Pause();
        // Export needs the disk. A finished audio pass costs nothing and stays; the graph keeps
        // what a running one measured, and the pass starts over when the editor returns.
        _timelineMedia.DropUnfinishedAudioPass();
        _timelineMedia.CancelThumbnailLoad(vm, false);
        var dlg = new ExportView();
        _activeExportDialog = dlg;
        var exportPreferences = await UserPreferences.LoadAsync();
        dlg.Bind(vm, _media, _prefs.Current.ExportAcceleration, exportPreferences.ExportFormat, exportPreferences.DeleteSidecarAfterExport,
            vm.SourcePath is { } source && ExportHandoff.CanCommentSource(source) && exportPreferences.AddSourceExportComment
                ? AfterExportStep.SourceComment : AfterExportStep.RecordExport,
            exportPreferences.ExportFolder, exportPreferences.ExportFilenameTemplate);
        dlg.Closed += async (_, _) =>
        {
            if (dlg.DataContext is ExportDialogViewModel completed)
            { _prefs.Current = _prefs.Current with { ExportAcceleration = !completed.CanCheckHardware ? _prefs.Current.ExportAcceleration : completed.SelectedAcceleration.Id }; await SavePreferencesAsync(vm); }
        };
        dlg.AttachExportRunner(_ffmpeg, statusBarProgress);
        dlg.ChooseSourceCommentAsync = ReadSourceCommentChoiceAsync;
        dlg.AfterSuccessfulExportAsync = ct => RecordSuccessfulExportAsync(vm, dlg, ct);
        dlg.ExportFinished += (_, _) => HandleExportFinished(vm, dlg);
        dlg.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeExportDialog, dlg))
            {
                _activeExportDialog = null;
                ExportHost.Content = null;
                ExportHost.Visibility = Visibility.Collapsed;
                EditorWorkspace.Visibility = Visibility.Visible;
                vm.IsExportScreen = false;
                if (_reopenPreviewsOnReturn is { } reopen) { _reopenPreviewsOnReturn = null; _ = reopen(); }
                if (vm.ShowWaveform && vm.SourcePath is { } waveformPath && _media is not null) _timelineMedia.EnsureAudioPass(vm, waveformPath, _media);
                if (vm.ShowThumbnails && vm.SourcePath is { } thumbnailPath && _media is not null) _timelineMedia.StartThumbnailLoad(vm, thumbnailPath, _media);
            }
        };
        EditorWorkspace.Visibility = Visibility.Collapsed;
        vm.IsExportScreen = true;
        ExportHost.Content = dlg;
        ExportHost.Visibility = Visibility.Visible;
        dlg.Focus();
    }

    private void HandleExportFinished(MainViewModel vm, ExportView dlg)
    {
        var plan = dlg.AcceptedPlan;
        switch (dlg.Outcome)
        {
            case ExportDialogOutcome.Completed when plan is not null:
                vm.ClearStatus();
                var sidecarNote = dlg.SourceCommentFailed ? " Project sidecars were kept." : MaybeDeleteSidecar(plan.SourcePath, dlg.ShouldDeleteSidecarAfterSuccess);
                vm.Banner = new BannerInfo(dlg.SourceCommentFailed ? StatusKind.Warning : StatusKind.Success,
                    "Export complete.",
                    $"Wrote {Path.GetFileName(plan.OutputPath)}. {dlg.SourceCommentNote}{sidecarNote}",
                    new[]
                    {
                        new BannerAction("Show in Explorer", () => RevealInExplorer(plan.OutputPath)),
                    });
                break;

            case ExportDialogOutcome.Failed when plan is not null:
                vm.ClearStatus();
                vm.Banner = new BannerInfo(StatusKind.Danger,
                    "Export failed.",
                    "See the export screen for details.",
                    Array.Empty<BannerAction>());
                break;

            case ExportDialogOutcome.Cancelled:
                vm.ClearStatus();
                break;
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// If <paramref name="shouldDelete"/> is true, delete the sidecar next to
    /// the source. Failures are non-fatal (export already succeeded) - returns
    /// a short trailing fragment for the success banner so the user can see
    /// whether the deletion actually happened. Empty string if nothing to do.
    /// </summary>
    private string MaybeDeleteSidecar(string sourcePath, bool shouldDelete)
        => ExportHandoff.DeleteSidecars(_store ??= new ProjectStore(), sourcePath, _sourceFingerprint, shouldDelete);

    private void OnBannerActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is BannerAction a) a.Invoke();
    }

    private void OnDismissBanner(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Banner = null;
    }

    private void OnToggleExcluded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        var seg = vm.SelectedSegment;
        if (seg is null) return;
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, seg));
    }

    private void OnSelectRedMarkers(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy) return;
        vm.SelectMarkersWhere(m => m.Confidence == Confidence.Low);
        int count = vm.SelectedMarkerCount;
        Notice(vm, StatusKind.Info, count == 0 ? "There are no red (uncertain) markers."
            : count == 1 ? "1 red (uncertain) marker selected."
            : $"{count} red (uncertain) markers selected. Del deletes them; C confirms them. Esc clears the selection.");
    }

    /// <summary>Ctrl+Alt+A: selects the markers in the part of the timeline currently on screen.</summary>
    private void OnSelectVisibleMarkers(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy || vm.IsCollapsed) return;
        var (from, to) = VisibleTimelineRange(vm);
        vm.SelectMarkersWhere(m => m.TimeUs >= from && m.TimeUs <= to);
        int count = vm.SelectedMarkerCount;
        Notice(vm, StatusKind.Info, count == 0 ? "There are no markers in the visible part of the timeline."
            : count == 1 ? "1 visible marker selected."
            : $"{count} visible markers selected ({RefineCoordinator.FormatTimeMs(from)} to {RefineCoordinator.FormatTimeMs(to)}). Esc clears the selection.");
    }

    /// <summary>Source-time range of the timeline currently scrolled into view.</summary>
    private (long From, long To) VisibleTimelineRange(MainViewModel vm)
    {
        double width = Timeline.ActualWidth;
        if (width <= 0 || vm.TimelineDurationUs <= 0) return (0, vm.DurationUs);
        double left = Math.Clamp(TimelineScroll.HorizontalOffset, 0, width);
        double right = Math.Clamp(left + TimelineScroll.ViewportWidth, left, width);
        long Time(double x) => vm.ToSourceTime((long)Math.Round(x / width * vm.TimelineDurationUs));
        return (Time(left), Time(right));
    }

    private void OnRenameSelectedSegment(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy) return;
        // A selected segment, or the segment that starts at the selected marker.
        var segment = vm.SelectedSegment
            ?? (vm.SelectedMarker is { } m ? vm.Segments.FirstOrDefault(s => s.StartUs == m.TimeUs) : null);
        if (segment is null) { Notice(vm, StatusKind.Info, "Select a segment (or the marker where it starts) to rename it."); return; }
        var dialog = new RenameSegmentDialog(segment.Label ?? "") { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SegmentName != segment.Label)
            vm.CommandStack.Execute(new RenameSegmentCommand(vm, segment, dialog.SegmentName));
    }

    private void OnResetNumberedSegmentNames(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy) return;
        var command = new ResetNumberedNamesCommand(vm);
        if (command.Count == 0)
        {
            Notice(vm, StatusKind.Info, "No segment has a typed or imported name like \"" + SectionNames.Format(vm.SectionNameTemplate, 3)
                + "\". Unnamed segments already number themselves.");
            return;
        }
        vm.CommandStack.Execute(command);
        vm.StatusOverride = command.Count == 1 ? "1 segment name reset to automatic numbering. Undo restores it."
            : $"{command.Count} segment names reset to automatic numbering. Undo restores them.";
    }

    private void OnShowShortcuts(object sender, RoutedEventArgs e) => new ShortcutsDialog { Owner = this }.ShowDialog();

    private void OnJumpPreviousUnconfirmed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm) return;
        var previous = vm.Markers.Where(m => !m.IsBookend && !m.Confirmed && m.TimeUs < vm.PlayheadUs)
            .OrderByDescending(m => m.TimeUs).FirstOrDefault();
        if (previous is null) { Notice(vm, StatusKind.Info, "No unconfirmed split before the playhead."); return; }
        vm.SelectMarker(previous);
        _playback.SeekTo(vm, previous.TimeUs);
    }

    private void OnPreviousMarker(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) JumpToMarker(vm, -1); }
    private void OnNextMarker(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) JumpToMarker(vm, 1); }

    // After a seek the playhead shows the frame the player landed on, up to a frame away from a
    // marker that is not on a frame time (imported chapters). That marker is the one we are at.
    private long NearPlayheadUs(MainViewModel vm) => Math.Max(0, PlaybackController.FrameDurationUs(vm) - 1);

    /// <summary>Alt+Left / Alt+Right: select and go to the previous or next marker.</summary>
    private void JumpToMarker(MainViewModel vm, int direction)
    {
        if (!vm.CanEdit || vm.IsCollapsed) return;
        long near = NearPlayheadUs(vm);
        var target = direction > 0
            ? vm.Markers.Where(m => !m.IsBookend && m.TimeUs > vm.PlayheadUs + near).OrderBy(m => m.TimeUs).FirstOrDefault()
            : vm.Markers.Where(m => !m.IsBookend && m.TimeUs < vm.PlayheadUs - near).OrderByDescending(m => m.TimeUs).FirstOrDefault();
        if (target is null) { Notice(vm, StatusKind.Info, direction > 0 ? "No marker after the playhead." : "No marker before the playhead."); return; }
        vm.SelectMarker(target);
        _playback.SeekTo(vm, target.TimeUs);
    }

    private void OnSelectAllMarkers(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy) return;
        vm.SelectAllMarkers();
        int count = vm.SelectedMarkerCount;
        Notice(vm, StatusKind.Info, count == 0 ? "There are no markers to select."
            : count == 1 ? "1 marker selected."
            : $"{count} markers selected. R refines, C confirms, Del deletes them. Esc clears the selection.");
    }

    private void OnDeleteSelectedSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        if (vm.SelectionKind == SelectionKind.Markers)
        {
            var selected = vm.SelectedMarkers.ToList();
            vm.ClearSelection();
            vm.CommandStack.Execute(new DeleteSplitsCommand(vm, selected));
            Notice(vm, StatusKind.Info, $"Deleted {selected.Count} markers. Ctrl+Z restores them.");
            return;
        }
        var marker = vm.SelectedMarker;
        if (marker is null || marker.IsBookend) return;
        vm.CommandStack.Execute(new DeleteSplitCommand(vm, marker));
        vm.ClearSelection();
    }

    private void OnConfirmSelectedSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true }) return;
        if (DataContext is MainViewModel { SelectionKind: SelectionKind.Markers, IsBusy: false } many)
        {
            var open = many.SelectedMarkers.Where(m => !m.Confirmed).ToList();
            if (open.Count == 0) { Notice(many, StatusKind.Info, "All selected markers are already confirmed."); return; }
            many.CommandStack.Execute(new SetConfirmedCommand(open, confirmed: true));
            Notice(many, StatusKind.Info, $"Confirmed {open.Count} markers.");
            return;
        }
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        if (vm.IsBusy) return;
        if (vm.SelectedMarker.Confirmed && !ConfirmUnconfirm(vm.SelectedMarker)) return;
        vm.CommandStack.Execute(new ToggleConfirmedCommand(vm.SelectedMarker));
    }

    private bool ConfirmUnconfirm(Split split)
        => MessageBox.Show(this,
            "This split is confirmed. Unconfirm it?",
            "Unconfirm split",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private bool ConfirmRefineConfirmed(Split split)
        => MessageBox.Show(this,
            "You've already confirmed this split. Refine it anyway?",
            "Refine confirmed split",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void OnNudgeSelectedMinus(object sender, RoutedEventArgs e)
        => NudgeSelected(-1);
    private void OnNudgeSelectedPlus(object sender, RoutedEventArgs e)
        => NudgeSelected(+1);

    private async void OnMoveSelectedToPlayhead(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm || vm.IsBusy) return;
        if (vm.SelectionKind == SelectionKind.Markers)
        { Notice(vm, StatusKind.Warning, "Select a single marker to move it to the playhead."); return; }
        if (vm.SelectedMarker is not { } split) return;
        // An arrow key or click may still be seeking the preview: wait briefly for the frame
        // the marker will land on instead of silently ignoring the key.
        for (int i = 0; i < 75 && (_playback.After.IsSeeking || _playback.After.IsLoading); i++) await Task.Delay(20);
        if (_playback.After.IsSeeking || _playback.After.IsLoading)
        { Notice(vm, StatusKind.Warning, "The preview is still moving to the playhead. Press M again in a moment."); return; }
        if (!ReferenceEquals(vm.SelectedMarker, split) || vm.IsBusy) return;
        if (split.Confirmed && !ConfirmUnconfirm(split)) return;
        if (split.Confirmed)
            vm.CommandStack.Execute(new ToggleConfirmedCommand(split));

        var sorted = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var idx = sorted.IndexOf(split);
        long minUs = idx > 0 ? sorted[idx - 1].TimeUs + 1 : 0;
        long maxUs = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs - 1 : vm.DurationUs;
        var candidate = _playback.After.PositionUs;
        if (candidate < minUs || candidate > maxUs)
        { Notice(vm, StatusKind.Warning, "The playhead is past a neighboring marker; a marker cannot move across another one."); return; }
        if (candidate == split.TimeUs)
        { Notice(vm, StatusKind.Info, "The marker is already at the playhead."); return; }
        long from = split.TimeUs;
        vm.CommandStack.Execute(new MoveSplitCommand(vm, split, from, candidate));
        Notice(vm, StatusKind.Success, $"Moved marker from {RefineCoordinator.FormatTimeMs(from)} to {RefineCoordinator.FormatTimeMs(candidate)}. Ctrl+Z undoes it.");
    }

    private static void Notice(MainViewModel vm, StatusKind kind, string message)
    {
        vm.StatusKind = kind;
        vm.StatusOverride = message;
    }

    private void NudgeSelected(int directionFrames)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        OnTimelineNudgeRequested(this, (vm.SelectedMarker, directionFrames));
    }

    private async void OnTimelineRefineRequested(object? sender, Split split)
    {
        if (DataContext is not MainViewModel vm || vm.SourcePath is null) return;
        await _refines.RunAsync(vm, new[] { split }, ConfirmRefineConfirmed);
    }

    private void OnTimelineConfirmedMarkerUnlockRequested(object? sender, Split split)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy) return;
        if (!ConfirmUnconfirm(split)) return;
        vm.CommandStack.Execute(new ToggleConfirmedCommand(split));
        vm.StatusKind = StatusKind.Warning;
        vm.StatusOverride = "Split unconfirmed. Drag again to move it.";
    }

    private RefineService? _refine;

    private async void OnRefineAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        await _refines.RunAsync(vm, vm.Markers.ToList(), ConfirmRefineConfirmed);
    }

    private async void OnRefineSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = vm.SelectedMarkers.Where(m => !m.IsBookend).ToList();
        if (selected.Count == 0) return;
        await _refines.RunAsync(vm, selected, ConfirmRefineConfirmed);
    }

    private void OnTimelineNudgeRequested(object? sender, (Split split, int direction) e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        if (e.split.Confirmed && !ConfirmUnconfirm(e.split)) return;
        if (e.split.Confirmed)
            vm.CommandStack.Execute(new ToggleConfirmedCommand(e.split));
        var frameUs = PlaybackController.FrameDurationUs(vm) * e.direction;
        var target = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var idx = target.IndexOf(e.split);
        long minUs = idx > 0 ? target[idx - 1].TimeUs + 1 : 0;
        long maxUs = idx < target.Count - 1 ? target[idx + 1].TimeUs - 1 : vm.DurationUs;
        var candidate = FrameSnap.SnapWithin(e.split.TimeUs + frameUs, vm.FrameRate, vm.FrameStartPhaseUs, minUs, maxUs) ?? e.split.TimeUs;
        if (candidate != e.split.TimeUs)
            vm.CommandStack.Execute(new MoveSplitCommand(vm, e.split, e.split.TimeUs, candidate));
    }

    private void OnTimelineSeekRequested(object? sender, (long TimeUs, bool StartPlaying, bool Exact, long? StopAtUs) e)
    {
        if (DataContext is not MainViewModel vm) return;
        _playback.SeekTo(vm, e.TimeUs, e.Exact);
        if (e.StartPlaying)
        {
            _playback.PlayUntilUs = e.StopAtUs;
            _playback.After.Play();
            _playback.Before.Play();
        }
    }
}
