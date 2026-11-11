using AwesomeAssertions;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class MediaProbeServiceTests
{
    [Fact]
    public void SelectPrimaryAudio_PrefersHighestChannelCount()
    {
        // The user's test fixture has 5.1 AC3 + stereo AAC; 5.1 should win.
        var streams = new[]
        {
            new AudioStream(Index: 2, Codec: "aac", Channels: 2, Default: true),
            new AudioStream(Index: 1, Codec: "ac3", Channels: 6, Default: false),
        };
        MediaProbeService.SelectPrimaryAudio(streams).Should().Be(1);
    }

    [Fact]
    public void SelectPrimaryAudio_BreaksTieByDefaultDisposition()
    {
        var streams = new[]
        {
            new AudioStream(Index: 1, Codec: "aac", Channels: 2, Default: false),
            new AudioStream(Index: 2, Codec: "aac", Channels: 2, Default: true),
        };
        MediaProbeService.SelectPrimaryAudio(streams).Should().Be(2);
    }

    [Fact]
    public void SelectPrimaryAudio_BreaksTieByLowestIndex()
    {
        var streams = new[]
        {
            new AudioStream(Index: 5, Codec: "aac", Channels: 2, Default: false),
            new AudioStream(Index: 3, Codec: "aac", Channels: 2, Default: false),
        };
        MediaProbeService.SelectPrimaryAudio(streams).Should().Be(3);
    }

    [Fact]
    public void SelectPrimaryAudio_NoStreams_ReturnsMinusOne()
    {
        MediaProbeService.SelectPrimaryAudio(Array.Empty<AudioStream>()).Should().Be(-1);
    }

    [Fact]
    public void RationalParse_Slashed()
    {
        var r = Rational.Parse("30000/1001");
        r.Numerator.Should().Be(30000);
        r.Denominator.Should().Be(1001);
        r.AsDouble.Should().BeApproximately(29.97, 0.01);
    }

    [Fact]
    public void RationalParse_DecimalFallback()
    {
        Rational.Parse("29.97").AsDouble.Should().BeApproximately(29.97, 0.01);
    }

    // Failure text goes into a banner. A per-frame log must not bury the reason or fill the window.
    [Fact]
    public void ErrorSummaryKeepsOnlyTheEndOfALongLog()
    {
        var log = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"[Parsed_showinfo_0] n:{i} pts_time:{i / 30.0}"))
            + "\nD:\\tv\\show.ts: I/O error\n";
        var result = new FfmpegResult { ExitCode = 1, Stdout = "", Stderr = log };
        Assert.EndsWith("I/O error", result.ErrorSummary);
        Assert.True(result.ErrorSummary.Length <= 403);
        Assert.Equal("No details were reported.", new FfmpegResult { ExitCode = 1, Stdout = "", Stderr = " \n" }.ErrorSummary);
    }
}
