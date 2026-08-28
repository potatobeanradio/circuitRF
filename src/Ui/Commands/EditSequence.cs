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

    /// <summary>
    /// The stamp every <see cref="Next"/> in this scope returns, or 0 when no group is open.
    ///
    /// <para><b>Thread-static</b>, so a group opened for a UI gesture can never re-stamp an edit
    /// recorded on some other thread. A group only ever spans one synchronous commit.</para>
    /// </summary>
    [ThreadStatic]
    private static long _groupStamp;

    /// <summary>The next stamp. Never zero, so zero can mean "nothing recorded yet".</summary>
    public static long Next() => _groupStamp != 0 ? _groupStamp : Interlocked.Increment(ref _next);

    /// <summary>
    /// Records everything until the returned scope is disposed under ONE stamp, across BOTH histories
    /// — which is what makes a mixed gesture ONE Ctrl+Z.
    ///
    /// <para>Owner, 2026-08-27: dragging a selection of wires AND primitives took two undos. The two
    /// halves land on two different histories by construction (§ this class's own header), and there
    /// is no stack to merge them onto — but a shared stamp says "these are one edit", which is
    /// exactly the question the stamp already exists to answer. The undo router drains every entry
    /// carrying the stamp it is acting on.</para>
    ///
    /// <para>Nested groups are transparent: the inner scope keeps the outer stamp and restores it,
    /// so a caller need not know whether it is already inside one.</para>
    /// </summary>
    public static Scope Group() => new(_groupStamp != 0 ? 0 : Interlocked.Increment(ref _next));

    /// <summary>The disposable <see cref="Group"/> returns. A struct, and <c>ref struct</c>-free so it
    /// can be used with an ordinary <c>using</c> in an iterator or async method if one ever needs to.</summary>
    public readonly struct Scope(long opened) : System.IDisposable
    {
        private readonly long _opened = SetIfOpening(opened);

        private static long SetIfOpening(long stamp)
        {
            if (stamp != 0) _groupStamp = stamp;
            return stamp;
        }

        /// <summary>Closes the group — a no-op for a nested scope, which opened nothing.</summary>
        public void Dispose()
        {
            if (_opened != 0) _groupStamp = 0;
        }
    }

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
