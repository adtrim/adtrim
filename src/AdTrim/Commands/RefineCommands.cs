using AdTrim.Models;
using AdTrim.ViewModels;

namespace AdTrim.Commands;

/// <summary>One marker's worth of refinement state - captured pre-execute so undo is symmetric.</summary>
public sealed record RefineMutation(
    Split Marker,
    long FromUs, long ToUs,
    Confidence? FromConfidence, Confidence? ToConfidence,
    long? FromOriginalTimeUs, long? ToOriginalTimeUs);

/// <summary>
/// Batched refine command: all mutations from a single Refine-all (or
/// single-split refine) pass get one undo entry: a single combined undo
/// entry for the whole refine pass. Skipped (Unchanged) markers don't get a
/// mutation entry.
/// </summary>
public sealed class BatchedRefineCommand : IEditCommand
{
    private readonly MainViewModel _vm;
    private readonly IReadOnlyList<RefineMutation> _mutations;

    public BatchedRefineCommand(MainViewModel vm, IReadOnlyList<RefineMutation> mutations)
    {
        _vm = vm;
        _mutations = mutations;
    }

    public string Description =>
        _mutations.Count == 1 ? "Refine split" : $"Refine {_mutations.Count} splits";

    public void Do()
    {
        foreach (var m in _mutations)
        {
            m.Marker.TimeUs = m.ToUs;
            m.Marker.Confidence = m.ToConfidence;
            m.Marker.OriginalTimeUs = m.ToOriginalTimeUs;
        }
        _vm.RebuildSegmentsFromSplits();
    }

    public void Undo()
    {
        foreach (var m in _mutations)
        {
            m.Marker.TimeUs = m.FromUs;
            m.Marker.Confidence = m.FromConfidence;
            m.Marker.OriginalTimeUs = m.FromOriginalTimeUs;
        }
        _vm.RebuildSegmentsFromSplits();
    }
}
