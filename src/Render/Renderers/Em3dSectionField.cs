// brief-em3d-84 R-em3d84-3 — a field plot's slice laid under a section: FieldSection's triangles projected onto the section's
// (u, v), coloured through the plot's range and map at the phase asked for.
//
// PNG AND VECTOR DIFFER IN ONE WAY, AND ONLY ONE. A PNG gets SKCanvas.DrawVertices with a colour per VERTEX, blended across each
// triangle (Gouraud). Skia's SVG and PDF devices record drawVertices as nothing at all (src/Render/RESOLVED.md, the data
// display's surface), and SVG has no mesh gradient, so in SVG and PDF each triangle is a filled path of ONE colour: the field's
// value at its centroid.
//
// A PNG triangle whose corners span much of the range is SUBDIVIDED first (RasterSteps). Skia blends the corner COLOURS in RGB,
// the GPU blends the field's components and maps each pixel, and a map is not linear in RGB: on the committed cavity's coarse
// slice one triangle spans 1,115 to 1,972 V/m and its blended colour read back as 1,881 V/m where the field is 1,787. The
// sub-triangles carry the components interpolated exactly as the GPU interpolates them.
//
// THINNING (owner decision Q2), vector output only. Above a triangle limit, neighbouring triangles whose centroid colours fall
// in the same step of the map (ColourSteps — steps the eye cannot tell apart) are merged into one path: their union, traced
// from the edges only one of them has. Area is never dropped and the outline never moves — the pieces are made of the slice's
// own triangles, welded by the mesh edge each vertex lies on (FieldRecipe), not by position. PNG is never thinned. The report
// carries the triangle count before and after, so a thinned picture is never mistaken for the full one.

using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>One filled piece of a vector field layer: a triangle, or — thinned — several neighbouring ones of one colour step,
/// as closed loops (outer loops counter-clockwise, holes clockwise; filled non-zero).</summary>
/// <param name="Area">The piece's area from its loops, m² — the thinning gate's check that no area was dropped.</param>
public sealed record Em3dFieldPiece(SKColor Colour, IReadOnlyList<IReadOnlyList<Uv>> Loops, double Area, int Triangles);

/// <summary>A field slice as a section draws it: under the region outlines, over the page.</summary>
public sealed class Em3dFieldLayer
{
    /// <summary>The slice's triangles, three corners each, in the section's own plane, world metres.</summary>
    public required Uv[] Vertices { get; init; }

    /// <summary>A PNG's triangles as drawn — the slice's, subdivided where one spans much of the range — three corners each.</summary>
    public required Uv[] RasterVertices { get; init; }

    /// <summary>A PNG's colours, one per <see cref="RasterVertices"/> corner.</summary>
    public required SKColor[] VertexColours { get; init; }

    /// <summary>A vector page's filled pieces.</summary>
    public required IReadOnlyList<Em3dFieldPiece> Pieces { get; init; }

    /// <summary>True: drawn with DrawVertices (PNG); false: as <see cref="Pieces"/> (SVG, PDF).</summary>
    public bool Raster { get; init; }

    /// <summary>The legend's lines (FieldPlotResolver.LegendLines), or none for <c>--no-legend</c>.</summary>
    public IReadOnlyList<string> Legend { get; init; } = [];

    public required ColorMap3D Map { get; init; }
    public required FieldColorScale Scale { get; init; }

    /// <summary>brief-em3d-88 — a temperature's page: the wires, the thermal boundaries and the caption; null for an EM field.</summary>
    public Em3dThermalPage? Thermal { get; init; }

    /// <summary>brief-em3d-89 — a Surfaces or Faces plot: the surfaces seen along a direction, depth-buffered; null for a section.</summary>
    public Em3dSurfaceLayer? Surface { get; init; }

    /// <summary>brief-sar-6 R-sar6-5 — a SAR plot's peak markers, in the section's plane (<see cref="Em3dSectionField.PeakMarkers"/>).</summary>
    public IReadOnlyList<Em3dFieldMarker> Markers { get; init; } = [];

    /// <summary>The slice's triangles (a surface plot's field triangles).</summary>
    public int Triangles => Surface?.Triangles ?? Vertices.Length / 3;

    /// <summary>What the picture drew them as: the triangles themselves, or fewer pieces when thinned.</summary>
    public int TrianglesDrawn => Raster ? Triangles : Pieces.Count;
}

/// <summary>
/// brief-sar-6 R-sar6-5 — a peak marker in a section's plane: the averaging cube's outline (a point for local SAR), solid where
/// the plane cuts the cube, dashed where it is the cube seen along the section's axis, and its label.
/// </summary>
public sealed record Em3dFieldMarker(Uv Lo, Uv Hi, string Label, bool OnPlane, bool Point);

