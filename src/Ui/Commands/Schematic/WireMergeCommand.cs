using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Merges two wires into a single wire by removing both and inserting one merged wire.
///
/// Designed to run as the second step of a CompositeCommand after a MoveCommand or
/// PlaceWireCommand has positioned wireA. When composed:
///   Execute order: MoveCommand/PlaceWireCommand → WireMergeCommand
///   Undo order:    WireMergeCommand.Undo()      → MoveCommand/PlaceWireCommand.Undo()
///
/// WireMergeCommand.Undo() restores wireA to its post-move endpoint positions so the
/// preceding MoveCommand.Undo() can then restore wireA to its pre-move positions.
/// </summary>
internal sealed class WireMergeCommand : IUiCommand
{
    private readonly SchematicEditModel  _model;
    private readonly EditableWire        _wireA;
    private readonly int                 _wireAIndex;
    private readonly IReadOnlyList<(double X, double Y)> _wireAEndPoints;
    private readonly EditableWire        _wireB;
    private readonly int                 _wireBIndex;
    private readonly IReadOnlyList<(double X, double Y)> _wireBPoints;
    private readonly EditableWire        _merged;

    public string Description  => "Merge wires";
    public string MergedWireId => _merged.Id;

    /// <param name="wireAIndex">
    /// Index wireA occupies (or will occupy) in Wires after the preceding command executes.
    /// Pass <c>model.Wires.IndexOf(wireA)</c> for a drag; pass <c>model.Wires.Count</c>
    /// when wireA is a newly-drawn wire not yet in the model (PlaceWireCommand will append it).
    /// </param>
    public WireMergeCommand(
        SchematicEditModel model,
        EditableWire wireA, int wireAIndex, IReadOnlyList<(double X, double Y)> wireAEndPoints,
        EditableWire wireB,
        EditableWire merged)
    {
        _model          = model;
        _wireA          = wireA;
        _wireAIndex     = wireAIndex;
        _wireAEndPoints = wireAEndPoints;
        _wireB          = wireB;
        _wireBIndex     = model.Wires.IndexOf(wireB);
        _wireBPoints    = wireB.Points.ToList();
        _merged         = merged;
    }

    public void Execute()
    {
        _model.Wires.Remove(_wireA);
        _model.Wires.Remove(_wireB);
        int insertAt = Math.Min(Math.Min(_wireAIndex, _wireBIndex), _model.Wires.Count);
        _model.Wires.Insert(insertAt, _merged);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.Wires.Remove(_merged);
        // Restore A to its post-move points so the preceding MoveCommand.Undo() can
        // subsequently restore it to pre-move points.
        _wireA.Points.Clear();
        _wireA.Points.AddRange(_wireAEndPoints);
        // Restore B to its original points.
        _wireB.Points.Clear();
        _wireB.Points.AddRange(_wireBPoints);
        // Re-insert in original order to preserve relative positions.
        if (_wireAIndex <= _wireBIndex)
        {
            _model.Wires.Insert(Math.Min(_wireAIndex, _model.Wires.Count), _wireA);
            _model.Wires.Insert(Math.Min(_wireBIndex, _model.Wires.Count), _wireB);
        }
        else
        {
            _model.Wires.Insert(Math.Min(_wireBIndex, _model.Wires.Count), _wireB);
            _model.Wires.Insert(Math.Min(_wireAIndex, _model.Wires.Count), _wireA);
        }
        _model.NotifyChanged();
    }
}
