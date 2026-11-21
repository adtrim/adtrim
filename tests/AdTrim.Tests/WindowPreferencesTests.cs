using System.Text.Json;
using System.Windows;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// A recording window must open where it can be seen, never exactly on top of another window, and
// its saves must not undo what another window saved.
public sealed class WindowPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-winprefs-" + Guid.NewGuid().ToString("N"));
    private static readonly Rect Area = new(0, 0, 2560, 1400);
    public WindowPreferencesTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private static WindowPreferences With(UserPreferences p) { var prefs = new WindowPreferences(); prefs.Loaded(p); return prefs; }

    [Fact]
    public void ASavedWindowOffTheMonitorIsBroughtBackOnIt()
    {
        var place = With(new UserPreferences { Left = 5000, Top = -300, Width = 9000, Height = 800, IsMaximized = false })
            .Place(Area, 900, 600, []);
        Assert.Equal(new WindowPlacement(0, 0, 2560, 800, false), place);
    }

    [Fact]
    public void UnusableNumbersFallBackToTheDefaultSize()
    {
        var place = With(new UserPreferences { Left = double.NaN, Top = double.PositiveInfinity, Width = double.NaN, Height = double.NaN, IsMaximized = false })
            .Place(Area, 900, 600, []);
        Assert.Equal(new WindowPlacement(0, 0, 1280, 720, false), place);
    }

    // Builds before IsMaximized saved a maximized window as a normal one the size of the work area.
    [Fact]
    public void AnOldFakeMaximizedWindowOpensMaximizedWithANormalRestoreSize()
    {
        var prefs = With(new UserPreferences { Left = 0, Top = 0, Width = 2560, Height = 1400, IsMaximized = null });
        var place = prefs.Place(Area, 900, 600, []);
        Assert.True(place.Maximize);
        Assert.Equal((1280.0, 720.0), (place.Width, place.Height));
        Assert.Equal(1280, prefs.Current.Width);
        Assert.False(With(new UserPreferences { Left = 0, Top = 0, Width = 2560, Height = 1400, IsMaximized = false }).Place(Area, 900, 600, []).Maximize);
    }

    [Fact]
    public void ANewWindowStepsAsideFromOnesAlreadyAtTheSameSpot()
    {
        var prefs = With(new UserPreferences { Left = 100, Top = 100, Width = 1280, Height = 720, IsMaximized = false });
        var place = prefs.Place(Area, 900, 600, [new Point(100, 100), new Point(128, 128), new Point(400, 400)]);
        Assert.Equal((156.0, 156.0), (place.Left, place.Top));
        // Stepping stops at the edge of the work area instead of pushing the window off it.
        var full = With(new UserPreferences { Left = 1280, Top = 680, Width = 1280, Height = 720, IsMaximized = false })
            .Place(Area, 900, 600, [new Point(1280, 680)]);
        Assert.Equal((1280.0, 680.0), (full.Left, full.Top));
    }

    [Fact]
    public void TheSnapshotRecordsThisRecordingsPositionAndTheWindow()
    {
        var positions = Enumerable.Range(0, 20).ToDictionary(i => $"C:\\tv\\{i}.mp4", i => (long)i, StringComparer.OrdinalIgnoreCase);
        var prefs = With(new UserPreferences { Positions = positions });
        var vm = new MainViewModel { SourcePath = "C:\\TV\\5.mp4", PlayheadUs = 99, ShowWaveform = false };
        var snapshot = prefs.Snapshot(vm, new Rect(10, 20, 1300, 800), maximized: true);
        Assert.Equal(20, snapshot.Positions.Count);
        Assert.Equal(99, snapshot.Positions["C:\\TV\\5.mp4"]);
        Assert.True(snapshot.IsMaximized);
        Assert.False(snapshot.ShowWaveform);
        Assert.Equal(new Rect(10, 20, 1300, 800), new Rect(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height));
    }

    // A recording reopened from the list must count as the most recent. Dictionary reuses a removed
    // entry's slot, so re-adding it kept its old place and the next new recording evicted it.
    [Fact]
    public void TheOldestPositionIsForgottenNotTheOneJustSaved()
    {
        var positions = Enumerable.Range(0, 20).ToDictionary(i => $"C:\\tv\\{i}.mp4", i => (long)i, StringComparer.OrdinalIgnoreCase);
        var prefs = With(new UserPreferences { Positions = positions });
        prefs.Snapshot(new MainViewModel { SourcePath = "C:\\tv\\0.mp4", PlayheadUs = 500 }, new Rect(0, 0, 1000, 700), false);
        var snapshot = prefs.Snapshot(new MainViewModel { SourcePath = "C:\\tv\\new.mp4", PlayheadUs = 7 }, new Rect(0, 0, 1000, 700), false);
        Assert.Equal(20, snapshot.Positions.Count);
        Assert.Equal(500, snapshot.Positions["C:\\tv\\0.mp4"]);
        Assert.False(snapshot.Positions.ContainsKey("C:\\tv\\1.mp4"));
        Assert.Equal(["C:\\tv\\0.mp4", "C:\\tv\\new.mp4"], snapshot.Positions.Keys.TakeLast(2));
    }

    [Fact]
    public async Task ASaveDoesNotUndoASettingAnotherWindowSaved()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "preferences.json"), JsonSerializer.Serialize(new UserPreferences()));
        var prefs = With(await UserPreferences.ReadAsync(_directory));
        var other = await UserPreferences.UpdateAsync(p => p with { StartMuted = true, SectionNameTemplate = "Chapter {n}" }, _directory);
        prefs.ApplyShared(other);
        Assert.Equal("Chapter {n}", prefs.Current.SectionNameTemplate);
        var snapshot = prefs.Snapshot(new MainViewModel(), new Rect(1, 2, 1000, 700), maximized: false);
        await prefs.SaveAsync(snapshot, null, saveMarkerSource: false, _directory);
        var saved = await UserPreferences.ReadAsync(_directory);
        Assert.True(saved.StartMuted);
        Assert.Equal("Chapter {n}", saved.SectionNameTemplate);
        Assert.Equal(1000, saved.Width);
    }

    [Fact]
    public void TheDamagedSettingsBannerSaysWhatResetWillKeep()
    {
        Assert.EndsWith("using now.", With(new UserPreferences { AddSourceExportComment = true }).DamagedBannerBody);
        Assert.EndsWith(", with source export comments off.", With(new UserPreferences { AddSourceExportComment = false }).DamagedBannerBody);
    }
}
