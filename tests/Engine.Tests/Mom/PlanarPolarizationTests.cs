// ANT-6 — polarization, gated in five groups.
//
// WHAT IS EXACT HERE AND WHAT IS NOT. The whole phase is a ROTATION and a pair of rotation
// INVARIANTS over ANT-4's two complex numbers, so almost every statement about it is exact rather
// than tolerant, and almost none of it needs a solve:
//
//   §5.1  the analytic linear source  — an x̂-directed element over the slab has IDENTICALLY ZERO
//                                       Ludwig-3 cross-pol in the principal planes, for every θ and
//                                       every stack. Exact, from the arithmetic, with no oracle: the
//                                       cross component carries a sinφcosφ that no element factor can
//                                       cancel. And NON-zero on the diagonals, which is what stops
//                                       the test passing on a decomposition that returns zero always.
//   §5.2  the known circular case     — two orthogonal elements in phase quadrature: axial ratio
//                                       0 dB and a known sense, BOTH signs, so the sense convention
//                                       is pinned rather than assumed. The oracle is the real field
//                                       vector's own rotation, (E × dE/dt)·r̂ sampled in TIME, which
//                                       shares no algebra with PlanarPolarization.
//   §5.3  the sentinels               — pure linear reads the NAMED sentinel, not ∞, not a NaN and not
//                                       a plausible-looking clamp.
//   §5.4  the reference angle         — named wins; derived is REPORTED and is the same number the
//                                       beamwidth cut derives; ambiguous REFUSES, and the axial ratio
//                                       and the sense survive the refusal, which is the whole reason
//                                       they are in this phase.
//   §5.5  the cubes and the definition — group, axes, names, units, the definition carried in both the
//                                       name and the note, and the pair absent-and-noted when refused.
//
// The mesh-limited cross-pol FLOOR (§4) is a MEASUREMENT, reported in RESOLVED.md §ANT-6, not a
// test — it is a property of the mesher at three densities, and gating it would gate the machine.

