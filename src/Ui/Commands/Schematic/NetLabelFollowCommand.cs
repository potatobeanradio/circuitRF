using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Re-anchors net labels to their owner wire at given FOOT points after a geometry edit, keeping each
/// label's perpendicular gap. A label is anchored by segment INDEX, so an edit that inserts vertices
/// ahead of the label's segment (a segment drag adding a jog at a pinned end) would otherwise slide it
/// onto a different segment of the same wire. Runs after the edit that moved the wire; Undo restores
/// each anchor exactly.
/// </summary>
internal sealed class NetLabelFollowCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly IReadOnlyList<(EditableNetLabel Label, double FootX, double FootY)> _targets;
    private List<SchematicEditModel.NetLabelAnchorSnap> _snaps = [];

    public NetLabelFollowCommand(
        SchematicEditModel model,
        IReadOnlyList<(EditableNetLabel Label, double FootX, double FootY)> targets)
    {
        _model   = model;
        _targets = targets;
    }

    public string Description => "Move net labels";

    public void Execute()
    {
        _snaps = [];
        foreach (var (l, fx, fy) in _targets)
        {
            if (_model.FindWire(l.OwnerWireId) is not { Points.Count: >= 2 } owner) continue;
            _snaps.Add(new SchematicEditModel.NetLabelAnchorSnap(
                l, l.OwnerWireId, l.SegmentIndex, l.AlongT, l.OffsetX, l.OffsetY, l.X, l.Y));
            double ox = l.OffsetX, oy = l.OffsetY;
            l.AnchorToWire(owner, fx, fy);
            l.OffsetX = ox; l.OffsetY = oy;
            l.X = fx + ox;  l.Y = fy + oy;
        }
        if (_snaps.Count > 0) _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var s in _snaps)
        {
            s.Label.OwnerWireId  = s.OwnerWireId;
            s.Label.SegmentIndex = s.SegmentIndex;
            s.Label.AlongT       = s.AlongT;
            s.Label.OffsetX      = s.OffsetX;
            s.Label.OffsetY      = s.OffsetY;
            s.Label.X            = s.X;
            s.Label.Y            = s.Y;
        }
        if (_snaps.Count > 0) _model.NotifyChanged();
    }
}
