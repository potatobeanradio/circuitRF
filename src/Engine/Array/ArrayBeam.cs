// brief-paa-2 R-paa2-2a / R-paa2-7 — the LOCAL evaluation of an array's beam, and every metric read from it.
//
// THE OVERVIEW GRID IS NEVER USED TO COMPUTE A METRIC (note §6.2). A 64 × 64 array at half a wavelength has a 1.6° beam; a record's 1°
// grid puts one or two samples across it, so a beamwidth, sidelobe level or pointing error read there is wrong. So the beam is
// evaluated on its own fine grid: a patch around the peak in the tangent plane, then the two principal cuts through it (the plane of
// the peak and boresight, and the great circle across it), each at a step no coarser than a tenth of the beamwidth — ESTIMATED from the
// lattice, pitch and frequency before the sum and CONFIRMED from the cut (re-cut finer when the measured beam is narrower). Each
// sidelobe a metric reads is re-sampled ten times finer again around its own peak. The element pattern is interpolated at these points
// and the array factor is exact there (ElementPattern, ArrayPattern).
//
// A peak or a sidelobe is read off its samples by the parabola through the three around it, in dB for the cut and in power for the
// patch: on a step of a tenth of a beamwidth that is worth ~1e-4° in pointing, measured in brief-paa-2's completion note.

using System.Numerics;

using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>One principal cut through the peak: the angle from the peak along it, degrees, and the power there over the peak's, dB.</summary>
public sealed record ArrayCut(string Plane, double[] OffsetDeg, double[] RelativeDb);

/// <summary>R-paa2-7 — what the local evaluation reads off a beam.</summary>
/// <param name="ElevationBeamwidthDeg">Half-power beamwidth in the plane holding the peak and boresight.</param>
/// <param name="AzimuthBeamwidthDeg">Half-power beamwidth across that plane, through the peak.</param>
/// <param name="ElevationSidelobeDb">The first sidelobe in the elevation cut over the peak, dB (negative); null when there is none.</param>
/// <param name="PointingErrorDeg">The angle from the requested direction to the peak; null when none was requested.</param>
/// <param name="StepDeg">The step the cuts were sampled at.</param>
/// <param name="EstimatedBeamwidthDeg">The beamwidth estimated from the lattice before the sum.</param>
public sealed record ArrayBeamMetrics(
    double PeakThetaDeg, double PeakPhiDeg, double PeakPower, double? PointingErrorDeg,
    double ElevationBeamwidthDeg, double AzimuthBeamwidthDeg, double? ElevationSidelobeDb, double? AzimuthSidelobeDb,
    double StepDeg, double EstimatedBeamwidthDeg, ArrayCut Elevation, ArrayCut Azimuth)
{
    /// <summary>The higher of the two cuts' first sidelobes, dB over the peak; null when neither has one.</summary>
    public double? FirstSidelobeDb => (ElevationSidelobeDb, AzimuthSidelobeDb) switch
    {
        (null, null) => null,
        (null, var a) => a,
        (var e, null) => e,
        var (e, a) => Math.Max(e!.Value, a!.Value),
    };
}

/// <summary>How far the nearest grating lobe is from the visible circle, in direction cosines (negative: it is visible), and where it is.</summary>
public sealed record ArrayGratingLobe(double Margin, double U, double V);

/// <summary>R-paa2-2a / R-paa2-7 — the beam of an array, from its local evaluation.</summary>
public static class ArrayBeam
{
    /// <summary>The half-power beamwidth of the array's largest dimension, uniformly weighted, at boresight, degrees: 0.886·λ/L. A
    /// taper and a scan only widen it, so a step from it is never too coarse. A single element is given 180°.</summary>
    public static double EstimateBeamwidthDeg(ArrayLattice lattice, double hz)
    {
        double lambda = Mom.EmConstants.C0 / hz;
        double l = Math.Max(lattice.Cols * lattice.Dx, lattice.Rows * lattice.Dy);
        if (lattice.Count == 1 || !(l > 0)) return 180;
        return Math.Min(180, 0.886 * lambda / l * 180 / Math.PI);
    }

