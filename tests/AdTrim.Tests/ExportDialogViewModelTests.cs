using AwesomeAssertions;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class ExportNamingTests
{
    [Fact]
    public void DefaultFilename_LowercasesS00E00Token()
    {
        var name = ExportNaming.DeriveDefaultFilename(
            @"D:\Recorded TV\The Rookie (2018)\Season 08\The Rookie (2018) - S08E18 - The Bandit.mp4",
            unixTimestamp: 1_700_000_000);
        name.Should().Be("The Rookie (2018) - s08e18 - The Bandit-ADT-1700000000.mp4");
    }

    [Fact]
    public void DefaultFilename_NoSourceFallsBackToPlaceholder()
    {
        ExportNaming.DeriveDefaultFilename(null).Should().Be("export-ADT.mp4");
    }

    [Theory]
    [InlineData("clean.mp4", true)]
    [InlineData("with spaces.mp4", true)]
    [InlineData("bad<char.mp4", false)]
    [InlineData("bad/slash.mp4", false)]
    [InlineData("", false)]
    public void IsValidFilename_HonorsWindowsRules(string name, bool expected)
    {
        ExportNaming.IsValidFilename(name).Should().Be(expected);
    }
}
