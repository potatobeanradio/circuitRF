using CircuitRF.WBond;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tiers 0 and 1 of brief-wbond-wba §5 — the two Grover filament formulae against closed
/// forms and against each other.
///
/// <para><b>Where a tier names a closed form, that closed form is the oracle</b> — not another wBond
/// path agreeing with itself.</para>
/// </summary>
public class GroverOracleTests
{
    private const double Mu0Over4Pi = Grover.Mu0Over4Pi;

    private static Filament Straight(double ax, double ay, double az, double length, double radius,
                                     double ux = 1, double uy = 0, double uz = 0)
        => Filament.FromEndpoints(ax, ay, az, ax + ux * length, ay + uy * length, az + uz * length, radius);

    // ---------------------------------------------------------------- tier 0

    /// <summary>
    /// TIER 0 — the parallel formula against Grover's closed form for two equal, fully overlapping
    /// filaments: M = (μ₀/2π)·[ l·asinh(l/d) − √(l²+d²) + d ].
    ///
    /// <para>This pins the exact combination of the four end-pair terms. A plausible-looking
    /// different sign pattern passes a self-consistency check while failing this.</para>
    /// </summary>
    [Theory]
    [InlineData(1e-3, 0.2e-3)]
    [InlineData(1e-3, 0.02e-3)]
    [InlineData(2.54e-3, 0.5e-3)]
    [InlineData(0.5e-3, 1e-3)]
    public void Tier0_ParallelFormula_MatchesGroverClosedForm_EqualOverlappingFilaments(double l, double d)
    {
        // Radius small enough that the GMD floor never engages: this tests the formula, not the clamp.
        double radius = d / 1000.0;
        var p = Straight(0, 0, 0, l, radius);
        var q = Straight(0, d, 0, l, radius);

        double actual = Grover.Mutual(in p, in q);
        double expected = 2.0 * Mu0Over4Pi * (l * Math.Asinh(l / d) - Math.Sqrt(l * l + d * d) + d);

        Assert.Equal(expected, actual, RelativeTolerance(expected, 1e-12));
    }

    /// <summary>
    /// TIER 0 — the general offset case, against the same closed form built by superposition.
    /// Two filaments offset axially by exactly their own length are the difference of overlapping
    /// configurations, which the four-term formula must reproduce without special-casing.
    /// </summary>
    [Fact]
    public void Tier0_ParallelFormula_IsAdditiveUnderSubdivision()
    {
        const double d = 0.15e-3, radius = 1e-7;
        var whole = Straight(0, 0, 0, 2e-3, radius);
        var other = Straight(0, d, 0, 2e-3, radius);

        double m = Grover.Mutual(in whole, in other);

        // Split BOTH filaments in half; the double sum over the four sub-pairs must equal the whole.
        var a1 = Straight(0, 0, 0, 1e-3, radius);
        var a2 = Straight(1e-3, 0, 0, 1e-3, radius);
        var b1 = Straight(0, d, 0, 1e-3, radius);
        var b2 = Straight(1e-3, d, 0, 1e-3, radius);

        double sum = Grover.Mutual(in a1, in b1) + Grover.Mutual(in a1, in b2)
                   + Grover.Mutual(in a2, in b1) + Grover.Mutual(in a2, in b2);

        Assert.Equal(m, sum, RelativeTolerance(m, 1e-12));
    }

    /// <summary>
    /// TIER 0 — antiparallel filaments negate. This is the property the image construction relies
    /// on, so it is pinned independently of the image code.
    /// </summary>
    [Fact]
    public void Tier0_ReversingAFilament_NegatesTheMutual()
    {
        const double radius = 1e-7;
        var p = Straight(0, 0, 0, 1e-3, radius);
        var forward = Straight(0, 0.3e-3, 0, 1e-3, radius);
        var reversed = Filament.FromEndpoints(1e-3, 0.3e-3, 0, 0, 0.3e-3, 0, radius);

        double a = Grover.Mutual(in p, in forward);
        double b = Grover.Mutual(in p, in reversed);

        Assert.Equal(-a, b, RelativeTolerance(a, 1e-13));
    }

