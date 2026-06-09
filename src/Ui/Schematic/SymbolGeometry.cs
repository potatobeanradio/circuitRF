// Framework-free geometry helpers for SymbolPrimitive lists.
// No Skia / Avalonia references.

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Geometry utilities for symbol primitive lists: bounding-box computation
/// and other framework-free operations shared between the edit model and tests.
/// </summary>
public static class SymbolGeometry
{
    private const double Fallback    = 200.0;
    private const double MinHalfSize = 5.0;   // minimum half-size for a degenerate primitive bbox
    private const double TextHalf    = 30.0;  // approximate half-extent for text/bitmap anchors

    // ── BboxOf — single-primitive AABB ───────────────────────────────────────────

    /// <summary>
    /// Returns the axis-aligned bounding box of a single <see cref="SymbolPrimitive"/>
    /// in component-LOCAL coordinates.  Always has non-zero size (minimum ±5 per axis)
    /// so a degenerate primitive (e.g., a Text anchor point) has a selectable bbox.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) BboxOf(SymbolPrimitive prim)
    {
        // Text/Bitmap are skipped by ComputeBb; handle them here with a fixed extent.
        double ax, ay, bx, by;
        switch (prim)
        {
            case TextPrimitive t:
                ax = t.AnchorX - TextHalf; ay = t.AnchorY - TextHalf;
                bx = t.AnchorX + TextHalf; by = t.AnchorY + TextHalf;
                break;
            case BitmapPrimitive bm:
                ax = bm.X; ay = bm.Y; bx = bm.X + bm.W; by = bm.Y + bm.H;
                break;
            default:
            {
                var (x0, y0, x1, y1) = ComputeBb([prim]);
                ax = x0; ay = y0; bx = x1; by = y1;
                break;
            }
        }

        // Enforce minimum half-size so point/near-degenerate prims are clickable.
        double cx = (ax + bx) * 0.5, cy = (ay + by) * 0.5;
        double hw = Math.Max((bx - ax) * 0.5, MinHalfSize);
        double hh = Math.Max((by - ay) * 0.5, MinHalfSize);
        return (cx - hw, cy - hh, cx + hw, cy + hh);
    }

    // ── HitTest — per-primitive click test ───────────────────────────────────────

