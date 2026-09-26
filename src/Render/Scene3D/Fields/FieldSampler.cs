// brief-em3d-29 R-em3d29-3e / R-em3d29-4 — a field's value AT A POINT, read from the field data: the value
// the hover tooltip prints, and the one gate 3 holds against Palace's own probes.
//
// EXACT, not the drawn interpolation: the point is located in its element and the element's own shape
// functions are evaluated there — Lagrange P2 over the ten nodes at order 2, P1 over the four at order 1.
// The GEOMETRY is inverted with the same shape functions (Newton's method), because Gmsh's order-2 mesh is
// CURVED: an edge node is not its edge's midpoint on a curved boundary, and a linear inverse would put the
// point at the wrong place in the element. An order-2 Nédélec field lies in P2, so on the element's own
// nodes the P2 interpolant reproduces it; what is left is Palace's Float32 storage.
//
// Location is a uniform grid of cell bounding boxes, built once per mesh off the drawing path.

namespace CircuitRF.Render.Scene3D.Fields;

public sealed class FieldSampler
{
    private readonly FieldMesh _m;
    private readonly double[] _lo = new double[3], _cell = new double[3];
    private readonly int[] _dim = new int[3];
    private readonly int[] _start;     // CSR: bucket b's cells are _items[_start[b] .. _start[b+1])
    private readonly int[] _items;

