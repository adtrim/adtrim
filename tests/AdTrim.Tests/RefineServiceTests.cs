using AwesomeAssertions;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class RefineServiceTests
{
    [Fact]
    public void ParseCandidates_PullsPtsTimeAndPictType()
    {
        const string json = """
        {
          "frames": [
            { "pts_time": "10.000000", "pict_type": "I" },
            { "pts_time": "10.033367", "pict_type": "P" },
            { "pts_time": "10.066734", "pict_type": "B" }
          ]
        }
        """;
        var parsed = RefineService.ParseCandidates(json);
        parsed.Should().HaveCount(3);
        parsed[0].timeUs.Should().Be(10_000_000);
        parsed[0].pictType.Should().Be('I');
        parsed[2].pictType.Should().Be('B');
    }

    [Fact]
    public void ParseSignals_BlackdetectWindowsAreOffsetByWindowStart()
    {
        const string stderr = "[blackdetect @ 0x1] black_start:0.500 black_end:0.700 black_duration:0.200\n";
        var signals = RefineService.ParseSignals(stderr, offsetUs: 10_000_000);
        signals.BlackWindows.Should().ContainSingle();
        signals.BlackWindows[0].start.Should().Be(10_500_000);
        signals.BlackWindows[0].end.Should().Be(10_700_000);
    }

    [Fact]
    public void ParseSignals_SilencePairsAreMatchedInOrder()
    {
        const string stderr = """
        [silencedetect @ 0x1] silence_start: 0.100
        [silencedetect @ 0x1] silence_end: 0.500 | silence_duration: 0.400
        [silencedetect @ 0x1] silence_start: 1.000
        [silencedetect @ 0x1] silence_end: 1.200
        """;
        var signals = RefineService.ParseSignals(stderr, offsetUs: 0);
        signals.SilenceWindows.Should().HaveCount(2);
        signals.SilenceWindows[0].start.Should().Be(100_000);
        signals.SilenceWindows[0].end.Should().Be(500_000);
        signals.SilenceWindows[1].start.Should().Be(1_000_000);
        signals.SilenceWindows[1].end.Should().Be(1_200_000);
    }

    [Fact]
    public void ParseSignals_SceneScoresPickUpPtsTimeAndValue()
    {
        const string stderr = """
        frame:0    pts:0       pts_time:0.000
        lavfi.scene_score=0.05
        frame:30   pts:1000    pts_time:1.001
        lavfi.scene_score=0.85
        """;
        var signals = RefineService.ParseSignals(stderr, offsetUs: 0);
        // SceneRx greedily reads pts_time and the next lavfi.scene_score.
        signals.SceneScores.Should().NotBeEmpty();
        // Highest reported score should resolve via SceneScoreAt's nearest-neighbor.
        signals.SceneScoreAt(1_001_000).Should().BeApproximately(0.85, 0.01);
    }

    [Fact]
    public void ParseSignals_RealFFmpegShape_FindsSceneScores()
    {
        // Real ffmpeg `metadata=print` output has a `[Parsed_metadata_N @ HEX]`
        // prefix on every line, and emits the pts_time + scene_score on
        // SEPARATE lines (one frame's data spans two lines). Verify the regex
        // matches across that line break with the prefix present.
        const string stderr =
            "[Parsed_metadata_3 @ 0000012cd51e5cc0] frame:0    pts:2965    pts_time:0.0329444\r\n" +
            "[Parsed_metadata_3 @ 0000012cd51e5cc0] lavfi.scene_score=0.728309\r\n" +
            "[Parsed_metadata_3 @ 0000012cd51e5cc0] frame:1    pts:4467    pts_time:0.0496333\r\n" +
            "[Parsed_metadata_3 @ 0000012cd51e5cc0] lavfi.scene_score=0.842744\r\n";
        var signals = RefineService.ParseSignals(stderr, offsetUs: 258_000_000);
        signals.SceneScores.Should().HaveCount(2,
            because: "two frames in the sample → two scene-score lines");
        signals.SceneScoreAt(258_032_944).Should().BeApproximately(0.728, 0.01);
        signals.SceneScoreAt(258_049_633).Should().BeApproximately(0.842, 0.01);
    }
}
