using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class LogoFreeSegmenterTests
{
    private const byte Speech = 90, Silent = 0; // loudness bytes: dBFS + 120

    /// <summary>30 minutes without a station graphic: a program of recurring sets, a 30 s opening
    /// sequence with its own look and continuous music, then a pod of 15 s advertisements with a
    /// splice silence at every unit boundary; a second pod later.</summary>
    private static SuggestionEvidence Recording(int seconds = 1800, (int Start, int End)[]? podsOverride = null)
    {
        var pods = podsOverride ?? [(400, 580), (1200, 1380)];
        var random = new Random(11);
        var appearance = new byte[seconds * ShowProfile.Width];
        // Speech with natural pauses (300 ms near -65 dB, every 8 s on average); the opening's
        // music plays continuously, without pauses.
        var loudness = Enumerable.Repeat(Speech, seconds * 50).ToArray();
        for (int k = random.Next(400); k < loudness.Length - 15; k += random.Next(200, 600))
            if (k / 50 < 370 || k / 50 >= 400) Array.Fill(loudness, (byte)55, k, 15);
        var scenes = new List<(long, double)>();
        var sets = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, ShowProfile.Width).Select(_ => (byte)random.Next(40, 216)).ToArray()).ToArray();
        byte[] shot = sets[0];
        int nextCut = 0;
        for (int i = 0; i < seconds; i++)
        {
            bool ad = pods.Any(p => i >= p.Start && i < p.End);
            bool opening = i >= 370 && i < 400;
            if (ad && pods.Any(p => i >= p.Start && (i - p.Start) % 15 == 0 && i < p.End) || pods.Any(p => i == p.End))
            {
                // Station splice: a cut with about 100 ms of silence around it.
                for (int k = i * 50 - 3; k < i * 50 + 3; k++) if (k >= 0 && k < loudness.Length) loudness[k] = Silent;
                nextCut = i;
            }
            if (i >= nextCut)
            {
                shot = ad || opening ? Enumerable.Range(0, ShowProfile.Width).Select(_ => (byte)random.Next(256)).ToArray() : sets[random.Next(sets.Length)];
                nextCut = i + (ad || opening ? random.Next(1, 4) : random.Next(6, 25));
                scenes.Add((i * 1_000_000L, .8));
            }
            for (int j = 0; j < ShowProfile.Width; j++)
                appearance[i * ShowProfile.Width + j] = (byte)Math.Clamp(shot[j] + random.Next(-4, 5), 0, 255);
        }
        return new SuggestionEvidence([], scenes.Distinct().OrderBy(s => s.Item1).ToArray(), [], 0, false)
            { Appearance = appearance, Loudness = loudness };
    }

    [Fact]
    public void BreaksStartAtTheFirstSpliceNotAtAnOpeningSequenceWithContinuousSound()
    {
        var breaks = LogoFreeSegmenter.FindBreaks(Recording(), 1_800_000_000);
        Assert.True(breaks.Any(b => b.StartUs == 400_000_000), "proposals: " + string.Join(", ", breaks.Select(b => $"{b.StartUs / 1e6}-{b.EndUs / 1e6}")));
        Assert.DoesNotContain(breaks, b => b.StartUs < 400_000_000 && b.EndUs > 370_000_000);
        Assert.All(breaks, b => Assert.False(b.AutoExclude));
        // Never removes program: every proposal lies inside a pod.
        Assert.All(breaks, b => Assert.True(b.StartUs >= 400_000_000 && b.EndUs <= 580_000_000 || b.StartUs >= 1_200_000_000 && b.EndUs <= 1_380_000_000));
    }

    [Fact]
    public void AbstainsWhenTheProfileWouldCallMostOfTheRecordingNonProgram()
    {
        // Mostly fast-cut, unique-look material: the premise that program is the majority fails.
        var evidence = Recording(1800, [(100, 500), (550, 950), (1000, 1400), (1450, 1750)]);
        Assert.Empty(LogoFreeSegmenter.FindBreaks(evidence, 1_800_000_000));
    }

    [Theory]
    [InlineData(45_100_000, 60_100_000, true)]   // whole units within a few frames: a pod of advertisements
    [InlineData(45_700_000, 60_700_000, false)]  // 0.7 s off: an unrelated pause, e.g. before an opening theme
    public void SplicesChainOnlyAtNearlyExactAdvertisementLengths(long first, long second, bool expected)
    {
        long[] dips = [18_000_000, 18_000_000 + first, 18_000_000 + first + second];
        Assert.Equal(expected, LogoFreeSegmenter.Chain(dips, 18_000_000, 1));
    }

    [Fact]
    public void ConstantSoundWithoutQuietMomentsYieldsNoSpliceSilences()
    {
        // A level that rarely drops would otherwise make every cut look like a splice.
        Assert.Empty(LogoFreeSegmenter.QuietRuns(Enumerable.Repeat((byte)90, 10_000).ToArray()));
    }

    [Fact]
    public void QuietDigitalProgramStillShowsItsSplices()
    {
        // Measured on Wheel of Fortune: median about -43 dB, floor about -69 dB. Splices at
        // digital silence must count even though -50 dB is close to the median.
        var loudness = Enumerable.Repeat((byte)77, 10_000).ToArray();
        for (int i = 0; i < 200; i++) loudness[i * 50] = 51;
        loudness[5_000] = 0;
        Assert.Contains(LogoFreeSegmenter.QuietRuns(loudness), r => r.Start == 5_000 * 20_000L);
    }

    [Theory]
    [InlineData(80, 74, true)]   // noisy SD floor near -40 dB: a dip to -46 dB is a splice
    [InlineData(40, 74, false)]  // digital floor near -80 dB: -46 dB is ordinary quiet speech
    public void SpliceSilenceIsRelativeToTheRecordingsOwnFloor(byte floor, byte dip, bool expected)
    {
        var loudness = Enumerable.Repeat((byte)100, 10_000).ToArray(); // typical level -20 dB
        for (int i = 0; i < 200; i++) loudness[i * 50] = floor;
        loudness[5_000] = dip;
        bool found = LogoFreeSegmenter.QuietRuns(loudness).Any(r => r.Start == 5_000 * 20_000L);
        Assert.Equal(expected, found);
    }
}
