// WHERE A SUB-CELL TOUCHES THE DESIGN IT SITS IN, AWAY FROM EVERY PIN IT DECLARES —
// brief-lvs-9-hierarchy.md R-lvs9-3, docs/design/lvs.md §4.5/R-lvs-18.
//
// ── THE CLASSIC HIERARCHICAL-LVS FAILURE, AND WHY IT IS ANSWERED HERE ──────────────────────────
//
// A hierarchical reading says a cell reaches the design around it through its declared pins and
// nowhere else. The artwork is free to disagree: a shield frame, a ground ring, a module drawn to
// overlap the pour it sits on. Where it does, EVERY net the hierarchy concluded is suspect —
// silently, because the copper is plainly joined in the picture and the netlist plainly says it is
// not.
//
// The only honest responses are to report it or to flatten that one cell (R-lvs9-3a), and
// circuitRF does both. What is NOT a response is absorbing the contact into the parent's net: the
// hierarchy would then say one thing and the copper another, which is the whole class of defect
// LVS exists to find, reintroduced by LVS itself (R-lvs9-3c).
//
// ── IT IS ONE BOOLEAN PER INSTANCE PER LAYER, ON GEOMETRY ALREADY UNIONED (R-lvs9-3e) ──────────
//
// The parent's copper is unioned once, by CopperPieces, and is handed in as that partition — this
// file intersects against its PIECES and never re-unions it. The sub-cell's own contributed copper
// is unioned once per PLACEMENT, which is what R-lvs9-3e's "per instance" buys: two placements of
// one cell can overlap entirely different parent metal, so the answer is a property of the
// placement and not of the cell.
//
// It lives in Extraction rather than in Lvs/ because it calls Clipper, and geometry belongs to
// Extraction and Drc — the rule `LayoutRead`'s own header states and a source scan holds.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// One place a sub-cell's copper meets the parent's away from a declared pin — R-lvs9-3b, which
/// names the layer and the coordinate and carries a marker.
/// </summary>
/// <param name="Layer">The drawing layer the two met on.</param>
/// <param name="X">DBU, a point inside the contact.</param>
/// <param name="Y">DBU.</param>
/// <param name="WidthDbu">The contact's smaller extent — how much metal is actually touching,
/// which is what tells a deliberate overlap from a 2 DBU graze.</param>
/// <param name="Rings">The contact's own outline, in the DRC marker convention.</param>
public readonly record struct SubCellContactPoint(
    LayerKey Layer, long X, long Y, long WidthDbu, IReadOnlyList<long[]> Rings);

/// <summary>Whether a placed sub-cell keeps to its declared pins — R-lvs9-3.</summary>
public static class SubCellContact
{
    /// <summary>How many contacts one placement reports before the walk stops. A module drawn
    /// across a pour meets it in hundreds of places and every one of them is the same defect.</summary>
    public const int MaxPerPlacement = 8;

    /// <summary>
    /// Every place <paramref name="subCellCopper"/> meets <paramref name="parent"/>'s copper
    /// without a declared pin of this placement inside it.
    /// </summary>
    /// <param name="subCellCopper">What this PLACEMENT contributed, in the parent's own frame —
    /// the tagged flatten's shapes for one root instance, boundary pads included.</param>
    /// <param name="parent">The parent's partition, built WITHOUT this placement's internals. Its
    /// pieces are already unioned, which is what keeps this cheap.</param>
    /// <param name="declaredPins">This placement's boundary pins, transformed into the parent's
    /// frame, each on its own layer. A component of the intersection holding one of these is the
    /// declared contact and is not a finding.</param>
    /// <param name="tech">Supplies the expansion every reading of this board's copper shares.</param>
    /// <remarks>
    /// <b>A component of the INTERSECTION, not of the union</b> (R-lvs9-3e). A module whose pad
    /// overlaps a trace and whose ground ring also grazes it meets the parent twice on one piece of
    /// metal; asking whether the PIECE holds a pin would answer yes and report nothing. The ring is
    /// the graze, and it is what a designer has to go and look at.
    /// </remarks>
    public static IReadOnlyList<SubCellContactPoint> Find(
        IReadOnlyList<LayoutShape> subCellCopper,
        CopperPieces parent,
        IReadOnlyList<(long X, long Y, LayerKey Layer)> declaredPins,
        Technology? tech)
    {
        ArgumentNullException.ThrowIfNull(subCellCopper);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(declaredPins);

        if (tech is null || subCellCopper.Count == 0 || !parent.Any) return [];

        var found = new List<SubCellContactPoint>();

        // One union per placement per layer, through the same expansion every other reader uses —
        // so a via decomposes into a barrel and a landing pad here exactly as it does there.
        foreach (var (layer, sub) in LayerRegions.Build(subCellCopper, tech))
        {
            if (sub.Count == 0) continue;
            var box = BoundsOf(sub);

            foreach (int piece in parent.PiecesOn(layer))
            {
                // The narrow phase, and the reason the parent is never re-unioned: a placement in
                // one corner of a board is rejected against every piece in the other corner for
                // the cost of four comparisons.
                if (!box.Intersects(parent.BoundsOfPiece(piece))) continue;

                var meeting = Clipper.Intersect(sub, parent.PathsOfPiece(piece), LayoutClipper.Rule);
                if (meeting.Count == 0) continue;

                foreach (var component in Components(meeting))
                {
                    if (HoldsADeclaredPin(component, declaredPins, layer)) continue;

                    var bounds = BoundsOf(component);
                    found.Add(new SubCellContactPoint(
                        layer,
                        (bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2,
                        Math.Min(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY),
                        DrcRegions.ToRings(component)));

                    if (found.Count >= MaxPerPlacement) return found;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The intersection's connected components, one <see cref="Paths64"/> each.
    /// </summary>
    /// <remarks>
    /// Clipper's <c>NonZero</c> result is outer rings and their holes in one list; an outer ring
    /// winds positively after the union <see cref="LayerRegions"/> already performed, so a
    /// negatively-wound path is a hole of the outer ring before it. A hole never carries a pin of
    /// its own, so it rides with the component it belongs to rather than becoming one.
    /// </remarks>
    private static IEnumerable<Paths64> Components(Paths64 intersection)
    {
        Paths64? current = null;
        foreach (var path in intersection)
        {
            if (Clipper.Area(path) >= 0)
            {
                if (current is not null) yield return current;
                current = [path];
            }
            else current?.Add(path);
        }
        if (current is not null) yield return current;
    }

    private static bool HoldsADeclaredPin(
        Paths64 component, IReadOnlyList<(long X, long Y, LayerKey Layer)> pins, LayerKey layer)
    {
        foreach (var (x, y, pinLayer) in pins)
            if (pinLayer == layer && Regions.Contains(component, x, y)) return true;
        return false;
    }

    private static Bbox BoundsOf(Paths64 paths)
    {
        var box = Bbox.Empty;
        foreach (var path in paths)
            foreach (var point in path)
                box = box.Union(new Bbox(point.X, point.Y, point.X, point.Y));
        return box;
    }
}
