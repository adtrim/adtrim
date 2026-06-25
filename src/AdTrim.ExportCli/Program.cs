using System.Diagnostics;
using System.Globalization;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;

// Standalone export driver for export validation.
//
// Usage:
//   export-cli --in <source.mp4> --out <output.mp4> [--exclude 0,2,4,...]
//
// Segments are derived from chapter-imported splits + [0, duration] bookends.
// `--exclude` takes zero-based segment indices to drop. If omitted, every
// even-indexed segment (chapter-aligned commercials) is dropped - this matches
// the user's actual workflow on the test fixture.

string? inPath = null, outPath = null;
var excludeIndices = new HashSet<int>();
bool autoExcludeEven = true;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--in":      inPath = args[++i]; break;
        case "--out":     outPath = args[++i]; break;
        case "--exclude":
            autoExcludeEven = false;
            foreach (var s in args[++i].Split(','))
                if (int.TryParse(s, out var n)) excludeIndices.Add(n);
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 64;
    }
}

if (inPath is null || outPath is null)
{
    Console.Error.WriteLine("usage: export-cli --in <source.mp4> --out <output.mp4> [--exclude 0,2,4,...]");
    return 64;
}

FfmpegRunner runner;
try { runner = new FfmpegRunner(); }
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Hint: set ADTRIM_FFMPEG_DIR=C:\\Program Files\\ffmpeg\\bin");
    return 69;
}

var probe = new MediaProbeService(runner);
var chapterSvc = new ChapterImportService(runner);
var encoder = new LibX264EncoderStrategy();
var export = new ExportService(runner, encoder);

Console.WriteLine($"[probe ] {inPath}");
var media = await probe.ProbeAsync(inPath);
Console.WriteLine($"[probe ] duration={media.DurationUs / 1_000_000.0:0.000}s "
                + $"video={media.VideoCodec} {media.Width}x{media.Height} "
                + $"fps={media.FrameRate.AsDouble:0.###} "
                + $"audio.primary=#{media.PrimaryAudioIndex}");

var chapters = await chapterSvc.ImportAsync(inPath, media.DurationUs);
Console.WriteLine($"[chap  ] imported {chapters.Count} internal chapter boundaries");

// Build segments from sorted splits + [0, duration].
var splitsUs = new List<long> { 0 };
splitsUs.AddRange(chapters.Select(c => c.TimeUs));
splitsUs.Add(media.DurationUs);
splitsUs.Sort();

var allSegments = new List<(int index, long start, long end)>();
for (int i = 0; i < splitsUs.Count - 1; i++)
    allSegments.Add((i, splitsUs[i], splitsUs[i + 1]));

if (autoExcludeEven)
{
    foreach (var (i, _, _) in allSegments) if (i % 2 == 0) excludeIndices.Add(i);
}

Console.WriteLine($"[plan  ] {allSegments.Count} segments - keeping {allSegments.Count - excludeIndices.Count}, dropping {excludeIndices.Count}");
foreach (var (i, s, e) in allSegments)
{
    var kept = !excludeIndices.Contains(i);
    Console.WriteLine($"          {(kept ? "KEEP" : "drop")}  seg {i}: {s / 1_000_000.0,9:0.000} → {e / 1_000_000.0,9:0.000}  ({(e - s) / 1_000_000.0,6:0.0}s)");
}

var keptSegments = allSegments
    .Where(t => !excludeIndices.Contains(t.index))
    .Select((t, idx) => new ExportSegment(idx + 1, t.start, t.end, $"Part {idx + 1}"))
    .ToList();

if (keptSegments.Count == 0)
{
    Console.Error.WriteLine("All segments excluded - nothing to export.");
    return 1;
}

var plan = new ExportPlan(
    SourcePath: inPath,
    OutputPath: outPath,
    SourceDurationUs: media.DurationUs,
    PrimaryAudioStreamIndex: media.PrimaryAudioIndex,
    KeptSegments: keptSegments,
    FrameRate: media.FrameRate);

Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
if (File.Exists(outPath)) File.Delete(outPath);

var lastPercent = -1;
var progress = new Progress<ExportProgress>(p =>
{
    var pct = (int)(p.OverallPercent * 100);
    if (pct != lastPercent)
    {
        lastPercent = pct;
        Console.WriteLine($"[{p.Phase,-15}] {pct,3}%  seg {p.CurrentSegment}/{p.TotalSegments}  {p.Message}");
    }
});

var sw = Stopwatch.StartNew();
try
{
    await export.RunExportAsync(plan, progress);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[export failed] {ex.Message}");
    return 1;
}
sw.Stop();

Console.WriteLine();
Console.WriteLine($"[done  ] {sw.Elapsed.TotalSeconds:0.0}s elapsed");
Console.WriteLine($"[done  ] output: {outPath}");
Console.WriteLine($"[done  ] size: {new FileInfo(outPath).Length / 1_000_000.0:0.0} MB "
                + $"(source: {new FileInfo(inPath).Length / 1_000_000.0:0.0} MB)");

return 0;
