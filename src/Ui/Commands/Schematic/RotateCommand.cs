using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Rotates selected components 90° CW or CCW. Canvas objects rotate by a given angle.
/// Re-routes any wire whose endpoint sits on a moved port to stay connected.
/// Undo reverses the rotation and wire reroutes atomically.
/// </summary>
internal sealed class RotateCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly bool _clockwise;

    private readonly List<(EditableComponent Comp, SymbolRotation OldRot)> _compSnaps = [];
    private readonly List<(EditableCanvasObject Obj, double OldDeg)> _objSnaps = [];
    private readonly List<WireMoveSnapshot> _wireSnaps = [];

    public string Description => _clockwise ? "Rotate CW" : "Rotate CCW";

    public RotateCommand(SchematicEditModel model, IReadOnlyList<string> selectedIds, bool clockwise = false)
    {
        _model     = model;
        _clockwise = clockwise;

        foreach (var id in selectedIds)
        {
            var comp = model.FindComponent(id);
            if (comp is not null) { _compSnaps.Add((comp, comp.Rotation)); continue; }

            var obj = model.FindCanvasObject(id);
            if (obj is not null) _objSnaps.Add((obj, obj.RotationDeg));
        }

        // Compute old→new port-position moves caused by the rotation.
        var portMoves = new List<(double Ox, double Oy, double Nx, double Ny)>();
        foreach (var (comp, oldRot) in _compSnaps)
        {
            var newRot   = Step(oldRot, clockwise);
            var portDefs = SymbolPortDefs.For(comp.Symbol);
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (ox, oy) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    comp.X, comp.Y, oldRot, comp.MirrorX);
                var (nx, ny) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    comp.X, comp.Y, newRot, comp.MirrorX);
                portMoves.Add((ox, oy, nx, ny));
            }
        }

        // Build wire-reroute snaps for any wire connected to a moved port.
        if (portMoves.Count > 0)
        {
            const double tol = 8.0;
            foreach (var wire in model.Wires)
            {
                if (wire.Points.Count < 2) continue;
                double sx = wire.Points[0].X,  sy = wire.Points[0].Y;
                double ex = wire.Points[^1].X, ey = wire.Points[^1].Y;
                double newSX = sx, newSY = sy, newEX = ex, newEY = ey;
                bool changed = false;
                foreach (var (ox, oy, nx, ny) in portMoves)
                {
                    if (SchematicGeometry.CoincidentPoints(sx, sy, ox, oy, tol))
                    { newSX = nx; newSY = ny; changed = true; }
                    if (SchematicGeometry.CoincidentPoints(ex, ey, ox, oy, tol))
                    { newEX = nx; newEY = ny; changed = true; }
                }
                if (!changed) continue;
                var newRoute = WireGeometry.OrthogonalRoute(newSX, newSY, newEX, newEY);
                _wireSnaps.Add(new WireMoveSnapshot(wire, wire.Points.ToList(), newRoute));
            }
        }
    }

    public void Execute()
    {
        foreach (var (comp, _) in _compSnaps)
            comp.Rotation = Step(comp.Rotation, _clockwise);
        foreach (var (obj, _) in _objSnaps)
            obj.RotationDeg = (obj.RotationDeg + (_clockwise ? 90.0 : -90.0) + 360.0) % 360.0;
        foreach (var s in _wireSnaps)
        {
            s.Wire.Points.Clear();
            s.Wire.Points.AddRange(s.EndPoints);
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, old) in _compSnaps) comp.Rotation = old;
        foreach (var (obj, old) in _objSnaps)   obj.RotationDeg = old;
        foreach (var s in _wireSnaps)
        {
            s.Wire.Points.Clear();
            s.Wire.Points.AddRange(s.StartPoints);
        }
        _model.NotifyChanged();
    }

    private static SymbolRotation Step(SymbolRotation r, bool cw) => cw
        ? r switch { SymbolRotation.R0 => SymbolRotation.R270, SymbolRotation.R270 => SymbolRotation.R180,
                     SymbolRotation.R180 => SymbolRotation.R90, _ => SymbolRotation.R0 }
        : r switch { SymbolRotation.R0 => SymbolRotation.R90,  SymbolRotation.R90 => SymbolRotation.R180,
                     SymbolRotation.R180 => SymbolRotation.R270, _ => SymbolRotation.R0 };
}
