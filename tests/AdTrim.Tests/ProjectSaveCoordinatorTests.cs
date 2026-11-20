using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using Xunit;

namespace AdTrim.Tests;

// Rules from the workspace guide: only a completed save of the current revision can report Saved,
// saves are serialized, and edits are flushed before switching recordings or closing.
public sealed class ProjectSaveCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adtrim-save-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly ProjectStore _store;
    private readonly FakeAutosave _autosave = new();
    private readonly List<string> _logged = new();
    private int _closeRequests;
    private static readonly MediaInfo Media = new(30_000_000, "h264", 1920, 1080, new Rational(30, 1), [new AudioStream(1, "ac3", 2, true)], 1);

    public ProjectSaveCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "Show.mp4");
        File.WriteAllBytes(_source, new byte[2048]);
        _store = new ProjectStore(Path.Combine(_directory, "data", "projects"));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class FakeAutosave : IAutosave
    {
        public int Restarts, Stops;
        public bool Running;
        public void Start() => Running = true;
        public void Restart() { Restarts++; Running = true; }
        public void Stop() { Stops++; Running = false; }
    }

    private ProjectSaveCoordinator Coordinator(Func<AdTrimProject, string>? write = null, bool hasRecording = true)
        => new(new ProjectSession(write), () => hasRecording ? new SaveTarget(_store, Media, ProjectStore.FingerprintOf(_source, Media.DurationUs)) : null,
            _autosave, (what, ex) => _logged.Add(what + ": " + ex.Message), () => _closeRequests++);

    private MainViewModel EditedProject()
    {
        var vm = EditingRegressionTests.Project();
        vm.SourcePath = _source;
        vm.CommandStack.Execute(new ToggleExcludedCommand(vm, vm.Segments[1]));
        Assert.True(vm.IsDirty);
        return vm;
    }

    [Fact]
    public async Task ACompletedSaveOfTheCurrentRevisionReportsSaved()
    {
        var vm = EditedProject();
        var saves = Coordinator();
        Assert.True(await saves.SaveAsync(vm));
        Assert.False(vm.IsDirty);
        Assert.Equal("Saved", vm.SaveStatus);
        var saved = _store.Load(_source, Media);
        Assert.Equal(SidecarLoadStatus.Loaded, saved.Status);
        Assert.Single(saved.Project!.ExcludedSegmentIds);
    }

    // An edit made while the save is writing is not in that save. Reporting Saved would lose it.
    [Fact]
    public async Task AnEditDuringASaveIsNotReportedAsSavedAndIsSavedNext()
    {
        var vm = EditedProject();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var saves = Coordinator(snapshot => { writing.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); return _store.Save(snapshot); });
        var save = saves.SaveAsync(vm);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(saves.InProgress);
        Assert.Equal("Saving", vm.SaveStatus);
        vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[2], 20_000_000, 21_000_000));
        release.Set();
        Assert.True(await save);
        Assert.False(saves.InProgress);
        Assert.True(vm.IsDirty);
        Assert.Equal("Unsaved changes", vm.SaveStatus);
        Assert.True(_autosave.Running);
    }

    [Fact]
    public async Task AFailedSaveKeepsTheEditsAndOffersRetry()
    {
        var vm = EditedProject();
        var saves = Coordinator(_ => throw new IOException("The disk is full."));
        Assert.False(await saves.SaveAsync(vm));
        Assert.True(vm.IsDirty);
        Assert.Equal("Save failed", vm.SaveStatus);
        Assert.Equal(StatusKind.Danger, vm.Banner!.Kind);
        Assert.Equal("The disk is full.", vm.Banner.Body);
        Assert.Equal("Retry", Assert.Single(vm.Banner.Actions).Label);
        Assert.False(saves.InProgress);
    }

    [Fact]
    public async Task NothingIsWrittenWithoutEditsOrWithoutARecording()
    {
        int writes = 0;
        var clean = EditingRegressionTests.Project();
        clean.SourcePath = _source;
        Assert.True(await Coordinator(_ => { writes++; return "x"; }).SaveAsync(clean));
        var edited = EditedProject();
        Assert.False(await Coordinator(_ => { writes++; return "x"; }, hasRecording: false).SaveAsync(edited));
        Assert.Equal(0, writes);
        Assert.True(edited.IsDirty);
    }

    // Edits that cannot be written beside the recording are kept in AdTrim's data folder; the user
    // is told once per recording, and an existing banner is not replaced.
    [Fact]
    public async Task SavingAwayFromTheRecordingIsMentionedOncePerRecording()
    {
        var saves = Coordinator(_ => Path.Combine(_directory, "data", "projects", "elsewhere.adt.json"));
        var vm = EditedProject();
        await saves.SaveAsync(vm);
        Assert.Equal("Edits are saved on this computer only.", vm.Banner!.Title);
        vm.Banner = null;
        vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[2], 20_000_000, 21_000_000));
        await saves.SaveAsync(vm);
        Assert.Null(vm.Banner);

        var besides = Coordinator(_store.Save);
        var other = EditedProject();
        await besides.SaveAsync(other);
        Assert.Null(other.Banner);
    }

    [Fact]
    public async Task FlushSavesEveryEditBeforeLeaving()
    {
        var vm = EditedProject();
        var saves = Coordinator();
        _autosave.Restart();
        Assert.True(await saves.FlushAsync(vm, () => throw new InvalidOperationException("Not asked when the save works")));
        Assert.False(vm.IsDirty);
        Assert.False(_autosave.Running);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AFailedFlushAsksAndFollowsTheAnswer()
    {
        int attempts = 0;
        var flaky = Coordinator(snapshot => ++attempts < 3 ? throw new IOException("Locked") : _store.Save(snapshot));
        var retried = EditedProject();
        var answers = new Queue<SaveFailureChoice>([SaveFailureChoice.Retry, SaveFailureChoice.Retry]);
        Assert.True(await flaky.FlushAsync(retried, answers.Dequeue));
        Assert.False(retried.IsDirty);
        Assert.Equal(3, attempts);

        var failing = Coordinator(_ => throw new IOException("Locked"));
        var stay = EditedProject();
        Assert.False(await failing.FlushAsync(stay, () => SaveFailureChoice.Stay));
        Assert.True(stay.IsDirty);
        var discard = EditedProject();
        Assert.True(await failing.FlushAsync(discard, () => SaveFailureChoice.Discard));
        Assert.False(discard.IsDirty);
    }

    [Fact]
    public async Task WaitingForIdleWaitsForTheSaveInProgress()
    {
        var vm = EditedProject();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var saves = Coordinator(snapshot => { writing.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); return _store.Save(snapshot); });
        var save = saves.SaveAsync(vm);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = saves.WaitForIdleAsync();
        await Task.Delay(100);
        Assert.False(idle.IsCompleted);
        release.Set();
        await save;
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Windows does not wait at logoff or shutdown, so the last edits are written at once.
    [Fact]
    public void SessionEndWritesPendingEditsImmediately()
    {
        var vm = EditedProject();
        Coordinator().SaveNow(vm);
        Assert.False(vm.IsDirty);
        Assert.Equal(SidecarLoadStatus.Loaded, _store.Load(_source, Media).Status);
        Assert.Empty(_logged);
    }

    // A close asked for during a switch waits for it, goes ahead after a successful flush, and is
    // dropped when the user chose to stay. Left set, it would close the window after the next open.
    [Fact]
    public void APendingCloseRunsOnceOrIsDropped()
    {
        var saves = Coordinator();
        Assert.False(saves.TakePendingClose(true));
        saves.CloseAfterSwitch();
        Assert.True(saves.TakePendingClose(true));
        Assert.Equal(1, _closeRequests);
        Assert.False(saves.TakePendingClose(true));
        saves.CloseAfterSwitch();
        Assert.True(saves.TakePendingClose(false));
        Assert.Equal(1, _closeRequests);
        saves.CloseAfterSwitch();
        saves.DropPendingClose();
        Assert.False(saves.TakePendingClose(true));
        Assert.Equal(1, _closeRequests);
    }
}
