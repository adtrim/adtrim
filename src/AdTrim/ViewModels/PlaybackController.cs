using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>What the playback controller needs from the recording window that shows the two panes.</summary>
public interface IPlaybackScreen
{
    /// <summary>The editor the window shows now.</summary>
    MainViewModel? Current { get; }
    /// <summary>The open recording's media, or null.</summary>
    MediaInfo? Media { get; }
    FfmpegRunner? Ffmpeg { get; }
    /// <summary>The window is closing; nothing may start.</summary>
    bool Closing { get; }
    /// <summary>The window is switching recordings.</summary>
    bool Switching { get; }
    /// <summary>Ad-break analysis is running; playback stays available while it does.</summary>
    bool Analyzing { get; }
    void Notice(MainViewModel vm, StatusKind kind, string message);
    /// <summary>Shows or hides the "no frame" overlay on the before pane for the playhead.</summary>
    void UpdateNoFrameOverlay(MainViewModel vm);
    /// <summary>The before pane could not show the preceding frame.</summary>
    void ShowFrameFailure(MainViewModel vm, string status);
}

/// <summary>
/// The two preview panes of a recording window and everything that moves them: seeking, play and
/// pause, frame steps, the J/K/L shuttle and rewind, keeping the before pane one frame behind,
/// recovering a preceding frame the player cannot find, and reopening the recording.
/// Pane B (<see cref="After"/>) drives the playhead; pane A (<see cref="Before"/>) follows one frame behind.
/// </summary>
public sealed class PlaybackController(IPlaybackScreen screen)
{
    public MpvPreviewViewModel After { get; } = new();
    public MpvPreviewViewModel Before { get; } = new();

    /// <summary>Playback pauses when pane B reaches this time (Play from a marker to the next).</summary>
    public long? PlayUntilUs { get; set; }

    public static long FrameDurationUs(MainViewModel vm)
    {
        var fps = vm.FrameRate.AsDouble;
        return fps <= 0 ? 33_366L : (long)Math.Round(1_000_000.0 / fps);
    }

    // ---- Seeking ----

    /// <summary>
    /// Seek both panes to bracket the requested time and immediately update vm.PlayheadUs so chained
    /// keystrokes accumulate. Pane B lands on <paramref name="timeUs"/>; pane A on timeUs − 1 frame.
    /// </summary>
    public void SeekTo(MainViewModel vm, long timeUs, bool exact = true)
    {
        _frameRecovery?.Cancel();
        _failedFrameRecovery = null;
        if (vm.IsCollapsed)
        {
            if (vm.KeptTimeline.DurationUs == 0) return;
            var output = vm.KeptTimeline.ToOutput(timeUs);
            vm.PlayheadUs = vm.KeptTimeline.ToSource(output);
            After.SeekUsExact(output);
            return;
        }
        timeUs = Math.Max(0, Math.Min(vm.DurationUs, timeUs));
        vm.PlayheadUs = timeUs;
        var frameUs = FrameDurationUs(vm);
        var beforeUs = Math.Max(0, timeUs - frameUs);
        if (exact)
        {
            After.SeekUsExact(timeUs);
            Before.SeekUsExact(beforeUs);
        }
        else
        {
            After.SeekUs(timeUs);
            Before.SeekUs(beforeUs);
        }
    }

    // ---- Play, pause, frame steps ----

    /// <summary>
    /// Toggle play/pause for both panes. On play-start, re-seek Pane A to
    /// exactly Pane B's position − 1 frame so playback begins synchronized;
    /// a few frames of drift during the playback itself is acceptable per
    /// the design. On pause, just pause both - the seek-while-paused path
    /// (<see cref="SeekTo"/>) keeps them aligned during scrubbing.
    /// </summary>
    public void PlayPauseBoth()
    {
        if (screen.Current is not { } vm) return;
        ResetShuttle();
        if (vm.IsBusy && !screen.Analyzing) return;
        // "Paused." or "Playing 2×" from the shuttle no longer describes what Space just did.
        if (_shuttleNotice is not null && vm.StatusOverride == _shuttleNotice) vm.ClearStatus();
        _shuttleNotice = null;
        if (vm.IsCollapsed)
        {
            if (vm.KeptTimeline.DurationUs > 0 && !After.IsLoading) After.PlayPause();
            return;
        }
        if (After.IsPlaying)
        {
            After.Pause();
            Before.Pause();
            PlayUntilUs = null;
        }
        else
        {
            // Re-sync before playback starts: Pane B at playhead, Pane A at
            // playhead − 1 frame. Both exact so we land on real frames, not
            // the nearest preceding keyframe.
            var frameUs = FrameDurationUs(vm);
            After.SeekUsExact(vm.PlayheadUs);
            Before.SeekUsExact(Math.Max(0, vm.PlayheadUs - frameUs));
            After.Play();
            Before.Play();
            PlayUntilUs = null;
        }
    }

