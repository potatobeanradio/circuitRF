// brief-em3d-28 R-em3d28-2 — the scene from the problem, through the one tessellation (R-em3d5-1).
//
// Everything is tessellated by Em3dTessellation, so the viewer's surfaces are the section renderer's,
// the CSXCAD writer's and the size estimate's: three consumers already agree on where a wire's surface
// is, and the viewer does not make a fourth answer.
//
// COLOURS (R-em3d28-2c). A conductor is painted with its drawing layer's colour from the technology,
// resolved by Em3dSectionRenderer.ObjectColours — the function brief 5's pictures use, so a via is the
// same colour in the 2D section, the layout editor and here. Dielectrics, air and the air-box faces
// take the fixed palette below, one set for the light variant and one for the dark, which is how the
// 2D editors follow the application theme. Ports take the theme's pin colour, as in the section view.
//
// THE PALETTE, documented because it is a user-visible choice: dielectrics cycle through six hues in
// problem order (green, blue, amber, violet, teal, rose) at 35 % opacity, so a stack of two substrates
// is two colours; air is a pale blue at 8 %; an air-box face is grey for PEC, blue for absorbing,
// orange for PMC and violet for symmetry.

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Theming;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>Builds a <see cref="Scene3DModel"/> from an <see cref="Em3dProblem"/>.</summary>
public static class Scene3DBuilder
{
    /// <summary>Opacity of a dielectric solid.</summary>
    public const byte DielectricAlpha = 90;
    /// <summary>Opacity of the air.</summary>
    public const byte AirAlpha = 20;
    /// <summary>Opacity of an air-box face.</summary>
    public const byte FaceAlpha = 40;
    /// <summary>Opacity of a port's sheet.</summary>
    public const byte PortAlpha = 170;
    /// <summary>A port's arrow is this fraction of the port's longer side.</summary>
    public const float ArrowFraction = 0.8f;
    /// <summary>Segments around a coaxial port's annulus.</summary>
    public const int AnnulusSegments = 32;

    private static long _tessellations;

    /// <summary>How many solids and sheets have been tessellated in this process — gate 5's counter
    /// (a hover must not move it).</summary>
    public static long Tessellations => Interlocked.Read(ref _tessellations);

    private static readonly (byte R, byte G, byte B)[] DielectricLight =
        [(92, 158, 92), (89, 140, 191), (191, 153, 77), (153, 115, 179), (77, 166, 166), (179, 102, 102)];
    private static readonly (byte R, byte G, byte B)[] DielectricDark =
        [(120, 190, 120), (120, 170, 225), (225, 185, 105), (185, 150, 215), (105, 200, 200), (215, 135, 135)];

    /// <summary>The scene for <paramref name="problem"/>. <paramref name="origins"/> and
    /// <paramref name="tech"/> colour the conductors; either may be absent.</summary>
    public static Scene3DModel Build(Em3dProblem problem, long generation,
                                     IReadOnlyDictionary<string, Em3dObjectOrigin>? origins = null,
                                     Technology? tech = null, ColorTheme? theme = null,
                                     ColorVariant variant = ColorVariant.Light,
                                     IReadOnlyList<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        origins ??= new Dictionary<string, Em3dObjectOrigin>();
        theme ??= ColorTheme.BuiltIn;
        bool dark = variant == ColorVariant.Dark;

        var box = problem.Boundary;
        var origin = ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);
        Vector3 L(Point3 p) => new((float)(p.X - origin.Item1), (float)(p.Y - origin.Item2), (float)(p.Z - origin.Item3));

        var conductorColours = Em3dSectionRenderer.ObjectColours(problem, origins, tech, theme, variant);
        var pin = theme.Resolve(ColorRole.LayoutPCellPin, variant);
        var ink = dark ? (R: (byte)205, G: (byte)210, B: (byte)215) : (R: (byte)60, G: (byte)64, B: (byte)70);
        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();
        var materials = problem.Materials.Select((m, i) => (m, i)).ToDictionary(t => t.m.Name, t => t, StringComparer.Ordinal);

