using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Tests.Composed;
using CircuitRF.Ui.Tests.ModelComposer.VerilogA;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-16 §6: Compile netlist. A cell that is only a composed FET is the FET's own module; a mixed cell
/// partitions by the table, adds only the joining nets as terminals and keeps a sub-cell's parameter as an expression of
/// the cell's; each refusal names what it refuses; the source is deterministic; and with a compiler installed the export
/// proves the compiled part against the cell, ships no topology, and a corrupted build fails the proof. Every part here is
/// synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class CompileNetlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl16-" + Guid.NewGuid().ToString("N")[..12]);

    public CompileNetlistTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — a cell holding only a composed FET compiles to exactly the standalone module (R-vl16-1c).
    [Fact]
    public void AFetOnlyCell_IsTheFetsOwnStandaloneModule()
    {
        string cmodel = ComposedTestModels.WriteTemp(VaTestModels.AnnModel(AnnActivation.Tanh), "tstfet.cmodel");
        string cell = WriteFetOnlyCell(cmodel);

        var plan = Partition(cell);
        var module = NetlistVerilogA.Write(plan, "TSTFET100");

        Assert.Equal(CmodelVerilogA.Write(CmodelFile.Read(cmodel), new VerilogAOptions { ModuleName = "TSTFET100", SourceName = "tstfet.cmodel" }),
                     module.Text);
        Assert.Equal(["g", "d", "0"], module.Nets);
    }

    // 2 — the partition: each element on its side, exactly the joining net added, a sub-cell parameter kept as an
    // expression of the cell's.
    [Fact]
    public void AMixedCell_PartitionsByTheTable_AndKeepsASubCellParameterAsAnExpression()
    {
        string cmodel = ComposedTestModels.WriteTemp(VaTestModels.AnnModel(AnnActivation.Tanh), "tstfet.cmodel");
        string cell = WriteAmplifier(cmodel);

        var plan = Partition(cell);
        Assert.Empty(plan.Refusals);
        Assert.Equal(["C1", "Q1", "R1", "X1.L1"], plan.Inside.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["SNP1"], plan.Outside.Select(e => e.Path));
        string join = Assert.Single(plan.Joins);
        Assert.Equal(plan.Outside[0].Nets[0], join);

        var module = NetlistVerilogA.Write(plan, "TSTPA200");
        // out touches nothing inside, so it is no terminal; ground is the last, tied to node 0 by the wrapper.
        Assert.Equal(["in", join, "0"], module.Nets);
        Assert.Contains("parameter real Lp = 1e-09;", module.Text);
        Assert.Contains("localparam real crf_k1 = (2.0 * Lp);", module.Text);
        Assert.Contains("crf_u1_", module.Text);
    }

    // 3 — the refusals: a table constituent quotes the writer's sentence; a spread inside an element value names the
    // value; an unknown type is named.
    [Fact]
    public void Refusals_NameTheTable_TheSpreadValue_AndTheUnknownType()
    {
        var m = RuntimeTestModels.MixedModel();
        string table = ComposedTestModels.WriteTemp(m with
        {
            Core = m.Core with { IdRF = Constituent.Table("Id", VaTestModels.Inputs, RuntimeTestModels.Pentagon), Dispersion = new ModelDispersion(1e-6) },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double>(m.Extrapolation!.Gx) { ["IdRF"] = 2.0 }),
        }, "table.cmodel");
        string cell = WriteAmplifier(table, rValue: "gauss(20,1)", diode: true);

        var plan = Partition(cell);
        Assert.Contains(plan.Refusals, r => r.Contains("D1 is a Diode", StringComparison.Ordinal));
        var ex = Assert.Throws<ProtectRefusedException>(() => NetlistVerilogA.Write(plan, "TSTPA200"));
        Assert.Contains(ex.Refusals, r => r.Contains("'Table' cannot be written as Verilog-A; refit Id as an ANN or an equation", StringComparison.Ordinal));
        Assert.Contains(ex.Refusals, r => r.Contains("Make gauss(20,1) a cell parameter to keep its spread.", StringComparison.Ordinal));
    }

    // 4 — two plans of one cell give the same source bytes and one CompiledFrom.
    [Fact]
    public void TheSource_IsDeterministic()
    {
        string cmodel = ComposedTestModels.WriteTemp(VaTestModels.AnnModel(AnnActivation.Tanh), "tstfet.cmodel");
        string cell = WriteAmplifier(cmodel);
        var options = new ExportOptions { LibraryDir = Path.Combine(_dir, "lib"), CompileNetlist = true };
        var a = ModelLibraryExport.Plan(cell, options).Protect!;
        var b = ModelLibraryExport.Plan(cell, options).Protect!;
        Assert.Equal(a.Module!.Text, b.Module!.Text);
        Assert.StartsWith("sha256:", a.Hash);
        Assert.Equal(a.Hash, b.Hash);
    }

    // 5, 6 — with a compiler: the export proves the part and writes a library folder holding no topology, which checks
    // clean; and a build compiled from a source with one coefficient changed fails the proof.
    [CompilerFact]
    public void Export_ProvesTheCompiledPart_ShipsNoTopology_AndACorruptedBuildFailsTheProof()
    {
        ProtectedModelTests.EnsureWorkerBeside(AppContext.BaseDirectory);
        string cmodel = ComposedTestModels.WriteTemp(VaTestModels.AnnModel(AnnActivation.Tanh), "tstfet.cmodel");
        string cell = WriteAmplifier(cmodel);
        string lib = Path.Combine(_dir, "lib");

        var result = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = lib, CompileNetlist = true });
        Assert.True(result.Succeeded, result.Refusal);
        string model = result.ModelDir!;

        // §6.6: no source, no .cmodel, no original schematic beyond the wrapper, no sub-cell folder.
        var files = Directory.EnumerateFiles(model, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(model, f).Replace('\\', '/')).ToList();
        Assert.DoesNotContain(files, f => f.EndsWith(".va", StringComparison.Ordinal) || f.EndsWith(".cmodel", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(model, ModelFolder.CellsFolder)));
        Assert.Contains($"models/{OsdiBuilds.CurrentRid}/TSTPA200.osdi", files);
        Assert.DoesNotContain(ModelFolderCheck.Validate(model), d => d.Severity == DiagnosticSeverity.Error);
        string source = Path.Combine(Path.GetDirectoryName(cell)!, ModelLibraryExport.ProtectedSourceFolder, "TSTPA200", "TSTPA200.va");
        Assert.Equal(source, result.SourcePath);
        Assert.True(File.Exists(source));

        var (part, _) = ModelPartManifest.Read(Path.Combine(model, ModelFolder.PartFileName));
        Assert.Equal(result.CompiledFrom, part!.CompiledFrom);
        Assert.Equal([OsdiBuilds.CurrentRid], part.Proved);
        Assert.Equal(OsdiBuilds.Rids.Count, result.Builds.Count);
        Assert.All(result.Builds.Where(b => b.State == ExportBuild.NotBuilt), b => Assert.NotEqual("", b.Reason));

        // §6.5: one coefficient changed (R1's 20 Ohm), compiled over the shipped build, and the proof refuses it.
        string corrupt = Path.Combine(_dir, "corrupt.va");
        File.WriteAllText(corrupt, File.ReadAllText(source).Replace(")/20.0;", ")/20.2;"));
        string build = OsdiBuilds.PlatformPath(Path.Combine(model, ModelFolder.ModelsFolder, "TSTPA200.osdi"), OsdiBuilds.CurrentRid);
        VerilogASourceCompiler.Build(corrupt, build, target: null);
        ExternalDeviceRegistry.ResetResolved();

        string srcText = ModelNetlist.FromCell(cell, out _)!;
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(CellFolder.SubFolderPath(cell, ViewType.Schematic));
        var proof = ProtectCheck.Run(srcText, "TSTPA200", srcBase, File.ReadAllText(ModelNetlist.PathOf(model)), "TSTPA200", model,
                                     part.Pins, ProtectBias.Defaults(part.Pins).Take(1).ToList(), ProtectCheck.Frequencies(null), thermal: false);
        Assert.False(proof.Passed);
        Assert.Contains("does not match the cell", proof.Refusal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    private static ProtectPlan Partition(string cell)
    {
        string text = ModelNetlist.FromCell(cell, out string? why) ?? throw new InvalidOperationException(why);
        string? baseDir = SchematicCircuit.ReferenceBaseOfDirectory(CellFolder.SubFolderPath(cell, ViewType.Schematic));
        return ProtectPartition.Partition(text, Path.GetFileName(cell), baseDir);
    }

    private static EditableComponent Fet(string name, string cmodel, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.ComposedFET, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "File", Expression = cmodel });
        return c;
    }

    private string WriteFetOnlyCell(string cmodel)
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
        return WriteCell(ws, "TSTFET100", sch);
    }

    /// <summary>
    /// TSTPA200: in → R1 → Q1's gate; Q1's drain → C1 to ground and → X1 (a sub-cell holding L1 = Ls, placed with
    /// Ls = 2*Lp) → SNP1 → out; Q1's source on ground. The cell parameter Lp is 1 nH.
    /// </summary>
    private string WriteAmplifier(string cmodel, string rValue = "20", bool diode = false)
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
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
        var r1 = Part("R1", SymbolKind.Resistor, 2000, 2000, "R", rValue, rValue.Contains('(') ? "" : "Ohm");
        var q1 = Fet("Q1", cmodel, 6000, 2000);
        var g1 = new EditableComponent { InstanceName = "G1", Symbol = SymbolKind.Ground, X = 6000, Y = 8000 };
        var c1 = Part("C1", SymbolKind.Capacitor, 10000, 6000, "C", "1", "pF");
        var g2 = new EditableComponent { InstanceName = "G2", Symbol = SymbolKind.Ground, X = 10000, Y = 9000 };
        var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../TSTSUB", X = 14000, Y = 2000 };
        x1.Parameters.Add(new EditableParameter { Name = "Ls", Expression = "2*Lp" });
        var s1 = Snp("SNP1", "../ext/line.s2p", 20000, 2000);
        var p2 = Pin("P2", 2, "out", 26000, 0);
        var parts = new List<EditableComponent> { p1, r1, q1, g1, c1, g2, x1, s1, p2 };
        EditableComponent? d1 = null;
        if (diode) parts.Add(d1 = new EditableComponent { InstanceName = "D1", Symbol = SymbolKind.Diode, X = 2000, Y = 8000 });
        foreach (var c in parts) sch.Components.Add(c);
        Wire(sch, p1, 0, r1, 0);
        Wire(sch, r1, 1, q1, 0);
        Wire(sch, q1, 2, g1, 0);
        Wire(sch, q1, 1, c1, 0);
        Wire(sch, c1, 1, g2, 0);
        Wire(sch, q1, 1, x1, 0);
        Wire(sch, x1, 1, s1, 0);
        Wire(sch, s1, 1, p2, 0);
        if (d1 is not null) { Wire(sch, p1, 0, d1, 0); Wire(sch, d1, 1, g1, 0); }
        string cell = WriteCell(ws, "TSTPA200", sch);
        SetParameters(cell, ("Lp", "1", "nH"));
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

