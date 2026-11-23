using System.IO;
using AdTrim.Models;
using System.Text.Json;

namespace AdTrim.Services;

/// <summary>Markers a recording without a saved project starts with. Stored as a number:
/// append new values, never reorder.</summary>
public enum NewFileMarkerSource { Imported, AdTrim, None }

public sealed record PixelBounds(int Left, int Top, int Width, int Height);

public sealed record UserPreferences
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    // New installs show both; a saved file keeps its own values.
    public bool ShowWaveform { get; init; } = true;
    public bool ShowThumbnails { get; init; } = true;
    public ExportFormat ExportFormat { get; init; } = ExportFormat.Mp4;
    public bool DeleteSidecarAfterExport { get; init; }
    public string ExportAcceleration { get; init; } = "automatic";
    public bool AddSourceExportComment { get; init; } = true;
    public NewFileMarkerSource NewFileMarkerSource { get; init; } = NewFileMarkerSource.Imported;
    /// <summary>Name of segments the user has not named, with {n} for the number.</summary>
    public string SectionNameTemplate { get; init; } = SectionNames.DefaultTemplate;
    public bool ShowRulerTicks { get; init; } = true;
    public bool StartMuted { get; init; }
    /// <summary>Where exports are saved; null means beside the recording.</summary>
    public string? ExportFolder { get; init; }
    public string ExportFilenameTemplate { get; init; } = ExportNaming.DefaultTemplate;
    public bool? IsMaximized { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 720;
    /// <summary>The normal window's screen rectangle in physical pixels. Left/Top above are WPF units, which
    /// mean different things on monitors with different scaling; older builds read only those.</summary>
    public PixelBounds? WindowPixels { get; init; }
    public Dictionary<string, long> Positions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public static string DataDirectory => AppDataDirectory.Path;

    public static async Task<UserPreferences> LoadAsync(UserPreferences? fallback = null, string? dataDirectory = null)
    {
        try { return await ReadOrDefaultAsync(fallback, dataDirectory); }
        catch (IOException) { return fallback ?? new() { AddSourceExportComment = false }; }
        catch (UnauthorizedAccessException) { return fallback ?? new() { AddSourceExportComment = false }; }
    }

    public static async Task<UserPreferences> ReadAsync(string? dataDirectory = null)
    {
        try
        {
            var path = Path.Combine(dataDirectory ?? DataDirectory, "preferences.json");
            var loaded = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(path))
                ?? throw new DamagedPreferencesException();
            return loaded with { ExportFormat = ExportFormats.Normalize(loaded.ExportFormat) };
        }
        catch (JsonException ex) { throw new DamagedPreferencesException(ex); }
    }

    /// <summary>True when the settings file exists but its contents cannot be parsed.</summary>
    public static async Task<bool> IsDamagedAsync(string? dataDirectory = null)
    {
        try { await ReadAsync(dataDirectory); return false; }
        catch (DamagedPreferencesException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Stages <paramref name="known"/> and replaces the damaged settings file atomically, keeping its bytes as a backup.
    /// Returns the kept copy's path, or null when the file is no longer damaged (nothing is changed then).
    /// </summary>
    public static async Task<string?> ResetDamagedAsync(UserPreferences known, string? dataDirectory = null)
    {
        await SaveGate.WaitAsync();
        try
        {
            if (!await IsDamagedAsync(dataDirectory)) return null;
            string directory = dataDirectory ?? DataDirectory, path = Path.Combine(directory, "preferences.json");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string kept = Path.Combine(directory, $"preferences.damaged-{stamp}.json");
            if (File.Exists(kept)) kept = Path.Combine(directory, $"preferences.damaged-{stamp}-{Guid.NewGuid():N}.json");
            await (known with { ExportFormat = ExportFormats.Normalize(known.ExportFormat) }).WriteAsync(directory, kept);
            return kept;
        }
        finally { SaveGate.Release(); }
    }

    private static async Task<UserPreferences> ReadOrDefaultAsync(UserPreferences? fallback = null, string? dataDirectory = null)
    {
        try { return await ReadAsync(dataDirectory); }
        catch (FileNotFoundException) { return fallback ?? new(); }
        catch (DirectoryNotFoundException) { return fallback ?? new(); }
    }
    /// <summary>
    /// The saved positions with <paramref name="used"/> as the most recent, keeping the twenty most
    /// recently used. Removing and re-adding a dictionary entry does not move it to the end, so the
    /// order is rebuilt.
    /// </summary>
    internal static Dictionary<string, long> MergePositions(IEnumerable<KeyValuePair<string, long>>? saved, KeyValuePair<string, long>? used)
    {
        var entries = (saved ?? []).ToList();
        if (used is { } recent)
        {
            entries.RemoveAll(e => e.Key.Equals(recent.Key, StringComparison.OrdinalIgnoreCase));
            entries.Add(recent);
        }
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - 20))) result[entry.Key] = entry.Value;
        return result;
    }

    public async Task SaveChangesAsync(UserPreferences previous, string? source, bool saveMarkerSource = false, string? dataDirectory = null)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await ReadOrDefaultAsync(previous, dataDirectory);
            var positions = MergePositions(latest.Positions,
                source is not null && Positions.TryGetValue(source, out var position) ? new(source, position) : null);
            await (this with
            {
                ShowWaveform = ShowWaveform != previous.ShowWaveform ? ShowWaveform : latest.ShowWaveform,
                ShowThumbnails = ShowThumbnails != previous.ShowThumbnails ? ShowThumbnails : latest.ShowThumbnails,
                ExportFormat = ExportFormats.Normalize(ExportFormat != previous.ExportFormat ? ExportFormat : latest.ExportFormat),
                DeleteSidecarAfterExport = DeleteSidecarAfterExport != previous.DeleteSidecarAfterExport ? DeleteSidecarAfterExport : latest.DeleteSidecarAfterExport,
                ExportAcceleration = ExportAcceleration != previous.ExportAcceleration ? ExportAcceleration : latest.ExportAcceleration,
                AddSourceExportComment = AddSourceExportComment != previous.AddSourceExportComment ? AddSourceExportComment : latest.AddSourceExportComment,
                NewFileMarkerSource = saveMarkerSource || NewFileMarkerSource != previous.NewFileMarkerSource ? NewFileMarkerSource : latest.NewFileMarkerSource,
                SectionNameTemplate = SectionNameTemplate != previous.SectionNameTemplate ? SectionNameTemplate : latest.SectionNameTemplate,
                ShowRulerTicks = ShowRulerTicks != previous.ShowRulerTicks ? ShowRulerTicks : latest.ShowRulerTicks,
                StartMuted = StartMuted != previous.StartMuted ? StartMuted : latest.StartMuted,
                ExportFolder = ExportFolder != previous.ExportFolder ? ExportFolder : latest.ExportFolder,
                ExportFilenameTemplate = ExportFilenameTemplate != previous.ExportFilenameTemplate ? ExportFilenameTemplate : latest.ExportFilenameTemplate,
                Positions = positions,
            }).WriteAsync(dataDirectory);
        }
        finally { SaveGate.Release(); }
    }

    public static async Task SaveExportFormatAsync(ExportFormat format)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await ReadOrDefaultAsync();
            await (latest with { ExportFormat = ExportFormats.Normalize(format) }).WriteAsync();
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>The settings shown in the Settings window, taken from <paramref name="source"/>.
    /// Window position and playback positions stay this window's own.</summary>
    public UserPreferences WithSettingsFrom(UserPreferences source) => this with
    {
        ShowWaveform = source.ShowWaveform, ShowThumbnails = source.ShowThumbnails, ShowRulerTicks = source.ShowRulerTicks,
        StartMuted = source.StartMuted,
        ExportFormat = source.ExportFormat, DeleteSidecarAfterExport = source.DeleteSidecarAfterExport,
        ExportAcceleration = source.ExportAcceleration, AddSourceExportComment = source.AddSourceExportComment,
        NewFileMarkerSource = source.NewFileMarkerSource, SectionNameTemplate = source.SectionNameTemplate,
        ExportFolder = source.ExportFolder, ExportFilenameTemplate = source.ExportFilenameTemplate,
    };

    /// <summary>Applies one change to the latest saved settings and saves them, serialized with every other save.
    /// A damaged settings file is not overwritten.</summary>
    public static async Task<UserPreferences> UpdateAsync(Func<UserPreferences, UserPreferences> change, string? dataDirectory = null)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await ReadOrDefaultAsync(null, dataDirectory);
            var next = change(latest);
            next = next with
            {
                ExportFormat = ExportFormats.Normalize(next.ExportFormat),
                SectionNameTemplate = SectionNames.Normalize(next.SectionNameTemplate),
                ExportFilenameTemplate = ExportNaming.NormalizeTemplate(next.ExportFilenameTemplate),
                ExportFolder = string.IsNullOrWhiteSpace(next.ExportFolder) ? null : next.ExportFolder.Trim(),
            };
            await next.WriteAsync(dataDirectory);
            return next;
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>Returns every setting to its default and keeps a copy of the file it replaces. Window
    /// positions and playback positions are kept; they are not settings. Returns the saved settings
    /// and the kept copy's path, or null when there was no file.</summary>
    public static async Task<(UserPreferences Saved, string? Kept)> ResetAllAsync(string? dataDirectory = null)
    {
        await SaveGate.WaitAsync();
        try
        {
            string directory = dataDirectory ?? DataDirectory, path = Path.Combine(directory, "preferences.json");
            UserPreferences latest;
            try { latest = await ReadOrDefaultAsync(null, dataDirectory); }
            catch (DamagedPreferencesException) { latest = new(); }
            var next = new UserPreferences
            {
                IsMaximized = latest.IsMaximized, Left = latest.Left, Top = latest.Top, Width = latest.Width, Height = latest.Height,
                WindowPixels = latest.WindowPixels, Positions = latest.Positions,
            };
            string? kept = null;
            if (File.Exists(path))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                kept = Path.Combine(directory, $"preferences.before-reset-{stamp}.json");
                if (File.Exists(kept)) kept = Path.Combine(directory, $"preferences.before-reset-{stamp}-{Guid.NewGuid():N}.json");
            }
            await next.WriteAsync(directory, kept);
            return (next, kept);
        }
        finally { SaveGate.Release(); }
    }

    public static async Task SaveDeleteSidecarAfterExportAsync(bool delete)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await ReadOrDefaultAsync();
            await (latest with { DeleteSidecarAfterExport = delete }).WriteAsync();
        }
        finally { SaveGate.Release(); }
    }

    public async Task SaveAsync()
    {
        await SaveGate.WaitAsync();
        try { await WriteAsync(); }
        finally { SaveGate.Release(); }
    }

    private async Task WriteAsync(string? dataDirectory = null, string? backupPath = null)
    {
        var directory = dataDirectory ?? DataDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this));
            if (backupPath is not null) File.Replace(temp, path, backupPath);
            else if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>The settings file exists but its contents cannot be parsed. Saves refuse to overwrite it.</summary>
public sealed class DamagedPreferencesException(Exception? inner = null)
    : IOException("Saved preferences are unreadable.", inner);
