// Owner: PCD-5 (brief-pcd-5 §3).

using System.IO.Compression;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-5: a designed cell behaves as any PCell — one test per requirement.</summary>
[Collection(PCellResolverCollection.Name)]
public sealed class Pcd5PCellPackageTests
{
    private const double Mm = 1e-3;

    [Fact]
    public void R1_CreateMakesADesignedCell()
    {
        using var ws = new Pcd5Workspace();
        var created = PCellDesign.Create(ws.Root, "Made1");
        var ccell = CellFolder.ReadCcell(created.CellDir)!;

        Assert.True(PCellDesign.IsDesigned(created.CellDir));
        Assert.Equal("cpcell:Made1", ccell.LayoutGenerator);
        Assert.True(ccell.EmCopper);
        Assert.True(File.Exists(created.SchematicPath) && File.Exists(created.SymbolPath));
        Assert.Throws<IOException>(() => PCellDesign.Create(ws.Root, "Made1"));   // never over an existing cell
    }

    [Fact]
    public void R2_TheGeneratorDrawsTheCellAtTheInstanceValues_AndPlacesInABench()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Thru2");
        using var registration = DesignedPCellResolver.Register(ws.Root);

        Assert.Equal(cell, DesignedPCellResolver.CellDirOf("cpcell:Thru2"));
        var declared = Assert.Single(PCellRegistry.DeclaredParameters("cpcell:Thru2")!);
        Assert.Equal(("Lmid", PCellDimension.Length, 1e-3, 6e-3), (declared.Name, declared.Dimension, declared.Minimum, declared.Maximum));
        Assert.Equal(3 * Mm, declared.Default!.Value.AsReal(), 12);

        Assert.True(PCellRegistry.TryGet("cpcell:Thru2", out var generator));
        double Span(double lmid)
        {
            var r = generator(new Dictionary<string, PCellValue> { ["Lmid"] = lmid }, ws.Tech, PCellLayerSelection.Default);
            Assert.Equal(["1", "2"], r.Pins.Select(p => p.Name));
            Assert.NotEmpty(r.Shapes);
            Assert.All(r.Shapes, s => Assert.IsNotType<LabelShape>(s));
            return Math.Abs(r.Pins[1].X - r.Pins[0].X) / (double)LayoutUnits.DefaultDbuPerMicron * 1e-6;
        }
        Assert.Equal(15 * Mm, Span(3 * Mm), 9);
        Assert.Equal(17 * Mm, Span(5 * Mm), 9);