        var b = new Accumulator(L);

        // ── solids ───────────────────────────────────────────────────────────────────────────
        string? outermost = OutermostDielectric(problem);
        foreach (var s in problem.Solids)
        {
            var kind = KindOf(s, origins);
            uint rgba;
            bool translucent = false;
            switch (s.Role)
            {
                case Em3dRole.Air:
                    rgba = dark ? Scene3DVertex.Pack(150, 190, 255, AirAlpha) : Scene3DVertex.Pack(140, 185, 240, AirAlpha);
                    translucent = true;
                    break;
                case Em3dRole.Dielectric:
                {
                    var pal = dark ? DielectricDark : DielectricLight;
                    var c = pal[Math.Max(0, dielectrics.IndexOf(s.Material)) % pal.Length];
                    rgba = Scene3DVertex.Pack(c.R, c.G, c.B, DielectricAlpha);
                    translucent = true;
                    break;
                }
                default:
                {
                    var c = conductorColours.TryGetValue(s.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
                    rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255);
                    break;
                }
            }
            Interlocked.Increment(ref _tessellations);
            var mesh = Em3dTessellation.Of(s);
            var (m, slot) = materials.TryGetValue(s.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            b.Object(new Scene3DObject
            {
                Id = 0, Name = s.Name, Kind = kind, Material = s.Material, MaterialValues = m, MaterialSlot = slot,
                Rgba = rgba, Translucent = translucent,
                InitiallyVisible = s.Role != Em3dRole.Air && s.Name != outermost,
            }, mesh);
        }

        // ── sheets ───────────────────────────────────────────────────────────────────────────
        foreach (var sh in problem.Sheets)
        {
            var c = conductorColours.TryGetValue(sh.Name, out var sk) ? sk : new SkiaSharp.SKColor(150, 150, 155);
            Interlocked.Increment(ref _tessellations);
            var (m, slot) = materials.TryGetValue(sh.Material, out var mt) ? (mt.m, mt.i) : ((Em3dMaterial?)null, -1);
            b.Object(new Scene3DObject
            {
                Id = 0, Name = sh.Name, Kind = Scene3DKind.Sheet, Material = sh.Material, MaterialValues = m,
                MaterialSlot = slot, Rgba = Scene3DVertex.Pack(c.Red, c.Green, c.Blue, 255),
            }, Em3dTessellation.OfSheet(sh));
        }

        // ── ports: a named sheet and a direction arrow ───────────────────────────────────────
        foreach (var p in problem.Ports)
        {
            var obj = new Scene3DObject
            {
                Id = 0, Name = p.Name, Kind = Scene3DKind.Port, PortNumber = p.Number,
                Rgba = Scene3DVertex.Pack(pin.R, pin.G, pin.B, PortAlpha), Translucent = true,
            };
            var (verts, tris) = PortSheet(p);
            uint line = Scene3DVertex.Pack(pin.R, pin.G, pin.B, 255);
            b.Object(obj, new Em3dTriangleMesh(verts, tris), PortArrow(p).Select(q => (q, line)));
        }

        // ── the air box: its six faces (hidden until asked for) and its twelve edges ─────────
        foreach (var (face, kind, corners) in Faces(box))
        {
            var (r, g, bl) = kind switch
            {
                Em3dBoundaryKind.Pec       => ((byte)150, (byte)150, (byte)158),
                Em3dBoundaryKind.Absorbing => ((byte)80, (byte)130, (byte)235),
                Em3dBoundaryKind.Pmc       => ((byte)235, (byte)140, (byte)50),
                _                          => ((byte)160, (byte)80, (byte)210),
            };
            b.Object(new Scene3DObject
            {
                Id = 0, Name = Em3dAirBox.FaceName(face), Kind = Scene3DKind.Boundary, Boundary = kind,
                Rgba = Scene3DVertex.Pack(r, g, bl, FaceAlpha), Translucent = true, InitiallyVisible = false,
            }, new Em3dTriangleMesh(corners, [new Em3dTriangle(0, 1, 2, ""), new Em3dTriangle(0, 2, 3, "")]));
        }
        uint edge = Scene3DVertex.Pack(ink.R, ink.G, ink.B, 255);
        b.Object(new Scene3DObject { Id = 0, Name = "airbox", Kind = Scene3DKind.Boundary, Rgba = edge },
                 null, BoxEdges(box).Select(q => (q, edge)));

        return b.Finish(generation, origin, problem, notes);
    }

