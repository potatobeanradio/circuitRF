using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Marks every port of the target component(s) as detached — the first persistent
/// connectivity override. A detached port renders unconnected even when geometrically
/// coincident with a wire or another pin, is excluded from net extraction, and makes
/// no wires follow during a subsequent drag.
///
/// Execute: sets DetachedPorts to all port indices (snapshots prior state for Undo).
/// Undo: restores the prior DetachedPorts sets.
/// Both call NotifyChanged() so the schematic re-renders.
/// </summary>
internal sealed class DisconnectCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly List<(EditableComponent Comp, HashSet<int> Prior)> _snapshots;

    public string Description => "Disconnect";

    public DisconnectCommand(SchematicEditModel model, IEnumerable<string> targetIds)
    {
        _model     = model;
        _snapshots = [];
        foreach (var id in targetIds)
        {
            var comp = model.FindComponent(id);
            if (comp is null) continue;
            _snapshots.Add((comp, new HashSet<int>(comp.DetachedPorts)));
        }
    }

    public void Execute()
    {
        foreach (var (comp, _) in _snapshots)
        {
            comp.DetachedPorts.Clear();
            int nPins = SymbolPortDefs.For(comp.Symbol, comp.PortCount).Length;
            for (int i = 0; i < nPins; i++)
                comp.DetachedPorts.Add(i);
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, prior) in _snapshots)
        {
            comp.DetachedPorts.Clear();
            foreach (var idx in prior)
                comp.DetachedPorts.Add(idx);
        }
        _model.NotifyChanged();
    }
}