public static class Em3dSectionField
{
    /// <summary>
    /// brief-sar-6 R-sar6-5 — <paramref name="peak"/> in a section across axis <paramref name="axis"/> at <paramref name="at"/>
    /// (metres): the cube's square in the plane, solid when the plane passes through the cube or the picture is a
    /// <paramref name="projection"/> (which shows the whole line through it), dashed otherwise, the label saying how far off the
    /// plane it is.
    /// </summary>
    public static IReadOnlyList<Em3dFieldMarker> PeakMarkers(FieldSarPeak peak, int axis, double at, bool projection)
    {
        var r = peak.Record;
        Uv InPlane(double[] p) => axis switch { 0 => new Uv(p[1], p[2]), 1 => new Uv(p[0], p[2]), _ => new Uv(p[0], p[1]) };
        double[] lo = r.CubeLoM ?? r.CentreM, hi = r.CubeHiM ?? r.CentreM;
        bool on = projection || at >= lo[axis] - 1e-12 && at <= hi[axis] + 1e-12;
        string where = on ? "" : $", {"xyz"[axis]} = {(r.CentreM[axis] * 1e3).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} mm";
        return [new Em3dFieldMarker(InPlane(lo), InPlane(hi), peak.Label + where, on, r.CubeLoM is null)];
    }

