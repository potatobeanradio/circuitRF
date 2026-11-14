// brief-paa-2 R-paa2-2a — one element's embedded pattern, sampled on the record's grid and referred to the element's own position
// (R-paa2-3), as the array sum reads it. On the record's own grid it is read; anywhere else it is INTERPOLATED, bilinear in θ and φ,
// Eθ and Eφ separately, magnitude and unwrapped phase. The element pattern is one element's, smooth over a beamwidth of the array,
// so the fine local grid of a metric costs an interpolation per point and not a re-solve (note §6.2). The interpolant's error is
// measured in brief-paa-2's completion note.
//
// OUTSIDE THE GRID IT IS ZERO, AND THAT IS PHYSICS ON A PLANAR RECORD: its θ axis stops at 90° because the model has no region below
// the plane (src/Engine/Mom/CLAUDE.md §3.6), not because nobody sampled it.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>An element's pattern: Eθ and Eφ on a grid, row-major [theta, phi].</summary>
public sealed class ElementPattern
{
    private readonly ConditionalWeakTable<ArrayDirections, Complex[][]> _sampled = new();
    private readonly double _phiPeriod;
    private readonly bool _periodic;

    public ElementPattern(PlanarFarFieldGrid grid, Complex[] eTheta, Complex[] ePhi)
    {
        if (eTheta.Length != grid.DirectionCount || ePhi.Length != grid.DirectionCount)
            throw new ArgumentException($"An element pattern on a {grid.ThetaDeg.Count} × {grid.PhiDeg.Count} grid has {grid.DirectionCount} samples per component.");
        Grid = grid;
        ETheta = eTheta;
        EPhi = ePhi;
        int np = grid.PhiDeg.Count;
        double step = np > 1 ? grid.PhiDeg[1] - grid.PhiDeg[0] : 360;
        _periodic = np == 1 || Math.Abs(grid.PhiDeg[^1] + step - grid.PhiDeg[0] - 360) < 1e-9 * 360;
        _phiPeriod = 360;
    }

    public PlanarFarFieldGrid Grid { get; }
    public Complex[] ETheta { get; }
    public Complex[] EPhi { get; }

    /// <summary>The pattern at every direction of <paramref name="directions"/>: read on its own grid, interpolated elsewhere. Kept per
    /// direction set, so a second sum over the same directions interpolates nothing.</summary>
    public (Complex[] ETheta, Complex[] EPhi) Sample(ArrayDirections directions)
    {
        var both = _sampled.GetValue(directions, d =>
        {
            int n = d.Count;
            var et = new Complex[n];
            var ep = new Complex[n];
            if (d.Grid is { } g && SameGrid(g, Grid))
            {
                Array.Copy(ETheta, et, n);
                Array.Copy(EPhi, ep, n);
            }
            else
                for (int i = 0; i < n; i++) (et[i], ep[i]) = At(d.ThetaRad[i], d.PhiRad[i]);
            return [et, ep];
        });
        return (both[0], both[1]);
    }

    private static bool SameGrid(PlanarFarFieldGrid a, PlanarFarFieldGrid b)
        => ReferenceEquals(a, b) || (a.ThetaDeg.SequenceEqual(b.ThetaDeg) && a.PhiDeg.SequenceEqual(b.PhiDeg));

    /// <summary>The pattern at θ, φ in radians. Zero beyond the grid's last θ.</summary>
    public (Complex ETheta, Complex EPhi) At(double thetaRad, double phiRad)
    {
        var th = Grid.ThetaDeg;
        var ph = Grid.PhiDeg;
        double t = thetaRad * 180 / Math.PI;
        if (t < th[0] - 1e-9 || t > th[^1] + 1e-9) return (Complex.Zero, Complex.Zero);
        t = Math.Clamp(t, th[0], th[^1]);
        int i = Bracket(th, t);
        int i1 = Math.Min(i + 1, th.Count - 1);
        double ft = i1 == i ? 0 : (t - th[i]) / (th[i1] - th[i]);

        double p = phiRad * 180 / Math.PI;
        int j, j1;
        double fp;
        int np = ph.Count;
        if (np == 1) { j = j1 = 0; fp = 0; }
        else if (_periodic)
        {
            p = ph[0] + Mod(p - ph[0], _phiPeriod);
            j = Bracket(ph, p);
            j1 = (j + 1) % np;
            double hi = j1 == 0 ? ph[0] + _phiPeriod : ph[j1];
            fp = (p - ph[j]) / (hi - ph[j]);
        }
        else
        {
            p = Math.Clamp(ph[0] + Mod(p - ph[0], _phiPeriod), ph[0], ph[^1]);
            j = Bracket(ph, p);
            j1 = Math.Min(j + 1, np - 1);
            fp = j1 == j ? 0 : (p - ph[j]) / (ph[j1] - ph[j]);
        }
        int a = Grid.IndexOf(i, j), b = Grid.IndexOf(i, j1), c = Grid.IndexOf(i1, j), d = Grid.IndexOf(i1, j1);
        return (Bilinear(ETheta[a], ETheta[b], ETheta[c], ETheta[d], ft, fp), Bilinear(EPhi[a], EPhi[b], EPhi[c], EPhi[d], ft, fp));
    }

    private static double Mod(double x, double m) => x - m * Math.Floor(x / m);

    /// <summary>The largest index whose value is at or below <paramref name="x"/>, within the axis.</summary>
    private static int Bracket(IReadOnlyList<double> axis, double x)
    {
        int lo = 0, hi = axis.Count - 1;
        if (x >= axis[hi]) return hi;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (axis[mid] <= x) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Bilinear in magnitude and in phase unwrapped against the first non-zero corner. <paramref name="ft"/> runs from the
    /// θ row of <paramref name="c00"/> to that of <paramref name="c10"/>, <paramref name="fp"/> from φ column 0 to 1.</summary>
    private static Complex Bilinear(Complex c00, Complex c01, Complex c10, Complex c11, double ft, double fp)
    {
        double w00 = (1 - ft) * (1 - fp), w01 = (1 - ft) * fp, w10 = ft * (1 - fp), w11 = ft * fp;
        double mag = w00 * c00.Magnitude + w01 * c01.Magnitude + w10 * c10.Magnitude + w11 * c11.Magnitude;
        if (mag == 0) return Complex.Zero;
        Complex reference = c00 != Complex.Zero ? c00 : c01 != Complex.Zero ? c01 : c10 != Complex.Zero ? c10 : c11;
        double r = reference.Phase;
        double Unwrap(Complex z) => z == Complex.Zero ? r : r + Math.IEEERemainder(z.Phase - r, 2 * Math.PI);
        double phase = w00 * Unwrap(c00) + w01 * Unwrap(c01) + w10 * Unwrap(c10) + w11 * Unwrap(c11);
        return Complex.FromPolarCoordinates(mag, phase);
    }
}
