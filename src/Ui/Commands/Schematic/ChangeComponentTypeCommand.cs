using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Replaces a component with a pre-built replacement of a different type.
/// The replacement preserves position/rotation/mirror and carries a new Id, a next-available
/// instance name for its type prefix, and the default parameters for its new type.
/// Wire endpoints at ports that map between old and new symbol are re-routed to the new positions.
/// Execute: removes old, adds new. Undo: removes new, restores old — clean swap.
/// </summary>
internal sealed class ChangeComponentTypeCommand : IUiCommand
{
    private const double SnapTolerance = 100.0;

    private readonly SchematicEditModel             _model;
    private readonly EditableComponent              _oldComp;
    private readonly EditableComponent              _newComp;
    private readonly List<WireEndpointMoveSnapshot> _endMoves;

    public string Description => $"Change type to {ComponentTypeRegistry.DisplayName(_newComp.Symbol, _newComp.PortCount)}";

    /// <param name="model">The edit model that owns the component.</param>
    /// <param name="oldComp">The component being replaced (currently in the model).</param>
    /// <param name="newComp">The fully-built replacement (new Id, new name, new params — NOT yet in the model).</param>
    public ChangeComponentTypeCommand(
        SchematicEditModel model,
        EditableComponent  oldComp,
        EditableComponent  newComp)
    {
        _model    = model;
        _oldComp  = oldComp;
        _newComp  = newComp;
        _endMoves = ComputeEndMoves(model, oldComp, newComp);
    }

    public void Execute()
    {
        _model.Components.Remove(_oldComp);
        _model.Components.Add(_newComp);
        foreach (var s in _endMoves) ApplyEndpoint(s, end: true);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.Components.Remove(_newComp);
        _model.Components.Add(_oldComp);
        foreach (var s in _endMoves) ApplyEndpoint(s, end: false);
        _model.NotifyChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<WireEndpointMoveSnapshot> ComputeEndMoves(
        SchematicEditModel model,
        EditableComponent  comp,
        EditableComponent  newComp)
    {
        var moves = new List<WireEndpointMoveSnapshot>();

        var oldPorts = SymbolPortDefs.For(comp.Symbol,    comp.PortCount);
        var newPorts = SymbolPortDefs.For(newComp.Symbol, newComp.PortCount);

        // World positions of old ports (component is still at its original kind here)
        var oldWorldPorts = new (double X, double Y)[oldPorts.Length];
        for (int i = 0; i < oldPorts.Length; i++)
            oldWorldPorts[i] = SchematicGeometry.LocalToWorld(
                oldPorts[i].LocalX, oldPorts[i].LocalY,
                comp.X, comp.Y, comp.Rotation, comp.MirrorX);

        // World positions of new ports (same transform, different local coords)
        var newWorldPorts = new (double X, double Y)[newPorts.Length];
        for (int i = 0; i < newPorts.Length; i++)
            newWorldPorts[i] = SchematicGeometry.LocalToWorld(
                newPorts[i].LocalX, newPorts[i].LocalY,
                comp.X, comp.Y, comp.Rotation, comp.MirrorX);

        const double wireTol = 8.0;
        var seen = new HashSet<(string, int)>();

        for (int oi = 0; oi < oldWorldPorts.Length; oi++)
        {
            var (opx, opy) = oldWorldPorts[oi];

            // Match each old port to the closest new port within SnapTolerance
            int    bestNi   = -1;
            double bestDist = SnapTolerance;
            for (int ni = 0; ni < newWorldPorts.Length; ni++)
            {
                double d = Math.Sqrt(SchematicGeometry.DistanceSq(opx, opy, newWorldPorts[ni].X, newWorldPorts[ni].Y));
                if (d < bestDist) { bestDist = d; bestNi = ni; }
            }

            if (bestNi < 0) continue;  // no matching new port; wires at this old port disconnect
            var (npx, npy) = newWorldPorts[bestNi];
            if (Math.Abs(npx - opx) < 1e-6 && Math.Abs(npy - opy) < 1e-6) continue;  // same position

            // Move all wire endpoints coincident with the old port to the new port position
            foreach (var wire in model.Wires)
            {
                if (wire.Points.Count == 0) continue;
                Check(wire, 0, opx, opy, npx, npy, wireTol, seen, moves);
                Check(wire, wire.Points.Count - 1, opx, opy, npx, npy, wireTol, seen, moves);
            }
        }

        return moves;
    }

    private static void Check(
        EditableWire wire, int idx,
        double opx, double opy,
        double npx, double npy,
        double tol,
        HashSet<(string, int)> seen,
        List<WireEndpointMoveSnapshot> moves)
    {
        if (idx >= wire.Points.Count) return;
        var (wx, wy) = wire.Points[idx];
        if (!SchematicGeometry.CoincidentPoints(wx, wy, opx, opy, tol)) return;
        if (!seen.Add((wire.Id, idx))) return;
        moves.Add(new WireEndpointMoveSnapshot(wire, idx, wx, wy, npx, npy));
    }

    private static void ApplyEndpoint(WireEndpointMoveSnapshot s, bool end)
    {
        if ((uint)s.PointIndex >= (uint)s.Wire.Points.Count) return;
        s.Wire.Points[s.PointIndex] = end ? (s.EndX, s.EndY) : (s.StartX, s.StartY);
    }
}
