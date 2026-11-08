using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-lcp-5 — a 3D .cem on a layout builds component ports, through an in-memory 3D view of the layout. LCP-1's board (two
// drawn ports, C1 and C2 across two gaps), given a drawn ground plane on Bottom Copper: a 3D view solves the copper that is
// drawn, and the planar board's ground is the stackup's. Nothing is meshed or solved: every gate reads the assembled problem.
public sealed class LayoutComponentPort3DTests : IClassFixture<PlanarComponentPortTests.Board>
{
    private readonly PlanarComponentPortTests.Board board;
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    private readonly byte[] _clay;

    public LayoutComponentPort3DTests(PlanarComponentPortTests.Board board, Xunit.Abstractions.ITestOutputHelper output)
    {
        (this.board, this.output) = (board, output);
        // This class's own fixture: the plane is written once, into its board alone.
        var view = LayoutPersistence.LoadFromFile(board.ClayPath);
        var bottom = new LayerKey(2, 0);
        if (!view.Shapes.Any(s => s.Layer == bottom))
        {
            view.Shapes.Add(new RectShape { Layer = bottom, X1 = 0, Y1 = -3_000_000, X2 = 12_000_000, Y2 = 3_000_000 });
            LayoutPersistence.SaveToFile(board.ClayPath, view);
        }
        _clay = File.ReadAllBytes(board.ClayPath);
    }

    private static EmSetup Driven(string[] parts)
    {
        var setup = PlanarComponentPortTests.Setup(parts);
        setup.Solver3D = Em3dSolverId.Palace;
        return setup;
    }

    private string Cws => Path.Combine(board.Workspace, ".cws");
    private string CellDir => Path.GetDirectoryName(Path.GetDirectoryName(board.ClayPath))!;

