using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Pastes a set of schematic objects (components, wires, canvas objects).
/// Name collisions are resolved at construction time: any pasted component whose instance
/// name already exists in the model is renamed to the next available name for its type prefix.
/// Undo removes them; Redo re-adds them with the same (already-resolved) names.
/// </summary>
internal sealed class SchematicPasteCommand : IUiCommand
{
    private readonly SchematicEditModel               _model;
    private readonly List<EditableComponent>          _comps;
    private readonly List<EditableWire>               _wires;
    private readonly List<EditableCanvasObject>       _cobjs;
    private readonly Action<IEnumerable<string>>?     _reselect;

    public string Description => "Paste";

    public SchematicPasteCommand(
        SchematicEditModel model,
        IEnumerable<EditableComponent>    comps,
        IEnumerable<EditableWire>         wires,
        IEnumerable<EditableCanvasObject> cobjs,
        Action<IEnumerable<string>>?      reselect = null)
    {
        _model    = model;
        _comps    = ResolveNames(model, comps.ToList());
        _wires    = wires.ToList();
        _cobjs    = cobjs.ToList();
        _reselect = reselect;
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
