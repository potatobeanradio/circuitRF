// L8d Tiers 6 and 7 — the driver, the counter, determinism, the refusals, and the physics.
//
// Everything that needs a physically converged answer is Category=Benchmark; the routine gate keeps
// the structural checks (the counter, determinism, the notes) and one representative physics case,
// which is the precedent L8a and L8c both set.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarSolveTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-11 / R-prt-14 — the counter and determinism
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T7_1_TheGeometricCoreIsBuiltOncePerMesh_WhateverTheSweepLength()
    {
        // R-fil-9's counter, one level up. A sweep of any length builds exactly one core per mesh:
        // the DUT plus every calibration standard. It exists for R-mom-11's own recorded reason —
        // "it is easy to lose in a refactor, so it is enforced by a counter, NOT by a comment".
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var three = PlanarSolve.Run(mesh, prt, slab, [9e9, 10e9, 11e9]);
        _out.WriteLine($"3-point sweep: {three.CoreFillCount} core(s), {three.StandardCount} standard mesh(es)");

        // The 101-point sweep is not run — the counter is the point, and a Dcim.Fit per point would
        // make the assertion cost 40 s to prove something structural. Instead: the count must be
        // independent of the frequency LIST, which is what a longer list would test.
        var one = PlanarSolve.Run(mesh, prt, slab, [10e9]);
        _out.WriteLine($"1-point sweep: {one.CoreFillCount} core(s)");

        Assert.Equal(one.CoreFillCount, three.CoreFillCount);
        Assert.Equal(1 + three.StandardCount, three.CoreFillCount);

        // Both ports of a symmetric line are the same cross-section, so they SHARE one calibration —
        // half the standards a naive implementation would build.
        Assert.Contains(three.Notes, n => n.Contains("share 1 calibration"));
        foreach (var n in three.Notes) _out.WriteLine($"  · {n}");
    }

    [Fact]
    public void T7_2_TheSweepIsBitIdenticalAcrossTwoRunsInOneProcess()
    {
        // R-prt-14, and it is R-fil-11's rule reaching the top of the stack: same problem, same
        // settings, bit-identical answer. Not "to a tolerance" — a tolerance here would let a
        // parallel accumulation whose order varies pass.
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var a = PlanarSolve.Run(mesh, prt, slab, [10e9]);
        var b = PlanarSolve.Run(mesh, prt, slab, [10e9]);

        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Points[0].S[i, j].Real),
                             BitConverter.DoubleToInt64Bits(b.Points[0].S[i, j].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Points[0].S[i, j].Imaginary),
                             BitConverter.DoubleToInt64Bits(b.Points[0].S[i, j].Imaginary));
            }
        _out.WriteLine($"S = [{a.Points[0].S[0, 0]:F12}  {a.Points[0].S[0, 1]:F12}]");
        _out.WriteLine($"    [{a.Points[0].S[1, 0]:F12}  {a.Points[0].S[1, 1]:F12}]  bit-identical across runs");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T7_3_TheFrequencyOrderDoesNotMatterToTheCaller_BecauseTheDriverSortsIt()
    {
        // The calibrator is stateful and must be stepped upward; the driver owns that invariant so a
        // caller handing over a descending list gets the same answer rather than a wrong one.
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        var up   = PlanarSolve.Run(mesh, prt, slab, [8e9, 10e9]);
        var down = PlanarSolve.Run(mesh, prt, slab, [10e9, 8e9]);

        Assert.Equal(up.Points[0].FrequencyHz, down.Points[0].FrequencyHz);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(up.Points[1].S[i, j].Real, down.Points[1].S[i, j].Real, 15);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-10 — reciprocity and passivity are gates; LOSSLESSNESS IS NOT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_1_TheDeembeddedAnswerIsReciprocalAndPassive_AndDeliberatelyNotLossless()
    {
        // L8a wrote the warning for exactly this test: "an OPEN planar structure radiates and
        // launches surface waves, so |S₁₁|² + |S₂₁|² < 1 LEGITIMATELY. Whoever writes L8d/L8e must
        // not copy the losslessness test across and then fix the kernel until it passes: that would
        // mean suppressing radiation, which is one of the two things L8 exists to model."
        //
        // So the missing power is MEASURED (R-prt-11) and reported, and there is no losslessness
        // assertion anywhere in this file.
        var lossless = new GroundedSlab(GroundedSlab.Fr4Starter.HeightM,
                                        new EmMaterial(GroundedSlab.Fr4Starter.Material.EpsR, 0.0));
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        foreach (double f in new[] { 2e9, 10e9 })
        {
            // The DUT is HALF a guided wavelength, not the 1.5 λ_g the other fixtures use, and that
            // is deliberate: the missing power here is a fraction of a percent, so it has to be
            // measured on a structure short enough that the de-embedding's own residual — which
            // T4_6 measures growing with electrical length — is smaller than the thing being
            // measured. On a 1.5 λ_g line at 10 GHz the residual is larger than the radiation and
            // the "missing" power comes out NEGATIVE, which says nothing about the physics.
            var problem = PlanarLineFixtures.LineOfWavelengths(lossless, w, 0.5, f);
            var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
            var r = PlanarSolve.Run(mesh, prt, lossless, [f]);
            var s   = r.Points[0].S;
            var raw = r.Points[0].RawS;

            double rec  = (s[0, 1] - s[1, 0]).Magnitude;
            double sum  = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
            double rawSum = raw[0, 0].Magnitude * raw[0, 0].Magnitude
                          + raw[1, 0].Magnitude * raw[1, 0].Magnitude;

            _out.WriteLine($"{f / 1e9,5:F1} GHz (tanδ = 0, PEC metal, ℓ = 0.5 λ_g): " +
                           $"|S₁₁| = {s[0, 0].Magnitude:F5}, |S₂₁| = {s[1, 0].Magnitude:F5}, " +
                           $"1 − Σ|S|² = {1 - sum:+0.00000;-0.00000} " +
                           $"({(1 - sum) * 100:F3}% radiated + surface wave); " +
                           $"the same on the RAW solve (no calibration in the path, so it also " +
                           $"carries the port's own radiation): {1 - rawSum:+0.00000;-0.00000}");

            Assert.True(rec < 1e-9, $"the de-embedded S is not reciprocal: {rec:E3}");

            // Passivity IS a gate — but at the level the calibration residual permits, and saying
            // that plainly is better than a tolerance nobody can account for.
            Assert.True(s[1, 0].Magnitude < 1.02,
                $"|S₂₁| = {s[1, 0].Magnitude:F5} — more gain than the de-embedding residual explains");

            // The RAW solve has no calibration in it at all, so its deficit is unambiguously power
            // leaving the structure. THAT is the assertion; the de-embedded number is the reportable.
            Assert.True(1 - rawSum > 0, $"the raw solve gained power: {1 - rawSum:E3}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-12 — the size of the conductor-loss omission, reported rather than hidden
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_2_ConductorLossIsNotModelled_AndKernelASaysHowBigThatIs()
    {
        // Kernel B's sheet is PEC (L8c). PlanarConductorLayer.SigmaSm and ThicknessM are carried and
        // unused. This does not measure kernel B at all — it measures what kernel B is MISSING, from
        // the kernel that does model it, so the A-vs-B comparison is read correctly and so whoever
        // schedules a surface-impedance term knows what it buys.
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        var p = EmProblemBuilders.Microstrip(w: w, h: slab.HeightM, t: 35e-6,
                                             epsR: slab.Material.EpsR, tanD: slab.Material.TanD);
        var rlgc = RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default));

        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            double omega = 2 * Math.PI * f;
            double r  = rlgc.RMatrix(omega)[0, 0];
            double g  = rlgc.GPerM(omega);
            double z0 = Math.Sqrt(rlgc.LPerM / rlgc.CPerM);

            double alphaC = 0.5 * r / z0;            // Np/m, conductor
            double alphaD = 0.5 * g * z0;            // Np/m, dielectric
            _out.WriteLine($"{f / 1e9,5:F1} GHz: α_c = {alphaC:F4} Np/m, α_d = {alphaD:F4} Np/m → " +
                           $"kernel B is missing {alphaC / (alphaC + alphaD):P1} of the conducted loss " +
                           $"({8.686 * alphaC:F3} dB/m of {8.686 * (alphaC + alphaD):F3} dB/m)");
            Assert.True(alphaC > 0 && alphaD > 0);
        }

        _out.WriteLine("Kernel B models neither of these — it adds RADIATION and surface-wave loss, " +
                       "which kernel A structurally cannot see (T6_1). The two are complementary, " +
                       "and a surface-impedance term for B is named in the report, not built here.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-13 — the DCIM validated range is a decision now, and the note says so
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_3_TheValidatedRangeIsReportedAsANote_NotAsARefusal()
    {
        var slab = GroundedSlab.Fr4Starter;

        // §10.7's own hero, 20 mm at 20 GHz: the far ends sit well past ρ/λ = 1.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 20e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
        var r = PlanarSolve.Run(mesh, prt, slab, [20e9], new PlanarSolveSettings(Deembed: false));

        string note = r.Notes.Single(n => n.Contains("Widest separation"));
        _out.WriteLine(note);

        Assert.Contains("ρ/λ", note);
        Assert.Contains("not a refusal", note);

        // And the refusal function itself still says no at that ratio — the point is that the DRIVER
        // does not act on it, and the note explains why.
        Assert.False(Dcim.WithinValidatedRange(GreensKernel.ScalarPotential, 2.4).Ok);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fil-10 / R17 — still enforced, now with ports on top
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_4_R17StillRefusesBeforeAllocating_WithSurfaceMeshersOwnWording()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanarSystem.GuardCeiling(SurfaceMesher.UnknownCeiling + 1));
        Assert.Contains("ceiling this kernel is built for", ex.Message);
        _out.WriteLine(ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 6 — a quarter-wave open stub, which is HALF OF L8'S OWN PHASE GATE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T6_5_AQuarterWaveOpenStubResonatesWhereItShould()
    {
        // The phase table's first gate is "a quarter-wave open stub resonates at the right
        // frequency". L8e owns the formal gate; this measures it now so L8e is not the first time
        // anyone looks — and it is the first structure in this slice that is not a line.
        //
        // The comparison is against λ_g/4 WITH THE OPEN-END EXTENSION NAMED, not against a bare
        // quarter wavelength: an open microstrip end behaves as ~0.4 h of extra line, and ignoring it
        // would put the "expected" resonance several percent high and invite fixing the solver
        // toward a formula that was never the truth.
        var slab = GroundedSlab.Fr4Starter;
        double w  = PlanarLineFixtures.Fr4HeroWidthM;
        double f0 = 6e9;

        // Size the stub for a nominal λ_g/4 at f0 using kernel B's own ε_eff at f0.
        var probe = PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, f0);
        var (pm, pp) = PlanarLineFixtures.MeshAndPorts(probe, PlanarLineFixtures.Shipping);
        var sol   = new PlanarSolveContext(pm, pp).SolveAt(PlanarLineFixtures.Kernel(slab, f0), f0);
        double eeff = CurrentWaveOracle.Extract(pm, sol.Currents[0], pp[0]).EffectivePermittivity(f0);
        double lambdaG = EmConstants.C0 / (f0 * Math.Sqrt(eeff));
        double stub = 0.25 * lambdaG;

        _out.WriteLine($"ε_eff({f0 / 1e9:F0} GHz) = {eeff:F4} → λ_g = {lambdaG * 1e3:F3} mm, " +
                       $"stub = {stub * 1e3:F3} mm, open-end extension ≈ {0.4 * slab.HeightM * 1e3:F3} mm " +
                       $"({0.4 * slab.HeightM / stub:P1} of the stub)");

        // A through line with the stub hanging off its middle.
        double thru = 1.2 * lambdaG;
        var problem = PlanarLineFixtures.Problem(slab, 1.4 * f0,
            PlanarLineFixtures.Rect(0, -0.5 * w, thru, 0.5 * w),
            PlanarLineFixtures.Rect(0.5 * (thru - w), 0.5 * w, 0.5 * (thru + w), 0.5 * w + stub));

        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var ports = PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(0,    0), PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(thru, 0), PlanarPortSide.MaxX, 50.0),
        ]);
        _out.WriteLine($"N = {mesh.Bases.Count}, through line {thru * 1e3:F3} mm");

        var freqs = new List<double>();
        for (double f = 0.7 * f0; f <= 1.35 * f0 + 1; f += 0.05 * f0) freqs.Add(f);

        var r = PlanarSolve.Run(mesh, ports, slab, freqs);

        double bestF = 0, best = double.PositiveInfinity;
        foreach (var p in r.Points)
        {
            double s21 = p.S[1, 0].Magnitude;
            _out.WriteLine($"  {p.FrequencyHz / 1e9,5:F2} GHz: |S₂₁| = {s21:F5}, |S₁₁| = {p.S[0, 0].Magnitude:F5}");
            if (s21 < best) { best = s21; bestF = p.FrequencyHz; }
        }

        // The open-end extension lowers the resonance: an electrically longer stub resonates sooner.
        double expected = f0 * stub / (stub + 0.4 * slab.HeightM);
        _out.WriteLine($"notch at {bestF / 1e9:F3} GHz; λ_g/4 alone predicts {f0 / 1e9:F3} GHz, " +
                       $"with a 0.4 h open-end extension {expected / 1e9:F3} GHz " +
                       $"→ measured/corrected − 1 = {(bestF - expected) / expected:+0.0%;-0.0%}");

        Assert.True(best < 0.5, $"the stub produced no notch — |S₂₁| bottoms out at {best:F4}");
        Assert.InRange(bestF, 0.80 * f0, 1.05 * f0);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-4 — THE MINIMUM FEED LENGTH, MEASURED. §8's first reportable.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T6_6_TheMinimumFeedLengthIsMeasuredInSubstrateHeights()
    {
        // FR-4 ONLY, and that is itself the answer for the other starter. The study needs cells
        // SMALLER than a substrate height, and on the 100 µm GaAs slab the wavelength-driven cell is
        // 418 µm at 10 GHz — four substrate heights wide. A feed of "a few h" is therefore shorter
        // than ONE cell there and the question cannot even be posed at the shipping mesh; resolving
        // it would need a mesh an order of magnitude finer than R17 allows on a real MMIC. The
        // practical reading is that on a thin substrate the requirement is satisfied by any feed the
        // mesher can represent at all, and the number below is the one that binds on a PCB.
        foreach (var (name, slab, w) in new[]
        {
            ("FR-4", GroundedSlab.Fr4Starter, PlanarLineFixtures.Fr4HeroWidthM),
        })
        {
            // 5 GHz with a 40-cell mesh, and both halves of that are forced. The feed length scale
            // is the SUBSTRATE HEIGHT while the cell size is λ_g/N, so cells smaller than h need
            // f > c/(20 h √εᵣ) ≈ 4.5 GHz at the shipping mesh — and the de-embedding residual grows
            // as f², so going higher buries the effect being measured. 5 GHz at 40 cells/λ is the
            // window where a substrate height is 2.2 cells AND the residual is ~4× below its 10 GHz
            // value.
            const double f = 5e9;
            double h = slab.HeightM;
            var meshSettings = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 40);

            _out.WriteLine($"── {name} (h = {h * 1e6:F0} µm, W = {w * 1e6:F0} µm) at 5 GHz, 40 cells/λ");

            // THE QUESTION IS ASKED OF THE CALIBRATION, NOT OF THE GEOMETRY, AND THAT IS A CORRECTION
            // RATHER THAN A CONVENIENCE. The obvious experiment — vary the DUT's feed length and
            // compare the de-embedded answers — requires shifting the reference planes to a common
            // point, and that multiplies S by e^{2γΔ}, so the ~1% γ residual T4_6 identifies
            // accumulates faster than the near field decays. Measured that way the answer moved
            // 1.8e-2 / 2.3e-2 / 3.9e-2 across feeds of 1 / 2 / 3 / 5 h — GROWING, i.e. reporting the
            // shift rather than the physics.
            //
            // Varying how much of the feed the STANDARD reproduces asks the same question with no
            // shift at all: the DUT is one fixed geometry, the reference planes never move, and the
            // only thing changing is whether the error box fits inside the region the calibration
            // replaced. That is exactly what "how long must the feed be" means.
            double feed = 6.0 * h;
            double wide = 2.0 * w;
            double mid  = 2.0 * w;

            Complex? reference = null, first = null;
            double refFeed = 0, spread = 0;
            foreach (double heights in new[] { 0.5, 1.0, 2.0, 3.0, 5.0 })
            {

                var problem = PlanarLineFixtures.Problem(slab, f,
                    PlanarLineFixtures.Rect(0,           -0.5 * w,    feed,           0.5 * w),
                    PlanarLineFixtures.Rect(feed,        -0.5 * wide, feed + mid,     0.5 * wide),
                    PlanarLineFixtures.Rect(feed + mid,  -0.5 * w,    2 * feed + mid, 0.5 * w));

                var mesh = SurfaceMesher.Mesh(problem, meshSettings).Mesh;
                var (x0, _, x1, _) = problem.Bounds();
                var ports = PlanarPorts.ResolveAll(mesh,
                [
                    new PlanarPort(1, new EmPoint(x0, 0), PlanarPortSide.MinX, 50.0),
                    new PlanarPort(2, new EmPoint(x1, 0), PlanarPortSide.MaxX, 50.0),
                ]);
                if (reference is null) _out.WriteLine($"   DUT N = {mesh.Bases.Count}");

                var r = PlanarSolve.Run(mesh, ports, slab, [f],
                    new PlanarSolveSettings(Calibration: new PlanarCalibrationSettings(EndRunHeights: heights)));
                var s = r.Points[0].S;
                Complex s11 = s[0, 0];

                // The reference is the PREVIOUS reproduction depth, so what is reported is how much
                // the answer still moves at each step — which is what "how long is long enough" means.
                string delta = reference is { } prev
                    ? $"moved {(s11 - prev).Magnitude:E3} from the {refFeed:F1} h case"
                    : "(first)";
                _out.WriteLine($"   standard reproduces {heights,4:F1} h ({heights * h * 1e6,6:F0} µm) " +
                               $"of a {feed * 1e6:F0} µm feed, {r.StandardCount} standard(s): " +
                               $"S₁₁ = {s11:F5}   {delta}");

                spread = Math.Max(spread, first is { } f0 ? (s11 - f0).Magnitude : 0);
                first ??= s11;
                reference = s11;
                refFeed   = heights;
            }

            // THE ANSWER, AND IT IS A NEGATIVE RESULT REPORTED AS ONE. There is no knee. Reproducing
            // 0.5 h and 1.0 h of the feed gives bit-identical answers (they round to the same cell
            // count); beyond that the answer wanders by 1.8e-3 / 3.1e-3 / 6.0e-3 per step, GROWING
            // rather than settling, for a total spread of ~1.0e-2 across 0.5–5 h. That growth tracks
            // the calibration's own γ scatter — a longer end run forces a longer standard, hence a
            // different Δℓ and a different extraction — not a near-field tail decaying.
            //
            // So the minimum feed length is NOT resolvable above this method's own floor, and the
            // floor is the radiative port-to-port coupling T4_6 identifies. The practical rule that
            // follows is the one the default already implements: the feed must be long enough to hold
            // the standard's end run and to keep the two error boxes apart — 3 substrate heights —
            // and the answer's sensitivity to that choice is ~1e-2, the same order as the
            // de-embedding residual itself. A user who needs better than 1e-2 needs a different port,
            // not a longer feed.
            _out.WriteLine($"   total spread across 0.5–5 h of reproduction: |ΔS₁₁| = {spread:E3}");
            Assert.True(spread < 3e-2,
                $"the de-embedded answer moves {spread:E3} with how much of the feed the standard " +
                "reproduces — more than the method's own residual accounts for");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 7 — cost. §8's fourth reportable, against L8c's own bare-fill numbers.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T7_4_TheCostOfADeembeddedSweep_AgainstL8csBareFill()
    {
        // L8c's Tier 8 measured the hero at 1.73 s per frequency and 178 s for 101 points, with NO
        // excitation and NO calibration. This is what de-embedding adds.
        var slab = GroundedSlab.Fr4Starter;
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);      // §10.7's own hero, N = 552
        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        var prt  = PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem));

        var freqs = new[] { 9e9, 10e9, 11e9 };
        var r = PlanarSolve.Run(mesh, prt, slab, freqs);

        double perPoint = (r.TotalKernelMs + r.TotalDutMs + r.TotalCalibrationMs) / freqs.Length;
        _out.WriteLine($"DUT N = {r.UnknownCount}, {r.StandardCount} standard mesh(es), " +
                       $"{r.CoreFillCount} core(s) built once ({r.CoreBuildMs / 1000:F2} s)");
        _out.WriteLine($"per frequency: kernel {r.TotalKernelMs / freqs.Length / 1000:F3} s, " +
                       $"DUT {r.TotalDutMs / freqs.Length / 1000:F3} s, " +
                       $"calibration {r.TotalCalibrationMs / freqs.Length / 1000:F3} s " +
                       $"→ {perPoint / 1000:F3} s");
        _out.WriteLine($"a 101-point sweep: {(r.CoreBuildMs + 101 * perPoint) / 1000:F0} s " +
                       $"(L8c's bare fill of the same hero: 178 s)");
        foreach (var n in r.Notes) _out.WriteLine($"  · {n}");

        Assert.Equal(1 + r.StandardCount, r.CoreFillCount);
        Assert.True(perPoint > 0);
    }
}
