// CL3 — the metal is a real conductor by default, and these are the gates that say what that is
// worth. `docs/sonnet-briefs/brief-conductor-loss-3-default-and-gate.md`; findings in
// `src/Engine/Mom/RESOLVED.md` §CL3.
//
// CL1 built the surface-impedance term and left it behind a null `PlanarFillSettings.ConductorLoss`;
// CL2 read it back out as `P_conductor`. Neither changed a number anyone runs. This brief flips the
// default in `PlanarSolve.Run` — the one scope where σ and t are in reach — and what has to be
// proved is not that the term exists (CL1 did that) but that it reaches the SHIPPED path and lands
// where CL1's convergence result says it should.
//
// Four things are gated here:
//
//   R-cl3-0  Milestone 0's decision. The quasi-static calibration path's γ takes its conductor term
//            from the standard's OWN static charge, and the reason is that it tracks the fill's own
//            α_c on the same mesh where kernel A's Wheeler term over-reads it by 20-75 %.
//   R-cl3-3  The phase gate: a de-embedded 50 Ω line's α out of kernel B against kernel A's, at
//            2 / 10 / 20 GHz on both starters, ground held PEC in BOTH.
//   R-cl3-4  Passivity and reciprocity get STRICTER, not looser. Adding loss cannot make a passive
//            structure active, and a re-bless would bury it if it did.
//   R-cl3-6  Every number above names the calibration path that produced it, and α is read from the
//            de-embedded S₂₁ rather than from the calibration's own γ.

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class ConductorLossDefaultTests(ITestOutputHelper output)
{
    private const double CopperSigma = 5.8e7, GoldSigma = 4.1e7;

    /// <summary>The PEC oracle — CL1's and CL2's own reference, and the only way back to it.</summary>
    private static PlanarSolveSettings Pec =>
        new(PlanarFillSettings.Default with { PerfectConductor = true });

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The two starters, re-bisected to a true 50 Ω — CL1's own widths, so the two briefs' tables
    // describe one cross-section rather than two.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (GroundedSlab Slab, double W, double T, double Sigma, double Lambdas) Starter(
        string which) => which == "gaas"
        ? (GroundedSlab.GaAsStarter, 70.72e-6, 3e-6, GoldSigma, 1.0)
        : (GroundedSlab.Fr4Starter, 3020.28e-6, 35e-6, CopperSigma, 1.5);

    private static (PlanarProblem P, PlanarMesh M, System.Collections.Generic.IReadOnlyList<PlanarPortResolution> Ports)
        UniformLine(string which, double fHi, int cpw = 20, int edge = 3)
    {
        var (slab, w, t, sigma, lambdas) = Starter(which);
        double lambdaTop = EmConstants.C0 / (fHi * Math.Sqrt(0.5 * (slab.Material.EpsR + 1)));
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, lambdas * lambdaTop, 0.5 * w)], sigma, t)],
            slab, fHi);
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                        EdgeMesh: edge > 0, EdgeCells: Math.Max(edge, 1));
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, ms);
        return (problem, mesh, ports);
    }

    /// <summary>
    /// Kernel A's TOTAL α and its conductor half, for the same cross-section with the GROUND HELD
    /// PEC — which is how kernel B terminates and is therefore the only comparison R-cl3-3 can make
    /// until CL4 runs. <b>The ORACLE, never an input</b> (D7 / R-qsc-2).
    /// </summary>
    private static (double Total, double Conductor, double Dielectric) KernelA(
        string which, double fHz)
    {
        var (slab, w, t, sigma, _) = Starter(which);
        var p = EmProblemBuilders.Microstrip(w, slab.HeightM, t, slab.Material.EpsR,
                                             tanD: slab.Material.TanD, sigmaSm: sigma,
                                             groundSigmaSm: double.PositiveInfinity);
        var r = new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, [fHz]);
        double om = 2 * Math.PI * fHz;
        var z = new Complex(r.Rlgc.RPerM(om), om * r.Rlgc.LPerM);
        var y = new Complex(r.Rlgc.GPerM(om), om * r.Rlgc.CPerM);
        double total = Complex.Sqrt(z * y).Real;
        double ac = r.Rlgc.RPerM(om) / (2.0 * Math.Sqrt(r.Rlgc.LPerM / r.Rlgc.CPerM));
        return (total, ac, total - ac);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl3-3 / milestone 5 — the phase gate, and it is one sentence per starter
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>α of a de-embedded uniform 50 Ω line, out of the SHIPPED path, against kernel A's.</b>
    /// Two runs of the same sweep on the same mesh, differing only in
    /// <see cref="PlanarFillSettings.PerfectConductor"/>, so the conductor term is a DIFFERENCE of
    /// two answers and every bias the two share cancels.
    ///
    /// <para><b>α is read from the published S₂₁ of a line of known length, never from the
    /// calibration's own γ</b> (R-cl3-6). That is not fastidiousness: on the MMIC starter all three
    /// of these frequencies are below the 26.07 GHz quasi-static crossover, so the calibration's γ
    /// is a SUPPLIED one, and reading α off it would be reading this brief's own arithmetic back to
    /// itself.</para>
    ///
    /// <para><b>THE BAND IS STATED FROM THE MEASUREMENT AND WAS NOT CHOSEN FIRST.</b>
    /// α_c out of kernel B is <b>0.60 to 0.84</b> of kernel A's across both starters and all three
    /// frequencies — and at 10 GHz it reads <b>0.647 (FR-4)</b> and <b>0.748 (GaAs)</b> against
    /// §CL1's independently measured 0.63 and 0.73 for the same quantity. CL1 measured it by
    /// extracting γ from two bare standards; this measures it through the whole shipped
    /// de-embedding. <b>The two instruments share no algebra and they agree</b>, which is what makes
    /// the 16-40 % under-read the single-sheet model's own structural limit rather than an error
    /// somewhere in this brief.</para>
    ///
    /// <para><b>TOTAL α, which is what a user sees</b> — on the MMIC starter, the substrate class
    /// this whole series exists for, the line reads <b>0.85 / 0.79 / 0.86</b> of kernel A's total at
    /// 2 / 10 / 20 GHz where with PEC metal it read <b>0.06 / 0.13 / 0.18</b>. On the FR-4 starter
    /// at 2 GHz it reads 0.99 against 0.95. <b>The FR-4 10 and 20 GHz points are deliberately NOT in
    /// the total-α band and the reason is pre-existing</b>: 1.6 mm FR-4 is h/λ₀ = 0.05 and 0.11
    /// there, the structure radiates and couples its ports through a surface wave, and the
    /// two-line calibration's own error scales as f² (CLAUDE.md §5). At 20 GHz it over-corrects far
    /// enough that |S₂₁| &gt; 1 and α comes out NEGATIVE — <b>identically so on the PEC oracle, at
    /// three line lengths and two mesh densities</b>, so it is not this brief's and no amount of
    /// refinement moves it. The DIFFERENCE is still good there, which is §CL1's own sentence
    /// ("the absolute α of a PEC FR-4 line out of this route is not a usable number; the difference
    /// is") measured a second time.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("fr4")]
    [InlineData("gaas")]
    public void R_cl3_3_TheDeembeddedLinesAlphaAgreesWithKernelA(string which)
    {
        double[] freqs = [2e9, 10e9, 20e9];
        var (problem, mesh, ports) = UniformLine(which, 20e9);
        double len = ports[1].ReferencePlaneM - ports[0].ReferencePlaneM;
        var (slab, w, t, _, _) = Starter(which);

        var pec   = PlanarSolve.Run(problem, mesh, ports, freqs, Pec);
        var lossy = PlanarSolve.Run(problem, mesh, ports, freqs);

        output.WriteLine($"{which}: w {w * 1e6:0.##} µm, t {t * 1e6:0.#} µm, " +
                         $"plane-to-plane {len * 1e3:0.####} mm, N {mesh.Bases.Count}, " +
                         $"crossover {PlanarCalibration.QuasiStaticCrossoverHz(slab) / 1e9:0.##} GHz");
        output.WriteLine($"{"f",7} {"a_PEC",11} {"a_lossy",11} {"a_c B",11} {"a_c A",11} " +
                         $"{"B/A",7} {"tot B/A",8} {"|S11|",9}  path");

        foreach (var (k, f) in freqs.Index())
        {
            double Alpha(PlanarSolveResult r) => -Math.Log(r.Points[k].S[1, 0].Magnitude) / len;
            double aPec = Alpha(pec), aLossy = Alpha(lossy);
            var a = KernelA(which, f);

            var cal = lossy.Points[k].Calibrations;
            string path = cal.Count > 0 ? cal[0].Source.ToString() : "-";

            output.WriteLine($"{f / 1e9,6:0.#}G {aPec,11:E3} {aLossy,11:E3} {aLossy - aPec,11:E3} " +
                             $"{a.Conductor,11:E3} {(aLossy - aPec) / a.Conductor,7:F3} " +
                             $"{aLossy / a.Total,8:F3} {lossy.Points[k].S[0, 0].Magnitude,9:E2}  {path}");

            // R-cl3-6 — the path is NAMED, and on the MMIC starter it is the quasi-static one at
            // every frequency here, which is exactly why §0 had to be settled before the flip.
            Assert.NotEmpty(path);
            if (which == "gaas")
                Assert.Equal(PlanarCalibrationSource.QuasiStatic, cal[0].Source);

            // The band, from the measurement. A single-zero-thickness sheet under-reads kernel A's
            // thick strip and CL1 measured by how much; this is the same deficit through a
            // different instrument, so the band is CL1's own with the measurement's spread on it.
            double ratio = (aLossy - aPec) / a.Conductor;
            Assert.InRange(ratio, 0.55, 0.90);

            // R-cl3-4, the direction — adding loss can only ADD attenuation.
            Assert.True(aLossy > aPec,
                        $"the conductor term REMOVED loss at {f / 1e9:0.#} GHz: " +
                        $"{aLossy:E3} against {aPec:E3} Np/m");
        }

        // …and the headline, on the one substrate class the series exists for.
        if (which == "gaas")
        {
            double totPec   = -Math.Log(pec.Points[1].S[1, 0].Magnitude) / len / KernelA(which, 10e9).Total;
            double totLossy = -Math.Log(lossy.Points[1].S[1, 0].Magnitude) / len / KernelA(which, 10e9).Total;
            output.WriteLine($"MMIC starter, total α at 10 GHz: {totPec:F3} of kernel A with PEC " +
                             $"metal, {totLossy:F3} with real metal");
            Assert.InRange(totPec, 0.05, 0.25);
            Assert.InRange(totLossy, 0.70, 0.95);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl3-4 — passivity and reciprocity get STRICTER, not looser
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Adding loss to the metal cannot make a passive structure active.</b> Milestone 4's whole
    /// point is that this is the one class of move a re-bless would bury: every other golden here
    /// shifts by a fraction of a per cent and reads as ordinary, while a σ_max that went UP would
    /// be a sign error hiding inside a plausible magnitude.
    ///
    /// <para>Gated on the RAW matrix as well as the de-embedded one, deliberately. The de-embedded
    /// answer on this stack is already over 1 at the bottom of the band for a reason that predates
    /// this series (D6's peel divides by a₂₁², CLAUDE.md §5), so a de-embedded-only test would be
    /// measuring the peel. The raw σ_max is a statement about the FILL, which is the only thing
    /// CL1's term touches.</para>
    /// </summary>
    [Fact]
    public void R_cl3_4_AddingConductorLossCannotMakeAStructureActive()
    {
        double[] freqs = [2e9, 6e9, 10e9];
        var (problem, mesh, ports) = UniformLine("fr4", 10e9, cpw: 10, edge: 0);

        var pec   = PlanarSolve.Run(problem, mesh, ports, freqs, Pec);
        var lossy = PlanarSolve.Run(problem, mesh, ports, freqs);

        output.WriteLine($"N {mesh.Bases.Count}; {"f",7} {"sigma_raw PEC",14} {"sigma_raw lossy",16} " +
                         $"{"|S12-S21| PEC",14} {"lossy",12}");

        foreach (var (k, f) in freqs.Index())
        {
            double SigmaMax(Mat<Complex> s)
            {
                // σ_max(S) = √(λ_max(SᴴS)) — a 2×2, so the closed form rather than an SVD.
                double a = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
                double d = s[0, 1].Magnitude * s[0, 1].Magnitude + s[1, 1].Magnitude * s[1, 1].Magnitude;
                Complex b = Complex.Conjugate(s[0, 0]) * s[0, 1] + Complex.Conjugate(s[1, 0]) * s[1, 1];
                double tr = a + d, det = a * d - b.Magnitude * b.Magnitude;
                return Math.Sqrt(0.5 * (tr + Math.Sqrt(Math.Max(tr * tr - 4 * det, 0))));
            }

            var rp = pec.Points[k].RawS;
            var rl = lossy.Points[k].RawS;
            double sp = SigmaMax(rp), sl = SigmaMax(rl);
            double recP = (rp[0, 1] - rp[1, 0]).Magnitude, recL = (rl[0, 1] - rl[1, 0]).Magnitude;

            output.WriteLine($"{f / 1e9,6:0.#}G {sp,14:F9} {sl,16:F9} {recP,14:E2} {recL,12:E2}");

            // The structural statement: real metal absorbs, so the raw scattering can only SHRINK.
            Assert.True(sl <= sp + 1e-9,
                        $"σ_max(RawS) ROSE when the metal was made lossy at {f / 1e9:0.#} GHz: " +
                        $"{sl:F9} against {sp:F9}");
            Assert.True(sl <= 1.0 + 1e-6, $"the raw matrix is not passive: σ_max = {sl:F9}");

            // Reciprocity is a property of the OPERATOR and Z_s enters it symmetrically
            // (Z[m,n] += Z_s·⟨f_m,f_n⟩ against the SAME Gram, mirrored with the rest of the fill),
            // so it must not degrade at all.
            Assert.True(recL <= Math.Max(recP, 1e-12) * 10.0,
                        $"reciprocity degraded: |S₁₂−S₂₁| went {recP:E2} → {recL:E2}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cl3-0 — milestone 0's decision, kept as a gate rather than only as a note
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The quasi-static calibration path's γ takes R from the STANDARD'S OWN STATIC CHARGE, and
    /// this is the measurement that decided it.</b> Three candidates were weighed (CL3 §0); this
    /// gates the two that are numbers rather than doctrine.
    ///
    /// <para>α_c as kernel B's own two lossy standards see it — the lossy two-line extraction minus
    /// the PEC one, which is §CL1's own instrument — against what each candidate supplier would
    /// have put into γ. <b>The static-charge supplier tracks it to a few per cent and MOVES WITH THE
    /// MESH THE WAY IT DOES; kernel A's Wheeler term does not move with the mesh at all</b>, because
    /// it is a different discretisation of a different formulation, and it over-reads by 20-75 %.
    /// That is the whole argument: this is not a better model of the metal, it is the SAME model the
    /// fill loaded, read off the same mesh, so the two agree across the crossover by construction
    /// rather than by calibration.</para>
    ///
    /// <para><b>Kernel A stays the ORACLE and is still not an input</b> — R-qsc-2's first bullet and
    /// D7's rule. Importing it here would have made both §10.9's A-vs-B gate and the crossover's own
    /// continuity tautologies at once, and it would have been the LESS accurate of the two anyway.</para>
    /// </summary>
    [Theory]
    [InlineData("fr4", 10, 0)]
    [InlineData("fr4", 20, 3)]
    [InlineData("gaas", 10, 0)]
    public void R_cl3_0_TheQuasiStaticConductorTermTracksTheFillsOwn(string which, int cpw, int edge)
    {
        bool gaas = which == "gaas";
        var (slab, w, t, sigma, _) = Starter(which);
        double fLo = gaas ? 2e9 : 500e6, fHi = gaas ? 40e9 : 6e9;
        double[] freqs = gaas ? [2e9, 10e9, 20e9] : [500e6, 1e9, 2e9, 3e9];
        double len = gaas ? 400e-6 : 6e-3;

        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
            slab, fHi);
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                        EdgeMesh: edge > 0, EdgeCells: Math.Max(edge, 1));
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, ms);

        var pecFill   = PlanarFillSettings.Default with { PerfectConductor = true };
        var lossyFill = PlanarFillSettings.Default with
                        { ConductorLoss = PlanarConductorLoss.For(problem) };

        // The MEASURED ladder at every frequency, on both sides, so α_c is a difference of two
        // answers taken the same way — never one quasi-static reading against one measured one.
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);
        var plan = new LineSeparationPlan(
            PlanarCalibration.SuggestDeltas(slab, fLo, fHi), -1, cross);
        var set = PlanarCalibration.BuildSet(
            ports[0], slab, plan, PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);

        var calPec   = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, pecFill,
                                                standards: set, separations: plan);
        var calLossy = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, lossyFill,
                                                standards: set, separations: plan);

        // The shipped supplier, and the one QSC's R-qsc-2 refuses — built off the SAME standards.
        var qs      = PlanarQuasiStaticLine.Extract(set[0], set[^1], slab, lossyFill);
        var qsPec   = PlanarQuasiStaticLine.Extract(set[0], set[^1], slab, pecFill);
        Assert.NotNull(qs.Conductor);
        Assert.Null(qsPec.Conductor);
        double zc = Math.Sqrt(qs.LPerMetre / qs.CPerMetre);

        output.WriteLine($"{which} cells/λ {cpw} edge {edge}: standards N " +
                         $"{string.Join("/", set.Select(x => x.Mesh.Bases.Count))}");
        output.WriteLine($"{"f",8} {"a_c fill",11} {"a_c static",11} {"static/fill",12} " +
                         $"{"a_c kernelA",12} {"A/fill",8}");

        foreach (double f in freqs)
        {
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            double aFill = calLossy.At(kernel, f, 1).Gamma.Gamma.Real
                         - calPec.At(kernel, f, 1).Gamma.Gamma.Real;

            double aStatic = qs.ResistancePerMetreAt(f) / (2.0 * zc);
            double aWheeler = KernelA(which, f).Conductor;

            output.WriteLine($"{f / 1e9,7:0.###}G {aFill,11:E3} {aStatic,11:E3} " +
                             $"{aStatic / aFill,12:F3} {aWheeler,12:E3} {aWheeler / aFill,8:F3}");

            // Candidate B tracks the fill. Measured 0.96-1.01 on the two coarse meshes and 1.06-1.15
            // on the refined GaAs one — the static distribution's edge crowding is the UNLOADED one
            // (series overview §1), bounded rather than divergent because it is integrated on the
            // same mesh the fill's own term is.
            Assert.InRange(aStatic / aFill, 0.90, 1.20);

            // Candidate A does not, and it is the same 20-75 % over-read from either side: §CL1's
            // measured 0.63 (FR-4) / 0.73 (GaAs) loaded-sheet deficit, inverted.
            Assert.True(aWheeler / aFill > 1.15,
                        $"kernel A's Wheeler term would have to over-read the fill for the decision " +
                        $"to hold; it read {aWheeler / aFill:F3} at {f / 1e9:0.###} GHz");

            // …and the static supplier is the CLOSER of the two, at every point. That is the
            // decision, stated as the comparison it was taken on.
            Assert.True(Math.Abs(aStatic / aFill - 1) < Math.Abs(aWheeler / aFill - 1),
                        $"the rejected supplier was closer at {f / 1e9:0.###} GHz");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The oracle, and it has to be exact or it is not one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b><see cref="PlanarFillSettings.PerfectConductor"/> reproduces the pre-CL3 answer BIT FOR
    /// BIT through the whole shipped path</b> — fill, both calibration standards, the quasi-static
    /// line's own γ, the error box and the renormalisation.
    ///
    /// <para>It is asserted as exact equality against a PEC-DECLARED stackup rather than against a
    /// recorded literal, which is the stronger form of the same statement: a problem whose metal is
    /// σ = 0 has no conductor term to carry, so the flag and the stackup must produce the identical
    /// matrix. <b>This is what every CL1 and CL2 accuracy gate rests on</b>, and it is why the flag
    /// stays permanently rather than being deleted once the default has moved.</para>
    ///
    /// <para><b>The quasi-static γ is checked separately and it is not the same statement</b>: it
    /// keeps its own <c>jω√(LC)</c> spelling when there is no conductor term, rather than the
    /// algebraically identical <c>√(jωL·jωC)</c>, precisely so that this equality is to the last bit
    /// and not to the last digit of the physics.</para>
    /// </summary>
    [Fact]
    public void ThePerfectConductorFlagIsBitIdenticalToPecMetal()
    {
        double[] freqs = [1e9, 3e9, 6e9];
        var (problem, mesh, ports) = UniformLine("fr4", 6e9, cpw: 10, edge: 0);

        // The same problem with the metal DECLARED perfect. Two different routes to one matrix.
        var pecProblem = problem with
        {
            Layers = [.. problem.Layers.Select(l => l with { SigmaSm = 0, ThicknessM = 0 })],
        };

        var byFlag    = PlanarSolve.Run(problem, mesh, ports, freqs, Pec);
        var byStackup = PlanarSolve.Run(pecProblem, mesh, ports, freqs);

        for (int k = 0; k < freqs.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Assert.Equal(byStackup.Points[k].S[i, j].Real, byFlag.Points[k].S[i, j].Real);
                    Assert.Equal(byStackup.Points[k].S[i, j].Imaginary,
                                 byFlag.Points[k].S[i, j].Imaginary);
                }

        output.WriteLine($"PEC by flag and PEC by stackup agree to the bit at {freqs.Length} points; " +
                         $"1 GHz S21 = {byFlag.Points[0].S[1, 0]}");

        // And the default is NOT that — the flip actually reached the shipped path.
        var lossy = PlanarSolve.Run(problem, mesh, ports, freqs);
        Assert.NotEqual(byFlag.Points[0].S[1, 0], lossy.Points[0].S[1, 0]);
        output.WriteLine($"default (real copper)      1 GHz S21 = {lossy.Points[0].S[1, 0]}");
    }

    /// <summary>
    /// <b>The conductor term reaches the QUASI-STATIC calibration path, which is where §0 said the
    /// whole MMIC band lives.</b> Structural rather than numerical: a GaAs run's γ below the
    /// 26.07 GHz crossover must carry a non-zero R, and the PEC oracle's must carry exactly none.
    /// </summary>
    [Fact]
    public void TheQuasiStaticGammaCarriesTheConductorTerm()
    {
        var (slab, w, t, sigma, _) = Starter("gaas");
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, 400e-6, 0.5 * w)], sigma, t)],
            slab, 20e9);
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem);

        var plan = PlanarCalibration.SeparationPlan(slab, 2e9, 20e9, ports[0]);
        var set  = PlanarCalibration.BuildSet(
            ports[0], slab, plan, PlanarCalibration.SuggestLengths(slab, 2e9, 20e9).Short);

        var lossy = PlanarQuasiStaticLine.Extract(
            set[0], set[^1], slab,
            PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) });
        var pec = PlanarQuasiStaticLine.Extract(
            set[0], set[^1], slab, PlanarFillSettings.Default with { PerfectConductor = true });

        // Every frequency of the shipped MMIC band is below the crossover — §0's own sentence.
        Assert.True(20e9 < PlanarCalibration.QuasiStaticCrossoverHz(slab));

        Assert.Null(pec.Conductor);
        Assert.Equal(0.0, pec.ResistancePerMetreAt(10e9));
        Assert.NotNull(lossy.Conductor);

        // C is untouched: only γ's series arm gained a term, so ε_eff and C_pul are bit-identical
        // and every quantity D7 reads off them is too.
        Assert.Equal(pec.CComplexPerMetre, lossy.CComplexPerMetre);
        Assert.Equal(pec.C0PerMetre, lossy.C0PerMetre);
        Assert.Equal(pec.EffectivePermittivity, lossy.EffectivePermittivity);

        output.WriteLine($"{"f",8} {"R Ω/m",10} {"alpha PEC",12} {"alpha lossy",12} {"|Zc| PEC",10} " +
                         $"{"|Zc| lossy",11} {"arg Zc PEC",11} {"arg Zc lossy",12}");
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            double r = lossy.ResistancePerMetreAt(f);
            Complex zp = LineDeembed.CharacteristicImpedance(pec.GammaAt(f), pec.CPerMetre, f);
            Complex zl = LineDeembed.CharacteristicImpedance(lossy.GammaAt(f), lossy.CPerMetre, f);
            output.WriteLine($"{f / 1e9,7:0.#}G {r,10:F2} {pec.GammaAt(f).Real,12:E3} " +
                             $"{lossy.GammaAt(f).Real,12:E3} {zp.Magnitude,10:F3} " +
                             $"{zl.Magnitude,11:F3} {zp.Phase * 180 / Math.PI,11:F3} " +
                             $"{zl.Phase * 180 / Math.PI,12:F3}");

            Assert.True(r > 0);
            Assert.True(lossy.GammaAt(f).Real > pec.GammaAt(f).Real);

            // §0's second consequence, closed: Z_c = γ/(jωC′) gets its √(1 + R/(jωL)) correction
            // for free once γ carries R, and the PHASE is the half the overlap gate never looked at.
            Assert.True(Math.Abs(zl.Phase) > Math.Abs(zp.Phase));
        }
    }
}
