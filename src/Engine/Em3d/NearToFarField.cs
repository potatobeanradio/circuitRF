// brief-em3d-31 R-em3d31-2 — the near-to-far-field transform of a closed surface's tangential fields, in the
// numeric layer. Pure functions: arrays in, a pattern out. No file, no process — src/Design reads openEMS's
// surface dumps and hands the samples here.
//
// WRITTEN FROM THE EQUIVALENCE PRINCIPLE, NOT FROM openEMS's nf2ff (GPL, and a separate program whose HDF5
// output circuitRF does not read). Love's equivalence on a closed surface S enclosing every source, outward
// normal n̂, e^{jωt}:
//
//     J_s = n̂ × H,     M_s = −n̂ × E
//     N(r̂) = ∮ J_s e^{+jk r̂·r'} dS',     L(r̂) = ∮ M_s e^{+jk r̂·r'} dS'
//     F_θ = −(jk/4π)(L_φ + η N_θ),        F_φ = +(jk/4π)(L_θ − η N_φ)
//
// with F = lim r·e^{+jkr}·E, VOLTS — PlanarFarFieldPattern's own r-normalisation, so the metrics stage
// reads a 3D pattern exactly as it reads kernel B's. The phase reference is the problem's origin.
//
// A CONDUCTING FLOOR (a PEC or PMC face that is the box's zmin face) leaves the surface open at the
// bottom. Image theory closes it exactly: the five faces above the floor, plus their mirror images across
// it, form a closed surface in the equivalent problem with the floor removed. Across a PEC plane an
// electric current's horizontal components flip and its vertical one does not; a magnetic current does
// the opposite. A PMC floor swaps the two. The pattern is then the upper hemisphere — the same domain the
// planar kernel reports — and there is nothing below the floor to report.
//
// QUADRATURE. The caller supplies each sample's area weight (the trapezoid rule on a face's node grid).
// Everything else here is exact arithmetic; the transform's accuracy is the sampling's.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Em3d;

/// <summary>One sample of a closed surface: position (m), outward unit normal, area weight (m²), and the
/// complex E (V/m) and H (A/m) there, Cartesian.</summary>
public readonly record struct NfSample(Point3 Position, Point3 Normal, double Weight,
                                       Complex Ex, Complex Ey, Complex Ez,
                                       Complex Hx, Complex Hy, Complex Hz);

/// <summary>What closes the surface below: nothing (the samples are a closed surface), or a conducting
/// plane at <c>z = FloorZ</c> whose image completes it.</summary>
public enum NfFloor { None, Pec, Pmc }

