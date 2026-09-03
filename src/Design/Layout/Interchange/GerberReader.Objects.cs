// Apertures, operations and the polarity decision (brief-L4e §4-§6). The other half of GerberReader.
//
// R-L4e-9's mapping is load-bearing for the round trip and must not be "simplified": a circle flash
// comes back a CircleShape and a rectangle flash a RectShape, because that is exactly what L4c's
// writer emitted for a CircleShape/ViaShape and a RectShape. Turning a circle flash into a 64-sided
// polygon renders identically and quietly destroys the round trip, which is why L4h's gate asserts the
// mapping on the TYPES.

using Clipper2Lib;

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class GerberReader
{
    // ── %AD aperture definition (R-L4e-7) ─────────────────────────────────────

    private void ParseApertureDefine(string rest)
    {
        // ADD<code><template>[,<mod>X<mod>…] — the D-code may be ZERO-PADDED: %ADD010C,0.001*% is
        // aperture 10 (R-L4e-5).
        if (rest.Length < 2 || rest[0] != 'D') { Count(_unknown, "%AD" + rest); return; }
        int i = 1;
        while (i < rest.Length && char.IsAsciiDigit(rest[i])) i++;
        if (i == 1 || !int.TryParse(rest[1..i], NumberStyles.None, CultureInfo.InvariantCulture, out int code))
        { Count(_unknown, "%AD" + rest); return; }

        string body = rest[i..];
        int comma = body.IndexOf(',');
        string template = comma < 0 ? body : body[..comma];
        string[] mods = comma < 0 ? [] : body[(comma + 1)..].Split('X', 'x');

        var def = new ApertureDef { Code = code, Function = ApertureFunction() };
        switch (template)
        {
            case "C": BuildCircleAperture(def, mods); break;
            case "R": BuildRectAperture(def, mods, rounded: false); break;
            case "O": BuildRectAperture(def, mods, rounded: true); break;
            case "P": BuildPolygonAperture(def, mods); break;
            default:  BuildMacroAperture(def, template, mods); break;
        }
        _apertures[code] = def;
    }

    private string? ApertureFunction() =>
        _apertureAttributes.TryGetValue(".AperFunction", out string? v) ? v : null;

    private void BuildCircleAperture(ApertureDef def, string[] mods)
    {
        long d = ModDbu(mods, 0);
        if (HoleOf(mods, 1) is { } hole)
        {
            // R-L4e-7: a CircleShape cannot carry a hole, so a holed circular aperture is the one case
            // where the shape-identity mapping cannot apply. Counted, because it is a real degradation.
            Count(_skipped, "circular aperture with a hole (imported as a polygon with a hole)");
            SetRings(def, new Paths64 { GerberPrimitives.Circle(0, 0, d / 2.0) }, hole);
            return;
        }
        def.IsPlainCircle = true;
        def.CircleDiameterDbu = d;
        def.Paths = [GerberPrimitives.Circle(0, 0, d / 2.0)];
    }

    private void BuildRectAperture(ApertureDef def, string[] mods, bool rounded)
    {
        long w = ModDbu(mods, 0), h = ModDbu(mods, 1);
        var hole = HoleOf(mods, 2);
        if (rounded)
        {
            // An obround is not expressible as a RectShape and never was — R-L4e-9 maps it to a polygon.
            SetRings(def, new Paths64 { GerberPrimitives.Obround(0, 0, w, h) }, hole);
            return;
        }
        if (hole is not null)
        {
            SetRings(def, new Paths64 { GerberPrimitives.Rect(0, 0, w, h) }, hole);
            return;
        }
        def.IsPlainRect = true;
        def.RectWidthDbu = w;
        def.RectHeightDbu = h;
        def.Paths = [GerberPrimitives.Rect(0, 0, w, h)];
    }

    private void BuildPolygonAperture(ApertureDef def, string[] mods)
    {
        long diameter = ModDbu(mods, 0);
        int vertices = (int)Math.Round(ModValue(mods, 1));
        double rotation = ModValue(mods, 2);
        SetRings(def, new Paths64 { GerberPrimitives.RegularPolygon(0, 0, diameter, vertices, rotation) },
            HoleOf(mods, 3));
    }

    private void BuildMacroAperture(ApertureDef def, string name, string[] mods)
    {
        if (!_macros.TryGetValue(name, out var macro))
        {
            Count(_unknown, $"aperture template \"{name}\"");
            return;
        }
        var args = new double[mods.Length];
        for (int i = 0; i < mods.Length; i++) args[i] = ModValue(mods, i);
        SetRings(def, macro.Instantiate(args, Format, s => Count(_skipped, s)), hole: null);
    }

    /// <summary>The optional hole modifier: a further <c>X&lt;d&gt;</c> (round) or
    /// <c>X&lt;x&gt;X&lt;y&gt;</c> (rectangular), which makes the flash a shape WITH A HOLE IN IT.</summary>
    private Paths64? HoleOf(string[] mods, int firstIndex)
    {
        if (mods.Length <= firstIndex) return null;
        long a = ModDbu(mods, firstIndex);
        if (a <= 0) return null;
        if (mods.Length <= firstIndex + 1) return [GerberPrimitives.Circle(0, 0, a / 2.0)];
        long b = ModDbu(mods, firstIndex + 1);
        return b <= 0 ? [GerberPrimitives.Circle(0, 0, a / 2.0)] : [GerberPrimitives.Rect(0, 0, a, b)];
    }

    /// <summary>Resolves an aperture's outer/hole ring structure ONCE, at definition time, so a flash
    /// is a translate rather than a Clipper call — a file with ten thousand pads flashes ten thousand
    /// times.</summary>
    private static void SetRings(ApertureDef def, Paths64 solid, Paths64? hole)
    {
        var paths = hole is null ? solid : Clipper.Difference(solid, hole, LayoutClipper.Rule);
        def.Paths = paths;

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, LayoutClipper.Rule);
        foreach (var shape in LayoutClipper.FromClipperTree(tree, default, null))
            if (shape is PolygonShape p && p.Xy.Length >= 6)
                def.Rings.Add((p.Xy, p.Holes));
    }

    private double ModValue(string[] mods, int index) =>
        index < mods.Length && double.TryParse(mods[index].Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    private long ModDbu(string[] mods, int index) =>
        index < mods.Length ? Format.DecimalToDbu(mods[index].Trim()) : 0;

    // ── %AM macro definition (R-L4e-8) ────────────────────────────────────────

    private void ParseMacro(string name, string[] segments)
    {
        if (name.Length == 0) { Count(_unknown, "%AM with no name"); return; }
        var blocks = new List<string>();
        for (int i = 1; i < segments.Length; i++)
        {
            string block = segments[i].Trim();
            if (block.Length > 0) blocks.Add(block);
        }
        _macros[name] = new GerberMacroDefinition(name, blocks);
    }

    // ── Word blocks (R-L4e-4/R-L4e-5) ─────────────────────────────────────────

    private void WordBlock(string raw)
    {
        string block = Strip(raw);
        if (block.Length == 0) return;                                   // a bare '*' (R-L4e-5)
        if (block.StartsWith("G04", StringComparison.Ordinal) ||
            block.StartsWith("G4", StringComparison.Ordinal)) return;    // a comment, legal anywhere

        long? rawX = null, rawY = null, rawI = null, rawJ = null;
        int? op = null;

        foreach (var (letter, value) in SplitLetterValues(block))
        {
            switch (letter)
            {
                case 'G': ApplyGCode(value); break;
                case 'D':
                {
                    long d = ParseIntOrDefault(value, -1);
                    if (d >= 10) SelectAperture((int)d);
                    else if (d is 1 or 2 or 3) op = (int)d;
                    else if (d >= 0) Count(_unknown, $"D{d:D2}");
                    break;
                }
                case 'X': rawX = Format.ParseCoordinateWord(value); break;
                case 'Y': rawY = Format.ParseCoordinateWord(value); break;
                case 'I': rawI = Format.ParseCoordinateWord(value); break;
                case 'J': rawJ = Format.ParseCoordinateWord(value); break;
                case 'M':
                {
                    long m = ParseIntOrDefault(value, -1);
                    if (m is not (0 or 1 or 2)) Count(_unknown, $"M{m:D2}");
                    break;
                }
                case 'N': break;                                          // deprecated sequence number
                default: Count(_unknown, $"word '{letter}'"); break;
            }
        }

        bool hasCoordinate = rawX is not null || rawY is not null;
        if (op is null && !hasCoordinate) return;                         // e.g. a bare "G01*" or "D10*"

        // R-L4e-4: an omitted operation code REPEATS the last one, and files exist in which the great
        // majority of blocks are exactly that.
        int operation = op ?? _lastOpCode;
        if (operation == 0)
        {
            Count(_unknown, "coordinate block before any D01/D02/D03");
            return;
        }
        _lastOpCode = operation;

        long tx = Resolve(rawX, _x, true);
        long ty = Resolve(rawY, _y, false);
        Operate(operation, tx, ty, rawI, rawJ);
    }

    private void ApplyGCode(string value)
    {
        switch (ParseIntOrDefault(value, -1))
        {
            case 1:  _mode = Interpolation.Linear; break;
            case 2:  _mode = Interpolation.ClockwiseArc; break;
            case 3:  _mode = Interpolation.CounterClockwiseArc; break;
            case 36: BeginRegion(); break;
            case 37: EndRegion(); break;
            case 54: break;   // R-L4e-5: the obsolete aperture-select prefix; the D-code still selects
            case 55: break;   // the obsolete flash prefix; the D03 still flashes
            case 70: _unit = GerberUnit.Inches; _format = null; break;
            case 71: _unit = GerberUnit.Millimetres; _format = null; break;
            case 74: _multiQuadrant = false; break;
            case 75: _multiQuadrant = true; break;
            case 90: _notation = GerberNotation.Absolute; _format = null; break;
            case 91: _notation = GerberNotation.Incremental; _format = null; break;
            case var g and >= 0: Count(_unknown, $"G{g:D2}"); break;
        }
    }

    /// <summary>R-L4e-3: incremental coordinates are legal, rare, and silently catastrophic if read as
    /// absolute — so they are supported here rather than assumed away.</summary>
    private long Resolve(long? raw, long current, bool isX)
    {
        if (raw is null) return current;
        long dbu = Format.ToDbu(raw.Value);
        if (Format.Notation == GerberNotation.Incremental) return current + dbu;
        return dbu + (isX ? _offsetX : _offsetY);
    }

    private void SelectAperture(int code)
    {
        FlushStroke();   // a stroke belongs to exactly one aperture
        if (_apertures.TryGetValue(code, out var def)) _aperture = def;
        else { _aperture = null; Count(_unknown, $"aperture D{code} used before it was defined"); }
    }

    // ── The three operations ──────────────────────────────────────────────────

    private void Operate(int op, long tx, long ty, long? rawI, long? rawJ)
    {
        switch (op)
        {
            case 1: Draw(tx, ty, rawI, rawJ); break;
            case 2: Move(tx, ty); break;
            case 3: Flash(tx, ty); break;
        }
        _x = tx;
        _y = ty;
    }

    private void Move(long tx, long ty)
    {
        if (_regionMode)
        {
            // R-L4e-12: a D02 inside a region starts a NEW contour. A D02 with no coordinates at all is
            // a move to the current point — a no-op that terminates the contour (R-L4e-5).
            CloseContour();
            return;
        }
        FlushStroke();
    }

    private void Draw(long tx, long ty, long? rawI, long? rawJ)
    {
        if (_regionMode)
        {
            _contour ??= NewContourAt(_x, _y);
            AppendSegment(_contour.Xy, _contour.Edges, tx, ty, rawI, rawJ);
            return;
        }

        if (_aperture is null) { Count(_unknown, "D01 with no aperture selected"); return; }

        if (_strokeXy.Count == 0)
        {
            _strokeXy.Add(_x); _strokeXy.Add(_y);
            _strokeAperture = _aperture;
            _strokeClear = _clearPolarity;
            _strokeNet = ObjectAttr(".N");
            _strokeFunction = _aperture.Function;
            _strokeComponent = ObjectAttr(".C");
            _strokePin = ObjectAttr(".P");
        }
        AppendSegment(_strokeXy, _strokeEdges, tx, ty, rawI, rawJ);

    }

    private void Flash(long tx, long ty)
    {
        FlushStroke();
        if (_regionMode) { Count(_skipped, "D03 flash inside a G36 region"); return; }
        if (_aperture is null) { Count(_unknown, "D03 with no aperture selected"); return; }

        var ap = _aperture;
        if (ap.IsPlainCircle)
        {
            // R-L4e-9 row 1 — the exact inverse of what L4c's writer emits for a CircleShape/ViaShape.
            Emit(new CircleShape { Cx = tx, Cy = ty, R = ap.CircleDiameterDbu / 2 }, ap.Function);
        }
        else if (ap.IsPlainRect)
        {
            // R-L4e-9 row 2. Halves are taken on the DBU integer, so an odd width lands one DBU low on
            // the far side rather than being carried through a double.
            long hw = ap.RectWidthDbu / 2, hh = ap.RectHeightDbu / 2;
            Emit(new RectShape { X1 = tx - hw, Y1 = ty - hh, X2 = tx - hw + ap.RectWidthDbu, Y2 = ty - hh + ap.RectHeightDbu },
                ap.Function);
        }
        else
        {
            foreach (var (outer, holes) in ap.Rings)
                Emit(new PolygonShape { Xy = Translated(outer, tx, ty), Holes = TranslatedHoles(holes, tx, ty) }, ap.Function);
        }
        _flashes++;
    }

    private static long[] Translated(long[] ring, long dx, long dy)
    {
        var copy = new long[ring.Length];
        for (int i = 0; i < ring.Length; i += 2) { copy[i] = ring[i] + dx; copy[i + 1] = ring[i + 1] + dy; }
        return copy;
    }

    private static List<long[]>? TranslatedHoles(List<long[]>? holes, long dx, long dy)
    {
        if (holes is not { Count: > 0 }) return null;
        var copy = new List<long[]>(holes.Count);
        foreach (var h in holes) copy.Add(Translated(h, dx, dy));
        return copy;
    }

    private string? ObjectAttr(string name) =>
        _objectAttributes.TryGetValue(name, out string? v) && v.Length > 0 ? v : null;

    private void Emit(LayoutShape shape, string? function)
    {
        shape.Net = ObjectAttr(".N");
        _objects.Add(new PaintedObject(shape, _clearPolarity, function, ObjectAttr(".C"), ObjectAttr(".P")));
    }

    // ── Strokes (R-L4e-10) ────────────────────────────────────────────────────

    private void FlushStroke()
    {
        if (_strokeXy.Count < 4) { ResetStroke(); return; }

        var ap = _strokeAperture!;
        long[] xy = [.. _strokeXy];
        var edges = _strokeEdges.Any(e => e.Kind == EdgeKind.Arc) ? new List<LayoutEdge>(_strokeEdges) : null;

        if (ap.IsPlainCircle)
        {
            // R-L4e-10: a round-capped PathShape of the aperture's own diameter — exactly what L4c's
            // writer emits for one, which is what closes that loop.
            var path = new PathShape { Xy = xy, Edges = edges, Width = ap.CircleDiameterDbu, End = PathEndStyle.Round };
            AddStroke(path);
        }
        else
        {
            // Stroking with a NON-circular aperture is deprecated but occurs. Sweeping the aperture
            // along the centreline (a Minkowski sum, through the shared LayoutClipper seam's own
            // Clipper2) is the only correct reading, and it is a real degradation — the path identity
            // and the width are both gone — so it is counted by name.
            Count(_skipped, "stroke with a non-circular aperture (swept into a region)");
            var centre = FlattenCentreline(xy, _strokeEdges);
            var swept = new Paths64();
            foreach (var pattern in ap.Paths)
                swept.AddRange(Clipper.MinkowskiSum(pattern, centre, false));
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Union, swept, new Paths64(), tree, LayoutClipper.Rule);
            foreach (var shape in LayoutClipper.FromClipperTree(tree, default, null)) AddStroke(shape);
        }

        _strokes++;
        ResetStroke();
    }

    private void AddStroke(LayoutShape shape)
    {
        shape.Net = _strokeNet;
        _objects.Add(new PaintedObject(shape, _strokeClear, _strokeFunction, _strokeComponent, _strokePin));
    }

    private void ResetStroke()
    {
        _strokeXy.Clear();
        _strokeEdges.Clear();
        _strokeAperture = null;
        _strokeNet = _strokeFunction = _strokeComponent = _strokePin = null;
    }

    /// <summary>Flattens an open bulge-carrying centreline to a plain polyline — needed only for the
    /// Minkowski sweep above. Everything that survives as a shape keeps its arcs (R-L4e-11).</summary>
    private static Path64 FlattenCentreline(long[] xy, List<LayoutEdge> edges)
    {
        var path = new Path64 { new(xy[0], xy[1]) };
        for (int i = 0; i + 3 < xy.Length; i += 2)
        {
            long x0 = xy[i], y0 = xy[i + 1], x1 = xy[i + 2], y1 = xy[i + 3];
            var edge = i / 2 < edges.Count ? edges[i / 2] : null;
            if (edge is { Kind: EdgeKind.Arc, Bulge: not 0 })
            {
                var arc = LayoutArc.FromBulge(x0, y0, x1, y1, edge.Bulge);
                int steps = Math.Max(2, GerberPrimitives.CircleSegments(arc.R) * (int)Math.Ceiling(Math.Abs(arc.Sweep) / (2 * Math.PI)));
                for (int s = 1; s < steps; s++)
                {
                    double a = arc.StartAngle + arc.Sweep * s / steps;
                    path.Add(GerberPrimitives.Point(arc.Cx + arc.R * Math.Cos(a), arc.Cy + arc.R * Math.Sin(a)));
                }
            }
            path.Add(new Point64(x1, y1));
        }
        return path;
    }
}
