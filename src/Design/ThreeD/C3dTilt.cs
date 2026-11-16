// brief-hzn-15 — a plane in any orientation as a drawing plane plus rotations: the frame a tilted sheet, a tilted prism and a tilted
// lumped port are stated in. The rotation is the SMALLEST one that carries the nearest axis plane's normal onto the plane's own, so a
// plane a degree off XY stays an XY sheet turned by a degree, and it is written as an object's placement writes rotations (about Z,
// then Y, then X, each omitted at 0°), so a tilted sheet is an ordinary Sheet with an ordinary Placement.

namespace CircuitRF.Design.ThreeD;

public static class C3dTilt
{
    /// <summary>The axis plane whose normal lies nearest <paramref name="normal"/>, and that normal signed to point the same way.</summary>
    public static (C3dPlane Plane, int NormalAxis, double[] Axis) NearestPlane(double[] normal)
    {
        int k = 0;
        for (int a = 1; a < 3; a++) if (Math.Abs(normal[a]) > Math.Abs(normal[k])) k = a;
        var e = new double[3];
        e[k] = normal[k] >= 0 ? 1 : -1;
        var plane = k switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
        return (plane, k, e);
    }

    /// <summary>The smallest rotation carrying unit vector <paramref name="from"/> onto unit vector <paramref name="to"/>, row-major 3 × 3.</summary>
    public static double[,] Between(double[] from, double[] to)
    {
        double[] k = [from[1] * to[2] - from[2] * to[1], from[2] * to[0] - from[0] * to[2], from[0] * to[1] - from[1] * to[0]];
        double s = Math.Sqrt(k[0] * k[0] + k[1] * k[1] + k[2] * k[2]);
        double c = from[0] * to[0] + from[1] * to[1] + from[2] * to[2];
        var r = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        if (s < 1e-15) return r;          // parallel (the caller never asks for the antiparallel case: the axis is signed to match)
        for (int i = 0; i < 3; i++) k[i] /= s;
        // Rodrigues: R = I + sinθ K + (1 − cosθ) K²
        double[,] K = { { 0, -k[2], k[1] }, { k[2], 0, -k[0] }, { -k[1], k[0], 0 } };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double kk = 0;
                for (int m = 0; m < 3; m++) kk += K[i, m] * K[m, j];
                r[i, j] += s * K[i, j] + (1 - c) * kk;
            }
        return r;
    }

    /// <summary>
    /// <paramref name="r"/> as rotations in list order — about Z by a, then Y by b, then X by c, so r = Rx(c)·Ry(b)·Rz(a) — each left
    /// out when it is 0° to within 1e-12°.
    /// </summary>
    public static List<C3dRotation> Euler(double[,] r)
    {
        double b = Math.Asin(Math.Clamp(r[0, 2], -1, 1));
        double a, c;
        if (Math.Abs(r[0, 2]) < 1 - 1e-12)
        {
            a = Math.Atan2(-r[0, 1], r[0, 0]);
            c = Math.Atan2(-r[1, 2], r[2, 2]);
        }
        else
        {
            a = 0;                                    // gimbal: only a ± c is defined
            c = Math.Atan2(r[2, 1], r[1, 1]);
        }
        var list = new List<C3dRotation>(3);
        void Add(C3dAxis axis, double rad)
        {
            double deg = rad * 180 / Math.PI;
            if (Math.Abs(deg) > 1e-12) list.Add(new C3dRotation { Axis = axis, Deg = deg });
        }
        Add(C3dAxis.Z, a);
        Add(C3dAxis.Y, b);
        Add(C3dAxis.X, c);
        return list;
    }

    /// <summary>The rotations as one transform, applied in list order.</summary>
    public static C3dTransform Rotation(IEnumerable<C3dRotation> rotations)
    {
        var t = C3dTransform.Identity;
        foreach (var r in rotations) t = t.Then(C3dTransform.Rotation(r.Axis, r.Deg));
        return t;
    }

    /// <summary>
    /// The plane through <paramref name="points"/> (least squares) and how far the farthest point lies off it, in the points' unit;
    /// null when they do not span a plane.
    /// </summary>
    public static (double[] Normal, double[] Centre, double Deviation)? FitPlane(IReadOnlyList<double[]> points)
    {
        int n = points.Count;
        if (n < 3) return null;
        var c = new double[3];
        foreach (var p in points) for (int i = 0; i < 3; i++) c[i] += p[i] / n;
        // The normal is the eigenvector of the scatter matrix with the smallest eigenvalue: found by Jacobi rotations (3 × 3, exact enough).
        var m = new double[3, 3];
        foreach (var p in points)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) m[i, j] += (p[i] - c[i]) * (p[j] - c[j]);
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
            if (off < 1e-30) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double cs = 1 / Math.Sqrt(t * t + 1), sn = t * cs;
                    for (int k = 0; k < 3; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = cs * mkp - sn * mkq;
                        m[k, q] = sn * mkp + cs * mkq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = cs * mpk - sn * mqk;
                        m[q, k] = sn * mpk + cs * mqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cs * vkp - sn * vkq;
                        v[k, q] = sn * vkp + cs * vkq;
                    }
                }
        }
        int least = 0;
        for (int i = 1; i < 3; i++) if (m[i, i] < m[least, least]) least = i;
        int most = 0;
        for (int i = 1; i < 3; i++) if (m[i, i] > m[most, most]) most = i;
        int middle = 3 - least - most;
        if (least == most || m[middle, middle] <= 1e-24 * Math.Max(1, m[most, most])) return null;    // collinear
        double[] normal = [v[0, least], v[1, least], v[2, least]];
        double len = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
        for (int i = 0; i < 3; i++) normal[i] /= len;
        double dev = points.Max(p => Math.Abs((p[0] - c[0]) * normal[0] + (p[1] - c[1]) * normal[1] + (p[2] - c[2]) * normal[2]));
        return (normal, c, dev);
    }
}