public static class NearToFarField
{
    /// <summary>
    /// The r-normalised pattern of <paramref name="samples"/> at <paramref name="fHz"/> on
    /// <paramref name="grid"/>, for the driven port <paramref name="drivenPort"/>. The medium outside the
    /// surface is free space. With a floor, θ past 90° is below it and must not be on the grid.
    /// </summary>
    public static PlanarFarFieldPattern Transform(IReadOnlyList<NfSample> samples, double fHz, PlanarFarFieldGrid grid,
                                                  int drivenPort, NfFloor floor = NfFloor.None, double floorZ = 0,
                                                  int? maxDegreeOfParallelism = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(grid);
        if (floor != NfFloor.None && grid.ThetaDeg[^1] > 90.0 + 1e-9)
            throw new ArgumentException("With a conducting floor the pattern is the upper hemisphere: θ must not pass 90°.");

        // The equivalent currents, with the floor's images appended.
        var src = new List<(double X, double Y, double Z, Complex Jx, Complex Jy, Complex Jz, Complex Mx, Complex My, Complex Mz)>(
            samples.Count * (floor == NfFloor.None ? 1 : 2));
        foreach (var s in samples)
        {
            var n = s.Normal;
            double w = s.Weight;
            // J = n × H, M = −n × E
            Complex jx = (n.Y * s.Hz - n.Z * s.Hy) * w, jy = (n.Z * s.Hx - n.X * s.Hz) * w, jz = (n.X * s.Hy - n.Y * s.Hx) * w;
            Complex mx = -(n.Y * s.Ez - n.Z * s.Ey) * w, my = -(n.Z * s.Ex - n.X * s.Ez) * w, mz = -(n.X * s.Ey - n.Y * s.Ex) * w;
            var p = s.Position;
            src.Add((p.X, p.Y, p.Z, jx, jy, jz, mx, my, mz));
            if (floor == NfFloor.None) continue;
            double zi = 2 * floorZ - p.Z;
            if (floor == NfFloor.Pec) src.Add((p.X, p.Y, zi, -jx, -jy, jz, mx, my, -mz));
            else                      src.Add((p.X, p.Y, zi, jx, jy, -jz, -mx, -my, mz));
        }

        double k = 2 * Math.PI * fHz / 299_792_458.0;
        double eta = FarFieldStage.Eta0;
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        var eth = new Complex[nt * np];
        var eph = new Complex[nt * np];
        var sx = src.Select(q => q.X).ToArray();
        var sy = src.Select(q => q.Y).ToArray();
        var sz = src.Select(q => q.Z).ToArray();
        var arr = src.ToArray();
        Complex c = new(0, k / (4 * Math.PI));

        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1, CancellationToken = ct };
        Parallel.For(0, nt, options, it =>
        {
            var (st, ctt) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180.0);
            for (int ip = 0; ip < np; ip++)
            {
                var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180.0);
                double rx = st * cp, ry = st * sp, rz = ctt;
                double nxr = 0, nxi = 0, nyr = 0, nyi = 0, nzr = 0, nzi = 0;
                double lxr = 0, lxi = 0, lyr = 0, lyi = 0, lzr = 0, lzi = 0;
                for (int q = 0; q < arr.Length; q++)
                {
                    var (sn, cs) = Math.SinCos(k * (rx * sx[q] + ry * sy[q] + rz * sz[q]));
                    ref readonly var a = ref arr[q];
                    nxr += a.Jx.Real * cs - a.Jx.Imaginary * sn; nxi += a.Jx.Real * sn + a.Jx.Imaginary * cs;
                    nyr += a.Jy.Real * cs - a.Jy.Imaginary * sn; nyi += a.Jy.Real * sn + a.Jy.Imaginary * cs;
                    nzr += a.Jz.Real * cs - a.Jz.Imaginary * sn; nzi += a.Jz.Real * sn + a.Jz.Imaginary * cs;
                    lxr += a.Mx.Real * cs - a.Mx.Imaginary * sn; lxi += a.Mx.Real * sn + a.Mx.Imaginary * cs;
                    lyr += a.My.Real * cs - a.My.Imaginary * sn; lyi += a.My.Real * sn + a.My.Imaginary * cs;
                    lzr += a.Mz.Real * cs - a.Mz.Imaginary * sn; lzi += a.Mz.Real * sn + a.Mz.Imaginary * cs;
                }
                Complex nx = new(nxr, nxi), ny = new(nyr, nyi), nz = new(nzr, nzi);
                Complex lx = new(lxr, lxi), ly = new(lyr, lyi), lz = new(lzr, lzi);
                // θ̂ = (cosθcosφ, cosθsinφ, −sinθ), φ̂ = (−sinφ, cosφ, 0)
                Complex nTh = nx * ctt * cp + ny * ctt * sp - nz * st, nPh = -nx * sp + ny * cp;
                Complex lTh = lx * ctt * cp + ly * ctt * sp - lz * st, lPh = -lx * sp + ly * cp;
                int idx = grid.IndexOf(it, ip);
                eth[idx] = -c * (lPh + eta * nTh);
                eph[idx] = c * (lTh - eta * nPh);
            }
        });
        return FarFieldStage.Pattern(grid, eth, eph, drivenPort, fHz);
    }

    /// <summary>
    /// A Cartesian far field (Palace's <c>farfield-rE.csv</c> writes r·E as x, y, z components) as the two
    /// spherical components on the grid's own triad. At a pole the Cartesian vector is well defined where θ̂
    /// and φ̂ are not, which is why a pole needs only one sample for every azimuth.
    /// </summary>
    public static (Complex ETheta, Complex EPhi) ToSpherical(Complex ex, Complex ey, Complex ez, double thetaDeg, double phiDeg)
    {
        var (st, ct) = Math.SinCos(thetaDeg * Math.PI / 180.0);
        var (sp, cp) = Math.SinCos(phiDeg * Math.PI / 180.0);
        return (ex * ct * cp + ey * ct * sp - ez * st, -ex * sp + ey * cp);
    }
}
