// brief-em3d-89 — what a Surfaces or Faces plot DRAWS, built once for both pictures: the 3D view's (on the GPU) and
// `circuitrf render --field`'s (a depth-buffered PNG, Em3dSurfaceField). Moved out of Viewer3DViewModel.Fields.cs and
// Viewer3DViewModel.Temperature.cs unchanged, as brief 84 moved the plot's resolution: a plot the window paints on a face is the
// plot the command line paints there, with the same triangles, the same nudges and the same range.
//
// What stayed behind is view state: the cancellation and the posting back to the UI thread, the hover samplers, the revalue of
// a sweep step onto parts already built, and the readouts.

using System.Numerics;
using CircuitRF.Design.Thermal;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A face painted with temperature: the object's name and its face index in the scene.</summary>
public readonly record struct TemperatureFace(string Object, int Face);

/// <summary>
/// A temperature's geometry for the chosen targets: each mesh surface with the nudge it is drawn with and the object it is on
/// (for the hot spot's name), the reflections that made a mirrored one (empty for a modelled one), the wires coloured from
/// their T(s), the range, the objects the field stands in for, and the note when nothing is painted.
/// </summary>
public sealed record FieldTemperatureBuild(
    IReadOnlyList<FieldSurface> Surfaces, IReadOnlyList<Vector3> Nudges, IReadOnlyList<string> Objects,
    IReadOnlyList<(int Axis, double At)[]> Reflections, IReadOnlyList<FieldWireSurface> Wires,
    FieldColorScale Scale, HashSet<string> Covered, string? Note);

/// <summary>
/// An EM field's geometry for the chosen targets: each surface with its nudge, the objects it stands in for, the faces painted
/// one by one and the sentences refusing the ones that could not be, and the hint for when nothing is drawn.
/// </summary>
public sealed record FieldEmBuild(
    IReadOnlyList<FieldSurface> Surfaces, IReadOnlyList<Vector3> Nudges, IReadOnlyList<string> Objects, HashSet<string> Covered,
    IReadOnlyList<(PaintedFieldFace Face, FieldFacePaint Paint)> Paints, IReadOnlyList<string> Refused, string? Hint);

public static class FieldSurfacePlot
{
    // ── temperature (brief-em3d-75 R-em3d75-4) ──────────────────────────────────────────────

