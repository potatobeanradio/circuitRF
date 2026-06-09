using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Pastes a set of schematic objects (components, wires, canvas objects).
/// Name collisions are resolved at construction time: any pasted component whose instance
/// name already exists in the model is renamed to the next available name for its type prefix.
/// When <paramref name="sourceGridSize"/> differs from the model's GridSize, connection points
/// are snapped to the destination P and a warning is posted via <paramref name="messageSink"/>.
/// Undo removes them; Redo re-adds them with the same (already-resolved, already-snapped) positions.
/// </summary>
internal sealed class SchematicPasteCommand : IUiCommand
{
    private readonly SchematicEditModel               _model;
    private readonly List<EditableComponent>          _comps;
    private readonly List<EditableWire>               _wires;
    private readonly List<EditableCanvasObject>       _cobjs;
    private readonly Action<IEnumerable<string>>?     _reselect;

    public string Description => "Paste";

    /// <param name="sourceGridSize">
    /// The connection grid P of the source design. Pass 0 (default) to skip cross-grid handling.
    /// When non-zero and different from <paramref name="model"/>.GridSize, connection points are
    /// snapped to the destination P and a warning is posted.
    /// </param>
    public SchematicPasteCommand(
        SchematicEditModel model,
        IEnumerable<EditableComponent>    comps,
        IEnumerable<EditableWire>         wires,
        IEnumerable<EditableCanvasObject> cobjs,
        Action<IEnumerable<string>>?      reselect = null,
        double sourceGridSize = 0,
        IMessageSink? messageSink = null)
    {
        _model    = model;
        _comps    = ResolveNames(model, comps.ToList());
        _wires    = wires.ToList();
        _cobjs    = cobjs.ToList();
        _reselect = reselect;

        if (sourceGridSize > 0 && Math.Abs(sourceGridSize - model.GridSize) > 1e-9)
            SnapToDestGrid(model.GridSize, model.AuthorGridSize, sourceGridSize, messageSink);
    }

    public void Execute()
    {
        _model.Components.AddRange(_comps);
        _model.Wires.AddRange(_wires);
        _model.CanvasObjects.AddRange(_cobjs);
        _model.NotifyChanged();

        var ids = _comps.Select(c => c.Id)
            .Concat(_wires.Select(w => w.Id))
            .Concat(_cobjs.Select(o => o.Id));
        _reselect?.Invoke(ids);
    }

    public void Undo()
    {
        foreach (var c in _comps) _model.Components.Remove(c);
        foreach (var w in _wires) _model.Wires.Remove(w);
        foreach (var o in _cobjs) _model.CanvasObjects.Remove(o);
        _model.NotifyChanged();
    }

    // ── Cross-grid snap (§5) ─────────────────────────────────────────────────

    /// <summary>
    /// Snaps all pasted connection points (component origins, wire vertices) to P_dst so they land
    /// exactly on the destination connection grid. Canvas objects snap to the fine author grid p_dst.
    /// Preserves intra-group coincidence: independently rounding each point to P_dst keeps two points
    /// coincident iff they were in the same P_dst cell before snapping.
    /// </summary>
    private void SnapToDestGrid(double pDst, double pAuthor, double pSrc, IMessageSink? sink)
    {
        static double Snap(double v, double p) => Math.Round(v / p) * p;

        foreach (var c in _comps)
        {
            c.X = Snap(c.X, pDst);
            c.Y = Snap(c.Y, pDst);
        }
        foreach (var w in _wires)
            for (int i = 0; i < w.Points.Count; i++)
                w.Points[i] = (Snap(w.Points[i].X, pDst), Snap(w.Points[i].Y, pDst));
        foreach (var o in _cobjs)
        {
            o.X = Snap(o.X, pAuthor);
            o.Y = Snap(o.Y, pAuthor);
        }

        // Post-snap R7 validation: all connection points must be on P_dst (should always pass).
        bool offGrid = false;
        double eps = 1e-6;
        foreach (var c in _comps)
        {
            if (Math.Abs(c.X / pDst - Math.Round(c.X / pDst)) > eps ||
                Math.Abs(c.Y / pDst - Math.Round(c.Y / pDst)) > eps) { offGrid = true; break; }
        }
        if (!offGrid)
            foreach (var w in _wires)
                foreach (var (px, py) in w.Points)
                    if (Math.Abs(px / pDst - Math.Round(px / pDst)) > eps ||
                        Math.Abs(py / pDst - Math.Round(py / pDst)) > eps) { offGrid = true; break; }

        if (offGrid)
            sink?.Warning($"Paste: some points could not be snapped exactly to grid {pDst} — verify connections.");
        else
            sink?.Warning($"Pasted content was created on a {pSrc}-unit grid; " +
                          $"this schematic uses {pDst}. Pins were snapped to this schematic's grid — verify connections.");
    }

    // ── Name-collision resolution ─────────────────────────────────────────────

    /// <summary>
    /// For each pasted component whose instance name already exists in the model, assigns
    /// the next available name for its type prefix. Components that don't collide keep their
    /// original names. The taken-name set is updated incrementally so components within the
    /// same paste batch don't collide with each other either.
    /// </summary>
    private static List<EditableComponent> ResolveNames(
        SchematicEditModel model, List<EditableComponent> comps)
    {
        var taken = new HashSet<string>(model.Components.Select(c => c.InstanceName));
        foreach (var comp in comps)
        {
            if (!taken.Contains(comp.InstanceName))
            {
                taken.Add(comp.InstanceName);
                continue;
            }
            string prefix = ComponentTypeRegistry.InstancePrefix(comp.Symbol);
            comp.InstanceName = SchematicEditModel.NextAvailableName(taken, prefix);
            taken.Add(comp.InstanceName);
        }
        return comps;
    }
}