    // ---------------------------------------------------------------- tier 1

    /// <summary>
    /// TIER 1 — the skew formula converges to the parallel closed form as ε → 0.
    ///
    /// <para><b>This is the only check in the brief that tests one formulation against a genuinely
    /// independent one.</b> The measured behaviour (brief §0.3 item 3): agreement to 9 digits at
    /// ε = 1e-6, physical ε² convergence above that, and ~3 digits lost to cancellation by 1e-8.</para>
    ///
    /// <para>The tolerances below are the <i>measured</i> ones. They are deliberately not uniform:
    /// loosening the 1e-2 case to hide a real error, or tightening it to look impressive, both
    /// destroy the information this test carries.</para>
    /// </summary>
    [Theory]
    [InlineData(1e-2, 3e-4)]   // physical ε² convergence dominates here, not numerical error
    [InlineData(1e-3, 3e-6)]
    [InlineData(1e-4, 3e-8)]
    [InlineData(1e-6, 1e-9)]   // 9 digits — the measured crossover point
    public void Tier1_SkewFormula_ConvergesToParallelClosedForm(double epsilon, double tolerance)
    {
        const double l = 1e-3, m = 1e-3, d = 0.2e-3, radius = 1e-9;

        var p = Straight(0, 0, 0, l, radius);

        // q is tilted out of p's direction by epsilon, at lateral separation d, starting opposite
        // p's start so that mu = nu = 0 in Grover's parameterisation.
        var q = Straight(0, d, 0, m, radius, ux: Math.Cos(epsilon), uy: 0, uz: Math.Sin(epsilon));

        double skew = Grover.Skew(in p, in q, Math.Cos(epsilon), Math.Sin(epsilon));
        double exact = 2.0 * Mu0Over4Pi * (l * Math.Asinh(l / d) - Math.Sqrt(l * l + d * d) + d);

        double relative = Math.Abs(skew - exact) / Math.Abs(exact);
        Assert.True(relative <= tolerance,
            $"Skew→parallel convergence at eps={epsilon:E0}: relative error {relative:E3} exceeded {tolerance:E0}. " +
            $"skew={skew:E12}, exact={exact:E12}.");
    }

    /// <summary>
    /// TIER 1 — the dispatcher actually takes the parallel branch below
    /// <see cref="Grover.ParallelEpsilon"/>, and the two branches agree across it.
    ///
    /// <para>Guards the crossover itself: a dispatcher that silently always took the skew path would
    /// pass every other test in this file.</para>
    /// </summary>
    [Fact]
    public void Tier1_DispatcherIsContinuousAcrossTheParallelCrossover()
    {
        const double l = 1e-3, radius = 1e-9, d = 0.2e-3;
        var p = Straight(0, 0, 0, l, radius);

        double justBelow = Grover.ParallelEpsilon / 2.0;
        double justAbove = Grover.ParallelEpsilon * 2.0;

        var qBelow = Straight(0, d, 0, l, radius, Math.Cos(justBelow), 0, Math.Sin(justBelow));
        var qAbove = Straight(0, d, 0, l, radius, Math.Cos(justAbove), 0, Math.Sin(justAbove));

        double below = Grover.Mutual(in p, in qBelow);
        double above = Grover.Mutual(in p, in qAbove);

        Assert.Equal(below, above, RelativeTolerance(below, 1e-9));
    }

