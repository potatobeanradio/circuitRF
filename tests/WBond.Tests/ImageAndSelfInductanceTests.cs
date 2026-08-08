namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tiers 2 and 3 of brief-wbond-wba §5 — the ground-plane image and the GMD self-inductance.
///
/// <para><b>Tier 2 is the classic silent failure.</b> A wrong image sign produces a plausible,
/// self-consistent, 10–30 % wrong array inductance that no self-consistency check will ever catch.
/// The horizontal and vertical cases are therefore asserted <b>separately, by hand-derived sign</b>,
/// before any closed-form comparison.</para>
/// </summary>
public class ImageAndSelfInductanceTests
{
    private const double Mu0Over2Pi = 2.0 * Grover.Mu0Over4Pi;

    // ---------------------------------------------------------------- tier 2: the sign rule

    /// <summary>
    /// TIER 2 — a HORIZONTAL filament's image runs <b>anti-parallel</b>.
    ///
    /// <para>Hand-derived: (x₁,y,h) → (x₂,y,h) mirrors to (x₁,y,−h) → (x₂,y,−h); reversing the
    /// traversal makes it run −x. This is the case everyone gets right.</para>
    /// </summary>
    [Fact]
    public void Tier2_HorizontalFilament_ImageIsAntiParallel()
    {
        var f = Filament.FromEndpoints(0, 0, 30e-6, 100e-6, 0, 30e-6, 1e-6);
        var image = f.Image();

        Assert.Equal(-1.0, image.Ux, 1e-15);
        Assert.Equal(0.0, image.Uy, 1e-15);
        Assert.Equal(0.0, image.Uz, 1e-15);

        // It starts at the mirror of the original's END, on the far side of the plane.
        Assert.Equal(100e-6, image.Ax, 1e-18);
        Assert.Equal(-30e-6, image.Az, 1e-18);
        Assert.Equal(f.Length, image.Length, 1e-18);
    }

    /// <summary>
    /// TIER 2 — a VERTICAL filament's image runs <b>parallel</b>, i.e. still +z.
    ///
    /// <para>Hand-derived: (x,y,0) → (x,y,h) mirrors to (x,y,0) → (x,y,−h); reversing it runs from
    /// (x,y,−h) back to (x,y,0), which points <b>+z</b>. This is the case that is wrong in every
    /// implementation that special-cases only the horizontal one, and it is why the rule is
    /// "mirror and reverse" rather than "mirror and negate the mutual".</para>
    /// </summary>
    [Fact]
    public void Tier2_VerticalFilament_ImageIsParallel()
    {
        var f = Filament.FromEndpoints(5e-6, 7e-6, 0, 5e-6, 7e-6, 40e-6, 1e-6);
        var image = f.Image();

        Assert.Equal(0.0, image.Ux, 1e-15);
        Assert.Equal(0.0, image.Uy, 1e-15);
        Assert.Equal(+1.0, image.Uz, 1e-15);

        Assert.Equal(-40e-6, image.Az, 1e-18);
        Assert.Equal(5e-6, image.Ax, 1e-18);
    }

    /// <summary>
    /// TIER 2 — a filament tilted in all three axes: the image's direction is the original's with x
    /// and y negated and z kept. Pins the general rule rather than the two axis-aligned corners.
    /// </summary>
    [Fact]
    public void Tier2_TiltedFilament_ImageNegatesLateralAndKeepsVertical()
    {
        var f = Filament.FromEndpoints(1e-6, 2e-6, 10e-6, 4e-6, 8e-6, 25e-6, 1e-6);
        var image = f.Image();

        Assert.Equal(-f.Ux, image.Ux, 1e-15);
        Assert.Equal(-f.Uy, image.Uy, 1e-15);
        Assert.Equal(+f.Uz, image.Uz, 1e-15);
        Assert.Equal(f.Length, image.Length, 1e-18);

        // Applying the rule twice returns the original — the plane is an involution.
        var back = image.Image();
        Assert.Equal(f.Ax, back.Ax, 1e-18);
        Assert.Equal(f.Ay, back.Ay, 1e-18);
        Assert.Equal(f.Az, back.Az, 1e-18);
        Assert.Equal(f.Ux, back.Ux, 1e-15);
        Assert.Equal(f.Uz, back.Uz, 1e-15);
    }

    // ---------------------------------------------------------------- tier 2: the closed form

