using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class MarkerSuggestionStoreTests
{
    [Fact]
    public void CacheRestoresOnlyValidatedBaselineAndInvalidatesChangedSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MarkerSuggestionStore(directory);
            var fingerprint = new SourceFingerprint(1000, 1, 100);
            var key = store.Key(Path.Combine(directory, "source.mp4"), fingerprint);
            var state = new MarkerSetState([new("a", 50, SplitSource.Manual, null, null, false, "A|;\"B")],
                ["segment-50-100"], null, MarkerSetSource.AdTrim, null);
            state = state with { BaselineId = state.ContentId() };
            store.Save(key, state);
            Assert.Equal(state.BaselineId, store.Load(key, 100)?.BaselineId);
            Assert.NotEqual(key, store.Key(Path.Combine(directory, "source.mp4"), fingerprint with { ModifiedUnixMs = 2 }));
            Assert.Null(store.Load(key, 49));
            var invalid = state with { ExcludedSegmentIds = ["segment-0-100"] };
            store.Save(key, invalid with { BaselineId = invalid.ContentId() });
            Assert.Null(store.Load(key, 100));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void BaselineTracksFinalExclusionAndIgnoresGeneratedIds()
    {
        var state = new MarkerSetState([new("a", 50, SplitSource.Manual, null, null, false)],
            ["segment-50-100"], null, MarkerSetSource.AdTrim, null);
        Assert.NotEqual(state.ContentId(), (state with { ExcludedSegmentIds = [] }).ContentId());
        Assert.Equal(state.ContentId(), (state with { Splits = [state.Splits[0] with { Id = "other" }] }).ContentId());
        var vm = new AdTrim.ViewModels.MainViewModel { DurationUs = 100 };
        vm.CommandStack.Execute(new ReplaceMarkerSetCommand(vm, state with { BaselineId = state.ContentId() }, "Apply"));
        Assert.False(vm.IsTimelineEdited);
        vm.Segments.Last().State = SegmentState.Default;
        Assert.True(vm.IsTimelineEdited);
    }
}
