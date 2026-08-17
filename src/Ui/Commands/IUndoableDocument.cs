namespace CircuitRF.Ui.Commands;

/// <summary>
/// Implemented by every editable document (schematic, symbol editor, and future data display).
/// Exposes the document's own <see cref="UndoRedoStack"/> so the workspace can route its
/// global Undo/Redo commands to whichever document is currently active.
/// </summary>
public interface IUndoableDocument
{
    UndoRedoStack UndoRedo { get; }

    /// <summary>
    /// Undoes the document's most recent edit, and tells the workspace whether there is one.
    ///
    /// <para><b>Why this is not just <see cref="UndoRedo"/>.</b> A document may hold more than one edit
    /// history: a layout showing a wirebond cell (wbond.md WB40) has its own command stack AND the
    /// wires' snapshot stack, and the two cannot be merged — one replays commands, the other restores
    /// whole-design snapshots. Ctrl+Z nevertheless asks one question, so the document is what answers
    /// it. <see cref="EditSequence"/> is how the answer is made total.</para>
    ///
    /// <para>Defaulted to the single-stack behaviour, so every document type that has exactly one
    /// history — schematic, symbol editor, tech editor, data display — needs no code and keeps
    /// behaving identically.</para>
    /// </summary>
    void UndoLast() => UndoRedo.Undo();

    /// <inheritdoc cref="UndoLast"/>
    void RedoLast() => UndoRedo.Redo();

    /// <summary>Whether <see cref="UndoLast"/> has anything to do — what gates the Undo command.</summary>
    bool CanUndoLast => UndoRedo.CanUndo;

    /// <summary>Whether <see cref="RedoLast"/> has anything to do.</summary>
    bool CanRedoLast => UndoRedo.CanRedo;

    /// <summary>
    /// What the Undo menu item and toolbar tooltip should say — the description of the entry
    /// <see cref="UndoLast"/> would actually take, which is not necessarily the command stack's when a
    /// document holds more than one history.
    /// </summary>
    string UndoLastDescription => UndoRedo.UndoDescription;

    /// <inheritdoc cref="UndoLastDescription"/>
    string RedoLastDescription => UndoRedo.RedoDescription;
}
