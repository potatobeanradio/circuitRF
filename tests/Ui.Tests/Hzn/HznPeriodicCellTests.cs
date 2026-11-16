using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-16 — a periodic unit cell: Hertzon's Primary/Secondary boundaries as the air box's periodic pairs and its scan, its
/// Floquet ports as <c>.c3d</c> Floquet ports whose modes are the result's ports, the problem EMerge is handed, and what other solvers and
/// the Hertzon writer refuse. The fixture (<c>periodic-cell.hzn</c>) was written for these tests by a script: a 12 mm square cell with a
/// slab and a perfectly conducting cross, its side faces drawn as sheets carrying the periodic pairs scanned to θ = 10°, and its bottom
/// and top faces drawn as sheets carrying two-mode Floquet ports.
/// </summary>
public sealed class HznPeriodicCellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn16_" + Guid.NewGuid().ToString("N")[..8]);

    public HznPeriodicCellTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "periodic-cell.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Cell");
    }

    private static C3dDocument Doc()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t.Document!;
    }

    private static Em3dProblem Problem()
    {
        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "Cell", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        return made.Problem!;
    }

    [Fact]
    public void PrimaryAndSecondary_AreTheAirBoxsPeriodicPairs_WithTheScan_AndTheirSheetsAreNotGeometry()
    {
        var doc = Doc();
        var box = C3dSetups.Read(doc).Single().Setup!.AirBox!;
        Assert.All(new[] { box.XMin, box.XMax, box.YMin, box.YMax }, f => Assert.Equal(Em3dBoundaryKind.Periodic, f!.Boundary));
        Assert.Equal(new Em3dScan(10, 0), box.Scan);
        Assert.DoesNotContain(doc.Objects, o => o.Name.StartsWith("Side", StringComparison.Ordinal) || o.Name.StartsWith("Port", StringComparison.Ordinal));
    }

    [Fact]
    public void FloquetPorts_CoverTheirZFaces_AndTheirModesAreNumberedOneAfterAnother()
    {
        var ports = Doc().Ports.OrderBy(p => p.Number).ToList();
        Assert.Equal([1, 3], ports.Select(p => p.Number));
        Assert.All(ports, p => Assert.Equal((2, C3dPorts.ModeZ0, 12_000_000L), (p.Floquet!.Modes, p.Z0, p.Rect.Size.U)));
        Assert.Equal([-10_000_000L, 10_000_000L], ports.Select(p => p.Offset));
    }

    [Fact]
    public void TheProblem_HasOneModeNormalisedPortPerMode_AndEMergeIsHandedTheCellAndItsPorts()
    {
        var problem = Problem();
        Assert.Empty(problem.Validate());
        Assert.Equal([1, 2, 3, 4], problem.Ports.Select(p => p.Number).Order());
        Assert.All(problem.Ports, p => Assert.True(p.ModeNormalised && p.Floquet is not null));
        Assert.Equal([1, 2], problem.Ports.Where(p => p.FaceGroup == 1).Select(p => p.Floquet!.Mode).Order());

        var lowering = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(null));
        Assert.Null(lowering.Refusal);
        var json = JsonNode.Parse(lowering.ProblemJson!)!;
        Assert.Equal(10, (double)json["periodic"]!["thetaDeg"]!);
        Assert.Equal("[[1,2],[3,4]]", new JsonArray([.. json["floquetPorts"]!.AsArray().Select(f => f!["modes"]!.DeepClone())]).ToJsonString());
        Assert.Empty(json["wavePorts"]!.AsArray());
    }

    [Fact]
    public void APeriodicCell_RunsOnEMergeOnly()
    {
        var problem = Problem();
        Assert.Contains("EMerge only", Em3dModePorts.SolverRefusal(problem, PalaceSolver.Id));
        Assert.Null(Em3dModePorts.SolverRefusal(problem, EmergeSolver.Id));
    }

    [Fact]
    public void APeriodicFaceWithoutItsPartner_AndAFloquetPortOverPartOfItsFace_AreRefused()
    {
        var problem = Problem();
        var f = problem.Boundary.Faces;
        var lone = problem with { Boundary = problem.Boundary with { Faces = f with { XMax = Em3dBoundaryKind.Pec } } };
        Assert.Contains(lone.Validate(), p => p.Contains("Only one of the air box's x faces is periodic"));
        var part = problem with
        {
            Ports = [.. problem.Ports.Select(p => p.FaceGroup == 1 ? p with { Max = p.Max with { X = p.Max.X - 1e-3 } } : p)],
        };
        Assert.Contains(part.Validate(), p => p.Contains("covers part of the zmin face"));
    }

    [Fact]
    public void ASecondaryWithoutItsPrimary_IsRefused()
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        var b = json["designs"]![0]!["boundaries"]!.AsArray();
        b.Remove(b.First(x => (string?)x!["name"] == "Primary_Y"));
        string path = Path.Combine(_root, "lonely.hzn");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Contains(Translate(path).Refusals, r => r.What == "Secondary without its Primary");
    }

    [Fact]
    public void TheHertzonWriter_RefusesAFloquetPortByName()
    {
        var t = Translate(Fixture);
        var r = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = "Cell", Technology = t.Technology });
        Assert.Contains(r.Refusals, i => i.What == "Floquet port");
    }
}
