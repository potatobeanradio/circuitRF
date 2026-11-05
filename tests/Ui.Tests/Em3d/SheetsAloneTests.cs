using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>A problem of sheets with no solid at all has an air box with nothing to cut from it, and that still has to mesh.</summary>
public class SheetsAloneTests
{
    private const double Um = 1e-6;

    private static Em3dPort Port(int n, double x)
        => new(n, $"P{n}", "strip", "airbox/zmin", new(x, 0, 0), new(x, 50 * Um, 10 * Um), new(0, 0, 1), 50, new Em3dReferencePlane(new(x, 0, 0), new(1, 0, 0), 0));

    [GmshFact]
    public void AProblemOfSheetsAlone_Meshes()
    {
        var a = Em3dBoundaryKind.Pec;
        Point2[] outline = [new(0, 0), new(300 * Um, 0), new(300 * Um, 50 * Um), new(0, 50 * Um)];
        var problem = new Em3dProblem(
            [], [new Em3dSheet("strip", "metal", outline, [], 10 * Um, 1 * Um, 0)],
            [new Em3dMaterial("metal", 1, null, 0, 1, 3.77e7)], [Port(1, 0), Port(2, 300 * Um)],
            new Em3dAirBox(new Point3(-20 * Um, -60 * Um, 0), new Point3(320 * Um, 110 * Um, 70 * Um), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(1e9, 40e9, 5, Em3dSweepKind.Linear), 25);
        var low = GmshGeoWriter.Write(problem, PalaceSettings.Default);
        Assert.True(low.Ok, low.Refusal);

        string dir = Path.Combine(Path.GetTempPath(), "crf-sheets-alone-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;
            var step = PalaceRun.Mesh(dir, low, gmsh, null, default);
            Assert.True(step.Ok, step.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