    /// <summary>
    /// The temperature's surfaces for the chosen targets on <paramref name="vol"/>, the wires coloured from the table at
    /// <paramref name="step"/>, and the range — the true minimum and maximum of what is drawn, or (<paramref name="fixRange"/>)
    /// the union over every step. Each modelled surface is reflected across every combination of <paramref name="mirrors"/>
    /// (axis 0 x, 1 y, 2 z; world metres) with the same values, so the range and the peak are the modelled part's.
    /// </summary>
    public static FieldTemperatureBuild Temperature(
        FieldQuantity q, FieldStep vol, FieldArray array, Scene3DModel scene, ClipPlane3D clip, IReadOnlyList<FieldGroup> groups,
        IReadOnlyList<TemperatureFace> faces, bool allFaces, bool onClip, ThermalResultTable? table, int step,
        IReadOnlyList<FieldSolution> steps, bool fixRange, CancellationToken ct, IReadOnlyList<(int Axis, double AtM)>? mirrors = null)
    {
        var origin = scene.Origin;
        var surfaces = new List<FieldSurface>();
        var nudges = new List<Vector3>();
        var objects = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        float eps = NudgeLength(scene);
        var byAttr = groups.Where(g => g.Dimension == 3).GroupBy(g => g.Attribute).ToDictionary(g => g.Key, g => g.First().Name);

        if (onClip && clip.Enabled)
        {
            var e = clip.Equation;
            surfaces.Add(FieldSection.Slice(vol.Mesh, array, origin, clip, ct));
            nudges.Add(-eps * new Vector3(e.X, e.Y, e.Z));
            objects.Add("the clip plane");
        }
        if (allFaces)
        {
            foreach (var (attr, surface) in FieldSurfaces.Exterior(vol.Mesh, array, origin, ct).OrderBy(kv => kv.Key))
            {
                string name = byAttr.GetValueOrDefault(attr, $"region {attr}");
                surfaces.Add(surface);
                nudges.Add(Vector3.Zero);
                objects.Add(name);
                covered.Add(name);
            }
        }
        foreach (var f in faces)
        {
            if (covered.Contains(f.Object)) continue;              // All Faces already paints it
            var region = groups.Where(g => g.Name == f.Object && g.Dimension == 3).Select(g => g.Attribute).ToHashSet();
            if (region.Count == 0 || scene.Objects.FirstOrDefault(o => o.Name == f.Object) is not { } so) continue;
            var tris = Scene3DFaces.Triangles(scene, so.Id, f.Face);
            if (tris.Count == 0) continue;
            double tol = FaceMatchTolerance(so.Min, so.Max);
            var whole = FieldSurfaces.RegionBoundary(vol.Mesh, array, region, origin, ct);
            surfaces.Add(FieldFaces.OnFace(whole, tris, tol));
            var (_, normal) = Scene3DFaces.AreaAndNormal(scene, so.Id, f.Face);
            nudges.Add(normal is { } n ? eps * n : Vector3.Zero);
            objects.Add(f.Object);
        }
        // brief-em3d-76 R-em3d76-4c — each modelled surface reflected across every combination of the symmetry planes: the
        // same values, so the range and the peak are the modelled part's
        var reflections = surfaces.Select(_ => Array.Empty<(int, double)>()).ToList();
        if (mirrors is { Count: > 0 })
        {
            double[] o = [origin.X, origin.Y, origin.Z];
            var local = mirrors.Select(m => (m.Axis, At: m.AtM - o[m.Axis])).ToList();
            int modelled = surfaces.Count;
            for (int mask = 1; mask < 1 << local.Count; mask++)
            {
                var set = local.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
                for (int i = 0; i < modelled; i++)
                {
                    var m = surfaces[i];
                    var n = nudges[i];
                    foreach (var (axis, at) in set)
                    {
                        m = m.Mirrored(axis, at);
                        n = axis switch { 0 => n with { X = -n.X }, 1 => n with { Y = -n.Y }, _ => n with { Z = -n.Z } };
                    }
                    surfaces.Add(m);
                    nudges.Add(n);
                    objects.Add(objects[i] + MirroredSuffix);
                    reflections.Add(set);
                }
            }
        }
        var wires = table is null ? [] : Wires(scene, table, step);

        var scale = Range(q, surfaces, wires, table, step);
        if (fixRange && steps.Count > 1)
            for (int k = 0; k < steps.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                if (k == step || steps[k].VolumePvtu is not { } pvtu) continue;
                var other = vol.WithArraysOf(pvtu).Load(q.Array.Name);
                if (other is null) continue;
                var revalued = surfaces.Select(s => s.Revalue(other) ?? s).ToList();
                var wk = table is null ? wires : [.. wires.Select(w => Table(table, w.Wire, k) is { } t ? FieldWires.Revalue(w, t) : w)];
                if (Range(q, revalued, wk, table, k) is { } sk) scale = scale is null ? sk : scale.Union(sk);
            }
        scale ??= FieldColorScale.MinMax(q, []);
        string? note = surfaces.Count == 0 && wires.Count == 0
            ? "Nothing is painted: right-click a face ▸ Plot Temperature, or View ▸ Temperature ▸ All Faces or On Clip Plane (turn the clip plane on)."
            : null;
        return new FieldTemperatureBuild(surfaces, nudges, objects, reflections, wires, scale, covered, note);
    }

    /// <summary>What a mirrored half's object is called: the modelled object's name and this.</summary>
    public const string MirroredSuffix = " (mirrored)";

    /// <summary>
    /// How far a mesher triangle's centroid may lie from a picked face's display triangles and still be on it: 1 % of the
    /// object's diagonal (the two tessellations of a curved face differ by their chords), but never more than a quarter of the
    /// object's thinnest extent. The region boundary's normals are not oriented, so the normal test cannot tell a face from
    /// the one opposite it; on a layer thinner than 1 % of its diagonal (a die attach, a metallisation) the diagonal alone
    /// reached through to the hidden opposite face and painted it too — its colder values set the legend and the hot spot.
    /// </summary>
    public static double FaceMatchTolerance(Vector3 min, Vector3 max)
    {
        var d = max - min;
        double thinnest = Math.Min(d.X, Math.Min(d.Y, d.Z));
        double tol = 0.01 * d.Length();
        return thinnest > 0 ? Math.Min(tol, 0.25 * thinnest) : tol;
    }

    /// <summary>The true range of what is painted at sweep step <paramref name="step"/>, or null when none of it is finite. A
    /// wire is drawn at its centreline's vertices, and its 1D chain's peak can fall between two of them: the run's own T(s)
    /// for each painted wire joins the range, so the legend's maximum is the run's, never the drawing's.</summary>
    public static FieldColorScale? Range(FieldQuantity q, IReadOnlyList<FieldSurface> surfaces, IReadOnlyList<FieldWireSurface> wires,
                                         ThermalResultTable? table, int step)
    {
        var scale = FieldColorScale.TryMinMax(q, [.. surfaces, .. wires.Select(w => w.Surface)]);
        if (table is null) return scale;
        foreach (var w in wires)
        {
            if (Table(table, w.Wire, step) is not { } t) continue;
            var finite = t.T.Where(double.IsFinite).ToList();
            if (finite.Count == 0) continue;
            var ws = new FieldColorScale(false, finite.Min(), finite.Max(), 100, false, FieldNames.Unit(q.Array.Name));
            scale = scale is null ? ws : scale.Union(ws);
        }
        return scale;
    }