    private static string Geo(Em3dGenerationResult g, EmSetup setup)
        => GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace)).Geo!;

    private static string Ports(Em3dProblem p)
        => string.Join("\n", p.Ports.Select(x => $"{x.Number} {x.Name} {x.Kind} {x.Min} {x.Max} {x.Z0}"));

    private static string Landings(Em3dGenerationResult g)
        => string.Join("\n", g.ComponentPorts!.Ports.SelectMany(p => p.Landings.Select(l => $"{p.Refdes} {l}")));

    /// <summary>Gate 1 — a .cem on the layout and a .cem on New 3D View's document of that layout build one problem: the same
    /// ports, solids and landings, down to the mesh script.</summary>
    [Fact]
    public void ACemAndAThreeDView_OfOneLayout_BuildOneProblem()
    {
        var setup = Driven(["C1", "C2"]);
        var cem = Em3dSetupProblem.Of(setup, board.Source(), board.Tech);
        Assert.True(cem.Ok, cem.Refusal);
        Assert.Equal(["C1", "C2"], cem.ComponentPorts!.Ports.Where(p => p.Ok).Select(p => p.Refdes));
        Assert.Equal(Em3dSetupProblem.ThroughViewNote, cem.Notes[0]);

        string c3d = Path.Combine(CellFolder.SubFolderPath(CellDir, ViewType.ThreeD), "Board.c3d");
        var made = C3dHierarchy.NewFromLayout(CellDir, c3d, Cws);
        var view = C3dProblemAssembly.Assemble(setup, made.Document!, c3d, Cws, fromCem: true);
        Assert.True(view.Ok, view.Refusal);

        Assert.Equal(4, cem.Problem!.Ports.Count);
        Assert.Equal(Ports(view.Problem!), Ports(cem.Problem));
        Assert.Equal(view.Problem!.Solids.Select(s => s.Name), cem.Problem.Solids.Select(s => s.Name));
        Assert.Equal(Landings(view), Landings(cem));
        Assert.Equal(Geo(view, setup), Geo(cem, setup));
        Assert.False(Directory.Exists(Path.GetDirectoryName(c3d)), "nothing is written");
    }

    /// <summary>Gate 2 — a setup with no component ports is the generator's problem, unchanged.</summary>
    [Fact]
    public void NoComponentPorts_IsTheGeneratorsProblem()
    {
        var setup = Driven([]);
        var one = Em3dSetupProblem.Of(setup, board.Source(), board.Tech);
        var generated = Em3dGenerator.Generate(setup, board.Source(), board.Tech);
        Assert.True(one.Ok, one.Refusal);
        Assert.Equal(generated.Notes, one.Notes);
        Assert.Equal(Geo(generated, setup), Geo(one, setup));
    }

    /// <summary>Gate 3 — each refusal: a loose .clay, a solve region beside component ports, and a drawn port the strict build
    /// refuses where New 3D View would have left it out.</summary>
    [Fact]
    public void TheRefusals()
    {
        var setup = Driven(["C1", "C2"]);

        string loose = Path.Combine(board.Workspace, "loose.clay");
        Assert.Equal(C3dHierarchy.NotInCellRefusal(loose),
                     Em3dSetupProblem.Of(setup, board.Source() with { AbsolutePath = loose }, board.Tech).Refusal);

        var region = setup.Clone();
        region.SolveRegion = new EmSolveRegion(-100, -1000, 12100, 1000);
        Assert.Equal(Em3dSetupProblem.RegionRefusal(region), Em3dSetupProblem.Of(region, board.Source(), board.Tech).Refusal);

        // Port 3 in the air: a .c3d's New 3D View leaves it out and goes on; a .cem's own port cannot be moved, so it refuses.
        var stray = new LabelShape
        {
            Layer = new LayerKey(1, 0), PortLayer = new LayerKey(1, 0), X = 6_000_000, Y = 3_000_000, Text = "3", Height = 300_000,
            IsPort = true, PortKind = PlanarPortKind.Edge, PortDirection = LayoutRotation.R0,
        };
        var refused = Em3dSetupProblem.Of(setup, board.Source([stray]), board.Tech);
        output.WriteLine(refused.Refusal);
        Assert.Null(refused.Problem);
        Assert.Contains("3", refused.Refusal);
        Assert.Null(refused.ComponentPorts);
    }

    /// <summary>The parts' pads come from the layout the caller holds, as the copper does: everything moved 2 mm in memory only
    /// still builds both ports, where the pads used to be read from the file and landed on no copper.</summary>
    [Fact]
    public void ThePads_ComeFromTheLayoutInMemory()
    {
        var source = board.Source();
        foreach (var shape in source.View.Shapes) LayoutGeometry.TranslateBy(shape, 0, 2_000_000);
        foreach (var inst in source.View.Instances) LayoutGeometry.TranslateBy(inst, 0, 2_000_000);
        var moved = Em3dSetupProblem.Of(Driven(["C1", "C2"]), source, board.Tech);
        Assert.True(moved.Ok, moved.Refusal);
        Assert.Equal(["C1", "C2"], moved.ComponentPorts!.Ports.Where(p => p.Ok).Select(p => p.Refdes));
        Assert.Equal(4, moved.Problem!.Ports.Count);
    }

    /// <summary>A board whose only ports are its component ports builds them, as a Planar setup and a .c3d do: there is no drawn
    /// port to translate, and that is not a refusal.</summary>
    [Fact]
    public void ComponentPortsAlone_AreEnough()
    {
        var source = board.Source();
        source.View.Shapes.RemoveAll(s => s is LabelShape { IsPort: true });
        var alone = Em3dSetupProblem.Of(Driven(["C1", "C2"]), source, board.Tech);
        Assert.True(alone.Ok, alone.Refusal);
        Assert.Equal(2, alone.Problem!.Ports.Count);
    }

    /// <summary>A list the setup cannot use (an electrostatic setup that kept one) is not a reason to build through the view: it is
    /// the generator's problem, as with no list.</summary>
    [Fact]
    public void AnInapplicableList_IsTheGeneratorsProblem()
    {
        var setup = Driven(["C1", "C2"]);
        setup.Problem3D = Em3dProblemType.Electrostatic;
        Assert.False(Em3dSetupProblem.ThroughView(setup));
        var plain = setup.Clone();
        plain.ComponentPorts = [];
        var one = Em3dSetupProblem.Of(setup, board.Source(), board.Tech);
        var generated = Em3dGenerator.Generate(plain, board.Source(), board.Tech);
        Assert.Equal(generated.Refusal, one.Refusal);
        Assert.Equal(generated.Notes, one.Notes);
    }

    /// <summary>Gate 5 — two builds of one unchanged setup give one mesh script, so a mesh made by a run reads as current.</summary>
    [Fact]
    public void TheMeshScript_IsStable_AcrossANoOp()
    {
        var setup = Driven(["C1", "C2"]);
        var first = Em3dSetupProblem.Of(setup, board.Source(), board.Tech);
        var second = Em3dSetupProblem.Of(setup.Clone(), board.Source(), board.Tech);
        Assert.Equal(Geo(first, setup), Geo(second, setup));
    }

    /// <summary>R-lcp5-7 — a 3D .cem's marks on its layout: one per built sheet, at the gap a Planar .cem marks, pointing the same way.</summary>
    [Fact]
    public void TheMarks_SitWhereThePlanarOnesDo()
    {
        var host = new LayoutComponentPortsHost(() => board.Source(), () => null);
        var planar = host.PrepareMarks(PlanarComponentPortTests.Setup(["C1", "C2"]))!.Build();
        var sheets = host.PrepareMarks(Driven(["C1", "C2"]))!.Build();
        Assert.Equal(2, sheets.Count);
        Assert.Equal(planar.Select(m => (m.Number, m.Refdes, m.Gap.X, m.Gap.Y, m.Gap.PortDirection)),
                     sheets.Select(m => (m.Number, m.Refdes, m.Gap.X, m.Gap.Y, m.Gap.PortDirection)));
    }

    /// <summary>Gate 4 — explain --component-ports and em --dry-run on a 3D .cem report the ports, writing nothing.</summary>
    [Fact]
    public void ExplainAndADryRun_ReportThePorts_AndWriteNothing()
    {
        var setup = Driven(["C1", "C2"]);
        setup.Name = "lcp5";
        setup.LayoutRef = Path.GetRelativePath(board.Workspace, board.ClayPath).Replace('\\', '/');
        string cem = Path.Combine(board.Workspace, "lcp5.cem");
        EmSetupPersistence.SaveToFile(cem, setup);
        byte[] before = File.ReadAllBytes(cem);
        var expected = Em3dSetupProblem.Of(setup, board.Source(), board.Tech).ComponentPorts!.Ports.Select(C3dComponentPorts.Describe).ToList();

        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var (_, json, err) = CliProcess.Run(repo, [], "explain", cem, "--component-ports", "--json");
        output.WriteLine(json + err);
        var lines = JsonNode.Parse(json)!["result"]!["explain"]!["walks"]!.AsArray()
            .Where(w => w!["step"]!.GetValue<string>() == "component port").Select(w => w!["resolved"]!.GetValue<string>()).ToList();
        Assert.Equal(expected, lines);

        var (exit, stdout, stderr) = CliProcess.Run(repo, [], "em", cem, "--dry-run");
        output.WriteLine(stdout + stderr);
        Assert.True(exit == 0, stderr + stdout);
        Assert.All(expected, e => Assert.Contains(e, stdout + stderr));
        Assert.Equal(before, File.ReadAllBytes(cem));
        Assert.Equal(_clay, File.ReadAllBytes(board.ClayPath));
        Assert.False(Directory.Exists(Path.Combine(board.Workspace, "results", "lcp5")));
    }
}
