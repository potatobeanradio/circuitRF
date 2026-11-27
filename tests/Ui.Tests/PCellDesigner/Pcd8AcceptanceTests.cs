// Owner: PCD-8 (brief-pcd-8 R-pcd8-4, brief-pcd-0 §8). The acceptance check that closes the series, every step through the
// functions the PCell Designer window and `circuitrf pcell` call.

using System.Globalization;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tuning;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>
/// The §8 cell <c>PadTee</c> and its bench, built as a user builds them in the PCell Designer: the netlist text applied,
/// the parameters typed into the table, the pad's far edge stretched by <c>Lpad</c>, Save. The example
/// <c>examples/PCell Designer</c> is this, written by <see cref="Pcd8ExampleAuthoring"/>.
/// </summary>
internal static class Pcd8PadTee
{
    public const string Cell = "PadTee";
    public const string Tech = "microstrip-0508.ctech";

    // Port 1 – TL1 – B1 – T1 – TL3 – Port 2; the tee's branch TL2 ends in the pad SH1, 2 mm wide and Lpad long, open in a
    // circuit simulation. W = 1.1 mm is 50 Ω on the board. The tee's pin 1 is on TL3's side, so the stub turns away from
    // TL1 and port 1's feed stays clear of other copper, as a calibrated EM port needs.
    public const string Text = """
        MLIN:TL1  n1  n2  W=W  L=L1
        MBEND:B1  n2  n3  W=W  Angle=90 deg
        MTEE:T1  n4  n3  n5  W1=W  W2=W  W3=W
        MLIN:TL3  n4  n6  W=W  L=3 mm
        MLIN:TL2  n5  n7  W=W  L=Lstub
        MSHAPE:SH1  n7  Form="polygon"  W=0 um  X[1]=0 um  Y[1]=-1000 um  X[2]=300 um  Y[2]=-1000 um  X[3]=300 um  Y[3]=1000 um  X[4]=0 um  Y[4]=1000 um  PinX[1]=0 um  PinY[1]=0 um  PinW[1]=W  PinDir[1]=180 deg  Model="open"
        Port:P1  n1  0  Num=1  Z=50 Ohm
        Port:P2  n6  0  Num=2  Z=50 Ohm
        """;

    private static readonly (string Name, string Default, string Unit, string Min, string Max, string Description)[] Parameters =
    [
        ("W",     "1.1", "mm", "0.5", "2",   "line width"),
        ("L1",    "3",   "mm", "1",   "6",   "feed length to the bend"),
        ("Lstub", "4",   "mm", "1",   "8",   "stub length to the pad"),
        ("Lpad",  "300", "um", "100", "600", "pad length"),
    ];

    public static string RepoTestData([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata"));

    /// <summary>An empty workspace on the microstrip board: New Workspace with its technology copied in.</summary>
    public static string Workspace(string root)
    {
        string tech = Path.Combine(root, "tech", Tech);
        Directory.CreateDirectory(Path.GetDirectoryName(tech)!);
        File.Copy(Path.Combine(RepoTestData(), "schematic-to-layout", Tech), tech, overwrite: true);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = "tech/" + Tech });
        return root;
    }

    /// <summary>Step 1, as in the window: Create, Apply the text, type the parameters, Stretch by Parameter, Save.</summary>
    public static string Design(string workspace)
    {
        var created = PCellDesign.Create(workspace, Cell);
        var vm = new PCellDesignerViewModel(created.CellDir, workspace);
        foreach (var p in Parameters)
        {
            vm.AddParameter();
            var row = vm.Parameters[^1];
            (row.Name, row.Unit, row.Default, row.Minimum, row.Maximum, row.Description) = (p.Name, p.Unit, p.Default, p.Min, p.Max, p.Description);
        }
        vm.CnlText = Text;
        Assert.True(vm.ApplyText(), vm.Status);
        Assert.True(vm.StretchByParameter("SH1", [2, 3], "Lpad", 0), vm.Status);
        Assert.Null(vm.Save());
        return created.CellDir;
    }

