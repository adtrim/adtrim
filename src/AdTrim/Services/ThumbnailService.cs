using System.Globalization;
using System.IO;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>
/// Timeline filmstrip frames: for each requested time, the nearest keyframe at or before it,
/// as a small JPEG cached per recording. One ffmpeg run fetches a whole batch, seeking straight
/// to each keyframe, so a strip costs a few dozen seeks rather than a read of the file.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>Tile height in pixels: the 56 px lane at 200% display scaling.</summary>
    public const int TileHeight = 112;
    public const int CacheVersion = 2;
    /// <summary>Requests snap to whole seconds; a keyframe pick is coarser than that anyway.</summary>
    public const long TimeStepUs = 1_000_000;

    private readonly FfmpegRunner? _runner;
    private readonly string _cacheRoot;

    public ThumbnailService(FfmpegRunner? runner, string? cacheRoot = null)
    {
        _runner = runner;
        _cacheRoot = cacheRoot ?? DefaultCacheRoot;
    }

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdTrim",
        "thumbnails");

    /// <summary>Folder-name suffix of the format this version writes (see <see cref="MediaCacheCleanup"/>).</summary>
    public static string CurrentFolderSuffix => $"_h{TileHeight}_v{CacheVersion}";

    public static string CacheDirectoryFor(string sourcePath, long durationUs, string cacheRoot, int cacheVersion = CacheVersion)
        => Path.Combine(cacheRoot, $"{FrameCacheService.ComputeSourceHash(sourcePath)}_d{durationUs}_h{TileHeight}_v{cacheVersion}");

    /// <summary>The cache time a request for <paramref name="timeUs"/> is fetched at.</summary>
    public static long Snap(long timeUs, long durationUs)
    {
        long last = Math.Max(0, (durationUs - 1) / TimeStepUs * TimeStepUs);
        return Math.Clamp((timeUs + TimeStepUs / 2) / TimeStepUs * TimeStepUs, 0, last);
    }

    /// <summary>Tiles already on disk for this recording.</summary>
    public ThumbnailStrip LoadCached(string sourcePath, long durationUs)
    {
        var dir = CacheDirectoryFor(sourcePath, durationUs, _cacheRoot);
        if (!Directory.Exists(dir)) return ThumbnailStrip.Empty;
        MediaCacheCleanup.Touch(dir);
        var tiles = new List<(long, string)>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.jpg"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var time))
                tiles.Add((time, path));
        return ThumbnailStrip.Empty.With(tiles);
    }

    /// <summary>ffmpeg opens its inputs one after another and each re-reads the recording's index
    /// (about 10 MB for a two-hour show), so a batch is split across a few runs at once.</summary>
    public const int ParallelRuns = 4;

    /// <summary>Fetches one tile per time. Returns the tiles written, in request order; a time
    /// with no decodable keyframe is simply absent.</summary>
    public async Task<IReadOnlyList<(long TimeUs, string Path)>> ExtractAsync(string sourcePath, long durationUs,
        IReadOnlyList<long> timesUs, CancellationToken ct = default)
    {
        var runner = _runner ?? throw new InvalidOperationException("FFmpeg is required to make thumbnails.");
        if (timesUs.Count == 0) return [];
        var dir = CacheDirectoryFor(sourcePath, durationUs, _cacheRoot);
        Directory.CreateDirectory(dir);
        int perRun = (timesUs.Count + ParallelRuns - 1) / ParallelRuns;
        var runs = await Task.WhenAll(timesUs.Chunk(perRun).Select(chunk => ExtractRunAsync(runner, sourcePath, dir, chunk, ct))).ConfigureAwait(false);
        return runs.SelectMany(r => r).ToList();
    }

    private static async Task<IReadOnlyList<(long TimeUs, string Path)>> ExtractRunAsync(FfmpegRunner runner, string sourcePath,
        string dir, IReadOnlyList<long> timesUs, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var time in timesUs)
            args.AddRange(["-skip_frame", "nokey", "-ss", (time / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture),
                "-noaccurate_seek", "-an", "-sn", "-dn", "-i", sourcePath]);
        var outputs = new List<(long Time, string Temp, string Final)>();
        for (int i = 0; i < timesUs.Count; i++)
        {
            var final = Path.Combine(dir, timesUs[i].ToString(CultureInfo.InvariantCulture) + ".jpg");
            var temp = final + ".tmp.jpg";
            outputs.Add((timesUs[i], temp, final));
            // Square the pixels first so anamorphic SD keeps its display shape.
            args.AddRange(["-map", $"{i}:v:0", "-frames:v", "1",
                "-vf", $"scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=-2:{TileHeight}", "-q:v", "5", temp]);
        }
        // One empty output fails the run; the tiles that were written are still good.
        await runner.RunFfmpegAsync(args, ct).ConfigureAwait(false);

        var written = new List<(long, string)>();
        foreach (var (time, temp, final) in outputs)
        {
            try
            {
                if (File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    File.Move(temp, final, overwrite: true);
                    written.Add((time, final));
                }
                else if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException) { /* cache write failures leave the tile unfetched */ }
        }
        return written;
    }
}

