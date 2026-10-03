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
            "The Rookie (2018) - S08E18 - The Bandit.mp4");
        name.Should().Be("The Rookie (2018) - s08e18 - The Bandit [AdTrim].mp4");
    }

    [Fact]
    public void DefaultFilename_NoSourceFallsBackToPlaceholder()
    {
        ExportNaming.DeriveDefaultFilename(null).Should().Be("export [AdTrim].mp4");
    }

    [Fact]
    public void RepeatExports_SkipExistingFilesAndDirectoriesWithoutChangingThem()
    {
        var folder = Path.Combine(Path.GetTempPath(), "adtrim-naming-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "Show - s01e02 - Title [AdTrim].mp4");
            File.WriteAllText(first, "original export");
            Directory.CreateDirectory(Path.Combine(folder, "Show - s01e02 - Title [AdTrim 2].mp4"));
            ExportNaming.DeriveDefaultFilename("Show - S01E02 - Title.mp4", folder)
                .Should().Be("Show - s01e02 - Title [AdTrim 3].mp4");
            ExportNaming.DeriveDefaultFilename(first, folder)
                .Should().Be("Show - s01e02 - Title [AdTrim 3].mp4");
            File.ReadAllText(first).Should().Be("original export");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("Z:")]
    [InlineData("not\0a-folder")]
    public void IncompleteDestination_DoesNotInterruptFilenameEditing(string folder)
    {
        ExportNaming.DeriveDefaultFilename("Show.mp4", folder).Should().Be("Show [AdTrim].mp4");
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
