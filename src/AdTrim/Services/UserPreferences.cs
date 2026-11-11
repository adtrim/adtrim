using System.IO;
using AdTrim.Models;
using System.Text.Json;

namespace AdTrim.Services;

/// <summary>Markers a recording without a saved project starts with. Stored as a number:
/// append new values, never reorder.</summary>
public enum NewFileMarkerSource { Imported, AdTrim, None }

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
    public bool? IsMaximized { get; init; }
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 720;
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

    public async Task SaveChangesAsync(UserPreferences previous, string? source, bool saveMarkerSource = false)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await ReadOrDefaultAsync(previous);
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
                Positions = positions,
            }).WriteAsync();
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
