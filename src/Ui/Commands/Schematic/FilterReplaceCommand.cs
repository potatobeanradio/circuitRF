using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// The LC Filter Designer's <b>Replace this Filter</b> as ONE undoable operation (brief FD-P §1.2):
/// the block is removed, each of its two pins keeps its net through a label, and the ladder is
/// pasted with its ends on the same names. Undo puts the block back exactly.
/// </summary>
internal sealed class FilterReplaceCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _filter;
    private readonly List<EditableNetLabel> _pinLabels;
    private readonly SchematicPasteCommand _paste;
    private int _index = -1;

    /// <param name="fragment">From <c>FilterPlacement.ReplacementFragment</c>.</param>
    public FilterReplaceCommand(
        SchematicEditModel model, EditableComponent filter,
        (SchematicEditModel Ladder, List<EditableNetLabel> PinLabels) fragment,
        Action<IEnumerable<string>>? reselect = null)
    {
        _model = model;
        _filter = filter;
        _pinLabels = fragment.PinLabels;
        // Built while the block is still on the sheet, so a ladder element can never be renamed
        // onto the block's own name and collide with it on Undo.
        _paste = new SchematicPasteCommand(
            model, fragment.Ladder.Components, fragment.Ladder.Wires, fragment.Ladder.CanvasObjects,
            reselect, netLabels: fragment.Ladder.NetLabels);
    }

    /// <inheritdoc/>
    public string Description => $"Replace {_filter.InstanceName} with its LC ladder";

    /// <inheritdoc/>
    public void Execute()
    {
        _index = _model.Components.IndexOf(_filter);
        _model.Components.Remove(_filter);
        _model.NetLabels.AddRange(_pinLabels);
        _paste.Execute();
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _paste.Undo();
        foreach (var n in _pinLabels) _model.NetLabels.Remove(n);
        // Back in its own place in the list, so an undone replacement saves byte for byte.
        if (_index >= 0 && _index <= _model.Components.Count) _model.Components.Insert(_index, _filter);
        else _model.Components.Add(_filter);
        _model.NotifyChanged();
    }
}
