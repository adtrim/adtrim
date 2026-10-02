using System.IO;
using System.Text.Json;

namespace AdTrim.Services;

public sealed record UserPreferences
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public bool ShowWaveform { get; init; }
    public bool ShowThumbnails { get; init; }
    public string ExportAcceleration { get; init; } = "automatic";
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
        try { return JsonSerializer.Deserialize<UserPreferences>(await File.ReadAllTextAsync(path)) ?? new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }
    public async Task SaveChangesAsync(UserPreferences previous, string? source)
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
                ExportAcceleration = ExportAcceleration != previous.ExportAcceleration ? ExportAcceleration : latest.ExportAcceleration,
                Positions = positions,
            }).WriteAsync();
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
