using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

// ── Snapshot types for delete ─────────────────────────────────────────────────

internal readonly record struct DeletedComponent(EditableComponent Component, int Index);
internal readonly record struct DeletedWire(EditableWire Wire, int Index);
internal readonly record struct DeletedCanvasObject(EditableCanvasObject Object, int Index);
internal readonly record struct DeletedNetLabel(EditableNetLabel Label, int Index);
internal readonly record struct DeletedDot(EditableDot Dot, int Index);

/// <summary>
/// Deletes a selection of schematic objects.
/// Undo restores them at their original list positions and re-selects them.
/// </summary>
internal sealed class DeleteCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Action<IEnumerable<string>>? _reselect;

    private readonly List<DeletedComponent>     _comps  = [];
    private readonly List<DeletedWire>           _wires  = [];
    private readonly List<DeletedCanvasObject>   _cobjs  = [];
    private readonly List<DeletedNetLabel>       _labels = [];
    private readonly List<DeletedDot>            _dots   = [];

    public string Description => BuildDescription();

    public DeleteCommand(
        SchematicEditModel model,
        IReadOnlyList<string> selectedIds,
        Action<IEnumerable<string>>? reselect = null)
    {
        _model    = model;
        _reselect = reselect;

        // Snapshot positions before deletion
        foreach (var id in selectedIds)
        {
            for (int i = 0; i < model.Components.Count; i++)
                if (model.Components[i].Id == id) { _comps.Add(new(model.Components[i], i)); break; }

            for (int i = 0; i < model.Wires.Count; i++)
                if (model.Wires[i].Id == id) { _wires.Add(new(model.Wires[i], i)); break; }

            for (int i = 0; i < model.CanvasObjects.Count; i++)
                if (model.CanvasObjects[i].Id == id) { _cobjs.Add(new(model.CanvasObjects[i], i)); break; }

            for (int i = 0; i < model.NetLabels.Count; i++)
                if (model.NetLabels[i].Id == id) { _labels.Add(new(model.NetLabels[i], i)); break; }

            for (int i = 0; i < model.Dots.Count; i++)
                if (model.Dots[i].Id == id) { _dots.Add(new(model.Dots[i], i)); break; }
        }
    }

    public void Execute()
    {
        foreach (var s in _comps)  _model.Components.Remove(s.Component);
        foreach (var s in _wires)  _model.Wires.Remove(s.Wire);
        foreach (var s in _cobjs)  _model.CanvasObjects.Remove(s.Object);
        foreach (var s in _labels) _model.NetLabels.Remove(s.Label);
        foreach (var s in _dots)   _model.Dots.Remove(s.Dot);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        // Re-insert at original indices (in reverse deletion order to keep indices valid)
        foreach (var s in _comps.OrderBy(x => x.Index))
            _model.Components.Insert(Math.Min(s.Index, _model.Components.Count), s.Component);

        foreach (var s in _wires.OrderBy(x => x.Index))
            _model.Wires.Insert(Math.Min(s.Index, _model.Wires.Count), s.Wire);

        foreach (var s in _cobjs.OrderBy(x => x.Index))
            _model.CanvasObjects.Insert(Math.Min(s.Index, _model.CanvasObjects.Count), s.Object);

        foreach (var s in _labels.OrderBy(x => x.Index))
            _model.NetLabels.Insert(Math.Min(s.Index, _model.NetLabels.Count), s.Label);

        foreach (var s in _dots.OrderBy(x => x.Index))
            _model.Dots.Insert(Math.Min(s.Index, _model.Dots.Count), s.Dot);

        _model.NotifyChanged();

        // Re-select all restored items
        var ids = _comps.Select(s => s.Component.Id)
            .Concat(_wires.Select(s => s.Wire.Id))
            .Concat(_cobjs.Select(s => s.Object.Id))
            .Concat(_labels.Select(s => s.Label.Id))
            .Concat(_dots.Select(s => s.Dot.Id));
        _reselect?.Invoke(ids);
    }

    private string BuildDescription()
    {
        int total = _comps.Count + _wires.Count + _cobjs.Count + _labels.Count + _dots.Count;
        return total == 1 ? "Delete object" : $"Delete {total} objects";
    }
}
