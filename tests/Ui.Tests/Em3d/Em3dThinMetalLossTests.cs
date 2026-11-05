using System.Text.Json;
using CircuitRF.Design.Em3d;
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
    public void WithTheCorrectionOff_ARunWarnsOnPalaceAndEmerge_ButNotOnOpenEmsAlone()
    {
        var problem = Line(5, 1, 3.77e7, 1e9) with { ThinMetalCorrection = false };
        Assert.Contains("is off", Assert.Single(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.Palace })));
        Assert.Single(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.Emerge }));
        Assert.Empty(Em3dThinMetalLoss.ForRun(problem, new EmSetup { Solver3D = Em3dSolverId.OpenEms }));
        Assert.Empty(Em3dThinMetalLoss.NotesForRun(problem, new EmSetup { Solver3D = Em3dSolverId.Palace }));
    }

    [Fact]
    public void ACorrectedConductor_GetsANote_AndNoWarning()
    {
        var problem = Line(5, 1, 3.77e7, 1e9);
        var setup = new EmSetup { Solver3D = Em3dSolverId.Palace };
        Assert.Empty(Em3dThinMetalLoss.ForRun(problem, setup));
        Assert.Contains("0.833 µm on 'strip'", Assert.Single(Em3dThinMetalLoss.NotesForRun(problem, setup)));
    }

    /// <summary>The Thickness Palace is written, in the mesh's micrometres, or null.</summary>
    private static double? PalaceThickness(Em3dProblem problem)
    {
        var low = GmshGeoWriter.Write(problem, PalaceSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        var config = PalaceConfigWriter.Write(problem, low.Groups, PalaceSettings.Default);
        Assert.True(config.Ok, config.Refusal);
        using var doc = JsonDocument.Parse(config.Json!);
        var entry = doc.RootElement.GetProperty("Boundaries").GetProperty("Conductivity").EnumerateArray().Single();
        return entry.TryGetProperty("Thickness", out var t) ? t.GetDouble() : null;
    }

    private static Em3dProblem Driven(Em3dProblem line) => line with
    {
        Ports = [new Em3dPort(1, "P1", "strip", "airbox/zmin", new Point3(0, 0, -1 * Um), new Point3(0, 5 * Um, 0), new Point3(0, 0, 1),
                              50, new Em3dReferencePlane(new Point3(0, 0, 0), new Point3(1, 0, 0), 0))],
    };

    [Fact]
    public void PalaceWritesThickness_OnAThinConductorOnly()
    {
        Assert.InRange(PalaceThickness(Driven(Line(5, 1, 3.77e7, 1e9)))!.Value, 0.83, 0.834);
        Assert.Null(PalaceThickness(Driven(Line(100, 35, 5.8e7, 1e9))));
    }

    [Fact]
    public void PalaceWritesNoThickness_WithTheCorrectionOff_OnAnEigenmodeProblem_OrPastTheRangeItCanEvaluate()
    {
        var thin = Driven(Line(5, 1, 3.77e7, 1e9));
        Assert.Null(PalaceThickness(thin with { ThinMetalCorrection = false }));
        Assert.Null(PalaceThickness(thin with { Type = Em3dProblemType.Eigenmode, EigenmodeTargetHz = 1e9, EigenmodeCount = 1 }));
        // 26 µm of copper: under three skin depths at 1 MHz, and past 700 at 10 THz.
        var wide = Driven(Line(100, 35, 5.8e7, 1e6)) with { Frequency = new Em3dFrequency(1e6, 1e13, 5, Em3dSweepKind.Linear) };
        var past = Assert.Single(Em3dThinMetalLoss.Find(wide));
        Assert.False(past.Corrected);
        Assert.Contains("over 700 skin depths", Assert.Single(Em3dThinMetalLoss.ForRun(wide, new EmSetup { Solver3D = Em3dSolverId.Palace })));
        Assert.Null(PalaceThickness(wide));
    }

    [Fact]
    public void EmergeProblemJson_CarriesHPerConductor_AndNothingOnThickMetal()
    {
        double? H(Em3dProblem problem)
        {
            var low = EmergeScriptWriter.Write(problem, EmergeSettings.Default);
            Assert.True(low.Ok, low.Refusal);
            using var doc = JsonDocument.Parse(low.ProblemJson!);
            var solid = doc.RootElement.GetProperty("solids").EnumerateArray().Single();
            return solid.TryGetProperty("thinMetalHM", out var h) ? h.GetDouble() : null;
        }
        Assert.InRange(H(Driven(Line(5, 1, 3.77e7, 1e9)))!.Value, 0.83 * Um, 0.834 * Um);
        Assert.Null(H(Driven(Line(100, 35, 5.8e7, 1e9))));
        Assert.Null(H(Driven(Line(5, 1, 3.77e7, 1e9)) with { ThinMetalCorrection = false }));
    }

    [Fact]
    public void TheSetting_IsOnByDefault_AndOnlyOffIsWritten()
    {
        Assert.True(new EmSetup().ThinMetalCorrection);
        Assert.DoesNotContain("ThinMetalCorrection", EmSetupPersistence.Serialize(new EmSetup { Name = "s" }));
        string off = EmSetupPersistence.Serialize(new EmSetup { Name = "s", ThinMetalCorrection = false });
        Assert.False(EmSetupPersistence.Deserialize(off).ThinMetalCorrection);
    }
}
