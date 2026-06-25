using System.IO;
using System.Runtime.CompilerServices;
using AdTrim.Models;

namespace AdTrim.Services;

public sealed class ThumbnailService
{
    public const int DefaultTileCount = 24;
    public const int TargetWidth = 320;
    public const int CacheVersion = 1;

    private readonly FfmpegRunner _runner;
    private readonly string _cacheRoot;

    public ThumbnailService(FfmpegRunner runner, string? cacheRoot = null)
    {
        _runner = runner;
        _cacheRoot = cacheRoot ?? DefaultCacheRoot;
    }

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdTrim",
        "thumbnails");

    public static string CacheDirectoryFor(
        string sourcePath,
        long durationUs,
        string? cacheRoot = null,
        int targetWidth = TargetWidth,
        int cacheVersion = CacheVersion)
    {
        var hash = FrameCacheService.ComputeSourceHash(sourcePath);
        return Path.Combine(
            cacheRoot ?? DefaultCacheRoot,
            $"{hash}_d{durationUs}_w{targetWidth}_v{cacheVersion}");
    }

    public static long TimeForTile(int index, int total, long durationUs)
    {
        if (total <= 0 || durationUs <= 0) return 0;
        var t = (long)Math.Round(((index + 0.5) / total) * durationUs);
        return Math.Max(0, Math.Min(Math.Max(0, durationUs - 1), t));
    }

    public async IAsyncEnumerable<TimelineThumbnail> GetOrCreateStripAsync(
        string sourcePath,
        long durationUs,
        int tileCount = DefaultTileCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var dir = CacheDirectoryFor(sourcePath, durationUs, _cacheRoot);
        Directory.CreateDirectory(dir);

        for (int i = 0; i < tileCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var timeUs = TimeForTile(i, tileCount, durationUs);
            var path = Path.Combine(dir, $"{i:000}_{timeUs}.jpg");

            if (!File.Exists(path))
                await ExtractOneAsync(sourcePath, timeUs, path, ct).ConfigureAwait(false);

            if (File.Exists(path))
                yield return new TimelineThumbnail(i, tileCount, path);
        }
    }

    private async Task ExtractOneAsync(string sourcePath, long timeUs, string outPath, CancellationToken ct)
    {
        var seconds = (timeUs / 1_000_000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var tmp = outPath + ".tmp.jpg";
        if (File.Exists(tmp))
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }

        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-ss", seconds,
            "-i", sourcePath,
            "-frames:v", "1",
            "-vf", $"scale={TargetWidth}:-2",
            "-q:v", "6",
            tmp,
        };
        var r = await _runner.RunFfmpegAsync(args, ct).ConfigureAwait(false);
        if (!r.Success || !File.Exists(tmp)) return;

        try
        {
            File.Move(tmp, outPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
