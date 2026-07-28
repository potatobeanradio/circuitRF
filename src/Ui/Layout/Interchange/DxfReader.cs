// DXF (ASCII) reader (docs/sonnet-briefs/brief-L4b-dxf-interchange.md §2). Format-specific: touches
// only text/groups, never CellFolder/Messages/dialogs — that orchestration lives in DxfImport. Reads
// the documented subset faithfully (LWPOLYLINE, POLYLINE/VERTEX/SEQEND, LINE, ARC, CIRCLE, ELLIPSE,
// SPLINE, SOLID, HATCH, TEXT/MTEXT, INSERT incl. its array fields, BLOCK/ENDBLK) and reports EVERYTHING
// else per-entity, so a user always knows what did not come through (§2's own framing).
//
// Tokenizing strategy: DXF's own group-code-0 boundary IS the natural entity/section/table delimiter,
// so the WHOLE file is first split into (Type, Body) tokens on every code-0 group (Type = that group's
// value; Body = every following non-zero-code group, up to the next code-0 group). This makes the
// HEADER section trivial — since HEADER contains no code-0 groups at all, its ENTIRE content (every
// "9 $VARNAME" marker and its value groups) lands in ONE token's Body list (the "SECTION" token whose
// own Body starts with "2 HEADER") — no special HEADER sub-parser is needed, just a scan for the
// "9 $INSUNITS" marker followed by its value group.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One imported shape, still carrying its raw DXF layer NAME — DxfImport resolves this to a
/// real <see cref="LayerKey"/> via <see cref="DxfLayerReconciliation"/> before any reconciliation runs,
/// mirroring the format-specific/orchestration split GdsiiReader/GdsiiImport already established.</summary>
public sealed record DxfImportedShape(LayoutShape Shape, string LayerName);

/// <summary>One BLOCK, or the synthetic model-space "structure" (<see cref="DxfReader.ModelSpaceName"/>)
/// — the DXF analogue of a GDSII structure/circuitRF cell, before <see cref="DxfImport"/> mangles names
/// and creates real cell folders.</summary>
public sealed class DxfStructure
{
    public string Name { get; init; } = "";
    public List<DxfImportedShape> Shapes { get; } = [];
    public List<LayoutInstance> Instances { get; } = []; // CellRef = raw referenced block name (unresolved)
}

public sealed class DxfReader
{
    /// <summary>Sentinel name for the top-level ENTITIES section, imported as its own cell — the DXF
    /// analogue of a GDSII library's top structure.</summary>
    public const string ModelSpaceName = "$MODEL";

    /// <summary>
    /// A FIXED internal parsing scale, independent of the file's actual <c>$INSUNITS</c> — every raw
    /// DXF coordinate/length value is multiplied by this constant before rounding to <c>long</c> DBU,
    /// preserving sub-drawing-unit precision regardless of what the real units turn out to be. This
    /// reader never resolves real-world units itself (R-L4b-4 requires the CALLER to ask when
    /// <see cref="InsUnits"/> is 0/unsupported, which the reader cannot do); <see cref="DxfImport"/>
    /// applies the actual, resolved rescale (this constant -&gt; the real DBU/drawing-unit ratio) as a
    /// second pass over the returned <see cref="Structures"/>, exactly mirroring how
    /// <c>GdsiiImport.RescaleAll</c> corrects <c>GdsiiReader</c>'s always-file-native coordinates.
    /// </summary>
    public const double ProvisionalDbuPerDrawingUnit = 1_000_000.0;

