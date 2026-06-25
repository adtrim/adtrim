using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AdTrim.Services;
using static AdTrim.Services.LibMpv;

namespace AdTrim.ViewModels;

/// <summary>
/// MPV-backed video preview. Mirrors the surface area of the (now legacy)
/// LibVLC-based <see cref="VideoPreviewViewModel"/> so MainWindow's wiring
/// is largely unchanged: same INPC properties (<c>PositionUs</c>, <c>IsPlaying</c>),
/// same lifecycle (Initialize / Open / Play / Pause / PlayPause / SeekUs /
/// StepFrame / Dispose), same threading characteristic (events arrive on
/// mpv's background thread → caller must marshal to the UI dispatcher).
///
/// <para>The reason we're here: LibVLC's state machine (Stopped → Opening →
/// Buffering → Paused) adds 500-1200 ms per seek. mpv keeps the decoder
/// hot between seeks and lands at 50-150 ms, matching LosslessCut's feel.</para>
/// </summary>
public sealed class MpvPreviewViewModel : INotifyPropertyChanged, IDisposable
{
    private IntPtr _ctx;                      // mpv handle
    private IntPtr _wid;                      // child HWND mpv renders into
    private string? _currentPath;
    private bool _initialized;
    private CancellationTokenSource? _eventLoopCts;
    private Thread? _eventLoop;

    // Property-observe reply IDs (arbitrary but distinct).
    private const ulong ObsTimePos = 1;
    private const ulong ObsPause = 2;
    private const ulong ObsDuration = 3;
    private const ulong ObsAspect = 4;

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    private long _positionUs;
    public long PositionUs
    {
        get => _positionUs;
        private set => Set(ref _positionUs, value);
    }

    // Display aspect ratio reported by mpv (after SAR / display-aspect correction).
    // Used by the pane layout to size the HwndHost so the video frame ends
    // exactly at the bottom of its row - the timestamp chip then sits directly
    // below it instead of below the bottom letterbox.
    private double _videoAspect;
    public double VideoAspect
    {
        get => _videoAspect;
        private set => Set(ref _videoAspect, value);
    }

    private bool _fastSeek = true;
    public bool FastSeek => _fastSeek;

    public void SetFastSeek(bool enabled)
    {
        if (_fastSeek == enabled) return;
        _fastSeek = enabled;
        if (_ctx != IntPtr.Zero)
        {
            // mpv exposes a `hr-seek` property: "yes" / "no" / "always".
            // "no" = keyframe-aligned (fast), "always" = exact (slower).
            mpv_set_property_string(_ctx, "hr-seek", _fastSeek ? "no" : "yes");
        }
    }

    private bool _muted;
    public bool IsMuted => _muted;