    /// <summary>
    /// TIER 2 — a horizontal wire over an infinite ground plane against the textbook closed form
    /// <c>L = (μ₀ℓ/2π)·ln(2h/a)</c>.
    ///
    /// <para>This is the end-to-end check that the image is not merely constructed correctly but
    /// <b>combined</b> correctly: the image's contribution is added, and the minus sign of the
    /// textbook derivation is carried by the reversed geometry.</para>
    ///
    /// <para><b>The closed form is the ℓ → ∞ limit</b>, so it is only a fair oracle at high aspect
    /// ratio. Measured, the residual is a <i>length-independent</i> end effect of
    /// (μ₀/2π)(2h − a) ≈ 200 pH at h = 20 mil — 8 % of a 100 mil wire and 0.45 % of an 800 mil one.
    /// The aspect ratios here are therefore chosen where the oracle is valid, and
    /// <see cref="Tier2_WireOverGroundResidual_IsTheLengthIndependentEndEffect"/> pins the residual
    /// itself, which is a far sharper statement than any tolerance.</para>
    /// </summary>
    [Theory]
    [InlineData(800.0, 20.0, 1.0)]
    [InlineData(1200.0, 30.0, 1.0)]
    [InlineData(600.0, 10.0, 0.7)]
    public void Tier2_WireOverGround_MatchesLnTwoHOverA(double lengthMil, double heightMil, double diameterMil)
    {
        var design = TestDesigns.SingleHorizontalWire(lengthMil, heightMil, diameterMil);
        var mesh = WireMesh.Build(design);
        var l = InductanceMatrix.Fill(mesh);

        double lengthM = WBondUnits.ToMetres(WBondUnits.ToNm(lengthMil, WBondUnit.Mil));
        double heightM = WBondUnits.ToMetres(WBondUnits.ToNm(heightMil, WBondUnit.Mil));
        double radiusM = WBondUnits.ToMetres(WBondUnits.ToNm(diameterMil, WBondUnit.Mil)) / 2.0;

        double expected = Mu0Over2Pi * lengthM * Math.Log(2.0 * heightM / radiusM);
        double actual = l[0, 0];

        double relative = Math.Abs(actual - expected) / expected;
        Assert.True(relative < 0.015,
            $"Wire over ground, l={lengthMil} mil h={heightMil} mil d={diameterMil} mil: " +
            $"got {actual * 1e12:F2} pH, closed form {expected * 1e12:F2} pH, {relative:P2} apart.");
    }

    /// <summary>
    /// TIER 2 — the residual against <c>ln(2h/a)</c> is the finite-length end effect
    /// <c>(μ₀/2π)(2h − a)</c>, and it is <b>independent of wire length</b>.
    ///
    /// <para>This explains the discrepancy instead of tolerating it. Measured: the gap is ~181 pH at
    /// ℓ/h = 5 and ~200 pH at ℓ/h = 100, while the inductance itself grows from 2,045 pH to
    /// 44,322 pH — which is exactly why the <i>relative</i> error falls as 1/ℓ and why a loose
    /// tolerance would have hidden a genuine sign or scale error at short lengths.</para>
    /// </summary>
    [Fact]
    public void Tier2_WireOverGroundResidual_IsTheLengthIndependentEndEffect()
    {
        const double heightMil = 20.0, diameterMil = 1.0;
        double heightM = WBondUnits.ToMetres(WBondUnits.ToNm(heightMil, WBondUnit.Mil));
        double radiusM = WBondUnits.ToMetres(WBondUnits.ToNm(diameterMil, WBondUnit.Mil)) / 2.0;

        double endEffect = Mu0Over2Pi * (2.0 * heightM - radiusM);
        double previousRatio = 0.0;

        foreach (double lengthMil in new[] { 100.0, 200.0, 800.0, 2000.0 })
        {
            var mesh = WireMesh.Build(TestDesigns.SingleHorizontalWire(lengthMil, heightMil, diameterMil));
            double actual = InductanceMatrix.Fill(mesh)[0, 0];

            double lengthM = WBondUnits.ToMetres(WBondUnits.ToNm(lengthMil, WBondUnit.Mil));
            double closedForm = Mu0Over2Pi * lengthM * Math.Log(2.0 * heightM / radiusM);

            double gap = closedForm - actual;
            double ratio = gap / endEffect;

            Assert.True(ratio is > 0.85 and < 1.02,
                $"At l={lengthMil} mil the residual should be the end effect (μ₀/2π)(2h−a)={endEffect * 1e12:F2} pH; " +
                $"measured gap {gap * 1e12:F2} pH, ratio {ratio:F4}.");

            // The approximation improves monotonically toward the exact end effect.
            Assert.True(ratio > previousRatio,
                $"The residual should approach the end effect from below as l grows; at {lengthMil} mil " +
                $"the ratio was {ratio:F4}, down from {previousRatio:F4}.");
            previousRatio = ratio;
        }

        Assert.True(previousRatio > 0.99,
            $"By l/h = 100 the residual should be within 1 % of (μ₀/2π)(2h−a); got {previousRatio:F4}.");
    }

