// ANT-11 — the finite ground plane: what was BUILT (§2's outline and its note), what was REFUSED
// (§3's edge-diffraction estimate), and the measurements that decided between them.
//
//   §A  R-fg-4 — the rim illumination is identically zero. The measurement that refused the estimate,
//       taken on the element factors directly, on both kernels and four stacks, and then end to end on
//       a pattern. This is the whole of ANT-11's case and it is a NEGATIVE result.
//   §B  A live ANT-4 defect the same measurement turned up: NaN in the grazing row.
//   §C  R-fg-1 — reading the outline changes nothing. Bit-identity of the pattern and every metric.
//   §D  R-fg-3 — the three size measures, each against a rectangle whose answer is arithmetic.
//   §E  §5's own gate list — the note appears, the refusal narrowed, and it did not become a deletion.
//
// R-fg-5's measurement (UTD's exactness for a half-plane) is `UtdHalfPlaneMeasurementTests`, which
// shares nothing with this file.

using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFiniteGroundTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    private const double FHz = 1.74e9;                        // the measured board's own resonance

    /// <summary>203.2 µm of FR-4 prepreg — the stackup the antenna series was measured on.</summary>
    private static GroundedSlab Prepreg => new(203.2e-6, new EmMaterial(4.4, 0.02));

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

    private static PlanarProblem Slab(GroundedSlab slab, double fHz = FHz) =>
        new([new PlanarConductorLayer("Metal", [PlanarLineFixtures.Rect(-1e-3, -1e-3, 1e-3, 1e-3)],
                                      5.8e7, 35e-6)], slab, fHz);

    /// <summary>The same one-level structure as an EXPLICIT stack, which is what selects the general
    /// stratified kernel — whose element factors come from a cascade traversal and share no algebra
    /// with the one-slab expressions.</summary>
    private static PlanarProblem General(GroundedSlab slab, double fHz = FHz) =>
        Slab(slab, fHz) with { MediumStack = LayerStack.FromGroundedSlab(slab) };

    /// <summary>A BURIED level — a conductor on the first interface of a two-layer stack, with 500 µm
    /// of cover above it. This is the configuration whose f_TE is read off the cross-region VOLTAGE and
    /// whose f_TM was a NaN at grazing (§B).</summary>
    private static PlanarProblem Buried(double fHz = FHz)
    {
        var stack = new LayerStack(
            Termination.Pec,
            [new MediumLayer(203.2e-6, new EmMaterial(4.4, 0.02)),
             new MediumLayer(500e-6,   new EmMaterial(3.0, 0.001))],
            Termination.Air);
        return new PlanarProblem(
            [new PlanarConductorLayer("M1", [PlanarLineFixtures.Rect(-1e-3, -1e-3, 1e-3, 1e-3)],
                                      5.8e7, 35e-6, 203.2e-6)],
            Prepreg, fHz, null, stack);
    }

    private static Vec<Complex> UnitCurrent(int n)
    {
        var v = new Vec<Complex>(n);
        for (int i = 0; i < n; i++) v[i] = Complex.One;
        return v;
    }

    /// <summary>A 70 × 70 mm pour centred on the origin — the measured board's own ground plane,
    /// which is 0.406 λ₀ at 1.74 GHz.</summary>
    private static PlanarGroundOutline Pour(string name = "Inner 1", double sideM = 70e-3) =>
        new(name, [PlanarLineFixtures.Rect(-sideM / 2, -sideM / 2, sideM / 2, sideM / 2)]);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §A — R-fg-4. THE MEASUREMENT THAT REFUSED THE ESTIMATE.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Both element factors are EXACTLY zero at θ = 90°, in every kernel and on every stack.</b>
    /// This is the input ANT-11's edge-diffraction estimate would have taken: the pattern in the
    /// direction the ground rim lies in. It is not small, it is zero, and the two polarizations get
    /// there independently — TM by its cos θ, TE by (1 + Γ^h) vanishing at k_ρ = k₀.
    ///
    /// <para><b>Exact equality is asserted, not a tolerance</b>, because the claim is a theorem about
    /// the geometry and a tolerance would let a future change put a small number there and pass. The
    /// zero is also the fix for §B's NaN, so this test guards both.</para>
    /// </summary>
    [Fact]
    public void AtGrazing_BothElementFactorsAreExactlyZero_InEveryKernelAndStack()
    {
        (string Name, PlanarProblem P)[] cases =
        [
            ("FR-4 1.6 mm, one-slab kernel",   Slab(GroundedSlab.Fr4Starter)),
            ("FR-4 1.6 mm, general kernel",    General(GroundedSlab.Fr4Starter)),
            ("FR-4 203 µm, one-slab kernel",   Slab(Prepreg)),
            ("FR-4 203 µm, general kernel",    General(Prepreg)),
            ("GaAs 100 µm, one-slab kernel",   Slab(GroundedSlab.GaAsStarter)),
            ("GaAs 100 µm, general kernel",    General(GroundedSlab.GaAsStarter)),
            ("buried level, two-layer stack",  Buried()),
        ];

        foreach (var (name, p) in cases)
        {
            var f = FarFieldElementFactors.For(p, FHz);
            var (tm, te) = f.At(Math.PI / 2, 0);
            _out.WriteLine($"  {name,-32} f_TM = {tm}, f_TE = {te}");
            Assert.Equal(Complex.Zero, tm);
            Assert.Equal(Complex.Zero, te);
        }
    }

    /// <summary>
    /// <b>And it is a genuine limit, not a clamp.</b> Off grazing both factors are finite, non-zero and
    /// approach that zero LINEARLY in cos θ — a factor of ten per decade of (90° − θ). A clamp would
    /// show as a plateau or a discontinuity; this shows the arithmetic away from the degeneracy is
    /// untouched and is converging on the value the exact limit supplies.
    /// </summary>
    [Fact]
    public void TheGrazingZeroIsALimit_ApproachedLinearlyInCosTheta_NotAClamp()
    {
        var f = FarFieldElementFactors.For(Slab(Prepreg), FHz);
        // 89° is NOT yet in the linear regime on this stack — |f_TM| is nearly flat from broadside to
        // 89° (1.48e-2 to 1.09e-2) because the cos θ factor has barely begun to act, so the 89 → 89.9
        // decade reads a ratio of 3.3. The linear approach is the ASYMPTOTIC statement and has to be
        // measured where the asymptote holds; a ladder starting at 89° measures the crossover instead.
        double[] deg = [89.9, 89.99, 89.999];
        var tm = new double[deg.Length];
        var te = new double[deg.Length];

        for (int i = 0; i < deg.Length; i++)
        {
            var (a, b) = f.At(deg[i] * Math.PI / 180.0, 0);
            (tm[i], te[i]) = (a.Magnitude, b.Magnitude);
            _out.WriteLine($"  θ = {deg[i],7:F2}°   |f_TM| = {tm[i]:E4}   |f_TE| = {te[i]:E4}");
        }

        foreach (var series in new[] { tm, te })
            for (int i = 1; i < series.Length; i++)
            {
                Assert.True(series[i] > 0, "the factor is zero away from grazing");
                double ratio = series[i - 1] / series[i];
                Assert.True(ratio > 8.0 && ratio < 12.0,
                            $"decade {i}: ratio {ratio:F2}, expected ≈ 10 (linear in cos θ)");
            }
    }

    /// <summary>
    /// <b>End to end, through the real pattern: the horizon is a STRUCTURAL zero, and one row in from
    /// it the pattern is barely down at all.</b> That gap is the whole of ANT-11's case. An estimate
    /// driven from the grazing row has nothing to be driven by, at any ground size, so it would be
    /// identically zero everywhere and would pass the brief's converge-to-the-infinite-limit self-test
    /// vacuously.
    /// </summary>
    [Fact]
    public void ARealPatternsGrazingRow_IsAStructuralZero_WhileOneRowInIsBarelyDown()
    {
        var pattern = PlanarFarField.Compute(Slab(Prepreg), OneXRooftop(), UnitCurrent(1), 1, FHz,
                                             PlanarFarFieldGrid.Hemisphere(5, 15));
        double peak = pattern.PeakIntensityWPerSr;
        int nt = pattern.Grid.ThetaDeg.Count;

        double RowPeak(int it)
        {
            double u = 0;
            for (int ip = 0; ip < pattern.Grid.PhiDeg.Count; ip++)
                u = Math.Max(u, pattern.U[pattern.Grid.IndexOf(it, ip)]);
            return u;
        }

        foreach (int it in new[] { 0, nt / 2, nt - 2, nt - 1 })
            _out.WriteLine($"  θ = {pattern.Grid.ThetaDeg[it],5:F1}°   " +
                           $"{10 * Math.Log10(RowPeak(it) / peak),9:F2} dB rel peak");

        double horizonDb = 10 * Math.Log10(RowPeak(nt - 1) / peak);
        double oneInDb   = 10 * Math.Log10(RowPeak(nt - 2) / peak);

        // The horizon is round-off against the peak — far below anything a model could mean.
        Assert.True(horizonDb < -250.0, $"the grazing row reads {horizonDb:F2} dB, not a structural zero");
        // …and θ = 85° is within a few dB of broadside, so this is not a pattern that simply died.
        Assert.True(oneInDb > -5.0, $"θ = 85° reads {oneInDb:F2} dB; the pattern is not still alive there");
        _out.WriteLine($"\n  horizon {horizonDb:F2} dB vs one row in {oneInDb:F2} dB — " +
                       $"recorded as {PlanarFiniteGround.MeasuredGrazingLevelDb:F2} dB on the harness fixture");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §B — THE ANT-4 DEFECT THE MEASUREMENT TURNED UP.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A buried level over a stratified stack used to put a NaN in the last row of the DEFAULT
    /// hemisphere grid</b>, and the NaN reached every metric: <c>U</c>, then
    /// <see cref="PlanarFarFieldPattern.RadiatedPowerW"/>, then directivity, both gains, efficiency and
    /// beamwidth. <c>CanCompute</c> said Ok and the only visible symptom was the scale caption reading
    /// "integrates to NaNW".
    ///
    /// <para>Cause: that configuration reads f_TM off the CROSS-REGION voltage, whose generalised
    /// transmission factor divides by the top region's TM characteristic impedance — which is exactly
    /// zero at grazing. <c>ZRatio</c> is cross-multiplied so a vanishing Z cannot produce a NaN, and the
    /// transmission factor then divides by it again and loses the guard. The limit is zero, which §A
    /// asserts; this test is the regression that the whole chain downstream is finite.</para>
    /// </summary>
    [Fact]
    public void ABuriedLevelOverAStratifiedStack_HasNoNaNInItsGrazingRow_NorInAnyMetric()
    {
        var problem = Buried();
        var mesh = OneXRooftop();
        Assert.True(PlanarFarField.CanCompute(problem, mesh, FHz).Ok);

        var pattern = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz,
                                             PlanarFarFieldGrid.Hemisphere(10, 45));

        Assert.Equal(90.0, pattern.Grid.ThetaDeg[^1]);          // the degenerate row IS in the default grid
        for (int i = 0; i < pattern.U.Count; i++)
        {
            Assert.True(double.IsFinite(pattern.U[i]), $"U[{i}] = {pattern.U[i]}");
            Assert.True(double.IsFinite(pattern.ETheta[i].Real) &&
                        double.IsFinite(pattern.ETheta[i].Imaginary), $"Eθ[{i}] = {pattern.ETheta[i]}");
            Assert.True(double.IsFinite(pattern.EPhi[i].Real) &&
                        double.IsFinite(pattern.EPhi[i].Imaginary), $"Eφ[{i}] = {pattern.EPhi[i]}");
        }

        Assert.True(double.IsFinite(pattern.RadiatedPowerW) && pattern.RadiatedPowerW > 0,
                    $"RadiatedPowerW = {pattern.RadiatedPowerW}");
        Assert.DoesNotContain("NaN", pattern.ScaleCaption);

        var report = PlanarMetrics.Evaluate(new PlanarMetricContext(
            problem, mesh, UnitCurrent(1), pattern, new Complex(0.02, -0.01), 50.0));
        foreach (var o in report.Outcomes)
            foreach (double v in o.Values)
                Assert.True(double.IsFinite(v), $"{o.Definition.CubeName} = {v}");

        _out.WriteLine($"  radiated {pattern.RadiatedPowerW:E6} W, " +
                       $"D = {report[PlanarMetric.DirectivityDbi].Value:F3} dBi, " +
                       $"η = {report[PlanarMetric.RadiationEfficiency].Value:F3} %");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §C — R-fg-1. READING THE OUTLINE CHANGES NOTHING.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The outline is a DESCRIBED BOUNDARY, and this is what that means in numbers.</b> The same
    /// problem with and without a ground outline produces a bit-identical pattern and bit-identical
    /// metrics — §5's "the primary cube is unchanged, byte for byte" applied to the only thing ANT-11
    /// added to the problem type. Equality, not a tolerance: an outline that moved any published number
    /// by one bit would mean it had reached the medium, the mesh or the fill.
    /// </summary>
    [Fact]
    public void AGroundOutline_ChangesNoPatternValueAndNoMetric_BitForBit()
    {
        var bare    = Slab(Prepreg);
        var withPour = bare with { GroundOutline = Pour() };

        Assert.False(bare.RequiresGeneralKernel);
        // The one thing that must NOT change: an outline may not select a different kernel.
        Assert.Equal(bare.RequiresGeneralKernel, withPour.RequiresGeneralKernel);

        var mesh = OneXRooftop();
        var grid = PlanarFarFieldGrid.Hemisphere(10, 45);
        var a = PlanarFarField.Compute(bare, mesh, UnitCurrent(1), 1, FHz, grid);
        var b = PlanarFarField.Compute(withPour, mesh, UnitCurrent(1), 1, FHz, grid);

        for (int i = 0; i < a.U.Count; i++)
        {
            Assert.Equal(a.U[i], b.U[i]);
            Assert.Equal(a.ETheta[i], b.ETheta[i]);
            Assert.Equal(a.EPhi[i], b.EPhi[i]);
        }

        var ra = PlanarMetrics.Evaluate(new PlanarMetricContext(
            bare, mesh, UnitCurrent(1), a, new Complex(0.02, -0.01), 50.0));
        var rb = PlanarMetrics.Evaluate(new PlanarMetricContext(
            withPour, mesh, UnitCurrent(1), b, new Complex(0.02, -0.01), 50.0));

        foreach (var m in Enum.GetValues<PlanarMetric>())
        {
            // Front-to-back is the ONE outcome an outline is allowed to change, and only its SENTENCE.
            if (m == PlanarMetric.FrontToBackDb) continue;
            Assert.Equal(ra[m].Ok, rb[m].Ok);
            Assert.Equal(ra[m].Values, rb[m].Values);
        }
        _out.WriteLine($"  {a.U.Count} directions and " +
                       $"{Enum.GetValues<PlanarMetric>().Length - 1} metrics identical bit for bit");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §D — R-fg-3. THE THREE MEASURES.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The three size measures against a rectangle whose answers are arithmetic: a 70 × 70 mm pour at
    /// 1.74 GHz is 0.4063 λ₀ a side, 0.4585 λ₀ in equal-area diameter (2/√π times the side), and — with
    /// a 49.4 mm patch inside it — 0.0599 λ₀ of margin. <b>The last two differ from the first by 13%
    /// and by a factor of SEVEN</b>, which is why all three are reported and none is called "the size".
    /// </summary>
    [Fact]
    public void TheThreeSizeMeasures_AreEachWhatTheyClaim_AndDifferFromEachOther()
    {
        var pour = Pour();
        double lambda = EmConstants.C0 / FHz;
        var metal = (-49.4e-3 / 2, -41.3e-3 / 2, 49.4e-3 / 2, 41.3e-3 / 2);

        var e = PlanarGroundExtent.Of(pour, metal, FHz);
        Assert.NotNull(e);

        _out.WriteLine($"  λ₀ = {lambda * 1e3:F2} mm");
        _out.WriteLine($"  box            {e.BoxWidthLambda:F4} × {e.BoxHeightLambda:F4} λ₀");
        _out.WriteLine($"  equal-area dia {e.EquivalentDiameterLambda:F4} λ₀");
        _out.WriteLine($"  margin         {e.MarginLambda:F4} λ₀");

        Assert.Equal(70e-3 / lambda, e.BoxWidthLambda, 12);
        Assert.Equal(70e-3 / lambda, e.BoxHeightLambda, 12);
        // A square's equal-area circle has diameter 2a/√π, i.e. 1.1284 × the side.
        Assert.Equal(70e-3 * 2.0 / Math.Sqrt(Math.PI) / lambda, e.EquivalentDiameterLambda, 12);
        // The worst of the four sides: (70 − 49.4)/2 = 10.3 mm, not (70 − 41.3)/2 = 14.35 mm.
        Assert.Equal(10.3e-3 / lambda, e.MarginLambda, 12);

        // And the point of reporting all three: the box reads SEVEN times the margin.
        Assert.True(e.BoxWidthLambda / e.MarginLambda > 6.0);
    }

    /// <summary>
    /// <b>A NEGATIVE margin is a real condition and is reported as one.</b> Metal overhanging the pour
    /// means the artwork claims a return that is not under it — clamping that at zero would hide the
    /// one case where the infinite-plane model is not merely optimistic but describes a different
    /// structure.
    /// </summary>
    [Fact]
    public void MetalOverhangingThePour_ReportsANegativeMargin_RatherThanZero()
    {
        var small = Pour(sideM: 20e-3);
        var e = PlanarGroundExtent.Of(small, (-15e-3, -5e-3, 15e-3, 5e-3), FHz);
        Assert.NotNull(e);
        _out.WriteLine($"  30 mm of metal over a 20 mm pour ⇒ margin {e.MarginLambda:F4} λ₀");
        Assert.True(e.MarginLambda < 0, $"margin {e.MarginLambda:F4} λ₀ was clamped");
        Assert.Equal(-5e-3 / (EmConstants.C0 / FHz), e.MarginLambda, 12);
    }

    /// <summary>
    /// <b>No outline is not a zero-sized one.</b> Both the extent and the run's note are null, so a
    /// single-conductor run with no pour drawn says nothing rather than reporting a plane of no size.
    /// </summary>
    [Fact]
    public void NoOutline_ProducesNoExtentAndNoNote_RatherThanZero()
    {
        Assert.Null(PlanarGroundExtent.Of(null, null, FHz));
        Assert.Null(PlanarGroundExtent.Of(new PlanarGroundOutline("Inner 1", []), null, FHz));
        Assert.Null(PlanarGroundExtent.SweepNote(null, null, 1e9, 2e9));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §E — §5's GATE LIST.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§5 — the ground-size note appears on a run that has an outline, whether or not anything was
    /// estimated</b>, and it carries the physical size, the electrical size at BOTH ends of the sweep,
    /// the margin, and the one-directional statement about which way the published numbers are wrong.
    /// </summary>
    [Fact]
    public void TheGroundSizeNote_StatesPhysicalAndElectricalSizeAcrossTheSweep()
    {
        string note = PlanarGroundExtent.SweepNote(
            Pour(), (-49.4e-3 / 2, -41.3e-3 / 2, 49.4e-3 / 2, 41.3e-3 / 2), 1e9, 3e9)!;
        _out.WriteLine(note);

        Assert.Contains("70 × 70 mm", note);
        Assert.Contains("Inner 1", note);
        Assert.Contains("1 GHz", note);
        Assert.Contains("3 GHz", note);
        Assert.Contains("laterally INFINITE", note);
        Assert.Contains("no back radiation at all", note);
        Assert.Contains("margin is the number to read", note);

        // A single-frequency sweep says so instead of quoting a span of one.
        string one = PlanarGroundExtent.SweepNote(Pour(), null, FHz, FHz)!;
        Assert.DoesNotContain("rising to", one);
        Assert.Contains("equal-area", one);
    }

    /// <summary>
    /// <b>§5 — the refusal NARROWED and did not become a deletion.</b> Two different problems, two
    /// different sentences, both refusals: with no outline it names what is missing and what drawing it
    /// buys; with an outline it quotes that outline's own size and gives the MEASURED reason the
    /// correction is still refused at that size. ANT-5's version was one constant for both.
    /// </summary>
    [Fact]
    public void TheFiniteGroundRefusal_IsAFunctionOfTheProblem_AndRefusesDifferentlyEitherWay()
    {
        var bare = Slab(Prepreg);
        var pour = bare with { GroundOutline = Pour() };

        var without = PlanarFiniteGround.CanCorrect(bare, FHz, bare.MetalBounds());
        var with    = PlanarFiniteGround.CanCorrect(pour, FHz, pour.MetalBounds());

        Assert.False(without.Ok);
        Assert.False(with.Ok);
        Assert.NotEqual(without.Reason, with.Reason);

        _out.WriteLine("── no outline ──\n" + without.Reason);
        _out.WriteLine("\n── with outline ──\n" + with.Reason);

        Assert.Contains("no finite ground outline", without.Reason!);
        Assert.Contains("Drawing the pour", without.Reason!);
        Assert.DoesNotContain("IS present", without.Reason!);

        Assert.Contains("IS present", with.Reason!);
        Assert.Contains("0.406", with.Reason!);                       // the outline's own size, quoted
        Assert.Contains("zero exactly", with.Reason!);                // the measured reason
        Assert.Contains("vertical basis is separately", with.Reason!); // why the two sets cannot meet
        Assert.Contains("laterally infinite", with.Reason!);           // and the mechanism that is absent
    }

    /// <summary>
    /// <b>§5 — <c>FrontToBackDb</c> still refuses, and the metric's sentence is the narrowed one.</b>
    /// The registry entry is unchanged in shape — present, refused, values empty — and its availability
    /// now reads the one predicate, so the phase that supplies a lower hemisphere flips
    /// <see cref="PlanarFiniteGround.CanCorrect"/> and nothing else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrontToBackStillRefuses_WithAndWithoutAnOutline_AndQuotesTheNarrowedReason(bool outline)
    {
        var problem = outline ? Slab(Prepreg) with { GroundOutline = Pour() } : Slab(Prepreg);
        var mesh = OneXRooftop();
        var pattern = PlanarFarField.Compute(problem, mesh, UnitCurrent(1), 1, FHz,
                                             PlanarFarFieldGrid.Hemisphere(10, 45));
        var outcome = PlanarMetrics.Evaluate(new PlanarMetricContext(
            problem, mesh, UnitCurrent(1), pattern, new Complex(0.02, -0.01), 50.0))
            [PlanarMetric.FrontToBackDb];

        Assert.False(outcome.Ok);
        Assert.Empty(outcome.Values);
        Assert.Contains(PlanarMetric.FrontToBackDb, PlanarMetrics.Registry.Select(d => d.Metric));

        string why = outcome.Verdict.Reason!;
        // The preamble ANT-5 wrote survives; the tail is now the problem-dependent half.
        Assert.Contains("identically zero by construction", why);
        Assert.Contains(outline ? "IS present" : "no finite ground outline", why);
        _out.WriteLine(why);
    }
}
