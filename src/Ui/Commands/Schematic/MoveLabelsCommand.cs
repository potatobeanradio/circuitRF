using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

internal sealed record MoveLabelSnapshot(
    EditableComponent                   Component,
    IReadOnlyList<(double DX, double DY)> OldOffsets,
    IReadOnlyList<(double DX, double DY)> NewOffsets);

/// <summary>
/// Records per-label offset moves for a set of components.
/// Execute applies NewOffsets; Undo restores OldOffsets.
/// </summary>
internal sealed class MoveLabelsCommand : IUiCommand
{
    private readonly SchematicEditModel      _model;
    private readonly List<MoveLabelSnapshot> _snaps;

    public string Description { get; }

    public MoveLabelsCommand(SchematicEditModel model, IEnumerable<MoveLabelSnapshot> snaps,
                             string description = "Move Labels")
    {
        _model       = model;
        _snaps       = snaps.ToList();
        Description  = description;
    }

    public void Execute()
    {
        foreach (var s in _snaps) Apply(s.Component, s.NewOffsets);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var s in _snaps) Apply(s.Component, s.OldOffsets);
        _model.NotifyChanged();
    }

    private static void Apply(EditableComponent c, IReadOnlyList<(double DX, double DY)> offsets)
    {
        c.LabelOffsets.Clear();
        c.LabelOffsets.AddRange(offsets);
    }
}