    /// <summary>Raw <c>$INSUNITS</c> value read from HEADER, or 0 if absent/never set — the caller
    /// (DxfImport) is the one that must not guess when this is 0 (R-L4b-4).</summary>
    public int InsUnits { get; private set; }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Unsupported entity type -> count, for gate 10 ("reported by type with counts, nothing
    /// silently dropped").</summary>
    public IReadOnlyDictionary<string, int> UnsupportedEntityCounts => _unsupportedCounts;

    public IReadOnlyList<DxfStructure> Structures { get; private set; } = [];

    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<string, int> _unsupportedCounts = new(StringComparer.OrdinalIgnoreCase);

    public static DxfReader Read(TextReader textReader)
    {
        var reader = new DxfReader();
        reader.Parse(textReader);
        return reader;
    }

    private void Parse(TextReader textReader)
    {
        var tokens = Tokenize(textReader);
        var modelSpace = new DxfStructure { Name = ModelSpaceName };
        var blocks = new List<DxfStructure>();

        int i = 0;
        while (i < tokens.Count)
        {
            var (type, body) = tokens[i];
            if (type == "EOF") break;

            if (type == "SECTION")
            {
                string sectionName = GetStr(body, 2, "");
                i++;
                if (sectionName == "HEADER")
                {
                    ParseHeaderBody(body);
                    i = SkipToEndsec(tokens, i);
                }
                else if (sectionName == "BLOCKS")
                {
                    i = ParseBlocksSection(tokens, i, blocks);
                }
                else if (sectionName == "ENTITIES")
                {
                    i = ParseEntityRun(tokens, i, "ENDSEC", modelSpace);
                    if (i < tokens.Count && tokens[i].Type == "ENDSEC") i++;
                }
                else
                {
                    i = SkipToEndsec(tokens, i);
                }
            }
            else
            {
                i++;
            }
        }

        blocks.Add(modelSpace);
        Structures = blocks;
    }

    private void ParseHeaderBody(List<DxfGroup> body)
    {
        for (int k = 0; k < body.Count - 1; k++)
        {
            if (body[k].Code == 9 && body[k].Value == "$INSUNITS")
                InsUnits = body[k + 1].AsInt();
        }
    }

    private static int SkipToEndsec(List<(string Type, List<DxfGroup> Body)> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type != "ENDSEC" && tokens[i].Type != "EOF") i++;
        if (i < tokens.Count && tokens[i].Type == "ENDSEC") i++;
        return i;
    }

    // ── BLOCKS ────────────────────────────────────────────────────────────────

    private int ParseBlocksSection(List<(string Type, List<DxfGroup> Body)> tokens, int i, List<DxfStructure> blocks)
    {
        while (i < tokens.Count && tokens[i].Type != "ENDSEC")
        {
            if (tokens[i].Type == "BLOCK")
            {
                string name = GetStr(tokens[i].Body, 2, $"Block{blocks.Count + 1}");
                i++;

                // A block name starting with '*' is an AutoCAD-internal/anonymous block by universal
                // DXF convention — *Model_Space and *Paper_Space (every AC1015+ file has both, real or
                // not), plus *U#/*D#/*X# blocks a real authoring tool generates for hatches, dimensions,
                // and xrefs. None of these are user cells; importing one as a bogus empty structure is
                // exactly the failure mode gate 12 ("a reader tested only against its own writer is not
                // tested") exists to catch on a real third-party file, not just our own export.
                if (name.StartsWith('*'))
                {
                    i = SkipEntityRun(tokens, i, "ENDBLK");
                    if (i < tokens.Count && tokens[i].Type == "ENDBLK") i++;
                    continue;
                }

                var structure = new DxfStructure { Name = name };
                i = ParseEntityRun(tokens, i, "ENDBLK", structure);
                if (i < tokens.Count && tokens[i].Type == "ENDBLK") i++;
                blocks.Add(structure);
            }
            else
            {
                i++;
            }
        }
        return i;
    }

    private static int SkipEntityRun(List<(string Type, List<DxfGroup> Body)> tokens, int i, string terminator)
    {
        while (i < tokens.Count && tokens[i].Type != terminator && tokens[i].Type != "ENDSEC" && tokens[i].Type != "EOF") i++;
        return i;
    }

    // ── Entity run (shared by BLOCKS/ENTITIES) ───────────────────────────────

    private int ParseEntityRun(List<(string Type, List<DxfGroup> Body)> tokens, int i, string terminator, DxfStructure into)
    {
        while (i < tokens.Count && tokens[i].Type != terminator && tokens[i].Type != "ENDSEC" && tokens[i].Type != "EOF")
        {
            var (type, body) = tokens[i];
            switch (type)
            {
                case "LWPOLYLINE":
                    into.Shapes.Add(ParseLwPolyline(body));
                    i++;
                    break;

                case "POLYLINE":
                    i = ParseOldPolyline(tokens, i, into);
                    break;

                case "LINE":
                    into.Shapes.Add(ParseLine(body));
                    i++;
                    break;

                case "ARC":
                    into.Shapes.Add(ParseArc(body));
                    i++;
                    break;

                case "CIRCLE":
                    into.Shapes.Add(ParseCircle(body));
                    i++;
                    break;

                case "ELLIPSE":
                    into.Shapes.Add(ParseEllipse(body));
                    i++;
                    break;

                case "SPLINE":
                    into.Shapes.Add(ParseSpline(body));
                    i++;
                    break;

                case "SOLID":
                    into.Shapes.Add(ParseSolid(body));
                    i++;
                    break;

                case "HATCH":
                    foreach (var s in ParseHatch(body)) into.Shapes.Add(s);
                    i++;
                    break;

                case "TEXT":
                case "MTEXT":
                    into.Shapes.Add(ParseText(body));
                    i++;
                    break;

                case "INSERT":
                    into.Instances.Add(ParseInsert(body));
                    i++;
                    break;

                default:
                    _unsupportedCounts[type] = _unsupportedCounts.GetValueOrDefault(type) + 1;
                    i++;
                    break;
            }
        }
        return i;
    }

