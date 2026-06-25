using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdTrim.Models;

public enum SegmentState
{
    Default,
    Selected,
    Excluded,
    SelectedExcluded,
}

public sealed class Segment : INotifyPropertyChanged
{
    private long _startUs;
    private long _endUs;
    private SegmentState _state;
    private string? _label;

    /// <summary>Start time in microseconds.</summary>
    public long StartUs
    {
        get => _startUs;
        set { if (Set(ref _startUs, value)) Notify(nameof(DurationUs), nameof(Id)); }
    }

    /// <summary>End time in microseconds.</summary>
    public long EndUs
    {
        get => _endUs;
        set { if (Set(ref _endUs, value)) Notify(nameof(DurationUs), nameof(Id)); }
    }

    public long DurationUs => _endUs - _startUs;

    /// <summary>Deterministic ID: derived from start/end so excluded-state survives marker moves.</summary>
    public string Id => $"segment-{_startUs}-{_endUs}";

    public SegmentState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    public string? Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsExcluded => _state is SegmentState.Excluded or SegmentState.SelectedExcluded;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
