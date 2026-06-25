using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AdTrim.Services;

public sealed record CachedFrame(long TimeUs, string ImagePath);

/// <summary>
/// Pre-extracts small JPEGs around split points so paused seeks can show the
/// destination frame instantly while LibVLC catches up in the background.
///
/// Layout on disk: <c>%LOCALAPPDATA%\AdTrim\framecache\{sourceHash}\{startUs}_{frameTimeUs}.jpg</c>
/// where <c>frameTimeUs</c> is the FFmpeg-reported pts_time of that frame
/// (microseconds, zero-padded). Lookup is by nearest stored timestamp.
///
/// Cache strategy:
///   - Index keyed by (sourceHash, frameTimeUs) - not by splitId. Deleting
///     or moving a split doesn't orphan the cache; nearby seeks still benefit.
///   - Pre-extracts a slightly wider window (±3 s) than audition shows
///     (±2 s) so small marker drags stay within cached range.
///   - LRU eviction at a budget of <see cref="MaxBudgetBytes"/>.
///   - Identity = (size, mtime); reusing across renames is fine, contents
///     must match.
/// </summary>
public sealed class FrameCacheService
{
    private const long MaxBudgetBytes = 256L * 1024 * 1024;   // 256 MB total
    private const long WindowHalfUs = 3_000_000L;             // ±3 s extracted
    private const int FrameStrideFrames = 2;                  // every Nth frame
    private const int TargetWidth = 640;
    private const int JpegQuality = 5;                        // ffmpeg -q:v 2..31 (lower = better)

    private readonly FfmpegRunner _runner;
    private readonly string _rootDir;
    private string? _currentSourceHash;
    private readonly ConcurrentDictionary<long, string> _index = new();
    private readonly object _lruLock = new();
    private long _currentBytes;

    public FrameCacheService(FfmpegRunner runner)
    {
        _runner = runner;
        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdTrim", "framecache");
        Directory.CreateDirectory(_rootDir);
    }

