using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Deletes specific wire segments from the schematic.
///
/// Removing a middle segment splits the wire into two (points before the cut / points after).
/// Removing an end segment drops the outer endpoint.
/// Wires with fewer than 2 remaining points are removed entirely.
/// Multiple segments on the same wire are processed together in a single operation.
/// Fully undoable — undo restores the original wire(s) exactly.
/// </summary>
internal sealed class DeleteSegmentsCommand : IUiCommand
{
    private readonly SchematicEditModel _model;

    // Per-affected-wire snapshot: original wire + its list index, plus the replacement wire(s).
    private sealed record WireSegmentDeleteSnap(
        EditableWire         Original,
        int                  OriginalIndex,
        List<EditableWire>   Replacements);

    private readonly List<WireSegmentDeleteSnap> _snaps = [];

    public string Description => _snaps.Sum(s => s.Replacements.Count == 0 ? 1 : 1) == 1
        ? "Delete Segment" : "Delete Segments";

    public DeleteSegmentsCommand(
        SchematicEditModel model,
        IReadOnlyList<(string WireId, int SegmentIndex)> segments)
    {
        _model = model;

        // Group by wire so multiple segments on the same wire are processed together.
        foreach (var group in segments.GroupBy(s => s.WireId))
        {
            var wire = model.FindWire(group.Key);
            if (wire is null || wire.Points.Count < 2) continue;

            int wireIndex = model.Wires.IndexOf(wire);
            var cuts = group.Select(g => g.SegmentIndex)
                            .Where(i => i >= 0 && i < wire.Points.Count - 1)
                            .Distinct()
                            .OrderBy(i => i)
                            .ToList();
            if (cuts.Count == 0) continue;

            var pieces = ComputePieces(wire.Points, cuts);
            var replacements = pieces.Select(p =>
            {
                var nw = new EditableWire();
                nw.Points.AddRange(p);
                return nw;
            }).ToList();

            _snaps.Add(new WireSegmentDeleteSnap(wire, wireIndex, replacements));
        }
    }

    public void Execute()
    {
        foreach (var snap in _snaps)
        {
            int insertAt = Math.Min(snap.OriginalIndex, _model.Wires.Count);
            _model.Wires.Remove(snap.Original);
            for (int i = 0; i < snap.Replacements.Count; i++)
                _model.Wires.Insert(Math.Min(insertAt + i, _model.Wires.Count), snap.Replacements[i]);
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var snap in _snaps)
        {
            foreach (var r in snap.Replacements)
                _model.Wires.Remove(r);
            _model.Wires.Insert(Math.Min(snap.OriginalIndex, _model.Wires.Count), snap.Original);
        }
        _model.NotifyChanged();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a point list at the given cut segment indices, discarding any resulting
    /// piece with fewer than 2 points. A cut at index i produces a piece ending at
    /// Points[i] (inclusive) and the next piece starting at Points[i+1].
    /// </summary>
    private static List<List<(double X, double Y)>> ComputePieces(
        IReadOnlyList<(double X, double Y)> pts,
        List<int> sortedCuts)
    {
        var pieces = new List<List<(double X, double Y)>>();
        int start = 0;

        foreach (int cut in sortedCuts)
        {
            // Piece from 'start' to 'cut' inclusive
            var piece = new List<(double X, double Y)>();
            for (int k = start; k <= cut && k < pts.Count; k++)
                piece.Add(pts[k]);
            AddNormalized(pieces, piece);
            start = cut + 1;
        }

        // Trailing piece from 'start' to end
        var last = new List<(double X, double Y)>();
        for (int k = start; k < pts.Count; k++)
            last.Add(pts[k]);
        AddNormalized(pieces, last);

        return pieces;
    }

    // Normalizes a candidate piece and adds it to the list if it has ≥ 2 distinct points.
    private static void AddNormalized(
        List<List<(double X, double Y)>> pieces,
        List<(double X, double Y)> piece)
    {
        var norm = WireGeometry.NormalizePoints(piece).ToList();
        if (norm.Count >= 2) pieces.Add(norm);
    }
}