/// <summary>
/// Fetches the tiles the timeline asks for, a small batch per ffmpeg run. The newest request
/// replaces anything still waiting (what's on screen now matters, not where the user was),
/// each batch first waits for playback seeks to settle, and a tile is never fetched twice.
/// </summary>
public sealed class ThumbnailLoader
{
    /// <summary>Bounds how long one batch can hold the disk once a scrub starts.</summary>
    public const int BatchSize = 8;

    private readonly Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<(long TimeUs, string Path)>>> _extract;
    private readonly Func<CancellationToken, ValueTask> _waitForIdle;
    private readonly Action<IReadOnlyList<(long TimeUs, string Path)>> _delivered;
    private readonly Func<long, long> _snap;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly HashSet<long> _known;
    private readonly HashSet<long> _attempted = new();
    private List<long> _pending = new();
    private Task _worker = Task.CompletedTask;
    private volatile bool _running;

    public ThumbnailLoader(IEnumerable<long> known, Func<long, long> snap,
        Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyList<(long TimeUs, string Path)>>> extract,
        Func<CancellationToken, ValueTask> waitForIdle, Action<IReadOnlyList<(long TimeUs, string Path)>> delivered)
    {
        _known = new HashSet<long>(known);
        _snap = snap;
        _extract = extract;
        _waitForIdle = waitForIdle;
        _delivered = delivered;
    }

    /// <summary>True while tiles are waiting or being fetched.</summary>
    public bool IsBusy => _running;

    /// <summary>Completes when the current queue drains (for tests and measurements).</summary>
    public Task Idle { get { lock (_gate) return _worker; } }

    public void Request(IEnumerable<long> timesUs)
    {
        lock (_gate)
        {
            if (_cancellation.IsCancellationRequested) return;
            var wanted = new List<long>();
            var seen = new HashSet<long>();
            foreach (var time in timesUs)
            {
                long snapped = _snap(time);
                if (seen.Add(snapped) && !_known.Contains(snapped) && !_attempted.Contains(snapped)) wanted.Add(snapped);
            }
            _pending = wanted;
            // The worker clears _running under this lock as it exits, so a request is never stranded.
            if (wanted.Count == 0 || _running) return;
            _running = true;
            _worker = Task.Run(RunAsync);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending = new();
            _cancellation.Cancel();
        }
    }

    private async Task RunAsync()
    {
        var ct = _cancellation.Token;
        try
        {
            while (true)
            {
                await _waitForIdle(ct).ConfigureAwait(false);
                List<long> batch;
                lock (_gate)
                {
                    batch = _pending.Take(BatchSize).ToList();
                    _pending = _pending.Skip(BatchSize).ToList();
                    if (batch.Count == 0) { _running = false; return; }
                    foreach (var time in batch) _attempted.Add(time);
                }
                var tiles = await _extract(batch, ct).ConfigureAwait(false);
                lock (_gate) foreach (var (time, _) in tiles) _known.Add(time);
                if (tiles.Count > 0) _delivered(tiles);
            }
        }
        catch (OperationCanceledException) { lock (_gate) _running = false; }
        catch (Exception ex)
        {
            lock (_gate) _running = false;
            System.Diagnostics.Debug.WriteLine($"[thumbnails] fetch failed: {ex.Message}");
        }
    }
}