/// <summary>Brief vendorlib-16 R-vl16-2h: a build made to ship asks the compiler for a generic CPU. Serialised with the
/// tests that point the compiler lookup elsewhere, because this one points it at a stub.</summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class ShippedBuildCompilerArgumentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl16c-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly Func<string?>? _previous = VerilogACompilerDiscovery.PreferredCommand;

    public ShippedBuildCompilerArgumentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        VerilogACompilerDiscovery.PreferredCommand = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void AShippedBuild_PassesAGenericCpu_AndTheTarget()
    {
        if (OperatingSystem.IsWindows()) return;   // the stub is a shell script
        string log = Path.Combine(_dir, "args.log");
        string stub = Path.Combine(_dir, "stub-compiler");
        File.WriteAllText(stub, $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo stub 1.0; exit 0; fi\necho \"$@\" >> '{log}'\n" +
                                "while [ $# -gt 0 ]; do if [ \"$1\" = \"-o\" ]; then shift; echo built > \"$1\"; fi; shift; done\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        VerilogACompilerDiscovery.PreferredCommand = () => stub;
        string va = Path.Combine(_dir, "m.va");
        File.WriteAllText(va, "module m(a); endmodule\n");

        VerilogASourceCompiler.Build(va, Path.Combine(_dir, "out", "m.osdi"), "aarch64-unknown-linux");

        string args = File.ReadAllText(log);
        Assert.Contains("--target aarch64-unknown-linux", args);
        Assert.Contains("--target_cpu generic", args);
    }
}
