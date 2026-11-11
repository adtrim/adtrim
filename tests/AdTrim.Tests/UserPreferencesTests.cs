using System.Text.Json;
using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.Models;
using Xunit;

namespace AdTrim.Tests;

public sealed class UserPreferencesTests
{
    [Fact]
    public async Task UnreadablePreferencesCannotEnableSourceWritesOrEraseKnownChoices()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            Assert.True((await UserPreferences.LoadAsync(dataDirectory: directory)).AddSourceExportComment);
            var known = new UserPreferences { AddSourceExportComment = false, ShowThumbnails = false };
            foreach (var contents in new[] { "{", "null" })
            {
                await File.WriteAllTextAsync(path, contents);
                Assert.False((await UserPreferences.LoadAsync(dataDirectory: directory)).AddSourceExportComment);
                Assert.Equal(known, await UserPreferences.LoadAsync(known, directory));
                await Assert.ThrowsAsync<DamagedPreferencesException>(() => UserPreferences.ReadAsync(directory));
                Assert.Equal(contents, await File.ReadAllTextAsync(path));
            }
            File.Delete(path);
            Assert.Equal(known, await UserPreferences.LoadAsync(known, directory));
            await Assert.ThrowsAsync<FileNotFoundException>(() => UserPreferences.ReadAsync(directory));
            await File.WriteAllTextAsync(path, "{\"AddSourceExportComment\":true}");
            Assert.True((await UserPreferences.ReadAsync(directory)).AddSourceExportComment);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ADamagedSettingsFileIsKeptAndReplacedOnlyWithTheKnownChoices()
    {
        // Saves refuse to overwrite a damaged file, so the reset is the only way back to working
        // settings; it must not lose the damaged bytes or turn source writes on by itself.
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"ShowThumbnails\":false,");
            Assert.True(await UserPreferences.IsDamagedAsync(directory));
            var startup = await UserPreferences.LoadAsync(dataDirectory: directory);
            Assert.False(startup.AddSourceExportComment);
            var known = startup with { ShowWaveform = false, Width = 1500 };

            var kept = await UserPreferences.ResetDamagedAsync(known, directory);

