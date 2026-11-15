// ================================================================
//  SimpleModels.cs  —  free space, log-distance, multi-slope, two-ray
//
//  free        ITU-R P.525: Lbf = 20·log10(4πd/λ), d the 3D distance.
//  logdist     free space to the reference distance d0, then exponent n:
//                L = Lbf(d0) + 10·n·log10(d/d0)            d ≥ d0
//              and free space below d0, so the curve is continuous.
//  multislope  the same with breakpoints b1 < b2 < …, an exponent per
//              segment (n1 from d0 to b1, n2 from b1 to b2, …), each
//              segment starting where the last ended: continuous at every
//              breakpoint by construction.
//  tworay      the direct ray plus one reflected from a flat ground of
//              stated permittivity and conductivity, summed as fields:
//                E ∝ e^(−jk·r1)/r1 + Γ(ψ)·e^(−jk·r2)/r2
//                L = −20·log10(λ/(4π) · |e^(−jk·r1)/r1 + Γ·e^(−jk·r2)/r2|)
//              with r1, r2 the direct and reflected path lengths, ψ the
//              grazing angle, and Γ the Fresnel coefficient of the ground
//              for the stated polarization (complex permittivity
//              εc = εr − j·σ/(ω·ε0)). Isotropic antennas.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RfCore.Propagation
{
    public static class FreeSpaceModel
    {
        public const string Document = "ITU-R P.525-5 (11/2024)";

        /// <summary>Free-space basic transmission loss over the 3D distance between the antennas.</summary>
        public static PathLossResult Evaluate(double frequencyHz, double distance3DM)
        {
            PropagationMath.Positive("free", "the frequency", frequencyHz, "Hz");
            PropagationMath.Positive("free", "the distance", distance3DM, "m");
            return PathLossResult.Single("free", Document, "free space", PropagationMath.FreeSpaceDb(frequencyHz, distance3DM));
        }
    }

    public static class LogDistanceModel
    {
        public const string Document = "log-distance: free space to d0, then a stated exponent per segment";

        /// <summary>Log-distance with one exponent beyond d0 (no breakpoints).</summary>
        public static PathLossResult LogDistance(double frequencyHz, double distance3DM, double exponent, double d0M, double sigmaDb)
            => MultiSlope("logdist", frequencyHz, distance3DM, d0M, Array.Empty<double>(), new[] { exponent }, sigmaDb);

        /// <summary>Free space to <paramref name="d0M"/>, then exponents[k] from breaks[k−1] (or d0) to breaks[k].
        /// exponents has one more entry than breaks.</summary>
        public static PathLossResult MultiSlope(string model, double frequencyHz, double distance3DM, double d0M,
                                                IReadOnlyList<double> breaks, IReadOnlyList<double> exponents, double sigmaDb)
        {
            PropagationMath.Positive(model, "the frequency", frequencyHz, "Hz");
            PropagationMath.Positive(model, "the distance", distance3DM, "m");
            PropagationMath.Positive(model, "the reference distance d0", d0M, "m");
            if (sigmaDb < 0) throw new PropagationRefusal($"{model}: the shadowing σ must not be negative; it is {PropagationMath.F(sigmaDb)} dB.");
            if (exponents.Count != breaks.Count + 1)
                throw new PropagationRefusal(
                    $"{model}: {breaks.Count} breakpoint(s) need {breaks.Count + 1} exponents (one per segment); {exponents.Count} were given.");
            double prev = d0M;
            foreach (double b in breaks)
            {
                if (!(b > prev))
                    throw new PropagationRefusal(
                        $"{model}: breakpoints must increase from d0 = {PropagationMath.F(d0M)} m; {PropagationMath.F(b)} m follows {PropagationMath.F(prev)} m.");
                prev = b;
            }
            foreach (double n in exponents)
                if (!(n > 0)) throw new PropagationRefusal($"{model}: every exponent must be greater than 0; one is {PropagationMath.F(n)}.");

            if (distance3DM <= d0M)
                return new PathLossResult(model, Document, PropagationMath.FreeSpaceDb(frequencyHz, distance3DM),
                    new[] { new LossTerm("free space", PropagationMath.FreeSpaceDb(frequencyHz, distance3DM)) }, sigmaDb);

            double l0 = PropagationMath.FreeSpaceDb(frequencyHz, d0M);
            double excess = 0, start = d0M;
            for (int k = 0; k < exponents.Count && start < distance3DM; k++)
            {
                double end = k < breaks.Count ? Math.Min(breaks[k], distance3DM) : distance3DM;
                excess += 10 * exponents[k] * Math.Log10(end / start);
                start = end;
            }
            double fsAtD = PropagationMath.FreeSpaceDb(frequencyHz, distance3DM);
            double total = l0 + excess;
            return new PathLossResult(model, Document, total,
                new[] { new LossTerm("free space", fsAtD), new LossTerm("excess over free space", total - fsAtD) }, sigmaDb);
        }
    }

    public static class TwoRayModel
    {
        public const string Document = "two-ray: direct and flat-ground reflected rays, Fresnel reflection coefficient";

        public enum Polarization { Vertical, Horizontal }

        /// <summary>The Fresnel reflection coefficient of a flat ground at grazing angle ψ (rad).</summary>
        public static Complex Reflection(double frequencyHz, double grazingRad, double epsR, double sigmaSPerM, Polarization pol)
        {
            const double eps0 = 8.8541878128e-12;
            var ec = new Complex(epsR, -sigmaSPerM / (2 * Math.PI * frequencyHz * eps0));
            double s = Math.Sin(grazingRad), c = Math.Cos(grazingRad);
            Complex root = Complex.Sqrt(ec - c * c);
            return pol == Polarization.Horizontal
                ? (s - root) / (s + root)
                : (ec * s - root) / (ec * s + root);
        }

        /// <param name="distance2DM">The horizontal distance between the antennas, m.</param>
        public static PathLossResult Evaluate(double frequencyHz, double distance2DM, double txHeightM, double rxHeightM,
                                              double epsR, double sigmaSPerM, Polarization pol)
        {
            PropagationMath.Positive("tworay", "the frequency", frequencyHz, "Hz");
            PropagationMath.Positive("tworay", "the distance", distance2DM, "m");
            PropagationMath.Positive("tworay", "the transmit height", txHeightM, "m");
            PropagationMath.Positive("tworay", "the receive height", rxHeightM, "m");
            if (!(epsR >= 1)) throw new PropagationRefusal($"tworay: the ground's relative permittivity must be at least 1; it is {PropagationMath.F(epsR)}.");
            if (sigmaSPerM < 0) throw new PropagationRefusal($"tworay: the ground's conductivity must not be negative; it is {PropagationMath.F(sigmaSPerM)} S/m.");

            double lambda = PropagationMath.C / frequencyHz, k = 2 * Math.PI / lambda;
            double r1 = Math.Sqrt(distance2DM * distance2DM + Math.Pow(txHeightM - rxHeightM, 2));
            double r2 = Math.Sqrt(distance2DM * distance2DM + Math.Pow(txHeightM + rxHeightM, 2));
            double psi = Math.Atan2(txHeightM + rxHeightM, distance2DM);
            Complex gamma = Reflection(frequencyHz, psi, epsR, sigmaSPerM, pol);
            Complex sum = Complex.FromPolarCoordinates(1 / r1, -k * r1) + gamma * Complex.FromPolarCoordinates(1 / r2, -k * r2);
            double total = -20 * Math.Log10(lambda / (4 * Math.PI) * sum.Magnitude);
            double fs = PropagationMath.FreeSpaceDb(frequencyHz, r1);
            return new PathLossResult("tworay", Document, total,
                new[] { new LossTerm("free space", fs), new LossTerm("ground reflection", total - fs) }, 0);
        }
    }
}