    /// <summary>
    /// Mute / unmute audio. Used by the dual-pane preview to silence one
    /// of the two mpv instances - they're playing the same file 1 frame
    /// apart, so unmuting both would comb-filter the audio horribly.
    /// </summary>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_ctx != IntPtr.Zero)
            mpv_set_property_string(_ctx, "mute", muted ? "yes" : "no");
    }

    /// <summary>HWND-host control passes its child HWND in here BEFORE Open.</summary>
    public IntPtr Wid
    {
        get => _wid;
        set
        {
            _wid = value;
            if (_ctx != IntPtr.Zero && value != IntPtr.Zero)
                mpv_set_property_string(_ctx, "wid", value.ToInt64().ToString(CultureInfo.InvariantCulture));
        }
    }

    public void Initialize()
    {
        if (_initialized) return;
        EnsureLoaded();

        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() returned null.");

        // ---- Options that must be set BEFORE initialize ----
        // Embed into the host HWND if it's already known. If not, this will
        // be set later via the Wid property and mpv will pick it up.
        if (_wid != IntPtr.Zero)
            mpv_set_option_string(_ctx, "wid", _wid.ToInt64().ToString(CultureInfo.InvariantCulture));

        // Don't pop a separate VLC-style window when wid is provided.
        mpv_set_option_string(_ctx, "force-window", "no");
        // Hide on-screen-controller (we have our own transport bar).
        mpv_set_option_string(_ctx, "osc", "no");
        // No log spam in stderr (libmpv would otherwise write a lot).
        mpv_set_option_string(_ctx, "msg-level", "all=no");
        // Default to fast seek. Toggle via SetFastSeek for frame-precise work.
        mpv_set_option_string(_ctx, "hr-seek", _fastSeek ? "no" : "yes");
        // Keep last frame visible after seek/load instead of going black.
        mpv_set_option_string(_ctx, "keep-open", "yes");
        // Pause on load so the user sees the first frame without auto-playing.
        mpv_set_option_string(_ctx, "pause", "yes");
        // Honor a muted state set before Initialize (set by the dual-pane host
        // to silence the secondary preview before it ever produces audio).
        mpv_set_option_string(_ctx, "mute", _muted ? "yes" : "no");
        // Hardware decoding when available (huge speedup for MPEG-2 1080i).
        mpv_set_option_string(_ctx, "hwdec", "auto-safe");
        // Audio device: leave default. Video output: WPF host = `gpu` is the
        // modern default and works in embedded windows.
        mpv_set_option_string(_ctx, "vo", "gpu");

        var initRc = mpv_initialize(_ctx);
        if (initRc < 0)
        {
            mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize failed: {initRc}");
        }

        // ---- Observe properties we care about (drives INPC updates) ----
        mpv_observe_property(_ctx, ObsTimePos, "time-pos", MpvFormat.Double);
        mpv_observe_property(_ctx, ObsPause, "pause", MpvFormat.Flag);
        mpv_observe_property(_ctx, ObsDuration, "duration", MpvFormat.Double);
        mpv_observe_property(_ctx, ObsAspect, "video-params/aspect", MpvFormat.Double);

        // Start the event pump on a background thread. mpv_wait_event blocks
        // until something arrives, which is fine - the thread exits when we
        // signal cancellation.
        _eventLoopCts = new CancellationTokenSource();
        _eventLoop = new Thread(EventLoop) { IsBackground = true, Name = "mpv-event-pump" };
        _eventLoop.Start(_eventLoopCts.Token);

        _initialized = true;
    }

    private void EventLoop(object? state)
    {
        var ct = (CancellationToken)state!;
        try
        {
            while (!ct.IsCancellationRequested && _ctx != IntPtr.Zero)
            {
                var evPtr = mpv_wait_event(_ctx, 0.05);
                if (evPtr == IntPtr.Zero) continue;
                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);

                switch (ev.EventId)
                {
                    case MpvEventId.Shutdown:
                        return;

                    case MpvEventId.PropertyChange when ev.Data != IntPtr.Zero:
                    {
                        var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                        var name = UnmarshalUtf8(prop.Name);
                        HandlePropertyChange(name, prop, ev.ReplyUserdata);
                        break;
                    }

                    case MpvEventId.FileLoaded:
                    case MpvEventId.PlaybackRestart:
                    {
                        // Sync IsPlaying defensively.
                        if (mpv_get_property(_ctx, "pause", MpvFormat.Flag, out int paused) == 0)
                            IsPlaying = paused == 0;
                        break;
                    }
                }
            }
        }
        catch { /* mpv handle going away mid-tick - fine */ }
    }

    private void HandlePropertyChange(string? name, MpvEventProperty prop, ulong replyId)
    {
        if (name is null || prop.Data == IntPtr.Zero) return;
        switch (replyId)
        {
            case ObsTimePos when prop.Format == MpvFormat.Double:
            {
                var sec = Marshal.PtrToStructure<double>(prop.Data);
                if (double.IsFinite(sec))
                    PositionUs = (long)Math.Round(sec * 1_000_000.0);
                break;
            }
            case ObsPause when prop.Format == MpvFormat.Flag:
            {
                var paused = Marshal.PtrToStructure<int>(prop.Data);
                IsPlaying = paused == 0;
                break;
            }
            case ObsAspect when prop.Format == MpvFormat.Double:
            {
                var aspect = Marshal.PtrToStructure<double>(prop.Data);
                if (double.IsFinite(aspect) && aspect > 0)
                    VideoAspect = aspect;
                break;
            }
        }
    }

    public void Open(string path)
    {
        if (!_initialized) Initialize();
        _currentPath = path;
        // mpv's "loadfile" command - args are <path> [<flags>]. "replace"
        // (the default) loads + plays-or-pauses according to the `pause`
        // property, which we set to yes during Initialize.
        var rc = Command(_ctx, "loadfile", path, "replace");
        if (rc < 0) throw new InvalidOperationException($"mpv loadfile failed: {rc}");
    }

    public void Play()
    {
        if (_ctx == IntPtr.Zero) return;
        int unpause = 0;
        mpv_set_property(_ctx, "pause", MpvFormat.Flag, ref unpause);
    }

    public void Pause()
    {
        if (_ctx == IntPtr.Zero) return;
        int paused = 1;
        mpv_set_property(_ctx, "pause", MpvFormat.Flag, ref paused);
    }

    public void PlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void SeekUs(long timeUs)
    {
        if (_ctx == IntPtr.Zero) return;
        var sec = timeUs / 1_000_000.0;
        // mpv "seek <time> absolute" - seeks to absolute position in seconds.
        // Honors the current `hr-seek` setting (we configured it on init).
        Command(_ctx, "seek", sec.ToString("0.######", CultureInfo.InvariantCulture), "absolute");
    }

    /// <summary>
    /// Frame-accurate absolute seek regardless of the global <c>hr-seek</c>
    /// setting. Use for keyboard-driven seeks (`[` / `]` / arrows / `,`) so
    /// they actually advance - fast-seek (`hr-seek=no`) aligns to the nearest
    /// preceding keyframe and a +1s forward seek often lands on the same
    /// keyframe you started on, making `]` appear to do nothing.
    /// Timeline clicks still use the faster <see cref="SeekUs"/>.
    /// </summary>
    public void SeekUsExact(long timeUs)
    {
        if (_ctx == IntPtr.Zero) return;
        var sec = timeUs / 1_000_000.0;
        Command(_ctx, "seek", sec.ToString("0.######", CultureInfo.InvariantCulture), "absolute+exact");
    }

    /// <summary>
    /// Step exactly one mpv frame forward, paused. Uses mpv's native
    /// <c>frame-step</c> command - which is grid-correct for telecined or
    /// otherwise non-uniform content, where the container's
    /// <c>r_frame_rate</c> doesn't match the actual displayed frame
    /// interval.
    /// <para>Cost: <c>frame-step</c> briefly unpauses to play one frame
    /// (~16-42ms of audio). We accept this because the silent alternative
    /// - seek-by-(1/r_frame_rate) - lands between real frames on 24fps
    /// content telecined to 1080i59.94, and mpv's exact seek then snaps
    /// to the NEXT real frame, making backward step a no-op.</para>
    /// </summary>
    public void StepFrameForward()
    {
        if (_ctx == IntPtr.Zero) return;
        Command(_ctx, "frame-step");
    }

    /// <summary>
    /// Step exactly one mpv frame backward, paused. mpv decodes from the
    /// preceding keyframe each time, so this is slower than forward step -
    /// noticeable as a brief pause on long-GOP MPEG-2 sources but still
    /// fast enough to feel responsive.
    /// </summary>
    public void StepFrameBack()
    {
        if (_ctx == IntPtr.Zero) return;
        Command(_ctx, "frame-back-step");
    }

    public void Dispose()
    {
        _eventLoopCts?.Cancel();
        try { _eventLoop?.Join(1000); } catch { }
        _eventLoopCts?.Dispose();
        _eventLoopCts = null;
        if (_ctx != IntPtr.Zero)
        {
            mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
        }
        _initialized = false;
        _currentPath = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