        // Placed in a bench: the planner runs the generator, which runs the planner (PCD-0's nested-run risk).
        string bench = ws.Bench("Bench2", cell, "5");
        var outcome = LayoutFromSchematic.Run(bench);
        Assert.Null(outcome.Refusal);
        var layout = LayoutPersistence.LoadFromFile(outcome.LayoutPath);
        var x1 = Assert.Single(layout.Instances);
        var placed = CellLayoutResolver.Resolve(x1, Path.GetDirectoryName(outcome.LayoutPath)!, ws.Tech).View!;
        Assert.Equal("cpcell:Thru2", placed.PCellOrigin!.GeneratorId);
        Assert.Equal(5 * Mm, placed.PCellOrigin.Parameters["Lmid"].AsReal(), 12);
    }

    [Fact]
    public void R2_AnIdInTwoFoldersIsUnresolved_AndACellPlacingItselfIsRefused()
    {
        using var ws = new Pcd5Workspace();
        string first = ws.Designed("Twice2");
        string lib = Directory.CreateDirectory(Path.Combine(ws.Root, "lib")).FullName;
        string second = ws.Designed("Twice2", lib);
        string loop = ws.Designed("Loop2", edit: m => m.Components.Add(new EditableComponent
            { InstanceName = "X9", Symbol = SymbolKind.Generic, CellRef = "../../Loop2", X = 4000 }));
        using var registration = DesignedPCellResolver.Register(ws.Root);

        Assert.Null(DesignedPCellResolver.CellDirOf("cpcell:Twice2"));
        Assert.False(PCellRegistry.TryGet("cpcell:Twice2", out _));
        Assert.DoesNotContain("cpcell:Twice2", PCellRegistry.AllKnownGeneratorIds());
        string describe = DescribeOf(ws.Root);
        Assert.Contains(first, describe);
        Assert.Contains(second, describe);

        Assert.True(PCellRegistry.TryGet("cpcell:Loop2", out var generator));
        var e = Assert.Throws<InvalidOperationException>(() =>
            generator(new Dictionary<string, PCellValue>(), ws.Tech, PCellLayerSelection.Default));
        Assert.Contains("Loop2 places itself", e.Message);
        Assert.NotNull(PCellRegistry.GeneratorContentKey("cpcell:Loop2"));   // the key stops at the cycle too
    }

    [Fact]
    public void R3_ExportIsDeterministic_AndImportUnpacksOnceAndNeverOverwrites()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Pkg3");
        string a = Path.Combine(ws.Root, "out", "a.cpcell"), b = Path.Combine(ws.Root, "out", "b.cpcell");
        var exported = PCellPackage.Export(cell, a);
        Assert.Equal(a, exported.Path);
        PCellPackage.Export(cell, b);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));

        using (var zip = ZipFile.OpenRead(a))
            Assert.Equal(["Pkg3/.ccell", "Pkg3/schematic/Pkg3.csch", "Pkg3/symbol/Pkg3.csym", "manifest.json"],
                         zip.Entries.Select(e => e.FullName));
        var manifest = PCellPackage.ReadManifest(a, out string? refusal)!;
        Assert.Null(refusal);
        Assert.Equal(("Pkg3", "cpcell:Pkg3", "circuitrf.pcell"), (manifest.Name, manifest.GeneratorId, manifest.Format));
        Assert.Equal(["MLIN"], manifest.Generators);
        using (DesignedPCellResolver.Register(ws.Root))
            Assert.Equal(manifest.ContentKey, PCellRegistry.GeneratorContentKey("cpcell:Pkg3"));

        using var other = new Pcd5Workspace();
        var imported = PCellPackage.Import(a, other.Root);
        Assert.Equal(Path.Combine(other.Root, "Pkg3"), imported.Path);
        Assert.True(PCellDesign.IsDesigned(imported.Path));
        Assert.Contains(PCellPackage.Import(a, other.Root).Findings, f => f.Id == "pcell.already-imported");

        File.AppendAllText(Path.Combine(imported.Path!, "schematic", "Pkg3.csch"), " ");
        var refused = PCellPackage.Import(a, other.Root);
        Assert.Null(refused.Path);
        Assert.Contains(refused.Findings, f => f.Id == "pcell.import-exists" && f.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void R4_ThePackagedTerminalsPairTheBenchByLvs()
    {
        using var ws = new Pcd5Workspace();
        string package = Path.Combine(ws.Root, "Lvs4.cpcell");
        Assert.NotNull(PCellPackage.Export(ws.Designed("Lvs4"), package).Path);

        using var other = new Pcd5Workspace();
        string cell = PCellPackage.Import(package, other.Root).Path!;
        var ccell = CellFolder.ReadCcell(cell)!;
        Assert.Equal([(1, "1"), (2, "2")], ccell.Terminals!.Select(t => (t.Port, t.LayoutPin.Single())));
        Assert.Equal(2, ccell.NumPorts);

        Assert.Equal(TerminalMapOrigin.Declared, TerminalMap.ResolveCell(cell).Origin);

        // Every declared layout pin is a pin of the cell the generator draws at the bench, so LVS's declared map resolves.
        using var registration = DesignedPCellResolver.Register(other.Root);
        var outcome = LayoutFromSchematic.Run(other.Bench("Bench4", cell, "4"));
        Assert.Null(outcome.Refusal);
        var x1 = Assert.Single(LayoutPersistence.LoadFromFile(outcome.LayoutPath).Instances);
        var drawn = CellLayoutResolver.Resolve(x1, Path.GetDirectoryName(outcome.LayoutPath)!, other.Tech).View!;
        Assert.Equal(ccell.Terminals!.SelectMany(t => t.LayoutPin), drawn.Pins.Select(p => p.Name));
    }

    [Fact]
    public void R4_AnImportedPackagePlacedInABenchHasItsPinsInLvs()
    {
        using var ws = new Pcd5Workspace();
        string package = Path.Combine(ws.Root, "Lvs4b.cpcell");
        Assert.NotNull(PCellPackage.Export(ws.Designed("Lvs4b"), package).Path);
        using var other = new Pcd5Workspace();
        string cell = PCellPackage.Import(package, other.Root).Path!;

        using var registration = DesignedPCellResolver.Register(other.Root);
        string bench = other.Bench("Bench4b", cell, "4");
        Assert.Null(LayoutFromSchematic.Run(bench).Refusal);
        var lvs = CircuitRF.Design.Layout.Lvs.LvsRun.Run(Path.GetDirectoryName(Path.GetDirectoryName(bench))!);
        Assert.True(lvs.ErrorCount == 0, string.Join("\n", lvs.Findings.Select(f => f.Diagnostic)));
    }

    [Fact]
    public void R2_CheckTakesADesignedGeneratorAsOffered_AndStillWarnsForAnUnknownOne()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Offered2");
        string unknown = ws.Designed("Unknown2");
        string ccellPath = Path.Combine(unknown, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.LayoutGenerator = "cpcell:NoSuchCell2";
        CellPersistence.SaveToFile(ccellPath, ccell);

        Assert.DoesNotContain(TerminalMap.Validate(cell, cell, null, null), f => f.Id == "check.terminals.generator-not-registered");
        Assert.Contains(TerminalMap.Validate(unknown, unknown, null, null), f => f.Id == "check.terminals.generator-not-registered");
    }

    [Fact]
    public void R5_UpdateSchematicFromLayoutCreatesTheDesignedCellsComponent()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Back5");
        using var registration = DesignedPCellResolver.Register(ws.Root);
        string generated = GeneratedCellStore.GetOrCreate(ws.Root, "cpcell:Back5", new Dictionary<string, PCellValue> { ["Lmid"] = 5 * Mm },
                                                          ws.Tech, ws.TechPath, PCellLayerSelection.Default, out _);
        var bench = CellCreate.Create(ws.Root, "Bench5", CellViews.Schematic);
        string layoutDir = CellFolder.SubFolderPath(bench.CellDir, ViewType.Layout);
        var layout = new LayoutView();
        layout.Instances.Add(new LayoutInstance { CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(layoutDir, generated)) });
        var schematic = new SchematicEditModel { SchematicDirectory = Path.GetDirectoryName(bench.SchematicPath) };

        LayoutToSchematicGenerator.Run(layout, schematic, layoutDir, ws.Tech).Command!.Execute();

        var x = Assert.Single(schematic.Components);
        Assert.Equal(cell, Path.GetFullPath(Path.Combine(schematic.SchematicDirectory!, x.CellRef!)));
        var lmid = x.Parameters.Single(p => p.Name == "Lmid");
        Assert.True(SchematicLayoutPlanner.TryResolveSiValue(lmid.Expression, lmid.Unit, new Scope("t"), new Evaluator(), out double v, out _));
        Assert.Equal(5 * Mm, v, 9);
        Assert.Equal(x.InstanceName, layout.Instances[0].SchematicId);
    }

    [Fact]
    public void R6_TheCellIsALibraryTileInLayoutAndSchematic()
    {
        using var ws = new Pcd5Workspace();
        string package = Path.Combine(ws.Root, "Tile6.cpcell");
        PCellPackage.Export(ws.Designed("Tile6"), package);
        using var other = new Pcd5Workspace();
        string cell = PCellPackage.Import(package, other.Root).Path!;

        using var registration = DesignedPCellResolver.Register(other.Root);
        Assert.Contains("cpcell:Tile6", PCellRegistry.AllKnownGeneratorIds());            // the layout palette's list
        Assert.Equal(3 * Mm, PCellRegistry.DeclaredDefaults("cpcell:Tile6")!["Lmid"].AsReal(), 12);
        var symbol = CellSymbolResolver.ResolveCellDirOrRef(cell, other.Root).Symbol;      // the schematic's tile
        Assert.Equal(2, symbol!.Pins.Count);
    }

    [Fact]
    public void R7_TheCheckFindsWhatS1AndDNumbersForbid()
    {
        using var ws = new Pcd5Workspace();
        Assert.DoesNotContain(PCellDesignCheck.Run(ws.Designed("Clean7")), f => f.Severity == DiagnosticSeverity.Error);

        string bad = ws.Designed("Bad7", edit: m =>
        {
            var stray = new EditableComponent { InstanceName = "TL9", Symbol = SymbolKind.Mlin, X = 9000, Y = 9000 };
            foreach (var (n, e) in new[] { ("W", "1.1"), ("L", "2") })
                stray.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = "mm", Dimension = UnitDimension.Length });
            m.Components.Add(stray);
            var var = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var };
            var.Parameters.Add(new EditableParameter { Name = "Lmid", Expression = "3", Unit = "mm" });
            m.Components.Add(var);
        });
        string ccellPath = Path.Combine(bad, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Parameters.Add(new CcellParameter { Name = "Shape", DefaultExpression = "a", Choices = ["a", "b"] });
        CellPersistence.SaveToFile(ccellPath, ccell);

        var findings = PCellDesignCheck.Run(bad);
        Assert.Contains(findings, f => f.Id == "pcell.unjoined" && f.Render().Contains("TL9"));
        Assert.Contains(findings, f => f.Id == "pcell.parameter-is-var");
        Assert.Contains(findings, f => f.Id == "pcell.choices");
        Assert.Null(PCellPackage.Export(bad, Path.Combine(ws.Root, "bad.cpcell")).Path);   // Export runs the check first
    }

    private static string DescribeOf(string root)
    {
        // The registered resolver is the one PCellRegistry asks; Describe is read off it.
        // Snapshot the list under the registry's own gate: other tests register resolvers in parallel.
        const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var list = (System.Collections.IEnumerable)typeof(PCellRegistry).GetField("_resolvers", Static)!.GetValue(null)!;
        var gate = (System.Threading.Lock)typeof(PCellRegistry).GetField("_resolverGate", Static)!.GetValue(null)!;
        DesignedPCellResolver[] resolvers;
        lock (gate) resolvers = [.. list.OfType<DesignedPCellResolver>()];
        return string.Join("\n", resolvers.Select(r => r.Describe()).Where(d => d.Contains(root)));
    }
}
