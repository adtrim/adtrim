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

public partial class MainWindow : Window
{
    // Dual-pane preview. `_previewAfter` is the canonical playhead (Pane B,
    // visible on the right, muted). `_previewBefore` follows at playhead − 1
    // frame (Pane A, visible on the left, has audio). Only `_previewAfter`
    // emits PositionUs back into the VM's PlayheadUs; Pane A is a slave that
    // gets seeked alongside Pane B whenever the playhead moves while paused,
    // and is re-synced at every play-start so they begin playback exactly
    // one frame apart. A few frames of drift during free playback is
    // acceptable per the design discussion.
    private readonly MpvPreviewViewModel _previewAfter = new();
    private readonly MpvPreviewViewModel _previewBefore = new();
    private FfmpegRunner? _ffmpeg;
    private MediaProbeService? _probe;
    private ChapterImportService? _chapters;
    private ProjectStore? _store;
    private AudioLevelService? _audioLevels;
    private AudioLevelPass? _audioPass;
    private ThumbnailService? _thumbnails;
    private ThumbnailLoader? _thumbnailLoader;
    private CancellationTokenSource? _refineCts;
    private CancellationTokenSource? _commercialAnalysisCts;
    private readonly MarkerSuggestionStore _suggestionStore = new();
    private const long UncertainMarkerSpacingUs = 5_000_000;
    private MarkerSetState? _importedMarkerSet;
    private MediaInfo? _media;
    private SourceFingerprint? _sourceFingerprint;
    private readonly ProjectSession _session = new();
    private bool _hydrating;
    private bool _allowClose;
    private bool _switching;
    private bool _closeWhenSwitchCompletes;
    private bool _saveInProgress;
    private long? _playUntilUs;

    private ExportView? _activeExportDialog;
    private bool _closingExport;