    public void StepFrame(MainViewModel vm, int direction)
    {
        if (vm.IsBusy) return;
        if (After.IsPlaying) { After.Pause(); Before.Pause(); }
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
            After.StepFrameForward();
            if (!vm.IsCollapsed) Before.StepFrameForward();
        }
        else
        {
            After.StepFrameBack();
            if (!vm.IsCollapsed) Before.StepFrameBack();
        }
    }

    // ---- J / K / L shuttle ----

    // Positive: forward playback rate; negative: rewind rate.
    private int _shuttle;
    private DispatcherTimer? _rewindTimer;
    private const int RewindTickMs = 100;
    private string? _shuttleNotice;

    private void ShuttleNotice(MainViewModel vm, string message)
    {
        _shuttleNotice = message;
        screen.Notice(vm, StatusKind.Info, message);
    }

    /// <summary>Positive: forward playback rate; negative: rewind rate; 0: neither.</summary>
    public int ShuttleRate => _shuttle;
    public DispatcherTimer? RewindTimer => _rewindTimer;

    public void Shuttle(MainViewModel vm, Key key)
    {
        if (vm.IsBusy && !screen.Analyzing) return;
        if (key == Key.K)
        {
            ResetShuttle();
            After.Pause();
            Before.Pause();
            ShuttleNotice(vm, "Paused.");
            return;
        }
        if (key == Key.L)
        {
            bool rewinding = _shuttle < 0;
            StopRewind();
            if (After.IsPlaying && !rewinding)
                _shuttle = Math.Min(4, Math.Max(1, _shuttle) * 2); // Space playback counts as 1×
            else
            {
                if (!After.IsPlaying) PlayPauseBoth();
                _shuttle = 1;
            }
            After.SetSpeed(_shuttle);
            Before.SetSpeed(_shuttle);
            ShuttleNotice(vm, $"Playing {_shuttle}×. K pauses.");
            return;
        }
        // J: rewind by stepping the playhead backward (mpv has no cheap reverse playback).
        if (After.IsPlaying) { After.Pause(); Before.Pause(); }
        After.SetSpeed(1);
        Before.SetSpeed(1);
        _shuttle = _shuttle < 0 ? Math.Max(-4, _shuttle * 2) : -1;
        _rewindTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(RewindTickMs), DispatcherPriority.Input, (_, _) => RewindTick(),
            Dispatcher.CurrentDispatcher);
        _rewindTimer.Start();
        ShuttleNotice(vm, $"Rewinding {-_shuttle}×. K stops.");
    }

    public void RewindTick()
    {
        if (screen.Closing || screen.Switching || screen.Current is not { IsFileLoaded: true, IsExportScreen: false } vm
            || (vm.IsBusy && !screen.Analyzing) || _shuttle >= 0)
        { StopRewind(); return; }
        long step = RewindTickMs * 1_000L * -_shuttle;
        long position = vm.IsCollapsed ? vm.TimelinePositionUs : vm.PlayheadUs;
        if (position <= 0) { ResetShuttle(); ShuttleNotice(vm, "Reached the start."); return; }
        long target = Math.Max(0, position - step);
        SeekTo(vm, vm.IsCollapsed ? vm.KeptTimeline.ToSource(target) : target, exact: false);
    }

    public void StopRewind()
    {
        _rewindTimer?.Stop();
        if (_shuttle < 0) _shuttle = 0;
    }

    public void ResetShuttle()
    {
        StopRewind();
        if (_shuttle > 1) { After.SetSpeed(1); Before.SetSpeed(1); }
        _shuttle = 0;
    }

    // ---- Keeping the before pane one frame behind ----

    // The advertised rate can differ from decoded frame spacing. Let mpv
    // find the predecessor instead of repeatedly seeking half a real frame back.
    private CancellationTokenSource? _frameRecovery;
    private (string? Path, long Time)? _failedFrameRecovery;

    public bool FrameRecoveryPending => _frameRecovery is not null;

    public void CancelFrameRecovery() => _frameRecovery?.Cancel();

    public void MaybeResyncPanes(MainViewModel vm)
    {
        if (vm.IsCollapsed || After.IsLoading || Before.IsLoading
            || After.IsSeeking || Before.IsSeeking) return;
        if (After.IsPlaying || Before.IsPlaying) return;
        var target = After.PositionUs;
        if (target <= vm.FrameStartPhaseUs) return;
        if (Before.PreviousFrameTargetUs == target)
        {
            if (Before.PositionUs < target - 2)
            {
                screen.UpdateNoFrameOverlay(vm);
                return;
            }
            if (Before.PositionUs >= target - 2 && _frameRecovery is null)
            {
                if (_failedFrameRecovery == (vm.SourcePath, target))
                    screen.ShowFrameFailure(vm, "Could not load the preceding frame. Try seeking again.");
                else RecoverPreviousFrameAsync(vm, target);
            }
            return;
        }
        _frameRecovery?.Cancel();
        Before.SeekPreviousFrame(target);
    }

    private async void RecoverPreviousFrameAsync(MainViewModel vm, long target)
    {
        if (vm.SourcePath is not { } path || screen.Ffmpeg is not { } ffmpeg) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _frameRecovery = cancellation;
        bool StillCurrent() => !screen.Closing && !cancellation.IsCancellationRequested && ReferenceEquals(screen.Current, vm)
            && vm.SourcePath == path && !vm.IsCollapsed
            && !After.IsPlaying && !After.IsSeeking && After.PositionUs == target;
        try
        {
            var previous = await new RefineService(ffmpeg).FindPreviousFrameAsync(path, target, cancellation.Token);
            if (!StillCurrent()) return;
            if (previous is { } frame)
            {
                _failedFrameRecovery = (path, target);
                Before.SeekPreviousFrame(target, Math.Max(0, frame - 1));
            }
            else
            {
                _failedFrameRecovery = (path, target);
                screen.ShowFrameFailure(vm, "No preceding frame found within 60 seconds of this position.");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            if (StillCurrent())
            {
                _failedFrameRecovery = (path, target);
                screen.ShowFrameFailure(vm, "Could not load the preceding frame. Try seeking again.");
            }
        }
        finally
        {
            if (ReferenceEquals(_frameRecovery, cancellation)) _frameRecovery = null;
            if (!screen.Closing && ReferenceEquals(screen.Current, vm)) MaybeResyncPanes(vm);
        }
    }

    // ---- Reopening ----

    /// <summary>
    /// Opens the recording again at <paramref name="position"/>, as after a source comment rewrote it.
    /// The panes open one at a time: an AMD driver crashed creating both video outputs at once.
    /// </summary>
    public async Task ReopenAsync(MainViewModel vm, string path, long position)
    {
        if (vm.SourcePath != path) return;
        try
        {
            if (vm.IsCollapsed)
            {
                if (vm.KeptTimeline.DurationUs > 0)
                    After.Open(vm.KeptTimeline.ToMpvEdl(path, screen.Media?.TimelineStartTimeUs ?? 0), vm.KeptTimeline.ToOutput(position), buffered: true);
            }
            else
            {
                After.Open(path, position);
                for (int i = 0; i < 200 && After.IsLoading; i++) await Task.Delay(25);
                if (vm.SourcePath != path) return;
                Before.Open(path, Math.Max(0, position - FrameDurationUs(vm)));
            }
            if (screen.Media is { } media)
            {
                After.SelectAudioStream(media.PrimaryAudioIndex);
                Before.SelectAudioStream(media.PrimaryAudioIndex);
            }
        }
        catch (InvalidOperationException ex)
        {
            vm.Banner = new BannerInfo(StatusKind.Warning, "The preview could not reopen the recording.", ex.Message, Array.Empty<BannerAction>());
        }
    }
}