    /// <summary>R-em3d75-5 — every wire the table has T(s) for, coloured on the scene's own triangles.</summary>
    public static List<FieldWireSurface> Wires(Scene3DModel scene, ThermalResultTable table, int step)
    {
        var list = new List<FieldWireSurface>();
        if (scene.Problem is not { } problem) return list;
        foreach (var w in table.Wires)
        {
            if (scene.Objects.FirstOrDefault(o => o.Name == w.Wire) is not { } o ||
                problem.Solids.FirstOrDefault(s => s.Name == w.Wire)?.Primitive is not Em3dSweep sweep) continue;
            var (first, count, offset) = Scene3DFaces.TrianglesAt(scene, o.Id);
            var positions = new List<Vector3>(count);
            var indices = new List<int>(count);
            for (int i = first; i < first + count; i++)
            {
                var v = scene.Vertices[scene.Indices[i]];
                positions.Add(new Vector3(v.X, v.Y, v.Z) + offset);
                indices.Add(indices.Count);
            }
            if (Table(table, w.Wire, step) is { } t && FieldWires.Colour(w.Wire, sweep, positions, indices, scene.Origin, t) is { } painted)
                list.Add(painted);
        }
        return list;
    }

    /// <summary>A wire's T(s) at sweep step <paramref name="step"/>, or null when the table has none.</summary>
    public static WireTemperature? Table(ThermalResultTable table, string wire, int step)
        => table.Wires.FirstOrDefault(w => w.Wire == wire) is { } w && step < w.PerPoint.Length ? new WireTemperature(wire, w.S, w.PerPoint[step]) : null;

    // ── an EM field (brief-em3d-29, brief-em3d-82) ──────────────────────────────────────────

    /// <summary>
    /// An EM field's surfaces: the clip plane's slice (<paramref name="onPlane"/>), the boundary of <paramref name="selected"/>
    /// (a dielectric, the air or a body — a volume quantity) or the conductors (a boundary quantity) for
    /// <paramref name="onSurfaces"/>, and each of <paramref name="targets"/> painted one by one. <paramref name="sampler"/> is
    /// the volume's point sampler when the solver's dump carries no regions (openEMS): <paramref name="sampled"/>, and made
    /// here when none is given. <paramref name="sliceClipped"/> false is a ClipPlane plot's slice, which the view's section plane
    /// does not cut: it stays exactly on its plane. brief-em3d-100 — every surface built is multiplied by <paramref name="drive"/>'s
    /// factor: this plot's drive.
    /// </summary>
    public static FieldEmBuild Em(FieldQuantity q, FieldStep? vol, FieldStep? bnd, IReadOnlyList<FieldGroup> groups, Scene3DModel scene,
                                  ClipPlane3D clip, bool onPlane, bool onSurfaces, Scene3DObject? selected,
                                  IReadOnlyList<(PaintedFieldFace Face, FieldFaceTarget Target)> targets, FieldSampler? sampler, bool sampled,
                                  CancellationToken ct, bool sliceClipped = true, FieldDriveReading? drive = null, bool smooth = false)
    {
        var origin = scene.Origin;
        var surfaces = new List<FieldSurface>();
        var nudges = new List<Vector3>();
        var objects = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        // A surface lies ON its face or plane; it moves a hair toward the eye's side so nothing it lies on hides it.
        float eps = NudgeLength(scene);
        string? hint = null;
        if (!q.OnBoundary && vol?.Load(q.Array.Name) is { } loaded)
        {
            var array = smooth ? FieldSmoothing.Cached(vol.Mesh, loaded) : loaded;    // brief-cxp-5 — once per loaded field
            if (onPlane && clip.Enabled)
            {
                var e = clip.Equation;
                surfaces.Add(FieldSection.Slice(vol.Mesh, array, origin, clip, ct));    // brief-em3d-84 — render --field's cut
                // The slice lies ON the plane; the plane's own discard would eat half of it, so it
                // moves a hair to the kept side (n·p + d ≤ 0). Unclipped, it stays on the plane: moved, it fell behind a face
                // lying in the plane whenever the eye was on the cut-away side.
                nudges.Add(sliceClipped ? -eps * new Vector3(e.X, e.Y, e.Z) : Vector3.Zero);
                objects.Add("the clip plane");
            }
            if (onSurfaces && selected is { Kind: Scene3DKind.Dielectric or Scene3DKind.Air or Scene3DKind.Body } s &&
                groups.Where(g => g.Name == s.Name && g.Dimension == 3).Select(g => g.Attribute).ToHashSet() is { Count: > 0 } region)
            {
                surfaces.Add(FieldSurfaces.RegionBoundary(vol.Mesh, array, region, origin, ct));
                nudges.Add(Vector3.Zero);
                objects.Add(s.Name);
                covered.Add(s.Name);
            }
            hint = "Turn the clip plane on, select a dielectric or the air in the tree, or right-click a face ▸ Plot Field, to show the field on it.";
        }
        else if (q.OnBoundary && bnd?.Load(q.Array.Name) is { } barray)
        {
            var metal = groups.Where(g => g.Kind is "Conductor" or "Sheet").ToList();
            if (onSurfaces && metal.Count > 0)
            {
                surfaces.Add(FieldSurfaces.Boundary(bnd.Mesh, barray, metal.Select(g => g.Attribute).ToHashSet(), origin));
                nudges.Add(Vector3.Zero);
                objects.Add("the conductors");
                foreach (var g in metal) covered.Add(g.Name);
            }
            hint = metal.Count == 0 ? $"{FieldNames.Friendly(q.Array.Name)} is drawn on conductors, and this problem has none."
                                    : "Turn surfaces on to show the field on the conductors, or right-click one of their faces ▸ Plot Field.";
        }
        // brief-em3d-82 — the fourth source: each painted face, drawn a hair in front of its own face.
        var paints = new List<(PaintedFieldFace Face, FieldFacePaint Paint)>();
        var refused = new List<string>();
        if (targets.Count > 0)
        {
            var painter = new FieldFacePainter(q, vol, bnd, groups, origin,
                sampled && vol is not null ? sampler ?? new FieldSampler(vol.Mesh, ct) : null, ct);
            foreach (var (face, target) in targets)
            {
                if (painter.Paint(target, out string? why) is { } paint)
                {
                    surfaces.Add(paint.Surface);
                    nudges.Add(eps * paint.Toward);
                    objects.Add(face.Object);
                    paints.Add((face, paint));
                }
                else if (why is not null) refused.Add(why);
            }
        }
        // brief-em3d-100 — the plot's drive, on its own surfaces (the step two plots share is never scaled)
        FieldDrive.Apply(surfaces, drive?.Factor ?? 1);
        return new FieldEmBuild(surfaces, nudges, objects, covered, paints, refused, hint);
    }

