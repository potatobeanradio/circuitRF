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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoDescription))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RedoDescription))]
    private bool _canRedo;

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
        Refresh();
    }

    private void Refresh()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }
}