    /// <summary>
    /// Returns true if the point (lx, ly) hits <paramref name="prim"/> within tolerance
    /// <paramref name="tol"/> (all in component-LOCAL coordinates).
    /// For filled/closed primitives, the interior counts as a hit.
    /// For open primitives, only near-edge counts.
    /// </summary>
    public static bool HitTest(SymbolPrimitive prim, double lx, double ly, double tol)
    {
        switch (prim)
        {
            case LinePrimitive l:
                return PointToSegDist(lx, ly, l.X1, l.Y1, l.X2, l.Y2) <= tol;

            case PolylinePrimitive pl:
            {
                var pts = pl.Points;
                for (int i = 0; i < pts.Count - 1; i++)
                    if (PointToSegDist(lx, ly, pts[i][0], pts[i][1], pts[i+1][0], pts[i+1][1]) <= tol)
                        return true;
                return false;
            }

            case RectPrimitive r:
            {
                double hw = r.W * 0.5, hh = r.H * 0.5;
                return r.Filled
                    ? InsideExpandedRect(lx, ly, r.Cx, r.Cy, hw, hh, tol)
                    : NearRectEdge(lx, ly, r.Cx, r.Cy, hw, hh, tol);
            }

            case RoundedRectPrimitive rr:
            {
                double hw = rr.W * 0.5, hh = rr.H * 0.5;
                return rr.Filled
                    ? InsideExpandedRect(lx, ly, rr.Cx, rr.Cy, hw, hh, tol)
                    : NearRectEdge(lx, ly, rr.Cx, rr.Cy, hw, hh, tol);
            }

            case CirclePrimitive c:
            {
                double dist = Math.Sqrt((lx - c.Cx) * (lx - c.Cx) + (ly - c.Cy) * (ly - c.Cy));
                return c.Filled ? dist <= c.R + tol : Math.Abs(dist - c.R) <= tol;
            }

            case EllipsePrimitive e:
            {
                // Approximate: use avg radius for hit-test
                double avgR = (e.Rx + e.Ry) * 0.5;
                double dist = Math.Sqrt((lx - e.Cx) * (lx - e.Cx) + (ly - e.Cy) * (ly - e.Cy));
                return e.Filled ? dist <= avgR + tol : Math.Abs(dist - avgR) <= tol;
            }

            case ArcPrimitive a:
            {
                double dist = Math.Sqrt((lx - a.Cx) * (lx - a.Cx) + (ly - a.Cy) * (ly - a.Cy));
                if (Math.Abs(dist - a.R) > tol) return false;
                return PointInArcAngleRange(lx - a.Cx, ly - a.Cy, a.StartDeg, a.SweepDeg);
            }

            case PolygonPrimitive pg:
            {
                var pts = pg.Points;
                if (pg.Filled && pts.Count >= 3 && PointInPolygon(lx, ly, pts)) return true;
                for (int i = 0; i < pts.Count; i++)
                {
                    int j = (i + 1) % pts.Count;
                    if (PointToSegDist(lx, ly, pts[i][0], pts[i][1], pts[j][0], pts[j][1]) <= tol)
                        return true;
                }
                return false;
            }

            case QuadCurvePrimitive qc:
                return NearSampledCurve(lx, ly, tol, SampleQuad(qc));

            case CubicCurvePrimitive cc:
                return NearSampledCurve(lx, ly, tol, SampleCubic(cc));

            case SinePrimitive s:
            {
                var (x0, y0, x1, y1) = BboxOf(s);
                return InsideExpandedRect(lx, ly, (x0+x1)*0.5, (y0+y1)*0.5, (x1-x0)*0.5, (y1-y0)*0.5, tol);
            }

            case HalfWavePrimitive hw:
            {
                var (x0, y0, x1, y1) = BboxOf(hw);
                return InsideExpandedRect(lx, ly, (x0+x1)*0.5, (y0+y1)*0.5, (x1-x0)*0.5, (y1-y0)*0.5, tol);
            }

            case TextPrimitive t:
                return Math.Abs(lx - t.AnchorX) <= TextHalf + tol
                    && Math.Abs(ly - t.AnchorY) <= TextHalf + tol;

            case BitmapPrimitive bm:
                return lx >= bm.X - tol && lx <= bm.X + bm.W + tol
                    && ly >= bm.Y - tol && ly <= bm.Y + bm.H + tol;

            default:
                return false;
        }
    }

    // ── TranslateBy — in-place mutation used by Move commands ─────────────────

    /// <summary>
    /// Translates all control points of <paramref name="prim"/> by (dx, dy) in-place.
    /// Called by <see cref="Commands.Symbol.MoveSymbolPrimitivesCommand"/> in both
    /// Execute (positive delta) and Undo (negated delta).
    /// </summary>
    public static void TranslateBy(SymbolPrimitive prim, double dx, double dy)
    {
        switch (prim)
        {
            case LinePrimitive l:
                l.X1 += dx; l.Y1 += dy; l.X2 += dx; l.Y2 += dy; break;

            case PolylinePrimitive pl:
                foreach (var pt in pl.Points) { pt[0] += dx; pt[1] += dy; }
                break;

            case RectPrimitive r:
                r.Cx += dx; r.Cy += dy; break;

            case RoundedRectPrimitive rr:
                rr.Cx += dx; rr.Cy += dy; break;

            case CirclePrimitive c:
                c.Cx += dx; c.Cy += dy; break;

            case EllipsePrimitive e:
                e.Cx += dx; e.Cy += dy; break;

            case ArcPrimitive a:
                a.Cx += dx; a.Cy += dy; break;

            case PolygonPrimitive pg:
                foreach (var pt in pg.Points) { pt[0] += dx; pt[1] += dy; }
                break;

            case QuadCurvePrimitive qc:
                qc.P0X += dx; qc.P0Y += dy;
                qc.CtrlX += dx; qc.CtrlY += dy;
                qc.P2X += dx; qc.P2Y += dy;
                break;

            case CubicCurvePrimitive cc:
                cc.P0X += dx; cc.P0Y += dy;
                cc.C1X += dx; cc.C1Y += dy;
                cc.C2X += dx; cc.C2Y += dy;
                cc.P3X += dx; cc.P3Y += dy;
                break;

            case SinePrimitive s:
                s.Cx += dx; s.Cy += dy; break;

            case HalfWavePrimitive hw:
                hw.Cx += dx; hw.Cy += dy; break;

            case TextPrimitive t:
                t.AnchorX += dx; t.AnchorY += dy; break;

            case BitmapPrimitive bm:
                bm.X += dx; bm.Y += dy; break;
        }
    }

