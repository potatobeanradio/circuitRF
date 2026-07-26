// DBU resolution migration (docs/design/layout-view.md §1.4 R4). A preference toggle this is not:
// refinement always succeeds; coarsening pre-scans without mutating and only commits when the
// whole design (every coordinate) survives the ratio exactly.

namespace CircuitRF.Ui.Layout;

public static class LayoutScaling
{
    private const int MaxOffenders = 20;

    /// <summary>
    /// Changes a layout's DBU resolution. <paramref name="newDbuPerMicron"/> must be an exact integer
    /// multiple or divisor of the current resolution. Refinement (multiply) always succeeds. Coarsening
    /// (divide) pre-scans every coordinate without mutating; if any coordinate would not survive the
    /// division exactly, returns false with a bounded, named offender list and leaves the layout
    /// completely unmutated — partial mutation on failure is the one unacceptable outcome.
    /// </summary>
    public static bool TryChangeResolution(LayoutView view, int newDbuPerMicron, out IReadOnlyList<string> offenders)
    {
        int current = view.DbuPerMicron;

        if (newDbuPerMicron <= 0)
        {
            offenders = [$"New resolution must be positive (got {newDbuPerMicron})."];
            return false;
        }

        if (newDbuPerMicron == current)
        {
            offenders = [];
            return true;
        }

        long ratio;
        bool refine;
        if (newDbuPerMicron > current && newDbuPerMicron % current == 0)
        {
            ratio = newDbuPerMicron / current;
            refine = true;
        }
        else if (current > newDbuPerMicron && current % newDbuPerMicron == 0)
        {
            ratio = current / newDbuPerMicron;
            refine = false;
        }
        else
        {
            offenders = [$"New resolution {newDbuPerMicron} DBU/µm is not an exact integer multiple or " +
                         $"divisor of the current resolution {current} DBU/µm."];
            return false;
        }

        if (refine)
        {
            ScaleView(view, v => v * ratio);
            view.DbuPerMicron = newDbuPerMicron;
            offenders = [];
            return true;
        }

        // Coarsening: pre-scan without mutating anything.
        var problems = new List<string>();
        int total = 0;
        ScanView(view, (v, label) =>
        {
            if (v % ratio != 0)
            {
                total++;
                if (problems.Count < MaxOffenders)
                    problems.Add($"{label}: {v} is not divisible by {ratio}.");
            }
        });

        if (total > 0)
        {
            if (total > MaxOffenders)
                problems.Add($"...and {total - MaxOffenders} more.");
            offenders = problems;
            return false;
        }

        ScaleView(view, v => v / ratio);
        view.DbuPerMicron = newDbuPerMicron;
        offenders = [];
        return true;
    }

    // ── Mutating pass ─────────────────────────────────────────────────────────

    private static void ScaleView(LayoutView view, Func<long, long> f)
    {
        view.SnapDbu = f(view.SnapDbu);

        foreach (var shape in view.Shapes)
            ScaleShape(shape, f);

        foreach (var inst in view.Instances)
        {
            inst.X = f(inst.X);
            inst.Y = f(inst.Y);
            inst.PitchX = f(inst.PitchX);
            inst.PitchY = f(inst.PitchY);
        }
    }

    private static void ScaleShape(LayoutShape shape, Func<long, long> f)
    {
        switch (shape)
        {
            case RectShape r:
                r.X1 = f(r.X1); r.Y1 = f(r.Y1); r.X2 = f(r.X2); r.Y2 = f(r.Y2);
                break;
            case PolygonShape p:
                ScaleArray(p.Xy, f);
                break;
            case RoundedRectShape rr:
                rr.X1 = f(rr.X1); rr.Y1 = f(rr.Y1); rr.X2 = f(rr.X2); rr.Y2 = f(rr.Y2);
                rr.CornerRadius = f(rr.CornerRadius);
                break;
            case CircleShape c:
                c.Cx = f(c.Cx); c.Cy = f(c.Cy); c.R = f(c.R);
                break;
            case CurveShape curve:
                ScaleArray(curve.Xy, f);
                ScaleCubicControlPoints(curve.Edges, f);
                if (curve.FlattenTolDbu is { } ctol) curve.FlattenTolDbu = f(ctol);
                break;
            case PathShape path:
                ScaleArray(path.Xy, f);
                ScaleCubicControlPoints(path.Edges, f);
                path.Width = f(path.Width);
                if (path.FlattenTolDbu is { } ptol) path.FlattenTolDbu = f(ptol);
                break;
            case ViaShape via:
                via.X = f(via.X); via.Y = f(via.Y);
                via.PadSize = f(via.PadSize); via.DrillSize = f(via.DrillSize);
                break;
            case LabelShape label:
                label.X = f(label.X); label.Y = f(label.Y);
                label.Height = f(label.Height);
                break;
        }
    }

