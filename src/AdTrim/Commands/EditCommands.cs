using AdTrim.Models;
using AdTrim.ViewModels;

namespace AdTrim.Commands;

/// <summary>Add a split at a specific time. Undo removes it and rebuilds segments.</summary>
public sealed class AddSplitCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly Split _split;
    private HashSet<string>? _exclusions;

    public AddSplitCommand(MainViewModel vm, long timeUs, SplitSource source)
    {
        _vm = vm;
        _split = new Split { TimeUs = timeUs, Source = source, Confidence = null, Confirmed = false };
    }

    public string Description => "Add split";

    public void Do()
    {
        _exclusions ??= _vm.CaptureExclusions();
        var insertAt = 0;
        for (; insertAt < _vm.Markers.Count; insertAt++)
            if (_vm.Markers[insertAt].TimeUs > _split.TimeUs) break;
        _vm.Markers.Insert(insertAt, _split);
        _vm.RebuildSegmentsFromSplits();
    }

    public void Undo()
    {
        _vm.Markers.Remove(_split);
        _vm.RebuildSegmentsFromSplits(_exclusions);
    }
}

/// <summary>Move a split. Both endpoints stored for symmetric undo.</summary>
public sealed class MoveSplitCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly Split _split;
    private readonly long _from;
    private readonly long _to;
    private Confidence? _fromConfidence;

    public MoveSplitCommand(MainViewModel vm, Split split, long fromUs, long toUs)
    {
        _vm = vm;
        _split = split;
        _from = fromUs;
        _to = toUs;
    }

    public string Description => "Move split";

    public void Do()
    {
        // A user-initiated move invalidates the prior refinement confidence:
        // the new position is the user's call, not the refiner's, so the
        // marker should fall back to its Neutral look. Captured here (not
        // in the ctor) so redo restores the same pre-move confidence even
        // if it changed between executions.
        _fromConfidence = _split.Confidence;
        _split.Confidence = null;
        _split.TimeUs = _to;
        _vm.RebuildSegmentsFromSplits();
    }

    public void Undo()
    {
        _split.TimeUs = _from;
        _split.Confidence = _fromConfidence;
        _vm.RebuildSegmentsFromSplits();
    }
}

/// <summary>Delete a split. Stores original position so undo restores it.</summary>
public sealed class DeleteSplitCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly Split _split;
    private int _originalIndex;
    private HashSet<string>? _exclusions;

    public DeleteSplitCommand(MainViewModel vm, Split split)
    {
        _vm = vm;
        _split = split;
    }

    public string Description => "Delete split";

    public void Do()
    {
        _exclusions ??= _vm.CaptureExclusions();
        var adjacent = _vm.Segments.Where(s => s.StartUs == _split.TimeUs || s.EndUs == _split.TimeUs).ToArray();
        if (adjacent.Select(s => s.IsExcluded).Distinct().Count() > 1)
            _vm.StatusOverride = "Merged section kept. Undo restores the boundary and exclusions.";
        _originalIndex = _vm.Markers.IndexOf(_split);
        _vm.Markers.Remove(_split);
        _vm.RebuildSegmentsFromSplits();
    }

    public void Undo()
    {
        _vm.Markers.Insert(_originalIndex, _split);
        _vm.RebuildSegmentsFromSplits(_exclusions);
    }
}

public sealed class ToggleConfirmedCommand : IEditCommand
{
    private readonly Split _split;
    public ToggleConfirmedCommand(Split split) => _split = split;
    public string Description => _split.Confirmed ? "Unconfirm split" : "Confirm split";
    public void Do() => _split.Confirmed = !_split.Confirmed;
    public void Undo() => _split.Confirmed = !_split.Confirmed;
}

public sealed class SetConfirmedCommand : IEditCommand
{
    private readonly IReadOnlyList<Split> _splits;
    private readonly bool _confirmed;
    private readonly bool[] _oldValues;

    public SetConfirmedCommand(IEnumerable<Split> splits, bool confirmed)
    {
        _splits = splits.ToList();
        _confirmed = confirmed;
        _oldValues = _splits.Select(s => s.Confirmed).ToArray();
    }

    public string Description => _confirmed ? $"Confirm {_splits.Count} splits" : $"Unconfirm {_splits.Count} splits";

    public void Do()
    {
        foreach (var split in _splits) split.Confirmed = _confirmed;
    }

    public void Undo()
    {
        for (int i = 0; i < _splits.Count; i++)
            _splits[i].Confirmed = _oldValues[i];
    }
}

public sealed class RenameSegmentCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly Split _start;
    private readonly string? _before;
    private readonly string _after;

    public RenameSegmentCommand(MainViewModel vm, Segment segment, string name)
    {
        _vm = vm;
        _start = vm.Markers.Single(m => m.TimeUs == segment.StartUs);
        _before = _start.ChapterTitle;
        _after = name.Trim();
        if (_after.Length == 0) throw new ArgumentException("Enter a segment name.", nameof(name));
    }

    public string Description => "Rename segment";
    public void Do() => Apply(_after);
    public void Undo() => Apply(_before);
    private void Apply(string? name)
    {
        _start.ChapterTitle = name;
        _vm.RebuildSegmentsFromSplits();
    }
}

public sealed class ToggleExcludedCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly string _key;
    public ToggleExcludedCommand(MainViewModel vm, Segment segment)
    { _vm = vm; _key = segment.BoundaryKey; }
    public string Description => "Toggle exclusion";
    public void Do() => Flip();
    public void Undo() => Flip();
    private void Flip()
    {
        var segment = _vm.Segments.First(s => s.BoundaryKey == _key);
        segment.State = segment.State switch
        {
            SegmentState.Default => SegmentState.Excluded,
            SegmentState.Excluded => SegmentState.Default,
            SegmentState.Selected => SegmentState.SelectedExcluded,
            SegmentState.SelectedExcluded => SegmentState.Selected,
            _ => segment.State,
        };
    }
}
