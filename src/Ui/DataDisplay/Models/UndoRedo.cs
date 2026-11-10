// ================================================================
//  UndoRedo.cs  —  Generic, reusable undo/redo command system.
//
//  Ported from splotRF/src/Models/UndoRedo.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.
//
//  HOW TO USE:
//  1. Implement IUndoableCommand for each reversible operation.
//       Execute()  performs or re-performs the operation.
//       Undo()     reverses it.
//
//  2. Create one UndoRedoManager per document/tab.
//
//  3a. Do(command)   — Executes the command, then pushes it onto the
//      undo stack.  Clears the redo stack.
//      Use when the action has NOT yet been applied.
//
//  3b. Push(command) — Pushes a command that has ALREADY been applied
//      (e.g. the final snapshot at drag-end) without calling Execute().
//      Clears the redo stack.
//      Use for drag-end / resize-end events where the mutation happened
//      incrementally during the drag.
//
//  4. Call Undo() / Redo() when the user invokes Ctrl+Z / Ctrl+Y.
//
//  5. Subscribe to StateChanged to keep Undo/Redo menu items or toolbar
//     buttons enabled/disabled whenever the stacks change.
// ================================================================

using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// A reversible operation.
/// <c>Execute()</c> performs or re-performs the action;
/// <c>Undo()</c> reverses it.
/// </summary>
public interface IUndoableCommand
{
    void Execute();
    void Undo();
}

/// <summary>
/// Wraps a sequence of commands into a single undoable unit.
/// <c>Execute()</c> runs the sub-commands in forward order;
/// <c>Undo()</c> runs them in reverse (last-in, first-undone).
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;

    public CompositeCommand(IReadOnlyList<IUndoableCommand> commands)
        => _commands = commands;

    public void Execute()
    {
        foreach (var cmd in _commands)
            cmd.Execute();
    }

    public void Undo()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }
}

/// <summary>
/// Manages an undo stack and a redo stack for a single document or tab.
/// All methods must be called from the UI thread.
/// </summary>
public sealed class UndoRedoManager
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();

    /// <summary>
    /// Raised whenever <see cref="CanUndo"/> or <see cref="CanRedo"/> may have changed.
    /// Subscribe to update menu-item / button enabled state.
    /// </summary>
    public event EventHandler? StateChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// The command that would be undone next, without disturbing either stack — the seam a caller
    /// needs to COALESCE a repeated gesture into the entry it already pushed, rather than filling
    /// the history with one step per wheel notch.
    /// </summary>
    public IUndoableCommand? Top => _undoStack.TryPeek(out var top) ? top : null;

    /// <summary>
    /// True while a command's <c>Execute</c> or <c>Undo</c> is running here. A change made then is
    /// the command's own, so the Data Display's edit recorder must not record it a second time.
    /// </summary>
    public bool IsApplying => _applying > 0;
    private int _applying;

    private void Applying(Action act)
    {
        _applying++;
        try { act(); }
        finally { _applying--; }
    }

    /// <summary>
    /// Calls <c>command.Execute()</c> then pushes it onto the undo stack.
    /// Clears the redo stack.
    /// </summary>
    public void Do(IUndoableCommand command)
    {
        Applying(command.Execute);
        _undoStack.Push(command);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pushes a command onto the undo stack WITHOUT calling <c>Execute()</c>.
    /// Clears the redo stack.
    /// </summary>
    public void Push(IUndoableCommand command)
    {
        _undoStack.Push(command);
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pops the most recent command, calls <c>Undo()</c>, then moves it to the redo stack.
    /// </summary>
    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undoStack.Pop();
        // An undo REWRITES the state every other breadcrumb is describing, and until now it left no
        // breadcrumb of its own. A user report carried two "addPlot (now 2)" notes six seconds
        // apart - the same count before both, and no removePlot between them, which made the trail
        // read as an add that silently failed. An untraced Ctrl+Z explains it exactly, and nothing
        // in the trail could say so. Ctrl+Z is a discrete keypress with no auto-repeat, so this
        // meets the same bar as every other note here.
        Gesture.Note("undo", $"{cmd.GetType().Name} (undo {_undoStack.Count}, redo {_redoStack.Count + 1})");
        Applying(cmd.Undo);
        _redoStack.Push(cmd);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pops the most recently undone command, calls <c>Execute()</c>,
    /// then moves it back to the undo stack.
    /// </summary>
    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redoStack.Pop();
        Gesture.Note("redo", $"{cmd.GetType().Name} (undo {_undoStack.Count + 1}, redo {_redoStack.Count})");
        Applying(cmd.Execute);
        _undoStack.Push(cmd);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Empties both stacks.  Call after opening a file to discard stale history.
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