    /// <summary>Indexes <paramref name="mesh"/> (a volume's tetrahedra or a boundary's triangles).</summary>
    public FieldSampler(FieldMesh mesh, CancellationToken ct = default)
    {
        _m = mesh;
        int n = mesh.CellCount, npc = mesh.NodesPerCell;
        var bmin = new float[3 * n];
        var bmax = new float[3 * n];
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
        for (int c = 0; c < n; c++)
        {
            for (int k = 0; k < 3; k++) { bmin[3 * c + k] = float.MaxValue; bmax[3 * c + k] = float.MinValue; }
            for (int j = 0; j < npc; j++)
            {
                int node = mesh.Cells[c * npc + j];
                for (int k = 0; k < 3; k++)
                {
                    float x = mesh.Points[3 * node + k];
                    if (x < bmin[3 * c + k]) bmin[3 * c + k] = x;
                    if (x > bmax[3 * c + k]) bmax[3 * c + k] = x;
                }
            }
            for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], bmin[3 * c + k]); hi[k] = Math.Max(hi[k], bmax[3 * c + k]); }
        }
        ct.ThrowIfCancellationRequested();
        // About two cells per bucket: the cube root of the cell count along the longest side.
        double ext = Math.Max(1e-30, Math.Max(hi[0] - lo[0], Math.Max(hi[1] - lo[1], hi[2] - lo[2])));
        double side = ext / Math.Max(1, Math.Cbrt(Math.Max(1, n) / 2.0));
        for (int k = 0; k < 3; k++)
        {
            _lo[k] = lo[k];
            _dim[k] = Math.Clamp((int)Math.Ceiling((hi[k] - lo[k]) / side), 1, 512);
            _cell[k] = Math.Max((hi[k] - lo[k]) / _dim[k], 1e-30);
        }
        long buckets = (long)_dim[0] * _dim[1] * _dim[2];
        _start = new int[buckets + 1];
        void Each(int c, Action<long> visit)
        {
            Span<int> a = stackalloc int[3], b = stackalloc int[3];
            for (int k = 0; k < 3; k++) { a[k] = Bin(k, bmin[3 * c + k]); b[k] = Bin(k, bmax[3 * c + k]); }
            for (int z = a[2]; z <= b[2]; z++)
                for (int y = a[1]; y <= b[1]; y++)
                    for (int x = a[0]; x <= b[0]; x++) visit(((long)z * _dim[1] + y) * _dim[0] + x);
        }
        for (int c = 0; c < n; c++) Each(c, b => _start[b + 1]++);
        for (long b = 0; b < buckets; b++) _start[b + 1] += _start[b];
        _items = new int[_start[buckets]];
        var fill = (int[])_start.Clone();
        for (int c = 0; c < n; c++) { int cc = c; Each(c, b => _items[fill[b]++] = cc); }
    }

    private int Bin(int k, double x) => Math.Clamp((int)Math.Floor((x - _lo[k]) / _cell[k]), 0, _dim[k] - 1);

    /// <summary>
    /// The cell containing point (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) — mesh
    /// units — and its reference coordinates there (barycentric L1..L3; L0 = 1 − the rest). For a triangle
    /// mesh, the triangle the point lies on (within <paramref name="surfaceTol"/>, mesh units) and its
    /// barycentrics. False when the point is in no cell.
    /// </summary>
    public bool Locate(double x, double y, double z, out int cell, out double l1, out double l2, out double l3, double surfaceTol = 0)
    {
        cell = -1; l1 = l2 = l3 = 0;
        Span<int> bin = [Bin(0, x), Bin(1, y), Bin(2, z)];
        long b = ((long)bin[2] * _dim[1] + bin[1]) * _dim[0] + bin[0];
        double best = double.MaxValue;
        for (int i = _start[b]; i < _start[b + 1]; i++)
        {
            int c = _items[i];
            if (_m.Shape == FieldCellShape.Tetrahedron)
            {
                if (!Invert(c, x, y, z, out double a1, out double a2, out double a3)) continue;
                double outside = Math.Max(0, -Math.Min(Math.Min(a1, a2), Math.Min(a3, 1 - a1 - a2 - a3)));
                if (outside < best) { best = outside; cell = c; l1 = a1; l2 = a2; l3 = a3; }
                if (outside == 0) return true;
            }
            else if (OnTriangle(c, x, y, z, out double a1, out double a2, out double dist))
            {
                double outside = Math.Max(0, -Math.Min(Math.Min(a1, a2), 1 - a1 - a2));
                double score = outside + dist;
                if (score < best && dist <= surfaceTol) { best = score; cell = c; l1 = a1; l2 = a2; l3 = 0; }
            }
        }
        // A point on a shared face, or one the Float32 node positions put a hair outside, still counts.
        return cell >= 0 && best <= 1e-6;
    }

    /// <summary>Array <paramref name="a"/>'s channels at the point, exactly (see the file header); false
    /// when the point is in no cell.</summary>
    public bool Sample(FieldArray a, double x, double y, double z, Span<double> channels, double surfaceTol = 0)
    {
        if (!Locate(x, y, z, out int c, out double l1, out double l2, out double l3, surfaceTol)) return false;
        Evaluate(a, c, l1, l2, l3, channels);
        return true;
    }

    /// <summary>Array <paramref name="a"/>'s channels in cell <paramref name="cell"/> at reference point (l1, l2, l3).</summary>
    public void Evaluate(FieldArray a, int cell, double l1, double l2, double l3, Span<double> channels)
    {
        int ch = a.Info.Channels, npc = _m.NodesPerCell;
        channels[..ch].Clear();
        if (a.Info.PerCell) { FieldSampling.Channels(a, -1, cell, channels); return; }
        Span<double> w = stackalloc double[10];
        int n = Shape(l1, l2, l3, w);
        Span<double> v = stackalloc double[ch];
        for (int j = 0; j < n; j++)
        {
            FieldSampling.Channels(a, _m.Cells[cell * npc + j], cell, v);
            for (int k = 0; k < ch; k++) channels[k] += w[j] * v[k];
        }
    }

    /// <summary>The shape functions at (l1, l2, l3), node by node in VTK's Lagrange order; returns how many.</summary>
    private int Shape(double l1, double l2, double l3, Span<double> w)
    {
        if (_m.Shape == FieldCellShape.Tetrahedron)
        {
            double l0 = 1 - l1 - l2 - l3;
            if (_m.Order == 1) { w[0] = l0; w[1] = l1; w[2] = l2; w[3] = l3; return 4; }
            w[0] = l0 * (2 * l0 - 1); w[1] = l1 * (2 * l1 - 1); w[2] = l2 * (2 * l2 - 1); w[3] = l3 * (2 * l3 - 1);
            w[4] = 4 * l0 * l1; w[5] = 4 * l1 * l2; w[6] = 4 * l0 * l2; w[7] = 4 * l0 * l3; w[8] = 4 * l1 * l3; w[9] = 4 * l2 * l3;
            return 10;
        }
        double m0 = 1 - l1 - l2;
        if (_m.Order == 1) { w[0] = m0; w[1] = l1; w[2] = l2; return 3; }
        w[0] = m0 * (2 * m0 - 1); w[1] = l1 * (2 * l1 - 1); w[2] = l2 * (2 * l2 - 1);
        w[3] = 4 * m0 * l1; w[4] = 4 * l1 * l2; w[5] = 4 * l2 * m0;
        return 6;
    }

    private void Node(int cell, int j, Span<double> p)
    {
        int node = _m.Cells[cell * _m.NodesPerCell + j];
        p[0] = _m.Points[3 * node]; p[1] = _m.Points[3 * node + 1]; p[2] = _m.Points[3 * node + 2];
    }

    /// <summary>The reference coordinates of a point in tetrahedron <paramref name="c"/>: linear from its
    /// corners, then — at order 2 — Newton on the quadratic geometry map.</summary>
    private bool Invert(int c, double x, double y, double z, out double l1, out double l2, out double l3)
    {
        Span<double> p0 = stackalloc double[3], p1 = stackalloc double[3], p2 = stackalloc double[3], p3 = stackalloc double[3];
        Node(c, 0, p0); Node(c, 1, p1); Node(c, 2, p2); Node(c, 3, p3);
        // Linear: [p1−p0 | p2−p0 | p3−p0] · l = x − p0.
        Span<double> j = stackalloc double[9];
        for (int k = 0; k < 3; k++) { j[3 * k] = p1[k] - p0[k]; j[3 * k + 1] = p2[k] - p0[k]; j[3 * k + 2] = p3[k] - p0[k]; }
        Span<double> r = [x - p0[0], y - p0[1], z - p0[2]];
        if (!Solve3(j, r, out l1, out l2, out l3)) return false;
        if (_m.Order == 1) return true;
        // A point far outside the cell's linear hull is not in it, curved or not: skip the Newton.
        if (Math.Min(Math.Min(l1, l2), Math.Min(l3, 1 - l1 - l2 - l3)) < -0.5) return true;
        Span<double> pn = stackalloc double[30];
        for (int n = 0; n < 10; n++) Node(c, n, pn.Slice(3 * n, 3));
        for (int it = 0; it < 30; it++)
        {
            double l0 = 1 - l1 - l2 - l3;
            // x(l) = Σ N_n(l) p_n, and dN/dl_k = ∂N/∂L_k − ∂N/∂L_0 with L_0 = 1 − l1 − l2 − l3.
            Span<double> nw = [l0 * (2 * l0 - 1), l1 * (2 * l1 - 1), l2 * (2 * l2 - 1), l3 * (2 * l3 - 1),
                               4 * l0 * l1, 4 * l1 * l2, 4 * l0 * l2, 4 * l0 * l3, 4 * l1 * l3, 4 * l2 * l3];
            Span<double> d1 = [-(4 * l0 - 1), 4 * l1 - 1, 0, 0, 4 * (l0 - l1), 4 * l2, -4 * l2, -4 * l3, 4 * l3, 0];
            Span<double> d2 = [-(4 * l0 - 1), 0, 4 * l2 - 1, 0, -4 * l1, 4 * l1, 4 * (l0 - l2), -4 * l3, 0, 4 * l3];
            Span<double> d3 = [-(4 * l0 - 1), 0, 0, 4 * l3 - 1, -4 * l1, 0, -4 * l2, 4 * (l0 - l3), 4 * l1, 4 * l2];
            Span<double> f = [0, 0, 0];
            j.Clear();
            for (int n = 0; n < 10; n++)
                for (int k = 0; k < 3; k++)
                {
                    double pk = pn[3 * n + k];
                    f[k] += nw[n] * pk;
                    j[3 * k] += d1[n] * pk; j[3 * k + 1] += d2[n] * pk; j[3 * k + 2] += d3[n] * pk;
                }
            Span<double> res = [x - f[0], y - f[1], z - f[2]];
            if (!Solve3(j, res, out double a, out double b, out double e)) return false;
            l1 += a; l2 += b; l3 += e;
            if (Math.Abs(a) + Math.Abs(b) + Math.Abs(e) < 1e-13) break;
        }
        return true;
    }

    /// <summary>The barycentrics of the point's projection on triangle <paramref name="c"/>'s corner plane,
    /// and its distance from that plane (mesh units).</summary>
    private bool OnTriangle(int c, double x, double y, double z, out double l1, out double l2, out double dist)
    {
        Span<double> p0 = stackalloc double[3], p1 = stackalloc double[3], p2 = stackalloc double[3];
        Node(c, 0, p0); Node(c, 1, p1); Node(c, 2, p2);
        double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
        double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
        double wx = x - p0[0], wy = y - p0[1], wz = z - p0[2];
        double uu = ux * ux + uy * uy + uz * uz, uv = ux * vx + uy * vy + uz * vz, vv = vx * vx + vy * vy + vz * vz;
        double wu = wx * ux + wy * uy + wz * uz, wv = wx * vx + wy * vy + wz * vz;
        double den = uu * vv - uv * uv;
        l1 = l2 = 0; dist = double.MaxValue;
        if (!(Math.Abs(den) > 0)) return false;
        l1 = (vv * wu - uv * wv) / den;
        l2 = (uu * wv - uv * wu) / den;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        dist = nl > 0 ? Math.Abs(wx * nx + wy * ny + wz * nz) / nl : double.MaxValue;
        return true;
    }

    private static bool Solve3(ReadOnlySpan<double> a, ReadOnlySpan<double> r, out double x, out double y, out double z)
    {
        double det = a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);
        x = y = z = 0;
        if (!(Math.Abs(det) > 0)) return false;
        x = (r[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (r[1] * a[8] - a[5] * r[2]) + a[2] * (r[1] * a[7] - a[4] * r[2])) / det;
        y = (a[0] * (r[1] * a[8] - a[5] * r[2]) - r[0] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * r[2] - r[1] * a[6])) / det;
        z = (a[0] * (a[4] * r[2] - r[1] * a[7]) - a[1] * (a[3] * r[2] - r[1] * a[6]) + r[0] * (a[3] * a[7] - a[4] * a[6])) / det;
        return true;
    }
}
