// brief-em3d-139 §3 — one run per solver against a 2D filament solve of the same section (testdata/em3d/thin-metal/, which
// references nothing in this repo). A real mesh and a real solve, so the Benchmark tier.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

[Trait("Category", "Benchmark")]
public sealed class ThinMetalRunTests : IDisposable
{
    private const double Um = 1e-6;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-thin-" + Guid.NewGuid().ToString("N")[..10]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>A 300 µm line of 1 µm aluminium, 10 µm over a perfect floor, in a perfect box, with a lumped port at each end.</summary>
    private static Em3dProblem FiveMicronLine()
    {
        const double W = 5 * Um, L = 300 * Um, H = 10 * Um;
        var pec = Em3dBoundaryKind.Pec;
        Point2[] outline = [new(0, 0), new(L, 0), new(L, W), new(0, W)];
        Em3dPort Port(int n, double x, double inward) => new(n, $"P{n}", "strip", "airbox/zmin", new Point3(x, 0, 0), new Point3(x, W, H),
            new Point3(0, 0, 1), 50, new Em3dReferencePlane(new Point3(x, W / 2, H / 2), new Point3(inward, 0, 0), 0));
        return new Em3dProblem(
            [new Em3dSolid("strip", "aluminium", Em3dRole.Conductor, new Em3dExtrudedPolygon(outline, [], H, H + Um), 0)], [],
            [new Em3dMaterial("aluminium", 1, null, 0, 1, 3.77e7)], [Port(1, 0, 1), Port(2, L, -1)],
            new Em3dAirBox(new Point3(-20 * Um, -60 * Um, 0), new Point3(L + 20 * Um, W + 60 * Um, H + Um + 60 * Um), new Em3dFaces(pec, pec, pec, pec, pec, pec)),
            new Em3dFrequency(0.1e9, 40e9, 5, Em3dSweepKind.Log), 20);
    }

    private void AgainstTheReference(EmSetup setup)
    {
        var problem = FiveMicronLine();
        var r = Em3dRunService.Run(setup, _ => new Em3dGenerationResult(problem, null, []), _root, CancellationToken.None, null, null);
        Assert.True(problem.ThinMetalCorrection);
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
        var s = r.Data!.Cubes["S"];
        var freq = s.Axes[0].Values;
        Complex At(int k, int i, int j) => s.ComplexValues[k * 4 + i * 2 + j];

        string[] rows = File.ReadAllLines(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "thin-metal", "reference.csv"));
        var reference = rows.Skip(1).Select(l => l.Split(',')).Where(c => c[0] == "5")
                            .Select(c => (R: double.Parse(c[2], CultureInfo.InvariantCulture), L: double.Parse(c[3], CultureInfo.InvariantCulture))).ToList();
        Assert.Equal(reference.Count, freq.Length);
        for (int k = 0; k < freq.Length; k++)
        {
            // −1/Y21, with Y from S on 50 Ω.
            Complex det = (1 + At(k, 0, 0)) * (1 + At(k, 1, 1)) - At(k, 0, 1) * At(k, 1, 0);
            Complex y21 = -2 * At(k, 1, 0) / det / 50;
            Complex z = -1 / y21;
            double rPrime = z.Real / 0.3, lPrime = z.Imaginary / (2 * Math.PI * freq[k]) / 300e-6 * 1e6;
            bool top = freq[k] > 10e9;
            Assert.InRange(rPrime / reference[k].R, top ? 0.75 : 0.97, top ? 1.25 : 1.03);
            if (!top) Assert.InRange(lPrime / reference[k].L, 0.97, 1.03);
        }
    }

    [PalaceFact]
    public void Palace_ReadsTheFiveMicronLine() => AgainstTheReference(new EmSetup
    {
        Name = "S", Solver3D = Em3dSolverId.Palace,
        // The preset's mesh sizes itself from the wavelength, far coarser than this line needs for L': on EMerge it read 0.69 of the
        // reference there and 0.99 here, with R' correct on both.
        Palace = new CemPalace { Quality = PalaceQuality.Standard, MaxElementWavelengths = 0.02, EdgeRefinement = 0.02, AdaptiveMaxIterations = 0, SweepAdaptiveTol = 0 },
    });

    [EmergeFact]
    public void Emerge_ReadsTheFiveMicronLine() => AgainstTheReference(new EmSetup
    {
        Name = "S", Solver3D = Em3dSolverId.Emerge,
        Emerge = new CemEmerge { Quality = PalaceQuality.Standard, MaxElementWavelengths = 0.02, EdgeRefinement = 0.02, AdaptiveSweep = false, AdaptiveMesh = 0 },
    });
}
