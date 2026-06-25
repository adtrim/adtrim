using System.Diagnostics;
using System.IO;
using System.Text;

namespace AdTrim.Services;

/// <summary>
/// Extracts per-bin audio peak levels from the primary audio stream of a media
/// file for waveform visualization. Decodes the audio stream to mono PCM s16le
/// at 8 kHz, then groups samples into fixed 40 ms bins (25 bins/sec). Each
/// output value is the peak absolute amplitude within that bin, normalized to
/// [0,1].
///
/// 40 ms bins are short enough that a 0.5 s silence between segments shows up
/// distinctly while keeping decode output and cache size modest.
///
/// Not a real-time path. Run from a background task - extraction takes a few
/// seconds on hour-long files. Results are persisted under local app data.
/// </summary>
public sealed class WaveformService
{
    private readonly FfmpegRunner? _runner;
    private readonly string _cacheRoot;

    private const string CacheExtension = ".adtwave";
    private const string CacheMagic = "ADTWAVE";
    public const int CacheVersion = 2;

    // 8 kHz mono is enough resolution for a visualization. Decoding AC3 to PCM
    // at 8 kHz is dominated by file I/O, not the resample.
    private const int SampleRateHz = 8000;

    /// <summary>One bin per 40 ms = 25 bins per second.</summary>
    public const int BinsPerSecond = 25;

    private const int SamplesPerBin = SampleRateHz / BinsPerSecond; // 320

    public WaveformService(FfmpegRunner? runner, string? cacheRoot = null)
    {
        _runner = runner;
        _cacheRoot = cacheRoot ?? DefaultCacheRoot;
    }

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdTrim",
        "waveforms");

    public static string CachePathFor(
        string sourcePath,
        int audioStreamIndex,
        long durationUs,
        string? cacheRoot = null,
        int cacheVersion = CacheVersion,
        int binsPerSecond = BinsPerSecond)
    {
        var hash = FrameCacheService.ComputeSourceHash(sourcePath);
        var fileName = $"{hash}_a{audioStreamIndex}_d{durationUs}_b{binsPerSecond}_v{cacheVersion}{CacheExtension}";
        return Path.Combine(cacheRoot ?? DefaultCacheRoot, fileName);
    }

    public async Task<float[]> GetOrExtractPeaksAsync(
        string sourcePath,
        int audioStreamIndex,
        long durationUs,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (TryLoadCachedPeaks(sourcePath, audioStreamIndex, durationUs, out var cached))
            return cached;

        var peaks = await ExtractPeaksAsync(sourcePath, audioStreamIndex, durationUs, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        SaveCachedPeaks(sourcePath, audioStreamIndex, durationUs, peaks);
        return peaks;
    }

    public bool TryLoadCachedPeaks(
        string sourcePath,
        int audioStreamIndex,
        long durationUs,
        out float[] peaks)
    {
        peaks = Array.Empty<float>();
        var path = CachePathFor(sourcePath, audioStreamIndex, durationUs, _cacheRoot);
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadString() != CacheMagic) return false;
            if (reader.ReadInt32() != CacheVersion) return false;
            if (reader.ReadInt32() != BinsPerSecond) return false;
            if (reader.ReadInt32() != audioStreamIndex) return false;
            if (reader.ReadInt64() != durationUs) return false;

            var count = reader.ReadInt32();
            if (count < 0 || count > 24 * 60 * 60 * BinsPerSecond) return false;
            peaks = new float[count];
            for (int i = 0; i < peaks.Length; i++)
                peaks[i] = reader.ReadSingle();
            return true;
        }
        catch
        {
            peaks = Array.Empty<float>();
            return false;
        }
    }

    public void SaveCachedPeaks(
        string sourcePath,
        int audioStreamIndex,
        long durationUs,
        IReadOnlyList<float> peaks)
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var path = CachePathFor(sourcePath, audioStreamIndex, durationUs, _cacheRoot);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheVersion);
                writer.Write(BinsPerSecond);
                writer.Write(audioStreamIndex);
                writer.Write(durationUs);
                writer.Write(peaks.Count);
                for (int i = 0; i < peaks.Count; i++)
                    writer.Write(peaks[i]);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Cache failures should never block editing or playback.
        }
    }

    public static int PeakCountForDuration(long durationUs)
    {
        if (durationUs <= 0) return 0;
        var totalSamples = (long)Math.Round(durationUs / 1_000_000.0 * SampleRateHz);
        if (totalSamples <= 0) return 0;
        return (int)((totalSamples + SamplesPerBin - 1) / SamplesPerBin);
    }

    public async Task<float[]> ExtractPeaksAsync(
        string sourcePath,
        int audioStreamIndex,
        long durationUs,
        CancellationToken ct = default)
    {
        var runner = _runner ?? throw new InvalidOperationException("FFmpeg is required to extract waveform peaks.");
        if (durationUs <= 0) return Array.Empty<float>();

        var binCount = PeakCountForDuration(durationUs);
        if (binCount <= 0) return Array.Empty<float>();

        var args = new List<string>
        {
            "-nostdin",
            "-v", "error",
            "-i", sourcePath,
            "-map", $"0:{audioStreamIndex}",
            "-vn",
            "-sn",
            "-ac", "1",
            "-ar", SampleRateHz.ToString(),
            "-f", "s16le",
            "-",
        };

        var psi = new ProcessStartInfo
        {
            FileName = runner.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* best effort */ }
        p.BeginErrorReadLine();

        await using var _ = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* race */ }
        });

        var peaks = new float[binCount];
        try
        {
            var stream = p.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            int samplesInBin = 0;
            int currentBin = 0;
            int peakInBin = 0;

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                // Drop trailing odd byte (shouldn't happen with s16le, but defensive).
                int usable = read - (read & 1);
                for (int i = 0; i < usable; i += 2)
                {
                    short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                    int abs = s < 0 ? -s : s;
                    if (abs > peakInBin) peakInBin = abs;

                    if (++samplesInBin >= SamplesPerBin)
                    {
                        if (currentBin < binCount)
                            peaks[currentBin] = peakInBin / 32768f;
                        currentBin++;
                        if (currentBin >= binCount) goto Done;
                        peakInBin = 0;
                        samplesInBin = 0;
                    }
                }
            }
            Done:
            if (samplesInBin > 0 && currentBin < binCount)
                peaks[currentBin] = peakInBin / 32768f;
        }
        finally
        {
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* killed by ct.Register */ }
        }

        if (p.ExitCode != 0 && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ffmpeg waveform extraction failed (exit {p.ExitCode}): {stderr}");
        }

        return peaks;
    }
}
