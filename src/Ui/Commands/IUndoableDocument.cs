namespace CircuitRF.Ui.Commands;

/// <summary>
/// A document the shell's Undo/Redo commands can act on — the whole contract those commands need,
/// and deliberately nothing about HOW the history is kept.
///
/// <para><b>Why this is separate from <see cref="IUndoableDocument"/>.</b> Every editor built on
/// <see cref="UndoRedoStack"/> answers these six questions through that stack, and
/// <see cref="IUndoableDocument"/> is exactly that shortcut. A Data Display does not: it keeps its
/// own <c>UndoRedoManager</c> (ported whole from the standalone plotter, with a per-tab stack and a
/// window-level one beside it), so it can answer the six questions but has no
/// <c>UndoRedoStack</c> to hand over. Routing Undo through the smaller contract is what lets the
/// shell — and the macOS menu bar, which is app-global and reaches every window — send Ctrl/Cmd+Z
/// to a Data Display at all.</para>
/// </summary>
public interface IEditHistoryDocument
{
    /// <summary>Undoes this document's most recent edit.</summary>
    void UndoLast();

    /// <inheritdoc cref="IEditHistoryDocument.UndoLast"/>
    void RedoLast();

    /// <summary>Whether <see cref="IEditHistoryDocument.UndoLast"/> has anything to do — what gates the Undo command.</summary>
    bool CanUndoLast { get; }

    /// <summary>Whether <see cref="RedoLast"/> has anything to do.</summary>
    bool CanRedoLast { get; }

    /// <summary>What the Undo menu item and toolbar tooltip should say.</summary>
    string UndoLastDescription { get; }

    /// <inheritdoc cref="UndoLastDescription"/>
    string RedoLastDescription { get; }
}

/// <summary>
/// The shortcut for every editable document whose edit history IS an <see cref="UndoRedoStack"/> —
/// schematic, symbol editor, tech editor, layout, cell-parameter editor, EM setup. Exposing the
/// stack answers all six of <see cref="IEditHistoryDocument"/>'s questions with no code per
/// document. A document that keeps its history some other way implements the smaller interface
/// directly; the shell's Undo/Redo route through that one and never through this.
/// </summary>
public interface IUndoableDocument : IEditHistoryDocument
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
    void IEditHistoryDocument.UndoLast() => UndoRedo.Undo();

    /// <inheritdoc cref="IEditHistoryDocument.UndoLast"/>
    void IEditHistoryDocument.RedoLast() => UndoRedo.Redo();

    /// <summary>Whether <see cref="IEditHistoryDocument.UndoLast"/> has anything to do — what gates the Undo command.</summary>
    bool IEditHistoryDocument.CanUndoLast => UndoRedo.CanUndo;

    /// <summary>Whether <see cref="RedoLast"/> has anything to do.</summary>
    bool IEditHistoryDocument.CanRedoLast => UndoRedo.CanRedo;

    /// <summary>
    /// What the Undo menu item and toolbar tooltip should say — the description of the entry
    /// <see cref="IEditHistoryDocument.UndoLast"/> would actually take, which is not necessarily the command stack's when a
    /// document holds more than one history.
    /// </summary>
    string IEditHistoryDocument.UndoLastDescription => UndoRedo.UndoDescription;

    /// <inheritdoc cref="IEditHistoryDocument.UndoLastDescription"/>
    string IEditHistoryDocument.RedoLastDescription => UndoRedo.RedoDescription;
}