            Assert.Equal("{\"ShowThumbnails\":false,", await File.ReadAllTextAsync(kept!));
            Assert.StartsWith("preferences.damaged-", Path.GetFileName(kept));
            var reset = await UserPreferences.ReadAsync(directory);
            Assert.False(reset.AddSourceExportComment);
            Assert.False(reset.ShowWaveform);
            Assert.Equal(1500, reset.Width);
            Assert.False(await UserPreferences.IsDamagedAsync(directory));
            // A healthy file is never replaced by a reset.
            Assert.Null(await UserPreferences.ResetDamagedAsync(known with { ShowWaveform = true }, directory));
            Assert.False((await UserPreferences.ReadAsync(directory)).ShowWaveform);
            Assert.Single(Directory.GetFiles(directory, "preferences.damaged-*.json"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AResetPreparationFailureCannotMakeDamagedSettingsLookLikeAFirstLaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            await File.WriteAllTextAsync(path, "{");
            // Serialization fails before the replacement is ready. The damaged original must
            // remain at its normal path so the next launch cannot enable source writes by default.
            await Assert.ThrowsAsync<ArgumentException>(() => UserPreferences.ResetDamagedAsync(
                new UserPreferences { AddSourceExportComment = false, Width = double.NaN }, directory));
            Assert.Equal("{", await File.ReadAllTextAsync(path));
            Assert.False((await UserPreferences.LoadAsync(dataDirectory: directory)).AddSourceExportComment);
            Assert.Empty(Directory.GetFiles(directory, "preferences.damaged-*.json"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AResetPublicationFailureLeavesTheDamagedFileAndCanBeRetried()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        var known = new UserPreferences { AddSourceExportComment = false };
        try
        {
            await File.WriteAllTextAsync(path, "{");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => UserPreferences.ResetDamagedAsync(known, directory));
                Assert.Equal("{", await File.ReadAllTextAsync(path));
                Assert.False((await UserPreferences.LoadAsync(dataDirectory: directory)).AddSourceExportComment);
                Assert.Empty(Directory.GetFiles(directory, "preferences.damaged-*.json"));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            }
            var kept = await UserPreferences.ResetDamagedAsync(known, directory);
            Assert.Equal("{", await File.ReadAllTextAsync(kept!));
            Assert.False((await UserPreferences.ReadAsync(directory)).AddSourceExportComment);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AnUnreadableButUndamagedSettingsFileIsNotReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"AddSourceExportComment\":true}");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(await UserPreferences.IsDamagedAsync(directory));
                Assert.Null(await UserPreferences.ResetDamagedAsync(new UserPreferences(), directory));
            }
            Assert.Equal("{\"AddSourceExportComment\":true}", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SidecarDeletionStaysOffForExistingUsersAndBothChoicesSurviveReload()
    {
        Assert.False(JsonSerializer.Deserialize<UserPreferences>("{}")!.DeleteSidecarAfterExport);
        foreach (bool delete in new[] { true, false })
            Assert.Equal(delete, JsonSerializer.Deserialize<UserPreferences>(
                JsonSerializer.Serialize(new UserPreferences { DeleteSidecarAfterExport = delete }))!.DeleteSidecarAfterExport);
    }

    [Fact]
    public void ExistingPreferencesStartWithMp4AndBothFormatsRoundTrip()
    {
        Assert.Equal(ExportFormat.Mp4, JsonSerializer.Deserialize<UserPreferences>("{}")!.ExportFormat);
        foreach (var format in new[] { ExportFormat.Mp4, ExportFormat.Ts })
            Assert.Equal(format, JsonSerializer.Deserialize<UserPreferences>(JsonSerializer.Serialize(new UserPreferences { ExportFormat = format }))!.ExportFormat);
        Assert.Equal(ExportFormat.Mp4, ExportFormats.Normalize((ExportFormat)999));
    }

    [Fact]
    public void StartingMarkerChoices_KeepTheirStoredNumbers()
    {
        // preferences.json stores the choice as a number; reordering would silently change
        // what existing users start new recordings with.
        ((int)NewFileMarkerSource.Imported).Should().Be(0);
        ((int)NewFileMarkerSource.AdTrim).Should().Be(1);
        ((int)NewFileMarkerSource.None).Should().Be(2);
    }

    [Fact]
    public void NoStartingMarkers_SurvivesASaveAndLoad()
    {
        var saved = JsonSerializer.Serialize(new UserPreferences { NewFileMarkerSource = NewFileMarkerSource.None });
        JsonSerializer.Deserialize<UserPreferences>(saved)!.NewFileMarkerSource.Should().Be(NewFileMarkerSource.None);
        JsonSerializer.Deserialize<UserPreferences>("{}")!.NewFileMarkerSource.Should().Be(NewFileMarkerSource.Imported,
            "imported markers stay the default");
    }

    // Positions are kept for the twenty recordings used most recently, not the twenty opened first.
    [Fact]
    public void ReopeningARecordingKeepsItsPositionWhenOlderOnesAreDropped()
    {
        var saved = Enumerable.Range(1, 20).Select(i => new KeyValuePair<string, long>($@"D:\tv\show{i}.ts", i)).ToList();
        var afterReuse = UserPreferences.MergePositions(saved, new(@"d:\TV\show1.ts", 500));
        Assert.Equal(20, afterReuse.Count);
        Assert.Equal(500, afterReuse[@"D:\tv\show1.ts"]);

        var afterNew = UserPreferences.MergePositions(afterReuse, new(@"D:\tv\show21.ts", 21));
        Assert.Equal(20, afterNew.Count);
        Assert.True(afterNew.ContainsKey(@"D:\tv\show1.ts"));
        Assert.False(afterNew.ContainsKey(@"D:\tv\show2.ts"));
        Assert.Equal(afterReuse.Count, UserPreferences.MergePositions(afterReuse, null).Count);
    }
}
