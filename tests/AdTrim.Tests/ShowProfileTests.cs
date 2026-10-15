using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class ShowProfileTests
{
    // 30 minutes: one program look with slow editing, two fast-cut pods of varied looks.
    private static readonly (int Start, int End)[] Pods = [(400, 580), (1200, 1380)];

    private static SuggestionEvidence Recording()
    {
        var random = new Random(7);
        var appearance = new byte[1800 * ShowProfile.Width];
        var scenes = new List<(long, double)>();
        // The program keeps returning to a few recurring sets in irregular shots; each
        // advertisement unit has a look of its own and much faster editing.
        var sets = Enumerable.Range(0, 4).Select(_ => Enumerable.Range(0, ShowProfile.Width).Select(_ => (byte)random.Next(40, 216)).ToArray()).ToArray();
        byte[] shot = sets[0];
        int nextCut = 0;
        for (int i = 0; i < 1800; i++)
        {
            bool ad = Pods.Any(p => i >= p.Start && i < p.End);
            if (i >= nextCut)
            {
                shot = ad ? Enumerable.Range(0, ShowProfile.Width).Select(_ => (byte)random.Next(256)).ToArray() : sets[random.Next(sets.Length)];
                nextCut = i + (ad ? random.Next(1, 4) : random.Next(6, 25));
                scenes.Add((i * 1_000_000L, .8));
            }
            for (int j = 0; j < ShowProfile.Width; j++)
                appearance[i * ShowProfile.Width + j] = (byte)Math.Clamp(shot[j] + random.Next(-4, 5), 0, 255);
        }
        return new SuggestionEvidence([], scenes, [], 0, false) { Appearance = appearance };
    }

    [Fact]
    public void LearnsTheProgramFromTheRecordingItselfWithoutAStationGraphic()
    {
        var profile = ShowProfile.Score(Recording(), 1_800_000_000);
        Assert.NotNull(profile);
        double Mean(int a, int b) => profile![a..b].Average();
        Assert.True(Mean(410, 570) > .8, "advertisement seconds should score as non-program");
        Assert.True(Mean(1210, 1370) > .8);
        Assert.True(Mean(50, 350) < .2, "program seconds should score as program");
        Assert.True(Mean(700, 1100) < .2);
    }

    [Fact]
    public void TooLittleMaterialAbstains()
    {
        Assert.Null(ShowProfile.Score(new SuggestionEvidence([], [], [], 0, false) { Appearance = new byte[300 * ShowProfile.Width] }, 300_000_000));
    }

    [Fact]
    public void AgreeingProfilePromotesAnUncertainCandidateAndMovesItsEntryPastALogoFreeIntro()
    {
        // The graphic disappears at 136 s (intro start) but the advertisements begin at 149 s.
        var profile = Enumerable.Range(0, 600).Select(i => i >= 149 && i < 315 ? .95 : .05).ToArray();
        var evidence = new SuggestionEvidence([(314_000_000L, 315_000_000L)],
            [(136_000_000L, .7), (149_000_000L, .5), (200_000_000L, .8)], [], 44, false);
        var candidate = new CommercialBreakProposal(136_000_000, 315_000_000, Confidence.Low, false, "Uncertain: graphic absence");
        var promoted = SuggestionEngine.Promote(evidence, profile, candidate);
        Assert.NotNull(promoted);
        Assert.Equal(149_000_000, promoted!.StartUs);
        Assert.Equal(315_000_000, promoted.EndUs);
        Assert.False(promoted.AutoExclude);
    }

    [Theory]
    [InlineData(962_074_000, 962_141_000, true)]   // 1.7 s act-in sting between two blacks: program, keep it
    [InlineData(958_800_000, 958_900_000, false)]  // 4.9 s: as long as a short advertisement, leave the return alone
    public void ShortStingBetweenBlacksAtAReturnStaysWithTheProgram(long firstStart, long firstEnd, bool moved)
    {
        var evidence = new SuggestionEvidence([(firstStart, firstEnd), (963_826_000L, 964_493_000L)], [], [], 44, false);
        var proposal = new CommercialBreakProposal(765_000_000, 964_493_000, Confidence.Low, false, "test");
        var result = SuggestionEngine.ProtectProgramEdges(evidence, proposal, [], []);
        Assert.Equal(moved ? firstEnd : 964_493_000, result.EndUs);
    }

    [Theory]
    [InlineData(10_700_000, true)]   // closing credits over black, then the first splice of a chained pod
    [InlineData(30_000_000, false)]  // a whole advertisement away: more likely a direct cut, leave it
    public void EntryWithoutASpliceMovesPastShortLogoFreeProgramMaterial(long gap, bool moved)
    {
        const long entry = 3_766_000_000;
        long first = entry + gap;
        long[] splices = [first, first + 15_000_000, first + 30_000_000, first + 45_000_000];
        var quiet = splices.Select(t => (t - 50_000, t + 50_000)).ToList();
        var evidence = new SuggestionEvidence([], [], [], 44, false);
        var proposal = new CommercialBreakProposal(entry, entry + 120_000_000, Confidence.Low, false, "test");
        var result = SuggestionEngine.ProtectProgramEdges(evidence, proposal, quiet, splices);
        Assert.Equal(moved ? first : entry, result.StartUs);
    }

    [Fact]
    public void EdgeWithASpliceSilenceIsNeverMoved()
    {
        const long entry = 1_000_000_000;
        long[] splices = [entry, entry + 15_000_000, entry + 30_000_000];
        var quiet = splices.Select(t => (t - 50_000, t + 50_000)).ToList();
        var proposal = new CommercialBreakProposal(entry, entry + 90_000_000, Confidence.Low, false, "test");
        Assert.Same(proposal, SuggestionEngine.ProtectProgramEdges(new SuggestionEvidence([], [], [], 44, false), proposal, quiet, splices));
    }

    [Fact]
    public void ProgramLikeProfileKeepsACandidateUncertain()
    {
        // A logo-free intro or neighboring program looks like the recording's program: never promote it.
        var profile = Enumerable.Range(0, 600).Select(i => i >= 68 && i < 175 ? .1 : .05).ToArray();
        var evidence = new SuggestionEvidence([], [(68_000_000L, .7), (175_000_000L, .6)], [], 44, false);
        var candidate = new CommercialBreakProposal(68_000_000, 175_000_000, Confidence.Low, false, "Uncertain: audio contradicts");
        Assert.Null(SuggestionEngine.Promote(evidence, profile, candidate));
    }
}
