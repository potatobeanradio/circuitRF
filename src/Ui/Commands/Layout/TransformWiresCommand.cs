using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// A rigid in-plane transform of some BOND WIRES, on the <b>layout's</b> undo stack (WB40f).
///
/// <h3>Why the layout's stack and not the wire editor's</h3>
/// <para>Rotate and Mirror in the Layout Editor act on one selection that may hold shapes, instances
/// and wires at once, and §6.3 makes that a normal selection to have — "select the pads and the wires
/// landing on them" is one gesture. The transform is therefore one rigid body about one pivot, and it
/// has to be <b>one undo entry</b>: two entries would let a single Ctrl+Z put the pads back and leave
/// the wires rotated, which is a state the user never created and cannot see the reason for.</para>
///
/// <para>So this composes into the same <c>CompositeCommand</c> the shape and instance edits already
/// build, and <c>WBondViewModel.MapWirePointsXy</c> — the primitive underneath — deliberately pushes
/// nothing onto the WIRE stack.</para>
///
/// <h3>Undo restores the points it captured, rather than inverting the map</h3>
/// <para>The inverse of a 90° rotation about a pivot is exact in integer arithmetic, but the inverse
/// of the general map this takes is not, and a mirror composed with a re-centring translation is
/// exactly the kind of thing that drifts a DBU per undo. A snapshot cannot drift.</para>
/// </summary>
internal sealed class TransformWiresCommand : IUiCommand
{
    private readonly WBondViewModel _editor;
    private readonly IReadOnlyList<int> _wires;
    private readonly Func<long, long, (long X, long Y)> _map;
    private readonly List<List<Point3>> _before;

    public string Description { get; }

    /// <param name="wires">Flat wire indices, in <c>WBondDesign.AllWires</c> order.</param>
    /// <param name="map">
    /// The SAME x/y map the shapes and instances take, so everything selected keeps its position
    /// relative to everything else. z is never mapped — see <c>MapWirePointsXy</c>.
    /// </param>
    internal TransformWiresCommand(
        WBondViewModel editor,
        IReadOnlyList<int> wires,
        Func<long, long, (long X, long Y)> map,
        string description)
    {
        _editor = editor;
        _wires = wires;
        _map = map;
        Description = description;

        // Captured at CONSTRUCTION, which is before Execute runs — the geometry as it stands now is
        // what undo has to restore, and by the time Execute is called for the first time it is gone.
        var all = editor.Design.AllWires().ToList();
        _before = [.. wires.Select(i => i >= 0 && i < all.Count
            ? new List<Point3>(all[i].Points)
            : [])];
    }

    public void Execute() => _editor.MapWirePointsXy(_wires, _map);

    public void Undo()
    {
        var all = _editor.Design.AllWires().ToList();
        var restored = new List<int>(_wires.Count);

        for (int k = 0; k < _wires.Count; k++)
        {
            int index = _wires[k];
            if (index < 0 || index >= all.Count) continue;

            var wire = all[index];
            var snapshot = _before[k];

            // A point COUNT change between the transform and its undo would mean the design was
            // restructured underneath this entry; restoring a mismatched list would corrupt the wire,
            // so the safe answer is to leave that one alone.
            if (snapshot.Count != wire.Points.Count) continue;

            for (int i = 0; i < snapshot.Count; i++) wire.Points[i] = snapshot[i];
            restored.Add(index);
        }

        // AfterFrame, not CommitPointMove: an undo of a 500-wire move must put the wires back on this
        // frame and pay for the matrix on the next (owner, 2026-08-18). Below the frame bound it is a
        // plain synchronous commit, so a small undo is unchanged.
        if (restored.Count > 0) _editor.CommitPointMoveAfterFrame(restored);
    }
}
