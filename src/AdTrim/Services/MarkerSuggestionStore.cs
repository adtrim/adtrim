using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdTrim.Commands;
using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>Bounded app-data cache for original analysis proposals, never user edits.</summary>
public sealed class MarkerSuggestionStore
{
    private const string DetectorVersion = SuggestionEngine.Version;
    private const int MaximumEntries = 100;
    private const long MaximumBytes = 50L * 1024 * 1024;
    private readonly string _directory;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public MarkerSuggestionStore(string? dataDirectory = null)
        => _directory = Path.Combine(dataDirectory ?? UserPreferences.DataDirectory, "marker-suggestions");

    public string Key(string sourcePath, SourceFingerprint fingerprint)
    {
        var identity = $"{Path.GetFullPath(sourcePath).ToUpperInvariant()}|{fingerprint.SizeBytes}|{fingerprint.DurationUs}|{fingerprint.ModifiedUnixMs}|{DetectorVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public MarkerSetState? Load(string key, long durationUs)
    {
        var path = Path.Combine(_directory, key + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Options);
            if (entry is null || entry.State is null || entry.Version != DetectorVersion || entry.State.Source != MarkerSetSource.AdTrim
                || entry.State.BaselineId is null || !StringComparer.Ordinal.Equals(key, entry.Key)
                || !IsValid(entry.State, durationUs)
                || entry.State.BaselineId != entry.State.ContentId()) return null;
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return entry.State;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return null; }
    }

    public void Save(string key, MarkerSetState state)
    {
        if (state.Source != MarkerSetSource.AdTrim || state.BaselineId is null) throw new ArgumentException("Only a complete AdTrim suggestion set can be cached.", nameof(state));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, key + ".json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(key, DetectorVersion, DateTime.UtcNow, state), Options));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Trim();
    }

    private void Trim()
    {
        var entries = new DirectoryInfo(_directory).GetFiles("*.json").OrderBy(f => f.LastAccessTimeUtc).ToArray();
        long total = entries.Sum(f => f.Length);
        for (int i = 0; i < entries.Length && (entries.Length - i > MaximumEntries || total > MaximumBytes); i++)
        { total -= entries[i].Length; entries[i].Delete(); }
    }

    private static bool IsValid(MarkerSetState state, long durationUs)
    {
        if (durationUs <= 0 || state.Splits is null || state.ExcludedSegmentIds is null
            || state.Splits.Any(s => s is null) || state.ExcludedSegmentIds.Any(id => id is null)) return false;
        var boundaries = new HashSet<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long previous = 0;
        foreach (var split in state.Splits)
        {
            if (string.IsNullOrWhiteSpace(split.Id) || !ids.Add(split.Id) || split.TimeUs <= previous
                || split.TimeUs >= durationUs || !Enum.IsDefined(split.Source)
                || (split.Confidence is { } confidence && !Enum.IsDefined(confidence))) return false;
            boundaries.Add($"segment-{previous}-{split.TimeUs}");
            previous = split.TimeUs;
        }
        boundaries.Add($"segment-{previous}-{durationUs}");
        return state.ExcludedSegmentIds.All(boundaries.Contains)
            && state.ExcludedSegmentIds.Distinct(StringComparer.Ordinal).Count() == state.ExcludedSegmentIds.Count;
    }

    private sealed record Entry(string Key, string Version, DateTime CreatedUtc, MarkerSetState State);
}
