// brief-em3d-84 R-em3d84-3 — a field on a clip plane, cut and ranged ONCE for both pictures: the 3D view's slice and
// `render --field`'s section. The cut is FieldSlicer's through FieldMeshTets at the scene's origin, on the plane as
// ClipPlane3D states it (in float, exactly as the view holds it), so the two pictures hold the same triangles; the range is
// FieldColorScale.Auto over that one slice, the GPU view's range for a ClipPlane plot — or, for a temperature (brief-em3d-88),
// FieldColorScale.MinMax, the view's range for one.

using System.Numerics;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A clip-plane cut of one quantity: its triangles (scene-local metres), its range and its map, and where the scene's
/// origin is (world metres) so a picture can place it.</summary>
/// <param name="Axis">The plane's normal axis: 0 x, 1 y, 2 z (a <paramref name="Line"/>'s: its ClipAxis3D.Section).</param>
/// <param name="Line">brief-dfr34-3 — a vertical section through two points: the picture's horizontal is the distance along it.</param>
public sealed record FieldSectionCut(FieldQuantity Quantity, FieldSurface Slice, FieldColorScale Scale, ColorMap3D Map,
                                     (double X, double Y, double Z) Origin, int Axis, FieldSectionLine? Line = null)
{
    /// <summary>Where <paramref name="channels"/> fall in the range at <paramref name="phase"/> (radians), 0..1.</summary>
    public double Position(ReadOnlySpan<double> channels, double phase) => Scale.Position(Quantity.Evaluate(channels, phase));
}

/// <summary>
/// brief-dfr34-3 R-dfr34-3-1 — a vertical section's line in plan, world metres: from (X0, Y0) toward (X1, Y1). Its plane holds the
/// line and z; a point's place in it is its distance <see cref="Along"/> the line from the first point, and its z. The plane's
/// normal is the line's left, (−dy, dx): what <see cref="ClipPlane3D"/> keeps and the 3D view looks along are both stated from it.
/// </summary>
public readonly record struct FieldSectionLine(double X0, double Y0, double X1, double Y1)
{
    public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));

    /// <summary>The unit direction from the first point toward the second.</summary>
    public (double X, double Y) Direction { get { double l = Length; return ((X1 - X0) / l, (Y1 - Y0) / l); } }

    /// <summary>The plane's unit normal, the line's left.</summary>
    public (double X, double Y) Normal { get { var (dx, dy) = Direction; return (-dy, dx); } }

    /// <summary>How far (x, y) is along the line from its first point, metres.</summary>
    public double Along(double x, double y) { var (dx, dy) = Direction; return (x - X0) * dx + (y - Y0) * dy; }

    /// <summary>How far (x, y) is off the plane, along <see cref="Normal"/>, metres.</summary>
    public double Across(double x, double y) { var (nx, ny) = Normal; return (x - X0) * nx + (y - Y0) * ny; }

    /// <summary>The world point at <paramref name="along"/> on the line, height <paramref name="z"/>.</summary>
    public (double X, double Y, double Z) At(double along, double z) { var (dx, dy) = Direction; return (X0 + along * dx, Y0 + along * dy, z); }

    /// <summary>A plot's two points in world metres, or null for an axis plot (or two points that are one).</summary>
    public static FieldSectionLine? Of(CircuitRF.Design.ThreeD.C3dFieldPlot p, int dbuPerMicron)
    {
        if (p.Through is not { IsDegenerate: false } t) return null;
        double M(long v) => CircuitRF.Design.ThreeD.C3dLowering.Metres(v, dbuPerMicron);
        return new FieldSectionLine(M(t.From.U), M(t.From.V), M(t.To.U), M(t.To.V));
    }

    /// <summary>The plane in scene-local metres (the scene at <paramref name="origin"/>), kept on the side <paramref name="flip"/>
    /// keeps.</summary>
    public ClipPlane3D ScenePlane((double X, double Y, double Z) origin, bool flip = false)
    {
        var (nx, ny) = Normal;
        double at = nx * (X0 - origin.X) + ny * (Y0 - origin.Y);
        return new ClipPlane3D
        {
            Enabled = true, Axis = ClipAxis3D.Section, ViewNormal = new Vector3((float)nx, (float)ny, 0), Offset = (float)at, Flip = flip,
        };
    }
}

public static class FieldSection
{
    /// <summary>The section of <paramref name="array"/> on <paramref name="mesh"/> by <paramref name="clip"/> — the view's slice
    /// and the headless one.</summary>
    public static FieldSurface Slice(FieldMesh mesh, FieldArray array, (double X, double Y, double Z) origin, ClipPlane3D clip,
                                     CancellationToken ct = default)
    {
        var e = clip.Equation;
        var tets = new FieldMeshTets(mesh, array, origin);
        // An axis plane visits only the cells whose extent holds it (FieldSliceIndex); a view-facing plane, every cell.
        int[]? only = null;
        if (clip.IsAxis && mesh.Shape == FieldCellShape.Tetrahedron && mesh.ToMetres > 0)
        {
            int axis = (int)clip.Axis;
            double n = axis == 0 ? e.X : axis == 1 ? e.Y : e.Z;
            double o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var cells = FieldSliceIndex.For(mesh, axis).CellsAt((-e.W / n + o) / mesh.ToMetres);
            int sub = tets.Count / Math.Max(mesh.CellCount, 1);
            only = new int[cells.Length * sub];
            for (int i = 0; i < cells.Length; i++)
                for (int k = 0; k < sub; k++) only[i * sub + k] = cells[i] * sub + k;
        }
        return FieldSlicer.Slice(tets, new Vector3D(e.X, e.Y, e.Z), e.W, ct, only);
    }

