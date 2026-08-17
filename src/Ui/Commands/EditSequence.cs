using System.Threading;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// A process-wide, monotonically increasing stamp handed to every undoable edit as it is recorded.
///
/// <h3>Why this exists</h3>
/// <para>WB39a puts TWO independent undo histories in front of one user: the wBond editor's wire
/// snapshots (<c>WBondViewModel</c>) and the hosted Layout Editor's command stack
/// (<see cref="UndoRedoStack"/>). They cannot be merged — one restores whole-design snapshots and the
/// other replays commands — but the question Ctrl+Z asks is a single one: <i>what did I do last?</i>
/// A stamp on each recorded entry answers exactly that, and nothing more.</para>
///
/// <para>Deliberately not a timestamp: two edits inside one millisecond are ordinary (a drag that ends
/// with a snap), and equal stamps would make the answer arbitrary. Deliberately not per-document
/// either — the comparison is only ever between two histories the same user is editing at the same
/// moment, and a shared counter makes that comparison total without anyone having to agree on an
/// origin.</para>
/// </summary>
public static class EditSequence
{
    private static long _next;

    /// <summary>The next stamp. Never zero, so zero can mean "nothing recorded yet".</summary>
    public static long Next() => Interlocked.Increment(ref _next);

    /// <summary>
    /// Which of two histories an <b>Undo</b> should act on: the FIRST when it has the more recent
    /// entry, or when the second has nothing to undo at all.
    /// </summary>
    /// <param name="firstCanUndo">Whether the first history has anything to undo.</param>
    /// <param name="firstStamp">Its <see cref="Next"/> stamp for the entry Undo would take.</param>
    public static bool UndoTakesFirst(bool firstCanUndo, long firstStamp,
                                     bool secondCanUndo, long secondStamp)
        => firstCanUndo && (!secondCanUndo || firstStamp > secondStamp);

    /// <summary>
    /// The mirror image for <b>Redo</b>: the OLDEST undone entry is the one the last Undo produced, so
    /// the smaller stamp wins.
    /// </summary>
    public static bool RedoTakesFirst(bool firstCanRedo, long firstStamp,
                                      bool secondCanRedo, long secondStamp)
        => firstCanRedo && (!secondCanRedo || firstStamp < secondStamp);
}
