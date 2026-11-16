using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-17 — a polyline's holes, and a port sheet drawn as a quadrilateral in an axis plane. The fixture
/// (<c>holes-and-quad-port.hzn</c>, written for these tests by a script) holds a substrate swept from a polyline with a square and a
/// triangular hole, a ground sheet with a square hole, and at z = 1 mm a board whose top edge slopes 0.0055, a pad whose bottom edge
/// is parallel to it, and the port sheet joining the two: sides parallel to y, ends along the slope, 0.32° from square.
/// </summary>
public sealed class HznPolylineHolesAndQuadPortTests : IDisposable
{
    private const long Mm = 1_000_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn17_" + Guid.NewGuid().ToString("N")[..8]);

    public HznPolylineHolesAndQuadPortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "holes-and-quad-port.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Holes");
    }

    private static C3dTranslation Ok(string path)
    {
        var t = Translate(path);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t;
    }

    private static readonly List<List<C3dPoint2>> SubHoles =
    [
        [new(2 * Mm, 6 * Mm), new(4 * Mm, 6 * Mm), new(4 * Mm, 8 * Mm), new(2 * Mm, 8 * Mm)],
        [new(14 * Mm, 6 * Mm), new(16 * Mm, 6 * Mm), new(15 * Mm, 8 * Mm)],
    ];

    /// <summary>The fixture with the substrate's <c>Holes</c> replaced.</summary>
    private string WithSubHoles(string holes)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        var sub = json["designs"]![0]!["objects"]!.AsArray().Single(o => (string?)o!["name"] == "Sub")!;
        sub["history"]![0]!["props"]!["Holes"] = holes;
        string path = Path.Combine(_root, "edited.hzn");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    [Fact]
    public void ASweptPolylineWithHoles_IsAPrismCarryingThem()
    {
        var sub = Assert.IsType<C3dPrism>(Ok(Fixture).Document!.Objects.Single(o => o.Name == "Sub"));
        Assert.Equal((C3dPlane.XY, 0L, Mm), (sub.Plane, sub.Offset, sub.Height));
        Assert.Equal(SubHoles, sub.Holes);
    }

    [Fact]
    public void AnUnsweptPolylineWithAHole_IsASheetCarryingIt()
    {
        var plane = Assert.IsType<C3dSheet>(Ok(Fixture).Document!.Objects.Single(o => o.Name == "Plane"));
        Assert.Equal([new(9 * Mm, 7 * Mm), new(11 * Mm, 7 * Mm), new(11 * Mm, 9 * Mm), new(9 * Mm, 9 * Mm)], plane.Holes.Single());
    }

    [Fact]
    public void AHoleCrossingItsOutline_OrOffItsPlane_IsRefusedByName()
    {
        Assert.Contains(Translate(WithSubHoles("18,4,0;22,4,0;22,6,0;18,6,0")).Refusals, r => r.What == "CreatePolyline with an invalid hole");
        Assert.Contains(Translate(WithSubHoles("2,6,0;4,6,0;4,8,0.5")).Refusals, r => r.What == "CreatePolyline with a hole off its plane");
    }

    [Fact]
    public void AParallelogramPortSheetInAnAxisPlane_IsALumpedPortDrawnAsItsCorners_JoiningTheBoardToThePad()
    {
        var doc = Ok(Fixture).Document!;
        var port = doc.Ports.Single();
        Assert.Equal(Em3dPortKind.Lumped, port.Kind);
        Assert.Equal(4, port.Corners!.Count);
        Assert.Equal(new C3dPoint3(11 * Mm, 3_060_500, Mm), port.Corners[1]);           // (11, 3 + 0.0055·11, 1) mm, as drawn
        Assert.DoesNotContain(doc.Objects, o => o.Name == "PortSheet");

        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "Holes", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        var p = made.Problem!.Ports.Single();
        Assert.Equal(["Board", "Pad"], new[] { p.NegativeObject, p.PositiveObject }.Order());
        Assert.NotNull(p.Quad);
        Assert.Empty(made.Problem.Validate());
    }

    [Fact]
    public void Export_WritesHoles_AndReadingTheExportGivesTheSameRings()
    {
        var t = Ok(Fixture);
        var keep = t.Document!.Objects.Where(o => o.Name is "Sub" or "Plane").ToList();
        var written = C3dToHzn.Write(new C3dDocument { Objects = keep }, new HznExportOptions { DesignName = "Holes", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Holes", written.Refusals).Render());
        string path = Path.Combine(_root, "export.hzn");
        HznExport.ToNewFile(written, path);

        var back = Ok(path).Document!;
        Assert.Equal(SubHoles, Assert.IsType<C3dPrism>(back.Objects.Single(o => o.Name == "Sub")).Holes);
        Assert.Single(Assert.IsType<C3dSheet>(back.Objects.Single(o => o.Name == "Plane")).Holes);
    }

    /// <summary>The second glasses example's runB and runC designs, under <c>CIRCUITRF_HERTZON_HOME</c>: the holed substrate and the
    /// parallelogram port (runC's corners named variables) assemble into a problem with no validation finding.</summary>
    [HertzonHomeFact]
    public void TheGlassesRunBAndRunCDesigns_AssembleWithTheirHoledSubstrateAndDrawnPort()
    {
        foreach (var (file, design) in new[] { ("*_RunB.hzn", "K1_IFA"), ("*_RunC.hzn", "A_IFA") })
        {
            string? path = Directory.EnumerateFiles(HertzonHomeFactAttribute.Home!, file, SearchOption.AllDirectories).FirstOrDefault();
            if (path is null) continue;
            var read = HznFile.Read(path);
            var t = HznToC3d.Translate(read.File!.Project, design);
            Assert.True(t.Ok, t.Refusal?.Render());
            Assert.Equal(2, Assert.IsType<C3dPrism>(t.Document!.Objects.Single(o => o.Name == "FPC")).Holes.Count);
            Assert.NotNull(t.Document.Ports.Single().Corners);

            string setup = t.Document.Setups[0].GetProperty("Name").GetString()!;
            var (plan, refusal) = HznEmergeRun.Plan(path, design, setup);
            Assert.True(plan is not null, refusal?.Render());
            var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
            Assert.True(made.Ok, made.Refusal);
            Assert.NotNull(made.Problem!.Ports.Single().Quad);
            Assert.Empty(made.Problem.Validate());
        }
    }
}