    /// <summary>
    /// Hash a source's identity into a stable directory key. SHA-256 over
    /// `{absolutePath}|{size}|{mtimeUnixMs}` - content-correlated enough that
    /// a re-encode invalidates the cache automatically.
    /// </summary>
    public static string ComputeSourceHash(string sourcePath)
    {
        var fi = new FileInfo(sourcePath);
        var key = $"{Path.GetFullPath(sourcePath).ToLowerInvariant()}|{fi.Length}|" +
                  $"{new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Load on-disk index for a source. Called on file open.</summary>
    public void AttachSource(string sourcePath)
    {
        var hash = ComputeSourceHash(sourcePath);
        if (_currentSourceHash == hash) return;
        _currentSourceHash = hash;
        _index.Clear();
        _currentBytes = 0;

        var dir = SourceDir(hash);
        if (!Directory.Exists(dir)) return;

        foreach (var path in Directory.EnumerateFiles(dir, "*.jpg"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Filename format: "{startUs}_{frameTimeUs}". Parse the frame time.
            var underscoreAt = name.LastIndexOf('_');
            if (underscoreAt < 0) continue;
            if (!long.TryParse(name.AsSpan(underscoreAt + 1), out var frameTimeUs)) continue;
            _index[frameTimeUs] = path;
            _currentBytes += new FileInfo(path).Length;
        }
    }

    /// <summary>
    /// Nearest cached frame at <paramref name="targetUs"/>, or null if nothing
    /// is cached within <paramref name="toleranceUs"/>. Touches LRU order.
    /// </summary>
    public CachedFrame? Lookup(long targetUs, long toleranceUs = 200_000)
    {
        if (_index.IsEmpty) return null;
        long bestDelta = long.MaxValue;
        long bestTime = 0;
        string? bestPath = null;
        foreach (var (t, p) in _index)
        {
            var d = Math.Abs(t - targetUs);
            if (d < bestDelta) { bestDelta = d; bestTime = t; bestPath = p; }
        }
        if (bestPath is null || bestDelta > toleranceUs) return null;
        try { File.SetLastAccessTimeUtc(bestPath, DateTime.UtcNow); } catch { /* best effort */ }
        return new CachedFrame(bestTime, bestPath);
    }

    /// <summary>
    /// Prime the cache for one window around <paramref name="centerUs"/>.
    /// Re-extracts only the frames we don't already have indexed.
    /// </summary>
    public async Task PrimeWindowAsync(
        string sourcePath, long centerUs, CancellationToken ct = default)
    {
        if (_currentSourceHash is null) AttachSource(sourcePath);
        long startUs = Math.Max(0, centerUs - WindowHalfUs);
        long endUs = centerUs + WindowHalfUs;

        // Skip if the window is already substantially covered (≥ 70% of expected frames).
        var expectedFrames = (int)((endUs - startUs) / (FrameStrideFrames * 33_367));   // 30fps, every Nth frame
        var alreadyCovered = _index.Count(kv => kv.Key >= startUs && kv.Key <= endUs);
        if (alreadyCovered >= expectedFrames * 0.7) return;

        var dir = SourceDir(_currentSourceHash!);
        Directory.CreateDirectory(dir);

        // ffmpeg -ss <start> -t <dur> -i <src> -vf "select=not(mod(n\,2)),scale=640:-1,showinfo" -q:v 5 ...
        // showinfo emits pts_time per frame in stderr so we can name files by exact frame time.
        var startSec = (startUs / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var durSec = ((endUs - startUs) / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var pattern = Path.Combine(dir, $"{startUs}_%04d.jpg");

        var args = new[]
        {
            "-y", "-v", "info",
            "-ss", startSec, "-t", durSec,
            "-i", sourcePath,
            "-vf", $"select=not(mod(n\\,{FrameStrideFrames})),scale={TargetWidth}:-2,showinfo",
            "-vsync", "vfr",
            "-q:v", JpegQuality.ToString(),
            pattern,
        };
        var r = await _runner.RunFfmpegAsync(args, ct).ConfigureAwait(false);
        if (!r.Success) return;

        // Parse showinfo's `pts_time:X.XXXXXX n:Y` lines to learn the actual
        // timestamps and rename the indexed files accordingly.
        var rx = new System.Text.RegularExpressions.Regex(
            @"n:\s*(?<n>\d+)\s+pts_time:(?<t>[\d.]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        var seenIndex = 0;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(r.Stderr))
        {
            seenIndex++;
            var src = Path.Combine(dir, $"{startUs}_{seenIndex:0000}.jpg");
            if (!File.Exists(src)) continue;
            var ptsSec = double.Parse(m.Groups["t"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture);
            var frameTimeUs = startUs + (long)Math.Round(ptsSec * 1_000_000.0);
            var dst = Path.Combine(dir, $"{startUs}_{frameTimeUs}.jpg");
            try
            {
                if (File.Exists(dst)) File.Delete(src);
                else File.Move(src, dst);
                _index[frameTimeUs] = dst;
                _currentBytes += new FileInfo(dst).Length;
            }
            catch { /* swallow - extraction is best-effort */ }
        }

        EvictIfOverBudget();
    }

    /// <summary>Prime windows for several split times in the background.</summary>
    public Task PrimeManyAsync(string sourcePath, IEnumerable<long> centersUs, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            foreach (var c in centersUs)
            {
                if (ct.IsCancellationRequested) return;
                try { await PrimeWindowAsync(sourcePath, c, ct); }
                catch { /* per-split failure shouldn't abort the prime pass */ }
            }
        }, ct);
    }

    /// <summary>Drop the least-recently-accessed JPEGs until under budget.</summary>
    public void EvictIfOverBudget()
    {
        lock (_lruLock)
        {
            if (_currentBytes <= MaxBudgetBytes) return;
            var entries = _index
                .Select(kv => new { kv.Key, kv.Value, Access = SafeAccessTime(kv.Value) })
                .OrderBy(x => x.Access)
                .ToList();
            foreach (var e in entries)
            {
                if (_currentBytes <= MaxBudgetBytes * 0.8) break;
                try
                {
                    var len = new FileInfo(e.Value).Length;
                    File.Delete(e.Value);
                    _index.TryRemove(e.Key, out _);
                    _currentBytes -= len;
                }
                catch { /* fine - let LRU re-try later */ }
            }
        }
    }

    private static DateTime SafeAccessTime(string path)
    {
        try { return File.GetLastAccessTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private string SourceDir(string hash) => Path.Combine(_rootDir, hash);
}
