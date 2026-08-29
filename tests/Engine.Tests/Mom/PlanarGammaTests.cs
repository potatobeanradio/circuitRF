// L8d Tiers 2 and 3 — γ two ways, then B against A.
//
// Tier 2 is the tier that decides whether the two-line extraction is right, and it is decided
// against an oracle that shares NO ALGEBRA with it: CurrentWaveOracle reads γ off the travelling
// wave of a single solve, with no T-matrix, no error box and no calibration standard anywhere in
// the path. A disagreement therefore localises itself — the current fit is wrong if it is noisy
// across stations, the two-line step is wrong if it is smooth and offset.
//
// Tier 3 is the phase table's own words: "A and B agree on a uniform line". Kernel A is the ORACLE
// here and never an input (D7) — reading Z_c or C_pul off QuasiStaticKernel and feeding it into B
// would make this gate a tautology.

using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarGammaTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-5 — the standard's port neighbourhood is IDENTICAL, asserted on coordinates
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T2_1_TheStandardReproducesTheDutsOwnCellsExactly_NotToATolerance()
    {
        var problem     = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);

        int k  = PlanarCalibration.EndRunCellsFor(prt[0], problem.Slab);
        var st = PlanarCalibration.BuildLine(prt[0], 6e-3, k);

        _out.WriteLine($"DUT N = {mesh.Bases.Count}; standard N = {st.Mesh.Bases.Count}, " +
                       $"ℓ = {st.LengthM * 1e3:F4} mm, end run {k} cell(s)");

        // The transverse partition is the DUT's, verbatim.
        Assert.Equal(prt[0].TransverseLines.Count, st.Port1.TransverseLines.Count);
        for (int i = 0; i < prt[0].TransverseLines.Count; i++)
            Assert.Equal(prt[0].TransverseLines[i], st.Port1.TransverseLines[i], 15);

        // And so is the longitudinal run inward from the port, for the first K cells — an EQUALITY,
        // because D4 makes the error box the same object rather than a similar one.
        for (int i = 0; i < k; i++)
        {
            Assert.Equal(prt[0].LongitudinalRunM[i], st.Port1.LongitudinalRunM[i], 15);
            Assert.Equal(prt[0].LongitudinalRunM[i], st.Port2.LongitudinalRunM[i], 15);
        }

        // Both ports of the standard resolve to the same width and the same number of bases as the
        // DUT's — which is what makes B's column structure transferable.
        Assert.Equal(prt[0].BasisCount, st.Port1.BasisCount);
        Assert.Equal(prt[0].BasisCount, st.Port2.BasisCount);
        Assert.Equal(prt[0].WidthM, st.Port1.WidthM, 15);

        // The reported length IS the plane-to-plane distance, not the drawn length — and it has a
        // FLOOR, because a standard cannot be shorter than its own two end runs. That floor is what
        // makes SuggestLengths return a separation rather than a second absolute length.
        double endLen = 0;
        for (int i = 0; i < k; i++) endLen += prt[0].LongitudinalRunM[i];
        double floor = 2 * (endLen - prt[0].LongitudinalRunM[0]);
        _out.WriteLine($"floor from the two end runs alone: {floor * 1e3:F4} mm");

        Assert.True(st.LengthM >= Math.Max(6e-3, floor) - 1e-15,
            $"the standard is {st.LengthM * 1e3:F3} mm, below both the 6 mm asked for and its own floor");
        Assert.True(st.LengthM < Math.Max(6e-3, floor) + prt[0].BulkCellM,
            "the standard overshot its floor by more than one bulk cell");
    }

    [Fact]
    public void T2_2_TwoStandardsOfDifferentLengthShareTheirWholePortNeighbourhood()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var (_, prt) = PlanarLineFixtures.MeshAndPorts(problem);
        int k = PlanarCalibration.EndRunCellsFor(prt[0], problem.Slab);

        var a = PlanarCalibration.BuildLine(prt[0], 5e-3,  k);
        var b = PlanarCalibration.BuildLine(prt[0], 12e-3, k);

        _out.WriteLine($"ℓ₁ = {a.LengthM * 1e3:F4} mm (N = {a.Mesh.Bases.Count}), " +
                       $"ℓ₂ = {b.LengthM * 1e3:F4} mm (N = {b.Mesh.Bases.Count})");

        // The two lines differ ONLY in how many bulk cells sit in the middle. If they differed at
        // the port too, the error box would not cancel and every de-embedded number would be wrong
        // in a way that looks like a convergence problem.
        for (int i = 0; i < k; i++)
        {
            Assert.Equal(a.Port1.LongitudinalRunM[i], b.Port1.LongitudinalRunM[i], 15);
            Assert.Equal(a.Port2.LongitudinalRunM[i], b.Port2.LongitudinalRunM[i], 15);
        }
        Assert.Equal(a.Port1.WidthM, b.Port1.WidthM, 15);
        Assert.True(b.LengthM > a.LengthM);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 2 — γ two ways
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T2_3_TwoLineGammaAgreesWithTheTravellingWaveOracle_WhichSharesNoAlgebraWithIt()
    {
        const double f = 10e9;
        var problem  = PlanarLineFixtures.Fr4Line(20e-3, f);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
        var kernel   = PlanarLineFixtures.Kernel(problem.Slab, f);

        // Route 1: the travelling wave on the DUT itself. No calibration in the path at all.
        var sol   = new PlanarSolveContext(mesh, prt).SolveAt(kernel, f);
        var wave  = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);

        // Route 2: two synthesised standards and a 2×2 trace.
        var g2 = TwoLine(problem, prt[0], kernel, f, 5e-3, 12e-3, out double dl, out double deg);

        double rel  = (wave.Gamma - g2.Gamma).Magnitude / wave.Gamma.Magnitude;
        double relB = Math.Abs(wave.Beta - g2.Beta) / wave.Beta;
        _out.WriteLine($"travelling wave: γ = {wave.Alpha:F4} + j{wave.Beta:F3}   (residual {wave.ResidualRel:E2})");
        _out.WriteLine($"two lines      : γ = {g2.Alpha:F4} + j{g2.Beta:F3}   Δℓ = {dl * 1e3:F3} mm, βΔℓ = {deg:F1}°");
        _out.WriteLine($"|Δγ|/|γ| = {rel:E3}, Δβ/β = {relB:E3}");

        // β is the well-determined half of γ on a low-loss line, so it carries the tight gate; α is
        // two orders of magnitude smaller and is reported rather than gated at the same strength.
        Assert.True(relB < 5e-3, $"β from the two routes differs by {relB:E3}");
        Assert.True(rel  < 1e-2, $"γ from the two routes differs by {rel:E3}");
        Assert.True(g2.Usable, $"βΔℓ = {deg:F1}° is outside TRL's usable interval — pick a better Δℓ");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T2_4_TheTwoRoutesAgreeAcrossTheBand_AndTheBranchUnwrapsCleanly()
    {
        // The fixture is scaled ELECTRICALLY (1.5 λ_g at every frequency), because both γ routes are
        // conditioned on electrical length — see PlanarLineFixtures.LineOfWavelengths for the
        // measurement that forced it.
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        double worst = 0;
        double prevBetaDl = double.NaN, prevF = 0;
        foreach (double f in new[] { 2e9, 6e9, 10e9 })
        {
            var problem     = PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, f);
            var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
            var kernel      = PlanarLineFixtures.Kernel(slab, f);

            // 2–20 GHz is a 10:1 band against an 8:1 usable interval, so SuggestDeltas returns TWO
            // separations and the extraction picks the better one per frequency. That count is
            // DERIVED from the band, not configured — see BandRatioPerSeparation for the measurement.
            var set = PlanarCalibration.BuildSet(prt[0], slab, 2e9, 10e9);
            var sShort = new PlanarSolveContext(set[0].Mesh, set[0].Ports).RawScatteringAt(kernel, f);

            var sLong  = new List<NumFlat.Mat<Complex>>();
            var deltas = new List<double>();
            for (int i = 1; i < set.Length; i++)
            {
                sLong.Add(new PlanarSolveContext(set[i].Mesh, set[i].Ports).RawScatteringAt(kernel, f));
                deltas.Add(set[i].LengthM - set[0].LengthM);
            }

            double expect = double.IsNaN(prevBetaDl)
                ? PlanarCalibration.EstimateBeta(slab, f)
                : prevBetaDl * (f / prevF);
            var g2 = PlanarCalibration.GammaBest(sShort, sLong, deltas, expect, out int pick);

            var sol  = new PlanarSolveContext(mesh, prt).SolveAt(kernel, f);
            var wave = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);

            double relB = Math.Abs(wave.Beta - g2.Beta) / wave.Beta;
            worst = Math.Max(worst, relB);
            _out.WriteLine($"{f / 1e9,5:F1} GHz: N = {mesh.Bases.Count,4}, ℓ₁ = {set[0].LengthM * 1e3:F3} mm, " +
                           $"{set.Length - 1} separation(s) {string.Join(" / ", deltas.Select(d => $"{d * 1e3:F2} mm"))}, " +
                           $"chose #{pick}: βΔℓ = {g2.ElectricalDegrees,6:F1}° {(g2.Usable ? "  " : "!!")} " +
                           $"β(two-line) = {g2.Beta:F2}, β(wave) = {wave.Beta:F2}, Δβ/β = {relB:E2}, " +
                           $"ε_eff = {g2.EffectivePermittivity(f):F4}, unwrapped {g2.Unwrapped}");

            prevBetaDl = g2.Beta; prevF = f;
            Assert.True(g2.Usable, $"βΔℓ = {g2.ElectricalDegrees:F1}° at {f / 1e9:F1} GHz is outside the usable interval");
        }

        Assert.True(worst < 1e-2, $"the two γ routes disagree by up to {worst:E2} across the band");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 3 — A and B agree on a uniform line
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T3_1_BAgreesWithAOnEeff_AtLowFrequency_AndDivergesByDispersionAbove()
    {
        // The phase table's own gate, and the first half of it: kernel A computes a quasi-static
        // ε_eff with no frequency in it; kernel B computes a full-wave one. They must agree where
        // quasi-TEM holds and separate where it does not — and THAT SEPARATION IS A RESULT, not an
        // error. Shipping mesh, because this is a physics number.
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        double eeffA = KernelAEeff(slab, w);
        double u = w / slab.HeightM;
        _out.WriteLine($"kernel A (quasi-static, t = 1 µm): ε_eff = {eeffA:F4}   W/h = {u:F4}");

        var rows = new List<(double F, double Eeff, double Rel, double Kj)>();
        foreach (double f in new[] { 1e9, 2e9, 5e9, 10e9, 20e9 })
        {
            var problem = PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, f);
            var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
            var prt     = PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem));
            var sol     = new PlanarSolveContext(mesh, prt)
                            .SolveAt(PlanarLineFixtures.Kernel(slab, f), f);
            var g       = CurrentWaveOracle.Extract(mesh, sol.Currents[0], prt[0]);

            double eeff = g.EffectivePermittivity(f);
            double kj = KirschningJansen.DispersiveEeff(
                u, slab.Material.EpsR, eeffA, KirschningJansen.NormalizedFreqGhzMm(f, slab.HeightM));

            rows.Add((f, eeff, (eeff - eeffA) / eeffA, kj));
            _out.WriteLine($"{f / 1e9,5:F1} GHz: N = {mesh.Bases.Count,4}, ε_eff(B) = {eeff:F4}, " +
                           $"B/A − 1 = {(eeff - eeffA) / eeffA:+0.00%;-0.00%}, " +
                           $"K-J = {kj:F4} → B/KJ − 1 = {(eeff - kj) / kj:+0.00%;-0.00%}, " +
                           $"residual {g.ResidualRel:E2}");
        }

        // At the bottom of the band the two must agree — B's full-wave answer there IS the
        // quasi-static one, and nothing about dispersion has started yet.
        Assert.True(Math.Abs(rows[0].Rel) < 0.02,
            $"at 1 GHz B and A differ by {rows[0].Rel:P2}, which is not quasi-TEM disagreement");

        // And the divergence must be UPWARD with frequency: microstrip dispersion pulls ε_eff toward
        // εᵣ. A downward drift would mean something is wrong, not that the line is dispersive.
        Assert.True(rows[^1].Eeff > rows[0].Eeff,
            "ε_eff did not rise with frequency — that is not microstrip dispersion");
        Assert.True(rows[^1].Eeff < slab.Material.EpsR, "ε_eff exceeded the substrate's own εᵣ");

        // The corroboration that makes the divergence a RESULT rather than an unexplained drift:
        // B's dispersion has to track the Kirschning-Jansen closed form, which knows nothing about
        // this kernel and is not an input to it anywhere.
        foreach (var (f, eeff, _, kj) in rows)
            Assert.True(Math.Abs(eeff - kj) / kj < 0.08,
                $"at {f / 1e9:F1} GHz B gives {eeff:F4} against K-J's {kj:F4} — that is not dispersion");
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────

    private static PlanarCalibration.GammaResult TwoLine(
        PlanarProblem problem, PlanarPortResolution port, PlanarKernelPair kernel, double f,
        double l1, double l2, out double deltaL, out double degrees,
        double expectedBetaDeltaL = double.NaN)
    {
        int k = PlanarCalibration.EndRunCellsFor(port, problem.Slab);
        var a = PlanarCalibration.BuildLine(port, l1, k);
        var b = PlanarCalibration.BuildLine(port, l2, k);

        var sa = new PlanarSolveContext(a.Mesh, a.Ports).RawScatteringAt(kernel, f);
        var sb = new PlanarSolveContext(b.Mesh, b.Ports).RawScatteringAt(kernel, f);

        deltaL = b.LengthM - a.LengthM;
        var g = PlanarCalibration.Gamma(sa, sb, deltaL, expectedBetaDeltaL);
        degrees = g.ElectricalDegrees;
        return g;
    }

    /// <summary>Kernel A's own ε_eff for the same cross-section — the ORACLE, never an input.</summary>
    private static double KernelAEeff(GroundedSlab slab, double widthM)
    {
        // t = 1 µm, and the choice is MEASURED rather than "as thin as possible". Kernel B's sheet
        // has no thickness at all, so the comparison wants A's thickness model out of the way — but
        // A's edge reference is a fraction of the metal THICKNESS (R-mom-8), so an absurdly thin
        // strip asks its mesher for an absurdly fine edge cell and the default mesh degenerates.
        // Measured against Hammerstad-Jensen's thin-strip 3.3158 on this cross-section:
        //   t = 1 nm   → 3.4652 (+4.5%)   ← degenerate; refining to Refined(2) recovers 3.3188
        //   t = 0.1 µm → 3.3062 (−0.3%)
        //   t = 1 µm   → 3.3169 (+0.03%)  ← used here
        //   t = 35 µm  → 3.2875 (−0.85%)  ← real copper; A's own thickness effect, correctly present
        var problem = EmProblemBuilders.Microstrip(
            w: widthM, h: slab.HeightM, t: 1e-6,
            epsR: slab.Material.EpsR, tanD: slab.Material.TanD);
        var rlgc = RlgcExtractor.Extract(problem, BoundaryMesher.Mesh(problem, EmMeshSettings.Default));
        return rlgc.Eeff;
    }
}