    /// <summary>Each marker: its square (or cross) and its label over a halo.</summary>
    internal static void DrawMarkers(SKCanvas canvas, Em3dFieldLayer layer, Func<Uv, SKPoint> map, float fontSize)
    {
        if (layer.Markers.Count == 0) return;
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, Color = SKColors.Black.WithAlpha(0xB0) };
        using var font = new SKFont(SkiaFonts.PlexSemiBold, fontSize);
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var textHalo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = fontSize * 0.3f, Color = SKColors.Black.WithAlpha(0xC0),
                                           StrokeJoin = SKStrokeJoin.Round };
        using var dash = SKPathEffect.CreateDash([5f, 3f], 0);
        foreach (var m in layer.Markers)
        {
            SKPoint a = map(m.Lo), b = map(m.Hi);
            var rect = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            if (m.Point || rect.Width < 4 || rect.Height < 4) rect = new SKRect(rect.MidX - 5, rect.MidY - 5, rect.MidX + 5, rect.MidY + 5);
            ink.PathEffect = m.OnPlane ? null : dash;
            foreach (var p in new[] { halo, ink })
            {
                if (m.Point)
                {
                    canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, p);
                    canvas.DrawLine(rect.MidX, rect.Top, rect.MidX, rect.Bottom, p);
                }
                else canvas.DrawRect(rect, p);
            }
            canvas.DrawText(m.Label, rect.Right + fontSize * 0.4f, rect.Top + fontSize * 0.9f, SKTextAlign.Left, font, textHalo);
            canvas.DrawText(m.Label, rect.Right + fontSize * 0.4f, rect.Top + fontSize * 0.9f, SKTextAlign.Left, font, text);
        }
    }

    /// <summary>
    /// The triangle count above which a vector slice is thinned. Measured (brief-em3d-84): the committed cavity's mid-height z
    /// slice is 144 triangles; a 120,000-cell second-order connector run slices to 3,784 – 11,565 triangles (x, y, z through its
    /// middle) at ~28 bytes of SVG each — 119 – 330 kB, which needs no thinning. 50,000 is ~1.4 MB of field before the outline:
    /// where a picture stops being something a reviewer opens in a browser. Thinning buys little on an FEM slice — the mesh is
    /// graded to the field, so neighbours rarely share a colour step: 11,565 triangles merged to 9,147 pieces at 256 steps and to
    /// 8,908 at 64, about a fifth of the bytes either way.
    /// </summary>
    public const int VectorTriangleLimit = 50_000;

    /// <summary>The map steps two triangles must share to be merged: 8 bits, as the GPU's lookup texture resolves them.</summary>
    public const int ColourSteps = 256;

    /// <summary>A PNG triangle is subdivided until each piece spans at most this fraction of the range (1/RasterSteps), so an
    /// RGB blend of its corners stays within a step of the map's colour at every point.</summary>
    public const int RasterSteps = 32;

    /// <summary>The most a PNG triangle's edge is divided into.</summary>
    public const int MaxSubdivision = 16;

    /// <summary>
    /// <paramref name="cut"/> projected onto the section's plane and coloured at <paramref name="phase"/> (radians): per vertex
    /// for a <paramref name="raster"/> page, per triangle (its centroid) otherwise — merged when the slice has more than
    /// <paramref name="thinAbove"/> triangles (null: never).
    /// </summary>
    public static Em3dFieldLayer Build(FieldSectionCut cut, double phase, bool raster, int? thinAbove, IReadOnlyList<string> legend,
                                       Em3dThermalPage? thermal = null, IReadOnlyList<Em3dFieldMarker>? markers = null)
    {
        var s = cut.Slice;
        int n = s.VertexCount, ch = s.Channels;
        var uv = new Uv[n];
        for (int i = 0; i < n; i++)
        {
            double x = s.Xyz[3 * i] + cut.Origin.X, y = s.Xyz[3 * i + 1] + cut.Origin.Y, z = s.Xyz[3 * i + 2] + cut.Origin.Z;
            // brief-dfr34-3 — a section through two points: the distance along its line, and z
            uv[i] = cut.Line is { } line ? new Uv(line.Along(x, y), z)
                  : cut.Axis switch { 0 => new Uv(y, z), 1 => new Uv(x, z), _ => new Uv(x, y) };
        }
        SKColor Colour(double t) { var (r, g, b) = cut.Map.Sample((float)t); return new SKColor(r, g, b); }

        var rasterUv = new List<Uv>();
        var vertexColours = new List<SKColor>();
        if (raster)
        {
            // Underneath, every slice triangle at its corners' colours: where a subdivided triangle meets one that was not, the
            // two edges' pixel coverage differs at the T-junction and would leave single background pixels showing through.
            for (int i = 0; i < n; i++)
            {
                rasterUv.Add(uv[i]);
                vertexColours.Add(Colour(cut.Position(s.Values.AsSpan(i * ch, ch), phase)));
            }
            var t = new double[3];
            var c = new double[ch];
            for (int k = 0; k < n / 3; k++)
            {
                for (int v = 0; v < 3; v++) t[v] = cut.Position(s.Values.AsSpan((3 * k + v) * ch, ch), phase);
                int m = Math.Clamp((int)Math.Ceiling((t.Max() - t.Min()) * RasterSteps), 1, MaxSubdivision);
                // The lattice (i, j), i + j ≤ m, over the triangle: weights ((m − i − j), i, j) / m of its three corners.
                void Corner(int i, int j)
                {
                    double w1 = (double)i / m, w2 = (double)j / m, w0 = 1 - w1 - w2;
                    Uv a = uv[3 * k], b = uv[3 * k + 1], d = uv[3 * k + 2];
                    rasterUv.Add(new Uv(w0 * a.U + w1 * b.U + w2 * d.U, w0 * a.V + w1 * b.V + w2 * d.V));
                    for (int q = 0; q < ch; q++)
                        c[q] = w0 * s.Values[(3 * k) * ch + q] + w1 * s.Values[(3 * k + 1) * ch + q] + w2 * s.Values[(3 * k + 2) * ch + q];
                    vertexColours.Add(Colour(cut.Position(c, phase)));
                }
                for (int i = 0; i < m; i++)
                    for (int j = 0; i + j < m; j++)
                    {
                        Corner(i, j); Corner(i + 1, j); Corner(i, j + 1);
                        if (i + j < m - 1) { Corner(i + 1, j); Corner(i + 1, j + 1); Corner(i, j + 1); }
                    }
            }
        }

        IReadOnlyList<Em3dFieldPiece> pieces = [];
        if (!raster)
        {
            // The centroid's value: the channels' mean (the slice is linear across a triangle), then the quantity's reading.
            int tris = n / 3;
            var t = new double[tris];
            var mean = new double[ch];
            for (int k = 0; k < tris; k++)
            {
                for (int c = 0; c < ch; c++)
                    mean[c] = (s.Values[(3 * k) * ch + c] + s.Values[(3 * k + 1) * ch + c] + s.Values[(3 * k + 2) * ch + c]) / 3;
                t[k] = cut.Position(mean, phase);
            }
            pieces = thinAbove is { } limit && tris > limit && s.Recipe is { } recipe
                ? Thin(uv, recipe, t, cut.Map)
                : [.. Enumerable.Range(0, tris).Select(k => Triangle(uv, k, Colour(t[k])))];
        }
        return new Em3dFieldLayer
        {
            Vertices = uv, RasterVertices = [.. rasterUv], VertexColours = [.. vertexColours], Pieces = pieces, Raster = raster, Legend = legend, Map = cut.Map, Scale = cut.Scale,
            Thermal = thermal, Markers = markers ?? [],
        };
    }

    private static Em3dFieldPiece Triangle(Uv[] uv, int k, SKColor colour)
    {
        IReadOnlyList<Uv> loop = [uv[3 * k], uv[3 * k + 1], uv[3 * k + 2]];
        return new Em3dFieldPiece(colour, [loop], Math.Abs(SignedArea(loop)), 1);
    }

    /// <summary>
    /// Neighbouring triangles of one colour step merged into pieces. Two triangles are neighbours when they share a mesh edge's
    /// two cut points — identified by the mesh nodes each lies between (<paramref name="recipe"/>), so welding never depends
    /// on two rounding paths agreeing. Each piece is the union of its triangles: every triangle is turned counter-clockwise, an
    /// edge two of them share cancels, and the edges left are chained into the piece's loops.
    /// </summary>
    public static List<Em3dFieldPiece> Thin(Uv[] uv, FieldRecipe recipe, double[] position, ColorMap3D map)
    {
        int tris = uv.Length / 3;
        var key = new ulong[uv.Length];
        for (int i = 0; i < uv.Length; i++) key[i] = VertexKey(recipe, i);
        var step = new int[tris];
        for (int k = 0; k < tris; k++) step[k] = Math.Clamp((int)Math.Floor(position[k] * ColourSteps), 0, ColourSteps - 1);

        // Each triangle's corners, counter-clockwise in (u, v); a triangle of no area, or two corners at one point, is left out
        // — it covers nothing.
        var corners = new int[tris * 3];
        var live = new bool[tris];
        for (int k = 0; k < tris; k++)
        {
            int a = 3 * k, b = a + 1, c = a + 2;
            if (key[a] == key[b] || key[b] == key[c] || key[a] == key[c]) continue;
            double area = Cross(uv[a], uv[b], uv[c]);
            if (area == 0) continue;
            live[k] = true;
            (corners[a], corners[b], corners[c]) = area > 0 ? (a, b, c) : (a, c, b);
        }

        // Neighbours of one step, joined.
        var parent = new int[tris];
        for (int k = 0; k < tris; k++) parent[k] = k;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var owner = new Dictionary<(ulong, ulong), int>();
        for (int k = 0; k < tris; k++)
        {
            if (!live[k]) continue;
            for (int e = 0; e < 3; e++)
            {
                ulong p = key[corners[3 * k + e]], q = key[corners[3 * k + (e + 1) % 3]];
                var edge = p < q ? (p, q) : (q, p);
                if (owner.TryGetValue(edge, out int other))
                {
                    if (step[other] == step[k]) { int ra = Find(other), rb = Find(k); if (ra != rb) parent[rb] = ra; }
                }
                else owner[edge] = k;
            }
        }

        // Each group's boundary: its directed edges whose reverse it does not also hold. Kept in a list, in the order the
        // triangles gave them — never enumerated from a hash set, whose order moves from one process to the next and would make
        // two renders of one plot differ in their bytes.
        var position0 = new Dictionary<ulong, Uv>();
        var groups = new Dictionary<int, Boundary>();
        var order = new List<int>();
        for (int k = 0; k < tris; k++)
        {
            if (!live[k]) continue;
            int root = Find(k);
            if (!groups.TryGetValue(root, out var g)) { groups[root] = g = new Boundary(); order.Add(root); }
            g.Triangles++;
            for (int e = 0; e < 3; e++)
            {
                int i = corners[3 * k + e], j = corners[3 * k + (e + 1) % 3];
                position0.TryAdd(key[i], uv[i]);
                g.Add(key[i], key[j]);
            }
        }

        var pieces = new List<Em3dFieldPiece>(order.Count);
        foreach (int root in order)
        {
            var g = groups[root];
            var next = new Dictionary<ulong, List<ulong>>();
            foreach (var (from, to) in g.Edges())
            {
                if (!next.TryGetValue(from, out var list)) next[from] = list = [];
                list.Add(to);
            }
            var loops = new List<IReadOnlyList<Uv>>();
            double area = 0;
            foreach (var (from0, _) in g.Edges())
            {
                if (!next.TryGetValue(from0, out var out0) || out0.Count == 0) continue;
                var loop = new List<Uv>();
                ulong at = from0;
                while (next.TryGetValue(at, out var outs) && outs.Count > 0)
                {
                    loop.Add(position0[at]);
                    ulong to = outs[0];
                    outs.RemoveAt(0);
                    at = to;
                    if (at == from0) break;
                }
                if (loop.Count >= 3) { loops.Add(loop); area += SignedArea(loop); }
            }
            int count = g.Triangles;
            double t = (step[root] + 0.5) / ColourSteps;
            var (cr, cg, cb) = map.Sample((float)t);
            pieces.Add(new Em3dFieldPiece(new SKColor(cr, cg, cb), loops, area, count));
        }
        return pieces;
    }

    /// <summary>One piece's directed edges, in the order they were added; an edge whose reverse arrives cancels.</summary>
    private sealed class Boundary
    {
        private readonly List<(ulong From, ulong To)?> _list = [];
        private readonly Dictionary<(ulong, ulong), int> _at = [];
        public int Triangles;

        public void Add(ulong from, ulong to)
        {
            if (_at.Remove((to, from), out int i)) { _list[i] = null; return; }
            _at[(from, to)] = _list.Count;
            _list.Add((from, to));
        }

        public IEnumerable<(ulong From, ulong To)> Edges() => _list.OfType<(ulong, ulong)>();
    }

    /// <summary>Which point of the mesh a cut vertex is: a node (it lies on one) or the crossing of the edge between two.</summary>
    private static ulong VertexKey(FieldRecipe recipe, int i)
    {
        int a = recipe.A[i], b = recipe.B[i];
        double t = recipe.T[i];
        if (t <= 0) b = a;
        else if (t >= 1) a = b;
        return a <= b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
    }

    private static double Cross(Uv a, Uv b, Uv c) => (b.U - a.U) * (c.V - a.V) - (c.U - a.U) * (b.V - a.V);

    /// <summary>A closed loop's signed area (counter-clockwise positive), m².</summary>
    public static double SignedArea(IReadOnlyList<Uv> loop)
    {
        double s = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            s += p.U * q.V - q.U * p.V;
        }
        return s / 2;
    }

    /// <summary>
    /// brief-em3d-88 — a temperature page's wires, over the slice, through the layer's own map and range: on a PNG each chord's
    /// two ends carry their own colour and Skia blends between them along the wire; on a vector page each pair of chords is one
    /// filled path at its mean temperature (the slice's own rule, R-em3d84-3). The caller clips.
    /// </summary>
    internal static void DrawWires(SKCanvas canvas, Em3dFieldLayer layer, Func<Uv, SKPoint> map)
    {
        if (layer.Thermal is not { Wires.Count: > 0 } page) return;
        SKColor Colour(double t) { var (r, g, b) = layer.Map.Sample((float)layer.Scale.Position(t)); return new SKColor(r, g, b); }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var w in page.Wires)
        {
            int n = w.Left.Count;
            if (layer.Raster)
            {
                var pts = new List<SKPoint>();
                var cols = new List<SKColor>();
                for (int k = 0; k + 1 < n; k++)
                {
                    SKPoint l0 = map(w.Left[k]), r0 = map(w.Right[k]), l1 = map(w.Left[k + 1]), r1 = map(w.Right[k + 1]);
                    SKColor c0 = Colour(w.T[k]), c1 = Colour(w.T[k + 1]);
                    pts.AddRange([l0, r0, r1, l0, r1, l1]);
                    cols.AddRange([c0, c0, c1, c0, c1, c1]);
                }
                if (pts.Count == 0) continue;
                using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, [.. pts], [.. cols]);
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = false };
                canvas.DrawVertices(vertices, SKBlendMode.Modulate, white);
                continue;
            }
            for (int k = 0; k + 1 < n; k++)
            {
                using var path = new SKPath();
                path.AddPoly([map(w.Left[k]), map(w.Left[k + 1]), map(w.Right[k + 1]), map(w.Right[k])], close: true);
                fill.Color = Colour((w.T[k] + w.T[k + 1]) / 2);
                canvas.DrawPath(path, fill);
            }
        }
    }

    /// <summary>Draws <paramref name="layer"/> through <paramref name="map"/> (the page's metres → device). The caller clips.</summary>
    internal static void Draw(SKCanvas canvas, Em3dFieldLayer layer, Func<Uv, SKPoint> map)
    {
        if (layer.Raster)
        {
            if (layer.RasterVertices.Length == 0) return;
            var pts = new SKPoint[layer.RasterVertices.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = map(layer.RasterVertices[i]);
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, pts, layer.VertexColours);
            // Modulate over WHITE: with no shader the paint's colour multiplies every vertex colour (src/Render/RESOLVED.md).
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = false };
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, white);
            return;
        }
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var piece in layer.Pieces)
        {
            using var path = new SKPath { FillType = SKPathFillType.Winding };
            foreach (var loop in piece.Loops) path.AddPoly([.. loop.Select(map)], close: true);
            fill.Color = piece.Colour;
            canvas.DrawPath(path, fill);
        }
    }
}
