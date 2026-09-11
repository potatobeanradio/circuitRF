// ANT-5 — the metrics, gated in four groups.
//
// WHAT CAN BE CHECKED WITHOUT A SOLVE, AND WHAT CANNOT. Most of this phase is arithmetic over ANT-4's
// pattern, so most of it is checkable against a hand-built pattern with no matrix anywhere near it —
// and the things that are really being defended are CONVENTIONS and REFUSALS, both of which are
// structural. Two groups do need a solve:
//
//   §6.1  the registry                — no solve. One entry per metric, unique cube names, both gains
//                                       named in full, and the two STAGED refusals: front-to-back
//                                       (present, refused, and its Evaluate already correct, which is
//                                       what makes the activation one predicate) and the zero
//                                       conductor term with its note.
//   §6.2  the conventions             — one cheap solve. The two gains differ by EXACTLY the one
//                                       mismatch factor; the efficiency denominator is the accepted
//                                       power; an efficiency above 1 refuses rather than clamps.
//   §6.3  THE POWER BALANCE           — the real gate, and the only non-vacuous one available: on a
//                                       LOSSLESS substrate the dielectric residual must be zero, so
//                                       ½Re(Y_jj) from the MoM factorisation, ∫U dΩ from the far
//                                       field and the pole residues from the spectral kernel are
//                                       three independent routes forced to close on one number. Run
//                                       on two substrates of very different thickness, because a
//                                       balance that closes in one regime may close on a
//                                       cancellation.
//   §6.4  the cavity model            — an INDEPENDENT analytic oracle for a patch's directivity and
//                                       both principal cuts, written from the two-slot construction
//                                       and sharing nothing with the MoM or with SpectralGreens.
//   §6.5  direction and beamwidth     — the peak is reported rather than assumed, and every way a cut
//                                       can fail to exist refuses by name instead of defaulting to
//                                       φ = 0.
//   §6.6  the cubes                   — group, axes, and a refused metric absent from the cubes and
//                                       present as a note.