using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarPolarizationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    private const double FHz  = 5e9;
    private const double Eta0 = EmConstants.Mu0 * EmConstants.C0;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures — ANT-4's and ANT-5's own hand meshes, so "one current element" and "two orthogonal
    // current elements" are expressible with no solve anywhere near them.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One x-rooftop over two cells on level 0 — a single horizontal current element.</summary>
    private static PlanarMesh OneXRooftop(double w = 1e-3, double h = 1e-3)
    {
        var cells = new[]
        {
            new PlanarCell(0, 0, 0, -w, -0.5 * h, 0.0, 0.5 * h),
            new PlanarCell(0, 1, 0, 0.0, -0.5 * h, w, 0.5 * h),
        };
        return new PlanarMesh(cells, [new PlanarBasis(0, 0, 1, PlanarBasisDirection.X)],
                              ["Metal"], [-w, 0.0, w], [-0.5 * h, 0.5 * h]);
    }

    /// <summary>A 2×2 block — the smallest mesh carrying BOTH an x and a y rooftop, which is what a
    /// circularly polarized current needs to be expressible at all. Cell order is R-msh-2's.</summary>
    private static PlanarMesh TwoByTwo(double w = 1e-3)
    {
        var cells = new[]
        {
            new PlanarCell(0, 0, 0, 0,   0,   w,     w),
            new PlanarCell(0, 1, 0, w,   0,   2 * w, w),
            new PlanarCell(0, 0, 1, 0,   w,   w,     2 * w),
            new PlanarCell(0, 1, 1, w,   w,   2 * w, 2 * w),
        };
        var bases = new[]
        {
            new PlanarBasis(0, 0, 1, PlanarBasisDirection.X),
            new PlanarBasis(0, 2, 3, PlanarBasisDirection.X),
            new PlanarBasis(0, 0, 2, PlanarBasisDirection.Y),
            new PlanarBasis(0, 1, 3, PlanarBasisDirection.Y),
        };
        return new PlanarMesh(cells, bases, ["Metal"], [0, w, 2 * w], [0, w, 2 * w]);
    }

    private static PlanarProblem SlabProblem(GroundedSlab slab, double fHz = FHz) =>
        new([new PlanarConductorLayer("Metal", [PlanarLineFixtures.Rect(-1e-3, -1e-3, 1e-3, 1e-3)],
                                     5.8e7, 35e-6)], slab, fHz);

    private static Vec<Complex> Currents(params Complex[] values)
    {
        var v = new Vec<Complex>(values.Length);
        for (int i = 0; i < values.Length; i++) v[i] = values[i];
        return v;
    }

    private static PlanarMetricContext Context(
        PlanarMesh mesh, Vec<Complex> currents, PlanarFarFieldPattern pattern,
        PlanarMetricSettings? settings = null) =>
        new(SlabProblem(GroundedSlab.Fr4Starter), mesh, currents, pattern,
            new Complex(0.02, 0.0), 50.0, settings);

    /// <summary>A pattern built BY HAND from (E_θ, E_φ) per direction — no mesh, no solve, no Green's
    /// function. What makes every invariant in §5.3 and §5.5 checkable in microseconds.</summary>
    private static PlanarFarFieldPattern HandPattern(
        PlanarFarFieldGrid grid, Func<double, double, (Complex Eth, Complex Eph)> f)
    {
        int n = grid.DirectionCount;
        var eth = new Complex[n];
        var eph = new Complex[n];
        var u   = new double[n];
        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                int k = grid.IndexOf(it, ip);
                var (a, b) = f(grid.ThetaDeg[it], grid.PhiDeg[ip]);
                eth[k] = a; eph[k] = b;
                u[k]   = (a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude) / (2 * Eta0);
            }
        return new PlanarFarFieldPattern(grid, eth, eph, u, 1, FHz);
    }

    private static PlanarPolarizationPattern Decompose(PlanarFarFieldPattern pattern, double phi0Deg) =>
        PlanarPolarization.Of(pattern, EmSuitability.Yes,
                              new PlanarPolarizationReference(phi0Deg, false, double.NaN));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.1 — the analytic linear source. The first gate, and it is an EXACT statement.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An x̂-directed current element over a grounded slab has IDENTICALLY ZERO Ludwig-3 cross-pol
    /// in the principal planes, for every θ and every stack — and non-zero on the diagonals.</b>
    ///
    /// <para>ANT-4 gives F_θ ∝ f_TM·J̃_x cosφ and F_φ ∝ −f_TE·J̃_x sinφ, so
    /// E_cross ∝ J̃_x·sinφcosφ·(f_TM − f_TE) about φ₀ = 0. The element factors never enter, which is
    /// why this catches a sign error in the decomposition with no oracle and no substrate physics.
    /// <b>The diagonal half is what stops the test being vacuous</b> — a decomposition that returned
    /// zero everywhere, or swapped co for cross, would pass the first half alone.</para>
    ///
    /// <para><b>The exactness is a property of the ANGLE's floating-point representation, not of the
    /// algebra, and only φ = φ₀ gets it.</b> There sin(0) is exactly 0 and the cross component is the
    /// exact-zero sentinel. At φ = 90° and 180° the mathematical zero is a cancellation of two
    /// round-off-sized terms instead, because π/2 and π are not representable: cos(π/2) is 6.1e-17,
    /// so both of E_cross's terms are ~1e-17 of the peak and the result lands near −358 dB rather
    /// than at the sentinel. That is the right answer and it is asserted as a structural zero rather
    /// than as an exact one.</para>
    /// </summary>
    [Fact]
    public void AnXDirectedElement_HasNoLudwig3CrossPolInThePrincipalPlanes()
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = OneXRooftop();
        var grid    = new PlanarFarFieldGrid([0, 15, 30, 45, 60, 75, 90], [0, 45, 90, 135, 180, 270]);
        var pattern = PlanarFarField.Compute(problem, mesh, Currents(Complex.One), 1, FHz, grid);

        var pol = Decompose(pattern, 0.0);

        double worstPrincipal = double.NegativeInfinity;
        foreach (double phi in new double[] { 0, 90, 180 })
        {
            int ip = PlanarMetrics.Nearest(grid.PhiDeg, phi);
            for (int it = 0; it < grid.ThetaDeg.Count; it++)
            {
                int k = grid.IndexOf(it, ip);
                double rel = pol.CrossPolDb[k] - pol.PeakCoPolDb;
                worstPrincipal = Math.Max(worstPrincipal, rel);
                Assert.True(rel < -250.0,
                            $"φ = {phi}°, θ = {grid.ThetaDeg[it]}°: the cross-pol reads {rel:F1} dB " +
                            $"below peak co-pol, which is not a structural zero");
                if (phi == 0.0) Assert.Equal(PlanarPolarization.FloorDb, pol.CrossPolDb[k]);

                // Every bit of the field is therefore in the co-polar component, which is the other
                // half of the same statement. θ = 90° is a PEC boundary condition, so the co-pol
                // there is itself a round-off zero — reported as computed, never floored.
                double total = pattern.ETheta[k].Magnitude * pattern.ETheta[k].Magnitude
                               + pattern.EPhi[k].Magnitude * pattern.EPhi[k].Magnitude;
                if (10.0 * Math.Log10(total) > PlanarPolarization.FloorDb)
                    Assert.Equal(10.0 * Math.Log10(total), pol.CoPolDb[k], 9);
            }
        }

        // The diagonal, where the two element factors differ and the cross component is real physics.
        int diag = PlanarMetrics.Nearest(grid.PhiDeg, 45.0);
        int mid  = PlanarMetrics.Nearest(grid.ThetaDeg, 45.0);
        double crossAtDiagonal = pol.CrossPolDb[grid.IndexOf(mid, diag)] - pol.PeakCoPolDb;
        _out.WriteLine($"x̂ element on 1.6 mm FR-4 at {FHz / 1e9} GHz: worst principal-plane cross-pol " +
                       $"over φ ∈ {{0, 90, 180}}° and every θ is {worstPrincipal:F1} dB below peak " +
                       $"co-pol (the exact-zero sentinel at φ = φ₀, round-off elsewhere); " +
                       $"θ = 45°, φ = 45° reads {crossAtDiagonal:F2} dB below peak co-pol; the " +
                       $"principal-plane figure is {pol.PrincipalPlaneCrossPolDb:F1} dB.");
        Assert.True(crossAtDiagonal > -60.0 && crossAtDiagonal < 0.0,
                    $"the diagonal-plane cross-pol reads {crossAtDiagonal:F2} dB below peak co-pol, " +
                    $"which is either zero (the decomposition is inert) or above the co-pol");
        Assert.True(pol.PrincipalPlaneCrossPolDb < -200.0,
                    $"the principal-plane cross-pol reads {pol.PrincipalPlaneCrossPolDb:F1} dB below " +
                    $"peak co-pol, which is not a structural zero");
    }

    /// <summary>
    /// <b>The decomposition is a ROTATION, so it conserves the pattern's own power exactly.</b>
    /// |E_co|² + |E_cross|² = |E_θ|² + |E_φ|² to machine precision at every direction and for every
    /// φ₀ — which means no level can move when the reference angle changes, and a metric read from U
    /// agrees with one read from the decomposition.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(37.5)]
    [InlineData(90.0)]
    [InlineData(217.0)]
    public void TheDecomposition_ConservesTheIntensityExactly(double phi0)
    {
        var grid = PlanarFarFieldGrid.Hemisphere(10, 20);
        var pattern = HandPattern(grid, (t, p) =>
            (new Complex(1.3 * Math.Cos(t * Math.PI / 180), 0.4 * Math.Sin(p * Math.PI / 90)),
             new Complex(-0.7 * Math.Sin(p * Math.PI / 180), 0.9 * Math.Cos(t * Math.PI / 360))));
        var pol = Decompose(pattern, phi0);

        double worst = 0;
        for (int k = 0; k < grid.DirectionCount; k++)
        {
            double before = pattern.ETheta[k].Magnitude * pattern.ETheta[k].Magnitude
                            + pattern.EPhi[k].Magnitude * pattern.EPhi[k].Magnitude;
            double after = Math.Pow(10, pol.CoPolDb[k] / 10.0) + Math.Pow(10, pol.CrossPolDb[k] / 10.0);
            worst = Math.Max(worst, Math.Abs(after - before) / before);
        }
        _out.WriteLine($"φ₀ = {phi0}°: worst relative |E_co|²+|E_cross|² vs |E_θ|²+|E_φ|² is {worst:E3}");
        Assert.True(worst < 1e-14, $"the rotation loses {worst:E3} of the intensity");
    }

    /// <summary>
    /// <b>φ₀ and φ₀ + 180° name the same polarization axis and give IDENTICAL magnitudes.</b> Both
    /// components change sign and nothing else, which is why ANT-6's derivation — unlike ANT-5's
    /// beamwidth cut — does not have to fold its axis toward the peak.
    /// </summary>
    [Fact]
    public void TheReferenceAngle_IsAnAxisAndNotADirection()
    {
        var grid = PlanarFarFieldGrid.Hemisphere(15, 30);
        var pattern = HandPattern(grid, (t, p) =>
            (new Complex(0.8, 0.3 * Math.Cos(p * Math.PI / 180)),
             new Complex(0.2 * Math.Sin(t * Math.PI / 180), -0.5)));

        var a = Decompose(pattern, 31.0);
        var b = Decompose(pattern, 211.0);
        for (int k = 0; k < grid.DirectionCount; k++)
        {
            Assert.Equal(a.CoPolDb[k], b.CoPolDb[k], 12);
            Assert.Equal(a.CrossPolDb[k], b.CrossPolDb[k], 12);
        }
    }

    /// <summary>The axial ratio and the sense are rotation INVARIANTS, so they cannot depend on φ₀ —
    /// which is why they are published unconditionally while the co/cross pair refuses.</summary>
    [Fact]
    public void TheAxialRatioAndSense_DoNotDependOnTheReferenceAngle()
    {
        var grid = PlanarFarFieldGrid.Hemisphere(15, 30);
        var pattern = HandPattern(grid, (t, p) =>
            (new Complex(0.8, 0.3), new Complex(0.2, -0.5 * Math.Cos(p * Math.PI / 180))));

        var a = Decompose(pattern, 0.0);
        var b = Decompose(pattern, 143.0);
        var none = PlanarPolarization.Of(pattern, EmSuitability.No("no reference"), null);
        for (int k = 0; k < grid.DirectionCount; k++)
        {
            Assert.Equal(a.AxialRatioDb[k], b.AxialRatioDb[k]);
            Assert.Equal(a.Sense[k],        b.Sense[k]);
            Assert.Equal(a.AxialRatioDb[k], none.AxialRatioDb[k]);
            Assert.Equal(a.Sense[k],        none.Sense[k]);
        }
        Assert.False(none.HasCoCross);
        Assert.Empty(none.CoPolDb);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.2 — the known circular case, and BOTH signs of the sense.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The sense oracle, and it shares no algebra with <c>PlanarPolarization</c>:</b> build the
    /// REAL field vector E(t) = Re{(E_θ θ̂ + E_φ φ̂)e^{jωt}} in Cartesian coordinates at one
    /// direction, sample it in time, and read the sign of (E × dE/dt)·r̂ by finite difference. IEEE
    /// right-handed means the vector turns the way a right hand's fingers curl with the thumb along
    /// the direction of propagation, which is exactly that sign being positive.
    /// </summary>
    private static double RotationSignByTimeSampling(Complex eth, Complex eph,
                                                     double thetaDeg, double phiDeg)
    {
        var (st, ct) = Math.SinCos(thetaDeg * Math.PI / 180.0);
        var (sp, cp) = Math.SinCos(phiDeg * Math.PI / 180.0);
        double[] th = [ct * cp, ct * sp, -st];
        double[] ph = [-sp, cp, 0];
        double[] r  = [st * cp, st * sp, ct];

        double[] At(double wt)
        {
            double a = (eth * new Complex(Math.Cos(wt), Math.Sin(wt))).Real;
            double b = (eph * new Complex(Math.Cos(wt), Math.Sin(wt))).Real;
            return [a * th[0] + b * ph[0], a * th[1] + b * ph[1], a * th[2] + b * ph[2]];
        }

        const double dt = 1e-6;
        var e0 = At(0.0);
        var e1 = At(dt);
        double[] d = [(e1[0] - e0[0]) / dt, (e1[1] - e0[1]) / dt, (e1[2] - e0[2]) / dt];
        double[] x = [e0[1] * d[2] - e0[2] * d[1],
                      e0[2] * d[0] - e0[0] * d[2],
                      e0[0] * d[1] - e0[1] * d[0]];
        return x[0] * r[0] + x[1] * r[1] + x[2] * r[2];
    }

    /// <summary>
    /// <b>§5's second gate: two orthogonal current elements in phase quadrature give axial ratio
    /// 0 dB at broadside and a KNOWN sense, and both signs are checked.</b>
    ///
    /// <para>At broadside the two element factors coincide, so the radiated pair inherits the current
    /// pair's own quadrature: currents (I_x, I_y) = (1, ∓j) give (E_θ, E_φ) ∝ (1, ∓j) about φ = 0 and
    /// therefore pure circular of one sense and then the other. <b>The sense is checked against the
    /// time-sampled rotation of the real field vector</b>, not against the same Stokes algebra that
    /// computed it — which is what pins the convention rather than assuming it. The current amplitudes
    /// are scaled by the two rooftops' own transforms so "quadrature" is a statement about the
    /// RADIATED field and not about a mesh's accidental symmetry.</para>
    /// </summary>
    [Theory]
    [InlineData(-1.0, +1.0)]   // I_y = −j·I_x  →  right-hand
    [InlineData(+1.0, -1.0)]   // I_y = +j·I_x  →  left-hand
    public void TwoOrthogonalElementsInQuadrature_AreCircularWithTheStatedSense(
        double jSign, double expectedSense)
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = TwoByTwo();
        var grid    = new PlanarFarFieldGrid([0, 30], [0, 90, 180, 270]);

        // Balance the pair on the ROOFTOP TRANSFORMS at broadside (k = 0, the dipole moments), so the
        // quadrature is in the radiated field rather than in the coefficients.
        double mx = RooftopSpectrum.Of(mesh, mesh.Bases[0], 0, 0).Real
                    + RooftopSpectrum.Of(mesh, mesh.Bases[1], 0, 0).Real;
        double my = RooftopSpectrum.Of(mesh, mesh.Bases[2], 0, 0).Real
                    + RooftopSpectrum.Of(mesh, mesh.Bases[3], 0, 0).Real;
        Complex iy = new Complex(0, jSign) * (mx / my);
        var currents = Currents(Complex.One, Complex.One, iy, iy);

        var pattern = PlanarFarField.Compute(problem, mesh, currents, 1, FHz, grid);
        var pol     = PlanarPolarization.Of(pattern, EmSuitability.Yes,
                                            new PlanarPolarizationReference(0, false, double.NaN));

        var (arDb, sense) = pol.AtBroadside;
        double oracle = RotationSignByTimeSampling(pattern.ETheta[0], pattern.EPhi[0], 0, 0);
        _out.WriteLine($"I_y = {(jSign > 0 ? "+" : "−")}j·I_x·{mx / my:F3}: broadside axial ratio " +
                       $"{arDb:E2} dB, s₃ = {sense:F12} ({PlanarPolarization.SenseWord(sense)}); " +
                       $"the time-sampled (E × Ė)·r̂ is {oracle:E3}");

        Assert.True(arDb < 1e-9, $"the broadside axial ratio reads {arDb:E3} dB, not 0");
        Assert.Equal(expectedSense, sense, 9);
        Assert.Equal(Math.Sign(expectedSense), Math.Sign(oracle));
        Assert.Equal(expectedSense > 0 ? "right-hand" : "left-hand", PlanarPolarization.SenseWord(sense));
    }

    /// <summary>
    /// <b>The exact relation between the two cubes, |s₃| = 2·AR/(AR² + 1)</b> — asserted over a
    /// pattern that sweeps the whole ellipse from linear to circular, because it is what says
    /// <c>AxialRatioDb</c> and <c>PolarizationSense</c> are the ellipse's SHAPE and its direction of
    /// travel rather than two estimates of one number.
    /// </summary>
    [Fact]
    public void TheAxialRatioAndTheSense_SatisfyTheirExactRelation()
    {
        var grid = new PlanarFarFieldGrid([0], [.. Enumerable.Range(0, 90).Select(i => i * 4.0)]);
        // φ parameterises the ellipse: E_φ/E_θ runs from 0 (linear) through ±j (circular).
        var pattern = HandPattern(grid, (t, p) =>
            (Complex.One, new Complex(0.0, Math.Sin(p * Math.PI / 180.0))));
        var pol = Decompose(pattern, 0);

        double worst = 0;
        for (int k = 0; k < grid.DirectionCount; k++)
        {
            if (pol.AxialRatioDb[k] >= PlanarPolarization.LinearAxialRatioDb) continue;
            double ar = Math.Pow(10, pol.AxialRatioDb[k] / 20.0);
            worst = Math.Max(worst, Math.Abs(Math.Abs(pol.Sense[k]) - 2 * ar / (ar * ar + 1)));
            Assert.True(pol.AxialRatioDb[k] >= 0.0, "the axial ratio in dB is never negative");
        }
        _out.WriteLine($"worst |s₃| − 2·AR/(AR²+1) over the whole ellipse family: {worst:E3}");
        Assert.True(worst < 1e-12, $"the two cubes disagree by {worst:E3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.3 — the sentinels. Stated values, not clamps, not infinities.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Pure linear polarization reads the NAMED sentinel exactly</b> — not ∞, not a NaN, and not a
    /// plausible-looking number that would make "linear" indistinguishable from "quite linear". The
    /// stable spelling of the axial ratio is what makes this exact rather than large: the obvious one
    /// subtracts two equal magnitudes here.
    /// </summary>
    [Fact]
    public void PureLinear_ReadsTheStatedAxialRatioSentinel()
    {
        var grid = new PlanarFarFieldGrid([0, 45, 90], [0, 90, 180, 270]);
        // Every direction linear: E_θ and E_φ share one phase, so Im{E_θ conj(E_φ)} is exactly 0.
        var pattern = HandPattern(grid, (t, p) =>
            (new Complex(0.6, 0.6), new Complex(0.2, 0.2)));
        var pol = Decompose(pattern, 0);

        for (int k = 0; k < grid.DirectionCount; k++)
        {
            Assert.Equal(PlanarPolarization.LinearAxialRatioDb, pol.AxialRatioDb[k]);
            Assert.Equal(0.0, pol.Sense[k]);
            Assert.False(double.IsInfinity(pol.AxialRatioDb[k]));
            Assert.False(double.IsNaN(pol.AxialRatioDb[k]));
        }
        Assert.Equal("linear", PlanarPolarization.SenseWord(pol.Sense[0]));
        Assert.Contains($"{PlanarPolarization.LinearAxialRatioDb:F0} dB",
                        PlanarPolarization.AxialRatioNote);
        Assert.Contains("LINEAR", PlanarPolarization.AxialRatioNote);

        // And a ratio genuinely PAST the sentinel saturates at it rather than running away — which is
        // what makes the sentinel a statement ("linear at this analysis's floor") and not a number.
        Assert.Equal(PlanarPolarization.LinearAxialRatioDb,
                     PlanarPolarization.AxialRatioDbOf(1.0 + 1e-16, 1e-8));
    }

    /// <summary>A direction carrying NO field has no polarization: the axial ratio takes the linear
    /// sentinel and the sense reads 0 rather than a 0/0. A structural null is the case, and a NaN in
    /// a real cube is the thing being avoided.</summary>
    [Fact]
    public void ADirectionWithNoField_TakesTheSentinelRatherThanANaN()
    {
        var grid = new PlanarFarFieldGrid([0], [0, 180]);
        var pattern = HandPattern(grid, (t, p) => (Complex.Zero, Complex.Zero));
        var pol = Decompose(pattern, 0);

        Assert.Equal(PlanarPolarization.LinearAxialRatioDb, pol.AxialRatioDb[0]);
        Assert.Equal(0.0, pol.Sense[0]);
        Assert.Equal(PlanarPolarization.FloorDb, pol.CoPolDb[0]);
        Assert.Equal(PlanarPolarization.FloorDb, pol.CrossPolDb[0]);
    }

    /// <summary>The stable spelling of the axial ratio is exact where the obvious one loses half its
    /// digits: a nearly linear direction. Checked against the circular-magnitude form at a ratio where
    /// that form still has digits, and then past it, where only the stable one can be right.</summary>
    [Theory]
    [InlineData(1e-1)]
    [InlineData(1e-3)]
    [InlineData(1e-4)]
    public void TheStableAxialRatioForm_AgreesWithTheCircularMagnitudesWhereThoseStillHaveDigits(
        double epsilon)
    {
        // E_φ = j·ε·E_θ: |E_R|/|E_L| = (1+ε)/(1−ε), so AR = 1/ε exactly.
        double t  = 1.0 + epsilon * epsilon;
        double im = epsilon;
        double db = PlanarPolarization.AxialRatioDbOf(t, im);
        double exact = 20.0 * Math.Log10(1.0 / epsilon);
        _out.WriteLine($"ε = {epsilon:E0}: AR = {db:F9} dB, exact 20log₁₀(1/ε) = {exact:F9} dB, " +
                       $"Δ = {db - exact:E2} dB");
        Assert.Equal(exact, db, 9);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.4 — the reference angle: named, derived-and-reported, or refused by name.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A NAMED reference angle always wins, and is reported as named rather than derived.</summary>
    [Fact]
    public void ANamedReferenceAngle_WinsAndIsReportedAsNamed()
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = OneXRooftop();
        var grid    = new PlanarFarFieldGrid([0, 45, 90], [0, 90, 180, 270]);
        var pattern = PlanarFarField.Compute(problem, mesh, Currents(Complex.One), 1, FHz, grid);

        var context = Context(mesh, Currents(Complex.One), pattern,
                              new PlanarMetricSettings(PolarizationReferencePhiDeg: 90.0));
        var pol = PlanarPolarization.For(context);

        Assert.True(pol.ReferenceVerdict.Ok);
        Assert.False(pol.Reference!.Derived);
        Assert.Equal(90.0, pol.Reference.PhiDeg);
        Assert.Contains("NAMED by the caller", pol.Reference.Note);
        _out.WriteLine(pol.Reference.Note);
    }

    /// <summary>
    /// <b>A derived reference angle is REPORTED, and it is the SAME NUMBER ANT-5's beamwidth cut
    /// derives</b> — because both read <c>PlanarBeamwidth.AxisAtPatternPeak</c>, which is what keeps
    /// "the dominant current axis" one physical quantity rather than two derivations of it. For an
    /// x̂-directed element that number is 0°, which is also the answer independent reasoning gives.
    /// </summary>
    [Fact]
    public void ADerivedReferenceAngle_IsReportedAndIsTheBeamwidthCutsOwnAxis()
    {
        var problem  = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh     = OneXRooftop();
        var currents = Currents(Complex.One);
        var grid     = PlanarFarFieldGrid.Hemisphere(15, 15);
        var pattern  = PlanarFarField.Compute(problem, mesh, currents, 1, FHz, grid);
        var context  = Context(mesh, currents, pattern);

        var pol = PlanarPolarization.For(context);
        Assert.True(pol.ReferenceVerdict.Ok);
        Assert.True(pol.Reference!.Derived);
        Assert.Equal(0.0, pol.Reference.PhiDeg, 9);
        Assert.Contains("DERIVED, not named", pol.Reference.Note);

        var (axis, _, _) = PlanarBeamwidth.AxisAtPatternPeak(context);
        Assert.Equal(axis, pol.Reference.PhiDeg);
        _out.WriteLine(pol.Reference.Note);

        // And the co-polar plane really is the plane the cross-pol vanishes in.
        Assert.True(pol.PrincipalPlaneCrossPolDb < -200.0,
                    $"the derived plane's cross-pol reads {pol.PrincipalPlaneCrossPolDb:F1} dB");
    }

    /// <summary>
    /// <b>An ambiguous reference angle REFUSES, and the axial ratio and the sense survive the
    /// refusal.</b> A circularly polarized current reads minor/major = 1.0 exactly — the case the
    /// refusal exists for — and the sentence points at the two cubes that DO describe such an antenna,
    /// which is the whole reason §3 put them in this phase rather than a later one.
    /// </summary>
    [Fact]
    public void AnAmbiguousReferenceAngle_RefusesByNameAndLeavesTheAxialRatio()
    {
        var problem = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh    = TwoByTwo();
        var grid    = new PlanarFarFieldGrid([0, 45], [0, 90, 180, 270]);
        double mx = RooftopSpectrum.Of(mesh, mesh.Bases[0], 0, 0).Real
                    + RooftopSpectrum.Of(mesh, mesh.Bases[1], 0, 0).Real;
        double my = RooftopSpectrum.Of(mesh, mesh.Bases[2], 0, 0).Real
                    + RooftopSpectrum.Of(mesh, mesh.Bases[3], 0, 0).Real;
        Complex iy = new Complex(0, -1) * (mx / my);
        var currents = Currents(Complex.One, Complex.One, iy, iy);

        var pattern = PlanarFarField.Compute(problem, mesh, currents, 1, FHz, grid);
        var context = Context(mesh, currents, pattern);
        var pol     = PlanarPolarization.For(context);

        Assert.False(pol.ReferenceVerdict.Ok);
        Assert.Null(pol.Reference);
        Assert.False(pol.HasCoCross);
        Assert.Empty(pol.CoPolDb);
        Assert.Empty(pol.CrossPolDb);
        Assert.Contains("no dominant LINEAR axis", pol.ReferenceVerdict.Reason);
        Assert.Contains("AxialRatioDb", pol.ReferenceVerdict.Reason);

        // What survives is exactly what describes this antenna.
        var (arDb, sense) = pol.AtBroadside;
        var (_, major, minor) = PlanarBeamwidth.AxisAtPatternPeak(context);
        _out.WriteLine($"minor/major = {minor / major:F12} (a circularly polarized current reads 1.0 " +
                       $"exactly); broadside axial ratio {arDb:E2} dB, s₃ = {sense:F9}");
        Assert.Equal(1.0, minor / major, 9);
        Assert.True(arDb < 1e-9);
        Assert.Equal(1.0, sense, 9);
        _out.WriteLine(pol.ReferenceVerdict.Reason!);
    }

    /// <summary>A structure with NO current moment at the peak has no axis to derive one from, and
    /// refuses for its own reason rather than the ambiguity one.</summary>
    [Fact]
    public void AStructureWithNoCurrentMoment_RefusesForItsOwnReason()
    {
        var problem  = SlabProblem(GroundedSlab.Fr4Starter);
        var mesh     = TwoByTwo();
        var currents = Currents(Complex.One, -Complex.One, Complex.Zero, Complex.Zero);
        var grid     = new PlanarFarFieldGrid([0], [0, 90, 180, 270]);
        var pattern  = PlanarFarField.Compute(problem, mesh, currents, 1, FHz, grid);

        var pol = PlanarPolarization.For(Context(mesh, currents, pattern));
        Assert.False(pol.ReferenceVerdict.Ok);
        Assert.Contains("CURRENT MOMENT at the", pol.ReferenceVerdict.Reason);
        Assert.Contains("PolarizationReferencePhiDeg", pol.ReferenceVerdict.Reason);
        // The axial ratio is still there, because it needs no reference.
        Assert.Equal(grid.DirectionCount, pol.AxialRatioDb.Count);
    }

    /// <summary>
    /// <b>A reference angle that disagrees across the set refuses the PAIR for the whole set</b>, in
    /// the same shape and for the same reason ANT-5's beamwidth cut axis does: a cube whose φ₀ changed
    /// halfway along its own frequency axis would be two quantities under one name, with nothing in
    /// the axis to say so. The axial ratio and the sense are published either way.
    /// </summary>
    [Fact]
    public void AReferenceAngleThatDisagreesAcrossTheSweep_RefusesThePairForTheWholeSet()
    {
        var grid = new PlanarFarFieldGrid([0], [0, 90, 180, 270]);
        PlanarPolarizationPattern At(double phi0, double fHz)
        {
            var pattern = HandPattern(grid, (t, p) => (Complex.One, new Complex(0.1, 0)));
            return PlanarPolarization.Of(
                new PlanarFarFieldPattern(grid, pattern.ETheta, pattern.EPhi, pattern.U, 1, fHz),
                EmSuitability.Yes, new PlanarPolarizationReference(phi0, true, 1e-6));
        }

        var agree = PlanarPolarizationSet.From([1e9, 2e9], [1], [At(0, 1e9), At(0, 2e9)]);
        Assert.True(agree.CoCrossVerdict.Ok);
        Assert.Equal(0.0, agree.ReferencePhiDeg);
        Assert.Empty(agree.Refusals);

        var disagree = PlanarPolarizationSet.From([1e9, 2e9], [1], [At(0, 1e9), At(37, 2e9)]);
        Assert.False(disagree.CoCrossVerdict.Ok);
        Assert.Contains("does not agree across the sweep", disagree.CoCrossVerdict.Reason);
        Assert.Contains("PolarizationReferencePhiDeg", disagree.CoCrossVerdict.Reason);
        Assert.Contains("AxialRatioDb and PolarizationSense", disagree.CoCrossVerdict.Reason);
        Assert.Single(disagree.Refusals);
        _out.WriteLine(disagree.CoCrossVerdict.Reason!);
    }

    /// <summary>
    /// <b>ANT-12 — round-off is not a disagreement, and φ₀ + 180° is not a second reference.</b> The
    /// comparison was exact equality (1e-9°) on the output of an eigen-decomposition, which refused the
    /// Ludwig-3 pair on the shipped 5.8 GHz patch and then printed its own two angles as 89.999° and
    /// 89.999°. Both halves are asserted here, plus the case that must STILL refuse: a rotation large
    /// enough to change the cube.
    /// </summary>
    [Fact]
    public void AReferenceThatDiffersOnlyByRoundOffOrBy180_IsTheSameReference()
    {
        var grid = new PlanarFarFieldGrid([0], [0, 90, 180, 270]);
        PlanarPolarizationPattern At(double phi0, double fHz)
        {
            var pattern = HandPattern(grid, (t, p) => (Complex.One, new Complex(0.1, 0)));
            return PlanarPolarization.Of(
                new PlanarFarFieldPattern(grid, pattern.ETheta, pattern.EPhi, pattern.U, 1, fHz),
                EmSuitability.Yes, new PlanarPolarizationReference(phi0, true, 1e-6));
        }

        // Round-off, at the scale the patch actually produced.
        Assert.True(PlanarPolarizationSet.From([1e9, 2e9], [1],
            [At(89.9990, 1e9), At(89.9994, 2e9)]).CoCrossVerdict.Ok);

        // The SAME plane, named the other way round: |E_co| and |E_cross| are unchanged by φ₀ → φ₀+180.
        Assert.True(PlanarPolarizationSet.From([1e9, 2e9], [1],
            [At(90.0, 1e9), At(270.0, 2e9)]).CoCrossVerdict.Ok);

        // And a real rotation still refuses — the tolerance is 0.01°, not "anything".
        var moved = PlanarPolarizationSet.From([1e9, 2e9], [1], [At(90.0, 1e9), At(90.5, 2e9)]);
        Assert.False(moved.CoCrossVerdict.Ok);
        Assert.Equal(0.01, PlanarPolarizationSet.ReferenceAgreementDeg);
        _out.WriteLine(moved.CoCrossVerdict.Reason!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5.5 — the cubes, and R-ant-8: the definition travels with the number.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-ant-8, asserted: a cross-pol number never appears without its definition.</b> The cube
    /// NAMES carry "Ludwig3" and the notes spell the definition out, so neither an exported file nor a
    /// picker can present the number bare. The brief allows either; this does both.
    /// </summary>
    [Fact]
    public void EveryCrossPolQuantity_CarriesLudwigsThirdDefinition()
    {
        Assert.Equal(4, PlanarPolarization.Cubes.Count);
        Assert.Equal(4, PlanarPolarization.Cubes.Select(c => c.CubeName).Distinct().Count());

        foreach (var (name, unit, note) in PlanarPolarization.Cubes)
        {
            Assert.False(string.IsNullOrWhiteSpace(unit));   // "" would mean "unstated", never "1"
            Assert.True(note.Length > 200, $"{name}'s note is too short to carry its definition");
        }

        foreach (string name in new[] { "CoPolLudwig3Db", "CrossPolLudwig3Db" })
        {
            Assert.Contains("Ludwig3", name);
            string note = PlanarPolarization.NoteOf(name);
            Assert.Contains("LUDWIG'S THIRD DEFINITION", note);
            Assert.Contains("φ₀", note);
        }

        // The mesh floor is said wherever cross-pol is reported (R-ant-11), and the sense convention
        // wherever a sense or an axial ratio is (R-ant-10).
        Assert.Contains("MESH", PlanarPolarization.NoteOf("CrossPolLudwig3Db"));
        Assert.Contains("IEEE", PlanarPolarization.NoteOf("PolarizationSense"));
        Assert.Contains("IEEE", PlanarPolarization.NoteOf("AxialRatioDb"));
        Assert.Contains("staircased", PlanarPolarization.MeshFloorNote);
        Assert.Contains("RESOLVED.md §ANT-6", PlanarPolarization.MeshFloorNote);
    }

    /// <summary>
    /// R-res-6 for the eighth phase running: polarization is four more cubes in ANT-4's own
    /// <c>"farfield"</c> group, on ANT-4's own axes, with no new result type — and the run's notes
    /// carry the reference angle, the sense convention and the mesh-limited floor.
    /// </summary>
    [Fact]
    public void APolarizedRun_AddsFourCubesToTheFarFieldGroup()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var far = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(30, 45));
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [FHz],
            new PlanarSolveSettings(Deembed: false, FarField: far));

        foreach (string name in new[] { "AxialRatioDb", "PolarizationSense",
                                       "CoPolLudwig3Db", "CrossPolLudwig3Db" })
        {
            var cube = result.Data[$"{PlanarFarField.Group}.{name}"];
            Assert.Equal(["freq", "theta", "phi", "port"], cube.Axes.Select(a => a.Name).ToArray());
            Assert.Equal(90.0, cube.Axes[1].Values[^1]);
            Assert.Equal(RfCore.Data.DataKind.Real, cube.DataKind);
            Assert.False(string.IsNullOrEmpty(cube.Unit));
        }
        Assert.Equal("1", result.Data[$"{PlanarFarField.Group}.PolarizationSense"].Unit);

        // No new result type, and the S cube is untouched.
        Assert.NotNull(result.Data["S"]);
        Assert.Contains(result.Notes, n => n.StartsWith("Polarization at"));
        Assert.Contains(result.Notes, n => n.Contains("Ludwig-3 reference angle was"));
        Assert.Contains(result.Notes, n => n.Contains("THE CROSS-POL FLOOR IS SET BY THE MESH"));
        _out.WriteLine(string.Join("\n\n", result.Notes.Where(
            n => n.StartsWith("Polarization at") || n.Contains("reference angle was"))));
    }

    /// <summary>
    /// <b>A refused reference angle takes the PAIR out of the cubes and puts the reason in the notes</b>
    /// — present and refused, the same shape a refused metric has — while <c>AxialRatioDb</c> and
    /// <c>PolarizationSense</c> are still published, because they need no reference.
    /// </summary>
    [Fact]
    public void ARefusedReferenceAngle_LeavesTheAxialRatioPublishedAndSaysWhy()
    {
        var grid = new PlanarFarFieldGrid([0, 45, 90], [0, 90, 180, 270]);
        var pattern = HandPattern(grid, (t, p) => (Complex.One, new Complex(0, 0.3)));
        var refused = PlanarPolarization.Of(
            pattern, EmSuitability.No("A test refusal, in the engine's own shape."), null);
        var set = PlanarPolarizationSet.From([FHz], [1], [refused]);

        Assert.False(set.CoCrossVerdict.Ok);
        Assert.Contains("CoPolLudwig3Db and CrossPolLudwig3Db are not published",
                        set.CoCrossVerdict.Reason);
        Assert.Contains("A test refusal", set.CoCrossVerdict.Reason);
        Assert.Equal(grid.DirectionCount, set.At(0, 0).AxialRatioDb.Count);
        Assert.Contains("No Ludwig-3 co/cross decomposition", set.At(0, 0).ScaleCaption);
    }

    /// <summary>R-res-8 — the caption states the scale, the convention and the floor, as one line a
    /// run prints verbatim. It is what stops a −45 dB cross-pol being read as the antenna's.</summary>
    [Fact]
    public void TheCaption_StatesTheScaleTheConventionAndTheFloor()
    {
        var grid = PlanarFarFieldGrid.Hemisphere(30, 45);
        var pattern = HandPattern(grid, (t, p) =>
            (new Complex(Math.Cos(t * Math.PI / 360), 0), new Complex(0, 0.02)));
        var caption = Decompose(pattern, 0).ScaleCaption;

        Assert.Contains("Polarization at", caption);
        Assert.Contains("port 1 driven at 1 V", caption);
        Assert.Contains("axial ratio", caption);
        Assert.Contains("IEEE", caption);
        Assert.Contains("φ₀ = 0.00°", caption);
        Assert.Contains("re 1 V, r-normalised", caption);
        _out.WriteLine(caption);
    }
}