    private static void ScaleArray(long[] xy, Func<long, long> f)
    {
        for (int i = 0; i < xy.Length; i++)
            xy[i] = f(xy[i]);
    }

    /// <summary>Bulge is a dimensionless sweep-angle descriptor, not a coordinate — never scaled.
    /// Cubic control points are coordinates and are easy to miss (they are NOT in the Xy vertex list).</summary>
    private static void ScaleCubicControlPoints(List<LayoutEdge>? edges, Func<long, long> f)
    {
        if (edges == null) return;
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Cubic) continue;
            e.C1X = f(e.C1X); e.C1Y = f(e.C1Y);
            e.C2X = f(e.C2X); e.C2Y = f(e.C2Y);
        }
    }

    // ── Read-only scan pass (same coordinate set as ScaleView) ───────────────

    private static void ScanView(LayoutView view, Action<long, string> check)
    {
        check(view.SnapDbu, "SnapDbu");

        for (int i = 0; i < view.Shapes.Count; i++)
            ScanShape(view.Shapes[i], i, check);

        for (int i = 0; i < view.Instances.Count; i++)
        {
            var inst = view.Instances[i];
            check(inst.X, $"Instances[{i}].X");
            check(inst.Y, $"Instances[{i}].Y");
            check(inst.PitchX, $"Instances[{i}].PitchX");
            check(inst.PitchY, $"Instances[{i}].PitchY");
        }
    }

    private static void ScanShape(LayoutShape shape, int index, Action<long, string> check)
    {
        string tag = $"Shapes[{index}] ({shape.GetType().Name})";
        switch (shape)
        {
            case RectShape r:
                check(r.X1, $"{tag}.X1"); check(r.Y1, $"{tag}.Y1");
                check(r.X2, $"{tag}.X2"); check(r.Y2, $"{tag}.Y2");
                break;
            case PolygonShape p:
                ScanArray(p.Xy, tag, check);
                break;
            case RoundedRectShape rr:
                check(rr.X1, $"{tag}.X1"); check(rr.Y1, $"{tag}.Y1");
                check(rr.X2, $"{tag}.X2"); check(rr.Y2, $"{tag}.Y2");
                check(rr.CornerRadius, $"{tag}.CornerRadius");
                break;
            case CircleShape c:
                check(c.Cx, $"{tag}.Cx"); check(c.Cy, $"{tag}.Cy"); check(c.R, $"{tag}.R");
                break;
            case CurveShape curve:
                ScanArray(curve.Xy, tag, check);
                ScanCubicControlPoints(curve.Edges, tag, check);
                if (curve.FlattenTolDbu is { } ctol) check(ctol, $"{tag}.FlattenTolDbu");
                break;
            case PathShape path:
                ScanArray(path.Xy, tag, check);
                ScanCubicControlPoints(path.Edges, tag, check);
                check(path.Width, $"{tag}.Width");
                if (path.FlattenTolDbu is { } ptol) check(ptol, $"{tag}.FlattenTolDbu");
                break;
            case ViaShape via:
                check(via.X, $"{tag}.X"); check(via.Y, $"{tag}.Y");
                check(via.PadSize, $"{tag}.PadSize"); check(via.DrillSize, $"{tag}.DrillSize");
                break;
            case LabelShape label:
                check(label.X, $"{tag}.X"); check(label.Y, $"{tag}.Y");
                check(label.Height, $"{tag}.Height");
                break;
        }
    }

    private static void ScanArray(long[] xy, string tag, Action<long, string> check)
    {
        for (int i = 0; i < xy.Length; i++)
            check(xy[i], $"{tag}.Xy[{i}]");
    }

    private static void ScanCubicControlPoints(List<LayoutEdge>? edges, string tag, Action<long, string> check)
    {
        if (edges == null) return;
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if (e.Kind != EdgeKind.Cubic) continue;
            check(e.C1X, $"{tag}.Edges[{i}].C1X"); check(e.C1Y, $"{tag}.Edges[{i}].C1Y");
            check(e.C2X, $"{tag}.Edges[{i}].C2X"); check(e.C2Y, $"{tag}.Edges[{i}].C2Y");
        }
    }
}
