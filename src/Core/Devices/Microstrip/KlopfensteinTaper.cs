namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// The Klopfenstein taper impedance profile (brief-mtaper-mklopf.md §2) — the taper's DEFINITION,
/// independent of microstrip (it is a pure impedance-taper synthesis result); the microstrip-
/// specific pieces (width synthesis, arc-length distribution) live in
/// <see cref="MicrostripKlopfModel"/> and <see cref="MicrostripOffsetCenterline"/>.
///
/// <b>Sources, and how a real cross-source discrepancy was resolved (R1/R14 — do not simplify
/// this away):</b>
/// <list type="bullet">
/// <item><b>φ(w,A) — Grossberg's rapid series</b> (M. A. Grossberg, <i>Extremely rapid computation
/// of the Klopfenstein impedance taper</i>, Proc. IEEE 56(9), 1629-1630, Sept. 1968). Confirmed
/// byte-for-byte identical between TWO independent reproductions: M. Steer, <i>Microwave and RF
/// Design: Networks</i> (3rd ed., 2019, open-access via LibreTexts), eqs (13)-(14); and the
/// independent reference implementation at github.com/ZiadHatab/klopf-taper (BSD-3, commit
/// <c>4b6fa1778b0c5df07d3088650c7952aac11c8f00</c>, 2026-05-03) — used here strictly as a
/// validation oracle per R-klp-1, never as a source of code.</item>
/// <item><b>A — a genuine discrepancy, resolved by direct derivation, not by picking a source.</b>
/// Steer's own eq (11), as digitized, reads <c>A = cosh⁻¹[ln(Z2/Z1)/Γm]</c> (no factor of 2) — but
/// Steer's OWN preceding sentence states the refined ρ₀ estimate is <c>ρ₀ ≈ ½·ln(Z2/Z1)</c>, and
/// Klopfenstein's own relation is <c>A = cosh⁻¹(ρ₀/Γm)</c>. Substituting gives
/// <c>A = cosh⁻¹[ln(Z2/Z1)/(2·Γm)]</c> — WITH the factor of 2 — which is exactly what the
/// independent klopf-taper reference computes (<c>G0 = log(Z2/Z1)/2; A = arccosh(abs(G0/Gmax))</c>).
/// Steer's own digitized eq (11) is therefore treated as carrying a dropped factor of 2 and is NOT
/// followed literally; <see cref="ComputeA"/> uses the algebraically-derived, oracle-confirmed
/// form. This is recorded here, not silently corrected, because it is exactly the kind of error R1
/// exists to catch.</item>
/// <item><b>The endpoint (Kajfez-Prewitt) correction — verified numerically, not assumed.</b> D.
/// Kajfez and J. O. Prewitt's correction (IEEE Trans. MTT 21(5), p. 364, May 1973) is reported
/// elsewhere as adding an extra "-1" term inside the profile's bracket. Both Steer's and
/// klopf-taper's modern reproductions already include it (a `+U(z-ℓ/2)+U(z+ℓ/2)-1` /
/// `heaviside(x-L/2,1)-heaviside(-x-L/2,1)` term respectively — algebraically equivalent away from
/// the exact endpoints). Confirmed directly by evaluating BOTH forms at the taper's own physical
/// endpoints (Z1=50Ω, Z2=120Ω): WITH the "-1" term the profile reproduces Z1/Z2 exactly
/// (50.0/120.0); WITHOUT it, the profile overshoots both ends (53.26/123.86) — reproducing exactly
/// the "fails to meet the end values... when the transformation ratio is large" symptom the
/// correction is reported to fix. <see cref="ImpedanceAt"/> always includes this term —
/// <see cref="MicrostripKlopfModelTests"/> pins the without-it regression directly.</item>
/// <item><b>Length ↔ 3dB-cutoff duality — the oracle's OWN two halves disagree, and this class
/// picks one deliberately rather than silently reproducing whichever the caller happens to hit
/// first.</b> The closed form (<c>L = c·√(arccos(0.5)²+A²) / (2π·√εeff·f3dB)</c>) is taken from
/// the klopf-taper oracle's <c>klopf_l2f</c>/<c>klopf_f2l</c> — but those two functions compute
/// <c>A</c> from the EXACT ρ₀ = (Z2−Z1)/(Z2+Z1), while the SAME repository's own <c>klopf()</c>
/// impedance-profile function computes <c>A</c> from the refined SMALL-REFLECTION estimate
/// ρ₀ ≈ ½·ln(Z2/Z1) — an internal inconsistency confirmed directly (running both with the same
/// Z1=50Ω/Z2=120Ω/Γm=−30dB gives A=3.2582 the first way, A=3.3196 the second; the oracle's own
/// f3dB output, 23.5695 GHz, reproduces ONLY the exact-ρ₀ form bit-for-bit). ½·ln(Z2/Z1) is not
/// merely "a cruder approximation to" (Z2−Z1)/(Z2+Z1) — it is the theoretically exact
/// accumulated small-reflection integral for a GRADUALLY tapered line (a WKB-type result), whereas
/// (Z2−Z1)/(Z2+Z1) is the reflection at an ABRUPT single-step junction; Klopfenstein's continuous
/// taper is the former case, not the latter, so the refined estimate is the more physically
/// appropriate quantity for a taper's own <c>A</c> — and using it consistently everywhere in this
/// class (profile AND length duality) is the internally-consistent choice, rather than switching
/// definitions of "Γmax" depending on which formula is being evaluated. This means
/// <see cref="LengthFromF3db"/>/<see cref="F3dbFromLength"/> do NOT reproduce the oracle's own
/// <c>klopf_l2f</c>/<c>klopf_f2l</c> output bit-for-bit (they differ by ≈1.7% at the worked example
/// above) — a recorded, deliberate divergence, not an unnoticed one.</item>
/// </list>
/// </summary>
public static class KlopfensteinTaper
{
    private const double SpeedOfLight = 2.99792458e8;

