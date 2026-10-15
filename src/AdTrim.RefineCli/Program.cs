using System.Diagnostics;
using System.Globalization;
using AdTrim.Models;
using AdTrim.Services;

// Standalone RefineService driver. Refines every
// chapter-import boundary on a fixture file and prints (original, refined,
// delta_ms, score, margin, confidence). Use this to tune the filter graph
// and scoring constants against real MPEG-2 / XDCAM EX 1080i58 recordings
// BEFORE the editor UI relies on the service.
//
// Validation criteria:
//   - ≥70% of markers move measurably closer to a visually verified boundary.
//   - No marker exits source bounds or crosses a neighbor.
//   - Full pass on a 1-hour file completes in <60s on the target machine.
//
// Usage:
//   refine-cli <source.mp4> [comma-separated split-times-in-seconds]
//
// If no split list is supplied, embedded MP4 chapters are imported as splits.

if (args.Length == 2 && args[0] == "--replay-suggestions")
{
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
    var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
    var evidence = System.Text.Json.JsonSerializer.Deserialize<SuggestionEvidence>(report.RootElement.GetProperty("Evidence"), options)
        ?? throw new InvalidDataException("Missing suggestion evidence.");
    long duration = report.RootElement.GetProperty("Media").GetProperty("DurationUs").GetInt64();
    var uncertain = new List<CommercialBreakProposal>();
    var breaks = SuggestionEngine.Classify(evidence, duration, uncertain.Add);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        DetectorVersion = SuggestionEngine.Version,
        FrameMappingPerformed = false,
        Breaks = breaks, UncertainBreaks = uncertain
    }, options));
    return 0;
}

if ((args.Length == 2 && (args[0] == "--detect" || args[0] == "--suggest" || args[0] == "--suggest-evidence"))
    || (args.Length == 3 && args[0] == "--suggest-from-evidence"))
{
    var path = Path.GetFullPath(args[1]);
    async Task<string> Hash()
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input));
    }
    var before = await Hash();
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var detectorRunner = new FfmpegRunner();
    var info = await new MediaProbeService(detectorRunner).ProbeAsync(path, cancellation.Token);
    var watch = Stopwatch.StartNew();
    SuggestionEvidence? evidence = null;
    CommercialDetectionResult detection;
    if (args[0] == "--suggest-from-evidence")
    {
        using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(args[2], cancellation.Token));
        if (!report.RootElement.GetProperty("SourceUnchanged").GetBoolean()
            || report.RootElement.GetProperty("SourceSha256").GetString() != before
            || report.RootElement.GetProperty("Media").GetProperty("DurationUs").GetInt64() != info.DurationUs)
            throw new InvalidDataException("Evidence report does not match this unchanged source.");
        evidence = System.Text.Json.JsonSerializer.Deserialize<SuggestionEvidence>(report.RootElement.GetProperty("Evidence"),
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true }) ?? throw new InvalidDataException("Missing suggestion evidence.");
        detection = await new SuggestionEngine(detectorRunner).AnalyzeEvidenceAsync(path, info, evidence, cancellation.Token);
    }
    else if (args[0] == "--suggest-evidence")
    {
        evidence = await new SuggestionEvidenceCollector(detectorRunner).CollectAsync(path, info, null, cancellation.Token);
        detection = await new SuggestionEngine(detectorRunner).AnalyzeEvidenceAsync(path, info, evidence, cancellation.Token);
    }
    else detection = args[0] == "--suggest"
        ? await new SuggestionEngine(detectorRunner).AnalyzeAsync(path, info, cancellationToken: cancellation.Token)
        : await new CommercialBreakDetector(detectorRunner).AnalyzeAsync(path, info, cancellationToken: cancellation.Token);
    var after = await Hash();
    if (before != after) throw new IOException("Source changed during analysis.");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        SourceSha256 = before, SourceUnchanged = true, ElapsedSeconds = watch.Elapsed.TotalSeconds,
        PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        Media = info, Detection = detection, Evidence = evidence,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    return 0;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: refine-cli <source.mp4> [t1,t2,...] (seconds)");
    Console.Error.WriteLine("       refine-cli --detect|--suggest|--suggest-evidence <source.mp4>");
    Console.Error.WriteLine("       refine-cli --replay-suggestions <evidence-report.json> (no frame mapping)");
    Console.Error.WriteLine("       refine-cli --suggest-from-evidence <source.mp4> <evidence-report.json>");
    return 64;
}