using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarMetricsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    private const double FHz = 5e9;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One x-rooftop over two cells — ANT-4's own hand mesh, so "a single current element"
    /// is expressible with no solve anywhere near it.</summary>
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

    /// <summary>A 2×2 block, which is the smallest mesh carrying BOTH an x and a y rooftop — what a
    /// circularly polarized current needs to be expressible at all. Cell order is R-msh-2's
    /// (LayerIndex, IY, IX).</summary>
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

    /// <summary>
    /// A pattern built BY HAND from a U(θ, φ) function — no solve, no Green's function. <c>E_θ</c> is
    /// given the whole of it so a metric reading the fields agrees with one reading U.
    /// </summary>
    private static PlanarFarFieldPattern HandPattern(PlanarFarFieldGrid grid, Func<double, double, double> u)
    {
        int n = grid.DirectionCount;
        var eth = new Complex[n];
        var eph = new Complex[n];
        var uu  = new double[n];
        const double eta0 = EmConstants.Mu0 * EmConstants.C0;
        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                int k = grid.IndexOf(it, ip);
                uu[k] = u(grid.ThetaDeg[it], grid.PhiDeg[ip]);
                eth[k] = Math.Sqrt(Math.Max(uu[k], 0) * 2.0 * eta0);
            }
        return new PlanarFarFieldPattern(grid, eth, eph, uu, 1, FHz);
    }

    private static PlanarMetricContext HandContext(
        PlanarFarFieldPattern pattern, PlanarMesh? mesh = null, Vec<Complex>? currents = null,
        PlanarMetricSettings? settings = null, Complex? y11 = null) =>
        new(SlabProblem(GroundedSlab.Fr4Starter), mesh ?? OneXRooftop(),
            currents ?? Currents(Complex.One), pattern, y11 ?? new Complex(0.02, -0.01), 50.0,
            settings);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.1 — the registry, and the two staged refusals. No solve.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every metric the enum names is in the registry exactly once, with a unique cube name,
    /// a note, and both delegates — which is what lets a picker, a CLI listing and an exporter all be
    /// driven off one list rather than three hand-maintained ones.</summary>
    [Fact]
    public void EveryMetricHasExactlyOneRegistryEntry_WithAUniqueCubeName()
    {
        var all = Enum.GetValues<PlanarMetric>();
        Assert.Equal(all.Length, PlanarMetrics.Registry.Count);
        Assert.Equal(all.Length, PlanarMetrics.Registry.Select(d => d.Metric).Distinct().Count());
        Assert.Equal(all.Length, PlanarMetrics.Registry.Select(d => d.CubeName).Distinct().Count());
        foreach (var m in all) Assert.Equal(m, PlanarMetrics.Of(m).Metric);
        foreach (var d in PlanarMetrics.Registry)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Note), $"{d.CubeName} has no note");
            Assert.NotNull(d.Availability);
            Assert.NotNull(d.Evaluate);
        }
        _out.WriteLine(string.Join("\n", PlanarMetrics.Registry.Select(
            d => $"{d.CubeName,-24} {d.Unit,-4} {d.Axis}")));
    }

    /// <summary>
    /// <b>R-ant-4 — no metric is called just "gain".</b> Asserted structurally rather than by reading
    /// prose: there is no cube whose name is "Gain", the two that exist are named in full, and each
    /// one's note names the other so a user who found one cannot miss that the other exists.
    /// </summary>
    [Fact]
    public void NeitherGainIsCalledGain_AndEachNotePointsAtTheOther()
    {
        var names = PlanarMetrics.Registry.Select(d => d.CubeName).ToArray();
        Assert.DoesNotContain("Gain", names);
        Assert.Contains("GainDbi", names);
        Assert.Contains("RealizedGainDbi", names);

        var g  = PlanarMetrics.Of(PlanarMetric.GainDbi);
        var rg = PlanarMetrics.Of(PlanarMetric.RealizedGainDbi);
        Assert.Contains("RealizedGainDbi", g.Note);
        Assert.Contains("EXCLUDES MISMATCH", g.Note);
        Assert.Contains("INCLUDES MISMATCH", rg.Note);
        Assert.Contains("GainDbi", rg.Note);
    }

    /// <summary>
    /// <b>§2a — front-to-back is PRESENT and REFUSED</b>, and the sentence names the reason: the ground
    /// plane is laterally infinite, so there is no lower hemisphere. Asserted so that deleting the
    /// refusal later is a test failure rather than a quiet change.
    ///
    /// <para><b>ANT-11 narrowed the tail of this sentence and this test changed with it.</b> ANT-5 ended
    /// it by naming "the finite-ground phase" as the thing that would supply the metric — a promise, and
    /// that phase measured why it cannot be kept as written (<c>PlanarFiniteGround</c>, R-fg-4/R-fg-5).
    /// The preamble asserted here is ANT-5's own and is unchanged; the problem-dependent tail is
    /// asserted in <c>PlanarFiniteGroundTests</c>, which owns both branches of it. What must NOT change
    /// is the shape: present in the registry, refused, values empty, never ∞.</para>
    /// </summary>
    [Fact]
    public void FrontToBackIsPresentInTheRegistry_AndRefusesNamingTheReason()
    {
        var report = PlanarMetrics.Evaluate(HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(10, 30), (t, _) => Math.Cos(t * Math.PI / 180))));

        var outcome = report[PlanarMetric.FrontToBackDb];
        Assert.False(outcome.Ok);
        Assert.Empty(outcome.Values);
        string why = outcome.Verdict.Reason!;
        Assert.Contains("LATERALLY INFINITE", why);
        Assert.Contains("identically zero by construction", why);
        // The fixture problem carries no ground outline, so the narrowed tail is the no-outline branch.
        Assert.Contains("no finite ground outline", why);
        // Not ∞, not a large finite number, and not missing from the registry.
        Assert.Contains(PlanarMetric.FrontToBackDb, PlanarMetrics.Registry.Select(d => d.Metric));
        _out.WriteLine(why);
    }

    /// <summary>
    /// <b>The proof that activating front-to-back is ONE PREDICATE.</b> The same registry entry, given
    /// a pattern whose θ axis reaches 180°, becomes available and returns the right number — its
    /// <c>Evaluate</c> is written and correct already. Nothing in the picker, the exporter or the cube
    /// emission has to change; the finite-ground phase moves
    /// <see cref="PlanarFarFieldGrid.MaxThetaDeg"/> and this follows.
    /// </summary>
    [Fact]
    public void FrontToBack_BecomesAvailableOnTheOnePredicate_AndItsValueIsAlreadyRight()
    {
        // Peak 1.0 at θ = 0; exactly 1/100 of it in the antipodal direction θ = 180.
        var theta = new double[] { 0, 45, 90, 135, 180 };
        var grid  = new PlanarFarFieldGrid(theta, [0, 90, 180, 270]);
        var pattern = HandPattern(grid, (t, _) => t == 180.0 ? 0.01 : Math.Cos(t * Math.PI / 360.0));

        var report = PlanarMetrics.Evaluate(HandContext(pattern));
        var outcome = report[PlanarMetric.FrontToBackDb];

        Assert.True(outcome.Ok, outcome.Verdict.Reason);
        Assert.Equal(20.0, outcome.Value, 10);
        _out.WriteLine($"θ axis to {grid.ThetaDeg[^1]}° ⇒ F/B = {outcome.Value:F6} dB");
    }

    /// <summary>
    /// <b>And it still never prints ∞.</b> A θ axis extended past 90° over a model that STILL has no
    /// lower hemisphere leaves a structural zero behind the pattern, and the ratio would be infinite —
    /// which is the one thing this metric exists to avoid. The second clause of the same availability
    /// predicate catches it, so raising <see cref="PlanarFarFieldGrid.MaxThetaDeg"/> without a
    /// finite-ground model cannot turn the staged refusal into a printed ∞.
    /// </summary>
    [Fact]
    public void AThetaAxisPast90_OverAModelWithNoBackHemisphere_StillRefusesRatherThanPrintingInfinity()
    {
        var grid = new PlanarFarFieldGrid([0, 45, 90, 135, 180], [0, 90, 180, 270]);
        var pattern = HandPattern(grid, (t, _) => t <= 90.0 ? Math.Cos(t * Math.PI / 180) : 0.0);

        var outcome = PlanarMetrics.Evaluate(HandContext(pattern))[PlanarMetric.FrontToBackDb];
        Assert.False(outcome.Ok);
        Assert.Contains("not positive", outcome.Verdict.Reason!);
        Assert.Contains("printing ∞", outcome.Verdict.Reason!);
        _out.WriteLine(outcome.Verdict.Reason);
    }

    /// <summary>
    /// <b>§6 — PowerConductor is present and zero, WITH its note.</b> This is the term most likely to
    /// be silently dropped as "not applicable"; a missing term reads as "not a factor", a zero term
    /// with this note reads as "this kernel does not model it".
    /// </summary>
    [Fact]
    public void PowerConductorIsPresentAndZero_WithItsNote()
    {
        var report = PlanarMetrics.Evaluate(HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(10, 30), (t, _) => Math.Cos(t * Math.PI / 180))));

        var outcome = report[PlanarMetric.PowerConductor];
        Assert.True(outcome.Ok);
        Assert.Equal(0.0, outcome.Value);

        string note = PlanarMetrics.Of(PlanarMetric.PowerConductor).Note;
        Assert.Contains("IDENTICALLY ZERO", note);
        Assert.Contains("perfect conductor", note);
        Assert.Contains("6.5 %", note);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.2 — the conventions, algebraically.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private (PlanarMetricReport Report, PlanarMetricContext Context) CheapSolve(
        double lengthM = 4e-3, PlanarMetricSettings? settings = null,
        System.Numerics.Complex? portReflection = null)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthM, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var ctx = new PlanarSolveContext(mesh, ports);
        var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(problem.Slab, FHz), FHz);
        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, FHz,
                                            PlanarFarFieldGrid.Hemisphere(2, 2));
        var mc = new PlanarMetricContext(problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0],
                                         ports[0].Z0, settings, null, portReflection);
        return (PlanarMetrics.Evaluate(mc), mc);
    }

    /// <summary>
    /// <b>R-ant-6 — the two gains differ by EXACTLY the one mismatch factor.</b> Trivially true if
    /// both are implemented from the same admittance, which is the point: this asserts they are not
    /// computed from two different mismatch estimates, which is how a tool ends up with a realized
    /// gain that does not correspond to its own gain.
    /// </summary>
    [Fact]
    public void TheTwoGains_DifferByExactlyTheOneMismatchFactor()
    {
        // ANT-12: the identity is asserted with a reflection SUPPLIED, because that is the only
        // state realized gain is published in now. Gamma = 0.2 is an arbitrary well-matched port;
        // the assertion is about the arithmetic, not about the value.
        var gamma = new System.Numerics.Complex(0.2, -0.1);
        var (report, mc) = CheapSolve(portReflection: gamma);
        double g  = report[PlanarMetric.GainDbi].Value;
        double rg = report[PlanarMetric.RealizedGainDbi].Value;
        double m  = mc.MismatchFactor;

        Assert.Equal(1.0 - (gamma * System.Numerics.Complex.Conjugate(gamma)).Real, m, 12);
        Assert.InRange(m, 0.0, 1.0);
        Assert.Equal(g + 10.0 * Math.Log10(m), rg, 12);
        _out.WriteLine($"gain {g:F4} dBi, realized {rg:F4} dBi, mismatch {m:F6} " +
                       $"({10 * Math.Log10(m):F3} dB), |S11|² = {1 - m:F6}");
    }

    /// <summary>
    /// <b>ANT-12 — realized gain is PRESENT and REFUSED when no port reflection is supplied</b>, which
    /// is every run today. Asserted so that publishing it again is a deliberate act: the number it
    /// published before came from the raw delta-gap self-admittance and read 15 dB low on a matched
    /// antenna, and the refusal carries the exact substitute arithmetic rather than only a reason.
    /// </summary>
    [Fact]
    public void RealizedGain_IsRefusedWithNoPortReflection_AndTheRefusalCarriesTheArithmetic()
    {
        var (report, mc) = CheapSolve();
        var outcome = report[PlanarMetric.RealizedGainDbi];

        Assert.False(outcome.Ok);
        Assert.Null(mc.PortReflection);
        Assert.True(double.IsNaN(mc.MismatchFactor));
        Assert.Contains("GainDbi + 10·log₁₀(1 − |S₁₁|²)", outcome.Verdict.Reason);
        Assert.Contains("delta-gap", outcome.Verdict.Reason);
        // Present in the registry, absent from the cubes — the FrontToBackDb staging, reused.
        Assert.Contains("RealizedGainDbi",
                        PlanarMetrics.Registry.Select(d => d.CubeName).ToArray());
        _out.WriteLine(outcome.Verdict.Reason);
    }

    /// <summary>
    /// <b>R-ant-5 — the efficiency denominator is the power ACCEPTED at the port.</b> Asserted as the
    /// identity η·P_accepted = P_radiated, which fails the moment someone divides by an incident power
    /// instead and is invisible in a plot.
    /// </summary>
    [Fact]
    public void TheEfficiencyDenominatorIsTheAcceptedPower_NotTheIncidentPower()
    {
        var (report, mc) = CheapSolve();
        double eta      = report[PlanarMetric.RadiationEfficiency].Value;
        double accepted = report[PlanarMetric.PowerAccepted].Value;
        double radiated = report[PlanarMetric.PowerRadiated].Value;

        Assert.Equal(radiated, eta * accepted, 15);
        Assert.Equal(0.5 * mc.RawSelfAdmittance.Real, accepted, 15);

        // And the gain really is D·η_rad, which is the same statement from the other side.
        Assert.Equal(report[PlanarMetric.DirectivityDbi].Value + 10.0 * Math.Log10(eta),
                     report[PlanarMetric.GainDbi].Value, 12);
        _out.WriteLine($"accepted {accepted:E6} W, radiated {radiated:E6} W, η {eta:P4}, " +
                       $"D {report[PlanarMetric.DirectivityDbi].Value:F3} dBi, " +
                       $"G {report[PlanarMetric.GainDbi].Value:F3} dBi");
    }

    /// <summary>
    /// <b>An efficiency above 1 REFUSES rather than clamps</b>, and the refusal names both numbers. A
    /// clamped efficiency reads as 100 % and hides exactly the kind of level error the itemisation
    /// exists to catch. The pattern here is scaled so it radiates more than the port accepts, which is
    /// what a wrong pattern NORMALISATION looks like.
    /// </summary>
    [Fact]
    public void RadiationEfficiencyAboveOne_IsRefusedAndNotClamped()
    {
        var pattern = HandPattern(PlanarFarFieldGrid.Hemisphere(5, 15), (t, _) => Math.Cos(t * Math.PI / 180));
        // ½·Re(Y) far smaller than ∫U dΩ, so η ≫ 1.
        var report = PlanarMetrics.Evaluate(HandContext(pattern, y11: new Complex(1e-9, 0)));

        var eff = report[PlanarMetric.RadiationEfficiency];
        Assert.False(eff.Ok);
        Assert.Empty(eff.Values);
        Assert.Contains("REFUSED rather than clamped", eff.Verdict.Reason!);
        Assert.Contains("above 1", eff.Verdict.Reason!);

        // The terms it is built from are still published, which is what makes the refusal actionable.
        Assert.True(report[PlanarMetric.PowerAccepted].Ok);
        Assert.True(report[PlanarMetric.PowerRadiated].Ok);
        _out.WriteLine(eff.Verdict.Reason);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.3 — THE POWER BALANCE. The gate.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Three independent routes closing on one number, on two substrates of very different
    /// thickness.</b> With tanδ = 0, PEC metal and a PEC floor there is nowhere for accepted power to
    /// go but the upper hemisphere and the substrate's guided modes, so the dielectric RESIDUAL must
    /// be zero — and the three quantities reaching that are ½Re(Y_jj) out of the MoM factorisation,
    /// ∫U dΩ out of the far field, and the pole residues out of the spectral kernel. None of them
    /// shares an implementation with another.
    ///
    /// <para><b>Two thicknesses because a balance that closes in one regime may be closing on a
    /// cancellation</b>: the 8 mm case books a third of its accepted power into the surface wave and
    /// the 1.6 mm case a fifth, so the surface-wave term is a real fraction in both and a sign or
    /// factor error in it cannot hide.</para>
    ///
    /// <para><b>The tolerance is 1e-3 and the residual is dominated by the MATRIX FILL, not by
    /// anything here</b> — measured, and recorded in <c>RESOLVED.md</c> §ANT-5 with the numbers: it is
    /// ~1e-5 on the shipped FR-4 cross-section and ~1e-2 on 100 µm GaAs, tracks how hard the DCIM fit
    /// is rather than the mesh density, and does not move when the far-field quadrature is refined by
    /// 10×.</para>
    /// </summary>
    [Theory]
    [InlineData(1.6e-3, 4.4, 2.9e-3)]     // the shipped FR-4 starter cross-section, loss removed
    [InlineData(8.0e-3, 2.2, 20e-3)]      // five times thicker, low permittivity
    public void ThePowerBalanceCloses_OnALosslessSubstrate(double h, double epsR, double widthM)
    {
        var slab = new GroundedSlab(h, new EmMaterial(epsR, 0));
        double lambda = EmConstants.C0 / (FHz * Math.Sqrt(0.5 * (epsR + 1)));
        var problem = PlanarLineFixtures.Line(slab, widthM, 0.5 * lambda, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var ctx = new PlanarSolveContext(mesh, ports);
        var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(slab, FHz), FHz);

        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, FHz,
                                            PlanarFarFieldGrid.Hemisphere(1, 2));
        var budget = PlanarPowerBudget.For(problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0]);

        _out.WriteLine($"h = {h * 1e3:F2} mm, εᵣ = {epsR}, N = {mesh.Bases.Count}");
        _out.WriteLine(budget.Caption);
        _out.WriteLine(budget.SurfaceWave!.Caption);

        double residual = budget.DielectricW / budget.AcceptedW;
        double swShare  = budget.SurfaceWaveW / budget.AcceptedW;
        _out.WriteLine($"dielectric residual = {residual:E3} of accepted; surface wave = {swShare:P2}");

        Assert.True(budget.AcceptedW > 0, "a driven passive structure accepts power");
        Assert.True(budget.RadiatedW > 0, "an open planar structure radiates");
        Assert.True(budget.SurfaceWaveW > 0, "a grounded slab guides a TM₀ mode at any thickness");
        Assert.InRange(swShare, 0.05, 0.95);
        Assert.True(Math.Abs(residual) < 1e-3,
            $"a LOSSLESS substrate absorbs nothing, so the dielectric residual must vanish; it is " +
            $"{residual:E3} of the accepted power ({SurfaceMesher.Eng(budget.DielectricW)}W of " +
            $"{SurfaceMesher.Eng(budget.AcceptedW)}W)");
        Assert.Equal(0.0, budget.ConductorW);
    }

    /// <summary>
    /// Turning the loss on moves the residual INTO the dielectric term and nowhere else: the radiated
    /// and guided terms stay the same order while the dielectric term goes from nothing to most of the
    /// budget. This is the statement the lossless gate above licenses.
    /// </summary>
    [Fact]
    public void ALossySubstrate_PutsTheResidualIntoTheDielectricTerm()
    {
        PlanarPowerBudget At(double tanD)
        {
            var slab = new GroundedSlab(1.6e-3, new EmMaterial(4.4, tanD));
            var problem = PlanarLineFixtures.Line(slab, 2.9e-3, 20e-3, FHz);
            var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
            var ctx = new PlanarSolveContext(mesh, ports);
            var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(slab, FHz), FHz);
            var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, FHz,
                                                PlanarFarFieldGrid.Hemisphere(1, 2));
            return PlanarPowerBudget.For(problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0]);
        }

        var lossless = At(0.0);
        var lossy    = At(0.02);
        _out.WriteLine("tanδ = 0:    " + lossless.Caption);
        _out.WriteLine("tanδ = 0.02: " + lossy.Caption);

        Assert.True(Math.Abs(lossless.DielectricW / lossless.AcceptedW) < 1e-3);
        Assert.True(lossy.DielectricW / lossy.AcceptedW > 0.1,
            "a 20 mm FR-4 line at 5 GHz absorbs a real fraction of what it accepts");
        Assert.True(lossy.RadiationEfficiency < lossless.RadiationEfficiency,
            "adding dielectric loss cannot raise the radiation efficiency");
        Assert.Equal(0.0, lossy.ConductorW);
    }

    /// <summary>
    /// The azimuth rule for the surface-wave integral is the periodic rectangle, so it is converged at
    /// a sample count far below the default. Measured here rather than assumed, because the default
    /// being 4× more than it needs to be is the cheap direction to be wrong in and the other one is
    /// not.
    /// </summary>
    [Fact]
    public void TheAzimuthRule_IsAlreadyConvergedAt90Samples()
    {
        var slab = new GroundedSlab(1.6e-3, new EmMaterial(4.4, 0));
        var problem = PlanarLineFixtures.Line(slab, 2.9e-3, 20e-3, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var ctx = new PlanarSolveContext(mesh, ports);
        var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(slab, FHz), FHz);

        double Sw(int n) =>
            PlanarSurfaceWaveLaunch.Compute(problem, mesh, sol.Currents[0], FHz, n).TotalW;

        double reference = Sw(1440);
        foreach (int n in new[] { 90, 180, 360 })
        {
            double rel = Math.Abs(Sw(n) - reference) / reference;
            _out.WriteLine($"N_φ = {n,5}: {Sw(n):E12} W, {rel:E2} from the 1440-sample value");
            Assert.True(rel < 1e-9, $"N_φ = {n} disagrees by {rel:E2}");
        }
    }

    /// <summary>
    /// <b>The one-slab line voltage is written as <c>(Z₀^p/2)(1 + Γ^p)</c> and the general cascade
    /// computes it from scratch; they must be the same number.</b> This is what licenses the slab
    /// branch being an identity rather than an approximation — and R-ant-2 still has the FILL choose
    /// which of the two a problem takes.
    /// </summary>
    [Fact]
    public void TheTwoSpectralKernels_AgreeOnTheLineVoltage()
    {
        var slab = GroundedSlab.Fr4Starter;
        var one  = PlanarSpectralVoltages.For(SlabProblem(slab), FHz);
        var gen  = PlanarSpectralVoltages.For(SlabProblem(slab) with
                       { MediumStack = LayerStack.FromGroundedSlab(slab) }, FHz);

        Assert.False(one.IsGeneral);
        Assert.True(gen.IsGeneral);

        double worst = 0;
        foreach (double r in new[] { 0.0, 0.3, 0.9, 1.0001, 1.5, 2.0, 2.09, 3.0, 10.0 })
            foreach (var pol in new[] { SurfaceWavePolarization.Tm, SurfaceWavePolarization.Te })
            {
                var a = one.Voltage(pol, r * one.K0, 0, 0);
                var b = gen.Voltage(pol, r * one.K0, 0, 0);
                worst = Math.Max(worst, (a - b).Magnitude / a.Magnitude);
            }
        _out.WriteLine($"worst relative disagreement over k_ρ/k₀ ∈ [0, 10]: {worst:E3}");
        Assert.True(worst < 1e-12, $"the two kernels disagree by {worst:E3}");
    }

    /// <summary>
    /// <b>The surface-wave pole really is the SIMPLE pole the residue treats it as.</b> Two checks
    /// that share nothing with the power integral: the contour residue is the same at two radii an
    /// order of magnitude apart, and V itself behaves as R/(k − k_p) on the real axis either side of
    /// the pole. A double pole or a branch point inside the contour would fail both.
    /// </summary>
    [Fact]
    public void TheSurfaceWavePole_IsTheSimplePoleTheResidueTreatsItAs()
    {
        var slab  = new GroundedSlab(1.6e-3, new EmMaterial(4.4, 0));
        var v     = PlanarSpectralVoltages.For(SlabProblem(slab), FHz);
        var modes = SurfaceWavePoles.Find(LayerStack.FromGroundedSlab(slab), FHz);
        var tm    = modes.Modes.Single(m => m.Polarization == SurfaceWavePolarization.Tm);

        Complex F(Complex k) => v.Voltage(SurfaceWavePolarization.Tm, k, 0, 0);
        double  r1 = 1e-4 * v.K0, r2 = 1e-5 * v.K0;
        var     a  = PlanarSurfaceWaveLaunch.Residue(F, tm.KRho, r1);
        var     b  = PlanarSurfaceWaveLaunch.Residue(F, tm.KRho, r2);
        _out.WriteLine($"TM₀ at k_ρ/k₀ = {tm.KRho.Real / v.K0:F8}; residue {a} vs {b}");
        Assert.True((a - b).Magnitude / a.Magnitude < 1e-7,
            $"the residue moved with the contour radius: {a} against {b}");

        // V ≈ R/(k − k_p) a short way off the pole, on both sides.
        foreach (double s in new[] { -1.0, 1.0 })
        {
            Complex k = tm.KRho + s * r2;
            Complex predicted = a / (k - tm.KRho);
            double rel = (F(k) - predicted).Magnitude / predicted.Magnitude;
            _out.WriteLine($"  at k_p {s:+0;-0}·{r2 / v.K0:E0}·k₀: V = {F(k)}, R/(k−k_p) = {predicted}, {rel:E2}");
            Assert.True(rel < 1e-3, $"V is not R/(k−k_p) near the pole: {rel:E2}");
        }
    }

    /// <summary>
    /// <b>A substrate too absorptive for a separable guided mode refuses the surface-wave term BY
    /// NAME — and takes the dielectric RESIDUAL with it</b>, because the residual's meaning depends on
    /// the term that was taken out of it. Reporting accepted − radiated under the dielectric name
    /// would be exactly the double count the note warns about, so the combined remainder is left for
    /// the caller to form out of the two cubes that are published.
    /// </summary>
    [Fact]
    public void ADeliberatelyAbsorptiveSubstrate_RefusesTheSurfaceWaveAndTheResidualWithIt()
    {
        // A THICK, high-permittivity, very lossy slab. Measured (RESOLVED.md §ANT-5): a barely bound
        // mode keeps most of its energy in the air above, so 1.6 mm FR-4 reads |Im k_ρ|/Re k_ρ = 1.2e-4
        // even at tanδ = 1 and is nowhere near this ceiling; it takes a mode genuinely inside the
        // dielectric (k_ρ/k₀ = 2.65 here) to reach it.
        var slab = new GroundedSlab(8e-3, new EmMaterial(10.0, 0.3));
        var problem = PlanarLineFixtures.Line(slab, 6e-3, 12e-3, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        var verdict = PlanarSurfaceWaveLaunch.CanCompute(problem, FHz);
        Assert.False(verdict.Ok);
        Assert.Contains("ceiling this residue is good to", verdict.Reason!);
        _out.WriteLine(verdict.Reason);

        var ctx = new PlanarSolveContext(mesh, ports);
        var sol = ctx.SolveAt(PlanarLineFixtures.Kernel(slab, FHz), FHz);
        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, FHz,
                                            PlanarFarFieldGrid.Hemisphere(5, 10));
        var report = PlanarMetrics.Evaluate(new PlanarMetricContext(
            problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0], ports[0].Z0));

        Assert.False(report[PlanarMetric.PowerSurfaceWave].Ok);
        Assert.False(report[PlanarMetric.PowerDielectric].Ok);
        Assert.Contains("double count", report[PlanarMetric.PowerDielectric].Verdict.Reason!);

        // Everything that does not depend on the itemisation still ships.
        Assert.True(report[PlanarMetric.PowerAccepted].Ok);
        Assert.True(report[PlanarMetric.PowerRadiated].Ok);
        Assert.True(report[PlanarMetric.RadiationEfficiency].Ok);
        Assert.True(report[PlanarMetric.DirectivityDbi].Ok);
        Assert.True(report[PlanarMetric.PowerConductor].Ok);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.4 — the cavity model. An independent analytic oracle.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The rectangular-patch CAVITY MODEL, from the two-slot construction, sharing nothing with the
    /// MoM or with <c>SpectralGreens</c>.</b> The TM₁₀ cavity has <c>E_z = E₀cos(πx/L)</c> and
    /// magnetic walls, so the two slots at x = 0 and x = L each carry <c>M_s = −2E₀ŷ</c> — in phase,
    /// spaced L. A ŷ-directed magnetic current radiates <c>E ∝ r̂ × ŷ = cosθ sinφ φ̂ − cosφ θ̂</c>,
    /// and the slot aperture contributes <c>sinc(k₀W sinθ sinφ/2)</c> with the two-element array
    /// factor <c>cos(k₀L sinθ cosφ/2)</c>. Hence
    /// <c>U ∝ sinc²(X)·cos²(Y)·(cos²φ + cos²θ sin²φ)</c>.
    /// </summary>
    private static double CavityU(double k0, double L, double W, double thetaRad, double phiRad)
    {
        double x = 0.5 * k0 * W * Math.Sin(thetaRad) * Math.Sin(phiRad);
        double y = 0.5 * k0 * L * Math.Sin(thetaRad) * Math.Cos(phiRad);
        double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(x) / x;
        double ct = Math.Cos(thetaRad), cp = Math.Cos(phiRad), sp = Math.Sin(phiRad);
        return sinc * sinc * Math.Cos(y) * Math.Cos(y) * (cp * cp + ct * ct * sp * sp);
    }

    private static double CavityDirectivityDbi(double k0, double L, double W, int n = 720)
    {
        double integral = 0, dt = (Math.PI / 2) / n, dp = 2 * Math.PI / (2 * n);
        for (int i = 0; i < n; i++)
        {
            double th = (i + 0.5) * dt, row = 0;
            for (int j = 0; j < 2 * n; j++) row += CavityU(k0, L, W, th, (j + 0.5) * dp);
            integral += row * dp * Math.Sin(th) * dt;
        }
        return 10 * Math.Log10(4 * Math.PI * CavityU(k0, L, W, 0, 0) / integral);
    }

    /// <summary>
    /// <b>§5's first gate: the cavity model agrees on a patch's directivity and on both principal
    /// cuts.</b> The patch is edge-fed at x = 0 on the shipped 1.6 mm FR-4 starter stackup
    /// (h/λ₀ = 0.013, thin enough for the cavity model to be trustworthy), with L set to half a guided
    /// wavelength.
    ///
    /// <para><b>Measured, and both halves are worth recording.</b> Directivity comes out 0.48 dB above
    /// the cavity model's and is MESH-INDEPENDENT to 0.006 dB from N = 237 to N = 3,831 — so the
    /// difference is the model's, not the mesh's: the cavity model has no surface wave and no feed.
    /// The H-plane cut agrees to 0.11 dB out to θ = 80° and the E-plane to 0.57 dB, the E-plane being
    /// the one the infinite ground plane changes most.</para>
    /// </summary>
    [Fact]
    public void TheCavityModel_AgreesOnPatchDirectivityAndOnBothPrincipalCuts()
    {
        const double f = 2.4e9;
        var slab = GroundedSlab.Fr4Starter;
        double k0 = 2 * Math.PI * f / EmConstants.C0, lambda0 = EmConstants.C0 / f;
        double L = 0.49 * lambda0 / Math.Sqrt(slab.Material.EpsR), W = 1.25 * L;

        var problem = PlanarLineFixtures.Problem(slab, f,
            PlanarLineFixtures.Rect(0, -0.5 * W, L, 0.5 * W));
        var mesh = SurfaceMesher.Mesh(problem,
            new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false)).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh,
            [new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50.0)]);
        var sol = new PlanarSolveContext(mesh, ports)
                      .SolveAt(PlanarLineFixtures.Kernel(slab, f), f);

        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], 1, f,
                                            PlanarFarFieldGrid.Hemisphere(1, 2));
        var report = PlanarMetrics.Evaluate(new PlanarMetricContext(
            problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0], 50.0));

        double mom = report[PlanarMetric.DirectivityDbi].Value;
        double cav = CavityDirectivityDbi(k0, L, W);
        _out.WriteLine($"patch {L * 1e3:F2} × {W * 1e3:F2} mm on {slab.HeightM * 1e3} mm FR-4, " +
                       $"h/λ₀ = {slab.HeightM / lambda0:F4}, N = {mesh.Bases.Count}");
        _out.WriteLine($"directivity: MoM {mom:F3} dBi, cavity model {cav:F3} dBi, Δ {mom - cav:F3} dB");
        Assert.True(Math.Abs(mom - cav) < 1.0,
            $"the MoM reads {mom:F3} dBi against the cavity model's {cav:F3} dBi");

        foreach (double phiDeg in new double[] { 0, 90 })
        {
            int ip = PlanarMetrics.Nearest(pattern.Grid.PhiDeg, phiDeg);
            double peakM = 0, peakC = 0;
            for (int it = 0; it < pattern.Grid.ThetaDeg.Count; it++)
            {
                peakM = Math.Max(peakM, pattern.U[pattern.Grid.IndexOf(it, ip)]);
                peakC = Math.Max(peakC, CavityU(k0, L, W, pattern.Grid.ThetaDeg[it] * Math.PI / 180,
                                                phiDeg * Math.PI / 180));
            }
            double worst = 0;
            for (int it = 0; it <= 80; it++)
            {
                double a = 10 * Math.Log10(pattern.U[pattern.Grid.IndexOf(it, ip)] / peakM);
                double b = 10 * Math.Log10(CavityU(k0, L, W, it * Math.PI / 180,
                                                   phiDeg * Math.PI / 180) / peakC);
                worst = Math.Max(worst, Math.Abs(a - b));
            }
            _out.WriteLine($"  φ = {phiDeg}° cut: worst disagreement {worst:F3} dB over θ ∈ [0, 80°]");
            Assert.True(worst < 1.0, $"the φ = {phiDeg}° cut disagrees by {worst:F3} dB");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.5 — direction and beamwidth.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The peak direction is REPORTED and is not assumed to be broadside.</b> A 3 λ_g line is the
    /// cheap structure whose peak is nowhere near it — measured at θ = 31° — and the derived beamwidth
    /// cut follows the peak rather than defaulting to φ = 0.
    /// </summary>
    [Fact]
    public void ThePeakDirection_IsReportedAndIsNotAssumedToBeBroadside()
    {
        var problem = PlanarLineFixtures.LineOfWavelengths(
            GroundedSlab.Fr4Starter, PlanarLineFixtures.Fr4HeroWidthM, 3.0, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var sol = new PlanarSolveContext(mesh, ports)
                      .SolveAt(PlanarLineFixtures.Kernel(problem.Slab, FHz), FHz);
        var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], 1, FHz,
                                            PlanarFarFieldGrid.Hemisphere(1, 2));
        var report = PlanarMetrics.Evaluate(new PlanarMetricContext(
            problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0], 50.0));

        double th = report[PlanarMetric.DirectivityPeakThetaDeg].Value;
        _out.WriteLine($"N = {mesh.Bases.Count}: peak at θ = {th}°, φ = " +
                       $"{report[PlanarMetric.DirectivityPeakPhiDeg].Value}°, " +
                       $"D = {report[PlanarMetric.DirectivityDbi].Value:F3} dBi, " +
                       $"beamwidth {report[PlanarMetric.BeamwidthDeg].Value:F2}° in the derived cut");
        Assert.True(th > 10.0, $"the peak of a 3 λ_g line is not at broadside; it reads θ = {th}°");
        Assert.True(report[PlanarMetric.DirectivityPeakPhiDeg].Ok);
        Assert.True(report[PlanarMetric.BeamwidthDeg].Ok);
    }

    /// <summary>
    /// <b>The azimuth of a BROADSIDE peak is refused by name</b> — at θ = 0 every φ names the same
    /// direction, so reporting the first grid value would look like a measurement of a direction. The
    /// θ cube still says where the peak is, which is what makes the refusal informative rather than a
    /// hole.
    /// </summary>
    [Fact]
    public void TheAzimuthOfABroadsidePeak_IsRefusedByName()
    {
        var report = PlanarMetrics.Evaluate(HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(5, 15), (t, _) => Math.Cos(t * Math.PI / 180))));

        Assert.True(report[PlanarMetric.DirectivityPeakThetaDeg].Ok);
        Assert.Equal(0.0, report[PlanarMetric.DirectivityPeakThetaDeg].Value);

        var phi = report[PlanarMetric.DirectivityPeakPhiDeg];
        Assert.False(phi.Ok);
        Assert.Contains("BROADSIDE", phi.Verdict.Reason!);
        _out.WriteLine(phi.Verdict.Reason);
    }

    /// <summary>A NAMED cut is used as named, reported as not derived, and lands on the cut axis.</summary>
    [Fact]
    public void ANamedCut_IsUsedAsNamedAndReportedAsNotDerived()
    {
        var (report, mc) = CheapSolve(settings: new PlanarMetricSettings([0.0, 90.0]));

        Assert.True(mc.Cuts.Verdict.Ok, mc.Cuts.Verdict.Reason);
        Assert.Equal(2, mc.Cuts.Cuts.Count);
        Assert.All(mc.Cuts.Cuts, c => Assert.False(c.Derived));
        Assert.Equal([0.0, 90.0], report.CutsPhiDeg);
        Assert.Equal(2, report[PlanarMetric.BeamwidthDeg].Values.Count);
        Assert.Contains("NAMED by the caller", mc.Cuts.Note);
        _out.WriteLine(mc.Cuts.Note);
        foreach (var c in mc.Cuts.Cuts)
            _out.WriteLine($"  φ = {c.PhiDeg}°: {c.BeamwidthDeg:F2}°, cut peak at θ = {c.PeakThetaDeg}°");
    }

    /// <summary>
    /// A DERIVED cut is reported as derived, with the axis it was derived from, and it is not silently
    /// φ = 0 — on this fixture the derivation and φ = 0 happen to coincide, so what is asserted is that
    /// the REPORT says which it was.
    /// </summary>
    [Fact]
    public void ADerivedCut_IsReportedAsDerivedWithTheAxisItCameFrom()
    {
        var (report, mc) = CheapSolve();
        Assert.True(mc.Cuts.Verdict.Ok, mc.Cuts.Verdict.Reason);
        Assert.All(mc.Cuts.Cuts, c => Assert.True(c.Derived));
        Assert.Contains("DERIVED, not named", mc.Cuts.Note);
        Assert.Contains("dominant axis", mc.Cuts.Note);
        Assert.Single(report.CutsPhiDeg);
        _out.WriteLine(mc.Cuts.Note);
    }

    /// <summary>
    /// <b>ANT-12 — a cut at φ and a cut at φ + 180° are ONE plane, and the set must not refuse over
    /// them.</b> A cut runs from −θ_max through broadside to +θ_max with the negative half on the
    /// φ + 180° branch, so 90° and 270° sample the same two half-planes. Comparing the raw values
    /// refused the beamwidth for the whole of the shipped 5.8 GHz patch's sweep, because the derived
    /// fold flipped at two frequencies. The genuine one-grid-step rotation still refuses.
    /// </summary>
    [Fact]
    public void CutsAtPhiAndPhiPlus180_AreOnePlane_AndDoNotRefuseTheSet()
    {
        var (a, _) = CheapSolve(settings: new PlanarMetricSettings([90.0]));
        var (b, _) = CheapSolve(settings: new PlanarMetricSettings([270.0]));

        var same = PlanarMetricSet.From([FHz, 2 * FHz], [1], [a, b]);
        Assert.True(same.Publishable(PlanarMetric.BeamwidthDeg).Ok,
                    same.Publishable(PlanarMetric.BeamwidthDeg).Reason);

        // The two cuts are the same plane, so they must also have measured the same beamwidth.
        Assert.Equal(a[PlanarMetric.BeamwidthDeg].Values[0],
                     b[PlanarMetric.BeamwidthDeg].Values[0], 9);

        var (c, _) = CheapSolve(settings: new PlanarMetricSettings([60.0]));
        var moved = PlanarMetricSet.From([FHz, 2 * FHz], [1], [a, c]);
        Assert.False(moved.Publishable(PlanarMetric.BeamwidthDeg).Ok);
        _out.WriteLine(moved.Publishable(PlanarMetric.BeamwidthDeg).Reason!);
    }

    /// <summary>
    /// <b>ANT-12 — the derived fold is NOT taken against a broadside peak's azimuth.</b> At θ_peak = 0
    /// every azimuth names the same direction, which is why <c>DirectivityPeakPhiDeg</c> refuses there;
    /// folding the current axis against that azimuth made the reported plane a function of which grid
    /// value the peak search landed on. With no usable azimuth the plane is named once, in [0, 180).
    /// </summary>
    [Fact]
    public void ADerivedCutAtBroadside_IsNamedInTheCanonicalHalf()
    {
        var (_, mc) = CheapSolve();
        Assert.True(mc.Peak.AzimuthIsDegenerate, $"peak θ = {mc.Peak.ThetaDeg}");
        Assert.True(mc.Cuts.Verdict.Ok, mc.Cuts.Verdict.Reason);
        Assert.All(mc.Cuts.Cuts, c => Assert.InRange(c.RequestedPhiDeg, 0.0, 180.0 - 1e-12));
        _out.WriteLine(mc.Cuts.Note);
    }

    /// <summary>
    /// <b>A circularly polarized current REFUSES the beamwidth rather than inventing a plane.</b> Two
    /// crossed rooftops driven in quadrature make the current moment's polarization ellipse a circle,
    /// so the minor/major ratio reads 1 exactly — which is the case this refusal exists for, and which
    /// the brief names as the same class of error as inventing a current direction for the mesher.
    /// </summary>
    [Fact]
    public void ACircularlyPolarizedCurrent_RefusesTheBeamwidthByName()
    {
        var mesh = TwoByTwo();
        var mc = HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(5, 15), (t, _) => Math.Cos(t * Math.PI / 180)),
            mesh, Currents(1, 1, Complex.ImaginaryOne, Complex.ImaginaryOne));

        var (axis, major, minor) = PlanarBeamwidth.DominantAxis(mesh, mc.BasisCurrents);
        _out.WriteLine($"axis {axis:F2}°, major {major:E3}, minor {minor:E3}, ratio {minor / major:F6}");
        Assert.Equal(1.0, minor / major, 9);

        var outcome = PlanarMetrics.Evaluate(mc)[PlanarMetric.BeamwidthDeg];
        Assert.False(outcome.Ok);
        Assert.Contains("CIRCULARLY POLARIZED", outcome.Verdict.Reason!);
        Assert.Contains("inventing a current direction for the mesher", outcome.Verdict.Reason!);
        _out.WriteLine(outcome.Verdict.Reason);
    }

    /// <summary>
    /// A grid sampling only half the azimuth circle has HALF A CUT, and half a cut has one half-power
    /// point. Refused by name, naming both azimuths the cut needs.
    /// </summary>
    [Fact]
    public void AHalfCircleAzimuthGrid_RefusesTheBeamwidthByName()
    {
        var grid = new PlanarFarFieldGrid([0, 30, 60, 90], [0, 45, 90, 135]);
        var outcome = PlanarMetrics.Evaluate(HandContext(
            HandPattern(grid, (t, _) => Math.Cos(t * Math.PI / 180))))[PlanarMetric.BeamwidthDeg];

        Assert.False(outcome.Ok);
        Assert.Contains("φ + 180° branch", outcome.Verdict.Reason!);
        _out.WriteLine(outcome.Verdict.Reason);
    }

    /// <summary>
    /// With no current there is no current moment, so there is no axis to derive a plane from — and
    /// this is the branch a balanced structure whose moment cancels in every direction would take. The
    /// directivity refuses alongside it, for its own reason, which is what the registry's per-metric
    /// verdicts are for.
    /// </summary>
    [Fact]
    public void AZeroCurrentMoment_RefusesTheBeamwidthByName()
    {
        var report = PlanarMetrics.Evaluate(HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(10, 30), (_, _) => 0.0),
            currents: Currents(Complex.Zero)));

        var bw = report[PlanarMetric.BeamwidthDeg];
        Assert.False(bw.Ok);
        Assert.Contains("CURRENT MOMENT at the peak direction is zero", bw.Verdict.Reason!);
        Assert.False(report[PlanarMetric.DirectivityDbi].Ok);
        _out.WriteLine(bw.Verdict.Reason);
    }

    /// <summary>
    /// A cut with no half-power crossing inside the sampled range refuses rather than reporting twice
    /// the range — "the beam is wider than what was sampled" and "the beamwidth is 180°" are different
    /// statements. An isotropic pattern is the cleanest way to have no crossing at all.
    /// </summary>
    [Fact]
    public void ACutWithNoHalfPowerCrossing_RefusesRatherThanReportingTheRange()
    {
        var outcome = PlanarMetrics.Evaluate(HandContext(
            HandPattern(PlanarFarFieldGrid.Hemisphere(5, 15), (_, _) => 1.0)))[PlanarMetric.BeamwidthDeg];

        Assert.False(outcome.Ok);
        Assert.Contains("no 3 dB crossing", outcome.Verdict.Reason!);
        Assert.Contains("different", outcome.Verdict.Reason!);
        _out.WriteLine(outcome.Verdict.Reason);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6.6 — the cubes.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-res-6 for the seventh phase running: the metrics are more cubes in ANT-4's own group.</b>
    /// Each is <c>[freq, port]</c>, except the beamwidth, which carries its cut between them — which is
    /// how "report the cut alongside the number" is satisfied without a second cube to go and read. A
    /// REFUSED metric is absent from the cubes and present as a note in the registry's own wording.
    /// </summary>
    [Fact]
    public void TheMetricCubes_LandInTheFarFieldGroupWithTheAxesTheyClaim()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var far = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(10, 30));
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [FHz],
            new PlanarSolveSettings(Deembed: false, FarField: far));

        Assert.NotNull(result.Solve.Metrics);
        var set = result.Solve.Metrics!;

        foreach (var d in PlanarMetrics.Registry)
        {
            string key = $"{PlanarFarField.Group}.{d.CubeName}";
            bool publishable = set.Publishable(d.Metric).Ok;
            Assert.Equal(publishable, result.Data.Contains(key));
            if (!publishable) continue;

            var cube = result.Data[key];
            string[] expected = d.Axis == PlanarMetricAxis.PerCut
                ? ["freq", "cut", "port"] : ["freq", "port"];
            Assert.Equal(expected, cube.Axes.Select(a => a.Name).ToArray());
            Assert.Equal("Hz", cube.Axes[0].Unit);
            Assert.Equal(2, cube.Axes[^1].Values.Length);        // the port axis, from ANT-4's first commit
            if (d.Axis == PlanarMetricAxis.PerCut) Assert.Equal("deg", cube.Axes[1].Unit);
        }

        // Front-to-back is the refused one, and its sentence is in the run's notes.
        Assert.False(result.Data.Contains($"{PlanarFarField.Group}.FrontToBackDb"));
        // ANT-11: the sentence narrowed. This fixture's problem carries no ground outline (nothing in
        // the fixture draws one), so the tail is the no-outline branch and it names the action.
        Assert.Contains(result.Notes, n => n.Contains("no finite ground outline"));
        Assert.Contains(result.Notes, n => n.Contains("Power budget at"));
        Assert.Contains(result.Notes, n => n.StartsWith("Two corrections to read this efficiency by"));

        // The S cube is untouched: the metrics ADD cubes, they do not change the result type.
        Assert.NotNull(result.Data["S"]);
        _out.WriteLine(string.Join("\n", result.Data.CubesIn(PlanarFarField.Group).Keys));
        foreach (string n in result.Notes.Where(n => n.Contains("Power budget") || n.Contains("Surface-wave")))
            _out.WriteLine(n);
    }

    /// <summary>
    /// The beamwidth cube's cut axis carries the φ that was actually used, and a named pair of cuts
    /// lands on it in order — so a number can never be read out of this cube without its plane.
    /// </summary>
    [Fact]
    public void TheBeamwidthCube_CarriesItsCutOnItsOwnAxis()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var far = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(10, 30),
                                             Metrics: new PlanarMetricSettings([0.0, 90.0]));
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [FHz],
            new PlanarSolveSettings(Deembed: false, FarField: far));

        var cube = result.Data[$"{PlanarFarField.Group}.BeamwidthDeg"];
        Assert.Equal(["freq", "cut", "port"], cube.Axes.Select(a => a.Name).ToArray());
        Assert.Equal([0.0, 90.0], cube.Axes[1].Values);
        Assert.Equal("deg", cube.Axes[1].Unit);
        _out.WriteLine($"cut axis = [{string.Join(", ", cube.Axes[1].Values)}] deg");
    }
}