    /// <summary>
    /// The beam's metrics, from a patch around the peak and the two principal cuts through it. The search starts at
    /// (<paramref name="startThetaDeg"/>, <paramref name="startPhiDeg"/>) — the requested scan, or the overview's maximum — and the
    /// pointing error is against (<paramref name="theta0Deg"/>, <paramref name="phi0Deg"/>) when given.
    /// </summary>
    public static ArrayBeamMetrics Evaluate(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements, double hz,
                                            double startThetaDeg, double startPhiDeg, double? theta0Deg = null, double? phi0Deg = null, double? stepDeg = null)
    {
        double estimate = EstimateBeamwidthDeg(lattice, hz);
        double step = Math.Min(stepDeg ?? estimate / 10, 1.0);
        double rad = Math.PI / 180;
        double phiRef = (phi0Deg ?? startPhiDeg) * rad;
        Power power = dirs => ArrayPattern.Sum(lattice, weights, elements, ArrayDirections.OfVectors(dirs), hz);

        var p = ArrayDirections.Vector(startThetaDeg * rad, startPhiDeg * rad);
        double startCos = Math.Max(Math.Cos(startThetaDeg * rad), 0.2);
        int half = (int)Math.Ceiling(1.5 * estimate / startCos / step);
        p = Climb(power, p, phiRef, step * rad, half, 8);
        p = Climb(power, p, phiRef, step * rad / 10, 3, 2);
        double peak = power([p]).Power(0);

        var (cutE, cutA) = Cuts(power, p, phiRef, step);
        double bwE = Beamwidth(cutE), bwA = Beamwidth(cutA);
        double measured = Math.Min(bwE, bwA);
        if (measured > 0 && step > measured / 10 * (1 + 1e-9))
        {
            step = measured / 10;
            (cutE, cutA) = Cuts(power, p, phiRef, step);
            bwE = Beamwidth(cutE);
            bwA = Beamwidth(cutA);
        }
        double? slE = Sidelobe(power, p, phiRef, cutE, true, step), slA = Sidelobe(power, p, phiRef, cutA, false, step);

        var (theta, phi) = ArrayDirections.Angles(p);
        double? pointing = theta0Deg is { } t0 ? Separation(p, ArrayDirections.Vector(t0 * rad, (phi0Deg ?? 0) * rad)) / rad : null;
        return new ArrayBeamMetrics(theta / rad, phi / rad, peak, pointing, bwE, bwA, slE, slA, step, estimate,
                                    Relative(cutE, peak), Relative(cutA, peak));
    }

    private delegate ArrayField Power(IReadOnlyList<(double X, double Y, double Z)> directions);

    private sealed record RawCut(string Plane, double[] OffsetDeg, double[] Power, (double, double, double) Tangent);

    private static ArrayCut Relative(RawCut cut, double peak)
        => new(cut.Plane, cut.OffsetDeg, [.. cut.Power.Select(v => v > 0 ? 10 * Math.Log10(v / peak) : double.NegativeInfinity)]);