var sourcePath = args[0];
if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"source not found: {sourcePath}");
    return 66;
}

FfmpegRunner runner;
try { runner = new FfmpegRunner(); }
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Hint: set ADTRIM_FFMPEG_DIR to a directory containing ffmpeg.exe + ffprobe.exe.");
    return 69;
}

var probe = new MediaProbeService(runner);
var chapters = new ChapterImportService(runner);
var refine = new RefineService(runner);

Console.WriteLine($"[probe ] {sourcePath}");
var media = await probe.ProbeAsync(sourcePath);
Console.WriteLine($"[probe ] duration={media.DurationUs / 1_000_000.0:0.000}s "
                + $"video={media.VideoCodec} {media.Width}x{media.Height} "
                + $"fps={media.FrameRate.AsDouble:0.###} "
                + $"audio.primary=#{media.PrimaryAudioIndex}");

List<long> splitsUs;
if (args.Length >= 2)
{
    splitsUs = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => (long)Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 1_000_000.0))
        .ToList();
    Console.WriteLine($"[input ] {splitsUs.Count} manual split(s)");
}
else
{
    var imported = await chapters.ImportAsync(sourcePath, media.DurationUs);
    splitsUs = imported.Select(c => c.TimeUs).ToList();
    Console.WriteLine($"[chap  ] imported {splitsUs.Count} internal chapter boundaries");
}

splitsUs.Sort();
long prevBound = 0;
var results = new List<(long origUs, RefineResult? r)>();
var sw = Stopwatch.StartNew();

Console.WriteLine();
Console.WriteLine($"  #   orig (s)    refined (s)   Δ ms   top    margin  tied  confidence");
Console.WriteLine("  --  ----------  -----------  ------  -----  ------  ----  ----------");

for (int i = 0; i < splitsUs.Count; i++)
{
    long orig = splitsUs[i];
    long nextBound = i + 1 < splitsUs.Count ? splitsUs[i + 1] : media.DurationUs;

    var result = await refine.RefineOneAsync(sourcePath, orig, prevBound, nextBound);
    results.Add((orig, result));

    if (result is null)
    {
        Console.WriteLine($"  {i + 1,2}  {orig / 1_000_000.0,10:0.000}        (no candidate frames in window)");
    }
    else
    {
        var deltaMs = result.DeltaUs / 1000.0;
        Console.WriteLine(
            $"  {i + 1,2}  {orig / 1_000_000.0,10:0.000}  {result.RefinedTimeUs / 1_000_000.0,11:0.000}"
          + $"  {deltaMs,6:+0.0;-0.0;0.0}  {result.TopScore,5:0.00}  {result.Margin,6:0.00}  {result.TiedAtTop,4}  {result.Confidence}");
        // Use refined as the prev-bound for the next iteration so the
        // cross-neighbor invariant matches the editor's runtime behavior.
        prevBound = Math.Max(prevBound, result.RefinedTimeUs);
    }
}

sw.Stop();

// Summary buckets.
int high = 0, medium = 0, low = 0, unchanged = 0, skipped = 0, moved = 0;
foreach (var (_, r) in results)
{
    if (r is null) { skipped++; continue; }
    switch (r.Confidence)
    {
        case Confidence.High:      high++; break;
        case Confidence.Medium:    medium++; break;
        case Confidence.Low:       low++; break;
        case Confidence.Unchanged: unchanged++; break;
    }
    if (Math.Abs(r.DeltaUs) > 1000) moved++;   // moved by >1ms
}

Console.WriteLine();
Console.WriteLine($"[done  ] elapsed {sw.Elapsed.TotalSeconds:0.0}s");
Console.WriteLine($"[summary] {results.Count} splits  ·  "
                + $"high={high}  medium={medium}  low={low}  unchanged={unchanged}  no-candidates={skipped}");
Console.WriteLine($"[summary] moved (>1ms): {moved}/{results.Count - skipped} "
                + $"({(results.Count - skipped == 0 ? 0 : 100.0 * moved / (results.Count - skipped)):0.0}%)");

return 0;
