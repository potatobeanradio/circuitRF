using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Issues #34 and #35 — a board import lands in a technology ON DISK, as a Gerber import does: one of its own
/// written beside the cells (New, the default), or an existing one it references and never changes (Use). Before,
/// the layers a board brought lived only in a session's copy of the workspace technology, so the next session drew
/// them as unnamed "L-n/0" layers, and a workspace with no technology kept nothing — which is also why New 3D View
/// from Layout refused the board. One test per claim.
/// </summary>
public sealed class PcbImportTechnologyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pcb-import-tech-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private const string TwoLayer = "pcb-2layer_RO4350B_20mil_1oz";

    private static string Fixture(string name)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "testdata", "pcb-samples", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name);
    }

    /// <summary>A workspace and the technology it resolves.</summary>
    private (string Ws, TechResolution Tech) Workspace(string? technologyId)
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", technologyId).WorkspaceDir;
        var (tech, _) = TechnologyResolver.ResolveForDocument(null, Path.Combine(ws, "x.clay"), null, new TechnologyCache());
        return (ws, tech);
    }

    /// <summary>The GUI's Import Board, minus the dialog: into a folder of its own, the workspace's technology the donor.</summary>
    private static PcbImport.ImportResult Import(string ws, TechResolution tech, string board, ImportTechnologyTarget? target = null)
    {
        string importDir = ImportFolder.Create(ws, "board");
        using var stream = File.OpenRead(Fixture(board));
        return PcbImport.Import(stream, importDir, "board", tech.Tech, Dbu, target: target, destTechPath: tech.ResolvedPath);
    }

    private static IEnumerable<string> Clays(PcbImport.ImportResult r)
        => r.CreatedCellDirs.SelectMany(d => Directory.EnumerateFiles(CellFolder.SubFolderPath(d, ViewType.Layout), "*.clay"));

    /// <summary>Every layer key one written layout draws on, with the name a FRESH read of its technology gives it.</summary>
    private static Dictionary<LayerKey, string?> LayersAsReopened(string clay)
    {
        var view = LayoutPersistence.LoadFromFile(clay);
        var (res, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, new TechnologyCache());
        return view.Shapes.Select(s => s.Layer).Distinct()
                   .ToDictionary(k => k, k => res.Tech?.Layers.FirstOrDefault(l => l.Key == k)?.Name);
    }

    [Fact]
    public void New_WritesATechnologyBesideTheCells_EveryLayerReopensNamed_AndTheWorkspaceOneIsUnchanged()
    {
        var (ws, tech) = Workspace(TwoLayer);
        string before = File.ReadAllText(tech.ResolvedPath!);

        var r = Import(ws, tech, "user-layers.kicad_pcb");

        Assert.Equal(Path.Combine(Path.GetDirectoryName(r.BoardCellDir!)!, "board.ctech"), r.TechPath);
        Assert.True(File.Exists(r.TechPath));
        foreach (string clay in Clays(r))
            Assert.All(LayersAsReopened(clay), kv => Assert.NotNull(kv.Value));

        // A layer the board adds is called what the board calls it, not by its geometry token.
        var names = LayersAsReopened(Path.Combine(CellFolder.SubFolderPath(r.BoardCellDir!, ViewType.Layout), "board.clay")).Values;
        Assert.Contains("User.Drawings", names);
        Assert.Equal(before, File.ReadAllText(tech.ResolvedPath!));
    }

    [Fact]
    public void NoWorkspaceTechnology_StillWritesOne_AndTheBoardDrawsIn3D()
    {
        var (ws, tech) = Workspace(null);
        Assert.Null(tech.Tech);
        var r = Import(ws, tech, "user-layers.kicad_pcb");

        string c3d = Path.Combine(CellFolder.SubFolderPath(r.BoardCellDir!, ViewType.ThreeD), "board.c3d");
        var made = C3dHierarchy.NewFromLayout(r.BoardCellDir!, c3d, Path.Combine(ws, ".cws"), new TechnologyCache());
        Assert.Null(made.Refusal);
        var e = new C3dElaborator(new TechnologyCache()).Elaborate(made.Document!, c3d, Path.Combine(ws, ".cws"));
        Assert.True(e.Ok, string.Join(" | ", e.Refusals));
    }

    [Fact]
    public void Use_ReferencesTheTechnology_AndWritesNothingToIt()
    {
        var (ws, tech) = Workspace(TwoLayer);
        string shared = tech.ResolvedPath!;
        string before = File.ReadAllText(shared);

        var r = Import(ws, tech, "user-layers.kicad_pcb", ImportTechnologyTarget.Use(shared));

        Assert.Equal(shared, r.TechPath);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(r.BoardCellDir!)!, "board.ctech")));
        Assert.Equal(before, File.ReadAllText(shared));
        Assert.Contains(r.Messages, m => m.StartsWith("Kept as unknown layers", StringComparison.Ordinal));
    }

    [Fact]
    public void Use_RefusesABoardWhoseCopperIsNotTheTechnologysConductors_AndCreatesNothing()
    {
        var (ws, tech) = Workspace(TwoLayer);
        string importDir = ImportFolder.Create(ws, "board");
        using var stream = File.OpenRead(Fixture("via-blind.kicad_pcb"));     // four copper layers, a two-layer technology

        var r = PcbImport.Import(stream, importDir, "board", tech.Tech, Dbu, target: ImportTechnologyTarget.Use(tech.ResolvedPath!));

        Assert.True(r.Cancelled);
        Assert.Contains("4 copper layer(s)", r.Refusal);
        Assert.Empty(r.CreatedCellDirs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(importDir));
    }
}
