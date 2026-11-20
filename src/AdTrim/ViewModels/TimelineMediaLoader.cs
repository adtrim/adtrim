using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>
/// The timeline's waveform and filmstrip for a recording window: the shared audio pass (which ad-break
/// analysis joins, so the audio is read once) and the thumbnail loader. Full-file reads yield to
/// scrubbing and to filmstrip tiles.
/// </summary>
/// <param name="ffmpeg">FFmpeg, or null when it is not available.</param>
/// <param name="previewInFlight">True while either preview pane is seeking or loading.</param>
/// <param name="post">Runs an update on the window's thread.</param>
/// <param name="thumbnailsChanged">Redraws the filmstrip after the cached tiles are shown.</param>
public sealed class TimelineMediaLoader(Func<FfmpegRunner?> ffmpeg, Func<bool> previewInFlight, Action<Action> post,
    Action thumbnailsChanged)
{
    public AudioLevelService? AudioLevels { get; set; }
    public ThumbnailService? Thumbnails { get; set; }
    public AudioLevelPass? AudioPass { get; private set; }
    public ThumbnailLoader? ThumbnailLoader { get; private set; }

    /// <summary>Stops the recording's audio pass, if running, and clears the timeline graph.</summary>
    public void ReleaseAudioPass(MainViewModel vm)
    {
        AudioPass?.Cancel();
        AudioPass = null;
        vm.AudioLoudness = null;
    }

    /// <summary>Stops the audio pass and forgets it, keeping the graph that is shown.</summary>
    public void DropAudioPass()
    {
        AudioPass?.Cancel();
        AudioPass = null;
    }

    /// <summary>
    /// Stops an unfinished audio pass so something else can use the disk. A finished pass costs nothing and stays.
    /// </summary>
    public void DropUnfinishedAudioPass()
    {
        if (AudioPass is { Completion.IsCompleted: false } running) { running.Cancel(); AudioPass = null; }
    }

    /// <summary>As <see cref="DropUnfinishedAudioPass"/>, and waits until the pass has stopped reading.</summary>
    public async Task StopUnfinishedAudioPassAsync()
    {
        if (AudioPass is { Completion.IsCompleted: false } pass)
        {
            pass.Cancel();
            try { await pass.Completion; } catch (OperationCanceledException) { }
            AudioPass = null;
        }
    }

    /// <summary>
    /// The recording's shared audio pass. The timeline graph fills from it and ad-break analysis
    /// joins it, so the audio is read once. Reuses a running or finished pass for the same file.
    /// </summary>
    public AudioLevelPass EnsureAudioPass(MainViewModel vm, string path, MediaInfo media)
    {
        if (AudioPass is { } current && current.SourcePath == path
            && !current.Completion.IsCanceled && !current.Completion.IsFaulted) return current;
        ReleaseAudioPass(vm);
        AudioLevels ??= new AudioLevelService(ffmpeg()!, AudioLevelService.DefaultCacheRoot);
        AudioLevelPass? pass = null;
        pass = AudioLevels.Start(path, media, WaitForFullReadTurnAsync, partial => post(() =>
        {
            if (ReferenceEquals(AudioPass, pass)) vm.AudioLoudness = partial;
        }));
        AudioPass = pass;
        _ = ShowAudioLevelsAsync(vm, pass);
        return pass;
    }

    private async Task ShowAudioLevelsAsync(MainViewModel vm, AudioLevelPass pass)
    {
        try
        {
            var levels = await pass.Completion;
            if (ReferenceEquals(AudioPass, pass)) vm.AudioLoudness = levels.Loudness;
        }
        catch (OperationCanceledException) { /* superseded, closed or paused for export */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[audio levels] measurement failed: {ex.Message}");
            if (ReferenceEquals(AudioPass, pass) && vm.ShowWaveform)
            {
                vm.StatusKind = StatusKind.Warning;
                vm.StatusOverride = "Waveform failed to load.";
            }
        }
    }

    // Recordings usually sit on a spinning disk, where audio pass reads would
    // otherwise compete with every scrub seek.
    public async ValueTask WaitForPreviewIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            bool inFlight = previewInFlight();
            var sinceSeek = Environment.TickCount64 - MpvPreviewViewModel.LastSeekActivityMs;
            if (!AudioLevelService.ShouldYieldToSeeks(sinceSeek, inFlight)) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // Full-file reads (the waveform pass, analysis picture passes) wait for scrubbing and for
    // filmstrip tiles: tiles are a few seeks and far more visible, so they go first.
    public async ValueTask WaitForFullReadTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            await WaitForPreviewIdleAsync(ct).ConfigureAwait(false);
            if (ThumbnailLoader is not { IsBusy: true }) return;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    public void RequestThumbnails(IReadOnlyList<long> times) => ThumbnailLoader?.Request(times);

    public void CancelThumbnailLoad(MainViewModel vm, bool clearThumbnails)
    {
        ThumbnailLoader?.Cancel();
        ThumbnailLoader = null;
        if (clearThumbnails) vm.ThumbnailTiles = ThumbnailStrip.Empty;
    }

    /// <summary>Shows the recording's cached tiles at once; the timeline then asks for the
    /// on-screen tiles it lacks (<see cref="Controls.TimelineView.ThumbnailsNeeded"/>).</summary>
    public void StartThumbnailLoad(MainViewModel vm, string path, MediaInfo media)
    {
        CancelThumbnailLoad(vm, clearThumbnails: true);
        if (ffmpeg() is not { } runner) return;
        Thumbnails ??= new ThumbnailService(runner);
        var svc = Thumbnails;
        var duration = media.DurationUs;
        var cached = svc.LoadCached(path, duration);
        ThumbnailLoader? loader = null;
        loader = new ThumbnailLoader(cached.Times, time => ThumbnailService.Snap(time, duration),
            (times, ct) => svc.ExtractAsync(path, duration, times, ct), WaitForPreviewIdleAsync,
            tiles => post(() =>
            {
                if (ReferenceEquals(ThumbnailLoader, loader)) vm.ThumbnailTiles = vm.ThumbnailTiles.With(tiles);
            }));
        ThumbnailLoader = loader;
        vm.ThumbnailTiles = cached;
        thumbnailsChanged();
    }
}
