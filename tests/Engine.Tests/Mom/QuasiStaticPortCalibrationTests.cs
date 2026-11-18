// QSC — "a 3.8 mm microstrip cannot be simulated from 100 MHz because de-embedding it needs a
// 161.5 mm calibration standard."
//
// RAW1 §6 left that open and named the reason: the low-frequency wall is the price of MEASURING γ
// from two full-wave line standards (D5), and nothing else. The two lines' length difference has to
// sit inside TRL's usable interval βΔℓ ∈ [20°, 160°], so it scales as 1/f_lo — 161.5 mm against a
// 3.8 mm DUT, 79,055 unknowns against a 12,000 ceiling, and the run refuses.
//
// The hole in the obvious fix, and this file exists partly to keep it shut: SUPPLYING γ DOES NOT BY
// ITSELF REMOVE THE LONG STANDARDS. γ is already an input to LineDeembed.SolveErrorBox, and the
// error box still needs two lines — one line gives two complex equations for three complex unknowns.
// What a known γ buys is that Δℓ need not be ELECTRICALLY long, because the usable interval protects
// the EXTRACTION of γ and nothing else. So Δℓ is sized from the substrate and the mesh instead, both
// standards become small and frequency-independent, and the sub-band ladder collapses to one.
//
// Five things are gated here:
//
//   Q1  The crossover is a property of the STACK, is not a user setting, and covers the reported
//       band whole.
//   Q2  A band entirely ABOVE the crossover builds exactly the ladder it always did. This brief adds
//       a path; it does not re-point the existing one.
//   Q3  Below the crossover the standards stop growing with the band edge — which is the defect.
//   Q4  The two paths agree where both run, at EVERY frequency in the overlap.
//   Q5  The a₂₂ SIGN margin survives the short standards — and a LONGER quasi-static separation is
//       worse, not better, which is why the separation is capped from above as well as floored.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class QuasiStaticPortCalibrationTests(ITestOutputHelper output)
{
    /// <summary>The board the series was reported on — 20 mil-class laminate, εᵣ ≈ 4.</summary>
    private static GroundedSlab ReportedBoard => new(0.6e-3, new EmMaterial(4.0, 0.02));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Q1 — the crossover
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>f_cross = 0.03·c / (h√(εᵣ−1)), and the grouping is what makes ONE number cover a 1.6 mm
    /// FR-4 board and a 0.1 mm MMIC.</b>
    ///
    /// <para>M1 measured quasi-static γ against the two-line γ per frequency on three stacks at two
    /// mesh densities and read the ~1 % crossover at ≈ 3 GHz on FR-4 and ≈ 10 GHz on the reported
    /// board. Those are ratios of 0.0295 and 0.0347; 0.03 sits at the conservative end, which is the
    /// right end — below the crossover the quasi-static value is the MORE accurate of the two, above
    /// it the measured one is.</para>
    ///
    /// <para>The assertion is deliberately loose. A crossover is a budget, not a threshold the
    /// physics has; what must not drift is the ORDER — GaAs far above FR-4, and the reported band
    /// entirely below its own board's.</para>
    /// </summary>
    [Fact]
    public void TheCrossoverIsAPropertyOfTheStackAndCoversTheReportedBandWhole()
    {
        double fr4    = PlanarCalibration.QuasiStaticCrossoverHz(GroundedSlab.Fr4Starter);
        double board  = PlanarCalibration.QuasiStaticCrossoverHz(ReportedBoard);
        double gaAs   = PlanarCalibration.QuasiStaticCrossoverHz(GroundedSlab.GaAsStarter);

        output.WriteLine($"FR-4 1.6 mm      {fr4 / 1e9:0.###} GHz   (M1 measured ~3 GHz)");
        output.WriteLine($"reported 0.6 mm  {board / 1e9:0.###} GHz   (M1 measured ~10 GHz)");
        output.WriteLine($"GaAs 0.1 mm      {gaAs / 1e9:0.###} GHz   (M1: agrees to 0.5-1.3% at 6-20 GHz)");

        Assert.InRange(fr4,   2.0e9,  4.5e9);
        Assert.InRange(board, 6.0e9, 12.0e9);
        Assert.InRange(gaAs, 18.0e9, 35.0e9);

        // THE POINT OF THE WHOLE BRIEF: the band the series was reported on — 100 MHz to 6 GHz on
        // that board — sits entirely below its own crossover, so every point of it takes the
        // quasi-static path and none of it needs a standard sized by λ.
        Assert.True(6e9 < board,
                    $"the reported band's top ({6e9 / 1e9:0.#} GHz) must be under that board's " +
                    $"crossover ({board / 1e9:0.###} GHz), or the wall is only partly removed");

        // ── AND THE SECOND LIMIT, WHICH IS NOT DECORATION ────────────────────────────────────
        //
        // An air substrate has no DIELECTRIC dispersion, so the first term alone answers "+∞" —
        // true of the MODE and false of the STRUCTURE. A coplanar pair 5 mm above its plane in air
        // at 10 GHz is a sixth of a free-space wavelength thick and its measured β sits 3.6 % off
        // k₀. `ElectricalThicknessCrossoverRatio` is the h/λ₀ limit that catches it, and it is a
        // MINIMUM, so it can only move a point back onto the measured path.
        double air5mm = PlanarCalibration.QuasiStaticCrossoverHz(
            new GroundedSlab(5e-3, new EmMaterial(1.0, 0.0)));
        output.WriteLine($"5 mm AIR slab   {air5mm / 1e9:0.###} GHz — finite, and below the 10 GHz " +
                         "the coplanar fixtures run at");
        Assert.True(air5mm < 10e9,
                    "a homogeneous substrate must not claim the quasi-static path at every frequency");

        // …and it is INERT on every stack M1 measured: the dispersion term binds first on all three,
        // so it moved no number this brief recorded.
        foreach (var (name, slab) in new (string, GroundedSlab)[]
                 { ("FR-4", GroundedSlab.Fr4Starter), ("board", ReportedBoard),
                   ("GaAs", GroundedSlab.GaAsStarter) })
        {
            double byThickness = PlanarCalibrationSettings.ElectricalThicknessCrossoverRatio *
                                 EmConstants.C0 / slab.HeightM;
            double actual = PlanarCalibration.QuasiStaticCrossoverHz(slab);
            output.WriteLine($"{name,-6} dispersion {actual / 1e9:0.###} GHz vs thickness " +
                             $"{byThickness / 1e9:0.###} GHz");
            Assert.True(actual < byThickness,
                        $"the thickness limit must not bind on {name}, or it changed a measured number");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Q2 — a band above the crossover is untouched
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>This brief adds a path below a crossover; it does not re-point the one above it.</b>
    ///
    /// <para>Asserted structurally rather than by comparing s-parameters, because structural is
    /// stronger here: a band whose bottom is at or above the crossover gets
    /// <see cref="PlanarCalibration.SuggestDeltas"/>'s own array, entry for entry, and no
    /// quasi-static entry at all — so <see cref="PlanarCalibration.BuildSet"/> builds the same
    /// standards from the same targets and every number downstream is bit-identical by construction.
    /// The measured calibration is right up there and the whole L8/L9 acceptance set rests on it.</para>
    /// </summary>
    [Theory]
    [InlineData(4e9, 20e9)]
    [InlineData(6e9, 12e9)]
    [InlineData(3.2e9, 6e9)]
    public void ABandEntirelyAboveTheCrossoverBuildsExactlyTheLadderItAlwaysDid(double fLo, double fHi)
    {
        var slab = GroundedSlab.Fr4Starter;
        Assert.True(fLo >= PlanarCalibration.QuasiStaticCrossoverHz(slab), "precondition");

        var plan = PlanarCalibration.SeparationPlan(slab, fLo, fHi);
        var old  = PlanarCalibration.SuggestDeltas(slab, fLo, fHi);

        output.WriteLine($"{fLo / 1e9:0.##}-{fHi / 1e9:0.##} GHz: " +
                         $"[{string.Join(", ", plan.DeltaLM.Select(d => $"{d * 1e3:0.####} mm"))}] " +
                         $"qsIndex={plan.QuasiStaticIndex}");

        Assert.Equal(-1, plan.QuasiStaticIndex);
        Assert.Equal(old.Length, plan.DeltaLM.Length);
        for (int i = 0; i < old.Length; i++) Assert.Equal(old[i], plan.DeltaLM[i]);   // bit for bit

        // …and nothing up there is ever asked the quasi-static question.
        Assert.False(plan.IsQuasiStaticAt(fLo));
        Assert.False(plan.IsQuasiStaticAt(fHi));
    }

    /// <summary>
    /// <b>A CALIBRATION GROUP stays on the measured ladder, and the decline is by name.</b> PCAL4's
    /// modal error box separates N modes by the DIFFERENCE of their electrical lengths over Δℓ
    /// (<see cref="PlanarCalibrationSettings.ModeSeparationFloorDegrees"/>); a substrate-sized Δℓ
    /// drives every one of those differences toward zero at the bottom of a band, so the
    /// quasi-static path would turn that refusal from a rare event into the normal case.
    /// </summary>
    [Fact]
    public void ACalibrationGroupIsDeclinedAndKeepsTheMeasuredLadder()
    {
        // A coupled pair close enough that PCAL2 would refuse the two feeds separately, which is
        // exactly the geometry PCAL4 calibrates as one group.
        var slab = GroundedSlab.Fr4Starter;
        double w = 2.9e-3, gap = 0.5 * slab.HeightM, len = 12e-3;
        var problem = PlanarLineFixtures.Problem(slab, 6e9,
            PlanarLineFixtures.Rect(0, 0, len, w),
            PlanarLineFixtures.Rect(0, w + gap, len, w + gap + w));
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(0,   0.5 * w),             PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(len, 0.5 * w),             PlanarPortSide.MaxX, 50.0),
            new PlanarPort(3, new EmPoint(0,   w + gap + 0.5 * w),   PlanarPortSide.MinX, 50.0),
            new PlanarPort(4, new EmPoint(len, w + gap + 0.5 * w),   PlanarPortSide.MaxX, 50.0),
        ]);

        int k = PlanarCalibration.EndRunCellsFor(ports[0], slab);
        var group = PlanarPorts.TryFormCalibrationGroup(
            mesh, ports[0], ports, k, 5.0 * slab.HeightM, 3, PlanarConductors.Of(mesh), out _);
        Assert.NotNull(group);
        var grouped = ports[0] with { Group = group };

        // The UNGROUPED port on the same band takes the quasi-static path…
        var single = PlanarCalibration.SeparationPlan(slab, 100e6, 6e9, ports[0]);
        Assert.True(single.QuasiStaticIndex >= 0);

        // …and the GROUPED one does not, by name, in the one function that decides.
        var asGroup = PlanarCalibration.SeparationPlan(slab, 100e6, 6e9, grouped);
        output.WriteLine($"ungrouped: {single.DeltaLM.Length} separation(s), qsIndex " +
                         $"{single.QuasiStaticIndex}; grouped: {asGroup.DeltaLM.Length}, qsIndex " +
                         $"{asGroup.QuasiStaticIndex}");

        Assert.Equal(-1, asGroup.QuasiStaticIndex);
        var old = PlanarCalibration.SuggestDeltas(slab, 100e6, 6e9);
        Assert.Equal(old.Length, asGroup.DeltaLM.Length);
        for (int i = 0; i < old.Length; i++) Assert.Equal(old[i], asGroup.DeltaLM[i]);
        Assert.False(asGroup.IsQuasiStaticAt(100e6));
    }

    /// <summary>
    /// <b>The pre-QSC answer is reproducible, and the switch that does it is one predicate.</b>
    /// <see cref="PlanarCalibrationSettings.QuasiStaticBelowCrossover"/> off gives
    /// <see cref="PlanarCalibration.SuggestDeltas"/>' array over the USER's band, entry for entry, at
    /// a band the shipped default would otherwise collapse to one short separation.
    ///
    /// <para>It exists for the two reasons <c>IncludePassiveNeighbours</c> and
    /// <c>IncludeDrivenGroups</c> already state: every measurement in §QSC's findings was taken
    /// against it, and the engine's own ceiling refusals need a run that still REACHES a ceiling —
    /// <c>EmDeembedCeilingTests</c> and <c>PlanarP2MemoryWinsTests</c>' pinned digest are both gated
    /// on it now. <b>It is not a crossover knob</b>: it is on or off, its off-state restores a
    /// refusal rather than a wrong number, and it has no <c>.cem</c> field.</para>
    /// </summary>
    [Fact]
    public void TheSwitchThatReproducesThePreQscAnswerGivesBackTheMeasuredLadder()
    {
        var slab = GroundedSlab.Fr4Starter;
        var off  = PlanarCalibrationSettings.Default with { QuasiStaticBelowCrossover = false };

        var shipped = PlanarCalibration.SeparationPlan(slab, 100e6, 6e9);
        var pre     = PlanarCalibration.SeparationPlan(slab, 100e6, 6e9, null, off);
        var old     = PlanarCalibration.SuggestDeltas(slab, 100e6, 6e9);

        output.WriteLine($"shipped: {shipped.DeltaLM.Length} separation(s), longest " +
                         $"{shipped.DeltaLM.Max() * 1e3:0.##} mm; pre-QSC: {pre.DeltaLM.Length}, " +
                         $"longest {pre.DeltaLM.Max() * 1e3:0.##} mm");

        Assert.Equal(-1, pre.QuasiStaticIndex);
        Assert.Equal(old.Length, pre.DeltaLM.Length);
        for (int i = 0; i < old.Length; i++) Assert.Equal(old[i], pre.DeltaLM[i]);   // bit for bit
        Assert.False(pre.IsQuasiStaticAt(100e6));

        // …and the shipped default really is the different thing, or this gate is vacuous.
        Assert.True(shipped.QuasiStaticIndex >= 0);
        Assert.True(pre.DeltaLM.Max() > 10 * shipped.DeltaLM.Max());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Q3 — the standards stop growing with the band edge
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The defect, stated as the arithmetic that used to produce it.</b>
    /// <c>RawSolveAndCalibrationRemedyTests.TheLongestStandardIsSetByTheLowerBandEdge_NotByTheMesh</c>
    /// asserts the OLD scaling and still passes unchanged, because
    /// <see cref="PlanarCalibration.LongestStandardLengthM"/> still describes the measured ladder and
    /// that is still what it describes. What changed is which band the ladder is drawn over.
    ///
    /// <para>On the reported board over 100 MHz–6 GHz that is three separations and a 92 mm longest
    /// standard against ONE separation of a few millimetres — and, because the whole band is below
    /// that board's crossover, no measured ladder at all.</para>
    /// </summary>
    [Fact]
    public void BelowTheCrossoverTheStandardsStopGrowingWithTheBandEdge()
    {
        var slab = ReportedBoard;
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);

        output.WriteLine($"crossover {cross / 1e9:0.###} GHz");
        output.WriteLine($"{"f_lo",10} {"old longest",13} {"separations now",16} {"longest now",13}");

        double? first = null;
        foreach (double fLo in new[] { 100e6, 200e6, 500e6, 1e9, 2e9 })
        {
            double oldLongest = PlanarCalibration.LongestStandardLengthM(slab, fLo, 6e9);
            var plan = PlanarCalibration.SeparationPlan(slab, fLo, 6e9);
            double now = PlanarCalibration.SuggestLengths(slab, fLo, 6e9).Short + plan.DeltaLM.Max();

            output.WriteLine($"{fLo / 1e6,9:0.#}M {oldLongest * 1e3,12:0.##}mm " +
                             $"{plan.DeltaLM.Length,16} {now * 1e3,12:0.##}mm");

            // The whole band is under this board's crossover, so there is exactly ONE separation
            // whatever the lower edge is — and it is the SAME length at every one of them.
            Assert.Single(plan.DeltaLM);
            Assert.Equal(0, plan.QuasiStaticIndex);
            first ??= now;
            Assert.Equal(first!.Value, now, 12);
        }

        // …against the old scaling, which is what refused the run.
        double at100M = PlanarCalibration.LongestStandardLengthM(slab, 100e6, 6e9);
        Assert.True(at100M / (first!.Value) > 15,
                    $"the old longest standard was {at100M * 1e3:0.#} mm against " +
                    $"{first.Value * 1e3:0.##} mm now — that ratio is the wall this removes");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The harness the two measurements share
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private sealed record Point(
        double F, Complex Gamma, Complex Zc, double ElDeg,
        double Residual, double Rejected, Mat<Complex> S, int LongestStandardN);

    /// <summary>
    /// De-embed one uniform line with a CHOSEN separation plan, so the two calibration paths can be
    /// compared at the same frequency on the same DUT. A null plan is what every real run uses.
    /// </summary>
    private static List<Point> Deembed(
        PlanarProblem problem, PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
        double fLo, double fHi, IReadOnlyList<double> freqs,
        LineSeparationPlan? plan, bool perfectConductor = false)
    {
        var slab = problem.Slab;
        var eff  = plan ?? PlanarCalibration.SeparationPlan(slab, fLo, fHi, ports[0]);
        var set  = PlanarCalibration.BuildSet(
            ports[0], slab, eff, PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);

        // CL3 — the SHIPPED metal, which PlanarSolve.Run now resolves for itself. This harness
        // builds its calibrator by hand so that it can name a separation plan, so it has to make the
        // same decision; leaving it null would have kept every measurement here on the PEC oracle
        // while every real run moved, and R-cl3-5 would have gone on comparing two lossless paths.
        var fill = perfectConductor
                 ? PlanarFillSettings.Default with { PerfectConductor = true }
                 : PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) };

        var cal = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, fill,
                                           standards: set, separations: eff);
        var dut = new PlanarSolveContext(mesh, ports, fill, null, slab.HeightM);

        var result = new List<Point>();
        foreach (double f in freqs.OrderBy(x => x))
        {
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            var c = cal.At(kernel, f, ports[0].Number);

            var raw = dut.RawScatteringAt(kernel, f);
            var s   = LineDeembed.Renormalise(
                LineDeembed.Apply(raw, [c.Box, c.Box]), [c.Zc, c.Zc], [50.0, 50.0]);

            result.Add(new Point(f, c.Gamma.Gamma, c.Zc, c.Gamma.ElectricalDegrees,
                                 c.Box.ConsistencyResidual, c.Box.RejectedResidual, s,
                                 set.Max(z => z.Mesh.Bases.Count)));
        }
        return result;
    }

    private static double MaxDeltaS(Mat<Complex> a, Mat<Complex> b)
    {
        double m = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
                m = Math.Max(m, (a[i, j] - b[i, j]).Magnitude);
        return m;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Q4 — the overlap, at every frequency and not just its ends
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The real gate: where both paths run, they must agree.</b> The band is chosen so the
    /// measured ladder is still affordable at its bottom — which is exactly the region the two paths
    /// overlap in, and the only region in which this comparison can be made at all.
    ///
    /// <para><b>The tolerance is the one M1 measured (~1 %) and it is asserted at EVERY point</b>,
    /// because agreeing at the ends of an interval and diverging in the middle is a thing two
    /// different approximations do.</para>
    ///
    /// <para><b>CL3/R-cl3-5 — IT COMPARES α AND arg Z_c NOW, AND IT IS THE ONLY THING IN THE SUITE
    /// THAT WOULD NOTICE AN α THAT STEPS AT THE CROSSOVER.</b> Until CL3 the metal was a perfect
    /// conductor on both sides, so both paths carried the identical dielectric α and a Z_c with no
    /// R correction on either — comparing them would have been vacuous, which is why the original
    /// gate compared β, |Z_c| and max|ΔS| and nothing else. It is not vacuous now: the standards
    /// are solved FULL-WAVE and their S carries α_c, while γ below the crossover is SUPPLIED, so
    /// this pair of columns is what says the supplied one carries the same conductor term the fill
    /// does. Both quantities are measured as the DIFFERENCE against a PEC run of the same path, so
    /// the dielectric term and the extraction's own radiation bias — which the two paths share —
    /// cancel instead of diluting the comparison.</para>
    /// </summary>
    [Fact]
    public void TheQuasiStaticPathAgreesWithTheMeasuredOneWhereBothRun()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        var slab = problem.Slab;

        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);
        double fLo = 500e6, fHi = 6e9;
        double[] freqs = [500e6, 750e6, 1e9, 1.5e9, 2e9, 3e9];
        Assert.All(freqs, f => Assert.True(f <= cross, "every comparison point is below the crossover"));

        var measured = Deembed(problem, mesh, ports, fLo, fHi, freqs,
            new LineSeparationPlan(
                PlanarCalibration.SuggestDeltas(slab, fLo, fHi), -1, cross));
        var quasi = Deembed(problem, mesh, ports, fLo, fHi, freqs, null);

        // CL3/R-cl3-5 — the PEC pair, kept beside the lossy one because this is the only place in
        // the suite where the two calibration paths meet on one geometry, and therefore the only
        // thing that would ever notice an α that steps at the crossover.
        var measuredPec = Deembed(problem, mesh, ports, fLo, fHi, freqs,
            new LineSeparationPlan(
                PlanarCalibration.SuggestDeltas(slab, fLo, fHi), -1, cross), perfectConductor: true);
        var quasiPec = Deembed(problem, mesh, ports, fLo, fHi, freqs, null, perfectConductor: true);

        output.WriteLine($"DUT N = {mesh.Bases.Count}; longest standard " +
                         $"{measured[0].LongestStandardN} measured vs " +
                         $"{quasi[0].LongestStandardN} quasi-static");
        output.WriteLine($"{"f",9} {"dbeta",9} {"dalpha",9} {"dZc",9} {"dargZc",9} {"max|dS|",10} " +
                         $"{"betaDL meas",12} {"betaDL qs",10}");

        for (int i = 0; i < freqs.Length; i++)
        {
            double dBeta = (quasi[i].Gamma.Imaginary - measured[i].Gamma.Imaginary) /
                           measured[i].Gamma.Imaginary;
            double dZc = (quasi[i].Zc.Magnitude - measured[i].Zc.Magnitude) / measured[i].Zc.Magnitude;
            double dS = MaxDeltaS(quasi[i].S, measured[i].S);

            // R-cl3-5 — α, AS THE CONDUCTOR TERM RATHER THAN AS THE TOTAL. The total α on this
            // stack is dominated by tanδ = 0.02 and by the extraction's own radiation bias, and
            // both are shared by the two paths, so comparing totals would have hidden a conductor
            // term that was out by a factor of two inside a ratio that still read 1.00. The
            // difference against the PEC run is the part this brief actually put there.
            double acQuasi    = quasi[i].Gamma.Real - quasiPec[i].Gamma.Real;
            double acMeasured = measured[i].Gamma.Real - measuredPec[i].Gamma.Real;
            double dAlpha = (acQuasi - acMeasured) / acMeasured;

            // …and arg Z_c, which is the OTHER half §0 named: Z_c = γ/(jωC′) gets its
            // √(1 + R/(jωL)) correction for free once γ carries R, and the phase is where that
            // correction is biggest — 3.5 % in magnitude against ~10° in phase at 100 MHz.
            double dArg = (quasi[i].Zc.Phase - measured[i].Zc.Phase) * 180.0 / Math.PI;

            output.WriteLine($"{freqs[i] / 1e9,8:0.###}G {dBeta,9:+0.000%;-0.000%} " +
                             $"{dAlpha,9:+0.000%;-0.000%} {dZc,9:+0.000%;-0.000%} {dArg,9:+0.000;-0.000} " +
                             $"{dS,10:E2} {measured[i].ElDeg,12:0.##} {quasi[i].ElDeg,10:0.##}");

            Assert.True(Math.Abs(dBeta) < 0.015,
                        $"β disagrees by {dBeta:P3} at {freqs[i] / 1e9:0.###} GHz");
            Assert.True(Math.Abs(dZc) < 0.015,
                        $"Z_c disagrees by {dZc:P3} at {freqs[i] / 1e9:0.###} GHz");
            Assert.True(dS < 0.03,
                        $"the de-embedded s-parameters disagree by {dS:E2} at {freqs[i] / 1e9:0.###} GHz");

            // THE THRESHOLDS BELOW ARE THE MEASUREMENT'S, NOT A BUDGET CHOSEN FIRST. Measured
            // across the whole overlap: α_c −3.07 % to −1.05 % (the quasi-static supplier reads a
            // little LOW, which is the static charge distribution against the solved one), and
            // arg Z_c +0.019° to +0.110°. Both bands are that spread with margin, and BOTH
            // COMPARISONS WERE VACUOUS BEFORE THIS BRIEF — with PEC metal the two paths carried
            // the identical dielectric α and a Z_c with no R correction on either side.
            Assert.True(Math.Abs(dAlpha) < 0.08,
                        $"α_c disagrees by {dAlpha:P3} at {freqs[i] / 1e9:0.###} GHz — the two " +
                        "calibration paths are supposed to carry the SAME conductor term");
            Assert.True(Math.Abs(dArg) < 0.25,
                        $"arg Z_c disagrees by {dArg:F3}° at {freqs[i] / 1e9:0.###} GHz");

            // And the thing the whole comparison exists to catch: a conductor term on ONE side of
            // the crossover. Both paths must actually have one.
            Assert.True(acQuasi > 0 && acMeasured > 0,
                        $"one of the two paths carried no conductor term at all at " +
                        $"{freqs[i] / 1e9:0.###} GHz: quasi-static {acQuasi:E3}, " +
                        $"measured {acMeasured:E3} Np/m");
        }

        // …and the standards really are smaller, which is the point of the exercise.
        Assert.True(quasi[0].LongestStandardN < measured[0].LongestStandardN);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Q5 — the a₂₂ sign margin, and why the separation is capped from ABOVE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The one failure mode that does NOT degrade smoothly with Δℓ.</b>
    /// <see cref="LineDeembed.SolveErrorBox"/> picks a₂₂'s sign by the redundant M₁₁ equation and
    /// reports the rejected sign's residual; a ratio near 1 means the sign was decided by noise, and
    /// a wrong a₂₂ is a smooth, plausible, wrong answer.
    ///
    /// <para><b>MEASURED, AND THE SURPRISE IS THE DIRECTION.</b> Shrinking Δℓ from 24 substrate
    /// heights to one bulk cell leaves the margin at a few hundred throughout. GROWING it breaks the
    /// calibration: at 24 h the separation reaches βΔℓ = 170° at 2 GHz, which is D6's own
    /// denominator zero at βΔℓ = nπ, and the margin collapses to ~25. A supplied γ frees Δℓ from the
    /// usable interval's LOWER end and leaves its upper end exactly where it was — which is why
    /// <see cref="PlanarCalibration.QuasiStaticSeparationM"/> caps the substrate term at the
    /// crossover as well as flooring it at the mesh.</para>
    ///
    /// <para>The residual itself is larger on this path than on the measured one (~1e-5 against
    /// ~1e-15) and that is not a regression: on the measured path γ was FITTED to the very two
    /// standards the residual then checks, so the check is nearly a tautology. With γ supplied the
    /// residual becomes an honest measure of the quasi-TEM assumption, which is a better diagnostic
    /// and a worse-looking number.</para>
    /// </summary>
    [Fact]
    public void TheA22SignMarginSurvivesTheShortStandards_AndALongerOneIsWorse()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        var slab = problem.Slab;
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);
        double[] freqs = [500e6, 1e9, 2e9];

        output.WriteLine($"bulk cell {ports[0].BulkCellM * 1e6:0.#} um, h {slab.HeightM * 1e6:0.#} um");
        output.WriteLine($"{"heights",8} {"dL mm",8} {"f",8} {"betaDL",8} {"residual",10} " +
                         $"{"rejected",10} {"margin",9}");

        double shippedWorst = double.PositiveInfinity, longWorst = double.PositiveInfinity;

        foreach (double heights in new[] { 24.0, 6.0, 1.5, 0.375 })
        {
            double dl = heights * slab.HeightM;
            var pts = Deembed(problem, mesh, ports, 500e6, 6e9, freqs,
                              new LineSeparationPlan([dl], 0, cross));

            foreach (var p in pts)
            {
                double margin = p.Rejected / Math.Max(p.Residual, 1e-300);
                output.WriteLine($"{heights,8:0.###} {dl * 1e3,8:0.###} {p.F / 1e9,7:0.###}G " +
                                 $"{p.ElDeg,8:0.##} {p.Residual,10:E2} {p.Rejected,10:E2} {margin,9:E1}");

                if (heights >= 24.0) longWorst = Math.Min(longWorst, margin);
                if (heights <= 6.0)  shippedWorst = Math.Min(shippedWorst, margin);
            }
        }

        output.WriteLine($"worst margin at or below the shipped 6 h: {shippedWorst:E2}");
        output.WriteLine($"worst margin at 24 h (past βΔℓ = nπ):    {longWorst:E2}");

        // The number this brief owes, recorded rather than described.
        Assert.True(shippedWorst > 50,
                    $"the a₂₂ sign must not be decided by noise; worst margin was {shippedWorst:E2}");

        // …and the reason the cap exists: LONGER is worse, by more than an order of magnitude.
        Assert.True(longWorst < shippedWorst / 5,
                    $"a 24 h separation should be measurably worse (it crosses βΔℓ = nπ); got " +
                    $"{longWorst:E2} against {shippedWorst:E2}");

        // The cap is what keeps the shipped separation off that zero, and it is inert on an ordinary
        // board: 6 h is already under λ/6 at the crossover here.
        double shipped = PlanarCalibration.QuasiStaticSeparationM(slab, ports[0]);
        output.WriteLine($"shipped separation {shipped * 1e3:0.###} mm " +
                         $"(6 h = {6 * slab.HeightM * 1e3:0.###} mm)");
        Assert.True(shipped <= 6 * slab.HeightM + 1e-12);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The run says which calibration produced each point — RAW1 §5's rule, one phase on
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>"Do not present the quasi-static answer as the measured one."</b> RAW1 §5 is what happens
    /// when a diagnostic flag cannot distinguish two cases: <c>CalibrationUsable</c> read 1 for a run
    /// in which nothing had been calibrated at all.
    ///
    /// <para>So the `.npy` carries <c>planar.CalQuasiStatic</c> per (freq, port) — 1 where γ was
    /// supplied, 0 where it was measured, NaN where the port was not calibrated — and this asserts
    /// all three in one sweep, because a cube that returned any single value everywhere would pass
    /// the half of this that matters most.</para>
    /// </summary>
    [Fact]
    public void TheRunReportsWhichCalibrationProducedEachPoint()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var ports   = PlanarLineFixtures.EndPorts(problem);
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(problem.Slab);
        double[] freqs = [1e9, 6e9];                       // one below the crossover, one above

        var run = new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, ports, freqs,
                                           PlanarSolveSettings.Default);
        var cube = run.Data[$"{PlanarKernel.DiagnosticsGroup}.CalQuasiStatic"];

        // [freq, port], row-major — two ports, both de-embedded, both sharing one calibration.
        output.WriteLine($"crossover {cross / 1e9:0.###} GHz; " +
                         $"CalQuasiStatic = [{string.Join(", ", cube.RealValues)}]");

        Assert.Equal(1.0, cube.RealValues[0]);             // 1 GHz, port 1 — supplied
        Assert.Equal(1.0, cube.RealValues[1]);             // 1 GHz, port 2
        Assert.Equal(0.0, cube.RealValues[2]);             // 6 GHz, port 1 — measured
        Assert.Equal(0.0, cube.RealValues[3]);             // 6 GHz, port 2

        // …and the note says what the flag means, rather than leaving a bare 1 to be interpreted.
        string notes = string.Join(" ", run.Solve.Notes);
        Assert.Contains("QUASI-STATIC", notes, StringComparison.Ordinal);
        Assert.Contains("CalQuasiStatic", notes, StringComparison.Ordinal);

        // A run that calibrates nothing leaves it NaN, exactly as every other calibration cube.
        var raw = new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, ports, freqs,
                                           PlanarSolveSettings.Default with { Deembed = false });
        var rawCube = raw.Data[$"{PlanarKernel.DiagnosticsGroup}.CalQuasiStatic"];
        Assert.All(rawCube.RealValues, v => Assert.True(double.IsNaN(v)));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M4 — the ceiling refusal must not offer a band edge that cannot bind
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>RAW1 §4's own defect, one phase on.</b> That refusal used to blame the mesh and offer
    /// "coarsen it"; RAW1 re-pointed it at the lower band edge, which was then what bound. With the
    /// quasi-static path in place a band edge BELOW the crossover binds nothing at all — the
    /// standards down there are sized from the substrate and the mesh — so the same sentence would
    /// once again be naming a remedy that cannot act.
    ///
    /// <para>Two cases, and each has to say something different: a measured standard over the
    /// ceiling still names a band edge, but one measured from the CROSSOVER rather than from the
    /// user's own f_lo; and the short quasi-static standard names the port's transverse mesh
    /// instead, because no band edge touches it.</para>
    /// </summary>
    [Fact]
    public void TheCeilingRefusalNamesARemedyThatCanActOnEachKindOfStandard()
    {
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem);

        var plan = PlanarCalibration.SeparationPlan(slab, 100e6, 6e9, ports[0]);
        var set  = PlanarCalibration.BuildSet(
            ports[0], slab, plan, PlanarCalibration.SuggestLengths(slab, 100e6, 6e9).Short);

        // The measured one: the sentence must quote the crossover as the ladder's bottom, not the
        // user's 100 MHz, and must not promise that lowering the edge further changes anything.
        string measured = PlanarSolve.BandEdgeRemedy(
            set[1], 40_000, 12_000, slab, 100e6, 6e9, null, quasiStandard: false);
        output.WriteLine("MEASURED STANDARD:\n" + measured + "\n");

        Assert.Contains("quasi-statically", measured, StringComparison.Ordinal);
        Assert.Contains("3.048 GHz", measured, StringComparison.Ordinal);

        // The quasi-static one: the band edge is explicitly NOT the remedy, and what is named is the
        // quantity that actually sets its size.
        string quasi = PlanarSolve.BandEdgeRemedy(
            set[^1], 40_000, 12_000, slab, 100e6, 6e9, null, quasiStandard: true);
        output.WriteLine("QUASI-STATIC STANDARD:\n" + quasi);

        Assert.Contains("band edge is NOT the remedy", quasi, StringComparison.Ordinal);
        Assert.DoesNotContain("Raising the lower band edge", quasi, StringComparison.Ordinal);
        Assert.Contains("TRANSVERSE", quasi, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="PlanarCalibration.MeasuredBandBottomHz"/> is what every band-edge quantity has to
    /// be asked of now — the user's lower edge, or the crossover, whichever is higher, clamped to
    /// the band. A sweep entirely above the crossover is unchanged; one entirely below it has no
    /// measured ladder to size and clamps at its own top.
    /// </summary>
    [Fact]
    public void TheMeasuredLaddersOwnBottomIsTheCrossoverOrTheUsersEdge()
    {
        var slab = GroundedSlab.Fr4Starter;
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);

        Assert.Equal(cross, PlanarCalibration.MeasuredBandBottomHz(slab, 100e6, 6e9), 3);
        Assert.Equal(4e9,   PlanarCalibration.MeasuredBandBottomHz(slab, 4e9, 20e9), 3);
        Assert.Equal(1e9,   PlanarCalibration.MeasuredBandBottomHz(slab, 100e6, 1e9), 3);
    }
}
