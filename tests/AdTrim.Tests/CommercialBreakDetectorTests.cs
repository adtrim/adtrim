using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class CommercialBreakDetectorTests
{
    [Fact]
    public void BlackDetectStartAndEndLinesArePairedIntoAnInterval()
    {
        long? pending = null;
        Assert.False(CommercialBreakDetector.TryParseBlackEvent("black_start:12.345000", ref pending, out _));
        Assert.True(CommercialBreakDetector.TryParseBlackEvent("black_end:12.645000 black_duration:0.300000", ref pending, out var interval));
        Assert.Equal((12_345_000L, 12_645_000L), interval);
        Assert.Null(pending);
    }

    [Fact]
    public void QuietBlackTransitionsWithinProgramDoNotEstablishACommercial()
    {
        var scenes = new[] { (50_000_000L, .7), (90_000_000L, .8), (160_000_000L, .6),
            (180_000_000L, .7), (200_000_000L, .8), (260_000_000L, .7), (300_000_000L, .8) };
        // The previous detector called this High and automatically removed it.
        Assert.Empty(CommercialBreakDetector.FindBreaks(
            [(150_000_000L, 150_200_000L), (210_000_000L, 210_300_000L)], scenes, 500_000_000,
            [(149_500_000L, 150_100_000L), (210_250_000L, 211_000_000L)]));
    }

    [Fact]
    public void RepeatedShortUnitsAreGroupedForReviewWithoutAutomaticContentLoss()
    {
        var scenes = Enumerable.Range(0, 13).Select(i => (155_000_000L + i * 5_000_000L, .8)).ToArray();
        var found = Assert.Single(CommercialBreakDetector.FindBreaks(
            [(150_000_000L, 150_200_000L), (180_000_000L, 180_200_000L), (210_000_000L, 210_200_000L)],
            scenes, 500_000_000));
        Assert.Equal(150_200_000, found.StartUs);
        Assert.Equal(210_200_000, found.EndUs);
        Assert.False(found.AutoExclude);
        Assert.Equal(Confidence.Low, found.Confidence);
        Assert.Contains("No audio evidence", found.Evidence);
    }

    [Fact]
    public void AdFreeAndSourceEdgesRemainIncluded()
    {
        Assert.Empty(CommercialBreakDetector.FindBreaks([], [], 600_000_000));
        Assert.Empty(CommercialBreakDetector.FindBreaks(
            [(10_000_000L, 10_200_000L), (40_000_000L, 40_200_000L), (70_000_000L, 70_200_000L)],
            Enumerable.Range(0, 15).Select(i => (i * 5_000_000L, .9)).ToArray(), 90_000_000));
    }
}
