// Owner: PCD-6 (brief-pcd-6 §4). Headless, through the view model (D1).

using System.Runtime.CompilerServices;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd6DesignerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd6-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        EmEvaluation.SolverStandIn = null;
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static string Source(params string[] parts) =>
        Path.Combine([Path.GetFullPath(Path.Combine(Here(), "..", "..", "..")), .. parts]);

    private static string Here([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;

    /// <summary>R-pcd6-1: one window per cell folder, however the folder is spelled; a designed cell is found from its
    /// schematic and from a placed component; both Tools menus, File ▸ Import and both context menus carry the entries.</summary>
    [Fact]
    public void EntryPoints_OneWindowPerCellFolder_AndTheDesignedCellIsFound()
    {
        var windows = new PCellDesignerWindows<object>();
        int created = 0, activated = 0;
        var (a, made) = windows.Show(Path.Combine(_scratch, "Cell"), () => { created++; return new object(); }, _ => activated++);
        var (b, again) = windows.Show(Path.Combine(_scratch, "x", "..", "Cell") + Path.DirectorySeparatorChar, () => new object(), _ => activated++);
        Assert.True(made);
        Assert.False(again);
        Assert.Same(a, b);
        Assert.Equal((1, 1), (created, activated));
        windows.Closed(Path.Combine(_scratch, "Cell"), a);
        Assert.False(windows.IsOpen(Path.Combine(_scratch, "Cell")));

        var (vm, p) = Pcd6Fixture.Open(Path.Combine(_scratch, "a"));
        Assert.Equal(vm.CellDir, PCellDesignerViewModel.DesignedCellOf(p.SchematicPath));
        var placed = new EditableComponent { InstanceName = "X1", CellRef = Path.GetRelativePath(p.SchematicDir, p.CellDir) };
        Assert.Equal(vm.CellDir, Path.GetFullPath(PCellDesignerViewModel.DesignedCellOf(placed, p.SchematicDir)!));
        File.WriteAllText(Path.Combine(p.CellDir, CellFolder.CcellFileName), "{}");
        Assert.Null(PCellDesignerViewModel.DesignedCellOf(p.SchematicPath));

        string window = File.ReadAllText(Source("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Equal(2, window.Split("OpenPCellDesignerCommand").Length - 1);
        Assert.Equal(2, window.Split("ImportPCellPackageCommand").Length - 1);
        Assert.Contains("CtxEditInPCellDesigner", File.ReadAllText(Source("src", "Ui", "Views", "Content", "SchematicView.axaml")));
        Assert.Contains("OpenPCellDesignerOn", File.ReadAllText(Source("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs")));
    }

    /// <summary>R-pcd6-2 and R-pcd6-3: the canvas is the plan at the Value column, not at the defaults; a pin's pose is
    /// read off the plan.</summary>
    [Fact]
    public async Task Canvas_IsThePlanAtTheValues()
    {
        var (vm, p) = Pcd6Fixture.Open(_scratch);
        var row = Assert.Single(vm.Parameters);
        Assert.Equal(("L1", "5", "mm", "1", "10"), (row.Name, row.Default, row.Unit, row.Minimum, row.Maximum));

        await vm.DesignAsync(0);
        Assert.NotNull(vm.Layout);
        long um = vm.Layout!.DbuPerMicron;
        Assert.Equal(5000 * um, Pcd6Fixture.Length(Pcd6Fixture.Box(vm.Layout, p.LayoutDir, "TL1")));

        row.Value = "8 mm";
        await vm.DesignAsync(0);
        Assert.Equal(8000 * um, Pcd6Fixture.Length(Pcd6Fixture.Box(vm.Layout!, p.LayoutDir, "TL1")));
        Assert.Equal("5", row.Default);   // the value is not the default

        var tl1End = vm.PinPoseOf("TL1", 2)!.Value;
        var tl2Start = vm.PinPoseOf("TL2", 1)!.Value;
        Assert.Equal((tl1End.X, tl1End.Y), (tl2Start.X, tl2Start.Y));   // joined
        Assert.Equal(180.0, Math.Abs(tl1End.OutwardDeg - tl2Start.OutwardDeg));
        Assert.NotNull(vm.PinAt(tl1End.X + 3, tl1End.Y, 5));
        Assert.Null(vm.PinAt(tl1End.X + 1000 * um, tl1End.Y + 1000 * um, 5));
    }

    /// <summary>R-pcd6-5: the response is the cell's schematic at the values, on the default sweep when it states none.</summary>
    [Fact]
    public async Task Response_FollowsTheValues()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        await vm.DesignAsync(0);
        var first = vm.Response;
        Assert.NotNull(first);
        Assert.Equal((2, 201), (first!.Ports, first.FrequencyCount));
        Assert.Equal(2, vm.Plot.Traces.Count);

        vm.Parameters[0].Value = "8 mm";
        await vm.DesignAsync(0);
        Assert.NotEqual(first.Matrices[100][1, 0], vm.Response!.Matrices[100][1, 0]);   // a longer line, another phase
    }

    /// <summary>R-pcd6-5: an EM-only cell (an MSHAPE with Model "none") draws no circuit curve and says why, pointing to
    /// Run EM; with Model "open" the shape counts as a model and the response is drawn.</summary>
    [Theory]
    [InlineData("\"none\"", false)]
    [InlineData("\"open\"", true)]
    public async Task Response_OfAnEmOnlyCell_IsNoCurveButASentence(string model, bool drawn)
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, edit: m =>
        {
            m.Components.RemoveAll(c => c.InstanceName is "P2" or "GND2");
            var pad = new EditableComponent { Symbol = SymbolKind.MShape, InstanceName = "SH1",
                                              Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1 } };
            foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
                pad.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
            pad.Parameters.Single(q => q.Name == DrawnMetal.Model).Expression = model;
            m.Components.Add(pad);
        });
        await vm.DesignAsync(0);
        Assert.Equal(drawn, vm.Response is not null);
        Assert.Equal(drawn, vm.Plot.Traces.Count > 0);
        if (!drawn) Assert.Matches("no circuit model.*SH1.*Run EM", vm.Status);
    }

    /// <summary>R-pcd6-9 and R-pcd6-3: Save writes the parameters (a range in base SI), the marker, and a layout planned at
    /// the defaults, never at the Value column.</summary>
    [Fact]
    public async Task Save_WritesTheCellAndItsLayoutAtTheDefaults()
    {
        var (vm, p) = Pcd6Fixture.Open(_scratch);
        vm.AddParameter();
        var added = vm.Parameters[1];
        (added.Minimum, added.Maximum, added.Description) = ("50", "0.2 mm", "pad length");
        vm.Parameters[0].Value = "8 mm";
        await vm.DesignAsync(0);

        Assert.Null(vm.Save());
        Assert.False(vm.Dirty);
        var cell = CellFolder.ReadCcell(vm.CellDir)!;
        Assert.NotNull(cell.PCellDesign);
        Assert.True(cell.EmCopper);
        Assert.Equal("cpcell:" + vm.CellName, cell.LayoutGenerator);
        Assert.Equal(new[] { (1, "P1"), (2, "P2") }, cell.Terminals!.Select(t => (t.Port, t.Name)).OrderBy(t => t.Port));
        var saved = cell.Parameters.Single(x => x.Name == added.Name);
        Assert.Equal(5e-5, saved.Minimum!.Value, 12);
        Assert.Equal(2e-4, saved.Maximum!.Value, 12);
        Assert.Equal("pad length", saved.Description);

        var clay = LayoutPersistence.LoadFromFile(vm.ClayPath);
        Assert.Equal(5000 * clay.DbuPerMicron, Pcd6Fixture.Length(Pcd6Fixture.Box(clay, p.LayoutDir, "TL1")));
        Assert.Contains("L1", File.ReadAllText(vm.SchematicPath));
    }

    /// <summary>R-pcd6-9: Save gives the cell a symbol generated from its Ports, so a bench can place it.</summary>
    [Fact]
    public void Save_GivesTheCellASymbolFromItsPorts()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        Assert.Null(vm.Save());
        var cell = CellFolder.ReadCcell(vm.CellDir)!;
        string symbol = Path.Combine(CellFolder.SubFolderPath(vm.CellDir, ViewType.Symbol), cell.PrimarySymbol!);
        Assert.Equal(2, SymbolPersistence.LoadFromFile(symbol).Pins.Count);
        Assert.Equal(2, cell.NumPorts);
    }

    /// <summary>R-pcd6-3: a range is saved as the clean base-SI value its text states, not a float-product's tail.</summary>
    [Fact]
    public void Save_WritesARangeAsACleanSiValue()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        vm.Parameters[0].Minimum = "100 um";
        vm.Parameters[0].Maximum = "7";
        Assert.Null(vm.Save());
        string json = File.ReadAllText(Path.Combine(vm.CellDir, CellFolder.CcellFileName));
        Assert.Contains("\"Minimum\": 0.0001", json);
        Assert.Contains("\"Maximum\": 0.007", json);
    }

    /// <summary>R-pcd6-6: Run EM saves, makes the cell's setup, solves at the values and overlays the result.</summary>
    [Fact]
    public async Task RunEm_SolvesTheCellAtTheValues_AndOverlaysIt()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        IReadOnlyDictionary<string, Value>? solvedAt = null;
        EmEvaluation.SolverStandIn = (_, values, path) =>
        {
            solvedAt = values;
            File.WriteAllText(path + ".s2p", "# GHz S RI R 50\n1 0 0 1 0 1 0 0 0\n2 0 0 1 0 1 0 0 0\n");
            return path + ".s2p";
        };
        await vm.DesignAsync(0);
        vm.Parameters[0].Value = "6 mm";
        vm.RunEm();
        await vm.Work;
        Assert.True(vm.Em is not null, vm.Status);
        Assert.Equal(6e-3, solvedAt!["L1"].AsReal(), 12);
        Assert.Contains(vm.Plot.Traces, t => t.Label == "EM S21");
        Assert.NotEmpty(EmSetupResolver.FindSetupsForLayout(Path.Combine(vm.WorkspaceRoot, ".cws"), vm.ClayPath));
    }

    /// <summary>R-pcd6-7: Optimise saves, then hands the cell's schematic to the workspace's Optimizer.</summary>
    [Fact]
    public void Optimise_SavesAndOpensTheSchematicWithTheOptimizer()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        string? opened = null;
        vm.OpenOptimizer = path => opened = path;
        vm.Parameters[0].Default = "6";
        vm.Optimise();
        Assert.Equal(vm.SchematicPath, opened);
        Assert.Equal("6", CellFolder.ReadCcell(vm.CellDir)!.Parameters[0].DefaultExpression);
    }
}
