namespace CircuitRF.Ui.Commands;

/// <summary>
/// Implemented by every editable document (schematic, symbol editor, and future data display).
/// Exposes the document's own <see cref="UndoRedoStack"/> so the workspace can route its
/// global Undo/Redo commands to whichever document is currently active.
/// </summary>
public interface IUndoableDocument
{
    UndoRedoStack UndoRedo { get; }
}
