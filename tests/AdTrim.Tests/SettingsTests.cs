using System.Text.Json;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

// The Settings window saves every change at once and shares it with every open window. These
// rules keep that from losing a change, overwriting a damaged file, or resetting what is not a setting.
public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    // Records compare dictionaries by reference; playback positions are checked on their own.
    private static readonly Dictionary<string, long> NoPositions = new();
    private static UserPreferences Comparable(UserPreferences p) => p with { Positions = NoPositions };
    private string PreferencesPath => Path.Combine(_directory, "preferences.json");

    [Fact]
    public void ExistingSettingsFilesKeepTodaysBehaviorForTheNewSettings()
    {
        var old = JsonSerializer.Deserialize<UserPreferences>("{}")!;
        Assert.True(old.ShowRulerTicks);
        Assert.False(old.FrameAccurateSeek);
        Assert.False(old.StartMuted);
        Assert.Null(old.ExportFolder);
        Assert.Equal("{title} [AdTrim]", old.ExportFilenameTemplate);
    }

    [Fact]
    public async Task AChangeIsSavedOnTopOfTheLatestFileAndCleanedUp()
    {
        await File.WriteAllTextAsync(PreferencesPath, JsonSerializer.Serialize(new UserPreferences { Width = 1500, ShowWaveform = false }));
        var saved = await UserPreferences.UpdateAsync(p => p with
        {
            StartMuted = true, ExportFolder = "  D:\\Exports  ", ExportFilenameTemplate = "no title here", SectionNameTemplate = "Chapter",
        }, _directory);
        var read = await UserPreferences.ReadAsync(_directory);
        Assert.Equal(Comparable(saved), Comparable(read));
        // Window size and the other window's display choice are not touched by a settings change.
        Assert.Equal(1500, read.Width);
        Assert.False(read.ShowWaveform);
        Assert.True(read.StartMuted);
        Assert.Equal("D:\\Exports", read.ExportFolder);
        // Unusable patterns never reach the file.
        Assert.Equal(ExportNaming.DefaultTemplate, read.ExportFilenameTemplate);
        Assert.Equal(SectionNames.DefaultTemplate, read.SectionNameTemplate);
    }

    [Fact]
    public async Task AWindowsLaterSaveDoesNotUndoASettingAnotherWindowChanged()
    {
        await File.WriteAllTextAsync(PreferencesPath, "{}");
        var windowLoaded = await UserPreferences.ReadAsync(_directory);
        var changed = await UserPreferences.UpdateAsync(p => p with { FrameAccurateSeek = true, ExportFilenameTemplate = "{title} [AdTrim {version}]" }, _directory);
        // The window that did not change them saves only what it changed itself.
        var windowSave = windowLoaded with { Width = 1400 };
        await windowSave.SaveChangesAsync(windowLoaded, null, dataDirectory: _directory);
        var read = await UserPreferences.ReadAsync(_directory);
        Assert.True(read.FrameAccurateSeek);
        Assert.Equal(changed.ExportFilenameTemplate, read.ExportFilenameTemplate);
        Assert.Equal(1400, read.Width);
    }

    [Fact]
    public async Task ADamagedFileIsNeverOverwrittenByASettingsChange()
    {
        await File.WriteAllTextAsync(PreferencesPath, "{");
        await Assert.ThrowsAsync<DamagedPreferencesException>(() => UserPreferences.UpdateAsync(p => p with { StartMuted = true }, _directory));
        Assert.Equal("{", await File.ReadAllTextAsync(PreferencesPath));
    }

    [Fact]
    public async Task ResetReturnsSettingsToDefaultsAndKeepsWindowPositionsAndACopy()
    {
        var before = new UserPreferences
        {
            AddSourceExportComment = false, StartMuted = true, ExportFolder = "D:\\Exports", Width = 1500, Left = 40,
            Positions = new(StringComparer.OrdinalIgnoreCase) { ["D:\\tv\\a.ts"] = 5 },
        };
        await File.WriteAllTextAsync(PreferencesPath, JsonSerializer.Serialize(before));
        string original = await File.ReadAllTextAsync(PreferencesPath);

        var (saved, kept) = await UserPreferences.ResetAllAsync(_directory);

        Assert.True(saved.AddSourceExportComment);
        Assert.False(saved.StartMuted);
        Assert.Null(saved.ExportFolder);
        Assert.Equal(1500, saved.Width);
        Assert.Equal(40, saved.Left);
        Assert.Equal(5, saved.Positions["D:\\tv\\a.ts"]);
        Assert.Equal(original, await File.ReadAllTextAsync(kept!));
        Assert.StartsWith("preferences.before-reset-", Path.GetFileName(kept));
        Assert.Equal(Comparable(saved), Comparable(await UserPreferences.ReadAsync(_directory)));
    }

    [Fact]
    public async Task ResetAlsoRecoversFromADamagedFile()
    {
        await File.WriteAllTextAsync(PreferencesPath, "{");
        var (saved, kept) = await UserPreferences.ResetAllAsync(_directory);
        Assert.Equal("{", await File.ReadAllTextAsync(kept!));
        Assert.Equal(Comparable(saved), Comparable(await UserPreferences.ReadAsync(_directory)));
    }

    [Theory]
    [InlineData("{title} [AdTrim]", null)]
    [InlineData("{title} [AdTrim {version}]", null)]
    [InlineData("{date} {title}", null)]
    [InlineData("[AdTrim]", "Include {title}")]
    [InlineData("{title} {season}", "Use only")]
    [InlineData("{title}: cut", "Remove characters")]
    [InlineData("{title}.", "cannot end with a period")]
    [InlineData("   ", "Enter a filename")]
    public void AFilenamePatternMustNameEachRecordingAndBeAWindowsFilename(string template, string? error)
    {
        var actual = ExportNaming.TemplateError(template);
        if (error is null) Assert.Null(actual);
        else Assert.Contains(error, actual);
    }

    [Fact]
    public void TheVersionAndDateCanBePartOfTheExportName()
    {
        string folder = Path.Combine(_directory, "exports");
        Directory.CreateDirectory(folder);
        string source = Path.Combine(_directory, "Show - S01E02 - Title.mp4");
        string named = ExportNaming.DeriveDefaultFilename(source, folder, ExportFormat.Mp4, "{title} [AdTrim {version}]");
        Assert.Equal($"Show - s01e02 - Title [AdTrim {AppVersion.Numeric}].mp4", named);
        // A second export of the same recording gets a number instead of replacing the first.
        File.WriteAllText(Path.Combine(folder, named), "earlier export");
        Assert.Equal($"Show - s01e02 - Title [AdTrim {AppVersion.Numeric}] (2).mp4",
            ExportNaming.DeriveDefaultFilename(source, folder, ExportFormat.Mp4, "{title} [AdTrim {version}]"));
        // Exporting that export again does not stack a second AdTrim marker.
        Assert.Equal($"Show - s01e02 - Title [AdTrim {AppVersion.Numeric}] (2).mp4",
            ExportNaming.DeriveDefaultFilename(Path.Combine(folder, named), folder, ExportFormat.Mp4, "{title} [AdTrim {version}]"));
        Assert.Equal($"{DateTime.Now:yyyy-MM-dd} Show - s01e02 - Title.ts",
            ExportNaming.DeriveDefaultFilename(source, folder, ExportFormat.Ts, "{date} {title}"));
    }

    [Fact]
    public void TheDefaultPatternNamesExportsExactlyAsBefore()
    {
        string folder = Path.Combine(_directory, "default");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Show [AdTrim].mp4"), "earlier export");
        Assert.Equal("Show [AdTrim 2].mp4", ExportNaming.DeriveDefaultFilename(Path.Combine(_directory, "Show.mp4"), folder));
        Assert.Equal("Show [AdTrim 2].mp4", ExportNaming.DeriveDefaultFilename(Path.Combine(_directory, "Show.mp4"), folder, ExportFormat.Mp4, "broken {pattern"));
    }
}
