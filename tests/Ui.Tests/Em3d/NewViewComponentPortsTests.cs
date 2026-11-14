using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-lcp-6 — New 3D View from Layout carries each .cem's component ports. LCP-1's board, given a drawn ground plane on Bottom
// Copper so its drawn ports translate into a 3D view. Nothing is built, meshed or solved: every gate reads documents.
public sealed class NewViewComponentPortsTests : IClassFixture<PlanarComponentPortTests.Board>
{
    private readonly PlanarComponentPortTests.Board board;

    public NewViewComponentPortsTests(PlanarComponentPortTests.Board board)
    {
        this.board = board;
        var view = LayoutPersistence.LoadFromFile(board.ClayPath);
        var bottom = new LayerKey(2, 0);
        if (!view.Shapes.Any(s => s.Layer == bottom))
        {
            view.Shapes.Add(new RectShape { Layer = bottom, X1 = 0, Y1 = -3_000_000, X2 = 12_000_000, Y2 = 3_000_000 });
            LayoutPersistence.SaveToFile(board.ClayPath, view);
        }
        // Each gate writes its own .cem files and its own .c3d: none survives into the next.
        foreach (string dir in new[] { EmDir, ThreeDDir })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string Cws => Path.Combine(board.Workspace, ".cws");
    private string CellDir => Path.GetDirectoryName(Path.GetDirectoryName(board.ClayPath))!;
    private string EmDir => Path.Combine(CellDir, "em");
    private string ThreeDDir => CellFolder.SubFolderPath(CellDir, ViewType.ThreeD);
    private string C3dPath => Path.Combine(ThreeDDir, "Board" + C3dPersistence.Extension);

    private string WriteCem(string file, EmSetup setup)
    {
        Directory.CreateDirectory(EmDir);
        string path = Path.Combine(EmDir, file);
        setup.LayoutRef = EmSetupResolver.MakeLayoutRef(path, board.ClayPath, Cws);
        EmSetupPersistence.SaveToFile(path, setup);
        return path;
    }

    private C3dFromLayout Make() => C3dHierarchy.NewFromLayout(CellDir, C3dPath, Cws);

    private static string Sweep(CircuitRF.Core.Design.FrequencySpec f)
        => $"{f.Kind} {f.Mode} {f.StartExpr} {f.StartUnit} {f.StopExpr} {f.StopUnit} {f.StepExpr} {f.StepUnit} {f.NumPoints}";

    private static string Ports(C3dDocument doc)
        => string.Join("\n", doc.Ports.Select(p => $"{p.Number} {p.Kind} {p.Plane} {p.Offset} {p.Rect.Min} {p.Rect.Size} {p.Z0}"));

    /// <summary>Gate 1 — a Planar .cem with C1 and C2, New 3D View, then the written .c3d's setup lists C1 and C2: a 3D setup of the
    /// .cem's name and sweep, none of its planar mesh settings, and each entry's Z0 kept.</summary>
    [Fact]
    public void APlanarCemsComponentPorts_BecomeA3DSetupOfTheNewView()
    {
        var planar = EmSetupPersistence.LoadFromFile(WriteCem("board.cem", PlanarComponentPortTests.Setup(["C1", "C2"])));
        var made = Make();
        CellCreate.WriteThreeDView(CellDir, "Board", made.Document!);

        var setup = EmSetupPersistence.FromEmbedded(Assert.Single(C3dPersistence.LoadFromFile(C3dPath).Setups));
        Assert.Equal("board", setup.Name);
        Assert.True(setup.Is3D);
        Assert.Equal(Em3dSolvers.DefaultInstalled(), setup.Solver3D);    // brief-icp-10 — nobody chose: the first installed solver
        Assert.Equal(Sweep(planar.Frequency), Sweep(setup.Frequency));
        Assert.Equal(new EmSetup().PlanarMesh, setup.PlanarMesh);
        Assert.Equal(planar.ComponentPorts, setup.ComponentPorts);

        var said = Assert.Single(made.Setups);
        Assert.True(said.FromPlanar);
        Assert.Contains("(C1, C2)", said.Describe(), StringComparison.Ordinal);
        Assert.Null(made.PortsNote);   // it counts as a copied setup: the default driven setup does not also fire
        Assert.Equal(2, made.Document!.Ports.Count);
    }

    /// <summary>The 3D setup made from a Planar .cem runs the solver the caller names (the GUI's Preferred 3D solver setting).</summary>
    [Fact]
    public void APlanarCemsSetup_RunsTheNamedSolver()
    {
        WriteCem("board.cem", PlanarComponentPortTests.Setup(["C1", "C2"]));
        var made = C3dHierarchy.NewFromLayout(CellDir, C3dPath, Cws, solver3D: Em3dSolverChoice.One(Em3dSolverId.Emerge));
        var setup = EmSetupPersistence.FromEmbedded(Assert.Single(made.Document!.Setups));
        Assert.Equal(Em3dSolverChoice.One(Em3dSolverId.Emerge), setup.Solver3D);
    }

    /// <summary>Gate 2 — copied once: an edit to either file leaves the other byte-identical, and the .c3d records no link.</summary>
    [Fact]
    public void TheTwoLists_AreSeparateAfterwards()
    {
        string cem = WriteCem("board.cem", PlanarComponentPortTests.Setup(["C1", "C2"]));
        CellCreate.WriteThreeDView(CellDir, "Board", Make().Document!);
        Assert.DoesNotContain(EmSetupPersistence.Extension, File.ReadAllText(C3dPath), StringComparison.Ordinal);

        byte[] cemBytes = File.ReadAllBytes(cem);
        var doc = C3dPersistence.LoadFromFile(C3dPath);
        var setup = EmSetupPersistence.FromEmbedded(doc.Setups[0]);
        setup.ComponentPorts = [new EmComponentPort("C2")];
        doc.Setups[0] = EmSetupPersistence.ToEmbedded(setup);
        C3dPersistence.SaveToFile(C3dPath, doc);
        Assert.Equal(cemBytes, File.ReadAllBytes(cem));

        byte[] c3dBytes = File.ReadAllBytes(C3dPath);
        var planar = EmSetupPersistence.LoadFromFile(cem);
        planar.ComponentPorts = [new EmComponentPort("C1")];
        EmSetupPersistence.SaveToFile(cem, planar);
        Assert.Equal(c3dBytes, File.ReadAllBytes(C3dPath));
    }

    /// <summary>Gate 3 — two .cem files with different lists give two setups, each its own list, never a union.</summary>
    [Fact]
    public void TwoCems_GiveTwoSetups_Unmerged()
    {
        WriteCem("a.cem", PlanarComponentPortTests.Setup(["C1"]));
        WriteCem("b.cem", PlanarComponentPortTests.Setup(["C2"]));
        var made = Make();
        var setups = made.Document!.Setups.Select(EmSetupPersistence.FromEmbedded).ToList();
        Assert.Equal(["board", "board 2"], setups.Select(s => s.Name));
        Assert.Equal(["C1"], setups[0].ComponentPorts.Select(c => c.Refdes));
        Assert.Equal(["C2"], setups[1].ComponentPorts.Select(c => c.Refdes));
        Assert.Equal(["a.cem", "b.cem"], made.Setups.Select(s => Path.GetFileName(s.Cem)));
    }

    /// <summary>Gate 4 — a Planar .cem with no component ports adds no setup, and the drawn port labels translate as with no .cem at
    /// all; one with component ports translates them to the same ports.</summary>
    [Fact]
    public void APlanarCemWithoutComponentPorts_AddsNothing()
    {
        var bare = Make();
        Assert.NotNull(bare.PortsNote);

        WriteCem("board.cem", PlanarComponentPortTests.Setup([]));
        var none = Make();
        Assert.Empty(none.Document!.Setups);
        Assert.Empty(none.Setups);
        Assert.Equal(bare.PortsNote, none.PortsNote);
        Assert.Equal(Ports(bare.Document!), Ports(none.Document!));

        WriteCem("board.cem", PlanarComponentPortTests.Setup(["C1", "C2"]));
        Assert.Equal(Ports(bare.Document!), Ports(Make().Document!));
    }

    /// <summary>Gate 5 — a 3D .cem with component ports keeps its drawn ports (translated with its list cleared, R-lcp6-1) and
    /// its list.</summary>
    [Fact]
    public void A3DCemWithComponentPorts_KeepsItsDrawnPortsAndItsList()
    {
        var driven = PlanarComponentPortTests.Setup(["C1", "C2"]);
        driven.Solver3D = Em3dSolverId.Palace;
        WriteCem("board.cem", driven);
        var made = Make();
        Assert.Empty(made.PortsLeftOut);
        Assert.Equal([1, 2], made.Document!.Ports.Select(p => p.Number));
        var setup = EmSetupPersistence.FromEmbedded(Assert.Single(made.Document.Setups));
        Assert.Equal(driven.ComponentPorts, setup.ComponentPorts);
        Assert.False(Assert.Single(made.Setups).FromPlanar);
    }
}