    /// <summary>
    /// TIER 1 — perpendicular filaments have <b>exactly</b> zero mutual inductance.
    ///
    /// <para>Not an approximation and not a symmetry accident: the Neumann integrand is
    /// dl₁·dl₂ = cos ε·dt·ds, which is identically zero when the filaments are perpendicular,
    /// whatever their positions. <b>This test caught a real error</b> — the Ω term of the skew
    /// formula transcribed outside the <c>2·cos ε</c> factor instead of inside it, which returns
    /// −1.85e-10 H here. Every other test in this file passed with that bug present.</para>
    /// </summary>
    [Theory]
    [InlineData(0.5e-3)]
    [InlineData(0.05e-3)]
    [InlineData(3.0e-3)]
    public void Tier1_PerpendicularFilaments_HaveExactlyZeroMutual(double separation)
    {
        const double radius = 1e-9;
        var p = Filament.FromEndpoints(-1e-3, 0, 0, 1e-3, 0, 0, radius);
        var q = Filament.FromEndpoints(0, -1e-3, separation, 0, 1e-3, separation, radius);

        double m = Grover.Mutual(in p, in q);

        Assert.True(Math.Abs(m) < 1e-24,
            $"Perpendicular filaments must have identically zero mutual (dl1.dl2 = 0), got {m:E6} H.");
    }

