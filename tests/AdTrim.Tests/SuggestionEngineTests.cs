using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class SuggestionEngineTests
{
    [Fact]
    public void DialogueAnchorDoesNotOverrideAnExistingBlackBoundaryWithAProgramShot()
    {
        var scores = Enumerable.Range(0, 600).Select(i => i >= 150 && i < 390 ? 0.0 : 1.0).ToArray();
        var dialogue = Enumerable.Range(0, 600).Select(i => i < 149 || i >= 310).ToArray();
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L), (309_800_000L, 310_000_000L)],
            [(149_000_000L, .99), (150_000_000L, .7), (390_000_000L, .8)], scores, 44, false) { CentreDialogue = dialogue };
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 600_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
    }

    [Fact]
    public void RecordedEvidenceRoundTripPreservesTimesAndOptionalAudio()
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L)], [(150_016_683L, .8)], [.1, .9], 44, true)
            { CentreDialogue = [false, true] };
        var restored = System.Text.Json.JsonSerializer.Deserialize<SuggestionEvidence>(System.Text.Json.JsonSerializer.Serialize(evidence, options), options)!;
        Assert.Equal(evidence.Black, restored.Black);
        Assert.Equal(evidence.Scenes, restored.Scenes);
        Assert.Equal(evidence.GraphicMatches, restored.GraphicMatches);
        Assert.Equal(evidence.CentreDialogue, restored.CentreDialogue);
        Assert.True(restored.FieldTransitions);
    }
    [Fact]
    public void DelayedGraphicDoesNotRemoveDialogueAfterABlackBoundedReturn()
    {
        var scores = Enumerable.Range(0, 600).Select(i => i >= 150 && i < 390 ? 0.0 : 1.0).ToArray();
        var dialogue = Enumerable.Range(0, 600).Select(i => i < 150 || i >= 310).ToArray();
        var evidence = new SuggestionEvidence([(301_800_000L, 302_000_000L), (309_800_000L, 310_000_000L)],
            [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (390_000_000L, .8)], scores, 44, false) { CentreDialogue = dialogue };
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 600_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
        Assert.False(proposal.AutoExclude);
    }

    [Fact]
    public void CentreEvidenceRejectsSilenceAndBalancedStereoButAcceptsDialogue()
    {
        static byte[] Samples(float left, float right, float centre)
            => new[] { left, right, centre }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.False(SuggestionEvidenceCollector.IsCentreDialogue(Samples(0, 0, 0)));
        Assert.False(SuggestionEvidenceCollector.IsCentreDialogue(Samples(.1f, .1f, .1f)));
        Assert.False(SuggestionEvidenceCollector.IsCentreDialogue(Samples(0, 0, .0001f)));
        Assert.False(SuggestionEvidenceCollector.IsCentreDialogue(Samples(0, 0, float.NaN)));
        Assert.True(SuggestionEvidenceCollector.IsCentreDialogue(Samples(.01f, .01f, .2f)));
    }

    [Fact]
    public void DialogueEndingBeforeThePictureCutDoesNotSelectTheLastProgramShot()
    {
        var scores = Enumerable.Range(0, 600).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var dialogue = Enumerable.Range(0, 600).Select(i => i < 148 || i >= 310).ToArray();
        var evidence = new SuggestionEvidence([(309_800_000L, 310_000_000L)],
            [(148_000_000L, .34), (150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (310_000_000L, .8)], scores, 44, false) { CentreDialogue = dialogue };
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 600_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
    }
    [Theory]
    [InlineData(10, 10)]
    [InlineData(280, 10)]
    [InlineData(10, 150)]
    [InlineData(280, 150)]
    public void RecurringGraphicIsLearnedInAnyCornerWithoutAChannelTemplate(int x, int y)
    {
        var graphic = new RecurringGraphic();
        var frame = new byte[RecurringGraphic.FrameBytes];
        for (int sample = 0; sample < 40; sample++)
        {
            Array.Fill(frame, (byte)(60 + sample));
            if (sample < 30)
                for (int yy = y; yy < y + 10; yy++)
                    for (int xx = x; xx < x + 10; xx++) frame[yy * RecurringGraphic.Width + xx] = 220;
            graphic.Observe(frame);
        }
        Assert.True(graphic.Complete());
        Assert.True(graphic.PatternPixels >= 5);
        Array.Fill(frame, (byte)80);
        Assert.Equal(0, graphic.Match(frame));
        for (int yy = y; yy < y + 10; yy++)
            for (int xx = x; xx < x + 10; xx++) frame[yy * RecurringGraphic.Width + xx] = 220;
        Assert.True(graphic.Match(frame) > .9);
    }

    [Fact]
    public void GraphicAbsenceAndDirectCutsAloneDoNotEstablishAdvertising()
    {
        var scores = Enumerable.Range(0, 650).Select(i => i >= 150 && i < 470 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(150_000_000L, .8), (470_000_000L, .8)], scores, 44, false);
        var uncertain = new List<CommercialBreakProposal>();
        Assert.Empty(SuggestionEngine.FindBreaks(evidence, 650_000_000, uncertain.Add));
        Assert.Contains("lacks corroborating break evidence", Assert.Single(uncertain).Evidence);
        Assert.False(uncertain[0].AutoExclude);
    }

    [Fact]
    public void RepeatedUnitsSupportALongDirectBreakWithoutAnArbitraryDurationLimit()
    {
        var scores = Enumerable.Range(0, 650).Select(i => i >= 150 && i < 470 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (470_000_000L, .8)], scores, 44, false);
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 650_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(470_000_000, proposal.EndUs);
        Assert.False(proposal.AutoExclude);
    }

    [Fact]
    public void ShortGraphicAbsenceAndSourceEdgeMaterialDoNotEstablishABreak()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i < 100 || (i >= 200 && i < 212) ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(100_000_000L, .8), (200_000_000L, .8), (212_000_000L, .8)], scores, 44, false);
        Assert.Empty(SuggestionEngine.FindBreaks(evidence, 500_000_000));
        Assert.Empty(SuggestionEngine.FindBreaks(evidence with { GraphicPixels = 0 }, 500_000_000));
    }

    [Fact]
    public void MixedFieldFrameIsExcludedOnEntryAndSkippedOnReturn()
    {
        long[] frames = [148_947_133, 148_980_500, 149_013_867];
        Assert.Equal(148_980_500, RefineService.SelectTransitionFrame(frames, 148_997_183, 500_000_000, true, true));
        Assert.Equal(149_013_867, RefineService.SelectTransitionFrame(frames, 148_997_183, 500_000_000, false, true));
        Assert.Null(RefineService.SelectTransitionFrame(frames, 148_997_183, 500_000_000, true, false));
    }

    [Fact]
    public void ReturnFadingUpThroughSeveralSecondsOfBlackEndsAtTheBlack()
    {
        // The graphic reappears once the program is visible; a later program cut must not become the return.
        var scores = Enumerable.Range(0, 600).Select(i => i >= 150 && i < 311 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L), (304_400_000L, 310_000_000L)],
            [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (316_000_000L, .8)], scores, 44, false);
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 600_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
    }

    // Blackdetect [1000000, 1200000) ends three frames into a fade; frames are 33367 us apart.
    private static readonly (long, int)[] FadeFrames =
        [(966_633, 90_000), (1_000_000, 0), (1_033_367, 0), (1_066_733, 0), (1_100_100, 129), (1_133_467, 2_024),
         (1_166_833, 3_358), (1_200_200, 80_000)];

    [Fact]
    public void ReturnThroughAFadeKeepsTheProgramsFaintOpeningFrames()
    {
        // Retain from the last fully black frame; blackdetect's end would remove three program frames.
        Assert.Equal(1_066_733, RefineService.SelectFadeEdge(FadeFrames, 1_000_000, 1_200_200, 35, entering: false));
    }

    [Fact]
    public void EntryThroughAFadeExcludesTheAdvertisementsFaintOpeningFrames()
    {
        Assert.Equal(1_100_100, RefineService.SelectFadeEdge(FadeFrames, 1_000_000, 1_200_200, 35, entering: true));
    }

    [Fact]
    public void CleanCutFromBlackKeepsBlackdetectsBoundary()
    {
        (long, int)[] frames = [(1_000_000, 0), (1_033_367, 0), (1_066_733, 90_000)];
        Assert.Null(RefineService.SelectFadeEdge(frames, 1_000_000, 1_066_733, 35, entering: false));
        Assert.Null(RefineService.SelectFadeEdge(frames, 1_000_000, 1_066_733, 35, entering: true));
    }

    [Fact]
    public void RecordingThatStartsInsideABreakGetsAnUncertainReturnMarker()
    {
        // Young Sheldon S01E02: no graphic for the first 26 s; the episode starts with a cut at 24.909 s.
        var scores = Enumerable.Range(0, 600).Select(i => i < 26 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(23_190_000L, .35), (24_909_000L, .5), (29_463_000L, .46)], scores, 641, false);
        var edge = Assert.Single(SuggestionEngine.EdgeCandidates(evidence, 600_000_000));
        Assert.Equal(0, edge.StartUs);
        Assert.Equal(24_909_000, edge.EndUs);
        Assert.False(edge.AutoExclude);
    }

    /// <summary>Speech at -30 dB with brief natural pauses, plus digital silence at the given seconds.</summary>
    private static byte[] Loudness(int seconds, params int[] splices)
    {
        var loudness = Enumerable.Range(0, seconds * 50).Select(i => i % 400 < 10 ? (byte)55 : (byte)90).ToArray();
        foreach (int s in splices) Array.Fill(loudness, (byte)0, s * 50 - 3, 6);
        return loudness;
    }

    [Fact]
    public void EdgeCandidatesAreReportedAsUncertainAndNeverPromoted()
    {
        var scores = Enumerable.Range(0, 600).Select(i => i < 40 || i >= 560 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(39_000_000L, .8), (560_000_000L, .8)], scores, 641, false)
            { Loudness = Loudness(600, 39, 560) };
        var uncertain = new List<CommercialBreakProposal>();
        Assert.Empty(SuggestionEngine.Classify(evidence, 600_000_000, uncertain.Add));
        Assert.Contains(uncertain, u => u.StartUs == 0 && u.EndUs == 39_000_000);
        Assert.Contains(uncertain, u => u.StartUs == 560_000_000 && u.EndUs == 600_000_000);
    }

    [Theory]
    [InlineData(.02, true)]   // Miami's logo-free intro and following scene: the recording's own program
    [InlineData(.14, false)]  // Survivor's pre-show: different enough to deserve a red marker
    public void CandidatesTheProfileCallsProgramAreNotMarked(double score, bool hidden)
    {
        var profile = Enumerable.Repeat(score, 600).ToArray();
        var candidate = new CommercialBreakProposal(68_000_000, 175_000_000, AdTrim.Models.Confidence.Low, false, "Uncertain");
        Assert.Equal(hidden, SuggestionEngine.LooksLikeProgram(profile, candidate));
        Assert.False(SuggestionEngine.LooksLikeProgram(null, candidate));
    }

    [Fact]
    public void ColdOpenWhereTheGraphicAppearsMidSceneIsNotMarked()
    {
        // Abbott cold opens: no graphic at first, but it appears without any station splice.
        var scores = Enumerable.Range(0, 600).Select(i => i < 46 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(45_940_000L, .5)], scores, 641, false) { Loudness = Loudness(600) };
        var uncertain = new List<CommercialBreakProposal>();
        SuggestionEngine.Classify(evidence, 600_000_000, uncertain.Add);
        Assert.DoesNotContain(uncertain, u => u.StartUs == 0);
    }

    [Fact]
    public void ReturnKeepsTheStationGraphicOverBlackBeforeTheProgram()
    {
        // Young Sheldon: the graphic sits on black for 12 frames before the picture returns.
        var frames = Enumerable.Range(0, 14).Select(i => (416_690_000L + i * 16_683L, i < 12 ? 5_333 : 90_000, i < 12)).ToArray();
        Assert.Equal(416_690_000, RefineService.SelectGraphicOverBlackReturn(frames, 416_690_000, 416_890_196, 92));
    }

    [Fact]
    public void ReturnKeepsTheGraphicEvenWhenTheProgramFadesInUnderIt()
    {
        // Young Sheldon break 2: 16 graphic-only frames, then the picture fades in beneath the graphic.
        var frames = Enumerable.Range(0, 34).Select(i => (1_073_613_300L + i * 16_683L, 5_331 + Math.Max(0, i - 16) * 300, i < 18)).ToArray();
        Assert.Equal(1_073_613_300, RefineService.SelectGraphicOverBlackReturn(frames, 1_073_613_300, 1_074_180_533, 92));
    }

    [Fact]
    public void ReturnIgnoresDarkFramesThatAreNotJustAStationGraphic()
    {
        // Dim picture spread over the frame (an advertisement fading out) is not program-side.
        var frames = Enumerable.Range(0, 6).Select(i => (1_000_000L + i * 33_367L, 5_333, false)).ToArray();
        Assert.Null(RefineService.SelectGraphicOverBlackReturn(frames, 1_000_000, 1_166_835, 92));
    }

    [Fact]
    public void StationGraphicOverBlackIsNotFullyBlack()
    {
        // A logo stays visible through the dark interval, so there is no fully black frame to move to.
        (long, int)[] frames = [(1_000_000, 1_777), (1_033_367, 1_777), (1_066_733, 1_777), (1_100_100, 90_000)];
        Assert.Null(RefineService.SelectFadeEdge(frames, 1_000_000, 1_100_100, 35, entering: true));
    }

    [Fact]
    public void EvenAnApparentlyCompleteGraphicGapIsOnlyAReviewSuggestion()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (310_000_000L, .8)], scores, 44, false);
        Assert.False(Assert.Single(SuggestionEngine.FindBreaks(evidence, 500_000_000)).AutoExclude);
    }

    [Fact]
    public void RapidEditingDoesNotDestroyTheContextOfACompleteBlackBoundedBreak()
    {
        var scores = Enumerable.Range(0, 650).Select(i => i >= 150 && i < 470 ? 0.0 : 1.0).ToArray();
        var cuts = Enumerable.Range(0, 600).Select(i => (151_000_000L + i * 500_000L, .9)).ToArray();
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L), (299_800_000L, 300_000_000L),
            (469_800_000L, 470_000_000L)], cuts, scores, 44, false);
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 650_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(470_000_000, proposal.EndUs);
    }

    [Fact]
    public void SmoothedGraphicReturnDoesNotChooseAnAdvertisementCutBeforeTheProgramReturns()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (305_000_000L, .9), (310_000_000L, .8)], scores, 44, false);
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 500_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
    }

    [Fact]
    public void ContrastingProgramLikeAudioInsideAGapVetoesEvenBlackBoundedCandidates()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L), (309_800_000L, 310_000_000L)],
            [(180_000_000L, .8), (210_000_000L, .8)], scores, 44, false)
            { CentreDialogue = Enumerable.Range(0, 500).Select(i => i < 150 || i >= 310 || (i >= 200 && i < 260)).ToArray() };
        var uncertain = new List<CommercialBreakProposal>();
        Assert.Empty(SuggestionEngine.FindBreaks(evidence, 500_000_000, uncertain.Add));
        Assert.Contains("program-like audio contradicts", Assert.Single(uncertain).Evidence);
    }

    [Fact]
    public void IdenticalCentreMixesInProgramAndAdsCannotVetoBlackBoundaries()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([(149_800_000L, 150_000_000L), (309_800_000L, 310_000_000L)], [], scores, 44, false)
            { CentreDialogue = Enumerable.Repeat(true, 500).ToArray() };
        var proposal = Assert.Single(SuggestionEngine.FindBreaks(evidence, 500_000_000));
        Assert.Equal(150_000_000, proposal.StartUs);
        Assert.Equal(310_000_000, proposal.EndUs);
        Assert.False(proposal.AutoExclude);
    }

    [Fact]
    public void GloballyUsefulAudioDoesNotProveAnEntryWithoutLocalAgreement()
    {
        var scores = Enumerable.Range(0, 500).Select(i => i >= 150 && i < 310 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([(309_800_000L, 310_000_000L)],
            [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (310_000_000L, .8)], scores, 44, false)
            { CentreDialogue = Enumerable.Range(0, 500).Select(i => i < 120 || i >= 310).ToArray() };
        var uncertain = new List<CommercialBreakProposal>();
        Assert.Empty(SuggestionEngine.FindBreaks(evidence, 500_000_000, uncertain.Add));
        Assert.Contains("no local agreement", Assert.Single(uncertain).Evidence);
    }

    [Fact]
    public void CadenceWithoutASupportedReturnDoesNotEstablishACompleteBreak()
    {
        var scores = Enumerable.Range(0, 600).Select(i => i >= 150 && i < 325 ? 0.0 : 1.0).ToArray();
        var evidence = new SuggestionEvidence([], [(150_000_000L, .8), (180_000_000L, .8), (210_000_000L, .8), (315_000_000L, .8)], scores, 44, false);
        Assert.Empty(SuggestionEngine.FindBreaks(evidence, 600_000_000));
    }

    // An uncertain candidate's time comes from one-second samples. On a progressive recording
    // it was never a frame time, and the candidate was hidden for lack of an exact match.
    [Fact]
    public void AnUncertainCandidateTakesTheFrameBesideItButNotOneAcrossAGap()
    {
        long[] frames = [10_000_000, 10_016_683, 10_033_367, 10_500_000];
        Assert.Null(RefineService.SelectTransitionFrame(frames, 10_020_000, 500_000_000, true, false));
        Assert.Equal(10_016_683, RefineService.SelectTransitionFrame(frames, 10_020_000, 500_000_000, true, false, nearest: true));
        Assert.Equal(10_033_367, RefineService.SelectTransitionFrame(frames, 10_033_000, 500_000_000, false, false, nearest: true));
        Assert.Null(RefineService.SelectTransitionFrame(frames, 10_250_000, 500_000_000, true, false, nearest: true));
    }
}