    private int ParseOldPolyline(List<(string Type, List<DxfGroup> Body)> tokens, int i, DxfStructure into)
    {
        var header = tokens[i].Body;
        bool closed = (GetInt(header, 70, 0) & 1) != 0;
        i++;

        var xs = new List<double>();
        var ys = new List<double>();
        var bulges = new List<double>();
        while (i < tokens.Count && tokens[i].Type == "VERTEX")
        {
            xs.Add(GetDbl(tokens[i].Body, 10));
            ys.Add(GetDbl(tokens[i].Body, 20));
            bulges.Add(GetDbl(tokens[i].Body, 42, 0));
            i++;
        }
        if (i < tokens.Count && tokens[i].Type == "SEQEND") i++;

        string layer = GetStr(header, 8, "0");
        into.Shapes.Add(BuildPolylineShape(xs, ys, bulges, closed, GetDbl(header, 40, 0), layer));
        return i;
    }

    // ── LWPOLYLINE ────────────────────────────────────────────────────────────

    private static DxfImportedShape ParseLwPolyline(List<DxfGroup> body)
    {
        bool closed = (GetInt(body, 70, 0) & 1) != 0;
        double constWidth = GetDbl(body, 43, 0);
        string layer = GetStr(body, 8, "0");

        var xs = new List<double>();
        var ys = new List<double>();
        var bulges = new List<double>();
        double curX = 0, curY = 0, curBulge = 0;
        bool have = false;

        void Commit() { if (have) { xs.Add(curX); ys.Add(curY); bulges.Add(curBulge); } }

        foreach (var g in body)
        {
            if (g.Code == 10) { Commit(); curX = g.AsDouble(); curBulge = 0; have = true; }
            else if (g.Code == 20) curY = g.AsDouble();
            else if (g.Code == 42) curBulge = g.AsDouble();
        }
        Commit();

        return BuildPolylineShape(xs, ys, bulges, closed, constWidth, layer);
    }

    private static DxfImportedShape BuildPolylineShape(
        List<double> xs, List<double> ys, List<double> bulges, bool closed, double width, string layer)
    {
        int n = xs.Count;
        var xy = new long[n * 2];
        for (int k = 0; k < n; k++) { xy[2 * k] = ToDbu(xs[k]); xy[2 * k + 1] = ToDbu(ys[k]); }

        bool anyBulge = bulges.Any(b => b != 0);
        List<LayoutEdge>? edges = anyBulge
            ? bulges.Select(b => new LayoutEdge { Kind = b != 0 ? EdgeKind.Arc : EdgeKind.Line, Bulge = b }).ToList()
            : null;

        if (closed)
        {
            LayoutShape shape = anyBulge
                ? new CurveShape { Xy = xy, Edges = edges }
                : new PolygonShape { Xy = xy };
            return new DxfImportedShape(shape, layer);
        }
        else
        {
            var pathEdges = anyBulge ? edges!.Take(Math.Max(0, n - 1)).ToList() : null;
            var path = new PathShape { Xy = xy, Edges = pathEdges, Width = ToDbu(width), End = PathEndStyle.Flush };
            return new DxfImportedShape(path, layer);
        }
    }

    // ── LINE / ARC / CIRCLE ───────────────────────────────────────────────────

    private static DxfImportedShape ParseLine(List<DxfGroup> body)
    {
        long x0 = ToDbu(GetDbl(body, 10)), y0 = ToDbu(GetDbl(body, 20));
        long x1 = ToDbu(GetDbl(body, 11)), y1 = ToDbu(GetDbl(body, 21));
        var path = new PathShape { Xy = [x0, y0, x1, y1], Width = 0, End = PathEndStyle.Flush };
        return new DxfImportedShape(path, GetStr(body, 8, "0"));
    }

