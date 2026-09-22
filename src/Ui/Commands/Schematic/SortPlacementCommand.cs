using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Applies a <see cref="SchematicSortPlacement"/> plan: every component to its own new origin, as one
/// undo entry. Each component's detached-port marks are cleared, as <see cref="MoveCommand"/> clears
/// them — a detach means "coincident but not connected", and after the move nothing is coincident.
/// </summary>
internal sealed class SortPlacementCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly List<(EditableComponent Comp, double OldX, double OldY, double NewX, double NewY, int[] OldDetached)> _moves;

    public string Description => "Sort Placement";

    public SortPlacementCommand(SchematicEditModel model, IReadOnlyList<SchematicSortPlacement.Move> moves)
    {
        _model = model;
        _moves = moves.Select(m => (m.Component, m.Component.X, m.Component.Y, m.X, m.Y,
                                    m.Component.DetachedPorts.ToArray())).ToList();
    }

    public void Execute()
    {
        foreach (var m in _moves)
        {
            m.Comp.X = m.NewX;
            m.Comp.Y = m.NewY;
            m.Comp.DetachedPorts.Clear();
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var m in _moves)
        {
            m.Comp.X = m.OldX;
            m.Comp.Y = m.OldY;
            m.Comp.DetachedPorts.Clear();
            foreach (int p in m.OldDetached) m.Comp.DetachedPorts.Add(p);
        }
        _model.NotifyChanged();
    }
}
