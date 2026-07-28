// GDSII stream writer (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2). Streaming write, one
// BGNSTR…ENDSTR per structure — hierarchy is preserved (no flattening of instances), only curved
// primitives are flattened (§3.2 R9e) and holes are keyholed (§3.1a). Format-specific: touches only
// bytes and records; the caller (GdsiiExport) supplies already-mangled structure names and already
// rebased CellRef values — this file has no CellFolder/Messages/dialog concerns.

using System.Linq;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>What actually happened during a write — the SAME counters <see cref="GdsiiExport.Analyze"/>
/// reports in the pre-flight fidelity dialog, produced by the identical code path (a dry run into
/// <see cref="Stream.Null"/>) so the preview can never disagree with the real write.</summary>
public sealed record GdsiiExportSummary(
    int CurvedShapesFlattened,
    int HolesKeyholed,
    int BitmapsSkipped,
    IReadOnlyList<string> Diagnostics);

public static class GdsiiWriter
{
    public static GdsiiExportSummary Write(
        Stream stream, IReadOnlyList<InterchangeStructure> structures, GdsiiUnits units, Technology? tech)
    {
        var offenders = GdsiiCoordinateValidation.CheckOverflow(structures);
        if (offenders.Count > 0) throw new GdsiiExportException(offenders);

        int curveCount = 0, holeCount = 0, bitmapCount = 0;
        var diagnostics = new List<string>();

        var w = new GdsiiRecordWriter(stream);
        var time = BuildTimeFields(DateTime.UtcNow);

        w.WriteInt16Array(GdsiiRecordType.Header, [600]);
        w.WriteInt16Array(GdsiiRecordType.BgnLib, time);
        w.WriteAscii(GdsiiRecordType.LibName, "LIB");
        w.WriteReal8Array(GdsiiRecordType.Units, [units.UserUnitMeters, units.DbUnitMeters]);

        foreach (var s in structures)
        {
            w.WriteInt16Array(GdsiiRecordType.BgnStr, time);
            w.WriteAscii(GdsiiRecordType.StrName, s.Name);

            foreach (var shape in s.Shapes)
                WriteShape(w, shape, tech, ref curveCount, ref holeCount, ref bitmapCount);

            foreach (var inst in s.Instances)
                WriteInstance(w, inst);

            w.WriteNoData(GdsiiRecordType.EndStr);
        }

        w.WriteNoData(GdsiiRecordType.EndLib);

        return new GdsiiExportSummary(curveCount, holeCount, bitmapCount, diagnostics);
    }

    // ── Shapes ─────────────────────────────────────────────────────────────────

    private static void WriteShape(
        GdsiiRecordWriter w, LayoutShape shape, Technology? tech,
        ref int curveCount, ref int holeCount, ref int bitmapCount)
    {
        switch (shape)
        {
            case BitmapShape:
                bitmapCount++; // §3.1b R10e — never exported; the count IS the report
                return;
            case LabelShape label:
                WriteText(w, label);
                return;
            case PathShape path:
                WritePath(w, path, tech, ref curveCount);
                return;
            case ViaShape via:
                WriteViaAsBoundary(w, via);
                return;
            default:
                WriteBoundaryLike(w, shape, tech, ref curveCount, ref holeCount);
                return;
        }
    }