    private readonly DispatcherTimer _autosaveTimer;

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
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    public MainWindow() : this(null) { }
    public MainWindow(string? startupPath)
    {
        _startupPath = startupPath;
        _openingPath = startupPath;
        InitializeComponent();
        // Runtime starts empty - the WPF designer uses the design-time
        // `<vm:MainViewModel/>` in XAML for live-preview only.
        DataContext = new MainViewModel();
        Timeline.ThumbnailsNeeded += times => _thumbnailLoader?.Request(times);

        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _autosaveTimer.Tick += OnAutosaveTick;

        WireViewModelEvents((MainViewModel)DataContext);

        Loaded += OnLoaded;
        InitializeUpdates();
        Closing += OnWindowClosing;
        Timeline.ReviewJoinRequested += (_, outputUs) =>
        {
            if (DataContext is not MainViewModel vm || !vm.IsCollapsed || vm.IsBusy) return;
            _playUntilUs = null;
            SeekTo(vm, vm.KeptTimeline.ToSource(Math.Max(0, outputUs - 2_000_000)));
            _previewAfter.Play();
        };
        _previewAfter.PlaybackFailed += (_, message) => Dispatcher.BeginInvoke(new Action(() =>
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
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
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
                        EnsureAudioPass(vm, path, _media);
                }
                else if (!vm.IsAnalyzingCommercials)
                {
                    // Analysis still needs a running pass; the hidden graph just stops drawing.
                    ReleaseAudioPass(vm);
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.ShowThumbnails))
            {
                if (vm.ShowThumbnails)
                {
                    if (vm.SourcePath is { } path && _media is not null)
                        StartThumbnailLoad(vm, path, _media);
                }
                else
                {
                    CancelThumbnailLoad(vm, clearThumbnails: true);
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
    private void UpdateNoFrameOverlay(MainViewModel vm)
    {
        if (vm.IsCollapsed) return;
        var frameUs = FrameDurationUs(vm);
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
        _previewAfter.PropertyChanged += OnPreviewPropertyChanged;
        _previewBefore.PropertyChanged += OnPreviewPropertyChanged;

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
            _probe = new MediaProbeService(_ffmpeg);
            _chapters = new ChapterImportService(_ffmpeg);
            _store = new ProjectStore();
            _thumbnails = new ThumbnailService(_ffmpeg);
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
            if (ReferenceEquals(sender, _previewBefore))
            {
                TimestampBeforePos.Text = FormatTimestampMs(_previewBefore.PositionUs);
                TimestampBeforeFrame.Text = FormatFrameIndex(_previewBefore.PositionUs, vm);
            }
            else if (ReferenceEquals(sender, _previewAfter))
            {
                TimestampAfterPos.Text = FormatTimestampMs(_previewAfter.PositionUs);
                TimestampAfterFrame.Text = FormatFrameIndex(_previewAfter.PositionUs, vm);
            }

            if (_previewAfter.IsLoading) return;
            // Only Pane B drives PlayheadUs.
            if (!ReferenceEquals(sender, _previewAfter))
            {
                MaybeResyncPanes(vm);
                return;
            }

            if (vm.IsCollapsed)
            {
                vm.PlayheadUs = vm.KeptTimeline.ToSource(_previewAfter.PositionUs);
                return;
            }
            vm.PlayheadUs = _previewAfter.PositionUs;
            MaybeResyncPanes(vm);

            if (_playUntilUs is { } stopUs && _previewAfter.PositionUs >= stopUs)
            {
                _previewAfter.Pause();
                _previewBefore.Pause();
                _playUntilUs = null;
            }
        }
        else if (e.PropertyName == nameof(MpvPreviewViewModel.IsSeeking))
        {
            if (DataContext is MainViewModel currentVm) MaybeResyncPanes(currentVm);
        }
        else if (e.PropertyName == nameof(MpvPreviewViewModel.IsPlaying))
        {
            // Swap the play/pause icon visibility.
            PauseGlyph.Visibility = _previewAfter.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = _previewAfter.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
            if (DataContext is MainViewModel currentVm) MaybeResyncPanes(currentVm);
        }
    }

    // ----- Pane drift / timestamp helpers ------------------------------

    // The advertised rate can differ from decoded frame spacing. Let mpv
    // find the predecessor instead of repeatedly seeking half a real frame back.
    private CancellationTokenSource? _frameRecovery;
    private (string? Path, long Time)? _failedFrameRecovery;

    private void MaybeResyncPanes(MainViewModel vm)
    {
        if (vm.IsCollapsed || _previewAfter.IsLoading || _previewBefore.IsLoading
            || _previewAfter.IsSeeking || _previewBefore.IsSeeking) return;
        if (_previewAfter.IsPlaying || _previewBefore.IsPlaying) return;
        var target = _previewAfter.PositionUs;
        if (target <= vm.FrameStartPhaseUs) return;
        if (_previewBefore.PreviousFrameTargetUs == target)
        {
            if (_previewBefore.PositionUs < target - 2)
            {
                UpdateNoFrameOverlay(vm);
                return;
            }
            if (_previewBefore.PositionUs >= target - 2 && _frameRecovery is null)
            {
                if (_failedFrameRecovery == (vm.SourcePath, target))
                {
                    NoFrameOverlay.Visibility = Visibility.Visible;
                    NoFrameMessage.Text = "Could not load the preceding frame";
                    MpvViewBefore.Visibility = Visibility.Collapsed;
                    vm.StatusOverride = "Could not load the preceding frame. Try seeking again.";
                }
                else RecoverPreviousFrameAsync(vm, target);
            }
            return;
        }
        _frameRecovery?.Cancel();
        _previewBefore.SeekPreviousFrame(target);
    }

    private async void RecoverPreviousFrameAsync(MainViewModel vm, long target)
    {
        if (vm.SourcePath is not { } path || _ffmpeg is null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _frameRecovery = cancellation;
        bool StillCurrent() => !_allowClose && vm.SourcePath == path && !vm.IsCollapsed
            && !_previewAfter.IsPlaying && !_previewAfter.IsSeeking && _previewAfter.PositionUs == target;
        try
        {
            var previous = await new RefineService(_ffmpeg).FindPreviousFrameAsync(path, target, cancellation.Token);
            if (!StillCurrent()) return;
            if (previous is { } frame)
            {
                _failedFrameRecovery = (path, target);
                _previewBefore.SeekPreviousFrame(target, Math.Max(0, frame - 1));
            }
            else
            {
                _failedFrameRecovery = (path, target);
                NoFrameOverlay.Visibility = Visibility.Visible;
                NoFrameMessage.Text = "Could not load the preceding frame";
                MpvViewBefore.Visibility = Visibility.Collapsed;
                vm.StatusOverride = "No preceding frame found within 60 seconds of this position.";
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            if (StillCurrent())
            {
                _failedFrameRecovery = (path, target);
                NoFrameOverlay.Visibility = Visibility.Visible;
                NoFrameMessage.Text = "Could not load the preceding frame";
                MpvViewBefore.Visibility = Visibility.Collapsed;
                vm.StatusOverride = "Could not load the preceding frame. Try seeking again.";
            }
        }
        finally
        {
            if (ReferenceEquals(_frameRecovery, cancellation)) _frameRecovery = null;
            if (!_allowClose && ReferenceEquals(DataContext, vm)) MaybeResyncPanes(vm);
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
        var aspect = _previewAfter.VideoAspect > 0
            ? _previewAfter.VideoAspect
            : _previewBefore.VideoAspect;
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

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => PlayPauseBoth();

    /// <summary>
    /// Toggle audio on Pane A ("before"). Pane B stays permanently muted -
    /// unmuting both produces comb-filter artifacts because the two mpv
    /// instances play the same file one frame apart. Icon swap is imperative
    /// here because MpvPreviewViewModel.IsMuted doesn't raise PropertyChanged
    /// (it's not a bindable observable today).
    /// </summary>
    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        var active = DataContext is MainViewModel { IsCollapsed: true } ? _previewAfter : _previewBefore;
        var newMuted = !active.IsMuted;
        active.SetMuted(newMuted);
        SpeakerOnGlyph.Visibility  = newMuted ? Visibility.Collapsed : Visibility.Visible;
        SpeakerOffGlyph.Visibility = newMuted ? Visibility.Visible   : Visibility.Collapsed;
    }

    /// <summary>
    /// Toggle play/pause for both panes. On play-start, re-seek Pane A to
    /// exactly Pane B's position − 1 frame so playback begins synchronized;
    /// a few frames of drift during the playback itself is acceptable per
    /// the design. On pause, just pause both - the seek-while-paused path
    /// (<see cref="SeekTo"/>) keeps them aligned during scrubbing.
    /// </summary>
    private void PlayPauseBoth()
    {
        if (DataContext is not MainViewModel vm) return;
        ResetShuttle();
        if (vm.IsBusy && _commercialAnalysisCts is null) return;
        if (vm.IsCollapsed)
        {
            if (vm.KeptTimeline.DurationUs > 0 && !_previewAfter.IsLoading) _previewAfter.PlayPause();
            return;
        }
        if (_previewAfter.IsPlaying)
        {
            _previewAfter.Pause();
            _previewBefore.Pause();
            _playUntilUs = null;
        }
        else
        {
            // Re-sync before playback starts: Pane B at playhead, Pane A at
            // playhead − 1 frame. Both exact so we land on real frames, not
            // the nearest preceding keyframe.
            var frameUs = FrameDurationUs(vm);
            _previewAfter.SeekUsExact(vm.PlayheadUs);
            _previewBefore.SeekUsExact(Math.Max(0, vm.PlayheadUs - frameUs));
            _previewAfter.Play();
            _previewBefore.Play();
            _playUntilUs = null;
        }
    }

    private void OnToggleFrameAccurateSeek(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item) return;
        _previewAfter.SetFastSeek(!item.IsChecked);
        _previewBefore.SetFastSeek(!item.IsChecked);
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
                _previewAfter.Wid = MpvViewAfter.Hwnd;
                _previewAfter.Initialize();
                // Pane B is muted; we hear audio from Pane A (the "before"
                // pane). Two mpv instances unmuted on the same file 1 frame
                // apart would comb-filter horribly.
                _previewAfter.SetMuted(true);
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
                _previewBefore.Wid = MpvViewBefore.Hwnd;
                _previewBefore.Initialize();
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
        if (!File.Exists(path)) { _openingPath = null; return; }
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
        _switching = true;
        bool flushed;
        try { flushed = await FlushBeforeLeavingAsync(vm); }
        finally { _switching = false; }
        if (!flushed) return;

        if (_ffmpeg is null || _probe is null || _chapters is null || _store is null)
        {
            try
            {
                _ffmpeg ??= new FfmpegRunner();
                _probe ??= new MediaProbeService(_ffmpeg);
                _chapters ??= new ChapterImportService(_ffmpeg);
                _store ??= new ProjectStore();
                _thumbnails ??= new ThumbnailService(_ffmpeg);
            }
            catch (FileNotFoundException ex)
            {
                MessageBox.Show(this, ex.Message, "FFmpeg not found",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (vm.IsCollapsed) SetCollapsed(vm, false);
        var latestPreferences = await UserPreferences.LoadAsync();
        _preferences = _preferences with { NewFileMarkerSource = latestPreferences.NewFileMarkerSource };
        _savedPreferences = _savedPreferences with { NewFileMarkerSource = latestPreferences.NewFileMarkerSource };
        RefreshDefaultMarkerSourceMenu();
        _switching = true;
        _hydrating = true;
        vm.IsBusy = true;
        vm.BusyOperation = "Opening";
        ReleaseAudioPass(vm);
        CancelThumbnailLoad(vm, clearThumbnails: true);
        _refineCts?.Cancel();
        _autosaveTimer.Stop();

        var previousPath = vm.SourcePath;
        var previousPosition = vm.PlayheadUs;
        try
        {
            _previewAfter.Open(path);
            _previewBefore.Open(path);
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            MpvViewAfter.Visibility = Visibility.Visible;
            MpvViewBefore.Visibility = Visibility.Visible;
            vm.StatusKind = StatusKind.Info;
            vm.StatusOverride = "Probing media…";
            var probed = await _probe!.ProbeAsync(path);
            var fingerprint = ProjectStore.FingerprintOf(path, probed.DurationUs);
            var loadResult = await Task.Run(() => _store!.Load(path, probed));
            IReadOnlyList<ChapterBoundary> imported = Array.Empty<ChapterBoundary>();
            _importedMarkerSet = null;
            try
            {
                imported = await _chapters!.ImportAsync(path, probed.DurationUs, includeStart: true);
                _importedMarkerSet = StateFromChapters(imported, MarkerSetSource.Imported);
            }
            catch (Exception ex)
            {
                vm.Banner = new BannerInfo(StatusKind.Warning, "Imported markers are unavailable.",
                    ex.Message, Array.Empty<BannerAction>());
            }
            // The recording being opened (vm.SourcePath still names the previous one here).
            vm.HasAdTrimAnalysis = _suggestionStore.Load(_suggestionStore.Key(path, fingerprint), probed.DurationUs) is not null;
            _importedMarkersAvailable = _importedMarkerSet is not null
                && (_importedMarkerSet.Splits.Count > 0 || _importedMarkerSet.StartChapterTitle is not null);
            UseOriginalImportedMarkersMenuItem.ToolTip = _importedMarkersAvailable
                ? null : _importedMarkerSet is null ? "Imported markers could not be read from this recording."
                    : "This recording has no imported chapter markers.";
            RefreshImportedMarkersItem(vm);
            _media = probed;
            _sourceFingerprint = fingerprint;
            vm.SourceFingerprint = fingerprint;
            _previewAfter.SelectAudioStream(_media.PrimaryAudioIndex);
            _previewBefore.SelectAudioStream(_media.PrimaryAudioIndex);
            vm.SourcePath = path;
            vm.FileName = Path.GetFileName(path);
            vm.DurationUs = _media.DurationUs;
            vm.FrameRate = _media.FrameRate;
            vm.FrameStartPhaseUs = _media.VideoStartTimeUs;
            vm.ZoomFactor = 1.0;   // start fully zoomed out - whole video visible
            vm.MediaInfoLine =
                $"{_media.FrameRate.AsDouble:0.##} fps · {_media.Width}×{_media.Height} · {_media.VideoCodec}";
            // The filmstrip goes first: it is the most visible and costs a few seeks, and the
            // waveform's full read yields to it. Starting here keeps it ahead of any analysis.
            if (vm.ShowThumbnails) StartThumbnailLoad(vm, path, _media);

            // Try sidecar hydration first.
            if (loadResult.Project is { } proj && loadResult.Status is SidecarLoadStatus.Loaded or SidecarLoadStatus.LoadedWithMtimeWarning)
            {
                HydrateFromSidecar(vm, proj);
                if (loadResult.Message is not null)
                {
                    vm.StatusKind = StatusKind.Warning;
                    vm.StatusOverride = loadResult.Message ?? "Source modified time changed; verify project state.";
                }
            }
            else
            {
                if (loadResult.Status is SidecarLoadStatus.Corrupt or SidecarLoadStatus.FingerprintMismatch or SidecarLoadStatus.UnsupportedVersion)
                {
                    vm.Banner = new BannerInfo(StatusKind.Warning,
                        "Project sidecar was not loaded.",
                        loadResult.Message ?? "Falling back to embedded chapters.",
                        Array.Empty<BannerAction>());
                }
                vm.StatusOverride = "Importing chapters…";
                if (_preferences.NewFileMarkerSource == NewFileMarkerSource.AdTrim)
                {
                    HydrateBlank(vm, MarkerSetSource.AdTrim);
                    vm.StatusOverride = "Analyzing ad breaks…";
                    var initialKey = _suggestionStore.Key(path, fingerprint);
                    var cached = _suggestionStore.Load(initialKey, probed.DurationUs);
                    if (cached is not null) ApplyMarkerSet(vm, cached, "Load AdTrim suggested markers", confirm: false);
                    else
                    {
                        var detected = await AnalyzeCommercialsAsync(vm, initialKey, cancellationToken: CancellationToken.None);
                        if (detected is not null) ApplyMarkerSet(vm, detected, "Load AdTrim suggested markers", confirm: false);
                        else
                        {
                            HydrateBlank(vm, MarkerSetSource.Blank);
                            vm.StatusOverride = "Analysis did not finish. All video remains included.";
                        }
                    }
                }
                else if (_preferences.NewFileMarkerSource == NewFileMarkerSource.None)
                    HydrateBlank(vm, MarkerSetSource.Blank);
                else
                    HydrateFromChapters(vm, _media, imported);
            }

            vm.CommandStack.Clear();
            vm.IsDirty = false;
            vm.SaveStatus = loadResult.Project is not null ? "Saved" : "No saved edits";
            if (_preferences.Positions?.TryGetValue(path, out var position) == true)
            {
                position = Math.Clamp(position, 0, vm.DurationUs);
                _previewAfter.Open(path, position);
                _previewBefore.Open(path, Math.Max(0, position - FrameDurationUs(vm)));
                vm.PlayheadUs = position;
            }
            else vm.PlayheadUs = 0;
            // Ensure Pane A starts 1 frame behind Pane B on initial load
            // (PlayheadUs = 0 means Pane A wants frame −1 → no-frame overlay).
            UpdateNoFrameOverlay(vm);

            if (vm.ShowWaveform)
                EnsureAudioPass(vm, path, _media);
            else if (!vm.IsAnalyzingCommercials)
                ReleaseAudioPass(vm);

            if (!vm.ShowThumbnails)
                CancelThumbnailLoad(vm, clearThumbnails: true);

            // Clear our transient open-time status only if it's still showing -
            // a sidecar warning or other update set between the probe and now
            // takes precedence and must not be wiped.
            if (vm.StatusOverride is "Probing media…" or "Importing chapters…")
                vm.StatusOverride = null;
        }
        catch (Exception ex)
        {
            if (previousPath is not null && _media is not null)
            {
                _previewAfter.Open(previousPath, previousPosition);
                _previewBefore.Open(previousPath, Math.Max(0, previousPosition - FrameDurationUs(vm)));
                _previewAfter.SelectAudioStream(_media.PrimaryAudioIndex);
                _previewBefore.SelectAudioStream(_media.PrimaryAudioIndex);
            }
            else { _previewAfter.Stop(); _previewBefore.Stop(); }
            MessageBox.Show(this, ex.Message, "Failed to open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _hydrating = false;
            _switching = false;
            vm.IsBusy = false;
            vm.BusyOperation = null;
            if (_closeWhenSwitchCompletes)
            {
                _closeWhenSwitchCompletes = false;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    /// <summary>Stops the recording's audio pass, if running, and clears the timeline graph.</summary>
    private void ReleaseAudioPass(MainViewModel vm)
    {
        _audioPass?.Cancel();
        _audioPass = null;
        vm.AudioLoudness = null;
    }

    /// <summary>
    /// The recording's shared audio pass. The timeline graph fills from it and ad-break analysis
    /// joins it, so the audio is read once. Reuses a running or finished pass for the same file.
    /// </summary>
    private AudioLevelPass EnsureAudioPass(MainViewModel vm, string path, MediaInfo media)
    {
        if (_audioPass is { } current && current.SourcePath == path
            && !current.Completion.IsCanceled && !current.Completion.IsFaulted) return current;
        ReleaseAudioPass(vm);
        _audioLevels ??= new AudioLevelService(_ffmpeg!, AudioLevelService.DefaultCacheRoot);
        AudioLevelPass? pass = null;
        pass = _audioLevels.Start(path, media, WaitForFullReadTurnAsync, partial => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_audioPass, pass)) vm.AudioLoudness = partial;
        }));
        _audioPass = pass;
        _ = ShowAudioLevelsAsync(vm, pass);
        return pass;
    }

    private async Task ShowAudioLevelsAsync(MainViewModel vm, AudioLevelPass pass)
    {
        try
        {
            var levels = await pass.Completion;
            if (ReferenceEquals(_audioPass, pass)) vm.AudioLoudness = levels.Loudness;
        }
        catch (OperationCanceledException) { /* superseded, closed or paused for export */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[audio levels] measurement failed: {ex.Message}");
            if (ReferenceEquals(_audioPass, pass) && vm.ShowWaveform)
            {
                vm.StatusKind = StatusKind.Warning;
                vm.StatusOverride = "Waveform failed to load.";
            }
        }
    }

    // Recordings usually sit on a spinning disk, where audio pass reads would
    // otherwise compete with every scrub seek.
    private async ValueTask WaitForPreviewIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            bool inFlight = _previewAfter.IsSeeking || _previewAfter.IsLoading
                || _previewBefore.IsSeeking || _previewBefore.IsLoading;
            var sinceSeek = Environment.TickCount64 - MpvPreviewViewModel.LastSeekActivityMs;
            if (!AudioLevelService.ShouldYieldToSeeks(sinceSeek, inFlight)) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // Full-file reads (the waveform pass, analysis picture passes) wait for scrubbing and for
    // filmstrip tiles: tiles are a few seeks and far more visible, so they go first.
    private async ValueTask WaitForFullReadTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            await WaitForPreviewIdleAsync(ct).ConfigureAwait(false);
            if (_thumbnailLoader is not { IsBusy: true }) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private void CancelThumbnailLoad(MainViewModel vm, bool clearThumbnails)
    {
        _thumbnailLoader?.Cancel();
        _thumbnailLoader = null;
        if (clearThumbnails) vm.ThumbnailTiles = ThumbnailStrip.Empty;
    }

    /// <summary>Shows the recording's cached tiles at once; the timeline then asks for the
    /// on-screen tiles it lacks (<see cref="Controls.TimelineView.ThumbnailsNeeded"/>).</summary>
    private void StartThumbnailLoad(MainViewModel vm, string path, MediaInfo media)
    {
        CancelThumbnailLoad(vm, clearThumbnails: true);
        if (_ffmpeg is null) return;
        _thumbnails ??= new ThumbnailService(_ffmpeg);
        var svc = _thumbnails;
        var duration = media.DurationUs;
        var cached = svc.LoadCached(path, duration);
        ThumbnailLoader? loader = null;
        loader = new ThumbnailLoader(cached.Times, time => ThumbnailService.Snap(time, duration),
            (times, ct) => svc.ExtractAsync(path, duration, times, ct), WaitForPreviewIdleAsync,
            tiles => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_thumbnailLoader, loader)) vm.ThumbnailTiles = vm.ThumbnailTiles.With(tiles);
            }));
        _thumbnailLoader = loader;
        vm.ThumbnailTiles = cached;
        Timeline.RefreshThumbnails();
    }

    private static void HydrateFromSidecar(MainViewModel vm, AdTrimProject proj)
    {
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();
        vm.MarkerSetSource = proj.MarkerSetSource;
        vm.MarkerBaselineId = proj.MarkerBaselineId?.StartsWith("v2:", StringComparison.Ordinal) == true
            ? proj.MarkerBaselineId : null;

        // Add bookends + persisted internal splits.
        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
        vm.Markers[0].ChapterTitle = proj.StartChapterTitle;
        foreach (var ps in proj.Splits.OrderBy(s => s.TimeUs))
        {
            vm.Markers.Add(new Split
            {
                Id = ps.Id,
                TimeUs = ps.TimeUs,
                Source = ps.Source,
                OriginalTimeUs = ps.OriginalTimeUs,
                Confidence = ps.Confidence,
                Confirmed = ps.Confirmed,
                ChapterTitle = ps.ChapterTitle,
            });
        }
        vm.Markers.Add(new Split { TimeUs = vm.DurationUs, Confidence = null, Confirmed = true, Label = "End" });

        foreach (var id in proj.ExcludedSegmentIds) vm.ExcludedSegmentIds.Add(id);
        vm.RebuildSegmentsFromSplits();
    }

    private static void HydrateFromChapters(MainViewModel vm, MediaInfo media, IReadOnlyList<ChapterBoundary> chapters)
    {
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();

        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
        vm.Markers[0].ChapterTitle = chapters.FirstOrDefault(c => c.TimeUs == 0)?.Title;
        foreach (var c in chapters.Where(c => c.TimeUs > 0))
        {
            vm.Markers.Add(new Split
            {
                TimeUs = c.TimeUs,
                ChapterTitle = c.Title,
                Source = SplitSource.Chapter,
                Confidence = null,
                Confirmed = false,
            });
        }
        vm.Markers.Add(new Split { TimeUs = media.DurationUs, Confidence = null, Confirmed = true, Label = "End" });
        vm.MarkerSetSource = MarkerSetSource.Imported;
        var state = StateFromChapters(chapters, MarkerSetSource.Imported);
        vm.MarkerBaselineId = StateId(state);
        vm.RebuildSegmentsFromSplits();
    }

    private static MarkerSetState StateFromChapters(IReadOnlyList<ChapterBoundary> chapters, MarkerSetSource source)
    {
        var start = chapters.FirstOrDefault(c => c.TimeUs == 0)?.Title;
        var splits = chapters.Where(c => c.TimeUs > 0).Select(c => new PersistedSplit(
            Guid.NewGuid().ToString("N")[..12], c.TimeUs, SplitSource.Chapter, null, null, false, c.Title)).ToArray();
        var preliminary = new MarkerSetState(splits, Array.Empty<string>(), start, source, null);
        return preliminary with { BaselineId = StateId(preliminary) };
    }

    private static string StateId(MarkerSetState state)
    {
        return state.ContentId();
    }

    private static void HydrateBlank(MainViewModel vm, MarkerSetSource source)
    {
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();
        vm.Markers.Add(new Split { TimeUs = 0, Confirmed = true, Label = "Start" });
        vm.Markers.Add(new Split { TimeUs = vm.DurationUs, Confirmed = true, Label = "End" });
        vm.MarkerSetSource = source;
        vm.RebuildSegmentsFromSplits();
        vm.MarkerBaselineId = vm.CurrentMarkerSetId();
    }

    private async Task<MarkerSetState?> AnalyzeCommercialsAsync(MainViewModel vm, string key,
        CancellationToken cancellationToken)
    {
        if (_ffmpeg is null || _media is null || vm.SourcePath is not { } source || vm.SourceFingerprint is not { } fingerprint) return null;
        var sourceAtStart = source;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _commercialAnalysisCts?.Cancel();

        _commercialAnalysisCts = linked;
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
                if (ReferenceEquals(_commercialAnalysisCts, linked) && !linked.IsCancellationRequested
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
                    vm.AnalysisTimeLeft = TimeLeftText(working.Elapsed, fraction - workingFrom, 1 - fraction);
                }
            });
            var media = _media;
            // Join the recording's audio pass (the timeline graph's) rather than reading the audio again.
            var result = await new SuggestionEngine(_ffmpeg).AnalyzeAsync(source, media, progress, linked.Token,
                (stage, ct) => Dispatcher.Invoke(() => EnsureAudioPass(vm, sourceAtStart, media)).JoinAsync(stage, ct),
                WaitForFullReadTurnAsync);
            if (linked.IsCancellationRequested || vm.SourcePath != sourceAtStart || vm.SourceFingerprint != fingerprint) return null;
            var splits = new List<PersistedSplit>();
            var exclusions = new List<string>();
            foreach (var proposal in result.Breaks.OrderBy(b => b.StartUs))
            {
                if (proposal.StartUs <= 0 || proposal.EndUs >= _media.DurationUs || proposal.EndUs <= proposal.StartUs
                    || splits.Any(s => s.TimeUs >= proposal.StartUs && s.TimeUs < proposal.EndUs)) continue;
                var startId = Guid.NewGuid().ToString("N")[..12];
                var endId = Guid.NewGuid().ToString("N")[..12];
                splits.Add(new PersistedSplit(startId, proposal.StartUs, SplitSource.Manual, null,
                    proposal.EndpointConfidence, false, "Possible commercial break"));
                splits.Add(new PersistedSplit(endId, proposal.EndUs, SplitSource.Manual, null,
                    proposal.EndpointConfidence, false, null));
                if (proposal.AutoExclude) exclusions.Add($"segment-{proposal.StartUs}-{proposal.EndUs}");
            }
            // Uncertain candidates appear as low-confidence (red) markers, unless they overlap a
            // suggested break or a more confident marker is already within a few seconds.
            var confident = splits.Select(s => s.TimeUs).ToList();
            foreach (var candidate in result.UncertainBreaks.OrderBy(b => b.StartUs))
            {
                if (candidate.EndpointConfidence != Confidence.Low || candidate.EndUs <= candidate.StartUs
                    || result.Breaks.Any(b => b.StartUs < candidate.EndUs && candidate.StartUs < b.EndUs)) continue;
                foreach (var (time, isStart) in new[] { (candidate.StartUs, true), (candidate.EndUs, false) })
                {
                    if (time <= 0 || time >= _media.DurationUs || confident.Any(t => Math.Abs(t - time) <= UncertainMarkerSpacingUs)
                        || splits.Any(s => s.TimeUs == time)) continue;
                    splits.Add(new PersistedSplit(Guid.NewGuid().ToString("N")[..12], time, SplitSource.Manual, null,
                        Confidence.Low, false, isStart ? "Possible commercial break (uncertain)" : null));
                }
            }
            splits.Sort((a, b) => a.TimeUs.CompareTo(b.TimeUs));
            var provisional = new MarkerSetState(splits, exclusions, null, MarkerSetSource.AdTrim, null);
            var state = provisional with { BaselineId = StateId(provisional) };
            vm.HasAdTrimAnalysis = true;
            var (breaks, uncertain) = SuggestionCounts(state);
            vm.Banner = new BannerInfo(StatusKind.Success, $"Ad-break analysis finished in {ElapsedText(total.Elapsed)}.",
                $"Found {Plural(breaks, "ad break")} and {Plural(uncertain, "uncertain red marker")}. Nothing was excluded; "
                + "review each break and exclude the ones you want removed.", Array.Empty<BannerAction>());
            try { _suggestionStore.Save(key, state); }
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
            if (ReferenceEquals(_commercialAnalysisCts, linked)) _commercialAnalysisCts = null;
            // Nothing else wants an unfinished pass once analysis stops with the graph hidden.
            if (!vm.ShowWaveform && _audioPass is { Completion.IsCompleted: false }) ReleaseAudioPass(vm);
            vm.IsBusy = wasBusy;
            vm.BusyOperation = previousOperation;
            vm.ProgressPercent = previousProgress;
        }
    }