    /// <summary>
    /// TIER 1b — the skew formula against <b>direct numerical integration of the Neumann double
    /// integral</b>, at genuinely skew angles.
    ///
    /// <para>This is the oracle that is independent of the closed form. Tier 1's ε → 0 convergence
    /// check cannot separate a correctly- from an incorrectly-placed Ω term, because both agree to
    /// 9 digits as cos ε → 1; the misplacement is wrong by 8 % at 30° and 31 % at 55°. <b>Neither
    /// test is redundant with the other and neither may be deleted.</b></para>
    /// </summary>
    [Theory]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(55.0)]
    [InlineData(120.0)]
    [InlineData(160.0)]
    public void Tier1b_SkewFormula_MatchesDirectNeumannIntegration(double degrees)
    {
        double eps = degrees * Math.PI / 180.0;
        const double l = 1e-3, m = 1e-3, d = 0.4e-3, radius = 1e-9;

        var p = Filament.FromEndpoints(0, 0, 0, l, 0, 0, radius);
        var q = Filament.FromEndpoints(
            0, 0, d,
            m * Math.Cos(eps), m * Math.Sin(eps), d,
            radius);

        double closedForm = Grover.Mutual(in p, in q);
        double numeric = NeumannOracle.Mutual(in p, in q);

        double relative = Math.Abs(closedForm - numeric) / Math.Abs(numeric);
        Assert.True(relative < 1e-9,
            $"Skew formula vs. Neumann integration at {degrees}°: relative error {relative:E3}. " +
            $"closed form={closedForm:E9}, numeric={numeric:E9}.");
    }

    /// <summary>
    /// TIER 1b — the same independent check for filaments that are skew in all three axes and
    /// offset along their own lengths, so μ and ν are both non-zero and neither starts at the
    /// common-perpendicular foot.
    /// </summary>
    [Fact]
    public void Tier1b_FullyGeneralPosition_MatchesDirectNeumannIntegration()
    {
        const double radius = 1e-9;
        var p = Filament.FromEndpoints(0.1e-3, -0.2e-3, 0.05e-3, 1.3e-3, 0.4e-3, 0.2e-3, radius);
        var q = Filament.FromEndpoints(-0.3e-3, 0.5e-3, 0.9e-3, 0.8e-3, 1.1e-3, 0.6e-3, radius);

        double closedForm = Grover.Mutual(in p, in q);
        double numeric = NeumannOracle.Mutual(in p, in q);

        double relative = Math.Abs(closedForm - numeric) / Math.Abs(numeric);
        Assert.True(relative < 1e-9,
            $"General-position skew: relative error {relative:E3}. " +
            $"closed form={closedForm:E9}, numeric={numeric:E9}.");
    }

    // ---------------------------------------------------------------- the GMD clamp

    /// <summary>
    /// The GMD floor is PHYSICS, not a numerical guard (WB6 / D5): two filaments of the same wire
    /// meeting at a bend have intersecting axes, and the correct separation is the conductor's own
    /// GMD, not zero and not an arbitrary epsilon.
    /// </summary>
    /// <summary>
    /// A shallow bend — the geometry every real wire is made of. The two filaments share an end
    /// point, so their axes intersect and the raw shortest distance is exactly zero, where the skew
    /// formula returns NaN. The clamp must make this finite <b>and</b> physically meaningful.
    ///
    /// <para>A 90° bend would be the wrong test here: cos ε = 0 makes the mutual identically zero
    /// regardless of what the clamp does, so it would pass with the clamp removed entirely.</para>
    /// </summary>
    [Fact]
    public void GmdClamp_TouchingBentFilaments_AreFiniteAndPositive()
    {
        const double radius = 12.7e-6;   // 0.5 mil
        double eps = 25.0 * Math.PI / 180.0;

        var a = Filament.FromEndpoints(0, 0, 0, 1e-3, 0, 0, radius);
        var b = Filament.FromEndpoints(
            1e-3, 0, 0,
            1e-3 + 1e-3 * Math.Cos(eps), 1e-3 * Math.Sin(eps), 0,
            radius);

        double m = Grover.Mutual(in a, in b);

        Assert.True(double.IsFinite(m),
            "A bend's two filaments share an end point; without the GMD floor this is NaN.");
        Assert.True(m > 0.0,
            $"Two filaments meeting at a shallow bend reinforce (cos eps > 0), so the mutual must be " +
            $"positive; got {m:E6} H.");
    }

    /// <summary>
    /// The clamp is the GMD, not an arbitrary epsilon (WB6). Halving the wire radius must visibly
    /// change a touching bend's mutual — an implementation that clamped at 1e-12 m would return
    /// almost the same number for both.
    /// </summary>
    [Fact]
    public void GmdClamp_TouchingBend_TracksTheConductorRadius()
    {
        double eps = 25.0 * Math.PI / 180.0;

        static Filament[] Bend(double radius, double eps) =>
        [
            Filament.FromEndpoints(0, 0, 0, 1e-3, 0, 0, radius),
            Filament.FromEndpoints(1e-3, 0, 0,
                1e-3 + 1e-3 * Math.Cos(eps), 1e-3 * Math.Sin(eps), 0, radius),
        ];

        var thick = Bend(12.7e-6, eps);
        var thin = Bend(6.35e-6, eps);

        double mThick = Grover.Mutual(in thick[0], in thick[1]);
        double mThin = Grover.Mutual(in thin[0], in thin[1]);

        // Thinner conductor => smaller effective separation => larger mutual. The sensitivity is
        // WEAK and that is physically right: d enters only near the shared vertex, while most of the
        // mutual comes from the far ends where the clamp never engages. Measured at 0.47 % for a 2x
        // radius change, so the band below brackets the real number rather than asserting a guess.
        // A fixed-epsilon clamp (say 1e-12 m) would give ~0 % and fail the lower bound.
        double delta = mThin / mThick - 1.0;
        Assert.True(delta is > 2e-3 and < 2e-2,
            $"Halving the radius must move a touching bend's mutual through the GMD floor by a small " +
            $"but real margin; measured {delta:P3} (expected ~0.47 %). thin={mThin:E6}, thick={mThick:E6}.");
    }

    [Fact]
    public void GmdClamp_MinimumSeparation_IsTheGeometricMeanOfTheRadii()
    {
        var thin = Filament.FromEndpoints(0, 0, 0, 1e-3, 0, 0, 1e-6);
        var thick = Filament.FromEndpoints(0, 1e-4, 0, 1e-3, 1e-4, 0, 4e-6);

        Assert.Equal(2e-6, Grover.MinimumSeparation(in thin, in thick), 1e-18);
        Assert.Equal(1e-6, Grover.MinimumSeparation(in thin, in thin), 1e-18);
    }

    internal static double RelativeTolerance(double reference, double relative) =>
        Math.Abs(reference) * relative;
}
