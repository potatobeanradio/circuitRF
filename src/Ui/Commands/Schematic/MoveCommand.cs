using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

// ── Snapshot types ─────────────────────────────────────────────────────────────

internal readonly record struct ComponentMoveSnapshot(
    EditableComponent Component,
    double StartX, double StartY,
    double EndX,   double EndY);

internal readonly record struct WireMoveSnapshot(
    EditableWire Wire,
    IReadOnlyList<(double X, double Y)> StartPoints,
    IReadOnlyList<(double X, double Y)> EndPoints);

internal readonly record struct CanvasObjectMoveSnapshot(
    EditableCanvasObject Object,
    double StartX, double StartY,
    double EndX,   double EndY);

/// <summary>Moves a single wire endpoint independently of the rest of the wire.</summary>
internal readonly record struct WireEndpointMoveSnapshot(
    EditableWire Wire,
    int PointIndex,
    double StartX, double StartY,
    double EndX,   double EndY);

/// <summary>Moves a user junction dot (so a crossing dot can ride its dragged wire).</summary>
internal readonly record struct DotMoveSnapshot(
    EditableDot Dot,
    double StartX, double StartY,
    double EndX,   double EndY);

/// <summary>
/// Records the detached-port set for one component at the moment a move is committed.
/// MoveCommand.Execute clears the set; MoveCommand.Undo restores it.
/// </summary>
internal readonly record struct ComponentDetachClearSnapshot(
    EditableComponent Component,
    HashSet<int> PriorDetachedPorts);

/// <summary>
/// Moves a selection of components, wires, canvas objects, and/or wire endpoints.
/// Records start and end positions; Execute() = apply end; Undo() = restore start.
/// </summary>
internal sealed class MoveCommand : IUiCommand
{
    private readonly SchematicEditModel                     _model;
    private readonly List<ComponentMoveSnapshot>            _comps;
    private readonly List<WireMoveSnapshot>                 _wires;
    private readonly List<CanvasObjectMoveSnapshot>         _cobjs;
    private readonly List<WireEndpointMoveSnapshot>         _endPts;
    private readonly List<DotMoveSnapshot>                  _dots;
    private readonly List<ComponentDetachClearSnapshot>     _detachClears;

    public string Description => "Move";

    public MoveCommand(
        SchematicEditModel model,
        List<ComponentMoveSnapshot> comps,
        List<WireMoveSnapshot> wires,
        List<CanvasObjectMoveSnapshot> cobjs,
        List<WireEndpointMoveSnapshot>? endPts = null,
        List<DotMoveSnapshot>? dots = null,
        List<ComponentDetachClearSnapshot>? detachClears = null)
    {
        _model        = model;
        _comps        = comps;
        _wires        = wires;
        _cobjs        = cobjs;
        _endPts       = endPts ?? [];
        _dots         = dots ?? [];
        _detachClears = detachClears ?? [];
    }

    public void Execute()
    {
        foreach (var s in _comps)         { s.Component.X = s.EndX; s.Component.Y = s.EndY; }
        foreach (var s in _wires)         ApplyWirePoints(s.Wire, s.EndPoints);
        foreach (var s in _cobjs)         { s.Object.X = s.EndX; s.Object.Y = s.EndY; }
        foreach (var s in _endPts)        ApplyEndpoint(s, end: true);
        foreach (var s in _dots)          { s.Dot.X = s.EndX; s.Dot.Y = s.EndY; }
        foreach (var s in _detachClears)  s.Component.DetachedPorts.Clear();
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var s in _comps)  { s.Component.X = s.StartX; s.Component.Y = s.StartY; }
        foreach (var s in _wires)  ApplyWirePoints(s.Wire, s.StartPoints);
        foreach (var s in _cobjs)  { s.Object.X = s.StartX; s.Object.Y = s.StartY; }
        foreach (var s in _endPts) ApplyEndpoint(s, end: false);
        foreach (var s in _dots)   { s.Dot.X = s.StartX; s.Dot.Y = s.StartY; }
        foreach (var s in _detachClears)
        {
            s.Component.DetachedPorts.Clear();
            foreach (var pi in s.PriorDetachedPorts)
                s.Component.DetachedPorts.Add(pi);
        }
        _model.NotifyChanged();
    }

    private static void ApplyWirePoints(EditableWire wire, IReadOnlyList<(double X, double Y)> pts)
    {
        wire.Points.Clear();
        wire.Points.AddRange(pts);
    }

    private static void ApplyEndpoint(WireEndpointMoveSnapshot s, bool end)
    {
        if ((uint)s.PointIndex >= (uint)s.Wire.Points.Count) return;
        s.Wire.Points[s.PointIndex] = end ? (s.EndX, s.EndY) : (s.StartX, s.StartY);
    }
}
