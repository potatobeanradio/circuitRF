// brief-em3d-31 R-em3d31-2 — the near-to-far-field transform against a closed form: the exact near fields of
// a Hertzian dipole, sampled on a box the way a surface dump samples them, must transform to the dipole's
// analytic far field. Nothing here is another circuitRF path agreeing with itself.

using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Em3d;

public class NearToFarFieldTests(ITestOutputHelper output)
{
    private const double F = 3e9;
    private static readonly double K = 2 * Math.PI * F / 299_792_458.0;
    private static readonly double Eta = FarFieldStage.Eta0;

    /// <summary>E and H of a current moment <paramref name="il"/> (A·m) along <paramref name="u"/> at
    /// <paramref name="at"/>, observed at <paramref name="p"/> — the exact dipole fields, e^{jωt}.</summary>
    private static (Complex[] E, Complex[] H) Dipole(Point3 at, double[] u, Complex il, Point3 p)
    {
        double dx = p.X - at.X, dy = p.Y - at.Y, dz = p.Z - at.Z, r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double[] rh = [dx / r, dy / r, dz / r];
        Complex jk = new(0, K), ph = Complex.Exp(new Complex(0, -K * r));
        Complex a = jk / r + 1 / (r * r) + 1 / (jk * r * r * r), b = 1 / (r * r) + 1 / (jk * r * r * r), h = jk / r + 1 / (r * r);
        double ur = u[0] * rh[0] + u[1] * rh[1] + u[2] * rh[2];
        double[] uxr = [u[1] * rh[2] - u[2] * rh[1], u[2] * rh[0] - u[0] * rh[2], u[0] * rh[1] - u[1] * rh[0]];
        var e = new Complex[3];
        var hh = new Complex[3];
        for (int i = 0; i < 3; i++)
        {
            double uPerp = u[i] - ur * rh[i];
            e[i] = Eta * il * ph / (4 * Math.PI) * (-a * uPerp + 2 * b * ur * rh[i]);
            hh[i] = il * ph / (4 * Math.PI) * h * uxr[i];
        }
        return (e, hh);
    }

