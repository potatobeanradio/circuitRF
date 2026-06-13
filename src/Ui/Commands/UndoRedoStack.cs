using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// Command stack backing Undo/Redo across all editors. Menu, toolbar, and keyboard shortcuts
/// all route through Execute(); editor-level mutations (placed in 6d) call Execute() with a
/// specific IUiCommand. The stack is reset when a new workspace is opened.
/// </summary>
public sealed partial class UndoRedoStack : ObservableObject
{
    private readonly Stack<IUiCommand> _undoStack = new();
    private readonly Stack<IUiCommand> _redoStack = new();

    // The command on top of the undo stack at the last save (MarkSaved); null = "saved at the
    // empty stack".  Dirty (IsModified) means the current top differs from this marker, so it
    // clears on undo back to the saved position and re-dirties on any edit after a save
    // (Execute clears the redo stack, so divergent branches are handled correctly).
    private IUiCommand? _savedCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoDescription))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RedoDescription))]
    private bool _canRedo;

    /// <summary>
    /// True when the current undo position differs from the last-saved position.
    /// This is the document "dirty" signal: it goes true on edit, false on undo back to the
    /// saved baseline, and false again immediately after <see cref="MarkSaved"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isModified;

    public string UndoDescription => _undoStack.TryPeek(out var cmd)
        ? $"Undo \"{cmd.Description}\""
        : "Undo";

    public string RedoDescription => _redoStack.TryPeek(out var cmd)
        ? $"Redo \"{cmd.Description}\""
        : "Redo";

    /// <summary>Execute a command and push it onto the undo stack. Clears the redo stack.</summary>
    public void Execute(IUiCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();
        Refresh();
    }

    public void Undo()
    {
        if (!_undoStack.TryPop(out var cmd)) return;
        cmd.Undo();
        _redoStack.Push(cmd);
        Refresh();
    }

    public void Redo()
    {
        if (!_redoStack.TryPop(out var cmd)) return;
        cmd.Execute();
        _undoStack.Push(cmd);
        Refresh();
    }

    /// <summary>Clear both stacks — called when a workspace is opened or new'd.</summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _savedCommand = null;
        Refresh();
    }

    /// <summary>
    /// Records the current undo position as the clean baseline (call after the document has been
    /// written to disk).  <see cref="IsModified"/> becomes false now, true again on the next edit,
    /// and false again if the user undoes back to this exact position.
    /// </summary>
    public void MarkSaved()
    {
        _savedCommand = _undoStack.Count > 0 ? _undoStack.Peek() : null;
        Refresh();
    }

    private void Refresh()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
        var top = _undoStack.Count > 0 ? _undoStack.Peek() : null;
        IsModified = !ReferenceEquals(top, _savedCommand);
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }
}
