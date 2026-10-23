using System.Text.Json;
using AwesomeAssertions;
using AdTrim.Services;
using AdTrim.Models;
using Xunit;

namespace AdTrim.Tests;

public sealed class UserPreferencesTests
{
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
}