    /// <summary>
    /// A ClipPlane plot's picture data: <paramref name="q"/> read from <paramref name="volume"/>, cut on <paramref name="clip"/>,
    /// ranged as the view ranges a ClipPlane plot (dB and the percentile the plot states; a temperature's true minimum and
    /// maximum), painted with the quantity's map.
    /// Null when the step holds no such volume array. brief-em3d-100 — the slice carries <paramref name="drive"/>, the plot's.
    /// </summary>
    /// <param name="smooth">brief-cxp-5 — <see cref="FieldSmoothing.WithinMaterials"/> first: each node's value averaged over the
    /// coincident nodes of cells of its own material, for a solver that writes each cell's own nodes (Palace at order 1).</param>
    public static FieldSectionCut? Cut(FieldQuantity q, FieldStep volume, (double X, double Y, double Z) origin, ClipPlane3D clip,
                                       bool db, double percentile, CancellationToken ct = default, FieldDriveReading? drive = null,
                                       FieldSectionLine? line = null, bool smooth = false)
    {
        if (q.OnBoundary || volume.Load(q.Array.Name) is not { } array) return null;
        if (smooth) array = FieldSmoothing.Cached(volume.Mesh, array);
        var slice = Slice(volume.Mesh, array, origin, clip, ct);
        FieldDrive.Apply([slice], drive?.Factor ?? 1);      // brief-em3d-100 — the plot's drive, as the view applies it
        // brief-em3d-88 — a temperature is ranged as the 3D view ranges one (brief-em3d-75 D9): its true minimum and maximum.
        var scale = q.IsTemperature ? FieldColorScale.MinMax(q, [slice]) : FieldSurfacePlot.EmScale(q, [slice], db, percentile, drive);
        return new FieldSectionCut(q, slice, scale, ColorMap3D.For(q), origin, (int)clip.Axis, line);
    }
}

/// <summary>
/// brief-cxp-5 — a display smoothing for a field written element by element. Palace's ParaView output gives every cell its own
/// nodes (four per first-order tetrahedron), so neighbouring cells hold different values at one point and a cut through them
/// is drawn as a mosaic. This averages each node's channels over every node at the same position whose cell has the same
/// attribute (the same material region), and only those: across a material interface the normal part of E really jumps (by
/// the permittivities' ratio at water against PDMS), and averaging there would draw a field that is not there. A per-cell
/// array, or one whose nodes are already shared, comes back unchanged in effect.
/// </summary>
public static class FieldSmoothing
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FieldArray, FieldArray> Smoothed = new();

    /// <summary><see cref="WithinMaterials"/>, made once per loaded array and kept while the array is: a plot rebuilt for a moved
    /// plane, a new drive or a phase reuses it, and nothing of it runs per frame.</summary>
    public static FieldArray Cached(FieldMesh mesh, FieldArray a) => Smoothed.GetValue(a, x => WithinMaterials(mesh, x));

    public static FieldArray WithinMaterials(FieldMesh mesh, FieldArray a)
    {
        if (a.Info.PerCell || mesh.NodeCount == 0) return a;
        int n = mesh.NodeCount, c = a.Components, npc = mesh.NodesPerCell;
        var attr = new int[n];
        for (int cell = 0; cell < mesh.CellCount; cell++)
            for (int k = 0; k < npc; k++) attr[mesh.Cells[cell * npc + k]] = mesh.Attribute[cell];
        // Positions are float in the file and coincident nodes are written identically, so the bits are the key.
        var groups = new Dictionary<(float, float, float, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var key = (mesh.Points[3 * i], mesh.Points[3 * i + 1], mesh.Points[3 * i + 2], attr[i]);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(i);
        }
        var re = (float[])a.Re.Clone();
        var im = a.Im is null ? null : (float[])a.Im.Clone();
        foreach (var list in groups.Values)
        {
            if (list.Count < 2) continue;
            for (int j = 0; j < c; j++)
            {
                double sr = 0, si = 0;
                foreach (int i in list) { sr += a.Re[i * c + j]; if (a.Im is { } ai) si += ai[i * c + j]; }
                foreach (int i in list) { re[i * c + j] = (float)(sr / list.Count); if (im is not null) im[i * c + j] = (float)(si / list.Count); }
            }
        }
        return new FieldArray { Info = a.Info, Re = re, Im = im };
    }
}
