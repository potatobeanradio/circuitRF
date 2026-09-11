// ANT-11 / R-fg-5 — IS UTD THE ASYMPTOTIC METHOD THE BRIEF BUDGETED ITS VALIDITY RANGE FOR?
//
// `brief-antenna-11` §3.2 says: "UTD is an asymptotic high-frequency method; on a ground plane of a
// fraction of a wavelength it degrades, and the measured board is exactly there at 0.40 λ₀. Find where
// it breaks and refuse past it rather than returning a plausible curve."
//
// **This file is that measurement, and its answer is that the premise is false.** For a straight
// perfectly-conducting edge — exterior wedge angle 2π, which is what the rim of a thin ground plane is
// — the Kouyoumjian-Pathak coefficient WITH its transition function is not an asymptotic
// approximation to Sommerfeld's exact half-plane solution. It IS that solution, rewritten. It agrees to
// round-off at every k·ρ measured, down to 0.2.
//
// Why measure something ANT-11 then refused to build (PlanarFiniteGround, R-fg-4)? Because the
// constant `PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement` is quoted in a shipped refusal's
// reasoning and in `src/Engine/Mom/CLAUDE.md`, and an unbacked constant is a memory rather than a
// measurement. Concretely it stops one specific wrong move: a later phase writing a `k·ρ` validity
// refusal for the diffraction COEFFICIENT, which would be a refusal against nothing, and spending its
// accuracy budget there instead of on the two limits that do bind (the locality assumption and
// multiple rim-to-rim diffraction — neither measured here, and RESOLVED.md §ANT-11 says so).
//
// ── WHAT IS INDEPENDENT HERE, AND WHAT IS DELIBERATELY SHARED ─────────────────────────────────
//
// The two sides share ONE thing: `Fresnel.FromZero`, a numerical evaluation of ∫₀^x e^{−jτ²}dτ. That
// is deliberate and it is stated rather than hidden. The quantity under test is the UTD ANGULAR
// construction — the cot pairs, the a^± arguments, the N^± integers and the ∓ polarization sign — and
// a Fresnel integral is the same mathematical object on both sides of the comparison, so writing it
// twice would test arithmetic nobody doubts while leaving the construction untested either way.
// **Because it is a common factor it is gated on its own first**, against three independent closed
// forms (its value at 0, its limit ∫₀^∞ = (√π/2)e^{−jπ/4}, and its large-argument asymptote), so it
// cannot silently shift both sides together.
//
// Nothing in this file touches `CircuitRF.Engine` except to read that one constant. There is no UTD
// implementation in the engine for it to agree with — which is the point.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class UtdHalfPlaneMeasurementTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The shared common factor, gated first.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static class Fresnel
    {
        /// <summary>(√π/2)·e^{−jπ/4} — the exact value of ∫₀^∞ e^{−jτ²}dτ.</summary>
        public static readonly Complex ToInfinity =
            Math.Sqrt(Math.PI) / 2.0 * Complex.Exp(-Complex.ImaginaryOne * Math.PI / 4.0);

        /// <summary>
        /// ∫₀^x e^{−jτ²}dτ by panelled Gauss-Legendre. The panel count grows with x² because that is
        /// the integrand's phase: e^{−jτ²} turns through x² radians over [0, x], so a fixed rule would
        /// silently lose the oscillation exactly where the transition function needs it.
        /// </summary>
        public static Complex FromZero(double x)
        {
            if (x == 0) return Complex.Zero;
            bool neg = x < 0;
            double a = Math.Abs(x);
            int panels = Math.Max(8, (int)(a * a / 1.5) + 8);
            var (gx, gw) = Quadrature.Nodes(20);
            double h = a / panels;
            Complex sum = Complex.Zero;
            for (int p = 0; p < panels; p++)
            {
                double mid = p * h + h / 2, half = h / 2;
                for (int i = 0; i < gx.Length; i++)
                {
                    double t = mid + half * gx[i];
                    sum += gw[i] * half * Complex.Exp(-Complex.ImaginaryOne * t * t);
                }
            }
            // Odd function of x, which is what makes the U(ξ) below correct for ξ < 0 too.
            return neg ? -sum : sum;
        }
    }

    /// <summary>
    /// The one quantity both sides read is checked against three closed forms it cannot be tuned to
    /// satisfy at once — the value at the origin, the exact limit, and the leading asymptote.
    /// </summary>
    [Fact]
    public void TheSharedFresnelIntegral_AgreesWithThreeIndependentClosedForms()
    {
        Assert.Equal(Complex.Zero, Fresnel.FromZero(0));

        // ∫₀^x + ∫_x^∞ must recover the exact limit. Written this way rather than as "∫₀^x → the
        // limit", which is what a first draft asserted and is FALSE at any x a test can afford: the
        // remaining tail is of size 1/(2x), so ∫₀^400 sits 1.25e-3 from the limit and reads as a
        // broken quadrature. Adding the tail's own asymptote back is the check that actually bounds
        // the quadrature's error, and it bounds it three orders tighter.
        foreach (double x in new[] { 20.0, 40.0, 60.0 })
        {
            var tailAsym = Complex.Exp(-Complex.ImaginaryOne * x * x)
                           / (2.0 * Complex.ImaginaryOne * x);
            double gap = (Fresnel.FromZero(x) + tailAsym - Fresnel.ToInfinity).Magnitude;
            _out.WriteLine($"  x = {x,5:F1}:  |∫₀^x + tail_asym − ∫₀^∞| = {gap:E3}  " +
                           $"(next asymptotic order ≈ {0.5 / (x * x * x):E3})");
            // The residue is the asymptote's OWN next order, 1/(4x³), not the quadrature's error.
            Assert.True(gap < 5.0 / (x * x * x), $"x = {x}: gap {gap:E3}");
        }

        // ∫_a^∞ e^{−jτ²}dτ ≈ e^{−ja²}/(2ja) for large a — the asymptote the UTD transition function's
        // F(X) → 1 limit rests on. Its own error is the next order, O(1/a³) against an O(1/a) term,
        // i.e. relative O(1/a²).
        foreach (double a in new[] { 6.0, 12.0, 25.0 })
        {
            var tail = Fresnel.ToInfinity - Fresnel.FromZero(a);
            var asym = Complex.Exp(-Complex.ImaginaryOne * a * a) / (2.0 * Complex.ImaginaryOne * a);
            double rel = (tail - asym).Magnitude / asym.Magnitude;
            _out.WriteLine($"  a = {a,5:F1}:  tail = {tail}, asymptote = {asym}, rel = {rel:E3}");
            Assert.True(rel < 2.0 / (a * a), $"a = {a}: tail vs asymptote {rel:E3}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Side A — SOMMERFELD'S EXACT HALF-PLANE SOLUTION.
    //
    // PEC half-plane, edge along ẑ at the origin, screen occupying φ = 0 (and equivalently φ = 2π),
    // so the angular domain is 0 < φ < 2π and the exterior wedge angle is 2π (n = 2). Plane-wave
    // incidence from azimuth φ′, normal to the edge, in this repository's e^{jωt} / e^{−jkR}
    // convention:
    //
    //     u_inc = e^{+jkρ cos(φ − φ′)}          — φ′ is the direction the SOURCE is in
    //     u     = U(ξ⁻)·e^{+jkρcos(φ−φ′)}  ∓  U(ξ⁺)·e^{+jkρcos(φ+φ′)}
    //     ξ^∓   = √(2kρ)·cos((φ ∓ φ′)/2)
    //     U(ξ)  = (e^{jπ/4}/√π)∫_{−∞}^{ξ} e^{−jτ²}dτ        (→ 0 at −∞, → 1 at +∞)
    //
    // with the upper sign (−) for SOFT (Dirichlet, E ∥ edge) and the lower (+) for HARD (Neumann).
    //
    // **The sign of the incident exponent is the part that is easy to get wrong and hard to notice.**
    // Written with e^{−jkρcos(φ−φ′)} instead, the solution still satisfies the boundary condition
    // exactly and still reduces to incident-plus-reflected in the lit region — both checks pass — but
    // the Fresnel tail then combines to e^{−jkρ(1+2cos(φ−φ′))} rather than to the outgoing e^{−jkρ},
    // so the "diffracted" field is not a cylindrical wave at all and disagrees with UTD by a factor of
    // order one at every distance. That was ANT-11's own first attempt and the measurement caught it.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static Complex U(double xi) =>
        0.5 + Complex.Exp(Complex.ImaginaryOne * Math.PI / 4.0) / Math.Sqrt(Math.PI)
              * Fresnel.FromZero(xi);

    private static Complex ExactTotal(double k, double rho, double phi, double phiP, bool soft)
    {
        double s = soft ? -1.0 : 1.0;
        double xm = Math.Sqrt(2 * k * rho) * Math.Cos((phi - phiP) / 2);
        double xp = Math.Sqrt(2 * k * rho) * Math.Cos((phi + phiP) / 2);
        var ui = Complex.Exp(Complex.ImaginaryOne * k * rho * Math.Cos(phi - phiP));
        var ur = Complex.Exp(Complex.ImaginaryOne * k * rho * Math.Cos(phi + phiP));
        return U(xm) * ui + s * U(xp) * ur;
    }

    /// <summary>The geometrical-optics field: each ray present exactly where its own Fresnel argument
    /// is positive, which is the same statement as U(ξ) → 1.</summary>
    private static Complex GeometricalOptics(double k, double rho, double phi, double phiP, bool soft)
    {
        double s = soft ? -1.0 : 1.0;
        Complex t = Complex.Zero;
        if (Math.Cos((phi - phiP) / 2) > 0)
            t += Complex.Exp(Complex.ImaginaryOne * k * rho * Math.Cos(phi - phiP));
        if (Math.Cos((phi + phiP) / 2) > 0)
            t += s * Complex.Exp(Complex.ImaginaryOne * k * rho * Math.Cos(phi + phiP));
        return t;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Side B — THE UTD (KOUYOUMJIAN-PATHAK) COEFFICIENT, n = 2.
    //
    //   D_{s,h} = −e^{−jπ/4}/(2n√(2πk) sinβ₀) · {
    //        cot((π + (φ−φ′))/2n)·F(kL·a⁺(φ−φ′))  +  cot((π − (φ−φ′))/2n)·F(kL·a⁻(φ−φ′))
    //     ∓ [ cot((π + (φ+φ′))/2n)·F(kL·a⁺(φ+φ′))  +  cot((π − (φ+φ′))/2n)·F(kL·a⁻(φ+φ′)) ] }
    //
    //   a^±(β) = 2cos²((2nπN^± − β)/2),   N^+ = round((π+β)/2nπ),   N^- = round((β−π)/2nπ)
    //   F(X)   = 2j√X·e^{jX}·∫_{√X}^∞ e^{−jτ²}dτ,   F(X) → 1 for large X
    //
    // and the diffracted field for plane-wave incidence is D·e^{−jkρ}/√ρ with L = ρ.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static Complex F(double X)
    {
        if (X <= 0) return Complex.Zero;              // F(0) = 0, not 1 — the √X prefactor
        double sx = Math.Sqrt(X);
        var tail = Fresnel.ToInfinity - Fresnel.FromZero(sx);
        return 2.0 * Complex.ImaginaryOne * sx * Complex.Exp(Complex.ImaginaryOne * X) * tail;
    }

    /// <summary>
    /// Null exactly on a shadow boundary, where an individual cotangent is infinite and KP's own
    /// small-argument limit is needed. The measurement samples around those directions instead of
    /// implementing that limit: it is machinery only a shipped estimate would need, and its absence is
    /// stated rather than worked around.
    /// </summary>
    private static Complex? Coefficient(double k, double L, double phi, double phiP, bool soft)
    {
        const double n = 2.0;
        double s = soft ? -1.0 : 1.0;

        Complex? Term(double beta)
        {
            double np = Math.Round((Math.PI + beta) / (2 * n * Math.PI));
            double nm = Math.Round((beta - Math.PI) / (2 * n * Math.PI));
            double ap = 2 * Math.Pow(Math.Cos((2 * n * Math.PI * np - beta) / 2), 2);
            double am = 2 * Math.Pow(Math.Cos((2 * n * Math.PI * nm - beta) / 2), 2);
            double c1 = (Math.PI + beta) / (2 * n), c2 = (Math.PI - beta) / (2 * n);
            if (Math.Abs(Math.Sin(c1)) < 1e-13 || Math.Abs(Math.Sin(c2)) < 1e-13) return null;
            return Math.Cos(c1) / Math.Sin(c1) * F(k * L * ap)
                 + Math.Cos(c2) / Math.Sin(c2) * F(k * L * am);
        }

        var t1 = Term(phi - phiP);
        var t2 = Term(phi + phiP);
        if (t1 is null || t2 is null) return null;

        var pre = -Complex.Exp(-Complex.ImaginaryOne * Math.PI / 4.0)
                  / (2 * n * Math.Sqrt(2 * Math.PI * k));
        return pre * (t1.Value + s * t2.Value);
    }

    private static Complex? UtdDiffracted(double k, double rho, double phi, double phiP, bool soft)
    {
        var d = Coefficient(k, rho, phi, phiP, soft);
        return d is null ? null
                         : d.Value * Complex.Exp(-Complex.ImaginaryOne * k * rho) / Math.Sqrt(rho);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The measurement.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-fg-5, and it is a correction to the brief rather than a confirmation of it.</b> The UTD
    /// coefficient does not degrade as k·ρ falls — it is exact at every k·ρ measured, from 0.2 (an
    /// eighth of a wavelength) to 120. There is no asymptotic validity floor for the coefficient to be
    /// refused past, so a k·ρ refusal written for one would refuse against nothing.
    ///
    /// <para>719 observation angles × 5 incidences × 18 distances × both polarizations, with the error
    /// taken against unit incident amplitude — an ABSOLUTE measure, because the diffracted field
    /// passes through zero and a relative one would divide by it.</para>
    /// </summary>
    [Fact]
    public void UtdHalfPlaneCoefficient_IsTheExactSommerfeldSolution_AtEveryDistanceMeasured()
    {
        const double k = 1.0;
        double[] kRho = [0.2, 0.4, 0.6, 0.8, 1.0, 1.28, 2, 3, 4, 6, 8, 10, 15, 20, 30, 50, 80, 120];
        double[] incidences = [30, 60, 90, 120, 150];

        _out.WriteLine("  k·ρ    ρ/λ      soft worst    hard worst   (absolute, vs unit incidence)");
        double overall = 0;
        foreach (double kr in kRho)
        {
            var worst = new double[2];
            for (int pol = 0; pol < 2; pol++)
            {
                bool soft = pol == 0;
                foreach (double pdeg in incidences)
                {
                    double phiP = pdeg * Math.PI / 180.0;
                    for (int i = 1; i < 720; i++)
                    {
                        double phi = 2 * Math.PI * i / 720;
                        var utd = UtdDiffracted(k, kr, phi, phiP, soft);
                        if (utd is null) continue;
                        var exact = ExactTotal(k, kr, phi, phiP, soft)
                                    - GeometricalOptics(k, kr, phi, phiP, soft);
                        worst[pol] = Math.Max(worst[pol], (utd.Value - exact).Magnitude);
                    }
                }
            }
            overall = Math.Max(overall, Math.Max(worst[0], worst[1]));
            _out.WriteLine($"{kr,7:F2} {kr / (2 * Math.PI),7:F3}   {worst[0]:E4}   {worst[1]:E4}");
        }

        _out.WriteLine($"\n  worst anywhere = {overall:E4}  " +
                       $"(PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement = " +
                       $"{PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement:E0})");

        // The constant the engine's refusal reasoning quotes must not drift from what is measured.
        Assert.True(overall < PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement,
                    $"worst disagreement {overall:E4} exceeds the recorded " +
                    $"{PlanarFiniteGround.MeasuredUtdHalfPlaneAgreement:E0}");

        // And it must not be flattered by a tolerance so loose it would pass an asymptotic method:
        // a genuinely asymptotic coefficient would be O(1) wrong at k·ρ = 0.2.
        Assert.True(overall < 1e-12, $"worst disagreement {overall:E4} is not round-off");
    }

    /// <summary>
    /// The exact side is checked on its own terms before it is used as an oracle, because this area has
    /// burned nine oracles and twice found the ORACLE wrong rather than the method. Two properties, and
    /// neither is a property the UTD side shares: the soft (Dirichlet) total field vanishes ON the
    /// screen, linearly in the distance from it; and in the deep shadow the total field tends to zero.
    /// </summary>
    [Fact]
    public void TheSommerfeldOracle_SatisfiesTheBoundaryConditionAndTheShadow()
    {
        const double k = 1.0, rho = 100.0;
        foreach (double pdeg in new[] { 30.0, 60.0, 120.0 })
        {
            double phiP = pdeg * Math.PI / 180.0;
            // Distance from the screen is ρ·sin φ, so a soft total field ∝ φ for small φ.
            double a = ExactTotal(k, rho, 1e-6, phiP, soft: true).Magnitude;
            double b = ExactTotal(k, rho, 1e-3, phiP, soft: true).Magnitude;
            _out.WriteLine($"  φ′ = {pdeg,5:F0}°:  |u| at φ=1e-6 is {a:E3}, at φ=1e-3 is {b:E3}, " +
                           $"ratio {b / a:F1} (linear ⇒ 1000)");
            Assert.True(a < 2e-4, $"soft total on the screen is {a:E3}, not ~0");
            Assert.True(Math.Abs(b / a - 1000.0) < 20.0, $"not linear in the distance: {b / a:F1}");
        }

        // Deep shadow of both rays: the total field is the diffracted field alone, so it must DECAY
        // as 1/√ρ. Asserted as the decay between two distances rather than against an absolute bound,
        // because the prefactor is a property of the direction (0.78 here) and a bound tight enough to
        // be meaningful at one angle is wrong at the next — a first draft picked 0.5/√(kρ) and failed
        // on a field that was behaving exactly as it should.
        double dir = Math.PI + Math.PI / 3 + 0.7, inc = Math.PI / 3;
        double near = ExactTotal(k, 100.0, dir, inc, soft: false).Magnitude;
        double far2 = ExactTotal(k, 400.0, dir, inc, soft: false).Magnitude;
        _out.WriteLine($"  deep shadow |u|: {near:E3} at kρ=100, {far2:E3} at kρ=400, " +
                       $"ratio {near / far2:F3} (1/√ρ ⇒ 2.000)");
        Assert.True(Math.Abs(near / far2 - 2.0) < 0.05,
                    $"the shadow field does not decay as 1/√ρ: ratio {near / far2:F3}");
    }
}