    /// <summary>Step 3's bench: P1 – X1 (Lpad = 200 um) – a 50 Ω line – X2 (Lpad = 400 um) – P2, swept 1-12 GHz. The two
    /// instances are placed from the Library, then wired by the text, as the designer's text view wires a cell.</summary>
    public static string Bench(string workspace, string cellDir, string name = "Bench")
    {
        var created = CellCreate.Create(workspace, name, CellViews.Schematic);
        string dir = Path.GetDirectoryName(created.SchematicPath!)!;
        var model = SchematicPersistence.LoadFromFile(created.SchematicPath!).model;
        model.SchematicDirectory = dir;
        string cellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(dir, cellDir));
        foreach (var (x, at) in new[] { ("X1", 0L), ("X2", 4000L) })
        {
            var c = new EditableComponent { InstanceName = x, Symbol = SymbolKind.Generic, CellRef = cellRef, X = at, Y = 0 };
            c.Parameters.Add(new EditableParameter { Name = "Lpad", Expression = x == "X1" ? "200" : "400", Unit = "um",
                                                     Dimension = UnitDimension.Length });
            model.Components.Add(c);
        }
        var merged = CellNetlistMerge.Merge(model, """
            Port:P1  n1  0  Num=1  Z=50 Ohm
            PadTee:X1  n1  n2  Lpad=200 um
            MLIN:TL1  n2  n3  W=1.1 mm  L=5 mm
            PadTee:X2  n3  n4  Lpad=400 um
            Port:P2  n4  0  Num=2  Z=50 Ohm
            """);
        Assert.True(merged.Schematic is not null, string.Join("\n", merged.Refusals));
        var bench = merged.Schematic!;
        bench.Analyses.Add(new SParameterAnalysis("SP1", [new FrequencySpec("1", "12", 23, SweepKind.Linear, "GHz", "GHz")]));
        SchematicPersistence.SaveToFile(created.SchematicPath!, bench, name);
        return created.SchematicPath!;
    }
}

