namespace AdTrim.Models;

/// <summary>
/// Timeline filmstrip frames for one recording, sorted by source time (µs). Immutable: adding
/// tiles returns a new strip, so the timeline redraws when the reference changes.
/// </summary>
public sealed class ThumbnailStrip
{
    public static readonly ThumbnailStrip Empty = new([], []);

    private readonly long[] _times;
    private readonly string[] _paths;

    private ThumbnailStrip(long[] times, string[] paths)
    {
        _times = times;
        _paths = paths;
    }

    public int Count => _times.Length;

    public IReadOnlyList<long> Times => _times;

    public bool Contains(long timeUs) => Array.BinarySearch(_times, timeUs) >= 0;

    public ThumbnailStrip With(IEnumerable<(long TimeUs, string Path)> tiles)
    {
        var merged = new SortedDictionary<long, string>();
        for (int i = 0; i < _times.Length; i++) merged[_times[i]] = _paths[i];
        foreach (var (time, path) in tiles) merged[time] = path;
        return new(merged.Keys.ToArray(), merged.Values.ToArray());
    }

    /// <summary>The tile closest in time, preferring the earlier one on a tie; null when empty.</summary>
    public (long TimeUs, string Path)? Nearest(long timeUs)
    {
        if (_times.Length == 0) return null;
        int i = Array.BinarySearch(_times, timeUs);
        if (i >= 0) return (_times[i], _paths[i]);
        i = ~i;
        if (i == 0) return (_times[0], _paths[0]);
        if (i == _times.Length) return (_times[^1], _paths[^1]);
        return timeUs - _times[i - 1] <= _times[i] - timeUs ? (_times[i - 1], _paths[i - 1]) : (_times[i], _paths[i]);
    }
}
