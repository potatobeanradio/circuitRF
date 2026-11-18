// CL7 — the ground plane reaches a user. `docs/sonnet-briefs/brief-conductor-loss-7-ground-reaches-a-user.md`;
// findings in `src/Engine/Mom/RESOLVED.md` §CL7.
//
// CL4 built a conducting ground plane on the general kernel and CL6 built it on the one-slab one.
// Every gate passed and NO RUN A USER COULD MAKE GOT ONE, because `PlanarExtractor` wrote
// `Termination.Pec` into both media. This file gates the flip.
//
//   R-cl7-1  A PERFECT floor is bit-identical to pre-CL7, through `PlanarSolve` rather than through
//            the kernel — which is what says the move came from the plane and not from something
//            else in the same commit. `PlanarProblem.WithPerfectGround` is the spelling, and it is NOT
//            `PlanarFillSettings.PerfectConductor` (that makes the STRIP perfect, CL4 §8).
//   R-cl7-2  Against kernel A, both surfaces lossy, gating the TERM and never the SHARE.
//   R-cl7-3  Milestone 0 — where the quasi-static path's γ gets its GROUND term, measured on CL3
//            §0's own instrument at four frequencies and four meshes on both starters.
//   R-cl7-4  Milestone 0b — the size of what the power budget's dielectric line was carrying.
//   R-cl7-5  Passivity and reciprocity with a DIRECTION check; loss may only increase.

