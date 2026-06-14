using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Wraps any editor command and runs the post-edit cleanup invariants so they are part of the SAME
/// undoable operation (one Undo restores the geometry AND the cleanup):
///  • junction-dot invariant (§5.1) — every user dot that no longer sits on a genuine 4-way
///    crossing is removed;
///  • no degenerate wires — a wire collapsed to fewer than two distinct points (e.g. a connector
///    whose two ends were dragged onto the same point) is removed rather than left as a zero-length
///    wire that would draw a bogus junction;
///  • net-label invariant — every anchored label whose owner wire was deleted is removed or
///    re-homed to the surviving wire; labels whose owner's segment list changed are re-anchored.
///
/// Applied centrally to all commands routed through SchematicViewModel.Execute(); a no-op when the
/// edit leaves every invariant intact.
/// </summary>
internal sealed class DotRevalidationCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly IUiCommand _inner;
    private readonly IMessageSink? _sink;
    // Items removed by this edit, paired with their original index, so Undo restores them in place.
    private List<(EditableDot Dot, int Index)> _removedDots = [];
    private List<(EditableWire Wire, int Index)> _removedWires = [];
    private List<(EditableNetLabel Label, int Index)>       _removedLabels    = [];
    private List<SchematicEditModel.NetLabelAnchorSnap>     _reanchoredLabels = [];

    public DotRevalidationCommand(SchematicEditModel model, IUiCommand inner, IMessageSink? sink = null)
    {
        _model = model;
        _inner = inner;
        _sink  = sink;
    }

    public string Description => _inner.Description;

    public void Execute()
    {
        _inner.Execute();

        // Degenerate wires: collapsed to < 2 distinct points → remove (descending index).
        _removedWires = _model.Wires
            .Select((w, i) => (Wire: w, Index: i))
            .Where(t => WireGeometry.NormalizePoints(t.Wire.Points).Count < 2)
            .OrderByDescending(t => t.Index)
            .ToList();
        foreach (var (_, idx) in _removedWires) _model.Wires.RemoveAt(idx);

        // Invalid dots (descending index so removals don't shift later indices).
        _removedDots = _model.FindInvalidDots()
            .Select(d => (Dot: d, Index: _model.Dots.IndexOf(d)))
            .Where(t => t.Index >= 0)
            .OrderByDescending(t => t.Index)
            .ToList();
        foreach (var (_, idx) in _removedDots) _model.Dots.RemoveAt(idx);

        // Net-label invariant: re-home or remove labels whose owner wire changed, so none is left
        // hanging unassigned. Part of the same undoable edit.
        var nl = _model.RevalidateNetLabels();
        _removedLabels    = nl.Removed;
        _reanchoredLabels = nl.Reanchored;

        // One label per node: a geometry edit may have merged two already-labeled nets onto one
        // physical node (e.g. a wire drawn between them). Keep the first label on each such node
        // (NetLabels order) and remove the rest, so the schematic stays unambiguous and the netlist
        // matches it. Same connectivity as extraction; folded into THIS undo via _removedLabels.
        foreach (var group in NetExtractor.LabelsSharingNode(_model))
        {
            var keep = group[0];
            for (int gi = group.Count - 1; gi >= 1; gi--)
            {
                var extra = group[gi];
                int idx = _model.NetLabels.IndexOf(extra);
                if (idx < 0) continue;
                _removedLabels.Add((extra, idx));
                _model.NetLabels.RemoveAt(idx);
                if (!string.Equals(extra.Name, keep.Name, StringComparison.Ordinal))
                    _sink?.Warning($"Net '{extra.Name}' merged into '{keep.Name}'; label '{extra.Name}' removed.");
            }
        }

        if (_removedWires.Count > 0 || _removedDots.Count > 0
            || _removedLabels.Count > 0 || _reanchoredLabels.Count > 0) _model.NotifyChanged();
    }

    public void Undo()
    {
        // Restore removed items at their original positions (ascending index), then undo the edit.
        foreach (var (wire, idx) in _removedWires.OrderBy(t => t.Index))
            _model.Wires.Insert(Math.Min(idx, _model.Wires.Count), wire);
        foreach (var (dot, idx) in _removedDots.OrderBy(t => t.Index))
            _model.Dots.Insert(Math.Min(idx, _model.Dots.Count), dot);

        // Restore net-label anchors changed by revalidation, then re-insert removed labels — both
        // BEFORE _inner.Undo() so the restored owner wire ids match the wires it brings back.
        foreach (var s in _reanchoredLabels)
        {
            s.Label.OwnerWireId  = s.OwnerWireId;
            s.Label.SegmentIndex = s.SegmentIndex;
            s.Label.AlongT       = s.AlongT;
            s.Label.OffsetX      = s.OffsetX;
            s.Label.OffsetY      = s.OffsetY;
            s.Label.X            = s.X;
            s.Label.Y            = s.Y;
        }
        foreach (var (label, idx) in _removedLabels.OrderBy(t => t.Index))
            _model.NetLabels.Insert(Math.Min(idx, _model.NetLabels.Count), label);

        _inner.Undo();
    }
}
