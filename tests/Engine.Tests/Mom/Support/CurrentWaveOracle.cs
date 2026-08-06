// L8d Tier 1 — γ straight off the travelling wave, with NO calibration in the path.
//
// D3's standing rule in this area, for the fifth time (kernel A's meshed ground plate, L7b-b's
// closed-form 2×2 eigen-decomposition, L8a's Sommerfeld contour, L8c's cross-correlation): the
// oracle must share no algebra with the thing it checks. This one shares none with the two-line
// extraction — no T-matrix, no error box, no calibration standard, not even a second solve.
//
// On a uniform line the total current obeys I(z) = I⁺e^{−γz} + I⁻e^{+γz}. Any such sequence sampled
// on a UNIFORM pitch Δz satisfies the two-term recurrence
//
//     I_{k−1} + I_{k+1} = 2·cosh(γΔz)·I_k
//
// identically — it is the characteristic equation of the recurrence, and it holds whatever the two
// wave amplitudes are, so the standing wave from an unmatched far end is not a nuisance here but the
// signal itself. Three consecutive currents therefore give γ in CLOSED FORM.
//
// MEASURED, AND IT CHANGED THE IMPLEMENTATION: taking γ triple by triple and averaging is the
// obvious way and it is badly conditioned, because the delta-gap port leaves a strong standing wave
// and every triple straddling a current NULL divides by a near-zero I_k. On the 20 mm FR-4 fixture
// at 10 GHz the per-triple β reads 402 ± 2 over most of the line and −467 / −376 at the two nulls.
// Solving the SAME recurrence in least squares over every station instead — w = Σ Ī_k(I_{k−1}+I_{k+1})
// / (2Σ|I_k|²) — weights each station by |I_k|² and is immune to them, while remaining exact for a
// genuine two-wave sum. The residual of that fit is the honest scatter measure and is reported.
//
// The triples must lie in the line's uniform middle: near either end the evanescent port field is
// still present and I(z) is not a two-wave sum there. TrimEnds is that exclusion, and it is stated
// as a number rather than hidden.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;

namespace CircuitRF.Engine.Tests.Mom.Support;

public static class CurrentWaveOracle
{
    public sealed record Result(Complex Gamma, double ResidualRel, int Stations, double PitchM)
    {
        public double Alpha => Gamma.Real;
        public double Beta  => Gamma.Imaginary;

        /// <summary>ε_eff implied by β at this frequency — the number kernel A also publishes.</summary>
        public double EffectivePermittivity(double fHz)
        {
            double b = Beta / (2.0 * Math.PI * fHz / EmConstants.C0);
            return b * b;
        }
    }

    /// <summary>
    /// γ from the current distribution of an already-solved line. <paramref name="trimEnds"/> is how
    /// many longitudinal stations to discard at each end before fitting.
    /// </summary>
    public static Result Extract(PlanarMesh mesh, Vec<Complex> currents, PlanarPortResolution port,
                                 int trimEnds = 2)
    {
        var line = PlanarExcitation.LineCurrent(
            mesh, currents, port.Direction, port.TransverseLines[0], port.TransverseLines[^1]);


        // The recurrence needs a UNIFORM pitch, and the SHIPPING mesh does not have one everywhere:
        // R-msh-5's edge grading makes the first and last few cells geometric. So find the longest
        // uniform run rather than assuming the middle is uniform after a fixed trim — that keeps the
        // oracle usable on the mesh a user actually gets, which is where every physics number in
        // this slice is taken.
        var cut = new double[line.Count];
        for (int i = 0; i < line.Count; i++) cut[i] = line[i].Coord;

        int lo = 0, hi = 0, bestLo = 0, bestHi = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            double p = cut[i + 1] - cut[i];
            if (i > lo && Math.Abs(p - (cut[lo + 1] - cut[lo])) > 1e-9 * p) lo = i;
            hi = i + 1;
            if (hi - lo > bestHi - bestLo) { bestLo = lo; bestHi = hi; }
        }

        int first = bestLo + trimEnds;
        int last  = bestHi - trimEnds;
        if (last - first < 2)
            throw new InvalidOperationException(
                $"The longest uniform run holds {bestHi - bestLo + 1} station(s), which leaves no " +
                $"triple after trimming {trimEnds} at each end. Lengthen the line, coarsen the mesh, " +
                "or turn the edge mesh off for this fixture.");

        double pitch = cut[bestLo + 1] - cut[bestLo];

        Complex num = Complex.Zero;
        double  den = 0;
        int     n   = 0;
        for (int k = first + 1; k < last; k++)
        {
            Complex ik = line[k].I;
            num += Complex.Conjugate(ik) * (line[k - 1].I + line[k + 1].I);
            den += ik.Magnitude * ik.Magnitude;
            n++;
        }

        Complex w = num / (2.0 * den);

        double res = 0, scale = 0;
        for (int k = first + 1; k < last; k++)
        {
            Complex a = line[k - 1].I + line[k + 1].I;
            Complex d = a - 2.0 * w * line[k].I;
            res   += d.Magnitude * d.Magnitude;
            scale += a.Magnitude * a.Magnitude;
        }
        double residual = Math.Sqrt(res / scale);

        Complex g = Acosh(w) / pitch;
        if (g.Real < 0) g = -g;                           // passive: Re γ ≥ 0
        if (g.Imaginary < 0) g = new Complex(g.Real, -g.Imaginary);

        return new Result(g, residual, n, pitch);
    }

    /// <summary>acosh for complex argument — principal branch, written rather than depended on.</summary>
    public static Complex Acosh(Complex w) =>
        Complex.Log(w + Complex.Sqrt(w * w - Complex.One));
}
