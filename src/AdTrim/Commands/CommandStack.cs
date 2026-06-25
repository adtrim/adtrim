namespace AdTrim.Commands;

public interface IEditCommand
{
    string Description { get; }
    void Do();
    void Undo();
}

/// <summary>
/// In-memory undo/redo stack: covers add/move/delete splits,
/// toggle confirmed, toggle excluded, and refine (as a single batched
/// command). Never persisted to sidecar.
/// </summary>
public sealed class CommandStack
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? PeekUndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? PeekRedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public event EventHandler? Changed;

    public void Execute(IEditCommand command)
    {
        command.Do();
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var c = _undo.Pop();
        c.Undo();
        _redo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var c = _redo.Pop();
        c.Do();
        _undo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