    private void ApplyMarkerSet(MainViewModel vm, MarkerSetState state, string action, bool confirm)
    {
        if (confirm && !ConfirmDiscardEdits(vm, state.Source)) return;
        if (vm.IsCollapsed) SetCollapsed(vm, false);
        vm.CommandStack.Execute(new ReplaceMarkerSetCommand(vm, state, action));
        var (breaks, uncertain) = SuggestionCounts(state);
        vm.StatusOverride = state.Source == MarkerSetSource.AdTrim
            ? $"{Plural(breaks, "possible ad break")}, {Plural(uncertain, "uncertain red marker")}. All video remains included; review before excluding."
            : action;
        vm.StatusKind = StatusKind.Info;
    }

    /// <summary>Suggested breaks (each starts with a labeled marker) and uncertain red markers in a set.</summary>
    private static (int Breaks, int Uncertain) SuggestionCounts(MarkerSetState state) =>
        (state.Splits.Count(s => s.Confidence != Confidence.Low && s.ChapterTitle is not null),
         state.Splits.Count(s => s.Confidence == Confidence.Low));

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string ElapsedText(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s"
        : $"{Math.Max(1, (int)Math.Round(elapsed.TotalSeconds))} s";

    /// <summary>Rough time left from the rate so far; null until there is enough progress to estimate.</summary>
    internal static string? TimeLeftText(TimeSpan elapsed, double done, double remaining)
    {
        if (elapsed < TimeSpan.FromSeconds(5) || done < .03) return null;
        double seconds = elapsed.TotalSeconds * remaining / done;
        return seconds < 60 ? "Less than a minute left" : $"About {(int)Math.Ceiling(seconds / 60)} min left";
    }

    /// <summary>Asks before edits are replaced by <paramref name="destination"/> markers. True when there
    /// are no edits to lose or the user agrees. Ask before any analysis work, not after.</summary>
    private bool ConfirmDiscardEdits(MainViewModel vm, MarkerSetSource destination)
    {
        if (!vm.IsTimelineEdited) return true;
        var sourceName = vm.MarkerSetSource switch
        {
            MarkerSetSource.Imported => "imported markers",
            MarkerSetSource.AdTrim => "AdTrim suggested markers",
            _ => "current markings",
        };
        var target = destination == MarkerSetSource.Imported ? "the original imported markers" : "the original AdTrim suggested markers";
        var confirmationTitle = vm.MarkerSetSource switch
        {
            MarkerSetSource.Imported => "Discard edits to imported markers?",
            MarkerSetSource.AdTrim => "Discard edits to AdTrim suggested markers?",
            _ => "Discard current edits?",
        };
        return _askDiscardEdits(
            $"This discards your edits to {sourceName}, including split positions, section names, and exclusions, then loads {target}.",
            confirmationTitle);
    }

    // Replaceable so the smoke check can answer it and see when it was asked.
    private Func<string, string, bool> _askDiscardEdits;

    /// <summary>The toolbar's Analyze ad breaks / Analyze ad breaks again button.</summary>
    private void OnAnalyzeAdBreaks(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.HasAdTrimAnalysis) OnAnalyzeCommercialsAgain(sender, e);
        else OnUseAdTrimSuggestions(sender, e);
    }

