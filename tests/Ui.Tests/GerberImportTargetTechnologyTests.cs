// Gate for docs/sonnet-briefs/brief-gerber-import-target-technology.md §6 — a Gerber import INTO a
// technology the workspace already has (R-gt-1), its refusals (R-gt-3, R-gt-5) and re-proposing the
// rows against another technology without reading the files again (R-gt-2). Design level: no dialog.
//
// The file set is DocGerberFixtures.WriteSixCopperSet — the same synthetic fourteen files the dialog's
// figure is built from. COUNTERS ONLY: there is no timing assertion in this file.

using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.Diagnostics.Fixtures;

namespace CircuitRF.Ui.Tests;

public sealed class GerberImportTargetTechnologyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-target-tech-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Workspace() => _root;

    private string Install(string id) => WorkspaceCreate.InstallTechnology(_root, id);

    private IReadOnlyList<string> SixCopperSet() =>
        DocGerberFixtures.WriteSixCopperSet(Path.Combine(_root, "set"));

    /// <summary>The six-layer technology with one change, written beside the shipped copy.</summary>
    private string EditedSixLayer(string name, Action<Technology> edit)
    {
        var tech = TechPersistence.LoadFromFile(Install(DocGerberFixtures.SixLayerId));
        edit(tech);
        string path = Path.Combine(_root, "tech", name + ".ctech");
        TechPersistence.SaveToFile(path, tech);
        return path;
    }

    private GerberImport.ImportResult ImportInto(string techPath, IReadOnlyList<string>? files = null) =>
        GerberImport.Import(files ?? SixCopperSet(), Workspace(), "board", null, 1000,
                            target: ImportTechnologyTarget.Use(techPath));

    [Fact]
    public void UsingTheShippedSixLayerTechnology_LandsEveryShapeOnItsKeys_WritesNoTechnology_AndChangesNothing()
    {
        string techPath = Install(DocGerberFixtures.SixLayerId);
        byte[] before = File.ReadAllBytes(techPath);
        var tech = TechPersistence.LoadFromFile(techPath);

        var result = ImportInto(techPath);

        Assert.False(result.Cancelled, string.Join("\n", result.Messages));
        Assert.Empty(Directory.EnumerateFiles(result.ImportDir!, "*.ctech", SearchOption.AllDirectories));

        string layoutDir = CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Directory.EnumerateFiles(layoutDir, "*.clay").Single());
        Assert.Equal(Path.GetFullPath(techPath),
                     Path.GetFullPath(CircuitRF.Core.RefPath.Resolve(layoutDir, view.TechRef!)));

        var keys = tech.Layers.Select(l => l.Key).ToHashSet();
        Assert.NotEmpty(view.Shapes);
        Assert.All(view.Shapes, s => Assert.Contains(s.Layer, keys));
        Assert.Single(view.Shapes.OfType<ViaShape>());

        Assert.Equal(before, File.ReadAllBytes(techPath));
    }

    [Fact]
    public void SixCopperFilesIntoTheFourLayerTechnology_AreRefused_AndNothingIsCreated()
    {
        var result = ImportInto(Install(DocGerberFixtures.FourLayerId));

        Assert.True(result.Cancelled);
        Assert.Contains("6 copper file(s)", result.Refusal);
        Assert.Contains("4 conductor(s)", result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(Workspace(), "board")));
    }

    /// <summary>The set's X2 ranks put L2 on "Inner 1" and L3 on "Inner 2", and this stackup binds
    /// those two drawing layers the other way round — so the second copper file would land on the
    /// third conductor.</summary>
    [Fact]
    public void ACopperFileWhoseRankContradictsTheConductorItLandsOn_IsRefused()
    {
        string techPath = EditedSixLayer("swapped", tech =>
        {
            var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
            (conductors[1].DrawingLayers, conductors[2].DrawingLayers) = (conductors[2].DrawingLayers, conductors[1].DrawingLayers);
        });

        var result = ImportInto(techPath);

        Assert.True(result.Cancelled);
        Assert.Contains("board.g1 is copper layer 2 from the top", result.Refusal);
        Assert.Contains("conductor 3", result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(Workspace(), "board")));
    }

    [Fact]
    public void ADrillFileNoViaEntryBinds_IsRefused()
    {
        string techPath = EditedSixLayer("no-via", tech => tech.Stackup.Layers.RemoveAll(l => l.Kind == StackupKind.Via));

        var result = ImportInto(techPath);

        Assert.True(result.Cancelled);
        Assert.Contains("board.drl is drill data", result.Refusal);
        Assert.False(Directory.Exists(Path.Combine(Workspace(), "board")));
    }

    /// <summary>D7's warning, on the Artwork to Schematic example's own set and workspace technology. Its job file
    /// states no stackup, which must raise no warning — the import once compared its own FR-4 guess against the
    /// technology as "the job file's". The same job file given a stackup that disagrees must still raise one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJobFileWarnsOnlyWhenItStatesAStackupThatDiffers(bool statesAStackup)
    {
        string example = Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "examples", "Artwork to Schematic");
        string set = Path.Combine(_root, "Board");
        Directory.CreateDirectory(set);
        foreach (string f in Directory.GetFiles(Path.Combine(example, "fab", "Board")))
            File.Copy(f, Path.Combine(set, Path.GetFileName(f)));
        string techPath = Path.Combine(_root, "board.ctech");
        File.Copy(Path.Combine(example, "tech", "board.ctech"), techPath);

        if (statesAStackup)
        {
            string job = Path.Combine(set, "Board.gbrjob");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(job))!.AsObject();
            json["MaterialStackup"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [ { "Type": "Copper", "Name": "Top", "Thickness": 0.035 },
                  { "Type": "Dielectric", "Name": "Core", "Thickness": 1.0, "DielectricConstant": 4.5 },
                  { "Type": "Copper", "Name": "Bottom", "Thickness": 0.035 } ]
                """);
            File.WriteAllText(job, json.ToJsonString());
        }

        var result = ImportInto(techPath, GerberImportEntry.FilesIn(set));

        Assert.False(result.Cancelled, string.Join("\n", result.Messages));
        var warnings = result.Messages.Where(m => m.Contains("job file states a stackup", StringComparison.Ordinal)).ToList();
        if (statesAStackup) Assert.Contains("1000 µm in the job file", Assert.Single(warnings));
        else Assert.Empty(warnings);
    }

    /// <summary>R-gt-2: started against A, re-proposed against B — the rows are a fresh run's against
    /// B, and every file was read exactly once. Reads are counted off the import's own progress
    /// labels, which name each file as it is read.</summary>
    [Fact]
    public void ReProposingAgainstAnotherTechnology_GivesAFreshRunsRows_WithoutReadingAnyFileTwice()
    {
        string a = Install(DocGerberFixtures.FourLayerId);
        string b = Install(DocGerberFixtures.SixLayerId);
        var files = SixCopperSet();

        IReadOnlyList<LayerMappingRow>? fresh = null;
        GerberImport.Import(files, Workspace(), "fresh", null, 1000, target: ImportTechnologyTarget.Use(b),
            resolveMapping: r => { fresh = r.Rows; return null; });

        var labels = new List<string>();
        var control = new RunControl { MinReportIntervalMs = 0, Progress = new SyncProgress(p => labels.Add(p.Stage)) };
        IReadOnlyList<LayerMappingRow>? reproposed = null;
        var result = GerberImport.Import(files, Workspace(), "board", null, 1000, control: control,
            target: ImportTechnologyTarget.Use(a),
            resolveMapping: r =>
            {
                reproposed = r.Repropose(TechPersistence.LoadFromFile(b));
                return new ImportMappingAnswer(reproposed, ImportTechnologyTarget.Use(b));
            });

        Assert.False(result.Cancelled, string.Join("\n", result.Messages));
        Assert.Equal(fresh!, reproposed!);
        foreach (string file in files)
            Assert.Single(labels, l => l == $"reading {Path.GetFileName(file)}");
    }

    private sealed class SyncProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
