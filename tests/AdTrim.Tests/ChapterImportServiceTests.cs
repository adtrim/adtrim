using AwesomeAssertions;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class ChapterImportServiceTests
{
    private const string ProbeChaptersJson = """
    {
      "chapters": [
        { "start_time": "0.000000",   "tags": { "title": "Part 1" } },
        { "start_time": "28.260000",  "tags": { "title": "Commercial 1" } },
        { "start_time": "606.972000", "tags": { "title": "Part 2" } },
        { "start_time": "606.970000", "tags": { "title": "Part 2 (duplicate)" } },
        { "start_time": "3594.660000", "tags": { "title": "End" } }
      ]
    }
    """;

    [Fact]
    public void Parse_PullsTimeAndTitleFromTagsTitle()
    {
        var parsed = ChapterImportService.ParseChapters(ProbeChaptersJson);
        parsed.Should().HaveCount(5);
        parsed[0].TimeUs.Should().Be(0);
        parsed[0].Title.Should().Be("Part 1");
        parsed[1].TimeUs.Should().Be(28_260_000);
    }

    [Fact]
    public void Normalize_DropsBookendsAndDedupesWithin250ms()
    {
        var parsed = ChapterImportService.ParseChapters(ProbeChaptersJson);
        // Real-source duration for The Rookie.
        var durationUs = 3_594_660_000L;
        var normalized = ChapterImportService.Normalize(parsed, durationUs);

        // 0 (start) and 3594.66 (end == duration) dropped.
        // 606.97 and 606.972 collapse to one entry.
        normalized.Should().HaveCount(2);
        normalized[0].TimeUs.Should().Be(28_260_000);
        normalized[1].TimeUs.Should().Be(606_970_000);
    }

    [Fact]
    public void Normalize_RejectsNegativeAndAtDuration()
    {
        var raw = new[]
        {
            new ChapterBoundary(-1_000_000, null),
            new ChapterBoundary(0, null),
            new ChapterBoundary(500_000_000, null),
            new ChapterBoundary(1_000_000_000, null),  // == duration
        };
        var normalized = ChapterImportService.Normalize(raw, 1_000_000_000);
        normalized.Should().ContainSingle()
            .Which.TimeUs.Should().Be(500_000_000);
    }
}
