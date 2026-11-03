using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-9 §5: model folders in a vendor library — Add's classifier, the index's counters, value search
/// across both kinds, a placed model running as the same cell placed locally, one netlist read per part, a missing
/// library refused by name and drawn as its stored glyph, and D1's gate. Every library is synthetic.
/// </summary>
public sealed class VendorModelLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl9-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _lib;

    public VendorModelLibraryTests()
    {
        _lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(_lib);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — a folder of model folders is a vendor library, however many cell files they hold.
    [Fact]
    public void Classify_CallsAFolderOfModelFolders_AVendorLibrary_NotAKit()
    {
        WriteRlcModel(Path.Combine(_lib, "Passives"), "TSTRLC100");
        WriteRlcModel(Path.Combine(_lib, "Passives"), "TSTRLC200");
        File.WriteAllText(Path.Combine(_lib, "Passives", "TSTRLC100", "models", "a.va"), "module a(p); endmodule\n");

        var (kind, build) = VendorLibraryRefs.Classify(_lib);
        Assert.Equal(VendorLibraryRefs.FolderKind.VendorLibrary, kind);
        Assert.Equal(2, build!.Entries.Count(e => e.IsModel));
    }

    // 2 — an index build reads one part.cpart per model folder and nothing else of it; a .lib inside models/ is no part.
    [Fact]
    public void Index_ReadsOnePartCpartPerModelFolder_AndNoModelFile()
    {
        string cat = Path.Combine(_lib, "Passives");
        var dirs = new[] { "TSTRLC100", "TSTRLC200", "TSTRLC300" }.Select(p => WriteRlcModel(cat, p)).ToList();
        File.WriteAllText(Path.Combine(dirs[0], "models", "own.lib"), ".subckt TSTOWN 1 2\nR1 1 2 1\n.ends TSTOWN\n");
        Directory.CreateDirectory(Path.Combine(cat, "TSTBAD"));
        File.WriteAllText(Path.Combine(cat, "TSTBAD", ModelFolder.PartFileName), "{");

        long manifests = VendorLibraryIndex.ManifestReads(_lib), files = VendorLibraryIndex.FileReads(_lib);
        var build = VendorLibraryIndex.Build(_lib);

        Assert.Equal(4, VendorLibraryIndex.ManifestReads(_lib) - manifests);   // three good, one unreadable
        Assert.Equal(0, VendorLibraryIndex.FileReads(_lib) - files);           // models/own.lib never scanned
        Assert.All(dirs, d => Assert.Equal(0, ModelNetlist.Reads(d)));         // no .cnl opened
        Assert.Equal(3, build.Entries.Count);
        Assert.DoesNotContain(build.Entries, e => e.Part == "TSTOWN");
        Assert.Contains(build.SummaryLines(), l => l.Contains("Passives/TSTBAD", StringComparison.Ordinal));
    }

    // 3 — value-aware search finds a passive model by its Value, beside a SPICE part of the same value.
    [Fact]
    public void Search_FindsAPassiveModelByValue_BesideASpicePart()
    {
        WriteRlcModel(Path.Combine(_lib, "Caps"), "TSTCAP0402", value: "10 pF");
        File.WriteAllText(Path.Combine(_lib, "Caps", "series.lib"), """
            * Synthetic test library. Not a real product.
            * Description : 0402 / 10[pF]
            * Property : C = 10[pF]
            .subckt TSTSPICE10P 1 2
            C1 1 2 10p
            .ends TSTSPICE10P
            """);

        var hits = VendorLibraryIndex.Build(_lib).Entries.Where(e => VendorLibrarySearch.Match(e, "10p")).ToList();
        Assert.Equal(["TSTCAP0402", "TSTSPICE10P"], hits.Select(e => e.Part).Order(StringComparer.Ordinal));
        Assert.Contains(hits, e => e.IsModel);
    }

    // 4 — a model placed from a library gives the S-parameters of the same cell placed from the workspace.
    [Fact]
    public void AModelFromALibrary_RunsAsTheSameCellPlacedLocally()
    {
        string model = WriteRlcModel(Path.Combine(_lib, "Passives"), "TSTRLC100");
        string ws = WorkspaceCreate.Create(_dir, "ws", null, vendorLibraries: [("Lib", _lib)]).WorkspaceDir;
        string local = CopyCell(model, Path.Combine(ws, "TSTRLC100"));
        string schDir = SchematicDirOf(ws);

        double[] freqs = [1e8, 1e9, 5e9];
        var fromLibrary = Run(schDir, Instance(VendorModelRefs.RefFor("Lib", "Passives/TSTRLC100")), freqs);
        var fromCell    = Run(schDir, Instance(Path.GetRelativePath(schDir, local).Replace('\\', '/')), freqs);

        var a = fromLibrary.Cubes["S"].ComplexValues;
        var b = fromCell.Cubes["S"].ComplexValues;
        Assert.Equal(b.Length, a.Length);
        Assert.Contains(a, v => v.Magnitude > 1e-3 && v.Magnitude < 1 - 1e-3);
        for (int i = 0; i < a.Length; i++) Assert.True((a[i] - b[i]).Magnitude <= 1e-12, $"S[{i}]: {a[i]} vs {b[i]}");
    }

    // 5 — two instances of one part read its .cnl once.
    [Fact]
    public void TwoInstancesOfOnePart_ReadItsNetlistOnce()
    {
        string model = WriteRlcModel(Path.Combine(_lib, "Passives"), "TSTRLC100");
        string ws = WorkspaceCreate.Create(_dir, "ws", null, vendorLibraries: [("Lib", _lib)]).WorkspaceDir;
        string reference = VendorModelRefs.RefFor("Lib", "Passives/TSTRLC100");

        long before = ModelNetlist.Reads(model);
        var r = NetExtractor.Extract(Placed(SchematicDirOf(ws), Instance(reference, "X1", 0), Instance(reference, "X2", 3000)),
                                     "tb", DiskCellResolver.Instance);
        Assert.Empty(r.Conflicts);
        Assert.Equal(1, ModelNetlist.Reads(model) - before);
        Assert.Equal(2, r.TestBench.Instances.Count(i => i.Reference == "TSTRLC100"));
    }

    // 6 — a missing library is a refusal naming the part, and the instance draws its stored glyph as NotFound.
    [Fact]
    public void AMissingLibrary_IsARefusalNamingThePart_AndDrawsTheStoredGlyph()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null, vendorLibraries: [("Lib", Path.Combine(_dir, "gone"))]).WorkspaceDir;
        string schDir = SchematicDirOf(ws);
        var comp = Instance(VendorModelRefs.RefFor("Lib", "Caps/TSTCAP0402"));
        comp.Parameters.Add(new EditableParameter { Name = VendorModelRefs.GlyphParameter, Expression = "C" });

        var r = NetExtractor.Extract(Placed(schDir, comp), "tb", DiskCellResolver.Instance);
        string refusal = Assert.Single(r.PartRefusals);
        Assert.Contains("TSTCAP0402", refusal);
        Assert.Contains("'Lib'", refusal);
        Assert.NotNull(r.RunRefusal);

        var drawn = CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, schDir);
        Assert.Equal(CellSymbolState.NotFound, drawn.State);
        Assert.Equal(2, drawn.Symbol!.Pins.Count);
    }

    // 7 — a .cnl edited by hand away from its .csch is check's error (D1).
    [Fact]
    public void ANetlistEditedAwayFromItsSchematic_IsACheckError()
    {
        string model = WriteRlcModel(Path.Combine(_lib, "Passives"), "TSTRLC100");
        Assert.Empty(ModelFolderCheck.Validate(model));

        string cnl = ModelNetlist.PathOf(model);
        File.WriteAllText(cnl, File.ReadAllText(cnl).Replace("C=2 pF", "C=3 pF"));
        Assert.Equal("check.model.netlist-not-schematic", Assert.Single(ModelFolderCheck.Validate(model)).Id);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-port series R–L–C cell with a parameter, written as Export would write it: its schematic, its symbol,
    /// its <c>.ccell</c>, the <c>.cnl</c> <see cref="ModelNetlist.FromCell"/> extracts, and a <c>part.cpart</c>.
    /// </summary>
    private static string WriteRlcModel(string category, string part, string value = "")
    {
        Directory.CreateDirectory(category);
        string dir = CellFolder.CreateCellFolder(category, part);
        Directory.CreateDirectory(Path.Combine(dir, ModelFolder.ModelsFolder));
        CellPersistence.SaveToFile(Path.Combine(dir, CellFolder.CcellFileName), new CcellFile
        {
            Parameters = [new CcellParameter { Name = "Rs", DefaultExpression = "10", Unit = "Ohm" }],
        });
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(dir, ViewType.Symbol), part + ".csym"),
                                     AutoSymbolGenerator.Generate(part, 2));

        // Pins at the chain's two ends; R, L and C end to end (a vertical two-terminal is ±200).
        var sch = new SchematicEditModel();
        sch.Components.Add(Pin("P1", 1, -100, -200));
        sch.Components.Add(Part("R1", SymbolKind.Resistor, 0, 0, "R", "Rs", ""));
        sch.Components.Add(Part("L1", SymbolKind.Inductor, 0, 400, "L", "3", "nH"));
        sch.Components.Add(Part("C1", SymbolKind.Capacitor, 0, 800, "C", "2", "pF"));
        sch.Components.Add(Pin("P2", 2, -100, 1000));
        SchematicPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(dir, ViewType.Schematic), part + ".csch"), sch);

        string? cnl = ModelNetlist.FromCell(dir, out string? why);
        Assert.True(cnl is not null, why);
        File.WriteAllText(ModelNetlist.PathOf(dir), cnl);

        var (lib, _) = new CircuitRF.Core.Netlist.CnlReader().Read(cnl!, "m", null);
        ModelPartManifest.Write(Path.Combine(dir, ModelFolder.PartFileName), new ModelPartManifest
        {
            Part = part, Cell = part, Kind = ModelPartManifest.KindPassive, Description = "Synthetic series RLC",
            Pins = [.. lib.Find(part)!.Ports], Parameters = [new ModelPartParameter { Name = "Rs", Default = "10 Ohm" }],
            Value = value,
        });
        return dir;
    }

    private static EditableComponent Pin(string name, int num, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static EditableComponent Part(string name, SymbolKind kind, double x, double y, string p, string v, string unit)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = p, Expression = v, Unit = unit });
        return c;
    }

    private static EditableComponent Instance(string cellRef, string name = "X1", double x = 0)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Generic, CellRef = cellRef, X = x, Y = 0 };
        c.Parameters.Add(new EditableParameter { Name = "Rs", Expression = "20", Unit = "Ohm" });
        return c;
    }

    private static string CopyCell(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            if (rel == ModelFolder.PartFileName || rel.EndsWith(".cnl", StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel))!);
            File.Copy(f, Path.Combine(to, rel));
        }
        return to;
    }

    private static string SchematicDirOf(string ws)
    {
        string d = Path.Combine(ws, "top", "schematic");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The instances with a 50 Ω termination on every pin of each.</summary>
    private static SchematicEditModel Placed(string schDir, params EditableComponent[] comps)
    {
        var model = new SchematicEditModel { SchematicDirectory = schDir };
        int port = 0;
        foreach (var comp in comps)
        {
            model.Components.Add(comp);
            var res = CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, schDir);
            if (res.Symbol is null) continue;
            foreach (var pin in res.Symbol.Pins.OrderBy(p => p.PortIndex))
            {
                double px = pin.LocalX + comp.X, py = pin.LocalY + comp.Y;
                port++;
                var term = new EditableComponent { InstanceName = $"P{port}", Symbol = SymbolKind.Term, X = px, Y = py + 200 };
                term.Parameters.Add(new EditableParameter { Name = "Num", Expression = port.ToString() });
                term.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
                model.Components.Add(term);
                model.Components.Add(new EditableComponent { InstanceName = $"G{port}", Symbol = SymbolKind.Ground, X = px, Y = py + 400 });
            }
        }
        return model;
    }

    private static DataSet Run(string schDir, EditableComponent comp, double[] freqs)
    {
        var r = NetExtractor.Extract(Placed(schDir, comp), "tb", DiskCellResolver.Instance);
        Assert.Empty(r.Conflicts);
        return SParameterEngine.Run(new Elaborator(r.Library).Elaborate(r.TestBench), freqs);
    }
}
