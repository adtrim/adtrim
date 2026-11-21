using System.Windows;
using AdTrim.Services;

namespace AdTrim.ViewModels;

/// <summary>Where a recording window opens and how big it is.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximize);

/// <summary>
/// A recording window's preferences: what it uses now (<see cref="Current"/>), what it last saved
/// (<see cref="Saved"/>, so a save writes only this window's changes), where the window opens, and the
/// snapshot it saves. Settings other windows saved are taken in without being undone.
/// </summary>
public sealed class WindowPreferences
{
    /// <summary>Playback positions remembered, most recent last.</summary>
    public const int MaximumPositions = 20;
    /// <summary>How far a window steps aside from one already open at the same spot.</summary>
    public const double CascadeStep = 28;

    public UserPreferences Current { get; set; } = new();
    public UserPreferences Saved { get; private set; } = new();

    public void Loaded(UserPreferences preferences) { Current = preferences; Saved = preferences; }

    /// <summary>The saved window rectangle, with unusable numbers replaced by defaults.</summary>
    public Rect Desired => new(double.IsFinite(Current.Left) ? Current.Left : 0,
        double.IsFinite(Current.Top) ? Current.Top : 0,
        Math.Max(1, double.IsFinite(Current.Width) ? Current.Width : 1280),
        Math.Max(1, double.IsFinite(Current.Height) ? Current.Height : 720));

    /// <summary>
    /// Fits the saved window into <paramref name="area"/> (the work area of the monitor it was on).
    /// A normal window covering the whole work area, saved by builds before IsMaximized existed, opens
    /// maximized with a 1280×720 restore size. A window that would open exactly on top of another
    /// (<paramref name="openWindows"/>, top-left corners of normal windows) steps aside.
    /// </summary>
    public WindowPlacement Place(Rect area, double minWidth, double minHeight, IReadOnlyList<Point> openWindows)
    {
        var desired = Desired;
        // Older builds saved a work-area-sized normal window as fake maximize.
        bool legacyMaximized = Current.IsMaximized is null
            && Math.Abs(desired.Left - area.Left) <= 2 && Math.Abs(desired.Top - area.Top) <= 2
            && Math.Abs(desired.Width - area.Width) <= 2 && Math.Abs(desired.Height - area.Height) <= 2;
        if (legacyMaximized) Current = Current with { Width = 1280, Height = 720 };
        var width = Math.Clamp(double.IsFinite(Current.Width) ? Current.Width : 1280, minWidth, Math.Max(minWidth, area.Width));
        var height = Math.Clamp(double.IsFinite(Current.Height) ? Current.Height : 720, minHeight, Math.Max(minHeight, area.Height));
        var left = Math.Clamp(double.IsFinite(Current.Left) ? Current.Left : area.Left, area.Left, Math.Max(area.Left, area.Right - width));
        var top = Math.Clamp(double.IsFinite(Current.Top) ? Current.Top : area.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        if (Current.IsMaximized == true || legacyMaximized) return new(left, top, width, height, true);
        // Every window restores the same saved position; step aside instead of hiding the one already there.
        while (left + CascadeStep + width <= area.Right && top + CascadeStep + height <= area.Bottom
            && openWindows.Any(w => Math.Abs(w.X - left) < 1 && Math.Abs(w.Y - top) < 1))
        {
            left += CascadeStep;
            top += CascadeStep;
        }
        return new(left, top, width, height, false);
    }

    /// <summary>
    /// What this window saves: its display choices, its normal (restore) bounds and whether it is maximized,
    /// and the open recording's playhead as the most recent of at most <see cref="MaximumPositions"/> positions.
    /// </summary>
    public UserPreferences Snapshot(MainViewModel vm, Rect bounds, bool maximized)
    {
        var positions = new Dictionary<string, long>(Current.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
        if (vm.SourcePath is { } source)
        {
            positions.Remove(source);
            positions[source] = vm.PlayheadUs;
        }
        while (positions.Count > MaximumPositions) positions.Remove(positions.Keys.First());
        Current = Current with { ShowWaveform = vm.ShowWaveform, ShowThumbnails = vm.ShowThumbnails,
            ShowRulerTicks = vm.ShowRulerTicks,
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            IsMaximized = maximized, Positions = positions };
        return Current;
    }

    /// <summary>Saves what changed since the last save, on top of the latest file.</summary>
    public async Task SaveAsync(UserPreferences snapshot, string? source, bool saveMarkerSource, string? dataDirectory = null)
    {
        await snapshot.SaveChangesAsync(Saved, source, saveMarkerSource, dataDirectory);
        Saved = snapshot;
    }

    /// <summary>The new-recording marker setting as saved now; it is read fresh each time a recording opens.</summary>
    public void UseNewFileMarkerSource(NewFileMarkerSource source)
    {
        Current = Current with { NewFileMarkerSource = source };
        Saved = Saved with { NewFileMarkerSource = source };
    }

    /// <summary>Takes in settings another window saved. They are saved already, so this window's later saves must not undo them.</summary>
    public void ApplyShared(UserPreferences saved)
    {
        Current = Current.WithSettingsFrom(saved);
        Saved = Saved.WithSettingsFrom(saved);
    }

    /// <summary>Replaces a damaged settings file with what this window uses now; returns where the damaged one was kept.</summary>
    public async Task<string?> ResetDamagedAsync()
    {
        var snapshot = Current;
        var kept = await UserPreferences.ResetDamagedAsync(snapshot);
        Saved = snapshot;
        return kept;
    }

    public string DamagedBannerBody =>
        "Settings changes cannot be saved until they are reset. Reset keeps a copy of the damaged file and saves the settings this window is using now"
        + (Current.AddSourceExportComment ? "." : ", with source export comments off.");
}
