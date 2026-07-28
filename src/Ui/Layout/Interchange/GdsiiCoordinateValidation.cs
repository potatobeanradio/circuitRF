// §2.1 item 2: GDSII coordinates are 4-byte signed integers (±2,147,483,647); our storage is long.
// "Validate on export and report any coordinate that will not fit, naming the shape, rather than
// truncating." Checked directly against each shape's own defining fields (extents/vertices/control
// points) — sufficient because flattening a curved primitive never produces a point outside its own
// defining extent (a circle's flattened points lie exactly on its radius; a cubic's subdivision points
// lie within its control polygon's convex hull), so there is no need to flatten twice just to validate.

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiCoordinateValidation
{
    public static IReadOnlyList<string> CheckOverflow(IReadOnlyList<InterchangeStructure> structures)
    {
        var offenders = new List<string>();
        foreach (var s in structures)
        {
            int shapeIndex = 0;
            foreach (var shape in s.Shapes)
            {
                shapeIndex++;
                if (shape is BitmapShape) continue; // never exported — nothing to validate
                if (!AllCoordsInRange(shape))
                    offenders.Add(
                        $"{s.Name}: shape #{shapeIndex} ({shape.GetType().Name} on layer " +
                        $"{shape.Layer.Layer}/{shape.Layer.Datatype}) has a coordinate beyond GDSII's " +
                        "32-bit integer range.");
            }

            int instIndex = 0;
            foreach (var inst in s.Instances)
            {
                instIndex++;
                long colExtentX = inst.X + (long)inst.Cols * inst.PitchX;
                long rowExtentY = inst.Y + (long)inst.Rows * inst.PitchY;
                if (!InRange(inst.X) || !InRange(inst.Y) || !InRange(colExtentX) || !InRange(rowExtentY))
                    offenders.Add(
                        $"{s.Name}: instance #{instIndex} (CellRef=\"{inst.CellRef}\") has a coordinate " +
                        "beyond GDSII's 32-bit integer range.");
            }
        }
        return offenders;
    }

    private static bool InRange(long v) => v >= int.MinValue && v <= int.MaxValue;

    private static bool AllCoordsInRange(LayoutShape shape)
    {
        bool ok = true;
        void Check(long v) { if (!InRange(v)) ok = false; }

        switch (shape)
        {
            case RectShape r:
                Check(r.X1); Check(r.Y1); Check(r.X2); Check(r.Y2);
                break;
            case PolygonShape p:
                foreach (var v in p.Xy) Check(v);
                if (p.Holes is not null) foreach (var h in p.Holes) foreach (var v in h) Check(v);
                break;
            case RoundedRectShape rr:
                Check(rr.X1); Check(rr.Y1); Check(rr.X2); Check(rr.Y2);
                break;
            case CircleShape c:
                Check(c.Cx - c.R); Check(c.Cx + c.R); Check(c.Cy - c.R); Check(c.Cy + c.R);
                break;
            case CurveShape curve:
                foreach (var v in curve.Xy) Check(v);
                CheckCubicControls(curve.Edges, Check);
                if (curve.Holes is not null) foreach (var h in curve.Holes) foreach (var v in h) Check(v);
                break;
            case PathShape path:
                foreach (var v in path.Xy) Check(v);
                CheckCubicControls(path.Edges, Check);
                break;
            case LabelShape l:
                Check(l.X); Check(l.Y);
                break;
            case ViaShape via:
                Check(via.X); Check(via.Y);
                break;
        }
        return ok;
    }

    private static void CheckCubicControls(List<LayoutEdge>? edges, Action<long> check)
    {
        if (edges is null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            check(e.C1X); check(e.C1Y); check(e.C2X); check(e.C2Y);
        }
    }
}

/// <summary>Thrown by <see cref="GdsiiWriter.Write"/> before any bytes are written when one or more
/// shapes/instances have a coordinate beyond GDSII's 32-bit integer range (§2.1 item 2, gate 8).</summary>
public sealed class GdsiiExportException(IReadOnlyList<string> offenders)
    : Exception($"GDSII export aborted — {offenders.Count} coordinate(s) exceed the 32-bit integer range:\n" +
                string.Join('\n', offenders))
{
    public IReadOnlyList<string> Offenders { get; } = offenders;
}
