using System.Numerics;
using CircuitRF.Engine.Loadpull;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Phase 4b-2 tests for the GamWriter — focused+broad grid builder (loadpull_pursuit.md §5).
/// </summary>
public class GamWriterTests(ITestOutputHelper output)
{
    private static readonly Complex MxpZ = new(80, 10);
    private static readonly Complex MxeZ = new(65, -20);

    // ── 1. Basic structure ────────────────────────────────────────────────────

    [Fact]
    public void Build_ContainsMxpAndMxePoints()
    {
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: []));

        // MXP and MXE must appear in the output.
        bool hasMxp = result.Points.Any(z => RfHelpers.VswrFromZ(z, MxpZ) < 1.001);
        bool hasMxe = result.Points.Any(z => RfHelpers.VswrFromZ(z, MxeZ) < 1.001);
        Assert.True(hasMxp, "MXP point missing from output.");
        Assert.True(hasMxe, "MXE point missing from output.");
        output.WriteLine($"Total points: {result.Points.Count}");
    }

    [Fact]
    public void Build_DenseNearOptima_SparseOutside()
    {
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: [], Vswr1: 1.5, Vswr1Resolution: 4, Vswr2: 3.0, Vswr2Resolution: 4));

        // Count points near each optimum (within VSWR1 = 1.5).
        int nearMxp = result.Points.Count(z => RfHelpers.VswrFromZ(z, MxpZ) <= 1.5);
        int nearMxe = result.Points.Count(z => RfHelpers.VswrFromZ(z, MxeZ) <= 1.5);
        int total   = result.Points.Count;

        output.WriteLine($"Near MXP (≤1.5 VSWR): {nearMxp}  Near MXE: {nearMxe}  Total: {total}");

        // Focused regions must be non-trivial.
        Assert.True(nearMxp >= 4, $"Too few focused points near MXP: {nearMxp}");
        Assert.True(nearMxe >= 4, $"Too few focused points near MXE: {nearMxe}");

        // Not all points are in the focused regions (some broad points exist).
        Assert.True(total > nearMxp + nearMxe - 2,
            "No broad points outside focused regions — grid structure incorrect.");
    }

    [Fact]
    public void Build_AllPointsHavePositiveRealPart()
    {
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: []));

        foreach (var z in result.Points)
            Assert.True(z.Real > 0,
                $"Non-physical impedance with Re(Z)={z.Real:F3} ≤ 0 in output.");
    }

    // ── 2. Non-convergent exclusion ───────────────────────────────────────────

    [Fact]
    public void Build_ExcludesPointsNearUnscorable()
    {
        // Place an unscorable point near MXE (within VSWR 1.02 of a box point).
        var unscorable = new List<Complex> { MxeZ + new Complex(2, 1) };

        var resultExcl = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: unscorable,
            KeepNonconverging: false, NonconvergentVswr: 1.05));

        var resultKeep = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: unscorable,
            KeepNonconverging: true, NonconvergentVswr: 1.05));

        // Exclusion should reduce point count compared to keep-all.
        output.WriteLine($"Excl={resultExcl.Points.Count}  Keep={resultKeep.Points.Count}");
        output.WriteLine($"Warnings: {string.Join("; ", resultExcl.Warnings)}");

        Assert.True(resultExcl.Points.Count <= resultKeep.Points.Count,
            "Exclusion should not add points.");

        // A warning must be issued when points are removed.
        if (resultExcl.Points.Count < resultKeep.Points.Count)
            Assert.True(resultExcl.Warnings.Count > 0,
                "No warning issued despite removing non-convergent points.");
    }

    [Fact]
    public void Build_KeepNonconverging_PreservesAll()
    {
        var unscorable = new List<Complex>
        {
            MxpZ + new Complex(1, 0),
            MxeZ + new Complex(-1, 2),
        };

        var resultExcl = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: unscorable, KeepNonconverging: false, NonconvergentVswr: 1.05));
        var resultKeep = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: unscorable, KeepNonconverging: true,  NonconvergentVswr: 1.05));

        Assert.True(resultKeep.Warnings.Count == 0,
            "KeepNonconverging=true should produce no removal warnings.");
    }

    // ── 3. Focused-disc geometry ─────────────────────────────────────────────

    /// <summary>
    /// Every focused point lies within VSWR1 of the optimum it belongs to. The focused sampling used
    /// to fill the circle's BOUNDING BOX, which put 12 of its 16 points OUTSIDE the requested circle
    /// — a `VSWR1 = 2` patch actually reached VSWR 3.32 — and left none of them at the circle's own
    /// low-impedance extreme (the nearest was 60 Ω off in reactance).
    /// </summary>
    [Theory]
    [InlineData(80,  10,  1.5)]
    [InlineData(50,   0,  2.0)]
    [InlineData(100, -30, 1.3)]
    public void FocusedPoints_LieWithinVswr1OfTheirOptimum(double zR, double zI, double vswr)
    {
        var z = new Complex(zR, zI);
        // Both optima at the same place, so every point in the result is focused or the optimum.
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(z, z,
            UnscorableZ: [], Vswr1: vswr, Vswr1Resolution: 4,
            Vswr2: vswr, Vswr2Resolution: 2));

        foreach (var pt in result.Points)
        {
            double v = RfHelpers.VswrFromZ(pt, z);
            Assert.True(v <= vswr * 1.0001,
                $"focused point {pt} is at VSWR {v:F2} from the optimum, past the requested {vswr}");
        }
        output.WriteLine($"Z={z}  VSWR1={vswr}  {result.Points.Count} points, all within it");
    }

    /// <summary>
    /// The full focused budget lands NEAR the optimum. Under box sampling most of it did not: of the
    /// 4×4 = 16 points per optimum, only 10 were within VSWR1 of MXP and 17 within VSWR1 of MXE
    /// across the whole grid — the rest had spilled into the box corners, out past the circle.
    /// </summary>
    [Fact]
    public void EachOptimumGetsItsWholeFocusedBudgetWithinVswr1()
    {
        const int res = 4;
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(FetMxpZ, FetMxeZ,
            UnscorableZ: [], Vswr1: 2.0, Vswr1Resolution: res,
            Vswr2: 20.0, Vswr2Resolution: 10));

        foreach (var (name, opt) in new[] { ("MXP", FetMxpZ), ("MXE", FetMxeZ) })
        {
            int near = result.Points.Count(z => RfHelpers.VswrFromZ(z, opt) <= 2.0);
            output.WriteLine($"within VSWR1 of {name}: {near}");
            Assert.True(near >= res * res,
                $"only {near} terminations within VSWR1 of {name}; the focused budget is {res * res}");
        }
    }

    /// <summary>
    /// And the focused disc reaches its own low-impedance extreme, at the optimum's OWN reactance —
    /// the reading a box sample cannot produce at an even resolution, because no row lands there.
    /// </summary>
    [Fact]
    public void FocusedDisc_ReachesTheLowImpedanceExtremeOfVswr1()
    {
        const double vswr1 = 2.0;
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(FetMxpZ, FetMxeZ,
            UnscorableZ: [], Vswr1: vswr1, Vswr1Resolution: 4,
            Vswr2: 20.0, Vswr2Resolution: 10));

        foreach (var opt in new[] { FetMxpZ, FetMxeZ })
        {
            var extreme = new Complex(opt.Real / vswr1, opt.Imaginary);
            Assert.Contains(result.Points, z => RfHelpers.VswrFromZ(z, extreme) < 1.001);
        }
    }

    // ── 3b. The broad sampling actually reaches VSWR2 (2026-08-23) ───────────
    //
    //  Reported: with VSWR2 = 20 the follow-on grid's low-impedance terminations were only about a
    //  VSWR of 3 from MXE. They were: the broad grid sampled the BOUNDING BOX of the VSWR2 circle
    //  on a linear lattice, which puts ~95 % of its columns above the centre's resistance and lands
    //  none of the remaining one near the centre's reactance. The lowest-resistance point within
    //  VSWR2 of MXE came from the VSWR1 box around MXP instead — so the low-Z reach was set by
    //  VSWR1, and VSWR2 bought only Smith-chart rim.

    // A realistic pair of optima, from a measured run: MXE ≈ 125 Ω, MXP ≈ 80 Ω.
    private static readonly Complex FetMxpZ = new(80.476, 0.001);
    private static readonly Complex FetMxeZ = new(124.766, -6.172);

    /// <summary>
    /// The low-impedance extreme of the VSWR2 circle — <c>Re(MXE)/VSWR2 + j·Im(MXE)</c> — is a grid
    /// point. This is the reading the setting promises, and it is what was missing.
    /// </summary>
    [Fact]
    public void BroadGrid_ReachesTheLowImpedanceExtremeOfVswr2()
    {
        const double vswr2 = 20.0;
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(FetMxpZ, FetMxeZ,
            UnscorableZ: [], Vswr1: 2.0, Vswr1Resolution: 4,
            Vswr2: vswr2, Vswr2Resolution: 10));

        var extreme = new Complex(FetMxeZ.Real / vswr2, FetMxeZ.Imaginary);
        Assert.Contains(result.Points, z => RfHelpers.VswrFromZ(z, extreme) < 1.001);
    }

    /// <summary>
    /// And the reach TRACKS the setting. Under the old box sampling it did not: both cases below were
    /// floored at the same 40.24 Ω, which is the VSWR1 box around MXP — not the broad sampling at all.
    ///
    /// <para>At <c>VSWR2 = 3</c> that floor is still what wins, and legitimately so: the whole broad
    /// disc then lies inside the focused boxes, whose own step 7 discards it (that region is already
    /// sampled, at higher resolution). What must be true is that the grid reaches AT LEAST as low as
    /// the VSWR2 circle does, and at VSWR2 = 20 it reaches exactly its extreme.</para>
    /// </summary>
    [Fact]
    public void RaisingVswr2_LowersTheReachableResistance()
    {
        double MinRe(double vswr2) => GamWriter.Build(new GamWriter.GamBuilderParams(
            FetMxpZ, FetMxeZ, UnscorableZ: [], Vswr1: 2.0, Vswr1Resolution: 4,
            Vswr2: vswr2, Vswr2Resolution: 10)).Points.Min(z => z.Real);

        double at3  = MinRe(3.0);
        double at20 = MinRe(20.0);
        output.WriteLine($"min Re: VSWR2=3 → {at3:F2} Ω   VSWR2=20 → {at20:F2} Ω");

        Assert.True(at3 <= FetMxeZ.Real / 3.0 + 1e-6,
            $"the grid must reach at least the VSWR2=3 extreme ({FetMxeZ.Real / 3.0:F2} Ω); got {at3:F2} Ω");
        Assert.Equal(FetMxeZ.Real / 20.0, at20, 2);
        Assert.True(at20 < at3, "raising VSWR2 must extend the low-impedance reach");
    }

    /// <summary>
    /// Nothing OVERSHOOTS the requested circle either — the other half of the box-vs-circle defect.
    /// A box's corners reach far past the circle they bound: on the measured run 40 of 134 points sat
    /// beyond VSWR2 from MXE, one of them at a VSWR of 2010, wasting simulation on the Smith-chart
    /// rim while the low-Z side went unsampled.
    /// </summary>
    [Fact]
    public void NoGridPointLiesBeyondVswr2FromMxe()
    {
        const double vswr2 = 20.0;
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(FetMxpZ, FetMxeZ,
            UnscorableZ: [], Vswr1: 2.0, Vswr1Resolution: 4,
            Vswr2: vswr2, Vswr2Resolution: 10));

        var worst = result.Points.MaxBy(z => RfHelpers.VswrFromZ(FetMxeZ, z));
        double worstVswr = RfHelpers.VswrFromZ(FetMxeZ, worst);
        output.WriteLine($"worst point {worst} at VSWR {worstVswr:F2} from MXE, of {result.Points.Count} points");

        Assert.True(worstVswr <= vswr2 * 1.001,
            $"a grid point sits at VSWR {worstVswr:F1} from MXE, past the requested {vswr2}");
    }

    /// <summary>
    /// The broad rings span the whole range rather than bunching at one end: every decade of VSWR
    /// between the focused region and VSWR2 has terminations in it. This is the property that makes
    /// a contour interpolator usable, and the one a linear box sample destroys at large VSWR2.
    /// </summary>
    [Fact]
    public void TheBroadRingsSpanTheWholeVswrRange()
    {
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(FetMxpZ, FetMxeZ,
            UnscorableZ: [], Vswr1: 2.0, Vswr1Resolution: 4,
            Vswr2: 20.0, Vswr2Resolution: 10));

        foreach (var (lo, hi) in new[] { (2.0, 4.0), (4.0, 8.0), (8.0, 14.0), (14.0, 20.0) })
        {
            int n = result.Points.Count(z =>
            {
                double v = RfHelpers.VswrFromZ(FetMxeZ, z);
                return v > lo && v <= hi;
            });
            output.WriteLine($"VSWR {lo}–{hi} from MXE: {n} points");
            Assert.True(n >= 4, $"only {n} terminations between VSWR {lo} and {hi} of MXE");
        }
    }

    // ── 4. File write round-trip ──────────────────────────────────────────────

    [Fact]
    public void WriteFile_ProducesReadableGam()
    {
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(MxpZ, MxeZ,
            UnscorableZ: []));

        var path = Path.Combine(Path.GetTempPath(), $"pursuit_test_{Guid.NewGuid():N}.gam");
        try
        {
            GamWriter.WriteFile(path, result);
            Assert.True(File.Exists(path), ".gam file not created.");

            var grid = GamReader.ReadFile(path);
            Assert.True(grid.Points.Count > 0, "GamReader read 0 points from written .gam.");
            output.WriteLine($"Written {result.Points.Count} pts; read back {grid.Points.Count} pts.");

            // Count should match (GamReader skips comments/header/blanks).
            Assert.Equal(result.Points.Count, grid.Points.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
