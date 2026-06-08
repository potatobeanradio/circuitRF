using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Wraps any editor command and runs the post-edit cleanup invariants so they are part of the SAME
/// undoable operation (one Undo restores the geometry AND the cleanup):
///  • junction-dot invariant (§5.1) — every user dot that no longer sits on a genuine 4-way
///    crossing is removed;
///  • no degenerate wires — a wire collapsed to fewer than two distinct points (e.g. a connector
///    whose two ends were dragged onto the same point) is removed rather than left as a zero-length
///    wire that would draw a bogus junction.
///
/// Applied centrally to all commands routed through SchematicViewModel.Execute(); a no-op when the
/// edit leaves every crossing intact and produces no degenerate wires.
/// </summary>
internal sealed class DotRevalidationCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly IUiCommand _inner;
    // Items removed by this edit, paired with their original index, so Undo restores them in place.
    private List<(EditableDot Dot, int Index)> _removedDots = [];
    private List<(EditableWire Wire, int Index)> _removedWires = [];

    public DotRevalidationCommand(SchematicEditModel model, IUiCommand inner)
    {
        _model = model;
        _inner = inner;
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

        if (_removedWires.Count > 0 || _removedDots.Count > 0) _model.NotifyChanged();
    }

    public void Undo()
    {
        // Restore removed items at their original positions (ascending index), then undo the edit.
        foreach (var (wire, idx) in _removedWires.OrderBy(t => t.Index))
            _model.Wires.Insert(Math.Min(idx, _model.Wires.Count), wire);
        foreach (var (dot, idx) in _removedDots.OrderBy(t => t.Index))
            _model.Dots.Insert(Math.Min(idx, _model.Dots.Count), dot);
        _inner.Undo();
    }
}
