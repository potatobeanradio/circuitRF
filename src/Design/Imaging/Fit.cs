// Primitives fitted to points — brief-img-1-raster-core.md R-im1-8.
//
// A via or a drill should come back as a circle, not a 40-gon, and a wire as a segment with a number saying how straight
// it was. Both fits return a residual so the caller decides whether the primitive explains the points.

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

/// <summary>A fitted circle; <paramref name="Residual"/> is the RMS radial distance of the points from it.</summary>
public readonly record struct CircleFit(double Cx, double Cy, double R, double Residual);

/// <summary>A fitted segment: the line through the points' centroid along their principal direction, from the
/// smallest projection to the largest. <paramref name="Residual"/> is the RMS perpendicular distance — its
/// straightness.</summary>
public readonly record struct SegmentFit(PointD A, PointD B, double Cx, double Cy, double Residual)
{
    public double Length => Math.Sqrt((B.x - A.x) * (B.x - A.x) + (B.y - A.y) * (B.y - A.y));

    /// <summary>The line's direction, 0 … π.</summary>
    public double Angle
    {
        get
        {
            double a = Math.Atan2(B.y - A.y, B.x - A.x);
            return a < 0 ? a + Math.PI : a >= Math.PI ? a - Math.PI : a;
        }
    }
}

public static class Fit
{
    /// <summary>The circle through <paramref name="pts"/>: an algebraic (Kåsa) fit, refined by geometric least squares
    /// (Gauss–Newton on the radial distances).</summary>
    public static CircleFit Circle(IReadOnlyList<PointD> pts)
    {
        if (pts.Count < 3) throw new ArgumentException("A circle needs at least three points.", nameof(pts));
        // Centred for conditioning.
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.x; my += p.y; }
        mx /= pts.Count; my /= pts.Count;

        // x² + y² + D x + E y + F = 0, least squares.
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in pts)
        {
            double x = p.x - mx, y = p.y - my, z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            sxz += x * z; syz += y * z; sz += z;
        }
        double n = pts.Count;
        var a = new double[3, 3] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var rhs = new[] { -sxz, -syz, -sz };
        var sol = Solve3(a, rhs);
        double cx = -sol[0] / 2, cy = -sol[1] / 2;
        double r = Math.Sqrt(Math.Max(0, cx * cx + cy * cy - sol[2]));

        for (int iter = 0; iter < 50; iter++)
        {
            // Residual d_i = |p - c| - r; Jacobian rows (-(x-cx)/ρ, -(y-cy)/ρ, -1).
            double j11 = 0, j12 = 0, j13 = 0, j22 = 0, j23 = 0, j33 = 0, g1 = 0, g2 = 0, g3 = 0;
            foreach (var p in pts)
            {
                double dx = p.x - mx - cx, dy = p.y - my - cy, rho = Math.Sqrt(dx * dx + dy * dy);
                if (rho < 1e-12) continue;
                double ja = -dx / rho, jb = -dy / rho, jc = -1, d = rho - r;
                j11 += ja * ja; j12 += ja * jb; j13 += ja * jc; j22 += jb * jb; j23 += jb * jc; j33 += jc * jc;
                g1 += ja * d; g2 += jb * d; g3 += jc * d;
            }
            var step = Solve3(new double[3, 3] { { j11, j12, j13 }, { j12, j22, j23 }, { j13, j23, j33 } }, [-g1, -g2, -g3]);
            cx += step[0]; cy += step[1]; r += step[2];
            if (Math.Abs(step[0]) + Math.Abs(step[1]) + Math.Abs(step[2]) < 1e-10) break;
        }
        double ss = 0;
        foreach (var p in pts)
        {
            double d = Math.Sqrt((p.x - mx - cx) * (p.x - mx - cx) + (p.y - my - cy) * (p.y - my - cy)) - r;
            ss += d * d;
        }
        return new CircleFit(cx + mx, cy + my, Math.Abs(r), Math.Sqrt(ss / n));
    }

    /// <summary>The total-least-squares segment through <paramref name="pts"/>.</summary>
    public static SegmentFit Segment(IReadOnlyList<PointD> pts)
    {
        if (pts.Count == 0) throw new ArgumentException("A segment needs at least one point.", nameof(pts));
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.x; my += p.y; }
        mx /= pts.Count; my /= pts.Count;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in pts)
        {
            double x = p.x - mx, y = p.y - my;
            sxx += x * x; sxy += x * y; syy += y * y;
        }
        // The principal direction of the 2×2 scatter.
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ux = Math.Cos(theta), uy = Math.Sin(theta);
        double tmin = double.MaxValue, tmax = double.MinValue, ss = 0;
        foreach (var p in pts)
        {
            double x = p.x - mx, y = p.y - my, t = x * ux + y * uy, d = -x * uy + y * ux;
            tmin = Math.Min(tmin, t);
            tmax = Math.Max(tmax, t);
            ss += d * d;
        }
        return new SegmentFit(new PointD(mx + tmin * ux, my + tmin * uy), new PointD(mx + tmax * ux, my + tmax * uy),
            mx, my, Math.Sqrt(ss / pts.Count));
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        // Gaussian elimination with partial pivoting; a singular system returns zeros (no step).
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (int c = 0; c < 3; c++)
        {
            int piv = c;
            for (int r = c + 1; r < 3; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-14) return [0, 0, 0];
            if (piv != c)
            {
                for (int k = 0; k < 3; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
                (v[c], v[piv]) = (v[piv], v[c]);
            }
            for (int r = c + 1; r < 3; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int k = c; k < 3; k++) m[r, k] -= f * m[c, k];
                v[r] -= f * v[c];
            }
        }
        var x = new double[3];
        for (int r = 2; r >= 0; r--)
        {
            double s = v[r];
            for (int k = r + 1; k < 3; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }
}
