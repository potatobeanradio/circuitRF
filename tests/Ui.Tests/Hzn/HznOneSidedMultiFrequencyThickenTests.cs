using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-18 — a one-sided lossy sheet, a Multi-Frequencies setup, and Thicken Sheet. The fixture (<c>one-sided-multi-thicken.hzn</c>,
/// written for these tests by a script) holds a perfect ground sheet, a copper strip 1 mm above it under a one-sided Finite
/// Conductivity boundary with a lumped port between them, a polyline thickened 0.5 mm up, a rectangle thickened by a variable
/// (0.25 mm) the other way, and a Multi-Frequencies setup at 2.44 and 6.1 GHz (written with Hertzon's other separator, ';').
/// </summary>
public sealed class HznOneSidedMultiFrequencyThickenTests : IDisposable
{
    private const long Mm = 1_000_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn18_" + Guid.NewGuid().ToString("N")[..8]);

    public HznOneSidedMultiFrequencyThickenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "one-sided-multi-thicken.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "OneSided");
    }

    private static C3dTranslation Ok(string path)
    {
        var t = Translate(path);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t;
    }

    private static Em3dProblem Problem()
    {
        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "OneSided", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        return made.Problem!;
    }

    [Fact]
    public void AOneSidedFiniteConductivitySheet_IsOneSided_AndRoundTripsThroughC3d()
    {
        var doc = Ok(Fixture).Document!;
        Assert.True(Assert.IsType<C3dSheet>(doc.Objects.Single(o => o.Name == "Strip")).OneSided);
        Assert.False(Assert.IsType<C3dSheet>(doc.Objects.Single(o => o.Name == "Gnd")).OneSided);

        string text = C3dPersistence.Serialize(doc);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"OneSided\""));
        Assert.True(C3dPersistence.Deserialize(text).Objects.OfType<C3dSheet>().Single(s => s.Name == "Strip").OneSided);
    }

    [Fact]
    public void TheEmergeProblemCarriesOneSided_ForThatSheetOnly_AndPalaceAndOpenEmsRefuseIt()
    {
        var problem = Problem();
        Assert.True(problem.Sheets.Single(s => s.Name == "Strip").OneSided);
        Assert.False(problem.Sheets.Single(s => s.Name == "Gnd").OneSided);

        var lowering = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(null));
        Assert.True(lowering.Ok, lowering.Refusal);
        var sheets = JsonNode.Parse(lowering.ProblemJson!)!["sheets"]!.AsArray();
        Assert.Equal([("Gnd", false), ("Strip", true)],
                     sheets.Select(s => ((string)s!["name"]!, s["oneSided"] is { } v && (bool)v)).OrderBy(x => x.Item1).ToList());

        Assert.Null(Em3dModePorts.SolverRefusal(problem, EmergeSolver.Id));
        foreach (var solver in new[] { PalaceSolver.Id, OpenEmsSolver.Id })
            Assert.Contains("Sheet 'Strip' is one-sided", Em3dModePorts.SolverRefusal(problem, solver));
    }

    [Fact]
    public void OneSidedOnAPerfectConductorSheet_IsAWarningAndDoesNothing()
    {
        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "OneSided", null);
        Assert.True(plan is not null, refusal?.Render());
        var sheets = plan.Document.Objects.OfType<C3dSheet>().ToList();
        sheets.Single(s => s.Name == "Strip").Material = sheets.Single(s => s.Name == "Gnd").Material;      // the ground's perfect metal
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        Assert.False(made.Problem!.Sheets.Single(s => s.Name == "Strip").OneSided);
        Assert.Contains(made.Warnings, w => w.Contains("Sheet 'Strip' is OneSided") && w.Contains("perfect conductor"));
    }

    [Fact]
    public void Export_WritesTwoSidedFalse_ForTheOneSidedSheet()
    {
        var t = Ok(Fixture);
        var keep = t.Document!.Objects.Where(o => o.Name is "Strip" or "Gnd").ToList();
        var written = C3dToHzn.Write(new C3dDocument { Objects = keep }, new HznExportOptions { DesignName = "OneSided", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("OneSided", written.Refusals).Render());
        string path = Path.Combine(_root, "export.hzn");
        HznExport.ToNewFile(written, path);
        var fc = HznFile.Read(path).File!.Project.Designs[0].Boundaries.Single(b => b.Kind == "Finite Conductivity");
        Assert.Equal(["Strip"], fc.Objects);
        Assert.Equal("False", fc.Props["TwoSided"]);
        Assert.True(Ok(path).Document!.Objects.OfType<C3dSheet>().Single(s => s.Name == "Strip").OneSided);
    }

    [Fact]
    public void AMultiFrequenciesSetup_SolvesAtItsFrequencies_AndAutoOpenSizesFromTheLowest()
    {
        var setup = Assert.Single(Ok(Fixture).Document!.Setups);
        double Ghz(JsonElement f) => double.Parse(f.GetProperty("StartExpr").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(2.44, Ghz(setup.GetProperty("Frequency")), 12);
        Assert.Equal([6.1], setup.GetProperty("AdditionalFrequencies").EnumerateArray().Select(Ghz).ToList());
        double padding = setup.GetProperty("AirBox").GetProperty("XMin").GetProperty("PaddingUm").GetDouble();
        Assert.Equal(299_792_458.0 / 2.44e9 / 3 * 1e6, padding, 0);
    }

    [Fact]
    public void AThickenedSheet_IsAPrismAlongItsNormal_AndAReversedOneGrowsBelow()
    {
        var doc = Ok(Fixture).Document!;
        var slab = Assert.IsType<C3dPrism>(doc.Objects.Single(o => o.Name == "Slab"));
        Assert.Equal((C3dPlane.XY, 0L, Mm / 2), (slab.Plane, slab.Offset, slab.Height));
        var down = Assert.IsType<C3dBox>(doc.Objects.Single(o => o.Name == "Down"));
        Assert.Equal((-Mm / 4, Mm / 4), (down.Min.Z, down.Size.Z));
        Assert.NotNull(C3dBindings.GetExpr(down, nameof(C3dBox.Size), 2));      // the variable is kept

        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        var slabJson = json["designs"]![0]!["objects"]!.AsArray().Single(o => (string?)o!["name"] == "Slab")!;
        slabJson["history"]![1]!["props"]!["BothSides"] = "True";
        string path = Path.Combine(_root, "both.hzn");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Contains(Translate(path).Refusals, r => r.What == "ThickenSheet on both sides");
    }

    /// <summary>The four glasses designs this brief opens, under <c>CIRCUITRF_HERTZON_HOME</c>, found by the end of their file names.</summary>
    [HertzonHomeFact]
    public void TheGlassesDesignsThisBriefOpens_Translate()
    {
        foreach (var (file, design) in new[] { ("*_CoupledFedStrip.hzn", "Ex1"), ("*_DualBranchIFA.hzn", "Ex2"), ("*_RunB.hzn", "Ex2_Benchmark"), ("*_RunC.hzn", "T_thick") })
        {
            string? path = Directory.EnumerateFiles(HertzonHomeFactAttribute.Home!, file, SearchOption.AllDirectories).FirstOrDefault();
            if (path is null) continue;
            var t = HznToC3d.Translate(HznFile.Read(path).File!.Project, design);
            Assert.True(t.Ok, $"{design}: {t.Refusal?.Render()}");
        }
    }
}
