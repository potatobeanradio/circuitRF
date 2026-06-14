using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Symbol;

/// <summary>
/// Rotates a set of SymbolPrimitives (and optionally SymbolPins) 90° CW (screen Y-down) about
/// the selection's bounding-box CENTER — ((minX+maxX)/2, (minY+maxY)/2) of the combined bbox.
/// Undo restores exact original coordinates from a snapshot taken at construction time.
/// The center is rotation-invariant, so N×90° composes to identity for grid-aligned selections.
/// Pins rotate with full precision (no per-step snap) to avoid cumulative drift.
/// </summary>
internal sealed class RotateSelectionCommand : IUiCommand
{
    private readonly EditableSymbol        _symbol;
    private readonly List<SymbolPrimitive> _prims;
    private readonly List<SymbolPin>       _pins;

    // center of the combined prims+pins bbox — rotation-invariant so 4× Execute = identity.
    private readonly double _cx, _cy;

    // Closures that restore each primitive's coordinates to their pre-Execute values.
    private readonly List<Action> _primRestores;

    // Pin positions captured at construction for exact Undo.
    private readonly double[] _pinOldX, _pinOldY;

    public string Description => "Rotate";

    public RotateSelectionCommand(EditableSymbol symbol,
        IEnumerable<SymbolPrimitive> prims,
        IEnumerable<SymbolPin>? pins = null)
    {
        _symbol = symbol;
        _prims  = prims.ToList();
        _pins   = pins?.ToList() ?? [];

        (_cx, _cy) = ComputeCenter(_prims, _pins);

        // Snapshot all primitive coordinate fields before any mutation.
        _primRestores = _prims.Select(SnapshotRestore).ToList();

        // Capture pin positions at construction for exact Undo.
        int n = _pins.Count;
        _pinOldX = new double[n]; _pinOldY = new double[n];
        for (int i = 0; i < n; i++)
        {
            _pinOldX[i] = _pins[i].LocalX;
            _pinOldY[i] = _pins[i].LocalY;
        }
    }

    public void Execute()
    {
        foreach (var p in _prims) SymbolGeometry.RotateBy90About(p, _cx, _cy);
        // Rotate each pin about center from its current position — incremental so
        // 4× Execute chains 4 rotations (= identity), matching the prim semantics.
        // No per-step Snap: rounding each step accumulates drift; exact Undo is always available.
        for (int i = 0; i < _pins.Count; i++)
        {
            double x = _pins[i].LocalX, y = _pins[i].LocalY;
            // 90° CW about (_cx, _cy): x' = cx + cy − y,  y' = cy − cx + x
            _pins[i].LocalX = _cx + _cy - y;
            _pins[i].LocalY = _cy - _cx + x;
        }
        _symbol.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var restore in _primRestores) restore();
        for (int i = 0; i < _pins.Count; i++)
        {
            _pins[i].LocalX = _pinOldX[i];
            _pins[i].LocalY = _pinOldY[i];
        }
        _symbol.NotifyChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns the center ((minX+maxX)/2, (minY+maxY)/2) of the union bbox of prims and pins.
    // The center is rotation-invariant: rotating the selection about its own center leaves it fixed,
    // so each independent R press uses the same point and N×90° composes to identity.
    private static (double cx, double cy) ComputeCenter(
        List<SymbolPrimitive> prims, List<SymbolPin> pins)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Use BboxOf per-primitive, NOT ComputeBb: ComputeBb skips Text/Bitmap, which would collapse a
        // text-only selection's pivot to the origin and fling the text across the canvas on rotate.
        foreach (var prim in prims)
        {
            var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(prim);
            if (bx0 < minX) minX = bx0;
            if (by0 < minY) minY = by0;
            if (bx1 > maxX) maxX = bx1;
            if (by1 > maxY) maxY = by1;
        }

        foreach (var pin in pins)
        {
            if (pin.LocalX < minX) minX = pin.LocalX;
            if (pin.LocalX > maxX) maxX = pin.LocalX;
            if (pin.LocalY < minY) minY = pin.LocalY;
            if (pin.LocalY > maxY) maxY = pin.LocalY;
        }

        if (minX == double.MaxValue) { minX = 0; maxX = 0; }
        if (minY == double.MaxValue) { minY = 0; maxY = 0; }