    private bool _importedMarkersAvailable;

    // The Ad break detection menu stays enabled (for the starting-markers preference), so this
    // item needs both conditions itself: the recording has chapters, and markers can change now.
    private void RefreshImportedMarkersItem(MainViewModel vm)
        => UseOriginalImportedMarkersMenuItem.IsEnabled = _importedMarkersAvailable && vm.CanReplaceMarkers;

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
        _commercialAnalysisCts?.Cancel();
        if (DataContext is MainViewModel vm) vm.StatusOverride = "Cancelling commercial analysis...";
    }

    private void OnUseImportedMarkers(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || _importedMarkerSet is null) return;
        ApplyMarkerSet(vm, _importedMarkerSet, "Revert to original imported markers", confirm: true);
    }

    private async void OnUseAdTrimSuggestions(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || vm.SourcePath is null || vm.SourceFingerprint is null) return;
        var source = vm.SourcePath;
        var key = _suggestionStore.Key(source, vm.SourceFingerprint);
        var state = _suggestionStore.Load(key, vm.DurationUs);
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
            || !StringComparer.Ordinal.Equals(key, _suggestionStore.Key(vm.SourcePath, vm.SourceFingerprint))) return;
        ApplyMarkerSet(vm, state, "Revert to original AdTrim suggested markers", confirm: !confirmed);
    }

    private async void OnAnalyzeCommercialsAgain(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanReplaceMarkers || vm.SourcePath is null || vm.SourceFingerprint is null) return;
        var source = vm.SourcePath;
        var key = _suggestionStore.Key(source, vm.SourceFingerprint);
        // Ask before the analysis work; markers are locked while it runs, so the answer stays valid.
        if (!ConfirmDiscardEdits(vm, MarkerSetSource.AdTrim)) return;
        var state = await AnalyzeCommercialsAsync(vm, key, CancellationToken.None);
        if (state is null)
        {
            vm.StatusOverride = "Analysis did not finish; current markings were kept.";
            return;
        }
        if (vm.SourcePath == source && vm.SourceFingerprint is not null
            && StringComparer.Ordinal.Equals(key, _suggestionStore.Key(vm.SourcePath, vm.SourceFingerprint)))
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
        _autosaveTimer.Stop();
        if (_hydrating || _saveInProgress || DataContext is not MainViewModel vm) return;
        await SaveProjectAsync(vm);
    }

    private async Task<bool> SaveProjectAsync(MainViewModel vm)
    {
        if (!vm.IsDirty) return true;
        if (_store is null || _media is null || vm.SourcePath is null || _sourceFingerprint is null) return false;
        var revision = vm.Revision;
        var source = vm.SourcePath;
        var snapshot = new AdTrimProject(AdTrimProject.CurrentSchemaVersion, source, _sourceFingerprint, _media,
            vm.Markers.Where(m => !m.IsBookend).Select(m => new PersistedSplit(m.Id, m.TimeUs,
                m.Source, m.OriginalTimeUs, m.Confidence, m.Confirmed, m.ChapterTitle)).ToList(),
            vm.ExcludedSegmentIds.ToList(), SidecarLocation.NextToSource, vm.Markers.FirstOrDefault()?.ChapterTitle,
            vm.MarkerSetSource, vm.MarkerBaselineId);
        _saveInProgress = true;
        vm.SaveStatus = "Saving";
        try
        {
            await _session.SaveAsync(_store, snapshot);
            if (vm.SourcePath == source && vm.Revision == revision)
            { vm.IsDirty = false; vm.SaveStatus = "Saved"; }
            else if (vm.SourcePath == source)
            { vm.SaveStatus = "Unsaved changes"; _autosaveTimer.Start(); }
            return true;
        }
        catch (Exception ex)
        {
            vm.SaveStatus = "Save failed";
            vm.Banner = new BannerInfo(StatusKind.Danger, "Could not save edits.", ex.Message,
                new[] { new BannerAction("Retry", () => OnAutosaveTick(null, EventArgs.Empty)) });
            return false;
        }
        finally { _saveInProgress = false; }
    }

    private async Task<bool> FlushBeforeLeavingAsync(MainViewModel vm)
    {
        _autosaveTimer.Stop();
        bool busy = vm.IsBusy;
        vm.IsBusy = true;
        await SavePreferencesAsync(vm);
        try
        {
            while (vm.IsDirty)
            {
                if (await SaveProjectAsync(vm)) continue;
                var choice = MessageBox.Show(this, "Edits could not be saved. Retry?\nChoose No to discard unsaved edits, or Cancel to stay here.",
                    "Save failed", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Cancel) return false;
                if (choice == MessageBoxResult.No) { vm.IsDirty = false; return true; }
            }
            return true;
        }
        finally { vm.IsBusy = busy; }
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Tick -= OnAutosaveTick;
            // mpv must release its video output before WPF destroys the host HWNDs.
            _previewAfter.PropertyChanged -= OnPreviewPropertyChanged;
            _previewBefore.PropertyChanged -= OnPreviewPropertyChanged;
            _previewAfter.Dispose();
            _previewBefore.Dispose();
            return;
        }
        e.Cancel = true;
        if (_commercialAnalysisCts is { } analysis)
        {
            analysis.Cancel();
            while (ReferenceEquals(_commercialAnalysisCts, analysis)) await Task.Delay(50);
        }
        if (_switching)
        {
            _closeWhenSwitchCompletes = true;
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
        _refineCts?.Cancel();
        if (await FlushBeforeLeavingAsync(vm))
        {
            _refineCts?.Cancel();
            ReleaseAudioPass(vm);
            CancelThumbnailLoad(vm, false);
            _allowClose = true;
            Close();
        }
        _switching = false;
    }

    // -------------------------------------------------------------------
    // Keyboard router
    // -------------------------------------------------------------------

    /// <summary>
    /// Seek both panes to bracket the requested time and immediately update
    /// vm.PlayheadUs so chained keystrokes accumulate. mpv's TimeChanged
    /// property events arrive asynchronously (often 50-200 ms after a seek);
    /// without this helper, pressing `[`, `[`, `[` reads the same stale
    /// `vm.PlayheadUs` three times and only the last seek "sticks".
    ///
    /// <para>Dual-pane: Pane B lands on <paramref name="timeUs"/>; Pane A
    /// lands on <c>timeUs − 1 frame</c>. Both seeks are issued together so
    /// the panes stay exactly 1 frame apart while paused. When Pane A's
    /// target is before frame 0, the "No frame available" overlay is shown
    /// instead (driven by PlayheadUs → UpdateNoFrameOverlay).</para>
    /// </summary>
    /// <param name="exact">When true, uses frame-accurate seek regardless of
    /// the global hr-seek mode. Keyboard shortcuts pass <c>true</c> so a
    /// +1s seek can't get rounded to the same keyframe in fast-seek mode.</param>
    private void SeekTo(MainViewModel vm, long timeUs, bool exact = true)
    {
        _frameRecovery?.Cancel();
        _failedFrameRecovery = null;
        if (vm.IsCollapsed)
        {
            if (vm.KeptTimeline.DurationUs == 0) return;
            var output = vm.KeptTimeline.ToOutput(timeUs);
            vm.PlayheadUs = vm.KeptTimeline.ToSource(output);
            _previewAfter.SeekUsExact(output);
            return;
        }
        timeUs = Math.Max(0, Math.Min(vm.DurationUs, timeUs));
        vm.PlayheadUs = timeUs;
        var frameUs = FrameDurationUs(vm);
        var beforeUs = Math.Max(0, timeUs - frameUs);
        if (exact)
        {
            _previewAfter.SeekUsExact(timeUs);
            _previewBefore.SeekUsExact(beforeUs);
        }
        else
        {
            _previewAfter.SeekUs(timeUs);
            _previewBefore.SeekUs(beforeUs);
        }
    }

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

        if (e.Key == Key.Escape)
        {
            if (vm.IsBusy && vm.BusyOperation == "Refining") CancelRefine();
            else if (vm.IsBusy && vm.BusyOperation == "Analyzing commercial breaks")
            {
                _commercialAnalysisCts?.Cancel();
                vm.StatusOverride = "Cancelling commercial analysis…";
            }
            else if (!vm.IsBusy && vm.SelectionKind != SelectionKind.None) vm.ClearSelection();
            e.Handled = true;
            return;
        }

        if (_commercialAnalysisCts is not null && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        { PlayPauseBoth(); e.Handled = true; return; }

        if (vm.IsBusy)
        {
            if (e.Key == Key.Tab) return;
            vm.StatusKind = StatusKind.Warning;
            vm.StatusOverride = $"{vm.BusyOperation ?? "Operation"} in progress.";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.None)
        { if (!e.IsRepeat) OnToggleCollapsed(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key is Key.Home or Key.End && Keyboard.Modifiers == ModifierKeys.None && vm.IsFileLoaded)
        {
            var positionUs = e.Key == Key.Home ? 0 : vm.TimelineDurationUs;
            SeekTo(vm, vm.IsCollapsed ? vm.KeptTimeline.ToSource(positionUs) : positionUs);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.J or Key.K or Key.L && vm.IsFileLoaded)
        { Shuttle(vm, e.Key); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { OnFitTimeline(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { OnZoomIn(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { OnZoomOut(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!ctrl && e.Key == Key.E) { OnExport(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (vm.IsCollapsed)
        {
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) PlayPauseBoth();
            else if (e.Key is Key.OemComma or Key.OemPeriod) StepFrame(vm, e.Key == Key.OemComma ? -1 : 1);
            else if (e.Key is Key.Left or Key.Right or Key.OemOpenBrackets or Key.OemCloseBrackets)
            {
                long step = e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets ? 1_000_000 : ArrowStepUs(vm);
                long direction = e.Key is Key.Left or Key.OemOpenBrackets ? -1 : 1;
                SeekTo(vm, vm.KeptTimeline.ToSource(vm.TimelinePositionUs + direction * step));
            }
            else if (ctrl && e.Key == Key.S) OnAutosaveTick(null, EventArgs.Empty);
            else if (ctrl && e.Key == Key.O) OnOpenFile(this, new RoutedEventArgs());
            else if (ctrl && e.Key == Key.W) OnCloseProject(this, new RoutedEventArgs());
            else if (!ctrl && !shift && (e.Key is >= Key.D0 and <= Key.D9 || e.Key is >= Key.NumPad0 and <= Key.NumPad9))
            {
                var digit = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                SeekTo(vm, vm.KeptTimeline.ToSource(vm.TimelineDurationUs * digit / 10));
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
            StepFrame(vm, -1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.OemPeriod)
        {
            StepFrame(vm, +1);
            e.Handled = true;
            return;
        }

        // 1-second step
        if (e.Key == Key.OemOpenBrackets)  { SeekTo(vm, vm.PlayheadUs - 1_000_000); e.Handled = true; return; }
        if (e.Key == Key.OemCloseBrackets) { SeekTo(vm, vm.PlayheadUs + 1_000_000); e.Handled = true; return; }

        // Arrow seek (zoom-dependent step)
        if (e.Key is Key.Left or Key.Right)
        {
            long step = ArrowStepUs(vm);
            var dir = e.Key == Key.Left ? -1L : 1L;
            SeekTo(vm, vm.PlayheadUs + dir * step);
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
                SeekTo(vm, vm.DurationUs * d / 10);
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
            if (next is not null) { vm.SelectMarker(next); SeekTo(vm, next.TimeUs); }
            e.Handled = true;
            return;
        }

        // Play/pause (Space)
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            PlayPauseBoth();
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

    // -------------------------------------------------------------------
    // Maximize constraint - clamp to the current monitor's work area.
    // Without this, a window using custom WindowChrome maximizes to a
    // rect that overhangs the screen edges and slides under the taskbar.
    // -------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var src = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        src?.AddHook(OnMaximizeBoundsHook);
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;

    private IntPtr OnMaximizeBoundsHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Title-bar double-click / Win+Up / window menu "Maximize" all send
        // WM_SYSCOMMAND with SC_MAXIMIZE - intercept and route to our
        // fake-maximize so they don't trigger the broken native path
        // (custom WindowChrome makes native maximize spill past the work area
        // by ~8 px). We let every other message - including WM_GETMINMAXINFO,
        // which is what WPF uses to enforce MinWidth/MinHeight on user resize
        // - fall through to default handling.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
        {
            OnMaximize(this, new RoutedEventArgs());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static long FrameDurationUs(MainViewModel vm)
    {
        var fps = vm.FrameRate.AsDouble;
        return fps <= 0 ? 33_366L : (long)Math.Round(1_000_000.0 / fps);
    }

    private static long ArrowStepUs(MainViewModel vm)
    {
        // Zoom-aware: at higher zoom, smaller steps.
        var z = vm.ZoomFactor;
        if (z >= 8) return FrameDurationUs(vm);   // 1 frame
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
            Filter = "MP4 video (*.mp4)|*.mp4",
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
            string.Equals(Path.GetExtension(f), ".mp4", StringComparison.OrdinalIgnoreCase));
        if (first is not null) _ = OpenFileAsync(first);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // "Fake maximize": with custom WindowChrome, WindowState=Maximized renders
    // a few pixels past the work area (the OS adds its own padded border that
    // WPF can't fully suppress via WM_GETMINMAXINFO). We fake it by sizing the
    // window to SystemParameters.WorkArea while leaving WindowState=Normal,
    // which fits exactly inside the taskbar and screen edges.
    private Rect? _preMaximizeBounds;
    private const string MaxIconGeometry = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreIconGeometry = "M2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_preMaximizeBounds.HasValue)
        {
            var b = _preMaximizeBounds.Value;
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            _preMaximizeBounds = null;
            MaxButtonIcon.Data = System.Windows.Media.Geometry.Parse(MaxIconGeometry);
        }
        else
        {
            _preMaximizeBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            var work = MonitorBounds.WorkArea(this);
            Left = work.Left; Top = work.Top;
            Width = work.Width; Height = work.Height;
            MaxButtonIcon.Data = System.Windows.Media.Geometry.Parse(RestoreIconGeometry);
        }
    }

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
        _switching = true;
        bool flushed;
        try { flushed = await FlushBeforeLeavingAsync(vm); }
        finally { _switching = false; }
        if (!flushed) return;
        SetCollapsed(vm, false);
        ReleaseAudioPass(vm);
        CancelThumbnailLoad(vm, clearThumbnails: true);
        _refineCts?.Cancel();
        _autosaveTimer.Stop();
        _previewAfter.Pause();
        _previewBefore.Pause();
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
        _previewAfter.Stop();
        _previewBefore.Stop();
        _media = null;
        _sourceFingerprint = null;
        _importedMarkerSet = null;
        vm.MarkerSetSource = MarkerSetSource.Unknown;
        vm.MarkerBaselineId = null;
        vm.SourceFingerprint = null;
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
        vm.Banner = new BannerInfo(StatusKind.Warning,
            "Export is open.",
            _activeExportDialog.IsExportInFlight
                ? "Cancel or finish the export before opening or closing a project."
                : "Return to editing before opening or closing a project.",
            Array.Empty<BannerAction>());
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
        if (_previewAfter.IsSeeking || _previewAfter.IsLoading) return;
        var timeUs = _previewAfter.PositionUs;
        var minDelta = FrameDurationUs(vm);
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
        if (next is not null) { vm.SelectMarker(next); SeekTo(vm, next.TimeUs); }
    }

    private void OnPreviousSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var previous = vm.Markers.Select(m => vm.ToDisplayTime(m.TimeUs))
            .Where(t => t < vm.TimelinePositionUs).DefaultIfEmpty(0).Max();
        SeekTo(vm, vm.ToSourceTime(previous));
    }

    private void OnNextSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var next = vm.Markers.Select(m => vm.ToDisplayTime(m.TimeUs))
            .Where(t => t > vm.TimelinePositionUs).DefaultIfEmpty(vm.TimelineDurationUs).Min();
        SeekTo(vm, vm.ToSourceTime(next));
    }

    private void OnStepFrameBack(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) StepFrame(vm, -1);
    }

    private void OnStepFrameForward(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) StepFrame(vm, +1);
    }

    private void StepFrame(MainViewModel vm, int direction)
    {
        if (vm.IsBusy) return;
        if (_previewAfter.IsPlaying) { _previewAfter.Pause(); _previewBefore.Pause(); }
        // Use mpv's native frame-step commands rather than a duration-based
        // seek. The container's r_frame_rate (used by FrameDurationUs) doesn't
        // always match mpv's actual displayed frame interval - telecined
        // 24fps content carried in a 1080i59.94 container is the classic
        // case. A duration-based step lands between real frames, and mpv's
        // absolute+exact seek then rounds *forward* to the next real frame,
        // so backward steps go nowhere.
        //
        // Both panes step in parallel: same source, same frame grid, so the
        // one-frame offset between PreviewBefore and PreviewAfter is preserved.
        // PlayheadUs updates via the time-pos observer once mpv reports the
        // new position (usually within one display refresh).
        if (direction > 0)
        {
            _previewAfter.StepFrameForward();
            if (!vm.IsCollapsed) _previewBefore.StepFrameForward();
        }
        else
        {
            _previewAfter.StepFrameBack();
            if (!vm.IsCollapsed) _previewBefore.StepFrameBack();
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
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

        _previewAfter.Pause();
        _previewBefore.Pause();
        // Export needs the disk. A finished audio pass costs nothing and stays; the graph keeps
        // what a running one measured, and the pass starts over when the editor returns.
        if (_audioPass is { Completion.IsCompleted: false } running) { running.Cancel(); _audioPass = null; }
        CancelThumbnailLoad(vm, false);
        var dlg = new ExportView();
        _activeExportDialog = dlg;
        dlg.Bind(vm, _media, _preferences.ExportAcceleration);
        dlg.Closed += async (_, _) =>
        {
            if (dlg.DataContext is ExportDialogViewModel completed)
            { _preferences = _preferences with { ExportAcceleration = !completed.CanCheckHardware ? _preferences.ExportAcceleration : completed.SelectedAcceleration.Id }; await SavePreferencesAsync(vm); }
        };
        dlg.AttachExportRunner(_ffmpeg, statusBarProgress);
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
                if (vm.ShowWaveform && vm.SourcePath is { } waveformPath && _media is not null) EnsureAudioPass(vm, waveformPath, _media);
                if (vm.ShowThumbnails && vm.SourcePath is { } thumbnailPath && _media is not null) StartThumbnailLoad(vm, thumbnailPath, _media);
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
                var sidecarNote = MaybeDeleteSidecar(plan.SourcePath, dlg.ShouldDeleteSidecarAfterSuccess);
                vm.Banner = new BannerInfo(StatusKind.Success,
                    "Export complete.",
                    $"Wrote {Path.GetFileName(plan.OutputPath)}.{sidecarNote}",
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
    {
        if (!shouldDelete) return string.Empty;
        _store ??= new ProjectStore();
        var existing = _store.CandidateSidecarPathsFor(sourcePath)
            .Where(File.Exists)
            .ToList();
        if (existing.Count == 0) return " Sidecar already absent.";
        try
        {
            foreach (var sidecar in existing) File.Delete(sidecar);
            return existing.Count == 1 ? " Sidecar deleted." : $" {existing.Count} sidecars deleted.";
        }
        catch (Exception ex)
        {
            return $" Sidecar delete failed: {ex.Message}";
        }
    }

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
            : $"{count} visible markers selected ({FormatTimeMs(from)} to {FormatTimeMs(to)}). Esc clears the selection.");
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

    private void OnShowShortcuts(object sender, RoutedEventArgs e) => new ShortcutsDialog { Owner = this }.ShowDialog();

    private void OnJumpPreviousUnconfirmed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { CanEdit: true } vm) return;
        var previous = vm.Markers.Where(m => !m.IsBookend && !m.Confirmed && m.TimeUs < vm.PlayheadUs)
            .OrderByDescending(m => m.TimeUs).FirstOrDefault();
        if (previous is null) { Notice(vm, StatusKind.Info, "No unconfirmed split before the playhead."); return; }
        vm.SelectMarker(previous);
        SeekTo(vm, previous.TimeUs);
    }

    private void OnPreviousMarker(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) JumpToMarker(vm, -1); }
    private void OnNextMarker(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) JumpToMarker(vm, 1); }

    /// <summary>Alt+Left / Alt+Right: select and go to the previous or next marker.</summary>
    private void JumpToMarker(MainViewModel vm, int direction)
    {
        if (!vm.CanEdit || vm.IsCollapsed) return;
        var target = direction > 0
            ? vm.Markers.Where(m => !m.IsBookend && m.TimeUs > vm.PlayheadUs).OrderBy(m => m.TimeUs).FirstOrDefault()
            : vm.Markers.Where(m => !m.IsBookend && m.TimeUs < vm.PlayheadUs).OrderByDescending(m => m.TimeUs).FirstOrDefault();
        if (target is null) { Notice(vm, StatusKind.Info, direction > 0 ? "No marker after the playhead." : "No marker before the playhead."); return; }
        vm.SelectMarker(target);
        SeekTo(vm, target.TimeUs);
    }

    // J / K / L shuttle. Positive: forward playback rate; negative: rewind rate.
    private int _shuttle;
    private DispatcherTimer? _rewindTimer;
    private const int RewindTickMs = 100;

    private void Shuttle(MainViewModel vm, Key key)
    {
        if (vm.IsBusy && _commercialAnalysisCts is null) return;
        if (key == Key.K)
        {
            ResetShuttle();
            _previewAfter.Pause();
            _previewBefore.Pause();
            Notice(vm, StatusKind.Info, "Paused.");
            return;
        }
        if (key == Key.L)
        {
            bool rewinding = _shuttle < 0;
            StopRewind();
            if (_previewAfter.IsPlaying && !rewinding)
                _shuttle = Math.Min(4, Math.Max(1, _shuttle) * 2); // Space playback counts as 1×
            else
            {
                if (!_previewAfter.IsPlaying) PlayPauseBoth();
                _shuttle = 1;
            }
            _previewAfter.SetSpeed(_shuttle);
            _previewBefore.SetSpeed(_shuttle);
            Notice(vm, StatusKind.Info, $"Playing {_shuttle}×. K pauses.");
            return;
        }
        // J: rewind by stepping the playhead backward (mpv has no cheap reverse playback).
        if (_previewAfter.IsPlaying) { _previewAfter.Pause(); _previewBefore.Pause(); }
        _previewAfter.SetSpeed(1);
        _previewBefore.SetSpeed(1);
        _shuttle = _shuttle < 0 ? Math.Max(-4, _shuttle * 2) : -1;
        _rewindTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(RewindTickMs), DispatcherPriority.Input, (_, _) => RewindTick(), Dispatcher);
        _rewindTimer.Start();
        Notice(vm, StatusKind.Info, $"Rewinding {-_shuttle}×. K stops.");
    }

    private void RewindTick()
    {
        if (DataContext is not MainViewModel vm || _shuttle >= 0) { StopRewind(); return; }
        long step = RewindTickMs * 1_000L * -_shuttle;
        long position = vm.IsCollapsed ? vm.TimelinePositionUs : vm.PlayheadUs;
        if (position <= 0) { ResetShuttle(); Notice(vm, StatusKind.Info, "Reached the start."); return; }
        long target = Math.Max(0, position - step);
        SeekTo(vm, vm.IsCollapsed ? vm.KeptTimeline.ToSource(target) : target, exact: false);
    }

    private void StopRewind()
    {
        _rewindTimer?.Stop();
        if (_shuttle < 0) _shuttle = 0;
    }

    private void ResetShuttle()
    {
        StopRewind();
        if (_shuttle > 1) { _previewAfter.SetSpeed(1); _previewBefore.SetSpeed(1); }
        _shuttle = 0;
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
        for (int i = 0; i < 75 && (_previewAfter.IsSeeking || _previewAfter.IsLoading); i++) await Task.Delay(20);
        if (_previewAfter.IsSeeking || _previewAfter.IsLoading)
        { Notice(vm, StatusKind.Warning, "The preview is still moving to the playhead. Press M again in a moment."); return; }
        if (!ReferenceEquals(vm.SelectedMarker, split) || vm.IsBusy) return;
        if (split.Confirmed && !ConfirmUnconfirm(split)) return;
        if (split.Confirmed)
            vm.CommandStack.Execute(new ToggleConfirmedCommand(split));

        var sorted = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var idx = sorted.IndexOf(split);
        long minUs = idx > 0 ? sorted[idx - 1].TimeUs + 1 : 0;
        long maxUs = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs - 1 : vm.DurationUs;
        var candidate = _previewAfter.PositionUs;
        if (candidate < minUs || candidate > maxUs)
        { Notice(vm, StatusKind.Warning, "The playhead is past a neighboring marker; a marker cannot move across another one."); return; }
        if (candidate == split.TimeUs)
        { Notice(vm, StatusKind.Info, "The marker is already at the playhead."); return; }
        long from = split.TimeUs;
        vm.CommandStack.Execute(new MoveSplitCommand(vm, split, from, candidate));
        Notice(vm, StatusKind.Success, $"Moved marker from {FormatTimeMs(from)} to {FormatTimeMs(candidate)}. Ctrl+Z undoes it.");
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
        await RefineMarkersAsync(vm, new[] { split });
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

    /// <summary>
    /// Common orchestrator for `Refine this split` and `Refine all splits`.
    /// Skips confirmed and bookend markers; per-marker bounds come from
    /// neighbors so the cross-neighbor invariant is honored. Mutations are
    /// applied as a single BatchedRefineCommand so the entire pass is one
    /// undo entry.
    /// </summary>
    private async Task RefineMarkersAsync(MainViewModel vm, IReadOnlyList<Split> targets)
    {
        if (!vm.CanEdit) return;
        if (vm.SourcePath is null || _ffmpeg is null)
        {
            vm.Banner = new BannerInfo(StatusKind.Danger,
                "Refine unavailable.",
                "Open a file first.",
                Array.Empty<BannerAction>());
            return;
        }

        _refine ??= new RefineService(_ffmpeg);
        var allowConfirmedSingle = targets.Count == 1
            && targets[0].Confirmed
            && !targets[0].IsBookend
            && ConfirmRefineConfirmed(targets[0]);

        var sorted = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var eligible = targets
            .Where(m => !m.IsBookend && (!m.Confirmed || allowConfirmedSingle))
            .Distinct()
            .OrderBy(m => m.TimeUs)
            .ToList();

        if (eligible.Count == 0)
        {
            vm.Banner = new BannerInfo(StatusKind.Info,
                "Nothing to refine.",
                "Bookends and confirmed splits are skipped.",
                Array.Empty<BannerAction>());
            return;
        }

        // Surface the refine pass start with both a banner AND status-bar
        // text so it's hard to miss. Banner stays until pass completes.
        _refineCts?.Dispose();
        _refineCts = new CancellationTokenSource();
        var ct = _refineCts.Token;
        var refineSource = vm.SourcePath;
        vm.IsBusy = true;
        vm.BusyOperation = "Refining";
        vm.Banner = new BannerInfo(StatusKind.Info,
            eligible.Count == 1 ? "Refining 1 split…" : $"Refining {eligible.Count} splits…",
            "Probing ±2 s around each marker for cleaner cut boundaries.",
            new[] { new BannerAction("Cancel", CancelRefine) });
        vm.StatusKind = StatusKind.Info;
        vm.ProgressPercent = 0.0;
        vm.StatusOverride = $"Refining 1 / {eligible.Count}…";
        // Clear the previous-pass chip while this pass runs; it'll be repopulated
        // on completion (or stay clear if cancelled / no mutations applied).
        vm.RefineSummary = null;

        var mutations = new List<RefineMutation>(eligible.Count);
        int high = 0, medium = 0, low = 0, unchanged = 0;

        for (int i = 0; i < eligible.Count; i++)
        {
            var marker = eligible[i];
            var idx = sorted.IndexOf(marker);
            long minBound = idx > 0 ? sorted[idx - 1].TimeUs : 0;
            if (idx > 0 && mutations.LastOrDefault(m => m.Marker == sorted[idx - 1]) is { } previous) minBound = previous.ToUs;
            long maxBound = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs : vm.DurationUs;

            vm.StatusOverride = $"Refining {i + 1} / {eligible.Count}…";
            vm.ProgressPercent = (i + 1) / (double)eligible.Count;

            RefineResult? result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await _refine.RefineOneAsync(vm.SourcePath, marker.TimeUs, minBound, maxBound, ct, _media?.PrimaryAudioIndex);
            }
            catch (OperationCanceledException)
            {
                vm.ClearStatus();
                vm.Banner = new BannerInfo(StatusKind.Warning,
                    "Refine cancelled.",
                    "No marker changes were applied.",
                    Array.Empty<BannerAction>());
                vm.IsBusy = false;
                vm.BusyOperation = null;
                return;
            }
            catch (Exception ex)
            {
                vm.ClearStatus();
                vm.Banner = new BannerInfo(StatusKind.Danger,
                    $"Refine failed on split #{idx}.",
                    ex.Message,
                    Array.Empty<BannerAction>());
                vm.IsBusy = false;
                vm.BusyOperation = null;
                return;
            }

            if (ct.IsCancellationRequested || vm.SourcePath != refineSource) return;
            if (result is null || result.Confidence == Confidence.Unchanged)
            {
                unchanged++;
                continue;
            }

            switch (result.Confidence)
            {
                case Confidence.High:   high++; break;
                case Confidence.Medium: medium++; break;
                case Confidence.Low:    low++; break;
            }

            mutations.Add(new RefineMutation(
                Marker: marker,
                FromUs: marker.TimeUs,
                ToUs: result.RefinedTimeUs,
                FromConfidence: marker.Confidence,
                ToConfidence: result.Confidence,
                FromOriginalTimeUs: marker.OriginalTimeUs,
                ToOriginalTimeUs: marker.OriginalTimeUs ?? marker.TimeUs));
        }

        if (ct.IsCancellationRequested || vm.SourcePath != refineSource) return;
        vm.ClearStatus();
        vm.IsBusy = false;
        vm.BusyOperation = null;
        if (mutations.Count > 0)
            vm.CommandStack.Execute(new BatchedRefineCommand(vm, mutations));

        // Build a useful completion banner: success if anything moved, info
        // otherwise. When the user refined a single split (the most common
        // case from the right-click menu), show the actual delta so they can
        // tell the algorithm did something even when the visual shift is
        // sub-pixel on a wide timeline.
        StatusKind kind;
        string title;
        string body;
        if (mutations.Count == 1)
        {
            var m = mutations[0];
            var deltaMs = (m.ToUs - m.FromUs) / 1000.0;
            kind = StatusKind.Success;
            title = $"Refined to {FormatTimeMs(m.ToUs)} ({m.ToConfidence})";
            body = deltaMs == 0
                ? "Marker stayed on the same frame (already on a clean boundary)."
                : $"Moved {(deltaMs >= 0 ? "+" : "")}{deltaMs:0.0} ms from {FormatTimeMs(m.FromUs)}.";
        }
        else if (mutations.Count > 0)
        {
            kind = StatusKind.Success;
            title = $"Refined {mutations.Count} of {eligible.Count} splits.";
            body = $"{high} high · {medium} medium · {low} low · {unchanged} unchanged";
        }
        else
        {
            kind = StatusKind.Info;
            title = eligible.Count == 1
                ? "No movement found for this split."
                : $"No movement found for {eligible.Count} splits.";
            body = "Marker stayed put - no candidate frame scored above the threshold.";
        }
        vm.Banner = new BannerInfo(kind, title, body, Array.Empty<BannerAction>());

        // Persistent summary chip in the timeline toolbar - survives banner
        // dismissal so the user can glance at the last pass's outcome while
        // working through the markers. Only set when the pass produced data
        // (eligible > 0) so it doesn't show "0 0 0 0" for nothing-to-do.
        if (eligible.Count > 0)
            vm.RefineSummary = $"{high}H · {medium}M · {low}L · {unchanged}U";
    }

    private void CancelRefine() => _refineCts?.Cancel();

    private static string FormatTimeMs(long us)
    {
        var totalSec = us / 1_000_000;
        var h = (int)(totalSec / 3600);
        var m = (int)((totalSec % 3600) / 60);
        var sec = (int)(totalSec % 60);
        var ms = (int)((us % 1_000_000) / 1_000);
        return h > 0 ? $"{h}:{m:00}:{sec:00}.{ms:000}" : $"{m:00}:{sec:00}.{ms:000}";
    }

    private async void OnRefineAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        await RefineMarkersAsync(vm, vm.Markers.ToList());
    }

    private async void OnRefineSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = vm.SelectedMarkers.Where(m => !m.IsBookend).ToList();
        if (selected.Count == 0) return;
        await RefineMarkersAsync(vm, selected);
    }

    private void OnTimelineNudgeRequested(object? sender, (Split split, int direction) e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        if (e.split.Confirmed && !ConfirmUnconfirm(e.split)) return;
        if (e.split.Confirmed)
            vm.CommandStack.Execute(new ToggleConfirmedCommand(e.split));
        var frameUs = FrameDurationUs(vm) * e.direction;
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
        SeekTo(vm, e.TimeUs, e.Exact);
        if (e.StartPlaying)
        {
            _playUntilUs = e.StopAtUs;
            _previewAfter.Play();
            _previewBefore.Play();
        }
    }
}
