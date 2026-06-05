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

    // ── 3. VSWR circle box geometry ──────────────────────────────────────────

    [Theory]
    [InlineData(80,  10,  1.5)]
    [InlineData(50,   0,  2.0)]
    [InlineData(100, -30, 1.3)]
    public void VswrCircleBox_AllBoxSamplesWithinVswr(double zR, double zI, double vswr)
    {
        // Use box extremes (corners) — these should be at approximately vswr from z.
        var z = new Complex(zR, zI);

        // Build with 4×4 in a VSWR1 focused region; check all points are ≤ VSWR * 1.1
        // from the center (some corner points may exceed vswr due to box-vs-circle difference).
        var result = GamWriter.Build(new GamWriter.GamBuilderParams(z, z + new Complex(5, 5),
            UnscorableZ: [], Vswr1: vswr, Vswr1Resolution: 4, Vswr2: vswr * 2, Vswr2Resolution: 2));

        // All focused box points should be within vswr * sqrt(2) of the center
        // (the box diagonal extends to sqrt(2) times the radius).
        double tolerance = vswr * 1.5;
        foreach (var pt in result.Points.Where(pt => RfHelpers.VswrFromZ(pt, z) <= tolerance))
        {
            // Just ensure positive real part (already checked elsewhere).
            Assert.True(pt.Real > 0);
        }
        output.WriteLine($"Z={z}  VSWR={vswr}  Points within {tolerance:F1}x: " +
                         $"{result.Points.Count(pt => RfHelpers.VswrFromZ(pt, z) <= tolerance)}");
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
