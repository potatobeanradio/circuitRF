// Owner: PCD-13 (brief-pcd-13 §4). One scoped test per kind and per rule, on Pcd5Workspace's designed through line
// (Port – TL1 5 mm – TL2 Lmid = 3 mm – TL3 7 mm – Port, on the microstrip-0508 board).

using Clipper2Lib;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.PCellDesigner.Operations;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd13OperationsTests
{
    private static readonly LayerKey Top = new(1, 0), Mask = new(3, 0);

    [Fact]
    public void Merge_AToolsAreaBecomesCopper_AndAnUnusedToolIsNone()
    {
        using var ws = new Pcd5Workspace();
        // A 200 µm tool square 2 mm to the side of TL1: no copper until merged.
        double plain = CopperArea(ws, Plan(ws, ws.Designed("Mg0", edit: m => m.Components.Add(Tool("SH1", "TL2.1", "0.5", "2", 0, -100, 200, 100)))), Top);
        double bare = CopperArea(ws, Plan(ws, ws.Designed("Mg1")), Top);
        Near(bare, plain);

        var at = Plan(ws, ws.Designed("Mg2", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "2", 0, -100, 200, 100));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Merge, A = ["TL1"], B = ["SH1"] }];
        }));
        Near(bare + 200.0 * 200 * Dbu2, CopperArea(ws, at, Top));
        Assert.True(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied);
    }

    [Fact]
    public void Subtract_AToolCutsAHoleInTheLine()
    {
        using var ws = new Pcd5Workspace();
        double bare = CopperArea(ws, Plan(ws, ws.Designed("Sb1")), Top);
        // The tool square sits inside TL1, 0.5 mm from TL2's start: a hole that severs nothing and touches no pin.
        var at = Plan(ws, ws.Designed("Sb2", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "0", 0, -100, 200, 100));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Subtract, A = ["TL1"], B = ["SH1"] }];
        }));
        Near(bare - 200.0 * 200 * Dbu2, CopperArea(ws, at, Top));
        var tl1 = at.Layout!.Shapes.OfType<PolygonShape>().Single(s => s.Component == "TL1");
        Assert.Equal(PCellOperations.GeneratedTag, tl1.Generated);
        Assert.Single(tl1.Holes!);
        // TL1 is still placed as itself (pins, origin), on a copy of its cell with no copper.
        var inst = at.Layout.Instances.Single(i => i.SchematicId == "TL1");
        Assert.Contains(GeneratedCellStore.VariantSeparator + "ops", inst.CellRef);
    }

    [Fact]
    public void Intersect_KeepsTheLineInsideTheTool()
    {
        using var ws = new Pcd5Workspace();
        // A 0.4 mm strip from TL2's start, 4 mm along it: TL2 trimmed to 3 mm × 0.4 mm.
        var at = Plan(ws, ws.Designed("Ix1", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0", "0", -4000, -200, 0, 200));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Intersect, A = ["TL2"], B = ["SH1"] }];
        }));
        Assert.True(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied, Lines(at));
        double tl2 = at.Layout!.Shapes.Where(s => s.Component == "TL2").Sum(s => Area(s, ws.Tech));
        Near(3000.0 * 400 * Dbu2, tl2);
    }

    [Fact]
    public void Grow_OntoTheMask_LeavesTheCopperAndKeepsTheModel()
    {
        using var ws = new Pcd5Workspace();
        string cell = ws.Designed("Gr1", edit: m => m.PCellOperations =
            [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Grow, A = ["TL2"], Amount = "Lmid/30", Onto = "Soldermask Top" }]);
        var bare = CopperArea(ws, Plan(ws, ws.Designed("Gr0")), Top);
        var at = Plan(ws, cell);
        Near(bare, CopperArea(ws, at, Top));
        var mask = at.Layout!.Shapes.Where(s => s.Layer == Mask).ToList();
        Assert.Single(mask);
        // TL2 grown by 100 µm on every side: (3 + 0.2) mm long.
        var b = Clipper.GetBounds(LayoutClipper.ToClipperPaths(mask[0], 1));
        Assert.InRange(b.right - b.left, 3200 * 1000 - 2, 3200 * 1000 + 2);
        Assert.Empty(PCellOperations.WithoutCircuitModel(Schematic(cell), ws.Tech));
    }

    [Fact]
    public void Grow_TakesAnAmountWithASpaceBeforeItsUnit()
    {
        using var ws = new Pcd5Workspace();
        var add = PCellOperations.Add(Schematic(ws.Designed("Gs0")),
            new PCellOperation { Kind = PCellOperationKind.Grow, A = ["TL2"], Amount = "100 um", Onto = "Soldermask Top" });
        Assert.Null(add.Refusal);
        string cell = ws.Designed("Gs1", edit: m => m.PCellOperations = add.Schematic!.PCellOperations);
        var at = Plan(ws, cell);
        Assert.True(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied, Lines(at));
        var b = Clipper.GetBounds(LayoutClipper.ToClipperPaths(at.Layout!.Shapes.Single(s => s.Layer == Mask), 1));
        Assert.InRange(b.right - b.left, 3200 * 1000 - 2, 3200 * 1000 + 2);
    }

    [Fact]
    public void Repeat_CopiesAPadAtAParameterPitch_WithItsDimension()
    {
        using var ws = new Pcd5Workspace();
        var at = Plan(ws, ws.Designed("Rp1", edit: m =>
        {
            var pad = Tool("SH1", "TL2.1", "0.5", "2", 0, -100, 200, 100);
            pad.Parameters.RemoveAll(p => p.Name == DrawnMetal.Role);
            m.Components.Add(pad);
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Repeat, A = ["SH1"], Rows = "1", Columns = "3",
                                                      PitchX = "Lmid/6", PitchY = "0", Layer = null }];
        }));
        double bare = CopperArea(ws, Plan(ws, ws.Designed("Rp0")), Top);
        Near(bare + 3 * 200.0 * 200 * Dbu2, CopperArea(ws, at, Top));
        var span = Assert.Single(PCellOperations.TraceOf(at.Layout!)!.Spans);
        Assert.Equal("Lmid/6", span.Expression);
        Assert.Equal(500L * 1000, Math.Abs(span.ToX - span.FromX) + Math.Abs(span.ToY - span.FromY));
    }

    /// <summary>PCD-15: Fillet rounds TL1's two outer corners by the radius, Chamfer cuts them at 45° by the leg; its joint
    /// with TL2 stays square (the layer is smoothed as a whole), and a suppressed step leaves the line as planned.</summary>
    [Fact]
    public void FilletAndChamfer_TreatTheOuterCornersOnly_AndCanBeSuppressed()
    {
        using var ws = new Pcd5Workspace();
        double bare = CopperArea(ws, Plan(ws, ws.Designed("Fc0")), Top);
        double r = 100 * 1000;   // 100 µm in DBU
        foreach (var (name, kind, removed) in new[] { ("Fc1", PCellOperationKind.Fillet, 2 * (1 - Math.PI / 4) * r * r),
                                                      ("Fc2", PCellOperationKind.Chamfer, r * r) })
        {
            var at = Plan(ws, ws.Designed(name, edit: m => m.PCellOperations =
                [new PCellOperation { Id = "op1", Kind = kind, A = ["TL1"], Amount = "Lmid/30" }]));
            Assert.True(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied, Lines(at));
            double cut = bare - CopperArea(ws, at, Top);
            Assert.True(Math.Abs(cut - removed) < 0.02 * removed, $"{kind}: {cut} DBU² removed, {removed} expected");
        }
        var off = Plan(ws, ws.Designed("Fc3", edit: m => m.PCellOperations =
            [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Fillet, A = ["TL1"], Amount = "100 um", Suppressed = true }]));
        Near(bare, CopperArea(ws, off, Top));
    }

    [Fact]
    public void ModelHonesty_ACopperCutIsEmOnly_UnlessKeepModel_AndTheBenchSaysSo()
    {
        using var ws = new Pcd5Workspace();
        var cut = new PCellOperation { Id = "op1", Kind = PCellOperationKind.Subtract, A = ["TL1"], B = ["SH1"] };
        string cell = ws.Designed("Mh1", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "0", 0, -100, 200, 100));
            m.PCellOperations = [cut, cut with { Id = "op2", KeepModel = true },
                                 new PCellOperation { Id = "op3", Kind = PCellOperationKind.Grow, A = ["TL1"], Amount = "50", Unit = "um", Onto = "Soldermask Top" }];
        });
        Assert.Equal(["op1 (Subtract)"], PCellOperations.WithoutCircuitModel(Schematic(cell), ws.Tech));

        using var registration = DesignedPCellResolver.Register(ws.Root);
        var circuit = PreparedCircuit.FromSchematic(ws.Bench("BenchMh1", cell, "3"));
        Assert.Contains("Mh1:op1 (Subtract)", EmModelSwitch.WithoutCircuitModel(circuit.Tb!, circuit.Lib!));
    }

    [Fact]
    public void ASubtractThatRemovesAPinsCopper_IsRefusedWithASentence()
    {
        using var ws = new Pcd5Workspace();
        double bare = CopperArea(ws, Plan(ws, ws.Designed("Pr0")), Top);
        var at = Plan(ws, ws.Designed("Pr1", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0", "0", -4000, -200, 0, 200));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Subtract, A = ["TL2"], B = ["SH1"] }];
        }));
        var line = Assert.Single(at.Lines, l => l.Text.StartsWith("op1 (Subtract) was not applied", StringComparison.Ordinal));
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Warning, line.Severity);
        Assert.Contains("removes the copper under TL2 pin", line.Text);
        Assert.False(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied);
        Near(bare, CopperArea(ws, at, Top));
    }

    [Fact]
    public void Lvs_CellsWithASubtractAndAMergeAreCleanOnTheirOwnLayout_AndALeafOnABench()
    {
        using var ws = new Pcd5Workspace();
        string cut = ws.Designed("Lv1", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "0", 0, -100, 200, 100));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Subtract, A = ["TL1"], B = ["SH1"] }];
        });
        string merged = ws.Designed("Lv2", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "0.55", 0, -100, 200, 100));   // across TL1's edge
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Merge, A = ["TL1"], B = ["SH1"] }];
        });
        foreach (string cell in new[] { cut, merged })
        {
            var at = Plan(ws, cell);
            Assert.True(PCellOperations.TraceOf(at.Layout!)!.Steps.Single().Applied, Lines(at));
            string clay = Path.Combine(cell, "layout", Path.GetFileName(cell) + ".clay");
            LayoutPersistence.SaveToFile(clay, at.Layout!);
            using var generated = GeneratedCellsRun.Prepare(clay);
            var own = LvsRun.Run(cell);
            Assert.True(own.ErrorCount == 0, Errors(own));
            Assert.Contains(own.Findings, f => f.Id == "lvs.scope.pcell-operations");
        }

        using var registration = DesignedPCellResolver.Register(ws.Root);
        string bench = ws.Bench("BenchLv1", cut, "3");
        Assert.Null(LayoutFromSchematic.Run(bench).Refusal);
        var placed = LvsRun.Run(Path.GetDirectoryName(Path.GetDirectoryName(bench))!);
        Assert.True(placed.ErrorCount == 0, Errors(placed));
    }

    [Fact]
    public void AStepThatJoinsOrSeversPorts_IsRefused()
    {
        using var ws = new Pcd5Workspace();
        // Without TL2, TL3 sits 1 mm past TL1's end: the ports are on two pieces of copper. A bridge would join them.
        var joined = Plan(ws, ws.Designed("Pj1", edit: m =>
        {
            m.Components.RemoveAll(c => c.InstanceName == "TL2");
            m.Components.Single(c => c.InstanceName == "TL3").Offset = new OffsetPlacement { Anchor = "TL1.2", Pin = 1, Dx = "1", Unit = "mm" };
            m.Components.Add(Tool("SH1", "TL1.2", "0", "0", -100, -200, 1100, 200));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Merge, A = ["TL1"], B = ["SH1"] }];
        }));
        Assert.Contains(joined.Lines, l => l.Text.StartsWith("op1 (Merge) was not applied: it changes which ports the copper joins", StringComparison.Ordinal));

        // A 100 µm band right across TL1: its pins keep their copper, but port 1 no longer reaches port 2.
        var severed = Plan(ws, ws.Designed("Pj2", edit: m =>
        {
            m.Components.Add(Tool("SH1", "TL2.1", "0.5", "0", 0, -1000, 100, 1000));
            m.PCellOperations = [new PCellOperation { Id = "op1", Kind = PCellOperationKind.Subtract, A = ["TL1"], B = ["SH1"] }];
        }));
        Assert.Contains(severed.Lines, l => l.Text.StartsWith("op1 (Subtract) was not applied: it changes which ports the copper joins", StringComparison.Ordinal));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private const double Dbu2 = 1e6;   // µm² in DBU² at 1000 DBU per µm

    /// <summary>Two areas in DBU² equal to a hundredth of a square micron's worth of rounding.</summary>
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1e4, expected + 1e4);

    /// <summary>An MSHAPE rectangle (x1, y1)–(x2, y2) µm in its own frame, pin 1 at its origin facing 180°, offset from
    /// <paramref name="anchor"/> by Dx, Dy mm, a tool (Role="tool", Model="open").</summary>
    private static EditableComponent Tool(string name, string anchor, string dxMm, string dyMm, double x1, double y1, double x2, double y2)
    {
        var shape = new EditableComponent { InstanceName = name, Symbol = SymbolKind.MShape, X = 9000, Y = 3000 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 0))
            shape.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        void Set(string row, double um) { var p = shape.Parameters.Single(p => p.Name == row); p.Expression = um.ToString(System.Globalization.CultureInfo.InvariantCulture); p.Unit = "um"; }
        (double X, double Y)[] corners = [(x1, y1), (x2, y1), (x2, y2), (x1, y2)];
        for (int i = 0; i < 4; i++) { Set(DrawnMetal.X(i + 1), corners[i].X); Set(DrawnMetal.Y(i + 1), corners[i].Y); }
        Set(DrawnMetal.PinX(1), 0);
        Set(DrawnMetal.PinY(1), 0);
        shape.Parameters.Single(p => p.Name == DrawnMetal.Model).Expression = $"\"{DrawnMetal.ModelOpen}\"";
        shape.Parameters.Add(new EditableParameter { Name = DrawnMetal.Role, Expression = $"\"{DrawnMetal.RoleTool}\"" });
        shape.Offset = new OffsetPlacement { Anchor = anchor, Pin = 1, Dx = dxMm, Dy = dyMm, Unit = "mm" };
        return shape;
    }

    private sealed record Planned(DesignedCellLayoutResult Result, string Cell)
    {
        public LayoutView? Layout => Result.Layout;
        public IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines => Result.Lines;
    }

    private static Planned Plan(Pcd5Workspace ws, string cell)
    {
        var at = DesignedCellLayout.At(cell, new Dictionary<string, Value>(), ws.Root, ws.Tech, ws.TechPath);
        Assert.True(at.Layout is not null, at.Refusal);
        return new Planned(at, cell);
    }

    private static SchematicEditModel Schematic(string cell)
        => SchematicPersistence.LoadFromFile(Path.Combine(cell, "schematic", Path.GetFileName(cell) + ".csch")).model;

    /// <summary>The cell's copper on a layer as the generator flattens it (every instance's shapes and the layout's own,
    /// labels and the ground pour left out), unioned, in DBU².</summary>
    private static double CopperArea(Pcd5Workspace ws, Planned at, LayerKey layer)
    {
        var shapes = new List<LayoutShape>();
        foreach (var inst in at.Layout!.Instances)
            shapes.AddRange(LayoutFlatten.FlattenAllLevels(inst, Path.Combine(at.Cell, "layout"), ws.Tech).Shapes);
        shapes.AddRange(at.Layout.Shapes.Where(s => s.Generated != GroundArtwork.PourTag));
        var paths = new Paths64();
        foreach (var s in shapes.Where(s => s.Layer == layer && LayoutBooleans.IsClipperOperand(s)))
            paths.AddRange(LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, ws.Tech)));
        return Clipper.Area(Clipper.Union(paths, FillRule.NonZero));
    }

    private static double Area(LayoutShape s, Technology tech)
        => Clipper.Area(Clipper.Union(LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, tech)), FillRule.NonZero));

    private static string Lines(Planned at) => string.Join("\n", at.Lines.Select(l => $"{l.Severity} {l.InstanceName} {l.Text}"));

    private static string Errors(LvsRunResult lvs)
        => string.Join("\n", lvs.Findings.Where(f => f.Severity == DiagnosticSeverity.Error).Select(f => $"{f.Id}: {f.Diagnostic}"));
}
