using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class ExportDisplayTitleTests
{
    [Theory]
    [InlineData("Survivor (2000) - S51E02 - Weaponized Honesty.mp4", "Survivor · Season 51, Episode 2", "Weaponized Honesty")]
    [InlineData("Scrubs - s02e01 - My First Time in a While.mp4", "Scrubs · Season 2, Episode 1", "My First Time in a While")]
    [InlineData("Family vacation.mp4", "Family vacation", "")]
    [InlineData("Show - S01E01E02 - Double episode.mp4", "Show - S01E01E02 - Double episode", "")]
    public void ShowIsProminentAndUnrecognizedNamesArePreserved(string filename, string title, string subtitle)
    {
        Assert.Equal((title, subtitle), ExportNaming.DisplayTitles(filename));
    }
}
