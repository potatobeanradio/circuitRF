// Framework-free geometry helpers for SymbolPrimitive lists.
// No Skia / Avalonia references.

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Geometry utilities for symbol primitive lists: bounding-box computation
/// and other framework-free operations shared between the edit model and tests.
/// </summary>
public static class SymbolGeometry
{
    private const double Fallback        = 200.0;
    private const double MinHalfSize     = 5.0;   // minimum half-size for a degenerate primitive bbox
    private const double TextAdvance     = 0.58;  // average advance factor (chars × em → world width)
    private const double TextAscentFrac  = 0.75;  // fraction of FontSize above baseline
    private const double TextDescentFrac = 0.25;  // fraction of FontSize below baseline

    // ── BboxOf — single-primitive AABB ───────────────────────────────────────────

    /// <summary>
    /// Returns the axis-aligned bounding box of a single <see cref="SymbolPrimitive"/>
    /// in component-LOCAL coordinates.  Always has non-zero size (minimum ±5 per axis)
    /// so a degenerate primitive (e.g., a Text anchor point) has a selectable bbox.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) BboxOf(SymbolPrimitive prim)
    {
        // Text/Bitmap are skipped by ComputeBb; handle them here with a font-aware extent.
        double ax, ay, bx, by;
        switch (prim)
        {
            case TextPrimitive t:
            {
                var (tcx, tcy) = TextCenter(t);
                var (tw, th)   = TextBoxSize(t);
                // R90/R270 swap the footprint.
                bool swap = t.Rotation is SymbolRotation.R90 or SymbolRotation.R270;
                double halfW = (swap ? th : tw) * 0.5;
                double halfH = (swap ? tw : th) * 0.5;
                ax = tcx - halfW; ay = tcy - halfH;
                bx = tcx + halfW; by = tcy + halfH;
                break;
            }
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

    // ── Text geometry helpers ─────────────────────────────────────────────────────

    /// <summary>Unrotated text box size (W = content advance, H = ascent+descent), font-approximated.</summary>
    public static (double W, double H) TextBoxSize(TextPrimitive t)
    {
        double w = t.Content is { Length: > 0 } c
            ? c.Length * t.FontSize * TextAdvance
            : t.FontSize * TextAdvance * 2;          // empty: same minimum as old BboxOf (halfW*2)
        double h = t.FontSize * (TextAscentFrac + TextDescentFrac);
        return (w, h);
    }

    /// <summary>Anchor offset from box center in the UNROTATED text frame (screen Y-down).</summary>
    private static (double ox, double oy) TextAnchorOffset(TextPrimitive t)
    {
        var (w, h) = TextBoxSize(t);
        double ox = t.Align switch
        {
            SymbolTextAlign.Center => 0.0,
            SymbolTextAlign.Right  => +w * 0.5,
            _                      => -w * 0.5,      // Left
        };
        double oy = t.VAlign switch
        {
            SymbolTextVAlign.Top    => -h * 0.5,
            SymbolTextVAlign.Middle =>  0.0,
            SymbolTextVAlign.Bottom => +h * 0.5,
            _                       => -h * 0.5 + t.FontSize * TextAscentFrac,  // Baseline (legacy)
        };
        return (ox, oy);
    }

    // CW 90° steps in screen (Y-down) coords — matches RotateBy90's R(x,y)=(−y,x).
    private static (double x, double y) RotStep(double x, double y, SymbolRotation r) => r switch
    {
        SymbolRotation.R90  => (-y,  x),
        SymbolRotation.R180 => (-x, -y),
        SymbolRotation.R270 => ( y, -x),
        _                   => ( x,  y),
    };

    /// <summary>The text box center C, derived from the anchor: C = Anchor − Rot(θ, anchorOffset).</summary>
    public static (double cx, double cy) TextCenter(TextPrimitive t)
    {
        var (ox, oy) = TextAnchorOffset(t);
        var (rx, ry) = RotStep(ox, oy, t.Rotation);
        return (t.AnchorX - rx, t.AnchorY - ry);
    }

    /// <summary>Baseline Y offset from the text box center, in LOCAL units (screen Y-down, +down).
    /// The renderer draws text centered at the box center, so it shifts the baseline by this to
    /// vertically center the glyph box.</summary>
    public static double TextBaselineDyFromCenter(TextPrimitive t)
    {
        var (_, h) = TextBoxSize(t);
        return t.FontSize * TextAscentFrac - h * 0.5;   // ascent below top; box centered at 0
    }

    /// <summary>Sets AnchorX/Y so the box center is (cx, cy): Anchor = C + Rot(θ, anchorOffset).</summary>
    public static void SetTextCenter(TextPrimitive t, double cx, double cy)
    {
        var (ox, oy) = TextAnchorOffset(t);
        var (rx, ry) = RotStep(ox, oy, t.Rotation);
        t.AnchorX = cx + rx;
        t.AnchorY = cy + ry;
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
                if (e.Rx <= 0 || e.Ry <= 0) return false;
                double dx = lx - e.Cx, dy = ly - e.Cy;
                // Outer ellipse expanded by tol on each axis.
                double fox = e.Rx + tol, foy = e.Ry + tol;
                double fOuter = (dx / fox) * (dx / fox) + (dy / foy) * (dy / foy);
                if (fOuter > 1.0) return false; // outside outer boundary
                if (e.Filled) return true;
                // Unfilled: also require point is outside the shrunk inner ellipse.
                double rix = Math.Max(e.Rx - tol, 0.0), riy = Math.Max(e.Ry - tol, 0.0);
                if (rix <= 0 || riy <= 0) return true; // ellipse thinner than 2*tol → whole interior hits
                double fInner = (dx / rix) * (dx / rix) + (dy / riy) * (dy / riy);
                return fInner >= 1.0;
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

            case ExponentialTaperPrimitive et:
            {
                var (x0, y0, x1, y1) = BboxOf(et);
                return InsideExpandedRect(lx, ly, (x0+x1)*0.5, (y0+y1)*0.5, (x1-x0)*0.5, (y1-y0)*0.5, tol);
            }

            case TextPrimitive t:
            {
                var (tbx0, tby0, tbx1, tby1) = BboxOf(t);
                return lx >= tbx0 - tol && lx <= tbx1 + tol
                    && ly >= tby0 - tol && ly <= tby1 + tol;
            }

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

            case ExponentialTaperPrimitive et:
                et.Cx += dx; et.Cy += dy; break;

            case TextPrimitive t:
                t.AnchorX += dx; t.AnchorY += dy; break;

            case BitmapPrimitive bm:
                bm.X += dx; bm.Y += dy; break;
        }
    }

    // ── RotateBy90 — 90° CW in screen (Y-down) coords about symbol origin ────────

    /// <summary>
    /// Rotates all control points of <paramref name="prim"/> 90° clockwise in screen (Y-down)
    /// coordinates about the symbol origin (0, 0): x' = −y, y' = x.
    /// Called by <see cref="Commands.Symbol.RotateSelectionCommand"/> in Execute and (×3) in Undo.
    /// </summary>
    public static void RotateBy90(SymbolPrimitive prim)
    {
        static (double nx, double ny) R(double x, double y) => (-y, x);

        switch (prim)
        {
            case LinePrimitive l:
                (l.X1, l.Y1) = R(l.X1, l.Y1);
                (l.X2, l.Y2) = R(l.X2, l.Y2);
                break;
            case PolylinePrimitive pl:
                foreach (var pt in pl.Points) { (pt[0], pt[1]) = R(pt[0], pt[1]); }
                break;
            case RectPrimitive r:
                (r.Cx, r.Cy) = R(r.Cx, r.Cy);
                (r.W, r.H)   = (r.H, r.W);
                break;
            case RoundedRectPrimitive rr:
                (rr.Cx, rr.Cy) = R(rr.Cx, rr.Cy);
                (rr.W, rr.H)   = (rr.H, rr.W);
                break;
            case CirclePrimitive c:
                (c.Cx, c.Cy) = R(c.Cx, c.Cy);
                break;
            case EllipsePrimitive e:
                (e.Cx, e.Cy) = R(e.Cx, e.Cy);
                (e.Rx, e.Ry) = (e.Ry, e.Rx);
                break;
            case ArcPrimitive a:
                (a.Cx, a.Cy) = R(a.Cx, a.Cy);
                a.StartDeg   = ((a.StartDeg + 90.0) % 360.0 + 360.0) % 360.0;
                break;
            case PolygonPrimitive pg:
                foreach (var pt in pg.Points) { (pt[0], pt[1]) = R(pt[0], pt[1]); }
                break;
            case QuadCurvePrimitive qc:
                (qc.P0X,   qc.P0Y)   = R(qc.P0X,   qc.P0Y);
                (qc.CtrlX, qc.CtrlY) = R(qc.CtrlX, qc.CtrlY);
                (qc.P2X,   qc.P2Y)   = R(qc.P2X,   qc.P2Y);
                break;
            case CubicCurvePrimitive cc:
                (cc.P0X, cc.P0Y) = R(cc.P0X, cc.P0Y);
                (cc.C1X, cc.C1Y) = R(cc.C1X, cc.C1Y);
                (cc.C2X, cc.C2Y) = R(cc.C2X, cc.C2Y);
                (cc.P3X, cc.P3Y) = R(cc.P3X, cc.P3Y);
                break;
            case SinePrimitive s:
                (s.Cx, s.Cy) = R(s.Cx, s.Cy);
                s.Axis = s.Axis == SineAxis.Horizontal ? SineAxis.Vertical : SineAxis.Horizontal;
                break;
            case ExponentialTaperPrimitive et:
                (et.Cx, et.Cy) = R(et.Cx, et.Cy);
                et.Axis = et.Axis == SineAxis.Horizontal ? SineAxis.Vertical : SineAxis.Horizontal;
                break;
            case TextPrimitive t:
                (t.AnchorX, t.AnchorY) = R(t.AnchorX, t.AnchorY);
                t.Rotation = t.Rotation switch
                {
                    SymbolRotation.R0   => SymbolRotation.R90,
                    SymbolRotation.R90  => SymbolRotation.R180,
                    SymbolRotation.R180 => SymbolRotation.R270,
                    _                   => SymbolRotation.R0,
                };
                break;
            case BitmapPrimitive bm:
                (bm.X, bm.Y) = R(bm.X, bm.Y);
                break;
        }
    }

    // ── RotateBy90About — 90° CW about an arbitrary anchor ───────────────────

    /// <summary>
    /// Rotates all control points of <paramref name="prim"/> 90° clockwise in screen (Y-down)
    /// coordinates about the point (ax, ay): translate to origin → <see cref="RotateBy90"/> → translate back.
    /// Called by <see cref="Commands.Symbol.RotateSelectionCommand"/> with the selection bbox center.
    /// </summary>
    public static void RotateBy90About(SymbolPrimitive prim, double ax, double ay)
    {
        TranslateBy(prim, -ax, -ay);
        RotateBy90(prim);
        TranslateBy(prim, ax, ay);
    }

    // ── ScaleBy — scale about a reference point ───────────────────────────────

    /// <summary>
    /// Scales all control points of <paramref name="prim"/> about the reference point
    /// (refX, refY) by factors (sx, sy).  Used by
    /// <see cref="Commands.Symbol.ResizeSymbolPrimitiveCommand"/>.
    /// </summary>
    public static void ScaleBy(SymbolPrimitive prim, double refX, double refY, double sx, double sy)
    {
        static double S(double v, double r, double s) => r + (v - r) * s;

        switch (prim)
        {
            case LinePrimitive l:
                l.X1 = S(l.X1, refX, sx); l.Y1 = S(l.Y1, refY, sy);
                l.X2 = S(l.X2, refX, sx); l.Y2 = S(l.Y2, refY, sy);
                break;
            case PolylinePrimitive pl:
                foreach (var p in pl.Points)
                { p[0] = S(p[0], refX, sx); p[1] = S(p[1], refY, sy); }
                break;
            case RectPrimitive r:
                r.Cx = S(r.Cx, refX, sx); r.Cy = S(r.Cy, refY, sy);
                r.W  = Math.Abs(r.W * sx); r.H  = Math.Abs(r.H * sy);
                break;
            case RoundedRectPrimitive rr:
                rr.Cx = S(rr.Cx, refX, sx); rr.Cy = S(rr.Cy, refY, sy);
                rr.W  = Math.Abs(rr.W * sx); rr.H  = Math.Abs(rr.H * sy);
                rr.Radius = Math.Min(rr.Radius * Math.Min(sx, sy),
                                     Math.Min(rr.W, rr.H) * 0.4);
                break;
            case CirclePrimitive c:
                c.Cx = S(c.Cx, refX, sx); c.Cy = S(c.Cy, refY, sy);
                c.R  = Math.Abs(c.R * Math.Sqrt(sx * sy));
                break;
            case EllipsePrimitive e:
                e.Cx = S(e.Cx, refX, sx); e.Cy = S(e.Cy, refY, sy);
                e.Rx = Math.Abs(e.Rx * sx); e.Ry = Math.Abs(e.Ry * sy);
                break;
            case ArcPrimitive a:
                a.Cx = S(a.Cx, refX, sx); a.Cy = S(a.Cy, refY, sy);
                a.R  = Math.Abs(a.R * Math.Sqrt(sx * sy));
                break;
            case PolygonPrimitive pg:
                foreach (var p in pg.Points)
                { p[0] = S(p[0], refX, sx); p[1] = S(p[1], refY, sy); }
                break;
            case QuadCurvePrimitive qc:
                qc.P0X   = S(qc.P0X,   refX, sx); qc.P0Y   = S(qc.P0Y,   refY, sy);
                qc.CtrlX = S(qc.CtrlX, refX, sx); qc.CtrlY = S(qc.CtrlY, refY, sy);
                qc.P2X   = S(qc.P2X,   refX, sx); qc.P2Y   = S(qc.P2Y,   refY, sy);
                break;
            case CubicCurvePrimitive cc:
                cc.P0X = S(cc.P0X, refX, sx); cc.P0Y = S(cc.P0Y, refY, sy);
                cc.C1X = S(cc.C1X, refX, sx); cc.C1Y = S(cc.C1Y, refY, sy);
                cc.C2X = S(cc.C2X, refX, sx); cc.C2Y = S(cc.C2Y, refY, sy);
                cc.P3X = S(cc.P3X, refX, sx); cc.P3Y = S(cc.P3Y, refY, sy);
                break;
            case SinePrimitive s:
                s.Cx = S(s.Cx, refX, sx); s.Cy = S(s.Cy, refY, sy);
                s.Length = Math.Abs(s.Length * (s.Axis == SineAxis.Horizontal ? sx : sy));
                s.Amp    = Math.Abs(s.Amp    * (s.Axis == SineAxis.Horizontal ? sy : sx));
                break;
            case ExponentialTaperPrimitive et:
                et.Cx = S(et.Cx, refX, sx); et.Cy = S(et.Cy, refY, sy);
                if (et.Axis == SineAxis.Horizontal)
                {
                    et.L  = Math.Abs(et.L  * sx);
                    et.W1 = Math.Abs(et.W1 * sy);
                    et.W2 = Math.Abs(et.W2 * sy);
                }
                else
                {
                    et.L  = Math.Abs(et.L  * sy);
                    et.W1 = Math.Abs(et.W1 * sx);
                    et.W2 = Math.Abs(et.W2 * sx);
                }
                break;
            case TextPrimitive t:
                t.AnchorX = S(t.AnchorX, refX, sx);
                t.AnchorY = S(t.AnchorY, refY, sy);
                break;
            case BitmapPrimitive bm:
                bm.X = S(bm.X, refX, sx); bm.Y = S(bm.Y, refY, sy);
                bm.W = Math.Abs(bm.W * sx); bm.H = Math.Abs(bm.H * sy);
                break;
        }
    }

    // ── Clone — transient copy for live previews ─────────────────────────────

    /// <summary>
    /// Returns a detached copy of <paramref name="prim"/> suitable for use as a transient
    /// live-resize/drag preview.  Mutations to the clone do not affect the original.
    /// </summary>
    public static SymbolPrimitive Clone(SymbolPrimitive prim) => prim switch
    {
        LinePrimitive l => new LinePrimitive
            { ColorRole = l.ColorRole, StrokeTier = l.StrokeTier,
              X1 = l.X1, Y1 = l.Y1, X2 = l.X2, Y2 = l.Y2 },
        PolylinePrimitive pl => new PolylinePrimitive
            { ColorRole = pl.ColorRole, StrokeTier = pl.StrokeTier,
              Points = pl.Points.Select(p => new double[] { p[0], p[1] }).ToList() },
        RectPrimitive r => new RectPrimitive
            { ColorRole = r.ColorRole, StrokeTier = r.StrokeTier, Filled = r.Filled,
              Cx = r.Cx, Cy = r.Cy, W = r.W, H = r.H },
        RoundedRectPrimitive rr => new RoundedRectPrimitive
            { ColorRole = rr.ColorRole, StrokeTier = rr.StrokeTier, Filled = rr.Filled,
              Cx = rr.Cx, Cy = rr.Cy, W = rr.W, H = rr.H, Radius = rr.Radius },
        CirclePrimitive c => new CirclePrimitive
            { ColorRole = c.ColorRole, StrokeTier = c.StrokeTier, Filled = c.Filled,
              Cx = c.Cx, Cy = c.Cy, R = c.R },
        EllipsePrimitive e => new EllipsePrimitive
            { ColorRole = e.ColorRole, StrokeTier = e.StrokeTier, Filled = e.Filled,
              Cx = e.Cx, Cy = e.Cy, Rx = e.Rx, Ry = e.Ry },
        ArcPrimitive a => new ArcPrimitive
            { ColorRole = a.ColorRole, StrokeTier = a.StrokeTier,
              Cx = a.Cx, Cy = a.Cy, R = a.R, StartDeg = a.StartDeg, SweepDeg = a.SweepDeg },
        PolygonPrimitive pg => new PolygonPrimitive
            { ColorRole = pg.ColorRole, StrokeTier = pg.StrokeTier, Filled = pg.Filled,
              Points = pg.Points.Select(p => new double[] { p[0], p[1] }).ToList() },
        QuadCurvePrimitive qc => new QuadCurvePrimitive
            { ColorRole = qc.ColorRole, StrokeTier = qc.StrokeTier,
              P0X = qc.P0X, P0Y = qc.P0Y, CtrlX = qc.CtrlX, CtrlY = qc.CtrlY,
              P2X = qc.P2X, P2Y = qc.P2Y },
        CubicCurvePrimitive cc => new CubicCurvePrimitive
            { ColorRole = cc.ColorRole, StrokeTier = cc.StrokeTier,
              P0X = cc.P0X, P0Y = cc.P0Y, C1X = cc.C1X, C1Y = cc.C1Y,
              C2X = cc.C2X, C2Y = cc.C2Y, P3X = cc.P3X, P3Y = cc.P3Y },
        SinePrimitive s => new SinePrimitive
            { ColorRole = s.ColorRole, StrokeTier = s.StrokeTier,
              Cx = s.Cx, Cy = s.Cy, Amp = s.Amp, Cycles = s.Cycles, Length = s.Length,
              PtsPerCycle = s.PtsPerCycle, Axis = s.Axis },
        ExponentialTaperPrimitive et => new ExponentialTaperPrimitive
            { ColorRole = et.ColorRole, StrokeTier = et.StrokeTier, Filled = et.Filled,
              Cx = et.Cx, Cy = et.Cy, W1 = et.W1, W2 = et.W2, L = et.L,
              NumPts = et.NumPts, Axis = et.Axis },
        TextPrimitive t => new TextPrimitive
            { Content = t.Content, AnchorX = t.AnchorX, AnchorY = t.AnchorY,
              FontSize = t.FontSize, FontStyle = t.FontStyle, Align = t.Align,
              VAlign = t.VAlign, Rotation = t.Rotation, ForceReadable = t.ForceReadable },
        BitmapPrimitive bm => new BitmapPrimitive
            { ImagePathRef = bm.ImagePathRef, X = bm.X, Y = bm.Y, W = bm.W, H = bm.H,
              Opacity = bm.Opacity, Locked = bm.Locked },
        _ => throw new ArgumentOutOfRangeException(nameof(prim), $"Unknown primitive type {prim.GetType().Name}"),
    };

    // ── StrokeTierOf — nullable stroke tier for any primitive ─────────────────

    /// <summary>
    /// Returns the <see cref="SymbolStrokeTier"/> of <paramref name="prim"/>, or
    /// <c>null</c> for primitives that have no stroke (e.g. Text, Bitmap).
    /// Used to derive linewidth-aware hit tolerances.
    /// </summary>
    public static SymbolStrokeTier? StrokeTierOf(SymbolPrimitive prim) => prim switch
    {
        LinePrimitive        l  => l.StrokeTier,
        PolylinePrimitive    pl => pl.StrokeTier,
        RectPrimitive        r  => r.StrokeTier,
        RoundedRectPrimitive rr => rr.StrokeTier,
        CirclePrimitive      c  => c.StrokeTier,
        EllipsePrimitive     e  => e.StrokeTier,
        ArcPrimitive         a  => a.StrokeTier,
        PolygonPrimitive     pg => pg.StrokeTier,
        QuadCurvePrimitive   qc => qc.StrokeTier,
        CubicCurvePrimitive  cc => cc.StrokeTier,
        SinePrimitive              s  => s.StrokeTier,
        ExponentialTaperPrimitive  et => et.StrokeTier,
        _                             => null,
    };

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
    ///   Sine: (cx ± length/2, cy ± amp).
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
                        ExpandRect(s.Cx, s.Cy, s.Length * 0.5, s.Amp);
                    else
                        ExpandRect(s.Cx, s.Cy, s.Amp, s.Length * 0.5);
                    break;

                case ExponentialTaperPrimitive et:
                {
                    double halfW = Math.Max(et.W1, et.W2) * 0.5;
                    if (et.Axis == SineAxis.Horizontal)
                        ExpandRect(et.Cx, et.Cy, et.L * 0.5, halfW);
                    else
                        ExpandRect(et.Cx, et.Cy, halfW, et.L * 0.5);
                    break;
                }

                // Text and Bitmap: no geometric extent without font metrics / image size.
                // Intentionally skipped — callers must add their own guard if needed.
            }
        }

        return any
            ? (minX, minY, maxX, maxY)
            : (-Fallback, -Fallback, Fallback, Fallback);
    }
}
