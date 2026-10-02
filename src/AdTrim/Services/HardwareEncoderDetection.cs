using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdTrim.Encoders;

namespace AdTrim.Services;

public sealed record EncoderCapability(HardwareAdapter Adapter, bool Available, double Milliseconds, string Detail);
public sealed record EncoderDetection(IReadOnlyList<EncoderCapability> Results, string? Error = null, double? SoftwareMilliseconds = null,
    string? SoftwareDetail = null)
{
    public EncoderCapability? Preferred => Results.Where(r => r.Available && (SoftwareMilliseconds is null || r.Milliseconds < SoftwareMilliseconds))
        .OrderBy(r => r.Milliseconds).ThenBy(r => r.Adapter.Id, StringComparer.Ordinal).FirstOrDefault();
}

public sealed class HardwareEncoderDetection(FfmpegRunner runner, string dataDirectory)
{
    private const int ProbeVersion = 3;
    private sealed record Cache(string Key, DateTime CreatedUtc, EncoderCapability[] Results, double? SoftwareMilliseconds, string? SoftwareDetail = null);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<EncoderDetection> DetectAsync(bool recheck = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<HardwareAdapter> adapters;
            string? enumerationError = null;
            try { adapters = await Task.Run(GraphicsAdapters.Enumerate, ct).ConfigureAwait(false); }
            catch (System.Runtime.InteropServices.COMException ex)
            { adapters = Array.Empty<HardwareAdapter>(); enumerationError = "GPU discovery failed: " + ex.Message; }
            var key = CacheKey(adapters);
            var cachePath = Path.Combine(dataDirectory, "hardware-encoders.json");
            bool cacheable = enumerationError is null && adapters.All(a => a.DriverVersion != "unknown");
            if (!recheck && cacheable && await ReadCacheAsync(key, ct).ConfigureAwait(false) is { } cached)
                return cached;
            var results = new List<EncoderCapability>();
            foreach (var adapter in adapters.Where(a => a.Encoder is not null))
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await ProbeAsync(adapter, ct).ConfigureAwait(false));
            }
            var software = await ProbeAsync(null, ct).ConfigureAwait(false);
            double? softwareMs = software.Available ? software.Milliseconds : null;
            if (cacheable)
            {
                try
                {
                    Directory.CreateDirectory(dataDirectory);
                    var temp = cachePath + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Cache(key, DateTime.UtcNow, results.ToArray(), softwareMs, software.Detail)), ct).ConfigureAwait(false);
                        File.Move(temp, cachePath, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new(results, Error: enumerationError, SoftwareMilliseconds: softwareMs, SoftwareDetail: software.Detail);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        { return new(Array.Empty<EncoderCapability>(), "Hardware check failed: " + ex.Message); }
        finally { Gate.Release(); }
    }

    public async Task<EncoderDetection?> ReadCachedAsync(CancellationToken ct = default)
    {
        try
        {
            var adapters = await Task.Run(GraphicsAdapters.Enumerate, ct).ConfigureAwait(false);
            if (adapters.Any(a => a.DriverVersion == "unknown")) return null;
            return await ReadCacheAsync(CacheKey(adapters), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Runtime.InteropServices.COMException)
        { return null; }
    }

    private string CacheKey(IReadOnlyList<HardwareAdapter> adapters)
    {
        var exe = new FileInfo(runner.FfmpegPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ProbeVersion}|{exe.FullName}|{exe.Length}|{exe.LastWriteTimeUtc.Ticks}|{JsonSerializer.Serialize(adapters)}")));
    }

    private async Task<EncoderDetection?> ReadCacheAsync(string key, CancellationToken ct)
    {
        try
        {
            var cache = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(
                Path.Combine(dataDirectory, "hardware-encoders.json"), ct).ConfigureAwait(false));
            return cache?.Key == key && cache.Results is not null && DateTime.UtcNow - cache.CreatedUtc < TimeSpan.FromDays(7)
                ? new(cache.Results, SoftwareMilliseconds: cache.SoftwareMilliseconds, SoftwareDetail: cache.SoftwareDetail) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }
    private async Task<EncoderCapability> ProbeAsync(HardwareAdapter? candidate, CancellationToken ct)
    {
        var adapter = candidate ?? new HardwareAdapter(-1, "Software", 0, 0, "", "");
        var path = Path.Combine(Path.GetTempPath(), "adtrim-hardware-" + Guid.NewGuid().ToString("N") + ".mp4");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var encoder = candidate is null ? null : new HardwareEncoderStrategy(adapter);
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
            if (encoder is not null) args.AddRange(encoder.DeviceArgs);
            args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=12", "-vf",
                encoder is null ? "bwdif=mode=send_frame:parity=auto:deint=all" : encoder.Filter });
            args.AddRange(encoder?.EncoderArgs ?? new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p" });
            args.AddRange(new[] { "-an", path });
            var timer = Stopwatch.StartNew();
            var encode = await runner.RunFfmpegAsync(args, timeout.Token).ConfigureAwait(false);
            timer.Stop();
            if (!encode.Success) return new(adapter, false, 0, Tail(encode.Stderr));
            var decode = await runner.RunFfmpegAsync(new[] { "-hide_banner", "-loglevel", "error", "-xerror", "-i", path, "-f", "null", "-" }, timeout.Token).ConfigureAwait(false);
            if (!decode.Success) return new(adapter, false, 0, "Encoded video failed validation: " + Tail(decode.Stderr));
            var probe = await runner.RunFfprobeAsync(new[] { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "json", path }, timeout.Token).ConfigureAwait(false);
            if (!probe.Success) return new(adapter, false, 0, Tail(probe.Stderr));
            using var json = JsonDocument.Parse(probe.Stdout);
            var stream = json.RootElement.GetProperty("streams")[0];
            bool valid = stream.GetProperty("codec_name").GetString() == "h264" && stream.GetProperty("width").GetInt32() == 1920
                && stream.GetProperty("height").GetInt32() == 1080 && stream.GetProperty("nb_read_frames").GetString() == "360";
            return new(adapter, valid, timer.Elapsed.TotalMilliseconds, valid ? "Test encode and decode passed." : "Test output did not match the expected format or frame count.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(adapter, false, 0, "Hardware check timed out."); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return new(adapter, false, 0, ex.Message); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string Tail(string text) => text.Length <= 1200 ? text.Trim() : text[^1200..].Trim();
}
