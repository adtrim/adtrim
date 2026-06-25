using AwesomeAssertions;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

/// <summary>
/// Unit tests for <see cref="ExportService.TryParseProgressOutTimeUs"/>.
/// The parser reads one stdout line at a time from ffmpeg's
/// <c>-progress pipe:1</c> output; only <c>out_time_us=N</c> lines carry
/// usable progress, everything else is filtered out. A bug here makes
/// per-segment progress freeze at 0% during long encodes.
/// </summary>
public class ExportServiceProgressParserTests
{
    [Fact]
    public void TryParseProgressOutTimeUs_ParsesValidLine()
    {
        ExportService.TryParseProgressOutTimeUs("out_time_us=200000").Should().Be(200_000L);
        ExportService.TryParseProgressOutTimeUs("out_time_us=0").Should().Be(0L);
        ExportService.TryParseProgressOutTimeUs("out_time_us=9876543210").Should().Be(9_876_543_210L);
    }

    [Fact]
    public void TryParseProgressOutTimeUs_ReturnsNullForNonProgressLines()
    {
        // ffmpeg emits ~10 different keys per record. Only out_time_us matters.
        ExportService.TryParseProgressOutTimeUs("frame=42").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("fps=30.5").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("bitrate=  82.7kbits/s").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("out_time=00:00:00.200000").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("out_time_ms=200000").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("progress=continue").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("progress=end").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("").Should().BeNull();
    }

    [Fact]
    public void TryParseProgressOutTimeUs_HandlesNAValueBeforeFirstFrame()
    {
        // ffmpeg writes "N/A" before any output time exists. Must not crash
        // or report 0 (which would imply a real start-of-segment frame).
        ExportService.TryParseProgressOutTimeUs("out_time_us=N/A").Should().BeNull();
    }

    [Fact]
    public void TryParseProgressOutTimeUs_RejectsMalformedValues()
    {
        ExportService.TryParseProgressOutTimeUs("out_time_us=").Should().BeNull();
        ExportService.TryParseProgressOutTimeUs("out_time_us=abc").Should().BeNull();
        // Prefix-only would be a false positive if we used Contains instead of StartsWith.
        ExportService.TryParseProgressOutTimeUs("xxxout_time_us=1000").Should().BeNull();
    }
}