    /// <summary>Trapezoid samples on a box's faces (each face its own node grid), skipping
    /// <paramref name="skipZMin"/>.</summary>
    private static List<NfSample> Box(double[] lo, double[] hi, int n, Func<Point3, (Complex[] E, Complex[] H)> field, bool skipZMin)
    {
        var list = new List<NfSample>();
        for (int axis = 0; axis < 3; axis++)
            for (int side = 0; side < 2; side++)
            {
                if (skipZMin && axis == 2 && side == 0) continue;
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n; j++)
                    {
                        var c = new double[3];
                        c[axis] = side == 0 ? lo[axis] : hi[axis];
                        c[a1] = lo[a1] + (hi[a1] - lo[a1]) * i / n;
                        c[a2] = lo[a2] + (hi[a2] - lo[a2]) * j / n;
                        double w = (hi[a1] - lo[a1]) / n * (hi[a2] - lo[a2]) / n * (i == 0 || i == n ? 0.5 : 1) * (j == 0 || j == n ? 0.5 : 1);
                        var nv = new double[3];
                        nv[axis] = side == 0 ? -1 : 1;
                        var p = new Point3(c[0], c[1], c[2]);
                        var (e, h) = field(p);
                        list.Add(new NfSample(p, new Point3(nv[0], nv[1], nv[2]), w, e[0], e[1], e[2], h[0], h[1], h[2]));
                    }
            }
        return list;
    }

    [Fact]
    public void AHertzianDipolesNearFieldOnABox_TransformsToItsAnalyticFarField()
    {
        double lam = 299_792_458.0 / F;
        Complex il = new(1e-3, 0);
        // Off-origin and tilted, so neither the phase reference nor an axis alignment can hide a sign.
        var at = new Point3(0.03 * lam, -0.05 * lam, 0.02 * lam);
        double s = 1 / Math.Sqrt(3);
        double[] u = [s, s, s];
        var samples = Box([-0.4 * lam, -0.4 * lam, -0.4 * lam], [0.4 * lam, 0.4 * lam, 0.4 * lam], 40, p => Dipole(at, u, il, p), false);
        var grid = FarFieldStage.Sphere(5, 10);
        var pat = NearToFarField.Transform(samples, F, grid, 1);

        double peak = Eta * K * il.Magnitude / (4 * Math.PI), worst = 0;
        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                var (st, ct) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180);
                var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180);
                double[] r = [st * cp, st * sp, ct];
                double ur = u[0] * r[0] + u[1] * r[1] + u[2] * r[2];
                // F = jηk·Il/4π · (−u⊥) · e^{+jk r̂·r₀}
                Complex f = new Complex(0, 1) * Eta * K * il / (4 * Math.PI) * Complex.Exp(new Complex(0, K * (r[0] * at.X + r[1] * at.Y + r[2] * at.Z)));
                Complex[] fv = [.. Enumerable.Range(0, 3).Select(i => -f * (u[i] - ur * r[i]))];
                var (fth, fph) = NearToFarField.ToSpherical(fv[0], fv[1], fv[2], grid.ThetaDeg[it], grid.PhiDeg[ip]);
                int k = grid.IndexOf(it, ip);
                worst = Math.Max(worst, Math.Max((pat.ETheta[k] - fth).Magnitude, (pat.EPhi[k] - fph).Magnitude) / peak);
            }
        output.WriteLine($"worst |ΔF| / peak = {worst:E3} (box ±0.4 λ, λ/100 sampling)");
        Assert.True(worst < 2e-3, $"worst {worst:E3}");

        // Its directivity is the Hertzian dipole's 1.5 exactly, through the metrics stage.
        double d = 4 * Math.PI * pat.PeakIntensityWPerSr / pat.RadiatedPowerW;
        output.WriteLine($"D = {d:F5} (1.5)");
        Assert.Equal(1.5, d, 2);
    }

    [Fact]
    public void AHorizontalDipoleOverAPecFloor_TransformsThroughTheImage()
    {
        double lam = 299_792_458.0 / F, h = 0.2 * lam;
        Complex il = new(1e-3, 0);
        var src = new Point3(0, 0, h);
        var img = new Point3(0, 0, -h);
        double[] ux = [1, 0, 0];
        (Complex[] E, Complex[] H) Pair(Point3 p)
        {
            var (e1, h1) = Dipole(src, ux, il, p);
            var (e2, h2) = Dipole(img, ux, -il, p);
            return ([e1[0] + e2[0], e1[1] + e2[1], e1[2] + e2[2]], [h1[0] + h2[0], h1[1] + h2[1], h1[2] + h2[2]]);
        }
        // Five faces above the floor at z = 0; the image closes the surface.
        var samples = Box([-0.4 * lam, -0.4 * lam, 0], [0.4 * lam, 0.4 * lam, 0.5 * lam], 40, Pair, skipZMin: true);
        var grid = PlanarFarFieldGrid.Hemisphere(5, 10);
        var pat = NearToFarField.Transform(samples, F, grid, 1, NfFloor.Pec, 0);

        double peak = 2 * Eta * K * il.Magnitude / (4 * Math.PI), worst = 0;
        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                var (st, ct) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180);
                var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180);
                double[] r = [st * cp, st * sp, ct];
                // array factor of the dipole and its negated image: 2j·sin(kh cosθ)
                Complex f = new Complex(0, 1) * Eta * K * il / (4 * Math.PI) * 2 * new Complex(0, 1) * Math.Sin(K * h * ct);
                Complex[] fv = [.. Enumerable.Range(0, 3).Select(i => -f * (ux[i] - r[0] * r[i]))];
                var (fth, fph) = NearToFarField.ToSpherical(fv[0], fv[1], fv[2], grid.ThetaDeg[it], grid.PhiDeg[ip]);
                int k = grid.IndexOf(it, ip);
                worst = Math.Max(worst, Math.Max((pat.ETheta[k] - fth).Magnitude, (pat.EPhi[k] - fph).Magnitude) / peak);
            }
        output.WriteLine($"worst |ΔF| / peak over the PEC floor = {worst:E3}");
        Assert.True(worst < 2e-3, $"worst {worst:E3}");
    }
}