    // ── Geometry helpers (private) ─────────────────────────────────────────────

    private static double PointToSegDist(double px, double py,
                                          double ax, double ay, double bx, double by)
    {
        double ddx = bx - ax, ddy = by - ay;
        double lenSq = ddx * ddx + ddy * ddy;
        if (lenSq < 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double t = Math.Clamp(((px - ax) * ddx + (py - ay) * ddy) / lenSq, 0.0, 1.0);
        double cx = ax + t * ddx, cy = ay + t * ddy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static bool InsideExpandedRect(double px, double py,
                                            double cx, double cy, double hw, double hh, double tol)
        => Math.Abs(px - cx) <= hw + tol && Math.Abs(py - cy) <= hh + tol;

    private static bool NearRectEdge(double px, double py,
                                      double cx, double cy, double hw, double hh, double tol)
    {
        // Within the expanded rect AND near at least one edge
        if (!InsideExpandedRect(px, py, cx, cy, hw + tol, hh + tol, 0.0)) return false;
        double dx = Math.Abs(px - cx), dy = Math.Abs(py - cy);
        return dx >= hw - tol || dy >= hh - tol;
    }

    private static bool PointInPolygon(double px, double py, List<double[]> pts)
    {
        int n = pts.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = pts[i][0], yi = pts[i][1];
            double xj = pts[j][0], yj = pts[j][1];
            if ((yi > py) != (yj > py) &&
                px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static bool PointInArcAngleRange(double relX, double relY, double startDeg, double sweepDeg)
    {
        // Angle of (relX, relY) relative to arc center, clockwise from +x in screen (y-down) coords.
        double angle = Math.Atan2(relY, relX) * (180.0 / Math.PI);
        // Normalize to [0, 360)
        angle = ((angle % 360.0) + 360.0) % 360.0;
        double start = ((startDeg % 360.0) + 360.0) % 360.0;

        if (sweepDeg >= 0)
        {
            double norm = angle;
            if (norm < start - 0.001) norm += 360.0;
            return norm <= start + sweepDeg + 0.001;
        }
        else
        {
            double norm = angle;
            if (norm > start + 0.001) norm -= 360.0;
            return norm >= start + sweepDeg - 0.001;
        }
    }

    private static (double X, double Y)[] SampleQuad(QuadCurvePrimitive qc)
    {
        const int N = 12;
        var pts = new (double, double)[N + 1];
        for (int i = 0; i <= N; i++)
        {
            double t = i / (double)N;
            double mt = 1.0 - t;
            pts[i] = (mt * mt * qc.P0X + 2 * mt * t * qc.CtrlX + t * t * qc.P2X,
                      mt * mt * qc.P0Y + 2 * mt * t * qc.CtrlY + t * t * qc.P2Y);
        }
        return pts;
    }

    private static (double X, double Y)[] SampleCubic(CubicCurvePrimitive cc)
    {
        const int N = 16;
        var pts = new (double, double)[N + 1];
        for (int i = 0; i <= N; i++)
        {
            double t  = i / (double)N;
            double mt = 1.0 - t;
            pts[i] = (mt*mt*mt * cc.P0X + 3*mt*mt*t * cc.C1X + 3*mt*t*t * cc.C2X + t*t*t * cc.P3X,
                      mt*mt*mt * cc.P0Y + 3*mt*mt*t * cc.C1Y + 3*mt*t*t * cc.C2Y + t*t*t * cc.P3Y);
        }
        return pts;
    }

    private static bool NearSampledCurve(double px, double py, double tol,
                                          (double X, double Y)[] pts)
    {
        for (int i = 0; i < pts.Length - 1; i++)
            if (PointToSegDist(px, py, pts[i].X, pts[i].Y, pts[i+1].X, pts[i+1].Y) <= tol)
                return true;
        return false;
    }

    // ── ComputeBb (existing) ──────────────────────────────────────────────────

    /// <summary>
    /// Computes the axis-aligned bounding box of a list of SymbolPrimitives in
    /// component-LOCAL coordinates.  Returns (0,0,0,0) for an empty list.
    /// For each primitive type the conservative bound is used:
    ///   Line/Polyline/Polygon: exact endpoint/vertex hull.
    ///   Rect/RoundedRect: exact corner hull.
    ///   Circle/Ellipse: axis-aligned box around the full ellipse.
    ///   Arc: axis-aligned box around the full containing circle (conservative).
    ///   QuadCurve/CubicCurve: convex hull of control polygon (conservative).
    ///   Sine/HalfWave: (cx ± length/2, cy ± amp).
    ///   Text/Bitmap: skipped (no geometric extent known without font metrics).
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) ComputeBb(
        IReadOnlyList<SymbolPrimitive> primitives)
    {
        if (primitives.Count == 0)
            return (0, 0, 0, 0);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool   any  = false;

        void Expand(double x, double y)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            any = true;
        }

        void ExpandRect(double cx, double cy, double hw, double hh)
        {
            Expand(cx - hw, cy - hh);
            Expand(cx + hw, cy + hh);
        }

        foreach (var prim in primitives)
        {
            switch (prim)
            {
                case LinePrimitive l:
                    Expand(l.X1, l.Y1);
                    Expand(l.X2, l.Y2);
                    break;

                case PolylinePrimitive pl:
                    foreach (var pt in pl.Points)
                        if (pt.Length >= 2) Expand(pt[0], pt[1]);
                    break;

                case RectPrimitive r:
                    ExpandRect(r.Cx, r.Cy, r.W * 0.5, r.H * 0.5);
                    break;

                case RoundedRectPrimitive rr:
                    ExpandRect(rr.Cx, rr.Cy, rr.W * 0.5, rr.H * 0.5);
                    break;

                case CirclePrimitive c:
                    ExpandRect(c.Cx, c.Cy, c.R, c.R);
                    break;

                case EllipsePrimitive e:
                    ExpandRect(e.Cx, e.Cy, e.Rx, e.Ry);
                    break;

                case ArcPrimitive a:
                    // Conservative: full circle bounding box.
                    ExpandRect(a.Cx, a.Cy, a.R, a.R);
                    break;

                case PolygonPrimitive pg:
                    foreach (var pt in pg.Points)
                        if (pt.Length >= 2) Expand(pt[0], pt[1]);
                    break;

                case QuadCurvePrimitive qc:
                    Expand(qc.P0X, qc.P0Y);
                    Expand(qc.CtrlX, qc.CtrlY);
                    Expand(qc.P2X, qc.P2Y);
                    break;

                case CubicCurvePrimitive cc:
                    Expand(cc.P0X, cc.P0Y);
                    Expand(cc.C1X, cc.C1Y);
                    Expand(cc.C2X, cc.C2Y);
                    Expand(cc.P3X, cc.P3Y);
                    break;

                case SinePrimitive s:
                    if (s.Axis == SineAxis.Horizontal)
                    {
                        ExpandRect(s.Cx, s.Cy, s.Length * 0.5, s.Amp);
                    }
                    else
                    {
                        ExpandRect(s.Cx, s.Cy, s.Amp, s.Length * 0.5);
                    }
                    break;

                case HalfWavePrimitive hw:
                    if (hw.Axis == SineAxis.Horizontal)
                    {
                        ExpandRect(hw.Cx, hw.Cy, hw.Length * 0.5, hw.Amp);
                    }
                    else
                    {
                        ExpandRect(hw.Cx, hw.Cy, hw.Amp, hw.Length * 0.5);
                    }
                    break;

                // Text and Bitmap: no geometric extent without font metrics / image size.
                // Intentionally skipped — callers must add their own guard if needed.
            }
        }

        return any
            ? (minX, minY, maxX, maxY)
            : (-Fallback, -Fallback, Fallback, Fallback);
    }
}
