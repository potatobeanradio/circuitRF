// EMB-4 R-emb4-1 — the Palace element estimate counts outline complexity.
//
// An imported zone outline carries many edges shorter than the element the script asks for at metal; Gmsh meshes each
// one as at least one segment, so every short edge seeds a cluster of small surface triangles and tetrahedra that the
// region volumes and the refined shells never priced. The fixture is the shipped microstrip with a copper pour beside
// it whose top edge is a sampled sinusoid — 800 edges of about 16 µm against a 313 µm conductor element, the scale of
// an imported zone's arcs — so the short edges are the only thing about it the old estimate missed.

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class PalaceOutlineEstimateTests(ITestOutputHelper output)
{
    /// <summary>
    /// Gmsh's tetrahedron count for <see cref="ScallopedPour"/> under its own script (GmshGeoWriter, the shipped Palace
    /// defaults; Gmsh 4.15.2 on macOS arm64, first-order mesh — the count does not depend on the element order). Run once
    /// and recorded: a test that ran Gmsh would be an EM run in the routine gate. The same pour with a straight edge
    /// made 20,779, against an estimate of 19,506.
    /// </summary>
    private const long RecordedGmshTetrahedra = 31_530;

    /// <summary>The shipped microstrip with an 8 × 3 mm pour beside it, its top edge a sinusoid of 0.3 mm amplitude and a
    /// 1 mm period sampled at <paramref name="edges"/> points (800: about 16 µm an edge).</summary>
    internal static (EmSetup Setup, EmLayoutSource Source) ScallopedPour(int edges = 800, double amplitudeMm = 0.3)
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        // Recorded with robust meshing OFF: it removes near-collinear points from the sampled sinusoid, which is the
        // short-edge population the outline term prices. Robust meshing now defaults on (EMB-2 R-emb2-3), so it is
        // stated here to keep the fixture the one Gmsh was run on.
        setup.RobustMeshing = false;
        long Dbu(double mm) => (long)Math.Round(mm * 1e3 * source.View.DbuPerMicron);
        var xy = new List<long> { Dbu(1), Dbu(1.5), Dbu(9), Dbu(1.5) };
        for (int k = edges; k >= 0; k--)
        {
            double x = 1 + 8.0 * k / edges;
            xy.Add(Dbu(x));
            xy.Add(Dbu(4.5 + amplitudeMm * Math.Sin(2 * Math.PI * (x - 1))));
        }
        source.View.Shapes.Add(new PolygonShape { Layer = new LayerKey(1, 0), Xy = [.. xy] });
        setup.AirBox = PalaceBackendTests.Padded(2000);
        return (setup, source);
    }

    [Fact]
    public void ManyShortEdges_EstimateIsWithinOneAndAHalfOfGmsh_AndSaysSo()
    {
        var (setup, source) = ScallopedPour();
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(generated.Ok, generated.Refusal);
        var settings = PalaceSettings.Resolve(setup.Palace);
        var estimate = Em3dRunService.EstimatePalace(generated.Problem!, settings)!;
        double ratio = (double)estimate.Tetrahedra / RecordedGmshTetrahedra;
        output.WriteLine($"estimate {estimate.Tetrahedra:N0} against Gmsh's {RecordedGmshTetrahedra:N0} ({ratio:0.00}×); " +
                         $"outline term {estimate.OutlineTetrahedra:N0}");

        Assert.InRange(ratio, 1 / 1.5, 1.5);
        // The outline term is what brings it there: without it the estimate is more than 1.5× low.
        Assert.True((double)(estimate.Tetrahedra - estimate.OutlineTetrahedra) / RecordedGmshTetrahedra < 1 / 1.5);
        Assert.True(estimate.OutlineDominates);
    }

    /// <summary>After meshing, the run states Gmsh's count against the estimate and the memory at Gmsh's count for the
    /// linear solver it actually uses.</summary>
    [Fact]
    public void AfterMeshing_TheNoteGivesGmshsCount_AndTheMemoryForTheChosenSolver()
    {
        var before = new Em3dPalaceEstimate(19_550, 0, 2, null, []);
        string direct = Em3dRunService.MeshedAgainstEstimate(
            RecordedGmshTetrahedra, before, PalaceSettings.Default with { LinearSolver = PalaceLinearSolver.Direct })!;
        string iterative = Em3dRunService.MeshedAgainstEstimate(RecordedGmshTetrahedra, before, PalaceSettings.Default)!;
        output.WriteLine(direct);
        Assert.StartsWith("Gmsh made 31,530 tetrahedra against the 19,550 estimated before meshing (1.6×)", direct,
                          StringComparison.Ordinal);
        Assert.Contains("on the direct linear solver", direct, StringComparison.Ordinal);
        Assert.Contains("on the iterative linear solver", iterative, StringComparison.Ordinal);
        Assert.NotEqual(direct.Replace("direct", ""), iterative.Replace("iterative", ""));   // the figure differs too
    }
}