using System.Numerics;
using RfCore.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class GroundReachesAUserTests(ITestOutputHelper output)
{
    private const double CopperSigma = 5.8e7, GoldSigma = 4.1e7;

    /// <summary>The two starters, at CL1's own re-bisected 50 Ω widths so every table in this series
    /// describes one cross-section rather than several.</summary>
    private static (GroundedSlab Pec, double W, double T, double Sigma) Starter(string which) =>
        which == "gaas"
            ? (GroundedSlab.GaAsStarter, 70.72e-6, 3e-6, GoldSigma)
            : (GroundedSlab.Fr4Starter,  3020.28e-6, 35e-6, CopperSigma);

    /// <summary>The same slab with its ground plane made of the same metal the strip is.</summary>
    private static GroundedSlab WithFloor(GroundedSlab slab, double sigma, double t) =>
        slab with { Floor = Termination.LossyGround(sigma, t) };

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // The ground kernel's own oracles — asserted before anything is measured with it
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The plane returns the whole current, and a FILAMENT integrates to the textbook closed
    /// form.</b> Two independent statements about <see cref="PlanarGroundReturn.PairKernel"/> that do
    /// not involve a mesh, a solve or a calibration — so if the supplier is wrong later, this says it
    /// is not the kernel.
    ///
    /// <para><c>∫K(d) d²ρ = 1</c> is charge/current neutrality: <c>K̂(0) = 1</c>. And a uniform line
    /// of current at height h gives <c>R = Re(Z_s)/(2πh)</c>, which is ALSO a strict upper bound on
    /// any distributed strip — spreading the source can only spread the return, so a supplier that
    /// exceeded it would be reporting a physically impossible concentration.</para>
    /// </summary>
    [Fact]
    public void TheGroundKernelIntegratesToOneAndToTheFilamentClosedForm()
    {
        foreach (double h in new[] { 1.6e-3, 100e-6 })
        {
            // ∫K d²ρ = 2π∫K(d) d dd, on a substitution that makes it exact rather than quadrature:
            // ∫₀^∞ 2π d·h/(π(4h²+d²)^{3/2}) dd = 2h·[−(4h²+d²)^{−1/2}]₀^∞ = 2h/(2h) = 1.
            double total = 0, dd = h / 4000.0;
            for (int i = 0; i < 4_000_000; i++)
            {
                double d = (i + 0.5) * dd;
                total += 2 * Math.PI * d * PlanarGroundReturn.PairKernel(h, d) * dd;
            }
            // The tail past 1000 h is 2h/√(4h²+d²) ≈ 2h/d = 2e-3 of the whole, added in closed form.
            double dMax = 4_000_000 * dd;
            total += 2 * h / Math.Sqrt(4 * h * h + dMax * dMax);
            output.WriteLine($"h = {h * 1e6:G4} µm: ∫K d²ρ = {total:F9}");
            Assert.InRange(total, 1 - 1e-6, 1 + 1e-6);

            // A uniform filament of length L, differenced between two lengths so the end effect
            // cancels exactly the way the two standards' differencing does.
            double Moment(double lengthM, int n)
            {
                double dy = lengthM / n, sum = 0;
                for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    sum += PlanarGroundReturn.PairKernel(h, Math.Abs(i - j) * dy) * dy * dy;
                return sum;
            }
            double sShort = Moment(200 * h, 2000), sLong = Moment(400 * h, 4000);
            double got = (sLong - sShort) / (200 * h), want = 1.0 / (2 * Math.PI * h);
            output.WriteLine($"   filament ΔS/Δℓ = {got:E6} against 1/(2πh) = {want:E6} " +
                             $"— {(got - want) / want:+0.000%;-0.000%}");
            Assert.InRange(got / want, 0.999, 1.001);
        }
    }

    /// <summary>
    /// <b>The REJECTED candidate's image ladder is right too</b> — checked against a direct numerical
    /// Hankel transform of <c>|σ̂_g(k)|²</c> on both starters, so CL7's decision is a comparison of two
    /// correct kernels rather than of one correct one and one bug. It is also where the 1.63× (FR-4)
    /// and 1.86× (GaAs) concentration factor is read off, at d = 0 and as the filament ratio A₀.
    /// </summary>
    [Fact]
    public void TheRejectedElectrostaticLadderMatchesADirectHankelTransform()
    {
        foreach (var (name, slab) in new[]
        {
            ("FR-4 1.6 mm", GroundedSlab.Fr4Starter),
            ("GaAs 100 µm", GroundedSlab.GaAsStarter),
        })
        {
            var eps = slab.EpsComplex;
            double h = slab.HeightM;
            var c = PlanarGroundReturn.ElectrostaticImageCoefficients(eps, h);
            double a0 = (2.0 * eps / (1.0 + eps)).Magnitude;
            output.WriteLine($"{name}: {c.Length} images, A₀ = {a0:F4} " +
                             $"(the filament concentration factor the charge kernel over-reads by)");

            foreach (double dh in new[] { 0.0, 0.25, 1.0, 4.0 })
            {
                double d = dh * h;
                double series = PlanarGroundReturn.ElectrostaticPairKernel(c, h, d);
                double direct = DirectHankelOfSigmaSquared(eps, h, d);
                double mag    = PlanarGroundReturn.PairKernel(h, d);
                output.WriteLine($"   d/h {dh,5:F2}  ladder {series:E8}  direct {direct:E8}  " +
                                 $"rel {Math.Abs(series - direct) / direct:E2}   charge/current " +
                                 $"{series / mag:F3}");
                Assert.True(Math.Abs(series - direct) / direct < 1e-5);
            }
        }
    }

    /// <summary><c>(1/2π)∫₀^∞|σ̂_g|²J₀(kd)k dk</c>, straight — the ladder's independent oracle.</summary>
    private static double DirectHankelOfSigmaSquared(Complex eps, double h, double d)
    {
        const int n = 400_000;
        double kmax = 200.0 / h, dk = kmax / n, sum = 0;
        for (int i = 1; i <= n; i++)
        {
            double k = (i - 0.5) * dk;
            Complex f = eps / (Complex.Sinh(k * h) + eps * Complex.Cosh(k * h));
            sum += f.Magnitude * f.Magnitude * Bessel.J0(k * d).Real * k * dk;
        }
        return sum / (2 * Math.PI);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl7-1 — a PERFECT floor is bit-identical to pre-CL7, THROUGH PlanarSolve
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every perfect spelling of the floor produces the same bits as <c>Termination.Pec</c>,
    /// through the whole shipped path</b> — fill, both calibration standards, the quasi-static γ, the
    /// error box and the renormalisation. That is what says CL7's move came from the PLANE and not
    /// from something else in the same commit.
    ///
    /// <para><b>It is asked through <see cref="PlanarSolve"/> rather than through the kernel on
    /// purpose.</b> CL4 §1 and CL6 §2 already gated the kernel's own reduction at 0/960 bits; what
    /// this brief adds above it is the calibration path, and the quasi-static γ in particular now has
    /// a second R term that must be exactly absent rather than exactly zero — a
    /// <c>+ 0.0</c> would be invisible here and is what
    /// <c>PlanarQuasiStaticLine.GammaAt</c>'s two spellings exist to prevent.</para>
    ///
    /// <para>The zero is structural: <c>Termination.SurfaceImpedanceAt</c> returns exactly
    /// <see cref="Complex.Zero"/> on <c>PlanarSurfaceImpedance.IsPerfect</c>'s own three spellings,
    /// so every ζ-term is a complex zero ADDED to the shipped expression. The perfect spellings are
    /// deliberately NOT collapsed onto <c>Termination.Pec</c> anywhere — that would make this gate
    /// vacuous, which is CL3 §3's lesson.</para>
    /// </summary>
    [Theory]
    [InlineData("fr4")]
    [InlineData("gaas")]
    public void R_cl7_1_APerfectFloorIsBitIdenticalThroughTheShippedPath(string which)
    {
        bool gaas = which == "gaas";
        var (pecSlab, w, t, sigma) = Starter(which);
        double[] freqs = gaas ? [2e9, 10e9, 20e9] : [1e9, 3e9, 6e9];
        double fHi = freqs[^1];
        double len = gaas ? 400e-6 : 6e-3;

        PlanarProblem With(Termination floor) => new(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
            pecSlab with { Floor = floor }, fHi);

        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(With(Termination.Pec), ms);

        var reference = PlanarSolve.Run(With(Termination.Pec), mesh, ports, freqs);
        int moved = 0, total = 0;

        foreach (var (how, floor) in new[]
        {
            ("σ = +∞",                Termination.LossyGround(double.PositiveInfinity, t)),
            ("t = 0",                 Termination.LossyGround(sigma, 0.0)),
            ("σ = 0",                 Termination.LossyGround(0.0, t)),
        })
        {
            var run = PlanarSolve.Run(With(floor), mesh, ports, freqs);
            for (int k = 0; k < freqs.Length; k++)
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                total += 2;
                if (BitConverter.DoubleToInt64Bits(run.Points[k].S[i, j].Real) !=
                    BitConverter.DoubleToInt64Bits(reference.Points[k].S[i, j].Real)) moved++;
                if (BitConverter.DoubleToInt64Bits(run.Points[k].S[i, j].Imaginary) !=
                    BitConverter.DoubleToInt64Bits(reference.Points[k].S[i, j].Imaginary)) moved++;
            }
            output.WriteLine($"{which}: floor spelled \"{how}\" — {moved}/{total} bits moved so far");
        }

        // PlanarProblem.WithPerfectGround is the SPELLING the rest of this series measures against, so it
        // is exercised here rather than assumed to be the same thing.
        var viaOracle = PlanarSolve.Run(
            With(Termination.LossyGround(sigma, t)).WithPerfectGround(), mesh, ports, freqs);
        for (int k = 0; k < freqs.Length; k++)
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
        {
            total += 2;
            if (BitConverter.DoubleToInt64Bits(viaOracle.Points[k].S[i, j].Real) !=
                BitConverter.DoubleToInt64Bits(reference.Points[k].S[i, j].Real)) moved++;
            if (BitConverter.DoubleToInt64Bits(viaOracle.Points[k].S[i, j].Imaginary) !=
                BitConverter.DoubleToInt64Bits(reference.Points[k].S[i, j].Imaginary)) moved++;
        }
        output.WriteLine($"{which}: PlanarProblem.WithPerfectGround — {moved}/{total} bits moved in all");
        Assert.Equal(0, moved);

        // …and the REAL floor is not that, so the path under test is actually taken. The line's
        // published |S₂₁| falls, which is the whole point of the brief.
        var lossy = PlanarSolve.Run(With(Termination.LossyGround(sigma, t)), mesh, ports, freqs);
        for (int k = 0; k < freqs.Length; k++)
        {
            double a = reference.Points[k].S[1, 0].Magnitude, b = lossy.Points[k].S[1, 0].Magnitude;
            output.WriteLine($"   {freqs[k] / 1e9,5:0.#} GHz  |S21| {a:F9} → {b:F9}  " +
                             $"({20 * Math.Log10(b / a):+0.0000;-0.0000} dB)");
            Assert.True(b < a, "a conducting ground plane did not reduce |S₂₁|");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl7-3 — MILESTONE 0. Where the quasi-static path's γ gets its GROUND term.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The quasi-static calibration path's γ takes its GROUND term from the PLANE'S OWN INDUCED
    /// CHARGE, off the same solve and the same mesh, and this is the measurement that decided
    /// it.</b> CL3 §0's problem one surface over, and it GATES the flip: below
    /// <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> a port is calibrated from a γ supplied
    /// by the standard's own electrostatics, and on the shipped MMIC technology the crossover is
    /// 26.07 GHz — not a corner of the band but all of it.
    ///
    /// <para><b>CL3's own answer does not extend and the reason is structural.</b> Its supplier is
    /// <c>R = Σ_levels Re(Z_s)·[ΔS_level·Δℓ/|ΔT|²]</c>, a sum over MESHED levels; the plane is not a
    /// meshed level and has no ΔS of its own. The brief's candidate (a) — the plane's own density,
    /// through the same TEM identity — is what this measures, and what it finds is that the density
    /// in question is the RETURN CURRENT's rather than the induced CHARGE's: in an inhomogeneous
    /// line those are two different distributions, and the charge one over-reads by A₀ = 2εᵣ/(1+εᵣ).
    /// Both are computed here off the same two solves, so the decision is by number and not by
    /// argument (<see cref="PlanarGroundReturn"/>).</para>
    ///
    /// <para><b>The instrument is CL3 §0's own, with the GROUND's σ as the variable.</b> α as kernel
    /// B's two standards see it with a conducting floor, minus the same two standards on a PEC
    /// floor, on the same mesh — with <c>PlanarFillSettings.PerfectConductor</c> HELD ON throughout,
    /// because that flag makes the strip perfect and NOT the plane (CL4 §8), so what is left is the
    /// ground term on its own. The separation plan's quasi-static index is −1, which is the only way
    /// to make a sub-crossover point take the MEASURED two-line path: comparing the supplier against
    /// a γ the supplier itself produced would be a tautology.</para>
    /// </summary>
    /// <remarks><b>Split by COST, not by content.</b> The coarse pair is 1.8 s together and stays in
    /// the routine gate; the refined pair is 20 s and is tagged, on the repo's mechanical ~5 s rule.
    /// A <c>[Theory]</c>'s <c>InlineData</c> cases cannot be tagged individually, so the split is the
    /// only way to keep the cheap half running on every build — and the cheap half is the one that
    /// would catch a supplier someone deleted rather than narrowed.</remarks>
    [Theory]
    [InlineData("fr4",  10, 0)]
    [InlineData("gaas", 10, 0)]
    public void R_cl7_3_TheQuasiStaticGroundTermTracksTheFillsOwn(string which, int cpw, int edge)
        => GroundTermAgainstTheFill(which, cpw, edge);

    /// <inheritdoc cref="R_cl7_3_TheQuasiStaticGroundTermTracksTheFillsOwn"/>
    [Theory]
    [Trait("Category", "Benchmark")]          // measured: 6.8 s (FR-4) + 13.3 s (GaAs) — the refined
    [InlineData("fr4",  20, 3)]               // meshes' own standards, four frequencies each
    [InlineData("gaas", 20, 3)]
    public void R_cl7_3_TheQuasiStaticGroundTermTracksTheFillsOwn_RefinedMesh(
        string which, int cpw, int edge)
        => GroundTermAgainstTheFill(which, cpw, edge);

    private void GroundTermAgainstTheFill(string which, int cpw, int edge)
    {
        bool gaas = which == "gaas";
        var (pecSlab, w, t, sigma) = Starter(which);
        var slab = WithFloor(pecSlab, sigma, t);
        double fLo = gaas ? 5e9 : 500e6, fHi = gaas ? 40e9 : 6e9;
        double[] freqs = gaas ? [5e9, 10e9, 20e9, 25e9] : [500e6, 1e9, 2e9, 3e9];
        double len = gaas ? 400e-6 : 6e-3;

        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
            slab, fHi);
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                        EdgeMesh: edge > 0, EdgeCells: Math.Max(edge, 1));
        var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, ms);

        // The STRIP is perfect on both sides of every difference below, so the ground term is
        // isolated exactly as CL4 §7's B1 − A1 isolates it.
        var pecFill = PlanarFillSettings.Default with { PerfectConductor = true };

        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);
        var plan = new LineSeparationPlan(
            PlanarCalibration.SuggestDeltas(slab, fLo, fHi), -1, cross);
        var set = PlanarCalibration.BuildSet(
            ports[0], slab, plan, PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);

        var calGround = new PlanarPortCalibrator(ports[0], slab,    fLo, fHi, null, pecFill,
                                                 standards: set, separations: plan);
        var calPec    = new PlanarPortCalibrator(ports[0], pecSlab, fLo, fHi, null, pecFill,
                                                 standards: set, separations: plan);

        // The supplier itself, off the SAME two standards the differences above are taken on.
        var qs    = PlanarQuasiStaticLine.Extract(set[0], set[^1], slab,    pecFill);
        var qsPec = PlanarQuasiStaticLine.Extract(set[0], set[^1], pecSlab, pecFill);
        Assert.True(qs.GroundTermSupplied, "the conducting floor produced no ground term at all");
        Assert.False(qsPec.GroundTermSupplied, "a PEC floor must produce no ground term");
        Assert.Null(qs.Conductor);        // PerfectConductor speaks for the STRIP, and only for it
        double zc = Math.Sqrt(qs.LPerMetre / qs.CPerMetre);

        output.WriteLine($"{which} cells/λ {cpw} edge {edge}: standards N " +
                         $"{string.Join("/", set.Select(x => x.Mesh.Bases.Count))}, " +
                         $"crossover {cross / 1e9:F3} GHz, Zc {zc:F2} Ω");
        // The REJECTED candidate, computed off the same two solves so it is rejected by number.
        double eRatio = RejectedElectrostaticRatio(set[0], set[^1], slab, pecFill);

        output.WriteLine($"{"f",9} {"a_gnd fill",12} {"a_gnd supplier",15} {"supplier/fill",14} " +
                         $"{"a_gnd charge",13} {"charge/fill",12}");

        foreach (double f in freqs)
        {
            double aFill = calGround.At(PlanarLineFixtures.Kernel(slab, f), f, 1).Gamma.Gamma.Real
                         - calPec.At(PlanarLineFixtures.Kernel(pecSlab, f), f, 1).Gamma.Gamma.Real;
            double aStatic = qs.GroundResistancePerMetreAt(f) / (2.0 * zc);
            double aCharge = aStatic * eRatio;

            output.WriteLine($"{f / 1e9,8:0.###}G {aFill,12:E3} {aStatic,15:E3} " +
                             $"{aStatic / aFill,14:F3} {aCharge,13:E3} {aCharge / aFill,12:F3}");

            Assert.True(aFill > 0, $"the fill's own ground term is not positive at {f / 1e9:0.###} GHz");
            Assert.True(aStatic > 0, "the supplier's ground term is not positive");

            // The supplier tracks the fill's own ground term. The band is stated from the
            // measurement; see RESOLVED.md §CL7 for what the drift down the column is.
            Assert.InRange(aStatic / aFill, 0.85, 1.05);

            // …and the plane's CHARGE distribution — the candidate that looks equally reasonable
            // from the algebra — does not. It over-reads by A₀ = 2εᵣ/(1+εᵣ), which is a property of
            // the medium and not of this mesh, and it is the CLOSER of the two at no point.
            Assert.True(Math.Abs(aStatic / aFill - 1) < Math.Abs(aCharge / aFill - 1),
                        $"the rejected charge kernel was closer at {f / 1e9:0.###} GHz");
        }
    }

    /// <summary>
    /// <b>The REJECTED candidate's ratio to the shipped one on this pair of standards</b> — the
    /// plane's electrostatic induced CHARGE against its magnetostatic RETURN CURRENT, off the same
    /// two charge vectors, so the comparison is a pure kernel swap. For a filament it is exactly
    /// A₀ = 2ε*/(1+ε*) — 1.63 on FR-4, 1.86 on GaAs — and a distributed strip sits near it.
    /// </summary>
    private static double RejectedElectrostaticRatio(
        PlanarStandard shortStd, PlanarStandard longStd, GroundedSlab slab, PlanarFillSettings fill)
    {
        var terms = PlanarKernelTerms.StaticScalar(slab);
        var q1 = PlanarDeembed.StaticChargeComplex(shortStd.Mesh, terms, fill, null, slab.HeightM,
                                                   shortStd.ModePotential, shortStd.FloatingPotential);
        var q2 = PlanarDeembed.StaticChargeComplex(longStd.Mesh, terms, fill, null, slab.HeightM,
                                                   longStd.ModePotential, longStd.FloatingPotential);
        double h = slab.HeightM;
        double mag = PlanarGroundReturn.SecondMoment(longStd.Mesh, q2, h)
                   - PlanarGroundReturn.SecondMoment(shortStd.Mesh, q1, h);
        double ele = PlanarGroundReturn.ElectrostaticSecondMoment(longStd.Mesh, q2, slab.EpsComplex, h)
                   - PlanarGroundReturn.ElectrostaticSecondMoment(shortStd.Mesh, q1, slab.EpsComplex, h);
        return ele / mag;
    }

    /// <summary>
    /// <b>The supplier and the fill's own ground term move TOGETHER under a mesh change</b> — CL3
    /// §1's argument, and what says these are one model read off one solve rather than two models
    /// that happen to agree at one mesh.
    ///
    /// <para><b>The finding is that neither of them moves much, and that is a statement about the
    /// PLANE rather than a weak test.</b> CL3's STRIP term rose ~32% on FR-4 when the edge mesh was
    /// turned on, because the strip's own current crowds at its edges and the mesh is what resolves
    /// that. The plane's return current has no edge to crowd at: it is spread over a width set by h,
    /// which the strip's mesh does not change. So both move by ~1% — the fill by ×1.013 and the
    /// supplier by ×1.007 — and what is asserted is that the RATIO between them holds, which is the
    /// same claim one rung down.</para>
    /// </summary>
    [Fact]
    public void R_cl7_3b_TheSupplierAndTheFillMoveTogetherAcrossTheMesh()
    {
        var (pecSlab, w, t, sigma) = Starter("fr4");
        var slab = WithFloor(pecSlab, sigma, t);
        const double fLo = 500e6, fHi = 6e9, f = 2e9, len = 6e-3;

        (double Fill, double Static) At(int cpw, int edge)
        {
            var problem = new PlanarProblem(
                [new PlanarConductorLayer("Metal",
                    [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
                slab, fHi);
            var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                            EdgeMesh: edge > 0, EdgeCells: Math.Max(edge, 1));
            var (_, ports) = PlanarLineFixtures.MeshAndPorts(problem, ms);
            var pecFill = PlanarFillSettings.Default with { PerfectConductor = true };
            var plan = new LineSeparationPlan(
                PlanarCalibration.SuggestDeltas(slab, fLo, fHi), -1,
                PlanarCalibration.QuasiStaticCrossoverHz(slab));
            var set = PlanarCalibration.BuildSet(
                ports[0], slab, plan, PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);

            double fill = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, pecFill,
                              standards: set, separations: plan)
                          .At(PlanarLineFixtures.Kernel(slab, f), f, 1).Gamma.Gamma.Real
                        - new PlanarPortCalibrator(ports[0], pecSlab, fLo, fHi, null, pecFill,
                              standards: set, separations: plan)
                          .At(PlanarLineFixtures.Kernel(pecSlab, f), f, 1).Gamma.Gamma.Real;

            var qs = PlanarQuasiStaticLine.Extract(set[0], set[^1], slab, pecFill);
            return (fill, qs.GroundResistancePerMetreAt(f) /
                          (2.0 * Math.Sqrt(qs.LPerMetre / qs.CPerMetre)));
        }

        var coarse = At(10, 0);
        var fine   = At(20, 3);
        output.WriteLine($"FR-4 at {f / 1e9:F0} GHz, ground term Np/m:");
        output.WriteLine($"  cells/λ 10, no edge mesh : fill {coarse.Fill:E4}  supplier {coarse.Static:E4}" +
                         $"  ratio {coarse.Static / coarse.Fill:F3}");
        output.WriteLine($"  cells/λ 20, edge mesh 3  : fill {fine.Fill:E4}  supplier {fine.Static:E4}" +
                         $"  ratio {fine.Static / fine.Fill:F3}");
        output.WriteLine($"  MESH MOVE: fill ×{fine.Fill / coarse.Fill:F4}, " +
                         $"supplier ×{fine.Static / coarse.Static:F4}");

        // The ratio holds across the mesh change — measured 0.927 and 0.922 — which is the claim.
        double rc = coarse.Static / coarse.Fill, rf = fine.Static / fine.Fill;
        Assert.InRange(rf / rc, 0.95, 1.05);
        Assert.InRange(rc, 0.85, 1.05);
        Assert.InRange(rf, 0.85, 1.05);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl7-2 — against kernel A, both surfaces lossy, and it gates the TERM
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two independent formulations of ground loss, on the path a user's run now takes.</b> CL4
    /// §7 measured this on the GENERAL kernel with an explicit <c>LayerStack</c>, because that was
    /// the only place a conducting floor could be spelled. CL6 put one on <c>GroundedSlab</c> and
    /// CL7 makes the extractor write it, so the shipped route for an ordinary microstrip is the
    /// ONE-SLAB kernel — and that is what this runs on.
    ///
    /// <para><b>γ comes from the two-line extraction directly, never through
    /// <c>PlanarPortCalibrator</c></b>, so the QSC crossover is not in the path and this measurement
    /// is independent of milestone 0's. CL1's own gates did the same.</para>
    ///
    /// <para><b>The rows are the two where k₀H ≤ 0.07</b>, which is where CL4 §7 established the two
    /// formulations agree to ≈6%: GaAs 100 µm at 10 GHz (k₀H = 0.0209) and FR-4 1.6 mm at 2 GHz
    /// (0.0671). Above k₀H ≈ 0.3 neither kernel is authoritative — kernel A's quasi-TEM rule has no
    /// radiation term and the two-line instrument has lost resolution — and those rows are reported
    /// rather than resolved, in <c>PlanarLossyGroundTests.M5</c>.</para>
    ///
    /// <para><b>The SHARE is deliberately not gated and must not be quoted.</b> Kernel B reads 27.48%
    /// where kernel A reads 21.06% on the same row, and the inflation is exactly CL1's measured
    /// single-sheet strip deficit in the DENOMINATOR — quoting it as the ground's share of the
    /// conductor term would be quoting that deficit back with the opposite sign. The overview's
    /// 21.1% / ~11% / 25.0% are statements about the physics, reproduced by kernel A to the digit.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData("fr4",  2e9)]
    [InlineData("gaas", 10e9)]
    public void R_cl7_2_TheGroundTermAgreesWithKernelAWhereTheSubstrateIsThin(string which, double fHz)
    {
        var (pecSlab, w, t, sigma) = Starter(which);
        double k0h = 2 * Math.PI * fHz / EmConstants.C0 * pecSlab.HeightM;

        var (rStrip, z0, eeff) = KernelA(w, pecSlab, t, sigma, fHz, double.PositiveInfinity);
        var (rBoth,  _,  _)    = KernelA(w, pecSlab, t, sigma, fHz, sigma);
        double aStripA = rStrip / (2 * z0), aGroundA = (rBoth - rStrip) / (2 * z0);

        output.WriteLine($"{which} at {fHz / 1e9:F0} GHz: w {w * 1e6:F2} µm, t {t * 1e6:F1} µm, " +
                         $"σ {sigma:E3} S/m, k₀H {k0h:F4}, Z₀ {z0:F2} Ω, ε_eff {eeff:F4}");
        output.WriteLine($"  kernel A: α_strip {aStripA:E4}  α_ground {aGroundA:E4}  " +
                         $"α_total {aStripA + aGroundA:E4} Np/m   share {aGroundA / (aStripA + aGroundA):P2}");
        output.WriteLine("  kernel B, ONE-SLAB path (GroundedSlab.Floor), four alphas off one mesh " +
                         "and one pair of standards. A1 is the all-PEC extraction floor.");

        foreach (double gs in new[] { sigma, sigma / 4.0 })
        {
            var (a1, a2, b1, b2, n) = FourAlphas(pecSlab, w, t, sigma, fHz, eeff, gs);
            double aStripB = a2 - a1, aGroundB = b1 - a1, aTotalB = b2 - a1;
            double gA = gs == sigma ? aGroundA
                      : (KernelA(w, pecSlab, t, sigma, fHz, gs).R - rStrip) / (2 * z0);

            output.WriteLine($"   ground σ = {gs:E3} S/m, N = {n}");
            output.WriteLine($"     A1 {a1,12:E5}  A2 {a2,12:E5}  B1 {b1,12:E5}  B2 {b2,12:E5} Np/m");
            output.WriteLine($"     α_strip {aStripB:E4}  α_ground {aGroundB:E4}  α_total {aTotalB:E4}");
            output.WriteLine($"     additivity (α_strip+α_ground)/α_total = {(aStripB + aGroundB) / aTotalB:F4}" +
                             $"   ground B/A = {aGroundB / gA:F4}");

            Assert.True(aGroundB > 0, $"the ground term removed loss rather than adding it: {aGroundB:E3}");

            // THE GATE: the TERM, against kernel A, where the substrate is electrically thin.
            Assert.InRange(aGroundB / gA, 0.90, 1.12);

            // Additivity — the two surfaces are not double-counting each other through the solved
            // current. CL4 measured 1.0002 … 1.0021.
            Assert.InRange((aStripB + aGroundB) / aTotalB, 0.99, 1.01);
        }

        // The 1/√σ signature: a 4× resistivity step must multiply the ground term by √4.
        var full    = FourAlphas(pecSlab, w, t, sigma, fHz, eeff, sigma);
        var quarter = FourAlphas(pecSlab, w, t, sigma, fHz, eeff, sigma / 4.0);
        double scale = (quarter.B1 - quarter.A1) / (full.B1 - full.A1);
        output.WriteLine($"  1/√σ scaling across a 4× resistivity step: ×{scale:F4} against √4 = 2");
        Assert.InRange(scale, 1.85, 2.15);
    }

    private static (double R, double Z0, double Eeff) KernelA(double w, GroundedSlab slab, double t,
                                                              double sigmaSm, double fHz,
                                                              double groundSigma)
    {
        var p = EmProblemBuilders.Microstrip(w, slab.HeightM, t, slab.Material.EpsR, tanD: 0,
                                             sigmaSm: sigmaSm, groundSigmaSm: groundSigma);
        var res = new QuasiStaticKernel().SolveDetailed(p, EmMeshSettings.Default, [fHz]);
        return (res.Rlgc.RPerM(2 * Math.PI * fHz),
                Math.Sqrt(res.Rlgc.LPerM / res.Rlgc.CPerM), res.Rlgc.Eeff);
    }

    /// <summary>
    /// The four alphas one mesh and one pair of standards produce: {PEC, real} strip crossed with
    /// {PEC, real} ground, on the ONE-SLAB kernel.
    ///
    /// <para><b><c>PlanarFillSettings.PerfectConductor</c> makes the STRIP perfect and nothing
    /// else</b> — the plane is in the Green's function, not in the fill — which is exactly what makes
    /// B1 − A1 the ground term on its own, and exactly the trap that ate CL4's first measurement
    /// (subtracting a PEC-metal floor taken on the LOSSY-ground slab removes the very term being
    /// looked for and reports a ground share of −0.03% with every assertion green). The floor here
    /// comes from the PEC-ground slab.</para>
    /// </summary>
    private static (double A1, double A2, double B1, double B2, int N) FourAlphas(
        GroundedSlab pecSlab, double widthM, double t, double sigmaSm, double fHz, double eeff,
        double groundSigma)
    {
        double lambdaG = EmConstants.C0 / (fHz * Math.Sqrt(eeff));
        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);
        int n = 0;

        (double Pec, double Lossy) Pair(GroundedSlab slab)
        {
            var problem = new PlanarProblem(
                [new PlanarConductorLayer("Metal",
                    [PlanarLineFixtures.Rect(0, -0.5 * widthM, 4.0 * widthM, 0.5 * widthM)],
                    sigmaSm, t)],
                slab, fHz);

            var report = SurfaceMesher.Mesh(problem, ms);
            n = report.Mesh.Bases.Count;
            var ports  = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem));
            var kernel = PlanarFrequencyKernel.Fit(problem, fHz);
            var levels = new PlanarLevels([slab.HeightM]);

            int k = PlanarCalibration.EndRunCellsFor(ports[0], slab);
            var a = PlanarCalibration.BuildLine(ports[0], 0.25 * lambdaG, k);
            var b = PlanarCalibration.BuildLine(ports[0], a.LengthM + 0.25 * lambdaG, k);

            double Alpha(PlanarFillSettings settings)
            {
                var sa = new PlanarSolveContext(a.Mesh, a.Ports, settings, levels).RawScatteringAt(kernel, fHz);
                var sb = new PlanarSolveContext(b.Mesh, b.Ports, settings, levels).RawScatteringAt(kernel, fHz);
                double expect = 2 * Math.PI * Math.Sqrt(eeff) * fHz * (b.LengthM - a.LengthM) / EmConstants.C0;
                return LineCalibration.Gamma(sa, sb, b.LengthM - a.LengthM, expect).Gamma.Real;
            }

            return (Alpha(PlanarFillSettings.Default with { PerfectConductor = true }),
                    Alpha(PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) }));
        }

        var pec   = Pair(pecSlab);
        var lossy = Pair(pecSlab with { Floor = Termination.LossyGround(groundSigma, t) });
        return (pec.Pec, pec.Lossy, lossy.Pec, lossy.Lossy, n);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl7-5 — passivity and reciprocity, with a DIRECTION check
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Making the ground plane a real conductor cannot make a passive structure active, and it
    /// may only ADD loss.</b> CL4 carried none of CL3's gates and §CL4 §9 named this as one of the
    /// three that a default flip of this reach owes.
    ///
    /// <para>Gated on the RAW matrix as well as the de-embedded one, for CL3 §9's reason: the
    /// de-embedded answer on this stack is already over 1 at the bottom of the band because D6's
    /// peel divides by a₂₁², so a de-embedded-only test would be measuring the peel. The raw σ_max is
    /// a statement about the operator, which is the only thing the floor touches.</para>
    ///
    /// <para><b>The DIRECTION is the point.</b> Every other golden in the re-bless moves by a
    /// fraction of a per cent and reads as ordinary; a σ_max that went UP would be a sign error
    /// hiding inside a plausible magnitude, and only this notices.</para>
    /// </summary>
    [Theory]
    [InlineData("fr4")]
    [InlineData("gaas")]
    public void R_cl7_5_AConductingGroundPlaneMayOnlyAddLoss(string which)
    {
        bool gaas = which == "gaas";
        var (pecSlab, w, t, sigma) = Starter(which);
        double[] freqs = gaas ? [2e9, 10e9, 20e9] : [1e9, 3e9, 6e9];
        double len = gaas ? 400e-6 : 6e-3;

        PlanarProblem With(Termination floor) => new(
            [new PlanarConductorLayer("Metal",
                [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
            pecSlab with { Floor = floor }, freqs[^1]);

        var ms = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(With(Termination.Pec), ms);

        var pec   = PlanarSolve.Run(With(Termination.Pec), mesh, ports, freqs);
        var lossy = PlanarSolve.Run(With(Termination.LossyGround(sigma, t)), mesh, ports, freqs);

        output.WriteLine($"{which}, N {mesh.Bases.Count}: {"f",8} {"σmax PEC floor",16} " +
                         $"{"σmax real plane",16} {"|S12−S21| PEC",15} {"real",12}");

        static double SigmaMax(Mat<Complex> s)
        {
            // σ_max(S) = √(λ_max(SᴴS)) — a 2×2, so the closed form rather than an SVD.
            double a = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
            double d = s[0, 1].Magnitude * s[0, 1].Magnitude + s[1, 1].Magnitude * s[1, 1].Magnitude;
            Complex b = Complex.Conjugate(s[0, 0]) * s[0, 1] + Complex.Conjugate(s[1, 0]) * s[1, 1];
            double tr = a + d, det = a * d - b.Magnitude * b.Magnitude;
            return Math.Sqrt(0.5 * (tr + Math.Sqrt(Math.Max(tr * tr - 4 * det, 0))));
        }

        for (int k = 0; k < freqs.Length; k++)
        {
            var rp = pec.Points[k].RawS;
            var rl = lossy.Points[k].RawS;
            double sp = SigmaMax(rp), sl = SigmaMax(rl);
            double recP = (rp[0, 1] - rp[1, 0]).Magnitude, recL = (rl[0, 1] - rl[1, 0]).Magnitude;

            output.WriteLine($"{freqs[k] / 1e9,7:0.#}G {sp,16:F9} {sl,16:F9} {recP,15:E2} {recL,12:E2}");

            Assert.True(sl <= sp + 1e-12,
                        $"σ_max(RawS) ROSE when the ground plane was made a conductor at " +
                        $"{freqs[k] / 1e9:0.#} GHz: {sl:F12} against {sp:F12}");
            Assert.True(sl <= 1.0 + 1e-6, $"the raw matrix is not passive: σ_max = {sl:F9}");

            // Reciprocity is a property of the OPERATOR: the floor enters through the Green's
            // function, which is symmetric in source and observer, so it must not degrade at all.
            Assert.True(recL <= Math.Max(recP, 1e-12) * 10.0,
                        $"reciprocity degraded: |S₁₂−S₂₁| went {recP:E2} → {recL:E2}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // R-cl7-4 — MILESTONE 0b. What the power budget's dielectric line was carrying
    // ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A conducting ground plane's dissipation lands in the budget's RESIDUAL, and this is how
    /// big it is.</b> <c>PlanarPowerBudget.ConductorW</c> is an integral over the fill's own basis
    /// functions and the plane has none — it enters as a termination of the Green's function — so
    /// what it absorbs arrives as everything-else-minus and is booked against the line CL2 called
    /// dielectric loss. CL4 §8 recorded that and deliberately did not fix it; it was harmless while
    /// no run had a conducting floor and is not afterwards.
    ///
    /// <para><b>The sharpest form of the measurement is tanδ = 0 with a PERFECT strip</b>, exactly
    /// R-cl2-3a's construction one surface over: then the ground plane is the ONLY absorber in the
    /// model, so the whole residual is the plane's and its size is read directly rather than
    /// differenced. The second row is the realistic one — real metal, real tanδ — where the question
    /// is what fraction of the line a user actually reads is not dielectric loss.</para>
    ///
    /// <para><b>The decision this measurement supports is recorded in <c>RESOLVED.md</c> §CL7:
    /// the line is RENAMED and re-documented rather than split.</b> Splitting needs a second
    /// quadratic form in the spectral domain — a 2D spectral integral per basis PAIR for
    /// <c>½∫Re(Z_s)|H_tan|²</c> over the plane — which is a brief rather than a relabelling, and
    /// CL4's own "Must NOT" reserved the residual's arithmetic. What this brief may not do is leave
    /// a line whose label says one mechanism while it reports two.</para>
    /// </summary>
    [Theory]
    [InlineData("fr4")]
    [InlineData("gaas")]
    public void R_cl7_4_HowMuchOfTheDielectricLineIsTheGroundPlane(string which)
    {
        bool gaas = which == "gaas";
        var (starterSlab, w, t, sigma) = Starter(which);
        double f = gaas ? 30e9 : 6e9;
        double len = gaas ? 1200e-6 : 20e-3;

        (PlanarPowerBudget B, int N) Budget(GroundedSlab slab, bool perfectStrip)
        {
            var problem = new PlanarProblem(
                [new PlanarConductorLayer("Metal",
                    [PlanarLineFixtures.Rect(0, -0.5 * w, len, 0.5 * w)], sigma, t)],
                slab, f);
            var mesh = SurfaceMesher.Mesh(problem,
                new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false)).Mesh;
            var ports = PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem));

            var loss = PlanarConductorLoss.For(problem);
            var fill = perfectStrip
                     ? PlanarFillSettings.Default with { PerfectConductor = true }
                     : PlanarFillSettings.Default with { ConductorLoss = loss };
            var sol = new PlanarSolveContext(mesh, ports, fill)
                          .SolveAt(PlanarLineFixtures.Kernel(slab, f), f);
            var pattern = PlanarFarField.Compute(problem, mesh, sol.Currents[0], ports[0].Number, f,
                                                 PlanarFarFieldGrid.Hemisphere(1, 2));
            var inputs = perfectStrip
                       ? null
                       : new PlanarConductorLossInputs(loss, PlanarGram.Build(mesh), null);
            return (PlanarPowerBudget.For(problem, mesh, sol.Currents[0], pattern, sol.Y[0, 0], inputs),
                    mesh.Bases.Count);
        }

        // ── Row 1: tanδ = 0 and a PERFECT strip — the plane is the only absorber in the model ──
        var lossless = starterSlab with { Material = new EmMaterial(starterSlab.Material.EpsR, 0.0) };
        var (cleanPec,  n) = Budget(lossless, perfectStrip: true);
        var (cleanReal, _) = Budget(lossless with { Floor = Termination.LossyGround(sigma, t) },
                                    perfectStrip: true);

        output.WriteLine($"{which} line at {f / 1e9:F0} GHz, N = {n}");
        output.WriteLine($"  tanδ = 0, PERFECT strip — the ground plane is the ONLY absorber:");
        output.WriteLine($"    PEC floor : residual {SurfaceMesher.Eng(cleanPec.DielectricAndGroundW)}W " +
                         $"= {cleanPec.DielectricAndGroundW / cleanPec.AcceptedW:E2} of accepted");
        output.WriteLine($"    real plane: residual {SurfaceMesher.Eng(cleanReal.DielectricAndGroundW)}W " +
                         $"= {cleanReal.DielectricAndGroundW / cleanReal.AcceptedW:P3} of accepted");

        // With tanδ = 0 and a perfect strip the PEC-floor residual must vanish (it is the fill's own
        // accuracy, ~1e-2 relative per RESOLVED.md §ANT-5), and the real-plane one must not.
        Assert.True(Math.Abs(cleanPec.DielectricAndGroundW / cleanPec.AcceptedW) < 1e-2);
        Assert.True(cleanReal.DielectricAndGroundW / cleanReal.AcceptedW > 1e-3,
                    "a conducting ground plane absorbed nothing at all");

        // ── Row 2: the realistic line, PEC ground against a real one ───────────────────────────
        var (realPec,  _) = Budget(starterSlab, perfectStrip: false);
        var (realReal, _) = Budget(starterSlab with { Floor = Termination.LossyGround(sigma, t) },
                                   perfectStrip: false);
        double ground = realReal.DielectricAndGroundW - realPec.DielectricAndGroundW;

        output.WriteLine($"  tanδ = {starterSlab.Material.TanD:G3}, real strip:");
        output.WriteLine($"    PEC floor : {realPec.Caption}");
        output.WriteLine($"    real plane: {realReal.Caption}");
        output.WriteLine($"    the PLANE's share of the renamed line: " +
                         $"{SurfaceMesher.Eng(ground)}W of " +
                         $"{SurfaceMesher.Eng(realReal.DielectricAndGroundW)}W = " +
                         $"{ground / realReal.DielectricAndGroundW:P2}");

        Assert.True(ground > 0, "the plane subtracted from the residual rather than adding to it");

        // The bookkeeping still closes, which is what says nothing was dropped on the way through.
        double sum = realReal.RadiatedW + realReal.SurfaceWaveW +
                     realReal.DielectricAndGroundW + realReal.ConductorW;
        Assert.Equal(realReal.AcceptedW, sum, 1e-12 * realReal.AcceptedW);
    }
}