public sealed class Pcd8AcceptanceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd8-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        EmEvaluation.SolverStandIn = null;
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static string Say(IEnumerable<Diagnostic> findings) => string.Join("\n", findings.Select(f => $"{f.Severity} {f.Id}: {f.Render()}"));

    [Fact]
    public void PadTee_DesignedExportedPlacedTwice_LvsClean_Simulated_AndOptimisedByEm()
    {
        // 1. Design.
        string source = Pcd8PadTee.Workspace(Path.Combine(_scratch, "source"));
        string cell = Pcd8PadTee.Design(source);
        var check = PCellDesignCheck.Run(cell);
        Assert.True(check.All(f => f.Severity != DiagnosticSeverity.Error), Say(check));

        // 2. Export and import; the same bytes twice.
        string package = Path.Combine(_scratch, "PadTee.cpcell");
        var exported = PCellPackage.Export(cell, package);
        Assert.True(exported.Path is not null, Say(exported.Findings));
        byte[] first = File.ReadAllBytes(package);
        Assert.NotNull(PCellPackage.Export(cell, package).Path);
        Assert.Equal(first, File.ReadAllBytes(package));
        string ws = Pcd8PadTee.Workspace(Path.Combine(_scratch, "bench"));
        var imported = PCellPackage.Import(package, ws);
        Assert.True(imported.Path is not null, Say(imported.Findings));

        // 3. Place twice: two generated cells that differ, each with an Lpad handle.
        string bench = Pcd8PadTee.Bench(ws, imported.Path!);
        var laid = LayoutFromSchematic.Run(bench, emSetup: true);
        Assert.True(laid.Refusal is null, laid.Refusal);
        Assert.DoesNotContain(laid.Lines, l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning);
        var layout = LayoutPersistence.LoadFromFile(laid.LayoutPath);
        string layoutDir = Path.GetDirectoryName(laid.LayoutPath)!;
        var tech = TechPersistence.LoadFromFile(Path.Combine(ws, "tech", Pcd8PadTee.Tech));
        var x = new[] { "X1", "X2" }.Select(id => layout.Instances.Single(i => i.SchematicId == id)).ToArray();
        Assert.NotEqual(x[0].CellRef, x[1].CellRef);
        using (DesignedPCellResolver.Register(ws))
        {
            foreach (var (inst, lpad) in x.Zip(new[] { 200e-6, 400e-6 }))
            {
                var origin = CellLayoutResolver.Resolve(inst.CellRef, layoutDir).View!.PCellOrigin!;
                Assert.Equal("cpcell:PadTee", origin.GeneratorId);
                Assert.Equal(lpad, origin.Parameters["Lpad"].AsReal(), 12);
                Assert.True(PCellRegistry.TryGet(origin.GeneratorId, out var generator));
                var handle = Assert.Single(generator(origin.Parameters, tech, PCellLayerSelection.Default).Handles ?? []);
                Assert.Equal(("Lpad", PCellHandleQuantity.Length), (handle.Parameter, handle.Quantity));
            }
        }

        // 4. LVS clean.
        var lvs = LvsRun.Run(Path.GetDirectoryName(Path.GetDirectoryName(bench))!);
        Assert.True(lvs.ErrorCount == 0, string.Join("\n", lvs.Findings.Select(f => f.Diagnostic)));

        // 5. The circuit simulation runs. The pad is open (Model="open", S2), so Lpad moves the copper and not the circuit;
        //    a line length does move it.
        double[] S21(params (string Key, string Value)[] set)
        {
            var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(bench), new CircuitEvaluationRequest
            {
                Em = false, Analyses = ["SP1"], Tunables = set.ToDictionary(s => s.Key, s => s.Value),
            });
            Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
            var snp = DataSetBuilder.ToSnp(r.Results.Single(a => a.Name == "SP1").Data);
            return [.. snp.Matrices.Select(mat => mat[1, 0].Magnitude)];
        }
        var asPlaced = S21();
        Assert.Equal(asPlaced, S21(("X1.Lpad", "400 um")), (a, b) => Math.Abs(a - b) < 1e-12);
        Assert.NotEqual(asPlaced, S21(("X1.Lstub", "6 mm")), (a, b) => Math.Abs(a - b) < 1e-6);

        // 6. One em=correct optimisation of the bench by EM of its own layout (the .cem Update Layout made), tune X1.Lpad;
        //    the stand-in solver sees both pads as copper.
        string cem = Assert.Single(laid.EmSetups);
        var seen = new List<string>();
        EmEvaluation.SolverStandIn = (g, values, path) =>
        {
            Assert.DoesNotContain("X1", EmCellParts.NotCopper(g.Schematic!, Path.GetDirectoryName(bench)));
            Assert.DoesNotContain("X2", EmCellParts.NotCopper(g.Schematic!, Path.GetDirectoryName(bench)));
            string dir = Path.GetDirectoryName(g.Source.AbsolutePath)!;
            foreach (var id in new[] { "X1", "X2" })
            {
                var view = CellLayoutResolver.Resolve(g.Source.View.Instances.Single(i => i.SchematicId == id).CellRef, dir).View!;
                Assert.Contains(view.Shapes.Select(LayoutGeometry.BboxOf), b => Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY) > 0
                                && Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY) == 2000 * g.Source.DbuPerMicron);
            }
            seen.Add(g.Key);
            var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(bench), new CircuitEvaluationRequest { Em = false, Analyses = ["SP1"] });
            var snp = DataSetBuilder.ToSnp(r.Results.Single(a => a.Name == "SP1").Data);
            string written = path + ".s2p";
            TouchstoneIO.WriteFile(snp, written);
            return written;
        };
        var (m, v, c) = SchematicPersistence.LoadFromFile(bench);
        int sp = m.Analyses.FindIndex(a => a.Name == "SP1");
        m.Analyses[sp] = new SParameterAnalysis("SP1", [new FrequencySpec("6", "8", 3, SweepKind.Linear, "GHz", "GHz")]) { Model = AnalysisModel.Em };
        m.Tuning = new TuningSetup
        {
            Variables = [new TunableEntry { Key = "X1.Lpad", Opt = true, Min = "100 um", Max = "600 um", Scale = TuneScale.Lin }],
            Goals = [new OptimizationGoal { Name = "Match", Expression = "dB(SP1.S(1,1))", Analysis = "SP1", Type = GoalType.Le, Limit = "-15" }],
            Optimizer = new OptimizerSettings { Algorithm = "simplex", Em = OptimizerEmMode.Correct, EmBudget = 3, Seed = 1 },
        };
        SchematicPersistence.SaveToFile(bench, m, c, v.PanX, v.PanY, v.Zoom);
        var optimisation = OptimizationRun.Create(PreparedCircuit.FromSchematic(bench));
        Assert.True(optimisation.Refusal is null, optimisation.Refusal?.Render());
        var run = optimisation.Run();
        Assert.True(run.Refusal is null, run.Refusal?.Render());
        Assert.True(run.EmMode == OptimizerEmMode.Correct, $"{run.EmMode}: " + Say(run.Notes) + string.Join("\n", run.Stages));
        Assert.NotEmpty(seen);
        Assert.True(File.Exists(cem));
    }
}
