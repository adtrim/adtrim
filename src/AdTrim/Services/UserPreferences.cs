using System.IO;
using System.Text.Json;

namespace AdTrim.Services;

public sealed record UserPreferences
{
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
    public async Task SaveAsync()
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