    private static Scene3DKind KindOf(Em3dSolid s, IReadOnlyDictionary<string, Em3dObjectOrigin> origins)
    {
        if (origins.TryGetValue(s.Name, out var o))
            return o.Kind switch
            {
                Em3dObjectKind.Dielectric => Scene3DKind.Dielectric,
                Em3dObjectKind.Air        => Scene3DKind.Air,
                Em3dObjectKind.Body       => Scene3DKind.Body,
                Em3dObjectKind.Via        => s.Role == Em3dRole.Air ? Scene3DKind.Air : Scene3DKind.Via,
                Em3dObjectKind.Wire       => Scene3DKind.Wire,
                _                         => Scene3DKind.Conductor,
            };
        return s.Role switch
        {
            Em3dRole.Air        => Scene3DKind.Air,
            Em3dRole.Dielectric => Scene3DKind.Dielectric,
            _                   => s.Primitive is Em3dSweep or Em3dSphere or Em3dTruncatedSphere ? Scene3DKind.Wire : Scene3DKind.Conductor,
        };
    }

    /// <summary>The dielectric that encloses the most volume — the substrate a user looks through.
    /// Ties go to the first in problem order.</summary>
    public static string? OutermostDielectric(Em3dProblem problem)
    {
        string? best = null;
        double vol = -1;
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Dielectric))
        {
            double v = Em3dSizeEstimate.Volume(s.Primitive);
            if (v > vol) { vol = v; best = s.Name; }
        }
        return best;
    }

    // ── geometry of ports and the box ────────────────────────────────────────────────────────

    private static (List<Point3>, List<Em3dTriangle>) PortSheet(Em3dPort p)
    {
        var (a, c) = (p.Min, p.Max);
        var verts = new List<Point3>();
        var tris = new List<Em3dTriangle>();
        if (p.Annulus is { } an)
        {
            double cx = (a.X + c.X) / 2, cy = (a.Y + c.Y) / 2, z = a.Z;
            for (int k = 0; k < AnnulusSegments; k++)
            {
                double t = 2 * Math.PI * k / AnnulusSegments;
                verts.Add(new Point3(cx + an.InnerRadiusM * Math.Cos(t), cy + an.InnerRadiusM * Math.Sin(t), z));
                verts.Add(new Point3(cx + an.OuterRadiusM * Math.Cos(t), cy + an.OuterRadiusM * Math.Sin(t), z));
            }
            for (int k = 0; k < AnnulusSegments; k++)
            {
                int i0 = 2 * k, o0 = 2 * k + 1, i1 = 2 * ((k + 1) % AnnulusSegments), o1 = i1 + 1;
                tris.Add(new Em3dTriangle(i0, o0, o1, p.Name));
                tris.Add(new Em3dTriangle(i0, o1, i1, p.Name));
            }
            return (verts, tris);
        }
        if (a.X == c.X)      verts.AddRange([new(a.X, a.Y, a.Z), new(a.X, c.Y, a.Z), new(a.X, c.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else if (a.Y == c.Y) verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, a.Y, c.Z), new(a.X, a.Y, c.Z)]);
        else                 verts.AddRange([new(a.X, a.Y, a.Z), new(c.X, a.Y, a.Z), new(c.X, c.Y, a.Z), new(a.X, c.Y, a.Z)]);
        tris.Add(new Em3dTriangle(0, 1, 2, p.Name));
        tris.Add(new Em3dTriangle(0, 2, 3, p.Name));
        return (verts, tris);
    }

    /// <summary>The arrow's segments, as pairs of points: shaft then two barbs. A wave port's arrow is
    /// its voltage path; a lumped port's runs along its direction through the sheet's centre.</summary>
    private static List<Point3> PortArrow(Em3dPort p)
    {
        Point3 from, to;
        if (p.Kind == Em3dPortKind.Wave && p.VoltagePath is { } vp) (from, to) = (vp.From, vp.To);
        else
        {
            var c = new Point3((p.Min.X + p.Max.X) / 2, (p.Min.Y + p.Max.Y) / 2, (p.Min.Z + p.Max.Z) / 2);
            double side = Math.Max(p.Max.X - p.Min.X, Math.Max(p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z));
            if (p.Annulus is { } an) side = 2 * an.OuterRadiusM;
            double h = side * ArrowFraction / 2;
            var d = p.Direction;
            from = new Point3(c.X - d.X * h, c.Y - d.Y * h, c.Z - d.Z * h);
            to = new Point3(c.X + d.X * h, c.Y + d.Y * h, c.Z + d.Z * h);
        }
        var dir = new Vector3((float)(to.X - from.X), (float)(to.Y - from.Y), (float)(to.Z - from.Z));
        float len = dir.Length();
        if (len <= 0) return [];
        dir /= len;
        var side3 = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        side3 = Vector3.Normalize(side3);
        double head = len * 0.25;
        Point3 Barb(float s) => new(to.X - dir.X * head + side3.X * head * 0.5 * s,
                                    to.Y - dir.Y * head + side3.Y * head * 0.5 * s,
                                    to.Z - dir.Z * head + side3.Z * head * 0.5 * s);
        return [from, to, to, Barb(1), to, Barb(-1)];
    }

    private static IEnumerable<(string Face, Em3dBoundaryKind Kind, Point3[] Corners)> Faces(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        yield return ("xmin", b.Faces.XMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, lo.Y, hi.Z), new(lo.X, hi.Y, hi.Z), new(lo.X, hi.Y, lo.Z)]);
        yield return ("xmax", b.Faces.XMax, [new(hi.X, lo.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, lo.Y, hi.Z)]);
        yield return ("ymin", b.Faces.YMin, [new(lo.X, lo.Y, lo.Z), new(hi.X, lo.Y, lo.Z), new(hi.X, lo.Y, hi.Z), new(lo.X, lo.Y, hi.Z)]);
        yield return ("ymax", b.Faces.YMax, [new(lo.X, hi.Y, lo.Z), new(lo.X, hi.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(hi.X, hi.Y, lo.Z)]);
        yield return ("zmin", b.Faces.ZMin, [new(lo.X, lo.Y, lo.Z), new(lo.X, hi.Y, lo.Z), new(hi.X, hi.Y, lo.Z), new(hi.X, lo.Y, lo.Z)]);
        yield return ("zmax", b.Faces.ZMax, [new(lo.X, lo.Y, hi.Z), new(hi.X, lo.Y, hi.Z), new(hi.X, hi.Y, hi.Z), new(lo.X, hi.Y, hi.Z)]);
    }

    private static List<Point3> BoxEdges(Em3dAirBox b)
    {
        var (lo, hi) = (b.Min, b.Max);
        Point3 C(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        var e = new List<Point3>(24);
        for (int k = 0; k < 8; k++)
            for (int bit = 1; bit <= 4; bit <<= 1)
                if ((k & bit) == 0) { e.Add(C(k)); e.Add(C(k | bit)); }
        return e;
    }

    // ── accumulation ─────────────────────────────────────────────────────────────────────────

    private sealed class Accumulator(Func<Point3, Vector3> local)
    {
        private readonly List<Scene3DObject> _objects = [];
        private readonly List<Scene3DVertex> _verts = [];
        private readonly List<uint[]> _objIndices = [];
        private readonly List<Scene3DVertex> _lines = [];
        private readonly List<Scene3DLineBatch> _lineBatches = [];

        public void Object(Scene3DObject o, Em3dTriangleMesh? mesh, IEnumerable<(Point3 P, uint Rgba)>? lines = null)
        {
            uint id = (uint)(_objects.Count + 1);
            var obj = new Scene3DObject
            {
                Id = id, Name = o.Name, Kind = o.Kind, Material = o.Material, MaterialValues = o.MaterialValues,
                MaterialSlot = o.MaterialSlot, Rgba = o.Rgba, Translucent = o.Translucent,
                InitiallyVisible = o.InitiallyVisible, PortNumber = o.PortNumber, Boundary = o.Boundary,
            };
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            uint[] idx = [];
            if (mesh is not null)
            {
                int first = _verts.Count;
                foreach (var p in mesh.Vertices)
                {
                    var q = local(p);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    _verts.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, o.Rgba));
                }
                idx = new uint[mesh.Triangles.Count * 3];
                int w = 0;
                foreach (var t in mesh.Triangles)
                {
                    idx[w++] = (uint)(first + t.A);
                    idx[w++] = (uint)(first + t.B);
                    idx[w++] = (uint)(first + t.C);
                }
            }
            if (lines is not null)
            {
                int first = _lines.Count;
                foreach (var (p, rgba) in lines)
                {
                    var q = local(p);
                    min = Vector3.Min(min, q); max = Vector3.Max(max, q);
                    _lines.Add(new Scene3DVertex(q.X, q.Y, q.Z, id, rgba));
                }
                if (_lines.Count > first) _lineBatches.Add(new Scene3DLineBatch(id, first, _lines.Count - first));
            }
            if (min.X > max.X) { min = max = Vector3.Zero; }
            obj.Min = min; obj.Max = max;
            _objects.Add(obj);
            _objIndices.Add(idx);
        }

        public Scene3DModel Finish(long generation, (double, double, double) origin, Em3dProblem problem, IReadOnlyList<string>? notes)
        {
            var indices = new uint[_objIndices.Sum(i => i.Length)];
            var batches = new List<Scene3DBatch>();
            int at = 0;
            foreach (bool translucent in new[] { false, true })
                for (int k = 0; k < _objects.Count; k++)
                {
                    var o = _objects[k];
                    if (o.Translucent != translucent || _objIndices[k].Length == 0) continue;
                    _objIndices[k].CopyTo(indices, at);
                    batches.Add(new Scene3DBatch(o.Id, o.MaterialSlot, at, _objIndices[k].Length, translucent));
                    at += _objIndices[k].Length;
                }

            var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
            var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
            foreach (var o in _objects)
            {
                bmin = Vector3.Min(bmin, o.Min); bmax = Vector3.Max(bmax, o.Max);
                if (o.Kind is Scene3DKind.Air or Scene3DKind.Boundary) continue;
                if (o.Kind == Scene3DKind.Dielectric && !o.InitiallyVisible) continue;
                cmin = Vector3.Min(cmin, o.Min); cmax = Vector3.Max(cmax, o.Max);
            }
            if (bmin.X > bmax.X) { bmin = new Vector3(-1e-3f); bmax = new Vector3(1e-3f); }
            if (cmin.X > cmax.X) { cmin = bmin; cmax = bmax; }

            return new Scene3DModel
            {
                Generation = generation, Origin = origin,
                Vertices = [.. _verts], Indices = indices, LineVertices = [.. _lines],
                Objects = [.. _objects], Batches = [.. batches], LineBatches = [.. _lineBatches],
                BoundsMin = bmin, BoundsMax = bmax, ContentMin = cmin, ContentMax = cmax,
                Problem = problem, Notes = notes ?? [],
            };
        }
    }
}