    /// <summary>
    /// TIER 2 — the closed form is the ℓ → ∞ limit, so the agreement must <b>tighten monotonically</b>
    /// as the wire lengthens. A sign error or a missing image would show as a roughly constant or
    /// growing discrepancy, which a single-point tolerance would not catch.
    /// </summary>
    [Fact]
    public void Tier2_WireOverGround_AgreementTightensWithLength()
    {
        double previous = double.MaxValue;

        foreach (double lengthMil in new[] { 50.0, 100.0, 200.0, 400.0, 800.0 })
        {
            var mesh = WireMesh.Build(TestDesigns.SingleHorizontalWire(lengthMil, 20.0, 1.0));
            var l = InductanceMatrix.Fill(mesh);

            double lengthM = WBondUnits.ToMetres(WBondUnits.ToNm(lengthMil, WBondUnit.Mil));
            double heightM = WBondUnits.ToMetres(WBondUnits.ToNm(20.0, WBondUnit.Mil));
            double radiusM = WBondUnits.ToMetres(WBondUnits.ToNm(1.0, WBondUnit.Mil)) / 2.0;

            double expected = Mu0Over2Pi * lengthM * Math.Log(2.0 * heightM / radiusM);
            double relative = Math.Abs(l[0, 0] - expected) / expected;

            Assert.True(relative < previous,
                $"Agreement with ln(2h/a) must tighten as the wire lengthens; at {lengthMil} mil the " +
                $"error was {relative:E3}, up from {previous:E3}.");
            previous = relative;
        }

        // ~1.1 % at 800 mil: the end effect is ~200 pH against ~17,600 pH. Not a tolerance pulled
        // to fit — see Tier2_WireOverGroundResidual_IsTheLengthIndependentEndEffect for why.
        Assert.True(previous < 1.5e-2,
            $"At 800 mil the wire-over-ground closed form should be within 1.5 %; got {previous:P3}.");
    }

    /// <summary>
    /// TIER 2 — disabling the ground plane must raise the self-inductance substantially, because the
    /// image is what cancels most of the flux. A model that silently kept the image, or silently
    /// dropped it, would look identical in every other test in this file.
    /// </summary>
    [Fact]
    public void Tier2_DisablingTheGroundPlane_RaisesSelfInductance()
    {
        var withPlane = TestDesigns.SingleHorizontalWire(100.0, 20.0, 1.0);
        var withoutPlane = TestDesigns.SingleHorizontalWire(100.0, 20.0, 1.0);
        withoutPlane.GroundPlane.Enabled = false;

        double grounded = InductanceMatrix.Fill(WireMesh.Build(withPlane))[0, 0];
        double free = InductanceMatrix.Fill(WireMesh.Build(withoutPlane))[0, 0];

        // Measured 1.241x for this geometry (2,538 pH free vs 2,045 pH over ground). The band is
        // wide enough to be geometry-tolerant and tight enough that a silently-kept or
        // silently-dropped image (1.000x, or ~0.5x) fails it.
        double ratio = free / grounded;
        Assert.True(ratio is > 1.15 and < 1.35,
            $"Removing the ground plane must raise L by a specific, image-sized amount: " +
            $"free={free * 1e12:F1} pH, over-ground={grounded * 1e12:F1} pH, ratio {ratio:F4} (expected ~1.24).");
    }

    // ---------------------------------------------------------------- tier 3: GMD self-inductance

