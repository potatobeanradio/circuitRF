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
    // Net labels anchored to wireA or wireB, as they were before Execute moved them onto _merged.
    private List<SchematicEditModel.NetLabelAnchorSnap> _carriedLabels = [];

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
        // A label is anchored to a wire by id, so removing its owner orphans it — and the orphan pass
        // (RevalidateNetLabels) looks for a surviving wire under the label's LAST DRAWN position, which
        // a drag has not refreshed: a label riding a dragged wire was then deleted outright. Carry each
        // one onto the merged wire from where its owner puts it NOW. The merge preserves geometry, so
        // the foot lands on the same stretch of wire.
        _carriedLabels = [];
        foreach (var l in _model.NetLabels)
        {
            if (l.OwnerWireId != _wireA.Id && l.OwnerWireId != _wireB.Id) continue;
            var owner = l.OwnerWireId == _wireA.Id ? _wireA : _wireB;
            _carriedLabels.Add(new SchematicEditModel.NetLabelAnchorSnap(
                l, l.OwnerWireId, l.SegmentIndex, l.AlongT, l.OffsetX, l.OffsetY, l.X, l.Y));
            l.RecomputePosition(owner);
            double ox = l.OffsetX, oy = l.OffsetY;
            l.AnchorToWire(_merged, l.X - ox, l.Y - oy);   // anchor the FOOT, keep the label's gap
            l.OffsetX = ox; l.OffsetY = oy;
            l.X += ox; l.Y += oy;
        }

        _model.Wires.Remove(_wireA);
        _model.Wires.Remove(_wireB);
        int insertAt = Math.Min(Math.Min(_wireAIndex, _wireBIndex), _model.Wires.Count);
        _model.Wires.Insert(insertAt, _merged);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var s in _carriedLabels)
        {
            s.Label.OwnerWireId  = s.OwnerWireId;
            s.Label.SegmentIndex = s.SegmentIndex;
            s.Label.AlongT       = s.AlongT;
            s.Label.OffsetX      = s.OffsetX;
            s.Label.OffsetY      = s.OffsetY;
            s.Label.X            = s.X;
            s.Label.Y            = s.Y;
        }
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