        return ((minX + maxX) * 0.5, (minY + maxY) * 0.5);
    }

    // Returns a closure that restores all coordinate fields of prim to their current values.
    private static Action SnapshotRestore(SymbolPrimitive prim)
    {
        switch (prim)
        {
            case LinePrimitive l:
            {
                double x1 = l.X1, y1 = l.Y1, x2 = l.X2, y2 = l.Y2;
                return () => { l.X1 = x1; l.Y1 = y1; l.X2 = x2; l.Y2 = y2; };
            }
            case PolylinePrimitive pl:
            {
                var snap = pl.Points.Select(pt => (double[])pt.Clone()).ToArray();
                return () =>
                {
                    for (int i = 0; i < snap.Length && i < pl.Points.Count; i++)
                    { pl.Points[i][0] = snap[i][0]; pl.Points[i][1] = snap[i][1]; }
                };
            }
            case RectPrimitive r:
            {
                double cx = r.Cx, cy = r.Cy, w = r.W, h = r.H;
                return () => { r.Cx = cx; r.Cy = cy; r.W = w; r.H = h; };
            }
            case RoundedRectPrimitive rr:
            {
                double cx = rr.Cx, cy = rr.Cy, w = rr.W, h = rr.H;
                return () => { rr.Cx = cx; rr.Cy = cy; rr.W = w; rr.H = h; };
            }
            case CirclePrimitive c:
            {
                double cx = c.Cx, cy = c.Cy, r = c.R;
                return () => { c.Cx = cx; c.Cy = cy; c.R = r; };
            }
            case EllipsePrimitive e:
            {
                double cx = e.Cx, cy = e.Cy, rx = e.Rx, ry = e.Ry;
                return () => { e.Cx = cx; e.Cy = cy; e.Rx = rx; e.Ry = ry; };
            }
            case ArcPrimitive a:
            {
                double cx = a.Cx, cy = a.Cy, r = a.R, sd = a.StartDeg;
                return () => { a.Cx = cx; a.Cy = cy; a.R = r; a.StartDeg = sd; };
            }
            case PolygonPrimitive pg:
            {
                var snap = pg.Points.Select(pt => (double[])pt.Clone()).ToArray();
                return () =>
                {
                    for (int i = 0; i < snap.Length && i < pg.Points.Count; i++)
                    { pg.Points[i][0] = snap[i][0]; pg.Points[i][1] = snap[i][1]; }
                };
            }
            case QuadCurvePrimitive qc:
            {
                double p0x = qc.P0X, p0y = qc.P0Y, ctrlx = qc.CtrlX, ctrly = qc.CtrlY,
                       p2x = qc.P2X, p2y = qc.P2Y;
                return () =>
                {
                    qc.P0X = p0x; qc.P0Y = p0y;
                    qc.CtrlX = ctrlx; qc.CtrlY = ctrly;
                    qc.P2X = p2x; qc.P2Y = p2y;
                };
            }
            case CubicCurvePrimitive cc:
            {
                double p0x = cc.P0X, p0y = cc.P0Y, c1x = cc.C1X, c1y = cc.C1Y,
                       c2x = cc.C2X, c2y = cc.C2Y, p3x = cc.P3X, p3y = cc.P3Y;
                return () =>
                {
                    cc.P0X = p0x; cc.P0Y = p0y;
                    cc.C1X = c1x; cc.C1Y = c1y;
                    cc.C2X = c2x; cc.C2Y = c2y;
                    cc.P3X = p3x; cc.P3Y = p3y;
                };
            }
            case SinePrimitive s:
            {
                double cx = s.Cx, cy = s.Cy;
                var axis = s.Axis;
                return () => { s.Cx = cx; s.Cy = cy; s.Axis = axis; };
            }
            case ExponentialTaperPrimitive et:
            {
                double cx = et.Cx, cy = et.Cy;
                var axis = et.Axis;
                return () => { et.Cx = cx; et.Cy = cy; et.Axis = axis; };
            }
            case TextPrimitive t:
            {
                double anx = t.AnchorX, any = t.AnchorY;
                var rot = t.Rotation;
                return () => { t.AnchorX = anx; t.AnchorY = any; t.Rotation = rot; };
            }
            case BitmapPrimitive bm:
            {
                double x = bm.X, y = bm.Y;
                return () => { bm.X = x; bm.Y = y; };
            }
            default:
                return static () => { };
        }
    }
}