    /// <summary>The range the 3D view draws an EM field's surfaces in: dB or not, its top at <paramref name="percentile"/> of the
    /// values drawn (a temperature's is <see cref="Temperature"/>'s own, the true minimum and maximum).</summary>
    public static FieldColorScale EmScale(FieldQuantity q, IReadOnlyList<FieldSurface> surfaces, bool db, double percentile,
                                          FieldDriveReading? drive = null)
        => FieldColorScale.Auto(q, surfaces, db, percentile, unit: drive is { Relative: true } ? "" : null);

    /// <summary>The painted faces as the painter takes them: each face's triangles and normal off the scene, its role from
    /// the object's kind, and the tolerance a mesh triangle is matched to it with (a sheet's is tight — its mesh faces lie
    /// exactly on it, and a thin tetrahedron just off it must not count).</summary>
    public static List<(PaintedFieldFace Face, FieldFaceTarget Target)> FaceTargets(Scene3DModel scene, IEnumerable<PaintedFieldFace> faces)
    {
        var list = new List<(PaintedFieldFace, FieldFaceTarget)>();
        foreach (var f in faces)
        {
            if (scene.Objects.FirstOrDefault(o => o.Name == f.Object) is not { } so) continue;
            var tris = Scene3DFaces.Triangles(scene, so.Id, f.Face);
            if (tris.Count == 0) continue;
            var (_, normal) = Scene3DFaces.AreaAndNormal(scene, so.Id, f.Face);
            var role = so.Kind switch
            {
                Scene3DKind.Conductor or Scene3DKind.Via or Scene3DKind.Wire => FieldFaceRole.Conductor,
                Scene3DKind.Sheet => FieldFaceRole.Sheet,
                _ => FieldFaceRole.Solid,
            };
            double tol = role == FieldFaceRole.Sheet ? 1e-6 * (scene.BoundsMax - scene.BoundsMin).Length() : FaceMatchTolerance(so.Min, so.Max);
            string label = $"'{so.Name}/{so.FaceName(f.Face)}'" + (f.Side switch { 1 => " (top side)", -1 => " (bottom side)", _ => "" });
            list.Add((f, new FieldFaceTarget(so.Name, label, role, tris, normal, tol, f.Side)));
        }
        return list;
    }

    /// <summary>How far a surface lying on a face or a plane is moved toward the eye's side of it, scene-local metres: a part
    /// in ten thousand of the scene's diagonal.</summary>
    public static float NudgeLength(Scene3DModel scene) => 1e-4f * (scene.BoundsMax - scene.BoundsMin).Length();
}
