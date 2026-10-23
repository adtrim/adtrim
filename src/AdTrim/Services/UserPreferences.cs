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
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 720;
    public Dictionary<string, long> Positions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public static string DataDirectory => Environment.GetEnvironmentVariable("ADTRIM_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdTrim");

    public static async Task<UserPreferences> LoadAsync()
    {
        var path = Path.Combine(DataDirectory, "preferences.json");
        if (!File.Exists(path)) return new();
        try
        {
            var loaded = JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(path)) ?? new();
            return loaded with { ExportFormat = ExportFormats.Normalize(loaded.ExportFormat) };
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }
    public async Task SaveChangesAsync(UserPreferences previous, string? source, bool saveMarkerSource = false)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await LoadAsync();
            var positions = new Dictionary<string, long>(latest.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
            if (source is not null && Positions.TryGetValue(source, out var position))
            {
                positions.Remove(source);
                positions[source] = position;
            }
            while (positions.Count > 20) positions.Remove(positions.Keys.First());
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
            var latest = await LoadAsync();
            await (latest with { ExportFormat = ExportFormats.Normalize(format) }).WriteAsync();
        }
        finally { SaveGate.Release(); }
    }

    public static async Task SaveDeleteSidecarAfterExportAsync(bool delete)
    {
        await SaveGate.WaitAsync();
        try
        {
            var latest = await LoadAsync();
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

    private async Task WriteAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "preferences.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