    private static void WriteBoundaryLike(
        GdsiiRecordWriter w, LayoutShape shape, Technology? tech, ref int curveCount, ref int holeCount)
    {
        long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
        var rings = LayoutFlattener.Flatten(shape, tol);

        if (IsCurvedPrimitive(shape)) curveCount++;

        long[] outRing;
        if (rings.Count == 1)
        {
            outRing = rings[0];
        }
        else
        {
            // §3.1a — one self-touching contour, a zero-width slit per inner ring.
            outRing = Keyhole(rings[0], rings.Skip(1).ToList());
            holeCount += rings.Count - 1;
        }

        w.WriteNoData(GdsiiRecordType.Boundary);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)shape.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.Datatype, [(short)shape.Layer.Datatype]);
        w.WriteInt32Array(GdsiiRecordType.Xy, ToClosedIntArray(outRing)); // §2.1 item 3 — explicitly closed
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static bool IsCurvedPrimitive(LayoutShape shape) => shape switch
    {
        CircleShape => true,
        RoundedRectShape => true,
        CurveShape c => c.Edges is { } edges && edges.Any(e => e.Kind != EdgeKind.Line),
        _ => false,
    };

    private static void WritePath(GdsiiRecordWriter w, PathShape path, Technology? tech, ref int curveCount)
    {
        long tol = LayoutFlattener.ResolveTolDbu(path, tech);
        bool curved = path.Edges is { } edges && edges.Any(e => e.Kind != EdgeKind.Line);
        long[] centerline = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tol);
        if (curved) curveCount++;

        int pathType = path.End switch
        {
            PathEndStyle.Flush => 0,
            PathEndStyle.Round => 1,
            PathEndStyle.Square => 2,
            PathEndStyle.Extended => 4,
            _ => 0,
        };

        // Canonical PATH record order (per spec): LAYER, DATATYPE, PATHTYPE, WIDTH, [BGNEXTN,
        // ENDEXTN], XY, ENDEL — PATHTYPE before WIDTH, not the other way around. A strict reader
        // (KLayout) enforces this exact order and desyncs its own element parser when it isn't
        // followed, even though every individual record here is otherwise correctly framed.
        w.WriteNoData(GdsiiRecordType.Path);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)path.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.Datatype, [(short)path.Layer.Datatype]);
        w.WriteInt16Array(GdsiiRecordType.PathType, [(short)pathType]);
        w.WriteInt32Array(GdsiiRecordType.Width, [(int)path.Width]);
        if (pathType == 4)
        {
            // Design decision (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md plan): symmetric
            // Width/2 extension on both ends — genuinely exercises BGNEXTN/ENDEXTN rather than
            // reusing PATHTYPE 2's implicit square cap.
            long ext = path.Width / 2;
            w.WriteInt32Array(GdsiiRecordType.BgnExtn, [(int)ext]);
            w.WriteInt32Array(GdsiiRecordType.EndExtn, [(int)ext]);
        }
        w.WriteInt32Array(GdsiiRecordType.Xy, ToOpenIntArray(centerline));
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static void WriteText(GdsiiRecordWriter w, LabelShape label)
    {
        // Labels have no mirror field — reflect is always false for a LabelShape.
        var (_, angle) = GdsiiTransformCodec.ToGdsii(false, label.Rotation);

        w.WriteNoData(GdsiiRecordType.Text);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)label.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.TextType, [(short)(label.IsPort ? 1 : 0)]);
        // GDSII has no native text-height record; WIDTH on a TEXT element is this codebase's own,
        // internally-consistent convention for carrying LabelShape.Height (GdsiiReader reads it back).
        w.WriteInt32Array(GdsiiRecordType.Width, [(int)label.Height]);
        w.WriteBitArray(GdsiiRecordType.Strans, 0);
        w.WriteReal8Array(GdsiiRecordType.Angle, [angle]);
        w.WriteInt32Array(GdsiiRecordType.Xy, [(int)label.X, (int)label.Y]);
        w.WriteAscii(GdsiiRecordType.StringRec, label.Text);
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    private static void WriteViaAsBoundary(GdsiiRecordWriter w, ViaShape via)
    {
        long half = Math.Max(via.PadSize, 2) / 2;
        long x1 = via.X - half, x2 = via.X + half, y1 = via.Y - half, y2 = via.Y + half;
        w.WriteNoData(GdsiiRecordType.Boundary);
        w.WriteInt16Array(GdsiiRecordType.Layer, [(short)via.Layer.Layer]);
        w.WriteInt16Array(GdsiiRecordType.Datatype, [(short)via.Layer.Datatype]);
        w.WriteInt32Array(GdsiiRecordType.Xy,
            [(int)x1, (int)y1, (int)x2, (int)y1, (int)x2, (int)y2, (int)x1, (int)y2, (int)x1, (int)y1]);
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    // ── Instances ──────────────────────────────────────────────────────────────

    private static void WriteInstance(GdsiiRecordWriter w, LayoutInstance inst)
    {
        var (reflect, angle) = GdsiiTransformCodec.ToGdsii(inst.MirrorX, inst.Rot);
        ushort stransBits = reflect ? (ushort)0x8000 : (ushort)0;
        bool isArray = inst.Rows > 1 || inst.Cols > 1;

        w.WriteNoData(isArray ? GdsiiRecordType.ARef : GdsiiRecordType.SRef);
        w.WriteAscii(GdsiiRecordType.SName, inst.CellRef);
        w.WriteBitArray(GdsiiRecordType.Strans, stransBits);
        w.WriteReal8Array(GdsiiRecordType.Mag, [inst.Mag]);
        w.WriteReal8Array(GdsiiRecordType.Angle, [angle]);

        if (isArray)
        {
            w.WriteInt16Array(GdsiiRecordType.ColRow, [(short)inst.Cols, (short)inst.Rows]);
            // §2.1 item 5 — COLROW plus the three already-transformed reference points: origin, the
            // column reference point (origin displaced by Cols×PitchX), the row reference point
            // (origin displaced by Rows×PitchY). Written literally in OUR OWN unrotated-pitch
            // convention (LayoutInstanceTransform.ArrayCellOrigin never rotates the pitch) — a
            // compliant reader takes these three points as-is, with no rotation math of its own
            // needed to recover the grid (see GdsiiReader.ReadRef's own note on the reverse
            // direction), so this is exact for every rotation our own writer ever emits.
            long colRefX = inst.X + (long)inst.Cols * inst.PitchX;
            long rowRefY = inst.Y + (long)inst.Rows * inst.PitchY;
            w.WriteInt32Array(GdsiiRecordType.Xy,
                [(int)inst.X, (int)inst.Y, (int)colRefX, (int)inst.Y, (int)inst.X, (int)rowRefY]);
        }
        else
        {
            w.WriteInt32Array(GdsiiRecordType.Xy, [(int)inst.X, (int)inst.Y]);
        }
        w.WriteNoData(GdsiiRecordType.EndEl);
    }

    // ── Keyholing (§3.1a) ──────────────────────────────────────────────────────

    /// <summary>Bridges each hole ring into the outer ring via a zero-width slit (nearest-point
    /// bridge), producing one self-touching contour — exactly what every GDSII writer does, since the
    /// format cannot express a hole any other way.</summary>
    private static long[] Keyhole(long[] outer, List<long[]> holes)
    {
        var combined = new List<long>(outer);
        foreach (var hole in holes)
        {
            int combinedPoints = combined.Count / 2;
            int holePoints = hole.Length / 2;
            int bestOuterIdx = 0, bestHoleIdx = 0;
            long bestDistSq = long.MaxValue;

            for (int oi = 0; oi < combinedPoints; oi++)
            {
                long ox = combined[oi * 2], oy = combined[oi * 2 + 1];
                for (int hi = 0; hi < holePoints; hi++)
                {
                    long dx = ox - hole[hi * 2], dy = oy - hole[hi * 2 + 1];
                    long distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq) { bestDistSq = distSq; bestOuterIdx = oi; bestHoleIdx = hi; }
                }
            }

            long bridgeX = combined[bestOuterIdx * 2], bridgeY = combined[bestOuterIdx * 2 + 1];
            var insertion = new List<long>();
            for (int k = 0; k <= holePoints; k++)
            {
                int idx = (bestHoleIdx + k) % holePoints;
                insertion.Add(hole[idx * 2]);
                insertion.Add(hole[idx * 2 + 1]);
            }
            insertion.Add(bridgeX);
            insertion.Add(bridgeY); // slit closes back at the outer bridge point

            combined.InsertRange((bestOuterIdx + 1) * 2, insertion);
        }
        return combined.ToArray();
    }

    // ── Small helpers ──────────────────────────────────────────────────────────

    private static short[] BuildTimeFields(DateTime t) =>
    [
        (short)t.Year, (short)t.Month, (short)t.Day, (short)t.Hour, (short)t.Minute, (short)t.Second,
        (short)t.Year, (short)t.Month, (short)t.Day, (short)t.Hour, (short)t.Minute, (short)t.Second,
    ];

    private static int[] ToClosedIntArray(long[] ring)
    {
        var result = new int[ring.Length + 2];
        for (int i = 0; i < ring.Length; i++) result[i] = checked((int)ring[i]);
        result[ring.Length] = result[0];
        result[ring.Length + 1] = result[1];
        return result;
    }

    private static int[] ToOpenIntArray(long[] xy)
    {
        var result = new int[xy.Length];
        for (int i = 0; i < xy.Length; i++) result[i] = checked((int)xy[i]);
        return result;
    }
}
