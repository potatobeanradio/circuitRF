using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// The N-removed -> M-added generalization of <see cref="ReplaceShapeCommand"/> (L1e §3): every
/// boolean, offset, repair, and flatten-to-polygon operation removes one or more selected shapes and
/// replaces them with zero or more results. Inserting at the LOWEST removed index keeps z-order
/// predictable (L1b's rule, extended to the N→M case); Undo restores every original at its original
/// index, exactly as <see cref="ReplaceShapeCommand"/> does for the 1→1 case.
/// </summary>
internal sealed class ReplaceShapesCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly IReadOnlyList<(int Index, LayoutShape Before)> _removed;
    private readonly IReadOnlyList<LayoutShape> _added;

    public string Description { get; }

    /// <summary>
    /// <paramref name="removed"/> need not be pre-sorted — the constructor sorts ascending by
    /// <see cref="ValueTuple{T1,T2}.Item1"/> once, since both Execute and Undo depend on ascending
    /// order.
    /// </summary>
    public ReplaceShapesCommand(
        LayoutView view,
        IReadOnlyList<(int Index, LayoutShape Before)> removed,
        IReadOnlyList<LayoutShape> added,
        string description = "Edit Shapes")
    {
        _view = view;
        _removed = [.. removed.OrderBy(r => r.Index)];
        _added = added;
        Description = description;
    }

    /// <summary>Lowest removed index — where <see cref="_added"/> lands on Execute, and where it is
    /// found again on Undo. A pure function of <see cref="_removed"/>, so it is safe to recompute
    /// (never stored as mutable state) on every Execute/Undo/Redo cycle.</summary>
    private int InsertAt => _removed.Count > 0 ? _removed[0].Index : _view.Shapes.Count;

    /// <summary>L2b: when nothing is removed (paste/duplicate — <see cref="LayoutEditorViewModel.
    /// InsertPastedMixed"/>), <see cref="InsertAt"/> is <c>Shapes.Count</c> and every added shape
    /// lands at the tail — a safe trailing append for the spatial index's incremental fast path. Any
    /// removal at all (booleans, offset, repair, flatten, scale) shifts other shapes' indices in a way
    /// this command does not track precisely, so those fall back to <see cref="LayoutChangeInfo.Full"/>
    /// (the default <see cref="LayoutView.NotifyChanged"/> already applies) — correct, just a full
    /// rebuild instead of an incremental update; these are all discrete, infrequent user actions, not a
    /// per-frame hot path, so the rebuild cost is not felt.</summary>
    private bool IsPureAppend => _removed.Count == 0;

    public void Execute()
    {
        lock (_view.RenderLock)   // one step as far as the render thread is concerned — see DeleteShapesCommand
        {
            int insertAt = InsertAt;

            // Remove highest-to-lowest so earlier removals never shift a later removal's index.
            for (int i = _removed.Count - 1; i >= 0; i--)
                _view.Shapes.RemoveAt(_removed[i].Index);

            insertAt = Math.Min(insertAt, _view.Shapes.Count);
            for (int i = 0; i < _added.Count; i++)
                _view.Shapes.Insert(insertAt + i, _added[i]);

            _view.NotifyChanged(IsPureAppend ? LayoutChangeInfo.Appended(insertAt, _added.Count) : null);
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            int insertAt = Math.Min(InsertAt, Math.Max(0, _view.Shapes.Count - _added.Count));
            _view.Shapes.RemoveRange(insertAt, _added.Count);

            // Ascending order — each insertion at its original index is valid because every earlier
            // (lower-index) original has already been reinserted, exactly like ReplaceShapeCommand's
            // 1-index case generalizes.
            foreach (var (index, before) in _removed)
                _view.Shapes.Insert(index, before);

            _view.NotifyChanged(IsPureAppend ? LayoutChangeInfo.RemovedTrailing(insertAt, _added.Count) : null);
        }
    }
}
