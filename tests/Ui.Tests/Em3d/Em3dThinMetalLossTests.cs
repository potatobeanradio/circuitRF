using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>A conductor thin against the skin depth reads too little loss on a solver that models its faces as thick metal.</summary>
public class Em3dThinMetalLossTests
{
    private const double Um = 1e-6;

    private static Em3dProblem Line(double widthUm, double thickUm, double sigma, double startHz)
    {
        var (w, t) = (widthUm * Um, thickUm * Um);
        var a = Em3dBoundaryKind.Pec;
        Point2[] outline = [new(0, 0), new(1000 * Um, 0), new(1000 * Um, w), new(0, w)];
        return new Em3dProblem(
            [new Em3dSolid("strip", "metal", Em3dRole.Conductor, new Em3dExtrudedPolygon(outline, [], 0, t), 0)], [],
            [new Em3dMaterial("metal", 1, null, 0, 1, sigma)], [],
            new Em3dAirBox(new Point3(-1e-3, -1e-3, -1e-3), new Point3(2e-3, 1e-3, 1e-3), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(startHz, 40e9, 5, Em3dSweepKind.Linear), 25);
    }

    [Fact]
    public void Slab_ReadsLowByTheDcRatioWhenThin_AndNotAtAllWhenThick()
    {
        // t = δ/10: the thick-metal resistance 1/(2σδ) per square against the true 1/(σt), so 2δ/t = 20.
        Assert.Equal(20, Em3dThinMetalLoss.ReadsLowBy(0.1), 1);
        Assert.Equal(1, Em3dThinMetalLoss.ReadsLowBy(20), 3);
    }

    [Fact]
    public void ANarrowThinLine_IsFound_WithItsOwnSection()
    {
        // 1 µm × 5 µm: 2A/P = 5/6 µm, not the 1 µm a wide strip of that thickness would be.
        var found = Assert.Single(Em3dThinMetalLoss.Find(Line(5, 1, 3.77e7, 1e9)));
        Assert.Equal(5.0 / 6 * Um, found.ThicknessM, 9);
        Assert.InRange(found.ReadsLowBy, 5.5, 6.5);
    }

    [Fact]
    public void ThickMetal_OrAPerfectConductor_HasNothingToSay()
    {
        Assert.Empty(Em3dThinMetalLoss.Find(Line(100, 35, 5.8e7, 1e9)));
        Assert.Empty(Em3dThinMetalLoss.Find(Line(5, 1, double.PositiveInfinity, 1e9)));
    }

    [Fact]
    public void ARunWarnsOnPalaceAndEmerge_ButNotOnOpenEmsAlone()
    {
        var problem = Line(5, 1, 3.77e7, 1e9);
        Assert.Contains("'strip'", Assert.Single(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.Palace })));
        Assert.Single(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.Emerge }));
        Assert.Empty(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.OpenEms }));
    }
}