    /// <summary>The angle between two unit vectors, radians, by atan2 so a small one keeps its digits.</summary>
    public static double Separation((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        var c = Cross(a, b);
        return Math.Atan2(Math.Sqrt(Dot(c, c)), Dot(a, b));
    }

    /// <summary>θ̂ and φ̂ at <paramref name="p"/>; at the zenith they are taken at azimuth <paramref name="phiRef"/>.</summary>
    private static ((double, double, double) T, (double, double, double) P) Tangents((double X, double Y, double Z) p, double phiRef)
    {
        var (theta, phi) = ArrayDirections.Angles(p);
        if (Math.Sqrt(p.X * p.X + p.Y * p.Y) < 1e-12) phi = phiRef;
        var (st, ct) = Math.SinCos(theta);
        var (sp, cp) = Math.SinCos(phi);
        return ((ct * cp, ct * sp, -st), (-sp, cp, 0));
    }

    /// <summary>The direction at angle offsets (a, b) radians from <paramref name="p"/> along the two tangents.</summary>
    private static (double X, double Y, double Z) Offset((double X, double Y, double Z) p, (double X, double Y, double Z) e1, (double X, double Y, double Z) e2, double a, double b)
    {
        double rho = Math.Sqrt(a * a + b * b);
        if (rho == 0) return p;
        var (s, c) = Math.SinCos(rho);
        double fa = s * a / rho, fb = s * b / rho;
        return (c * p.X + fa * e1.X + fb * e2.X, c * p.Y + fa * e1.Y + fb * e2.Y, c * p.Z + fa * e1.Z + fb * e2.Z);
    }

    /// <summary>The peak of a (2·<paramref name="half"/> + 1)² patch at <paramref name="step"/> around <paramref name="p"/>, moved
    /// when it lands on the edge, then the parabola through its neighbours in each tangent direction.</summary>
    private static (double X, double Y, double Z) Climb(Power power, (double X, double Y, double Z) p, double phiRef, double step, int half, int moves)
    {
        for (int move = 0; move <= moves; move++)
        {
            var (e1, e2) = Tangents(p, phiRef);
            int side = 2 * half + 1;
            var dirs = new (double, double, double)[side * side];
            for (int i = 0; i < side; i++)
                for (int j = 0; j < side; j++)
                    dirs[i * side + j] = Offset(p, e1, e2, (i - half) * step, (j - half) * step);
            var f = power(dirs);
            int best = 0;
            for (int d = 1; d < dirs.Length; d++) if (f.Power(d) > f.Power(best)) best = d;
            int bi = best / side, bj = best % side;
            bool edge = bi == 0 || bj == 0 || bi == side - 1 || bj == side - 1;
            if (edge && move < moves)
            {
                p = dirs[best];
                continue;
            }
            double da = bi > 0 && bi < side - 1 ? Vertex(f.Power(best - side), f.Power(best), f.Power(best + side)) : 0;
            double db = bj > 0 && bj < side - 1 ? Vertex(f.Power(best - 1), f.Power(best), f.Power(best + 1)) : 0;
            return Offset(p, e1, e2, (bi - half + da) * step, (bj - half + db) * step);
        }
        return p;
    }

    /// <summary>The offset of a parabola's vertex from the middle of three equally spaced samples, in steps, within ±½.</summary>
    private static double Vertex(double left, double mid, double right)
    {
        double den = left - 2 * mid + right;
        return den < 0 ? Math.Clamp(0.5 * (left - right) / den, -0.5, 0.5) : 0;
    }

    private static (RawCut Elevation, RawCut Azimuth) Cuts(Power power, (double X, double Y, double Z) p, double phiRef, double stepDeg)
    {
        var (e1, e2) = Tangents(p, phiRef);
        return (Cut(power, p, e1, stepDeg, "elevation"), Cut(power, p, e2, stepDeg, "azimuth"));
    }

    /// <summary>The great circle through <paramref name="p"/> along <paramref name="tangent"/>, from −180° to 180° at the step.</summary>
    private static RawCut Cut(Power power, (double X, double Y, double Z) p, (double X, double Y, double Z) tangent, double stepDeg, string plane)
    {
        int half = (int)Math.Floor(180 / stepDeg);
        var offsets = new double[2 * half + 1];
        var dirs = new (double, double, double)[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = (i - half) * stepDeg;
            var (s, c) = Math.SinCos(offsets[i] * Math.PI / 180);
            dirs[i] = (c * p.X + s * tangent.X, c * p.Y + s * tangent.Y, c * p.Z + s * tangent.Z);
        }
        var f = power(dirs);
        return new RawCut(plane, offsets, [.. Enumerable.Range(0, offsets.Length).Select(f.Power)], tangent);
    }

    /// <summary>The half-power width of a cut about its middle sample, crossings interpolated linearly in dB; 360° when none is found.</summary>
    private static double Beamwidth(RawCut cut)
    {
        int m = cut.OffsetDeg.Length / 2;
        double half = cut.Power[m] / 2;
        double? Cross(int dir)
        {
            for (int i = m; i + dir >= 0 && i + dir < cut.Power.Length; i += dir)
            {
                double a = cut.Power[i], b = cut.Power[i + dir];
                if (b < half)
                {
                    double la = 10 * Math.Log10(a / half), lb = b > 0 ? 10 * Math.Log10(b / half) : -400;
                    double f = la / (la - lb);
                    return cut.OffsetDeg[i] + f * (cut.OffsetDeg[i + dir] - cut.OffsetDeg[i]);
                }
            }
            return null;
        }
        return Cross(+1) is { } r && Cross(-1) is { } l ? r - l : 360;
    }

    /// <summary>The first sidelobe of a cut over the peak, dB: beyond the half-power crossing on each side, the first minimum and then
    /// the first maximum, re-sampled ten times finer around it. The higher side; null when neither side has one.</summary>
    private static double? Sidelobe(Power power, (double X, double Y, double Z) p, double phiRef, RawCut cut, bool elevation, double stepDeg)
    {
        int m = cut.OffsetDeg.Length / 2;
        double peak = cut.Power[m];
        double? best = null;
        foreach (int dir in (int[])[+1, -1])
        {
            int i = m;
            int Last() => dir > 0 ? cut.Power.Length - 1 : 0;
            while (i != Last() && cut.Power[i] >= peak / 2) i += dir;
            while (i != Last() && cut.Power[i + dir] <= cut.Power[i]) i += dir;      // down to the first null
            if (i == Last() || cut.Power[i + dir] <= 0) continue;
            while (i != Last() && cut.Power[i + dir] > cut.Power[i]) i += dir;       // up to the first sidelobe
            if (i == Last() || cut.Power[i] <= 0) continue;

            // Ten times finer across the samples either side of it, then the parabola.
            const int Fine = 10;
            var dirs = new (double, double, double)[2 * Fine + 1];
            var t = cut.Tangent;
            for (int k = 0; k < dirs.Length; k++)
            {
                double off = (cut.OffsetDeg[i] + (k - Fine) * stepDeg / Fine) * Math.PI / 180;
                var (s, c) = Math.SinCos(off);
                dirs[k] = (c * p.X + s * t.Item1, c * p.Y + s * t.Item2, c * p.Z + s * t.Item3);
            }
            var f = power(dirs);
            int top = 0;
            for (int k = 1; k < dirs.Length; k++) if (f.Power(k) > f.Power(top)) top = k;
            double level = f.Power(top);
            if (top > 0 && top < dirs.Length - 1)
            {
                double l = Db(f.Power(top - 1)), c0 = Db(level), r = Db(f.Power(top + 1));
                double v = Vertex(l, c0, r);
                level = Math.Pow(10, (c0 - 0.25 * (l - r) * v) / 10);
            }
            double db = 10 * Math.Log10(level / peak);
            best = best is null ? db : Math.Max(best.Value, db);
        }
        return best;
    }

    private static double Db(double power) => power > 0 ? 10 * Math.Log10(power) : -400;

    /// <summary>
    /// R-paa2-7 — the grating lobe nearest the visible circle at the scan (θ₀, φ₀), from the lattice's pitch and the frequency alone:
    /// the lobes sit at û₀ + p·b₁ + q·b₂ in direction cosines, with b the reciprocal lattice in units of λ. The margin is the nearest
    /// one's distance outside the unit circle; negative when a lobe is in real space. Infinite for one element.
    /// </summary>
    public static ArrayGratingLobe GratingLobe(ArrayLattice lattice, double hz, double theta0Deg, double phi0Deg)
    {
        double lambda = Mom.EmConstants.C0 / hz;
        var (u0, v0, _) = ArrayDirections.Vector(theta0Deg * Math.PI / 180, phi0Deg * Math.PI / 180);
        bool alongX = lattice.Cols > 1 || (lattice.Kind == ArrayLatticeKind.Tri && lattice.Rows > 1);
        bool alongY = lattice.Rows > 1;
        if (!alongX && !alongY) return new ArrayGratingLobe(double.PositiveInfinity, double.NaN, double.NaN);
        double a1x = lattice.Dx, a1y = 0;
        double a2x = lattice.Kind == ArrayLatticeKind.Tri ? lattice.Dx / 2 : 0, a2y = lattice.Dy;
        double det = a1x * a2y - a1y * a2x;
        double b1x, b1y, b2x, b2y;
        if (alongX && alongY) { b1x = lambda * a2y / det; b1y = -lambda * a2x / det; b2x = -lambda * a1y / det; b2y = lambda * a1x / det; }
        else if (alongX) { b1x = lambda / lattice.Dx; b1y = 0; b2x = b2y = 0; }
        else { b1x = b1y = 0; b2x = 0; b2y = lambda / lattice.Dy; }
        var best = new ArrayGratingLobe(double.PositiveInfinity, double.NaN, double.NaN);
        for (int pp = -4; pp <= 4; pp++)
            for (int q = -4; q <= 4; q++)
            {
                if ((pp == 0 && q == 0) || (!alongX && pp != 0) || (!alongY && q != 0)) continue;
                double u = u0 + pp * b1x + q * b2x, v = v0 + pp * b1y + q * b2y;
                double margin = Math.Sqrt(u * u + v * v) - 1;
                if (margin < best.Margin) best = new ArrayGratingLobe(margin, u, v);
            }
        return best;
    }

    /// <summary>R-paa2-7 — scan loss, dB: how much less peak power per watt incident the scanned beam has than the boresight one.</summary>
    public static double ScanLossDb(double peakPowerAtScan, double incidentAtScan, double peakPowerAtBoresight, double incidentAtBoresight)
        => 10 * Math.Log10(peakPowerAtBoresight / incidentAtBoresight / (peakPowerAtScan / incidentAtScan));

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