    /// <summary>Grossberg's rapid series for φ(w,A), <c>w∈[-1,1]</c> (40 terms — the oracle's own
    /// default; Steer states 20 is already sufficient).</summary>
    public static double Phi(double w, double a, int terms = 40)
    {
        double an = 1.0, bn = w / 2.0, total = an * bn;
        for (int n = 1; n < terms; n++)
        {
            an = a * a * an / (4.0 * n * (n + 1));
            bn = (w / 2.0 * Math.Pow(1.0 - w * w, n) + 2.0 * n * bn) / (2.0 * n + 1.0);
            total += an * bn;
        }
        return total;
    }

    /// <summary>The EXACT direct-junction reflection coefficient — used only for R-klp-2's
    /// degeneracy guard, never for the profile itself (which uses <see cref="Rho0Estimate"/>).</summary>
    public static double Rho0Exact(double z1, double z2) => (z2 - z1) / (z2 + z1);

    /// <summary>Klopfenstein's own small-reflection refinement to ρ₀ (Steer: "a better estimate is
    /// ρ₀ = ½·ln(Z2/Z1)") — this is what actually feeds <see cref="ComputeA"/> and the profile.</summary>
    public static double Rho0Estimate(double z1, double z2) => 0.5 * Math.Log(z2 / z1);

    /// <summary>R-klp-2: Γmax must be strictly less than the taper's own exact direct-junction
    /// reflection magnitude, or the design is degenerate (the profile would need |S11| &gt; 1 in
    /// the passband). Throws, naming the bound, rather than returning a plausible-looking value.</summary>
    public static void ValidateGammaMax(double z1, double z2, double gammaMax)
    {
        double bound = Math.Abs(Rho0Exact(z1, z2));
        if (gammaMax >= bound)
            throw new ArgumentException(
                $"Klopfenstein taper: GammaMax={gammaMax:G6} must be strictly less than " +
                $"|(Z2-Z1)/(Z2+Z1)|={bound:G6} for Z1={z1:G6}, Z2={z2:G6}.");
    }

    /// <summary>Klopfenstein's own shape parameter A, per this class's own doc comment on the
    /// factor-of-2 resolution.</summary>
    public static double ComputeA(double z1, double z2, double gammaMax)
        => Math.Acosh(Math.Abs(Rho0Estimate(z1, z2) / gammaMax));

    /// <summary>
    /// The impedance profile at normalized position <paramref name="t"/>∈[0,1] along the taper's
    /// own ARC-length coordinate (R-klp-6 — the caller is responsible for mapping physical arc
    /// position to this normalized t; this function itself is purely the Klopfenstein shape).
    /// <c>t=0 → Z1</c>, <c>t=1 → Z2</c>, exactly (the Kajfez-Prewitt endpoint term, always applied
    /// — R-klp-4: the model NEVER smooths these, regardless of the artwork's own SmoothSteps).
    /// </summary>
    public static double ImpedanceAt(double t, double z1, double z2, double gammaMax)
    {
        double a = ComputeA(z1, z2, gammaMax);
        double x = t - 0.5;         // map [0,1] -> [-1/2,1/2] (Steer/oracle's own centered coordinate)
        double w = 2.0 * x;         // w = 2z/length, length normalized to 1
        bool rightEdge = x >= 0.5;
        bool leftEdge = x <= -0.5;
        double endpointCorrection = (rightEdge ? 1.0 : 0.0) - (leftEdge ? 1.0 : 0.0);

        double rho0Est = Rho0Estimate(z1, z2);
        double lnZ = Math.Log(z1 * z2) / 2.0
            + rho0Est / Math.Cosh(a) * (a * a * Phi(w, a) + endpointCorrection);
        return Math.Exp(lnZ);
    }

    /// <summary>R-klp-3: taper length for a given 3 dB cutoff frequency (oracle-sourced duality,
    /// see this class's own doc comment). <paramref name="eeff"/> is the effective permittivity the
    /// electrical length is measured against.</summary>
    public static double LengthFromF3db(double z1, double z2, double gammaMax, double f3dbHz, double eeff)
    {
        double a = ComputeA(z1, z2, gammaMax);
        double k = Math.Sqrt(Math.Acos(0.5) * Math.Acos(0.5) + a * a);
        return SpeedOfLight * k / (2.0 * Math.PI * Math.Sqrt(eeff) * f3dbHz);
    }

    /// <summary>The inverse of <see cref="LengthFromF3db"/> — the taper's own 3 dB cutoff for a
    /// given physical length. Exact algebraic inverse, so <c>L → f3dB → L</c> round-trips to
    /// floating-point precision (R-klp-3's own "the two invert consistently" gate).</summary>
    public static double F3dbFromLength(double z1, double z2, double gammaMax, double lengthMeters, double eeff)
    {
        double a = ComputeA(z1, z2, gammaMax);
        double k = Math.Sqrt(Math.Acos(0.5) * Math.Acos(0.5) + a * a);
        return SpeedOfLight * k / (2.0 * Math.PI * Math.Sqrt(eeff) * lengthMeters);
    }
}
