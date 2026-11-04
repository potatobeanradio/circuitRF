using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.ModelComposer.VerilogA;
using CircuitRF.Ui.ViewModels;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-19 §6: Encapsulated netlist. Every linear element — a sub-cell's L, an SnP, a technology line —
/// goes into exactly one S-parameter block with the ComposedFET outside and exactly the D3 ports; a cell that is only a
/// FET has no block; the export is proved and an altered block fails the proof; a parameter reaching only the FET stays
/// the part's while one reaching the block is frozen, and a spread on it is refused; the library folder holds one
/// .sNp and nothing of the network, and two exports write the same block. Every part here is synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class EncapsulatedNetlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl19-" + Guid.NewGuid().ToString("N")[..12]);

    public EncapsulatedNetlistTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>A short plan: the block's size is not what is tested.</summary>
    private static readonly IReadOnlyList<CircuitRF.Core.Design.FrequencySpec> Plan =
        [new("0", "10", 21, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz")];

    private static ExportOptions Options(string lib) => new()
    {
        LibraryDir = lib, Encapsulate = true, Sweep = Plan,
    };

    // 1 — one block holds every linear element, the SnP and the technology line included; the FET stays outside; the
    // ports are the pins the block touches, then the nets where it meets the FET.
    [Fact]
    public void Partition_PutsEveryLinearElementInOneBlock_AndTheFetOutside()
    {
        string cell = WriteAmplifier(Cmodel());
        var plan = ModelLibraryExport.Plan(cell, Options(Path.Combine(_dir, "lib")));
        Assert.True(plan.Refusals.Count == 0, string.Join(" | ", plan.Refusals));

        var p = ProtectPartition.Partition(ModelNetlist.FromCell(cell, out _)!, "TSTPA200",
            SchematicCircuit.ReferenceBaseOfDirectory(CellFolder.SubFolderPath(cell, ViewType.Schematic)), ProtectRule.Encapsulate);
        Assert.Equal(["C1", "R1", "SNP1", "TL1", "X1.L1"], p.Inside.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["Q1"], p.Outside.Select(e => e.Path));
        // in and out, then Q1's gate and drain nets; its source is on ground, the reference.
        Assert.Equal(4, plan.BlockPorts);
        Assert.Equal(2, p.Joins.Count);
        Assert.Contains("Port 1: pin in.", plan.ProtectLines);
        Assert.Contains("Port 2: pin out.", plan.ProtectLines);
        Assert.Contains(plan.ProtectLines, l => l.StartsWith("Port 3: joins Q1 (", StringComparison.Ordinal));
        Assert.Contains("4 ports × 21 points", plan.BlockLine);
    }

    // 2 — a cell that is only a ComposedFET has no block: it exports, and says so.
    [Fact]
    public void AFetOnlyCell_ExportsWithNoBlock_AndSaysSo()
    {
        string cell = WriteFetOnly(Cmodel());
        var options = Options(Path.Combine(_dir, "lib"));
        var plan = ModelLibraryExport.Plan(cell, options);
        Assert.True(plan.Refusals.Count == 0, string.Join(" | ", plan.Refusals));
        Assert.Equal(0, plan.BlockPorts);
        Assert.Contains(ModelLibraryExport.NothingLinearLine, plan.ProtectLines);

        var result = ModelLibraryExport.Export(cell, options);
        Assert.True(result.Succeeded, result.Refusal);
        Assert.DoesNotContain(Directory.EnumerateFiles(result.ModelDir!, "*", SearchOption.AllDirectories),
                              f => Path.GetExtension(f).StartsWith(".s", StringComparison.Ordinal) && f.EndsWith("p", StringComparison.Ordinal));
    }

    // 3, 4, 5 — the export: proved; one .sNp and nothing of the network in the folder, which checks clean; the
    // parameter reaching only the FET stays the part's and moves it, the one reaching the block is frozen and gone; two
    // exports give the same block bytes; and a block with one entry altered fails the proof.
    [Fact]
    public void Export_IsProved_ShipsOnlyTheBlock_FreezesWhatReachesIt_AndAnAlteredBlockFails()
    {
        string cell = WriteAmplifier(Cmodel());
        string lib = Path.Combine(_dir, "lib");
        var result = ModelLibraryExport.Export(cell, Options(lib));
        Assert.True(result.Succeeded, result.Refusal);
        Assert.Contains("The encapsulated part matches the cell", result.Message);
        string model = result.ModelDir!;

        var files = Directory.EnumerateFiles(model, "*", SearchOption.AllDirectories)
                             .Select(f => Path.GetRelativePath(model, f).Replace('\\', '/')).ToList();
        string block = Assert.Single(files, f => f.StartsWith("models/", StringComparison.Ordinal) && f.EndsWith(".s4p", StringComparison.Ordinal));
        Assert.Equal("models/TSTPA200.s4p", block);
        Assert.DoesNotContain(files, f => f.EndsWith(".s2p", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(model, ModelFolder.CellsFolder)));
        string cnl = File.ReadAllText(ModelNetlist.PathOf(model));
        Assert.Contains("SnP:ENC1", cnl);
        foreach (string gone in new[] { "R1", "C1", "TL1", "SNP1", "L1", "Lp", "X1", "line" })
            Assert.DoesNotMatch($@"\b{gone}\b", cnl);
        Assert.DoesNotContain(ModelFolderCheck.Validate(model), d => d.Severity == DiagnosticSeverity.Error);

        var (part, _) = ModelPartManifest.Read(Path.Combine(model, ModelFolder.PartFileName));
        Assert.Equal(ModelLibraryExport.ProtectionEncapsulated, part!.Protection);
        Assert.Equal(["Nf"], part.Parameters.Select(p => p.Name));
        Assert.NotEqual(PlacedS21(model, "Nf=1"), PlacedS21(model, "Nf=2"));

        // Two exports of one cell and plan: the same block, byte for byte.
        var again = ModelLibraryExport.Export(cell, Options(Path.Combine(_dir, "lib2")));
        Assert.True(again.Succeeded, again.Refusal);
        Assert.Equal(File.ReadAllBytes(Path.Combine(model, block)), File.ReadAllBytes(Path.Combine(again.ModelDir!, block)));

        // One entry altered: S11 at the middle sampled point (a proof frequency) — the frequency line's second word.
        string path = Path.Combine(model, block);
        var lines = File.ReadAllLines(path).ToList();
        int row = lines.FindIndex(l => l.TrimStart().StartsWith("5000000000", StringComparison.Ordinal));
        var words = lines[row].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        words[1] = (double.Parse(words[1], System.Globalization.CultureInfo.InvariantCulture) * 1.01).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        lines[row] = string.Join(" ", words);
        File.WriteAllLines(path, lines);

        string srcText = ModelNetlist.FromCell(cell, out _)!;
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(CellFolder.SubFolderPath(cell, ViewType.Schematic));
        var proof = ProtectCheck.Run(srcText, "TSTPA200", srcBase, cnl, "TSTPA200", model, part.Pins,
                                     ProtectBias.Defaults(part.Pins).Take(1).ToList(), [5e9], thermal: false, subject: "encapsulated");
        Assert.False(proof.Passed);
        Assert.Contains("The encapsulated part does not match the cell", proof.Refusal);
    }

    // 4 — a spread on a parameter the block reaches is refused with D5's sentence.
    [Fact]
    public void ASpreadOnAFrozenParameter_IsRefused()
    {
        string cell = WriteAmplifier(Cmodel(), lp: "gauss(1,0.1)");
        var plan = ModelLibraryExport.Plan(cell, Options(Path.Combine(_dir, "lib")));
        Assert.Contains("Lp has a spread and reaches the S-parameter block, which is sampled at one value. " +
                        "Make it reach only a device, or use Compile netlist.", plan.Refusals);
        Assert.Equal(["Lp"], plan.FrozenParameters);
    }

    // 6 — the dialog: protection off with Encapsulated preselected; without a compiler Compile netlist is greyed and
    // Encapsulated is not.
    [Fact]
    public void Dialog_DefaultsToOff_WithEncapsulatedPreselected_AndCompileGreyedWithoutACompiler()
    {
        string cell = WriteFetOnly(Cmodel());
        var vm = new ExportToLibraryViewModel(cell, [], initialLibrary: null);
        Assert.False(vm.ProtectModel);
        Assert.True(vm.EncapsulateChosen);
        Assert.False(vm.IsProtectChoiceEnabled);

        vm.ProtectModel = true;
        vm.Apply(new ExportPlan { CellDir = cell, PartNumber = "TSTFET100", ModelDir = Path.Combine(_dir, "lib", "TSTFET100"),
                                  CompileNetlistUnavailable = "No Verilog-A compiler is found." }, null);
        Assert.True(vm.IsProtectChoiceEnabled);
        Assert.False(vm.IsCompileNetlistEnabled);
        Assert.Equal("No Verilog-A compiler is found.", vm.CompileNetlistNote);
        Assert.True(vm.Options(replace: false).Encapsulate);
        Assert.False(vm.Options(replace: false).CompileNetlist);
    }

    // R-vl19-2d — over MCP: create what=libraryPart's `protect` key maps to --protect, and the CLI it runs publishes the
    // encapsulated part.
    [Fact]
    public void OverMcp_ProtectEncapsulate_PublishesTheBlock()
    {
        string cell = WriteAmplifier(Cmodel());
        var root = CircuitRF.Cli.Serve.PathRoot.Open(_dir, out _)!;
        var call = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            what = "libraryPart", cell, library = Path.Combine(_dir, "lib"), protect = "encapsulate",
            sweep = new[] { "start=0 stop=10 npts=21 unit=GHz" },
        }))!.AsObject();
        var argv = CircuitRF.Cli.Serve.ToolCatalog.ToArgv("create", call, root, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(["--protect", "encapsulate"], argv!.SkipWhile(a => a != "--protect").Take(2));

        var (exit, _, err) = RunCliPublic(argv!);
        Assert.True(exit == 0, err);
        Assert.True(File.Exists(Path.Combine(_dir, "lib", "TSTPA200", "models", "TSTPA200.s4p")), err);
        Assert.Contains("The encapsulated part matches the cell", err);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    private static string Cmodel() => ComposedTestModels.WriteTemp(VaTestModels.AnnModel(AnnActivation.Tanh), "tstfet.cmodel");

    /// <summary>The placed model's S21 at 3 GHz, with one override on its instance.</summary>
    private static Complex PlacedS21(string model, string overrides)
    {
        string text = File.ReadAllText(ModelNetlist.PathOf(model))
                    + "\nPort:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\nTSTPA200:XM a b " + overrides + "\n";
        var (lib, tb) = new CnlReader().Read(text, "placed", model);
        using var nl = new Elaborator(lib) { BaseDirectory = model }.Elaborate(tb);
        return SParameterEngine.Run(nl, [3e9]).Cubes["S"].ComplexValues[2];
    }

    private static EditableComponent Fet(string name, string cmodel, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.ComposedFET, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "File", Expression = cmodel });
        c.Parameters.Add(new EditableParameter { Name = "N", Expression = "Nf" });
        return c;
    }

    private string WriteFetOnly(string cmodel)
    {
        string ws = WorkspaceCreate.Create(_dir, "ws-fet", null).WorkspaceDir;
        var sch = new SchematicEditModel();
        var g = Pin("P1", 1, "g", 0, 0);
        var d = Pin("P2", 2, "d", 6000, -2000);
        var q = Fet("Q1", cmodel, 3000, 0);
        var gnd = new EditableComponent { InstanceName = "G1", Symbol = SymbolKind.Ground, X = 3000, Y = 4000 };
        foreach (var c in new[] { g, d, q, gnd }) sch.Components.Add(c);
        Wire(sch, g, 0, q, 0);
        Wire(sch, q, 1, d, 0);
        Wire(sch, q, 2, gnd, 0);
        string cell = WriteCell(ws, "TSTFET100", sch);
        SetParameters(cell, ("Nf", "1", ""));
        return cell;
    }

    /// <summary>
    /// TSTPA200: in → R1 → Q1's gate; Q1's drain → C1 to ground and → X1 (a sub-cell holding L1 = Ls, placed with
    /// Ls = 2*Lp) → SNP1 → TL1 (a microstrip line on the workspace's technology) → out; Q1's source on ground, its finger
    /// count the cell parameter Nf.
    /// </summary>
    private string WriteAmplifier(string cmodel, string lp = "1")
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", ShippedTechnologies.DefaultId).WorkspaceDir;
        string ext = Path.Combine(_dir, "ext");
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "line.s2p"),
            "! synthetic line\n# GHz S RI R 50\n0.001 0.1 0 0.9 0 0.9 0 0.1 0\n20 0.2 0.1 0.7 -0.3 0.7 -0.3 0.2 0.1\n");

        var sub = new SchematicEditModel();
        sub.Components.Add(Pin("P1", 1, "a", 0, 0));
        sub.Components.Add(Pin("P2", 2, "b", 4000, 0));
        sub.Components.Add(Part("L1", SymbolKind.Inductor, 2000, 2000, "L", "Ls", ""));
        Wire(sub, sub.Components[0], 0, sub.Components[2], 0);
        Wire(sub, sub.Components[2], 1, sub.Components[1], 0);
        string subCell = WriteCell(ws, "TSTSUB", sub);
        SetParameters(subCell, ("Ls", "3", "nH"));
        CircuitRF.Design.Symbol.SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(subCell, ViewType.Symbol), "TSTSUB.csym"),
                                     AutoSymbolGenerator.Generate("TSTSUB", 2));

        var sch = new SchematicEditModel();
        string schDir = Path.Combine(ws, "TSTPA200", "schematic");
        Directory.CreateDirectory(schDir);
        sch.SchematicDirectory = schDir;
        var p1 = Pin("P1", 1, "in", 0, 0);
        var r1 = Part("R1", SymbolKind.Resistor, 2000, 2000, "R", "20", "Ohm");
        var q1 = Fet("Q1", cmodel, 6000, 2000);
        var g1 = new EditableComponent { InstanceName = "G1", Symbol = SymbolKind.Ground, X = 6000, Y = 8000 };
        var c1 = Part("C1", SymbolKind.Capacitor, 10000, 6000, "C", "1", "pF");
        var g2 = new EditableComponent { InstanceName = "G2", Symbol = SymbolKind.Ground, X = 10000, Y = 9000 };
        var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../TSTSUB", X = 14000, Y = 2000 };
        x1.Parameters.Add(new EditableParameter { Name = "Ls", Expression = "2*Lp" });
        var s1 = Snp("SNP1", "../ext/line.s2p", 20000, 2000);
        var tl1 = Part("TL1", SymbolKind.Mlin, 26000, 2000, "W", "1.1", "mm");
        tl1.Parameters.Add(new EditableParameter { Name = "L", Expression = "8", Unit = "mm" });
        var p2 = Pin("P2", 2, "out", 32000, 0);
        foreach (var c in new[] { p1, r1, q1, g1, c1, g2, x1, s1, tl1, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, r1, 0);
        Wire(sch, r1, 1, q1, 0);
        Wire(sch, q1, 2, g1, 0);
        Wire(sch, q1, 1, c1, 0);
        Wire(sch, c1, 1, g2, 0);
        Wire(sch, q1, 1, x1, 0);
        Wire(sch, x1, 1, s1, 0);
        Wire(sch, s1, 1, tl1, 0);
        Wire(sch, tl1, 1, p2, 0);
        string cell = WriteCell(ws, "TSTPA200", sch);
        SetParameters(cell, ("Lp", lp, "nH"), ("Nf", "1", ""));
        return cell;
    }

    private static void SetParameters(string cell, params (string Name, string Default, string Unit)[] ps)
    {
        string path = Path.Combine(cell, CellFolder.CcellFileName);
        var ccell = File.Exists(path) ? CellPersistence.LoadFromFile(path) : new CcellFile();
        foreach (var (n, d, u) in ps) ccell.Parameters.Add(new CcellParameter { Name = n, DefaultExpression = d, Unit = u });
        CellPersistence.SaveToFile(path, ccell);
        CellStat.InvalidateCache();
    }
}