    /// <summary>
    /// TIER 3 — the shipping self-inductance choice: GMD = a, giving <b>external inductance only</b>
    /// (D4 / WB8), against <c>(μ₀ℓ/2π)[ln(2ℓ/a) − 1]</c>.
    ///
    /// <para>The internal contribution is deliberately absent here; it arrives with L_int(f) from the
    /// same Bessel evaluation that produces R(f), so the whole frequency dependence lives in one
    /// place.</para>
    /// </summary>
    [Fact]
    public void Tier3_SelfExternal_MatchesRosaWithoutTheInternalTerm()
    {
        const double a = 12.7e-6;   // 0.5 mil
        double worst = 0.0;
        double previous = double.MaxValue;

        foreach (double l in new[] { 1e-3, 2e-3, 5e-3, 20e-3 })
        {
            var f = Filament.FromEndpoints(0, 0, 0, l, 0, 0, a);
            double actual = Grover.SelfExternal(in f);
            double expected = Mu0Over2Pi * l * (Math.Log(2.0 * l / a) - 1.0);

            double relative = Math.Abs(actual - expected) / expected;
            worst = Math.Max(worst, relative);

            Assert.True(relative < previous,
                $"Rosa's l>>a approximation must tighten as l/a grows; at l={l:E1} the error was " +
                $"{relative:E3}, up from {previous:E3}.");
            previous = relative;
        }

        // At l/a = 79 the gap is ~0.3 % and it is ROSA's approximation, not ours.
        Assert.True(worst < 5e-3,
            $"Worst external self-inductance error against Rosa was {worst:P3}, expected ~0.3 %.");
    }

    /// <summary>
    /// TIER 3 — the GMD concept itself: evaluating at GMD = a·e^(−1/4) reproduces Rosa's
    /// <b>uniform-current</b> form including the +¼ internal term, to 0.23 % at ℓ/a = 79.
    ///
    /// <para>This is not the shipping path (D4 chooses GMD = a) but it is what justifies the GMD
    /// treatment at all, and it pins the 0.7788 constant.</para>
    /// </summary>
    [Fact]
    public void Tier3_GmdAtUniformCurrent_ReproducesRosaIncludingTheInternalTerm()
    {
        const double a = 12.7e-6, l = 1e-3;
        double gmd = a * Math.Exp(-0.25);

        // Same filament geometry, but with the radius set to the uniform-current GMD so the
        // separation floor lands there.
        var f = Filament.FromEndpoints(0, 0, 0, l, 0, 0, gmd);
        double actual = Grover.SelfExternal(in f);
        double rosa = Mu0Over2Pi * l * (Math.Log(2.0 * l / a) - 1.0 + 0.25);

        double relative = Math.Abs(actual - rosa) / rosa;
        Assert.True(relative < 4e-3,
            $"GMD = a*exp(-1/4) should reproduce Rosa's uniform-current value to ~0.23 %; " +
            $"got {relative:P3} (actual {actual * 1e12:F4} pH, Rosa {rosa * 1e12:F4} pH).");
    }

    /// <summary>
    /// TIER 9 (composition) — a wire of length 2ℓ equals two cascaded ℓ wires: subdividing the
    /// polyline must not change the assembled self-inductance.
    ///
    /// <para>This is the check that the double sum over filament pairs, including the touching-bend
    /// GMD clamp, is doing bookkeeping rather than inventing inductance at every vertex.</para>
    /// </summary>
    [Fact]
    public void Tier9_SubdividingAStraightWire_DoesNotChangeItsInductance()
    {
        var coarse = TestDesigns.SingleHorizontalWire(100.0, 20.0, 1.0);

        // Same wire, same endpoints, but sampled at 2, 4 and 8 segments.
        foreach (int segments in new[] { 2, 4, 8 })
        {
            var wire = new Wire { DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil) };
            for (int i = 0; i <= segments; i++)
                wire.Points.Add(Point3.Mils(100.0 * i / segments, 0, 20.0));

            var design = new WBondDesign { Arrays = { new WireArray { Name = "G1", Wires = { wire } } } };

            double subdivided = InductanceMatrix.Fill(WireMesh.Build(design))[0, 0];
            double single = InductanceMatrix.Fill(WireMesh.Build(coarse))[0, 0];

            double relative = Math.Abs(subdivided - single) / single;
            Assert.True(relative < 1e-3,
                $"Subdividing a straight wire into {segments} segments changed its inductance by " +
                $"{relative:P4} ({subdivided * 1e12:F3} pH vs {single * 1e12:F3} pH).");
        }
    }
}
