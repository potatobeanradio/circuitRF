using System.Numerics;
using CircuitRF.Engine.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Step 2 unit tests for GamReader (loadpull.md §2.2, Phase4b1_Brief.md Step 2).
/// </summary>
public class GamReaderTests(ITestOutputHelper output)
{
    private static void AssertNear(double expected, double actual, double tol, string label = "")
        => Assert.InRange(actual, expected - tol, expected + tol);

    // ── Test 1: mag_ang with header ───────────────────────────────────────────

    [Fact]
    public void GamReader_MagAngHeader_ParsesCorrectly()
    {
        var text = "# gamma Z0=50 mag_ang\n0.50  30\n0.00   0\n0.80  90\n";
        var grid = GamReader.ReadText(text);

        Assert.Equal(3, grid.Points.Count);
        AssertNear(50.0, grid.Z0, 1e-9, "Z0");

        // Point 0: |Γ|=0.5, ∠30°
        var g0 = grid.Points[0].Gamma;
        AssertNear(0.5 * Math.Cos(30 * Math.PI / 180), g0.Real,      1e-9, "Γ0.re");
        AssertNear(0.5 * Math.Sin(30 * Math.PI / 180), g0.Imaginary, 1e-9, "Γ0.im");

        // Point 1: Γ=0 → Z=Z0=50Ω
        AssertNear(0.0,  grid.Points[1].Gamma.Magnitude, 1e-9, "|Γ1|");
        AssertNear(50.0, grid.Points[1].Z.Real,           1e-6, "Z1.re");

        // Point 2: |Γ|=0.8, ∠90°
        var z2 = GamReader.GammaToZ(Complex.FromPolarCoordinates(0.8, Math.PI / 2), 50);
        AssertNear(z2.Real,      grid.Points[2].Z.Real,      1e-6, "Z2.re");
        AssertNear(z2.Imaginary, grid.Points[2].Z.Imaginary, 1e-6, "Z2.im");

        output.WriteLine($"Point 0: Γ={g0:F4}  Z={grid.Points[0].Z:F3}");
        output.WriteLine($"Point 2: Γ={grid.Points[2].Gamma:F4}  Z={grid.Points[2].Z:F3}");
    }

    // ── Test 2: header-less re imag (two-column) ──────────────────────────────

    [Fact]
    public void GamReader_HeaderlessReImTwoCol_InferredCorrectly()
    {
        // No header → default form = impedance; format inferred = re imag (no 'j').
        var text = "50.0  0.0\n80.0  10.0\n25.0  -5.0\n";
        var grid = GamReader.ReadText(text);
        Assert.Equal(3, grid.Points.Count);

        AssertNear(50.0, grid.Points[0].Z.Real,           1e-9, "Z0.re");
        AssertNear( 0.0, grid.Points[0].Z.Imaginary,      1e-9, "Z0.im");
        AssertNear( 0.0, grid.Points[0].Gamma.Magnitude,  1e-9, "|Γ0|");  // Z=Z0 → Γ=0

        AssertNear(80.0, grid.Points[1].Z.Real,      1e-9, "Z1.re");
        AssertNear(10.0, grid.Points[1].Z.Imaginary, 1e-9, "Z1.im");

        var g1 = GamReader.ZToGamma(new Complex(80, 10), 50);
        AssertNear(g1.Real,      grid.Points[1].Gamma.Real,      1e-9, "Γ1.re");
        AssertNear(g1.Imaginary, grid.Points[1].Gamma.Imaginary, 1e-9, "Γ1.im");

        output.WriteLine($"Z[1]={grid.Points[1].Z:F3}  Γ[1]={grid.Points[1].Gamma:F4}");
    }

    // ── Test 3: header-less re+j*imag literal ────────────────────────────────

