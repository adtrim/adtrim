using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdTrim.Models;

public sealed class Split : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("n").Substring(0, 12);
    private long _timeUs;
    private long? _originalTimeUs;
    private Confidence? _confidence;
    private SplitSource _source = SplitSource.Chapter;
    private bool _confirmed;
    private bool _isSelected;
    private bool _showAudition;
    private string? _label;

    public string Id
    {
        get => _id;
        set => Set(ref _id, value);
    }

    /// <summary>Time in integer microseconds. The authoritative time unit.</summary>
    public long TimeUs
    {
        get => _timeUs;
        set => Set(ref _timeUs, value);
    }

    public long? OriginalTimeUs
    {
        get => _originalTimeUs;
        set => Set(ref _originalTimeUs, value);
    }

    public Confidence? Confidence
    {
        get => _confidence;
        set => Set(ref _confidence, value);
    }

    public SplitSource Source
    {
        get => _source;
        set => Set(ref _source, value);
    }

    public bool Confirmed
    {
        get => _confirmed;
        set => Set(ref _confirmed, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool ShowAudition
    {
        get => _showAudition;
        set => Set(ref _showAudition, value);
    }

    /// <summary>Optional label - used for Start/End bookend markers.</summary>
    public string? Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public bool IsBookend => _label is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
