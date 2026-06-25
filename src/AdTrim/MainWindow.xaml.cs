using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private WaveformService? _waveform;
    private CancellationTokenSource? _waveformCts;
    private ThumbnailService? _thumbnails;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _refineCts;
    private MediaInfo? _media;
    private long? _playUntilUs;

    /// <summary>
    /// Currently-displayed or currently-hidden-but-still-running export
    /// dialog. Held across export lifecycles so a second Export click - or
    /// a status-bar click on the "Encoding…" indicator - brings back the
    /// same dialog (with its parts list + live progress intact) rather
    /// than spawning a fresh one. Cleared on the dialog's Closed event.
    /// </summary>
    private ExportDialog? _activeExportDialog;

    private readonly DispatcherTimer _autosaveTimer;
    private const long AuditionWindowUs = 2_000_000L;          // ±2s

    public MainWindow()
    {
        InitializeComponent();
        // Runtime starts empty - the WPF designer uses the design-time
        // `<vm:MainViewModel/>` in XAML for live-preview only.
        DataContext = new MainViewModel();

        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _autosaveTimer.Tick += OnAutosaveTick;

        WireViewModelEvents((MainViewModel)DataContext);

        Loaded += OnLoaded;
        Closed += (_, _) => { _previewAfter.Dispose(); _previewBefore.Dispose(); };
        PreviewKeyDown += OnPreviewKeyDown;
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
            // Debounced autosave: reset timer on every dirty event.
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ZoomFactor))
                UpdateTimelineLayout(forceRecenter: true);
            else if (e.PropertyName == nameof(MainViewModel.ShowWaveform))
            {
                if (vm.ShowWaveform)
                {
                    if (vm.SourcePath is { } path && _media is not null)
                        StartWaveformLoad(vm, path, _media);
                }
                else
                {
                    CancelWaveformLoad(vm, clearPeaks: true);
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
        var frameUs = FrameDurationUs(vm);
        var paneATimeUs = vm.PlayheadUs - frameUs;
        var needsOverlay = paneATimeUs < vm.FrameStartPhaseUs - (frameUs / 2);
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

        if (vm.DurationUs <= 0) return;
        var playheadX = (vm.PlayheadUs / (double)vm.DurationUs) * contentWidth;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
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

        if (App.PendingOpenPath is { } path && File.Exists(path))
        {
            App.PendingOpenPath = null;
            _ = OpenFileAsync(path);
        }
    }

    private void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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

            // Only Pane B drives PlayheadUs and audition state.
            if (!ReferenceEquals(sender, _previewAfter))
            {
                MaybeResyncPanes(vm);
                return;
            }

            vm.PlayheadUs = _previewAfter.PositionUs;
            MaybeResyncPanes(vm);

            // Audition boundary cue: when the playhead first reaches the
            // split's exact time, pause both panes for ~300ms so the user
            // sees the boundary frame held visible, then resume to play
            // out the +2s tail. The pause is the cue.
            if (_auditionSplitUs is { } splitUs
                && !_auditionBoundaryHandled
                && _previewAfter.PositionUs >= splitUs)
            {
                _auditionBoundaryHandled = true;
                _previewAfter.Pause();
                _previewBefore.Pause();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AuditionBoundaryPauseMs) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    // Skip if the audition was cancelled (Escape) during the freeze.
                    if (_auditionTargetEndUs is null) return;
                    _previewAfter.Play();
                    _previewBefore.Play();
                };
                _auditionResumeTimer = timer;
                timer.Start();
            }

            // Audition end-watch.
            if (_auditionTargetEndUs is { } endUs && _previewAfter.PositionUs >= endUs)
                EndAudition();
            if (_playUntilUs is { } stopUs && _previewAfter.PositionUs >= stopUs)
            {
                _previewAfter.Pause();
                _previewBefore.Pause();
                _playUntilUs = null;
            }
        }
        else if (e.PropertyName == nameof(MpvPreviewViewModel.IsPlaying))
        {
            // Swap the play/pause icon visibility.
            PauseGlyph.Visibility = _previewAfter.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
            PlayGlyph.Visibility = _previewAfter.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ----- Pane drift / timestamp helpers ------------------------------

    private bool _resyncing;

    /// <summary>
    /// Expected Before/After delta is exactly +1 frame (After leads Before).
    /// Rapid frame-step / 1-second-step key spam (`.`, `]`) can drift the panes
    /// - sometimes far enough that Before ends up showing a *later* frame than
    /// After. While paused this is never acceptable, so re-seek Before to
    /// After − frameUs whenever the actual delta strays outside half-frame
    /// tolerance. Skips during playback (drift is acceptable there per the
    /// design) and during an in-flight resync to avoid feedback storms.
    /// </summary>
    private void MaybeResyncPanes(MainViewModel vm)
    {
        if (_resyncing) return;
        if (_previewAfter.IsPlaying || _previewBefore.IsPlaying) return;
        var frameUs = FrameDurationUs(vm);
        if (frameUs <= 0) return;
        var actualDelta = _previewAfter.PositionUs - _previewBefore.PositionUs;
        var lo = frameUs / 2;
        var hi = (3 * frameUs) / 2;
        if (actualDelta >= lo && actualDelta <= hi) return;

        _resyncing = true;
        var target = Math.Max(0, _previewAfter.PositionUs - frameUs);
        _previewBefore.SeekUsExact(target);
        // Clear the guard after one round-trip so future drift can be caught.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); _resyncing = false; };
        timer.Start();
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
        var newMuted = !_previewBefore.IsMuted;
        _previewBefore.SetMuted(newMuted);
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
        if (!File.Exists(path)) return;
        if (DataContext is not MainViewModel vm) return;
        if (BlockProjectSwitchForExport(vm)) return;

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

        CancelWaveformLoad(vm, clearPeaks: true);
        CancelThumbnailLoad(vm, clearThumbnails: true);
        _refineCts?.Cancel();
        _autosaveTimer.Stop();

        // Open the same file in both panes. mpv handles two readers on the
        // same path fine (and the OS page cache shares the underlying file
        // pages, so the incremental cost is decoder state, not I/O).
        _previewAfter.Open(path);
        _previewBefore.Open(path);
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        MpvViewAfter.Visibility = Visibility.Visible;
        MpvViewBefore.Visibility = Visibility.Visible;

        // Suppress autosave during initial hydration.
        _autosaveTimer.Stop();

        try
        {
            vm.StatusKind = StatusKind.Info;
            vm.StatusOverride = "Probing media…";
            _media = await _probe!.ProbeAsync(path);
            vm.SourcePath = path;
            vm.FileName = Path.GetFileName(path);
            vm.DurationUs = _media.DurationUs;
            vm.FrameRate = _media.FrameRate;
            vm.FrameStartPhaseUs = _media.VideoStartTimeUs;
            vm.ZoomFactor = 1.0;   // start fully zoomed out - whole video visible
            vm.MediaInfoLine =
                $"{_media.FrameRate.AsDouble:0.##} fps · {_media.Width}×{_media.Height} · {_media.VideoCodec}";

            // Try sidecar hydration first.
            var loadResult = _store!.Load(path, _media);
            if (loadResult.Project is { } proj && loadResult.Status is SidecarLoadStatus.Loaded or SidecarLoadStatus.LoadedWithMtimeWarning)
            {
                HydrateFromSidecar(vm, proj);
                if (loadResult.Status == SidecarLoadStatus.LoadedWithMtimeWarning)
                {
                    // TODO surface in status bar - for now, MessageBox keeps the soft-warning behavior visible.
                    vm.StatusKind = StatusKind.Warning;
                    vm.StatusOverride = loadResult.Message ?? "Source modified time changed; verify project state.";
                }
            }
            else
            {
                if (loadResult.Status is SidecarLoadStatus.Corrupt or SidecarLoadStatus.FingerprintMismatch)
                {
                    vm.Banner = new BannerInfo(StatusKind.Warning,
                        "Project sidecar was not loaded.",
                        loadResult.Message ?? "Falling back to embedded chapters.",
                        Array.Empty<BannerAction>());
                }
                vm.StatusOverride = "Importing chapters…";
                await HydrateFromChaptersAsync(vm, path, _media);
            }

            vm.CommandStack.Clear();
            vm.IsDirty = false;
            // Ensure Pane A starts 1 frame behind Pane B on initial load
            // (PlayheadUs = 0 means Pane A wants frame −1 → no-frame overlay).
            UpdateNoFrameOverlay(vm);

            if (vm.ShowWaveform)
                StartWaveformLoad(vm, path, _media);
            else
                CancelWaveformLoad(vm, clearPeaks: true);

            if (vm.ShowThumbnails)
                StartThumbnailLoad(vm, path, _media);
            else
                CancelThumbnailLoad(vm, clearThumbnails: true);

            // Clear our transient open-time status only if it's still showing -
            // a sidecar warning or other update set between the probe and now
            // takes precedence and must not be wiped.
            if (vm.StatusOverride is "Probing media…" or "Importing chapters…")
                vm.StatusOverride = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelWaveformLoad(MainViewModel vm, bool clearPeaks)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        if (clearPeaks) vm.WaveformPeaks = null;
    }

    private void StartWaveformLoad(MainViewModel vm, string path, MediaInfo media)
    {
        // Cancel any previous extraction (user opened another file before this one finished).
        CancelWaveformLoad(vm, clearPeaks: true);
        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;

        if (media.PrimaryAudioIndex < 0) return;

        _waveform ??= new WaveformService(_ffmpeg!);
        var svc = _waveform;
        var audioIdx = media.PrimaryAudioIndex;
        var duration = media.DurationUs;

        _ = Task.Run(async () =>
        {
            try
            {
                // Let open/hydration/rendering finish before starting a full-file
                // ffmpeg read. Cached waveforms return quickly; misses happen off
                // the UI thread and below normal priority inside the service.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, ct);
                var peaks = await svc.GetOrExtractPeaksAsync(path, audioIdx, duration, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested && vm.ShowWaveform) vm.WaveformPeaks = peaks;
                });
            }
            catch (OperationCanceledException) { /* superseded by a newer open */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[waveform] extraction failed: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ct.IsCancellationRequested && vm.ShowWaveform)
                    {
                        vm.StatusKind = StatusKind.Warning;
                        vm.StatusOverride = "Waveform failed to load.";
                    }
                });
            }
        }, ct);
    }

    private void CancelThumbnailLoad(MainViewModel vm, bool clearThumbnails)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
        if (clearThumbnails) vm.Thumbnails.Clear();
    }

    private void StartThumbnailLoad(MainViewModel vm, string path, MediaInfo media)
    {
        CancelThumbnailLoad(vm, clearThumbnails: true);
        if (_ffmpeg is null) return;
        _thumbnails ??= new ThumbnailService(_ffmpeg);
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;
        var svc = _thumbnails;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var thumb in svc.GetOrCreateStripAsync(path, media.DurationUs, ct: ct)
                    .ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !vm.ShowThumbnails) return;
                        var existing = vm.Thumbnails.FirstOrDefault(t => t.Index == thumb.Index);
                        if (existing is not null) vm.Thumbnails.Remove(existing);
                        vm.Thumbnails.Add(thumb);
                    });
                }
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[thumbnails] extraction failed: {ex.Message}");
            }
        }, ct);
    }

    private static void HydrateFromSidecar(MainViewModel vm, AdTrimProject proj)
    {
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();

        // Add bookends + persisted internal splits.
        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
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
            });
        }
        vm.Markers.Add(new Split { TimeUs = vm.DurationUs, Confidence = null, Confirmed = true, Label = "End" });

        foreach (var id in proj.ExcludedSegmentIds) vm.ExcludedSegmentIds.Add(id);
        vm.RebuildSegmentsFromSplits();
    }

    private async Task HydrateFromChaptersAsync(MainViewModel vm, string path, MediaInfo media)
    {
        var chapters = await _chapters!.ImportAsync(path, media.DurationUs);
        vm.ClearSelection();
        vm.Markers.Clear();
        vm.Segments.Clear();
        vm.ExcludedSegmentIds.Clear();

        vm.Markers.Add(new Split { TimeUs = 0, Confidence = null, Confirmed = true, Label = "Start" });
        foreach (var c in chapters)
        {
            vm.Markers.Add(new Split
            {
                TimeUs = c.TimeUs,
                Source = SplitSource.Chapter,
                Confidence = null,
                Confirmed = false,
            });
        }
        vm.Markers.Add(new Split { TimeUs = media.DurationUs, Confidence = null, Confirmed = true, Label = "End" });
        vm.RebuildSegmentsFromSplits();
    }

    private void OnAutosaveTick(object? sender, EventArgs e)
    {
        _autosaveTimer.Stop();
        if (DataContext is not MainViewModel vm) return;
        if (_store is null || _media is null || vm.SourcePath is null) return;
        if (!vm.IsDirty) return;

        try
        {
            var fp = ProjectStore.FingerprintOf(vm.SourcePath, _media.DurationUs);
            var splits = vm.Markers
                .Where(m => !m.IsBookend)
                .Select(m => new PersistedSplit(m.Id, m.TimeUs, m.Source, m.OriginalTimeUs, m.Confidence, m.Confirmed))
                .ToList();
            var proj = new AdTrimProject(
                SchemaVersion: AdTrimProject.CurrentSchemaVersion,
                SourcePath: vm.SourcePath,
                Fingerprint: fp,
                Media: _media,
                Splits: splits,
                ExcludedSegmentIds: vm.ExcludedSegmentIds.ToList(),
                SidecarLocation: SidecarLocation.NextToSource);
            _store.Save(proj);
            vm.IsDirty = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[autosave] failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // Audition playback
    // -------------------------------------------------------------------

    private long? _auditionTargetEndUs;
    private long? _auditionRestoreUs;
    // Design intent: brief pause/visual cue at the boundary. The pause IS the
    // visual cue - when the playhead crosses the split's timestamp the video
    // freezes for ~300ms on that exact frame, then resumes. No marker pulse
    // animation needed: the freeze on the boundary frame is what the user
    // actually wants to evaluate.
    private long? _auditionSplitUs;
    private bool _auditionBoundaryHandled;
    private DispatcherTimer? _auditionResumeTimer;
    private const int AuditionBoundaryPauseMs = 300;

    private void StartAudition()
    {
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        // If a prior audition's boundary pause is still pending (rare - only
        // possible if the user re-triggers audition during the 300ms freeze),
        // clear it so it doesn't fire into the new pass.
        _auditionResumeTimer?.Stop();
        _auditionResumeTimer = null;
        var t = vm.SelectedMarker.TimeUs;
        var frameUs = FrameDurationUs(vm);
        _auditionRestoreUs = vm.PlayheadUs;
        _auditionTargetEndUs = t + AuditionWindowUs;
        _auditionSplitUs = t;
        _auditionBoundaryHandled = false;
        _playUntilUs = null;
        var startB = Math.Max(0, t - AuditionWindowUs);
        var startA = Math.Max(0, startB - frameUs);
        _previewAfter.SeekUsExact(startB);
        _previewBefore.SeekUsExact(startA);
        _previewAfter.Play();
        _previewBefore.Play();
    }

    private void EndAudition()
    {
        if (_auditionTargetEndUs is null) return;
        _previewAfter.Pause();
        _previewBefore.Pause();
        if (_auditionRestoreUs is { } restore && DataContext is MainViewModel vm)
            SeekTo(vm, restore);
        _auditionTargetEndUs = null;
        _auditionRestoreUs = null;
        _auditionSplitUs = null;
        _auditionBoundaryHandled = false;
        _auditionResumeTimer?.Stop();
        _auditionResumeTimer = null;
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
        if (DataContext is not MainViewModel vm) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.Escape)
        {
            if (_auditionTargetEndUs is not null) EndAudition();
            else if (vm.IsBusy && vm.BusyOperation == "Refining") CancelRefine();
            e.Handled = true;
            return;
        }

        if (vm.IsBusy)
        {
            vm.StatusKind = StatusKind.Warning;
            vm.StatusOverride = $"{vm.BusyOperation ?? "Operation"} in progress.";
            e.Handled = true;
            return;
        }

        // Undo / redo
        if (ctrl && e.Key == Key.Z) { vm.CommandStack.Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (shift && e.Key == Key.Z))) { vm.CommandStack.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { OnAutosaveTick(null, EventArgs.Empty); e.Handled = true; return; }
        if (!ctrl && e.Key == Key.E) { OnExport(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { OnOpenFile(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.W) { OnCloseProject(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.R) { OnRefineAll(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.C) { OnConfirmAllRemaining(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D0) { OnFitTimeline(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { OnZoomIn(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { OnZoomOut(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (!ctrl && e.Key == Key.R && vm.SelectedMarker is not null)
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
        if (e.Key == Key.M && !ctrl && vm.SelectedMarker is not null)
        {
            OnMoveSelectedToPlayhead(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Confirm split (C)
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
            vm.CommandStack.Execute(new ToggleExcludedCommand(seg));
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
                vm.CommandStack.Execute(new ToggleExcludedCommand(delSeg));
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

        // Next unconfirmed (N)
        if (e.Key == Key.N && !ctrl)
        {
            var next = vm.NextUnconfirmedAfter(vm.PlayheadUs);
            if (next is not null) vm.SelectMarker(next);
            e.Handled = true;
            return;
        }

        // Audition preview (P)
        if (e.Key == Key.P && !ctrl && vm.SelectedMarker is not null)
        {
            StartAudition();
            e.Handled = true;
            return;
        }

        // Play/pause (Space)
        if (e.Key == Key.Space)
        {
            PlayPauseBoth();
            e.Handled = true;
            return;
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
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
            var work = SystemParameters.WorkArea;
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

    private void OnCloseProject(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (BlockProjectSwitchForExport(vm)) return;
        CancelWaveformLoad(vm, clearPeaks: true);
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
        vm.FileName = null;
        vm.DurationUs = 0;
        vm.MediaInfoLine = "";
        vm.PlayheadUs = 0;
        vm.Banner = null;
        vm.ClearStatus();
        vm.IsDirty = false;
        _media = null;
        VideoPlaceholder.Visibility = Visibility.Visible;
        MpvViewAfter.Visibility = Visibility.Collapsed;
        MpvViewBefore.Visibility = Visibility.Collapsed;
        NoFrameOverlay.Visibility = Visibility.Collapsed;
    }

    private bool BlockProjectSwitchForExport(MainViewModel vm)
    {
        if (_activeExportDialog is null) return false;
        _activeExportDialog.Show();
        _activeExportDialog.Activate();
        vm.Banner = new BannerInfo(StatusKind.Warning,
            "Export dialog is active.",
            _activeExportDialog.IsExportInFlight
                ? "Cancel or finish the export before opening or closing a project."
                : "Close the export dialog before opening or closing a project.",
            Array.Empty<BannerAction>());
        return true;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsBusy) vm.CommandStack.Undo();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsBusy) vm.CommandStack.Redo();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ZoomFactor = Math.Clamp(vm.ZoomFactor * 1.25, 0.25, 32.0);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ZoomFactor = Math.Clamp(vm.ZoomFactor * 0.8, 0.25, 32.0);
    }

    private void OnFitTimeline(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ZoomFactor = 1.0;
    }

    private void OnAddSplitAtPlayhead(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy || vm.DurationUs <= 0) return;
        AddSplitAtPlayhead(vm);
    }

    private void AddSplitAtPlayhead(MainViewModel vm)
    {
        var timeUs = vm.PlayheadUs;
        var minDelta = FrameDurationUs(vm);
        if (!vm.Markers.Any(m => Math.Abs(m.TimeUs - timeUs) < minDelta))
            vm.CommandStack.Execute(new AddSplitCommand(vm, timeUs, SplitSource.Manual));
    }

    private void OnConfirmAllRemaining(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy) return;
        var targets = vm.Markers.Where(m => !m.IsBookend && !m.Confirmed).ToList();
        if (targets.Count > 0) vm.CommandStack.Execute(new SetConfirmedCommand(targets, confirmed: true));
    }

    private void OnAutoConfirmHighConfidence(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsBusy) return;
        var targets = vm.Markers
            .Where(m => !m.IsBookend && !m.Confirmed && m.Confidence == Confidence.High)
            .ToList();
        if (targets.Count > 0) vm.CommandStack.Execute(new SetConfirmedCommand(targets, confirmed: true));
    }

    private void OnJumpNextUnconfirmed(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var next = vm.NextUnconfirmedAfter(vm.PlayheadUs);
        if (next is not null) vm.SelectMarker(next);
    }

    private void OnPreviousSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var prev = vm.Markers.Where(m => m.TimeUs < vm.PlayheadUs).OrderByDescending(m => m.TimeUs).FirstOrDefault();
        if (prev is not null) SeekTo(vm, prev.TimeUs);
    }

    private void OnNextSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var next = vm.Markers.Where(m => m.TimeUs > vm.PlayheadUs).OrderBy(m => m.TimeUs).FirstOrDefault();
        if (next is not null) SeekTo(vm, next.TimeUs);
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
            _previewBefore.StepFrameForward();
        }
        else
        {
            _previewAfter.StepFrameBack();
            _previewBefore.StepFrameBack();
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // A dialog is already open (configuring or hidden-but-running). Bring
        // it back rather than spawning a parallel instance.
        if (_activeExportDialog is not null)
        {
            _activeExportDialog.Show();
            _activeExportDialog.Activate();
            return;
        }

        if (vm.IsBusy) return;
        if (_ffmpeg is null)
        {
            vm.Banner = new BannerInfo(StatusKind.Danger,
                "FFmpeg is not available.",
                "See binaries/README.md for install instructions.",
                Array.Empty<BannerAction>());
            return;
        }

        var encoder = new Encoders.LibX264EncoderStrategy();
        var service = new ExportService(_ffmpeg, encoder);

        // Status-bar progress sink. Tee'd to the dialog so the dialog's
        // progress view AND the main-window status bar update together -
        // the latter keeps working when the user hides the dialog via
        // "Run in background."
        var statusBarProgress = new Progress<ExportProgress>(p =>
        {
            vm.StatusOverride = p.Message;
            vm.StatusKind = p.Phase == ExportPhase.Failed ? StatusKind.Danger : StatusKind.Info;
            vm.ProgressPercent = p.OverallPercent;
        });

        vm.Banner = null;

        var dlg = new ExportDialog { Owner = this };
        _activeExportDialog = dlg;
        dlg.Bind(vm, _media);
        dlg.AttachExportRunner(service, statusBarProgress);
        dlg.ExportFinished += (_, _) => HandleExportFinished(vm, dlg);
        dlg.Closed += (_, _) => { if (ReferenceEquals(_activeExportDialog, dlg)) _activeExportDialog = null; };
        // Modeless: doesn't block MainWindow. Lets the user reopen on a second
        // Export click after "Run in background" hides this same instance.
        dlg.Show();
    }

    /// <summary>
    /// Post-export banner + optional sidecar deletion. Fired from the dialog's
    /// ExportFinished event regardless of whether the dialog was visible when
    /// the task completed (it re-shows itself on completion, but we still
    /// want the main-window banner in case the user immediately closes it).
    /// </summary>
    private void HandleExportFinished(MainViewModel vm, ExportDialog dlg)
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
                    "See the export dialog for details.",
                    Array.Empty<BannerAction>());
                break;

            case ExportDialogOutcome.Cancelled:
                vm.ClearStatus();
                break;
        }
    }

    /// <summary>
    /// Status-bar click handler. When an export dialog is alive (visible or
    /// hidden via "Run in background"), bring it back into view. No-op when
    /// the status text belongs to some other long-running op like Refine -
    /// those don't have a dialog to surface, so the click is harmlessly
    /// inert rather than confusing.
    /// </summary>
    private void OnStatusOverrideClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_activeExportDialog is null) return;
        _activeExportDialog.Show();
        _activeExportDialog.Activate();
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
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        var seg = vm.SelectedSegment;
        if (seg is null) return;
        vm.CommandStack.Execute(new ToggleExcludedCommand(seg));
    }

    private void OnDeleteSelectedSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) return;
        var marker = vm.SelectedMarker;
        if (marker is null || marker.IsBookend) return;
        vm.CommandStack.Execute(new DeleteSplitCommand(vm, marker));
        vm.ClearSelection();
    }

    private void OnPreviewSelectedSplit(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedMarker: not null }) StartAudition();
    }

    private void OnConfirmSelectedSplit(object sender, RoutedEventArgs e)
    {
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

    private void OnMoveSelectedToPlayhead(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        if (vm.IsBusy) return;
        var split = vm.SelectedMarker;
        if (split.Confirmed && !ConfirmUnconfirm(split)) return;
        if (split.Confirmed)
            vm.CommandStack.Execute(new ToggleConfirmedCommand(split));

        var sorted = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var idx = sorted.IndexOf(split);
        long minUs = idx > 0 ? sorted[idx - 1].TimeUs + 1 : 0;
        long maxUs = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs - 1 : vm.DurationUs;
        var candidate = FrameSnap.Clamp(vm.PlayheadUs, minUs, maxUs);
        candidate = FrameSnap.Snap(candidate, vm.FrameRate, vm.FrameStartPhaseUs);
        candidate = FrameSnap.Clamp(candidate, minUs, maxUs);
        if (candidate == split.TimeUs) return;
        vm.CommandStack.Execute(new MoveSplitCommand(vm, split, split.TimeUs, candidate));
    }

    private void NudgeSelected(int directionFrames)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        OnTimelineNudgeRequested(this, (vm.SelectedMarker, directionFrames));
    }

    private void OnTimelineAuditionRequested(object? sender, Split split)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SelectMarker(split);
        StartAudition();
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
        if (vm.IsBusy) return;
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
            long maxBound = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs : vm.DurationUs;

            vm.StatusOverride = $"Refining {i + 1} / {eligible.Count}…";
            vm.ProgressPercent = (i + 1) / (double)eligible.Count;

            RefineResult? result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await _refine.RefineOneAsync(vm.SourcePath, marker.TimeUs, minBound, maxBound, ct);
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
        if (DataContext is not MainViewModel vm || vm.SelectedMarker is null) return;
        await RefineMarkersAsync(vm, new[] { vm.SelectedMarker });
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
        var candidate = FrameSnap.Clamp(e.split.TimeUs + frameUs, minUs, maxUs);
        candidate = FrameSnap.Snap(candidate, vm.FrameRate, vm.FrameStartPhaseUs);
        candidate = FrameSnap.Clamp(candidate, minUs, maxUs);
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
