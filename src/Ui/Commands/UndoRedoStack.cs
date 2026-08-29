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

    // Kept in lockstep with the two stacks above: entry N's EditSequence stamp. A parallel stack
    // rather than a tuple element, so nothing that peeks at a command has to be rewritten.
    private readonly Stack<long> _undoStamps = new();
    private readonly Stack<long> _redoStamps = new();

    /// <summary>
    /// The <see cref="EditSequence"/> stamp of the entry Undo would take next, or 0 when there is
    /// none. Compared against another history's own to answer "which of these did the user do last" —
    /// see <see cref="EditSequence"/> for why that question needs asking at all.
    /// </summary>
    public long TopUndoStamp => _undoStamps.Count > 0 ? _undoStamps.Peek() : 0;

    /// <summary>The same, for the entry Redo would take next.</summary>
    public long TopRedoStamp => _redoStamps.Count > 0 ? _redoStamps.Peek() : 0;

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
        _undoStamps.Push(EditSequence.Next());
        _redoStack.Clear();
        _redoStamps.Clear();
        Refresh();
    }

    /// <summary>
    /// Moves one entry from the undo side to the redo side, then undoes it.
    /// </summary>
    /// <remarks>
    /// <b>The entry moves BEFORE the command runs, and that ordering is the fix to a real corruption</b>
    /// (owner-reported, 2026-08-28: undoing repeatedly, or alternating undo and redo quickly,
    /// eventually loses the history).
    ///
    /// <para><c>cmd.Undo()</c> raises the model's <c>Changed</c> event, which every open editor
    /// listens to — so arbitrary view-model code runs in the middle of this method. Popping the
    /// command, running it, and only then pushing it and its stamp left a window in which the four
    /// stacks disagreed with each other, and anything that reached <see cref="Execute"/> from inside
    /// that window pushed a stamp this method then POPPED as though it were its own. One such event
    /// and <c>_undoStack</c> and <c>_undoStamps</c> are permanently out of lockstep:
    /// <see cref="TopUndoStamp"/> starts naming the wrong entry, which is what the Match Designer's
    /// one-gesture-one-entry amend reads to decide whether to call this method at all.</para>
    ///
    /// <para>Doing the whole move first makes every observable state consistent for the duration. A
    /// nested <see cref="Execute"/> then clears a redo stack this entry is already on — which is the
    /// ordinary meaning of "an edit was made after an undo", not a corruption — and the stamps stay
    /// paired with their commands whatever happens.</para>
    ///
    /// <para><b>The try/finally is the other half.</b> A handler that throws — Avalonia catches it and
    /// the application carries on — used to leave the popped command on neither stack, gone for good,
    /// with the stamps short by one. Now the move stands and only the command's own effect is
    /// incomplete.</para>
    /// </remarks>
    public void Undo()
    {
        if (!_undoStack.TryPop(out var cmd)) return;
        _redoStack.Push(cmd);
        // The entry keeps the stamp it was recorded with: undo MOVES a cursor through history, it
        // does not add to it. Re-stamping here would make the next Ctrl+Z pick this same history
        // again forever.
        _redoStamps.Push(_undoStamps.Count > 0 ? _undoStamps.Pop() : 0);
        try { cmd.Undo(); }
        finally { Refresh(); }
    }

    /// <inheritdoc cref="Undo"/>
    public void Redo()
    {
        if (!_redoStack.TryPop(out var cmd)) return;
        _undoStack.Push(cmd);
        _undoStamps.Push(_redoStamps.Count > 0 ? _redoStamps.Pop() : 0);
        try { cmd.Execute(); }
        finally { Refresh(); }
    }

    /// <summary>Clear both stacks — called when a workspace is opened or new'd.</summary>
    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _undoStamps.Clear();
        _redoStamps.Clear();
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
