using System.Globalization;
using System.Text;

namespace AdTrim.Models;

public sealed record KeptSpan(long StartUs, long EndUs, long OutputStartUs)
{
    public long DurationUs => EndUs - StartUs;
    public long OutputEndUs => OutputStartUs + DurationUs;
}

/// <summary>Immutable mapping between source time and the kept playback timeline.</summary>
public sealed class KeptTimeline
{
    public IReadOnlyList<KeptSpan> Spans { get; }
    public long DurationUs { get; }
    public KeptTimeline(IEnumerable<Segment> segments)
    {
        var spans = new List<KeptSpan>();
        long output = 0;
        foreach (var segment in segments.Where(s => !s.IsExcluded && s.DurationUs > 0).OrderBy(s => s.StartUs))
        {
            if (spans.Count > 0 && spans[^1].EndUs == segment.StartUs)
                spans[^1] = spans[^1] with { EndUs = segment.EndUs };
            else spans.Add(new(segment.StartUs, segment.EndUs, output));
            output += segment.DurationUs;
        }
        Spans = spans.AsReadOnly();
        DurationUs = output;
    }
    public long ToSource(long outputUs)
    {
        outputUs = Math.Clamp(outputUs, 0, DurationUs);
        foreach (var span in Spans)
            if (outputUs < span.OutputEndUs) return span.StartUs + outputUs - span.OutputStartUs;
        return Spans.Count == 0 ? 0 : Spans[^1].EndUs;
    }
    public long ToOutput(long sourceUs)
    {
        foreach (var span in Spans)
        {
            if (sourceUs < span.StartUs) return span.OutputStartUs;
            if (sourceUs < span.EndUs) return span.OutputStartUs + sourceUs - span.StartUs;
        }
        return DurationUs;
    }
    public string ToMpvEdl(string sourcePath)
    {
        if (Spans.Count == 0) throw new InvalidOperationException("No kept footage.");
        var file = $"%{Encoding.UTF8.GetByteCount(sourcePath)}%{sourcePath}";
        return "edl://" + string.Join(";", Spans.Select(s => file + "," + Seconds(s.StartUs) + "," + Seconds(s.DurationUs)));
    }
    private static string Seconds(long us) => (us / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
}