    [Fact]
    public void GamReader_HeaderlessReJImag_InferredCorrectly()
    {
        // Values contain 'j' → inferred re+j*imag. Default form = impedance.
        var text = "80+j*10\n25-j*5\n";
        var grid = GamReader.ReadText(text);
        Assert.Equal(2, grid.Points.Count);

        AssertNear(80.0, grid.Points[0].Z.Real,      1e-9, "Z0.re");
        AssertNear(10.0, grid.Points[0].Z.Imaginary, 1e-9, "Z0.im");
        AssertNear(25.0, grid.Points[1].Z.Real,      1e-9, "Z1.re");
        AssertNear(-5.0, grid.Points[1].Z.Imaginary, 1e-9, "Z1.im");

        output.WriteLine($"Z[0]={grid.Points[0].Z:F3}  Z[1]={grid.Points[1].Z:F3}");
    }

    // ── Test 4: impedance form header ─────────────────────────────────────────

    [Fact]
    public void GamReader_ImpedanceHeader_ConvertsToGamma()
    {
        var text = "# impedance Z0=50\n50  0\n150  0\n";
        var grid = GamReader.ReadText(text);
        Assert.Equal(2, grid.Points.Count);

        AssertNear(0.0, grid.Points[0].Gamma.Magnitude, 1e-9, "|Γ0|");  // Z=Z0 → Γ=0
        AssertNear(0.5, grid.Points[1].Gamma.Real,      1e-9, "Γ1.re"); // (150-50)/(150+50)=0.5
        AssertNear(0.0, grid.Points[1].Gamma.Imaginary, 1e-9, "Γ1.im");

        output.WriteLine($"Z=150 → Γ={grid.Points[1].Gamma:F4}  (expected 0.5+j0)");
    }

    // ── Test 5: Γ↔Z roundtrip ────────────────────────────────────────────────

    [Fact]
    public void GamReader_GammaZRoundtrip_IsExact()
    {
        var gammas = new[]
        {
            new Complex(0.3,  0.4),
            new Complex(-0.5, 0.2),
            Complex.Zero,
            new Complex(0.0,  0.8),
        };
        foreach (var gamma in gammas)
        {
            var z    = GamReader.GammaToZ(gamma, 50);
            var back = GamReader.ZToGamma(z, 50);
            AssertNear(gamma.Real,      back.Real,      1e-10, $"Γ={gamma}.re roundtrip");
            AssertNear(gamma.Imaginary, back.Imaginary, 1e-10, $"Γ={gamma}.im roundtrip");
            output.WriteLine($"Γ={gamma:F4} → Z={z:F3} → Γ={back:F4} ✓");
        }
    }

    // ── Test 6: comments and blank lines skipped ──────────────────────────────

    [Fact]
    public void GamReader_CommentsAndBlanks_Skipped()
    {
        var text = "# gamma Z0=50 mag_ang\n; comment line\n0.50  0\n\n; another\n0.30  90\n";
        var grid = GamReader.ReadText(text);
        Assert.Equal(2, grid.Points.Count);
        output.WriteLine($"After skipping: {grid.Points.Count} points");
    }

    // ── Test 7: Hero 3 .gam file ─────────────────────────────────────────────

    [Fact]
    public void GamReader_Hero3GamFile_Loads21Points()
    {
        var dir  = FindTestDataDir("Hero3");
        var path = Path.Combine(dir, "hero3_load.gam");
        var grid = GamReader.ReadFile(path);

        Assert.Equal(20, grid.Points.Count);
        AssertNear(50.0, grid.Z0, 1e-9, "Z0");

        foreach (var pt in grid.Points)
            Assert.True(pt.Gamma.Magnitude < 1.0 + 1e-9,
                $"Γ={pt.Gamma:F4} (line {pt.LineNumber}) has |Γ|≥1 (non-passive)");

        output.WriteLine($"Hero 3 grid: {grid.Points.Count} points, all passive ✓");
        foreach (var pt in grid.Points)
            output.WriteLine($"  Γ={pt.Gamma.Real:F4}{(pt.Gamma.Imaginary >= 0 ? "+" : "")}" +
                             $"{pt.Gamma.Imaginary:F4}j  Z={pt.Z.Real:F2}{(pt.Z.Imaginary >= 0 ? "+" : "")}{pt.Z.Imaginary:F2}j Ω");
    }

    private static string FindTestDataDir(string hero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", hero);
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"testdata/{hero} not found");
    }
}