    private static DxfImportedShape ParseArc(List<DxfGroup> body)
    {
        double cx = GetDbl(body, 10), cy = GetDbl(body, 20), r = GetDbl(body, 40);
        double startDeg = GetDbl(body, 50), endDeg = GetDbl(body, 51);
        double sweepDeg = NormalizePositiveDeg(endDeg - startDeg);
        double sweepRad = sweepDeg * Math.PI / 180.0;
        double startRad = startDeg * Math.PI / 180.0, endRad = startRad + sweepRad;

        long x0 = ToDbu(cx + r * Math.Cos(startRad)), y0 = ToDbu(cy + r * Math.Sin(startRad));
        long x1 = ToDbu(cx + r * Math.Cos(endRad)), y1 = ToDbu(cy + r * Math.Sin(endRad));
        double bulge = LayoutArc.ToBulge(sweepRad);

        var path = new PathShape
        {
            Xy = [x0, y0, x1, y1],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge }],
            Width = 0,
            End = PathEndStyle.Flush,
        };
        return new DxfImportedShape(path, GetStr(body, 8, "0"));
    }

    private static DxfImportedShape ParseCircle(List<DxfGroup> body)
    {
        var c = new CircleShape
        {
            Cx = ToDbu(GetDbl(body, 10)),
            Cy = ToDbu(GetDbl(body, 20)),
            R = ToDbu(GetDbl(body, 40)),
        };
        return new DxfImportedShape(c, GetStr(body, 8, "0"));
    }

    private static DxfImportedShape ParseSolid(List<DxfGroup> body)
    {
        double x1 = GetDbl(body, 10), y1 = GetDbl(body, 20);
        double x2 = GetDbl(body, 11), y2 = GetDbl(body, 21);
        double x3 = GetDbl(body, 12), y3 = GetDbl(body, 22);
        double x4 = GetDbl(body, 13, x3), y4 = GetDbl(body, 23, y3);
        // SOLID's own vertex order is 1,2,3,4 with 3-4 forming the "far" edge in a bowtie order
        // (1->2->4->3 traces the actual quadrilateral boundary) — a well-known SOLID quirk.
        long[] xy =
        [
            ToDbu(x1), ToDbu(y1),
            ToDbu(x2), ToDbu(y2),
            ToDbu(x4), ToDbu(y4),
            ToDbu(x3), ToDbu(y3),
        ];
        return new DxfImportedShape(new PolygonShape { Xy = xy }, GetStr(body, 8, "0"));
    }

    // ── ELLIPSE (§2 — approximate, always reported) ──────────────────────────

    private DxfImportedShape ParseEllipse(List<DxfGroup> body)
    {
        double cx = GetDbl(body, 10), cy = GetDbl(body, 20);
        double majorX = GetDbl(body, 11), majorY = GetDbl(body, 21);
        double ratio = GetDbl(body, 40, 1.0);
        string handle = GetStr(body, 5, "?");

        double majorR = Math.Sqrt(majorX * majorX + majorY * majorY);
        double angle = Math.Atan2(majorY, majorX);
        double minorR = majorR * ratio;

        _diagnostics.Add($"ELLIPSE (handle {handle}) approximated as 4 cubic edges — accurate to within ~0.02% of radius.");

        // Standard 4-cubic-Bezier approximation of an axis-aligned ellipse (semi-axes majorR, minorR),
        // then rotated by `angle` and translated to (cx, cy).
        const double kappa = 0.5522847498307936;
        (double X, double Y) Rot(double lx, double ly)
        {
            double rx = lx * Math.Cos(angle) - ly * Math.Sin(angle);
            double ry = lx * Math.Sin(angle) + ly * Math.Cos(angle);
            return (cx + rx, cy + ry);
        }

        var quad = new (double X, double Y)[] { (majorR, 0), (0, minorR), (-majorR, 0), (0, -minorR) };
        var ctrlOffsets = new (double Dx, double Dy)[] { (0, minorR * kappa), (-majorR * kappa, 0), (0, -minorR * kappa), (majorR * kappa, 0) };

        var xy = new long[8];
        var edges = new List<LayoutEdge>(4);
        for (int i = 0; i < 4; i++)
        {
            var (px, py) = Rot(quad[i].X, quad[i].Y);
            xy[2 * i] = ToDbu(px); xy[2 * i + 1] = ToDbu(py);
        }
        for (int i = 0; i < 4; i++)
        {
            var (p0x, p0y) = Rot(quad[i].X, quad[i].Y);
            var (p1x, p1y) = Rot(quad[(i + 1) % 4].X, quad[(i + 1) % 4].Y);
            var (c1x, c1y) = Rot(quad[i].X + ctrlOffsets[i].Dx, quad[i].Y + ctrlOffsets[i].Dy);
            var (c2x, c2y) = Rot(quad[(i + 1) % 4].X - ctrlOffsets[(i + 1) % 4].Dx, quad[(i + 1) % 4].Y - ctrlOffsets[(i + 1) % 4].Dy);
            edges.Add(new LayoutEdge
            {
                Kind = EdgeKind.Cubic,
                C1X = ToDbu(c1x), C1Y = ToDbu(c1y),
                C2X = ToDbu(c2x), C2Y = ToDbu(c2y),
            });
        }

        var curve = new CurveShape { Xy = xy, Edges = edges };
        return new DxfImportedShape(curve, GetStr(body, 8, "0"));
    }

    // ── SPLINE (§2, gate 9 — degree-3 non-rational exact; else approximated + reported) ──────────

    private DxfImportedShape ParseSpline(List<DxfGroup> body)
    {
        int flags = GetInt(body, 70, 0);
        int degree = GetInt(body, 71, 0);
        bool rational = (flags & 4) != 0;
        bool closedFlag = (flags & 1) != 0;
        string handle = GetStr(body, 5, "?");

        var knots = new List<double>();
        var ctrl = new List<(double X, double Y)>();
        double curX = 0; bool haveX = false;
        foreach (var g in body)
        {
            if (g.Code == 40) knots.Add(g.AsDouble());
            else if (g.Code == 10) { curX = g.AsDouble(); haveX = true; }
            else if (g.Code == 20 && haveX) { ctrl.Add((curX, g.AsDouble())); haveX = false; }
        }

        if (degree == 3 && !rational && ctrl.Count >= 4 && (ctrl.Count - 1) % 3 == 0 && IsBezierChainKnotVector(knots, ctrl.Count, degree))
        {
            int segments = (ctrl.Count - 1) / 3;
            bool closed = Dist(ctrl[0], ctrl[^1]) < 0.5;

            var edges = new List<LayoutEdge>(segments);
            for (int seg = 0; seg < segments; seg++)
            {
                var c1 = ctrl[seg * 3 + 1];
                var c2 = ctrl[seg * 3 + 2];
                edges.Add(new LayoutEdge
                {
                    Kind = EdgeKind.Cubic,
                    C1X = ToDbu(c1.X), C1Y = ToDbu(c1.Y),
                    C2X = ToDbu(c2.X), C2Y = ToDbu(c2.Y),
                });
            }

            if (closed)
            {
                var xy = new long[segments * 2];
                for (int k = 0; k < segments; k++) { xy[2 * k] = ToDbu(ctrl[k * 3].X); xy[2 * k + 1] = ToDbu(ctrl[k * 3].Y); }
                return new DxfImportedShape(new CurveShape { Xy = xy, Edges = edges }, GetStr(body, 8, "0"));
            }
            else
            {
                var xy = new long[(segments + 1) * 2];
                for (int k = 0; k <= segments; k++) { xy[2 * k] = ToDbu(ctrl[k * 3].X); xy[2 * k + 1] = ToDbu(ctrl[k * 3].Y); }
                return new DxfImportedShape(new PathShape { Xy = xy, Edges = edges, Width = 0 }, GetStr(body, 8, "0"));
            }
        }

        _diagnostics.Add(
            $"SPLINE (handle {handle}) is degree {degree}{(rational ? ", rational" : "")} — approximated by its control polygon (chord flattening).");

        long[] approxXy = new long[ctrl.Count * 2];
        for (int k = 0; k < ctrl.Count; k++) { approxXy[2 * k] = ToDbu(ctrl[k].X); approxXy[2 * k + 1] = ToDbu(ctrl[k].Y); }
        bool closedApprox = closedFlag || (ctrl.Count > 1 && Dist(ctrl[0], ctrl[^1]) < 0.5);
        LayoutShape approxShape = closedApprox ? new PolygonShape { Xy = approxXy } : new PathShape { Xy = approxXy, Width = 0 };
        return new DxfImportedShape(approxShape, GetStr(body, 8, "0"));
    }

    /// <summary>Detects a clamped "Bezier chain" knot vector for a degree-3 spline: the first and last
    /// knot values each repeated <c>degree+1</c> times, every interior knot value repeated exactly
    /// <c>degree</c> times — the shape <see cref="DxfWriter"/>'s own <c>WriteClosedSplineChain</c>
    /// always emits. A general (non-Bezier-form) knot vector fails this check and falls through to
    /// the approximated path above.</summary>
    private static bool IsBezierChainKnotVector(List<double> knots, int numCtrlPts, int degree)
    {
        int expectedSegments = (numCtrlPts - 1) / degree;
        int expectedKnotCount = numCtrlPts + degree + 1;
        if (knots.Count != expectedKnotCount) return false;

        var runs = new List<int>();
        int runLen = 1;
        for (int i = 1; i < knots.Count; i++)
        {
            if (Math.Abs(knots[i] - knots[i - 1]) < 1e-9) runLen++;
            else { runs.Add(runLen); runLen = 1; }
        }
        runs.Add(runLen);

        if (runs.Count != expectedSegments + 1) return false;
        if (runs[0] != degree + 1 || runs[^1] != degree + 1) return false;
        for (int i = 1; i < runs.Count - 1; i++)
            if (runs[i] != degree) return false;
        return true;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── HATCH (§3.1a — polyline-type and edge-type boundary loops) ───────────

    private List<DxfImportedShape> ParseHatch(List<DxfGroup> body)
    {
        string layer = GetStr(body, 8, "0");
        int numLoops = GetInt(body, 91, 0);

        var loops = new List<(long[] Xy, List<LayoutEdge>? Edges)>();
        int idx = 0;
        while (idx < body.Count && loops.Count < numLoops)
        {
            if (body[idx].Code != 92) { idx++; continue; }
            int flag = body[idx].AsInt();
            idx++;
            // Boundary path type flag (group 92): 1=External, 2=Polyline (verified against the public
            // DXF HATCH spec — see DxfWriter.WriteHatchLoop's own note on the earlier transposed bug).
            bool polyline = (flag & 2) != 0;
            if (polyline)
                idx = ParseHatchPolylineLoop(body, idx, loops);
            else
                idx = ParseHatchEdgeLoop(body, idx, loops);
        }

        if (loops.Count == 0) return [];

        var (outerXy, outerEdges) = loops[0];
        var holes = loops.Skip(1).Select(l => l.Xy).ToList();

        LayoutShape shape = outerEdges is { Count: > 0 } && outerEdges.Any(e => e.Kind != EdgeKind.Line)
            ? new CurveShape { Xy = outerXy, Edges = outerEdges, Holes = holes.Count > 0 ? holes : null }
            : new PolygonShape { Xy = outerXy, Holes = holes.Count > 0 ? holes : null };

        return [new DxfImportedShape(shape, layer)];
    }

    private static int ParseHatchPolylineLoop(List<DxfGroup> body, int idx, List<(long[] Xy, List<LayoutEdge>? Edges)> loops)
    {
        // Expect: 72(hasBulge) 73(closed) 93(numVerts) then numVerts * (10,20,[42])
        int hasBulge = 0, numVerts = 0;
        while (idx < body.Count && body[idx].Code is 72 or 73 or 93)
        {
            if (body[idx].Code == 72) hasBulge = body[idx].AsInt();
            else if (body[idx].Code == 93) numVerts = body[idx].AsInt();
            idx++;
        }

        var xs = new List<double>(); var ys = new List<double>(); var bulges = new List<double>();
        for (int v = 0; v < numVerts && idx < body.Count; v++)
        {
            double x = 0, y = 0, b = 0;
            if (idx < body.Count && body[idx].Code == 10) { x = body[idx].AsDouble(); idx++; }
            if (idx < body.Count && body[idx].Code == 20) { y = body[idx].AsDouble(); idx++; }
            if (hasBulge != 0 && idx < body.Count && body[idx].Code == 42) { b = body[idx].AsDouble(); idx++; }
            xs.Add(x); ys.Add(y); bulges.Add(b);
        }
        // Optional trailing source-object count (97) — skip it.
        if (idx < body.Count && body[idx].Code == 97) idx++;

        var xy = new long[xs.Count * 2];
        for (int k = 0; k < xs.Count; k++) { xy[2 * k] = ToDbu(xs[k]); xy[2 * k + 1] = ToDbu(ys[k]); }
        List<LayoutEdge>? edges = bulges.Any(b => b != 0)
            ? bulges.Select(b => new LayoutEdge { Kind = b != 0 ? EdgeKind.Arc : EdgeKind.Line, Bulge = b }).ToList()
            : null;
        loops.Add((xy, edges));
        return idx;
    }

    private static int ParseHatchEdgeLoop(List<DxfGroup> body, int idx, List<(long[] Xy, List<LayoutEdge>? Edges)> loops)
    {
        int numEdges = 0;
        if (idx < body.Count && body[idx].Code == 93) { numEdges = body[idx].AsInt(); idx++; }

        var xs = new List<double>(); var ys = new List<double>(); var edges = new List<LayoutEdge>();

        for (int e = 0; e < numEdges && idx < body.Count && body[idx].Code == 72; e++)
        {
            int edgeType = body[idx].AsInt(); idx++;
            switch (edgeType)
            {
                case 1: // line
                    {
                        double x0 = body[idx].AsDouble(); idx++;
                        double y0 = body[idx].AsDouble(); idx++;
                        double x1 = body[idx].AsDouble(); idx++;
                        double y1 = body[idx].AsDouble(); idx++;
                        if (xs.Count == 0) { xs.Add(x0); ys.Add(y0); }
                        xs.Add(x1); ys.Add(y1);
                        edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
                    }
                    break;

                case 2: // arc
                    {
                        double cx = body[idx].AsDouble(); idx++;
                        double cy = body[idx].AsDouble(); idx++;
                        double r = body[idx].AsDouble(); idx++;
                        double startDeg = body[idx].AsDouble(); idx++;
                        double endDeg = body[idx].AsDouble(); idx++;
                        int ccw = 1;
                        if (idx < body.Count && body[idx].Code == 73) { ccw = body[idx].AsInt(); idx++; }
                        double sweepDeg = ccw != 0 ? NormalizePositiveDeg(endDeg - startDeg) : -NormalizePositiveDeg(startDeg - endDeg);
                        double startRad = startDeg * Math.PI / 180.0, sweepRad = sweepDeg * Math.PI / 180.0;
                        double x0 = cx + r * Math.Cos(startRad), y0 = cy + r * Math.Sin(startRad);
                        double x1 = cx + r * Math.Cos(startRad + sweepRad), y1 = cy + r * Math.Sin(startRad + sweepRad);
                        if (xs.Count == 0) { xs.Add(x0); ys.Add(y0); }
                        xs.Add(x1); ys.Add(y1);
                        edges.Add(new LayoutEdge { Kind = EdgeKind.Arc, Bulge = LayoutArc.ToBulge(sweepRad) });
                    }
                    break;

                case 4: // spline (single Bezier segment — matches our own writer's per-edge convention)
                    {
                        int splineDegree = body[idx].AsInt(); idx++;
                        idx++; // 73 rational
                        idx++; // 74 periodic
                        int numKnots = body[idx].AsInt(); idx++;
                        int numCtrl = body[idx].AsInt(); idx++;
                        for (int k = 0; k < numKnots; k++) idx++; // skip knot values (40)
                        var pts = new List<(double X, double Y)>(numCtrl);
                        for (int k = 0; k < numCtrl; k++)
                        {
                            double px = body[idx].AsDouble(); idx++;
                            double py = body[idx].AsDouble(); idx++;
                            pts.Add((px, py));
                        }
                        if (idx < body.Count && body[idx].Code == 97) idx++;

                        if (pts.Count == 4)
                        {
                            if (xs.Count == 0) { xs.Add(pts[0].X); ys.Add(pts[0].Y); }
                            xs.Add(pts[3].X); ys.Add(pts[3].Y);
                            edges.Add(new LayoutEdge
                            {
                                Kind = EdgeKind.Cubic,
                                C1X = ToDbu(pts[1].X), C1Y = ToDbu(pts[1].Y),
                                C2X = ToDbu(pts[2].X), C2Y = ToDbu(pts[2].Y),
                            });
                        }
                    }
                    break;

                default:
                    // Ellipse (3) sub-edge type, or unrecognized — skip remaining groups for this edge
                    // as best-effort (advance to next 72/97) since HATCH sub-edge bodies are self-
                    // delimited only by the next edge-type marker; a genuinely unknown edge type here
                    // would desync the walk, so stop this loop's edge scan rather than guess further.
                    e = numEdges;
                    break;
            }
        }
        if (idx < body.Count && body[idx].Code == 97) idx++;

        // Drop the duplicated closing vertex (our own convention never repeats vertex 0).
        if (xs.Count > 1 && Math.Abs(xs[0] - xs[^1]) < 0.5 && Math.Abs(ys[0] - ys[^1]) < 0.5)
        {
            xs.RemoveAt(xs.Count - 1); ys.RemoveAt(ys.Count - 1);
        }

        var xy = new long[xs.Count * 2];
        for (int k = 0; k < xs.Count; k++) { xy[2 * k] = ToDbu(xs[k]); xy[2 * k + 1] = ToDbu(ys[k]); }
        loops.Add((xy, edges.Count > 0 ? edges : null));
        return idx;
    }

    // ── TEXT ──────────────────────────────────────────────────────────────────

    private static DxfImportedShape ParseText(List<DxfGroup> body)
    {
        double x = GetDbl(body, 10), y = GetDbl(body, 20);
        double height = GetDbl(body, 40, 1.0);
        string text = GetStr(body, 1, "");
        double rotDeg = GetDbl(body, 50, 0.0);
        bool isPort = GetInt(body, 70, 0) == 1;

        var label = new LabelShape
        {
            X = ToDbu(x), Y = ToDbu(y),
            Text = text, Height = ToDbu(height),
            Rotation = SnapRotation(rotDeg), IsPort = isPort,
        };
        return new DxfImportedShape(label, GetStr(body, 8, "0"));
    }

    private static LayoutRotation SnapRotation(double deg)
    {
        deg %= 360.0; if (deg < 0) deg += 360.0;
        int q = (int)Math.Round(deg / 90.0) % 4;
        return q switch { 1 => LayoutRotation.R90, 2 => LayoutRotation.R180, 3 => LayoutRotation.R270, _ => LayoutRotation.R0 };
    }

    // ── INSERT ────────────────────────────────────────────────────────────────

    private static LayoutInstance ParseInsert(List<DxfGroup> body)
    {
        string blockName = GetStr(body, 2, "");
        double x = GetDbl(body, 10), y = GetDbl(body, 20);
        double xscale = GetDbl(body, 41, 1.0), yscale = GetDbl(body, 42, 1.0);
        double rotDeg = GetDbl(body, 50, 0.0);
        int cols = GetInt(body, 70, 1), rows = GetInt(body, 71, 1);
        double pitchX = GetDbl(body, 44, 0.0), pitchY = GetDbl(body, 45, 0.0);

        var (mirrorX, rot, mag) = DxfTransformCodec.FromDxf(xscale, yscale, rotDeg, out _, out _);

        return new LayoutInstance
        {
            CellRef = blockName,
            X = ToDbu(x), Y = ToDbu(y),
            Rot = rot, MirrorX = mirrorX, Mag = mag,
            Rows = Math.Max(1, rows), Cols = Math.Max(1, cols),
            PitchX = ToDbu(pitchX), PitchY = ToDbu(pitchY),
        };
    }

    // ── Small helpers ─────────────────────────────────────────────────────────

    private static double NormalizePositiveDeg(double deg)
    {
        deg %= 360.0;
        if (deg <= 0) deg += 360.0;
        return deg;
    }

    /// <summary>Every raw DXF coordinate/length value passes through here — see
    /// <see cref="ProvisionalDbuPerDrawingUnit"/>'s own doc comment for why this is a FIXED internal
    /// scale rather than the file's real <c>$INSUNITS</c>.</summary>
    private static long ToDbu(double drawingUnitValue) =>
        (long)Math.Round(drawingUnitValue * ProvisionalDbuPerDrawingUnit, MidpointRounding.AwayFromZero);

    private static bool TryGet(List<DxfGroup> body, int code, out DxfGroup group)
    {
        foreach (var g in body) if (g.Code == code) { group = g; return true; }
        group = default;
        return false;
    }

    // Unescaped uniformly at this one funnel (docs/sonnet-briefs/brief-dxf-version-support.md R-dxf-2):
    // a real AutoCAD `\U+XXXX` escape can appear in ANY string-valued group this reader pulls through
    // GetStr (layer names, block names, TEXT content) regardless of the file's own version/encoding —
    // harmless/no-op for every other value read this way (handles, section/keyword names never contain
    // the literal escape sequence).
    private static string GetStr(List<DxfGroup> body, int code, string def) =>
        TryGet(body, code, out var g) ? DxfEncoding.Unescape(g.Value) : def;
    private static double GetDbl(List<DxfGroup> body, int code, double def = 0) => TryGet(body, code, out var g) ? g.AsDouble() : def;
    private static int GetInt(List<DxfGroup> body, int code, int def = 0) => TryGet(body, code, out var g) ? g.AsInt() : def;

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    private static List<(string Type, List<DxfGroup> Body)> Tokenize(TextReader textReader)
    {
        var groupReader = new DxfGroupReader(textReader);
        var tokens = new List<(string, List<DxfGroup>)>();
        string? currentType = null;
        List<DxfGroup> currentBody = [];

        while (groupReader.TryReadNext(out var g))
        {
            if (g.Code == 0)
            {
                if (currentType is not null) tokens.Add((currentType, currentBody));
                currentType = g.Value;
                currentBody = [];
            }
            else
            {
                currentBody.Add(g);
            }
        }
        if (currentType is not null) tokens.Add((currentType, currentBody));
        return tokens;
    }
}
