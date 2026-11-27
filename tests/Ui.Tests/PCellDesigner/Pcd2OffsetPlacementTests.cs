// Owner: PCD-2 (brief-pcd-2).

using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Tests.SchematicSync;
using Xunit;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-2: offset placement in the planner, its geometry, and the plan-at-values entry point.</summary>
public sealed class Pcd2OffsetPlacementTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static Bbox Box(LayoutView layout, string dir, string id) =>
        CellHierarchy.InstanceBbox(layout.Instances.Single(i => i.SchematicId == id), dir);

    /// <summary>R-pcd2-1: TL2 is placed from TL1 pin 2 by its offset (g = 1 mm on, turned 90°), TL3 rides with it as one
    /// body; an anchor that does not resolve, or one in the part's own chain, is a Warning and moves nothing.</summary>
    [Fact]
    public void AnOffsetPart_IsPlacedFromItsAnchor_WithItsChain_AndARefusalMovesNothing()
    {
        var p = SdlChecker.Prepare("O2-offset-turned", Path.Combine(_scratch, "a"));
        var plan = p.Plan();
        var layout = p.Applied(plan);
        long um = layout.DbuPerMicron;
        var (tl1, tl2, tl3) = (Box(layout, p.LayoutDir, "TL1"), Box(layout, p.LayoutDir, "TL2"), Box(layout, p.LayoutDir, "TL3"));
        Assert.Equal(tl1.MaxX + 1000 * um, (tl2.MinX + tl2.MaxX) / 2);   // g along TL1 pin 2's outward direction
        Assert.Equal((tl1.MinY + tl1.MaxY) / 2, tl2.MinY);                // its pin 1 there, the line turned to run up
        Assert.Equal(3000 * um, tl2.MaxY - tl2.MinY);
        Assert.Equal((tl2.MinX, tl2.MaxX, tl2.MaxY), (tl3.MinX, tl3.MaxX, tl3.MinY));   // the chain moved with it
        Assert.Contains(plan.Lines, l => l is { InstanceName: "TL2", Severity: SchematicLayoutPlanner.ReportSeverity.Info }
                                         && l.Text.Contains("placed by its offset from TL1 pin 2"));

        var stray = SdlChecker.Prepare("O2-offset-turned", Path.Combine(_scratch, "b"));
        var tl2Comp = stray.Model.Components.Single(c => c.InstanceName == "TL2");
        tl2Comp.Offset = tl2Comp.Offset! with { Anchor = "TL9.2" };
        Assert.Contains(stray.Plan().Lines, l => l is { InstanceName: "TL2", Severity: SchematicLayoutPlanner.ReportSeverity.Warning }
                                                 && l.Text.Contains("names no part placed"));
        tl2Comp.Offset = tl2Comp.Offset with { Anchor = "TL1.2", Dx = "nosuch * 2" };
        Assert.Contains(stray.Plan().Lines, l => l.InstanceName == "TL2" && l.Text.Contains("does not evaluate"));

        // C1: TL3 is joined to TL1 through TL2, so its chain already places it.
        var joined = SdlChecker.Prepare("C1-through-line", Path.Combine(_scratch, "c"));
        var before = joined.Applied(joined.Plan()).Instances.Select(i => (i.X, i.Y, i.RotationDegrees)).ToList();
        joined.Model.Components.Single(c => c.InstanceName == "TL3").Offset = new OffsetPlacement { Anchor = "TL1.2", Dx = "1e-3" };
        var refused = joined.Plan();
        Assert.Contains(refused.Lines, l => l is { InstanceName: "TL3", Severity: SchematicLayoutPlanner.ReportSeverity.Warning }
                                            && l.Text.Contains("in its own chain"));
        Assert.Equal(before, joined.Applied(refused).Instances.Select(i => (i.X, i.Y, i.RotationDegrees)).ToList());
    }

    /// <summary>R-pcd2-2: Pose is PCD-0 §4.2's geometry; Drag writes the change into Dx and Dy as literals, keeping g.</summary>
    [Fact]
    public void Pose_IsTheContractGeometry_AndDrag_KeepsTheParameterTerm()
    {
        var scope = new Scope("global");
        scope.Bind("g", "50", "um");
        var evaluator = new Evaluator();
        var anchor = new PinPose(1000, 0, 90);   // facing +y
        var offset = new OffsetPlacement { Anchor = "TL1.2", Dx = "g", Dy = "-2", Unit = "um", Angle = "90" };

        var pose = OffsetPlacer.Pose(offset, anchor, scope, evaluator, 1000, out string? refusal);
        Assert.Null(refusal);
        Assert.Equal(new PinPose(3000, 50_000, 0), pose);   // g along +y; Dy is 90° counter-clockwise (−x), so −2 µm is +x
        Assert.Null(OffsetPlacer.Pose(offset with { Angle = "nosuch" }, anchor, scope, evaluator, 1000, out refusal));
        Assert.Contains("Angle", refusal);

        // Dragged 3 µm along the anchor's direction and 2 µm toward −x (Dy's way): g + 3, and −2 + 2 = 0.
        var dragged = OffsetPlacer.Drag(offset, anchor, pose!.Value, 1000, 53_000, 1000);
        Assert.Equal(("g + 3", "0"), (dragged.Dx, dragged.Dy));
        Assert.Equal(new PinPose(1000, 53_000, 0), OffsetPlacer.Pose(dragged, anchor, scope, evaluator, 1000, out _));
        var back = OffsetPlacer.Drag(dragged, anchor, new PinPose(1000, 53_000, 0), 1000, 50_000, 1000);
        Assert.Equal("g", back.Dx);
    }

    /// <summary>R-pcd2-3: the same .clay bytes twice, the root stated, and a value that moves the offset part.</summary>
    [Fact]
    public void DesignedCellLayoutAt_IsDeterministic_AndStatesItsRoot()
    {
        var p = SdlChecker.Prepare("O2-offset-turned", _scratch);
        var none = new Dictionary<string, Value>(StringComparer.Ordinal);
        var first = DesignedCellLayout.At(p.CellDir, none, p.Workspace, p.Tech, p.TechPath);
        var second = DesignedCellLayout.At(p.CellDir, none, p.Workspace, p.Tech, p.TechPath);
        Assert.Null(first.Refusal);
        Assert.Equal(LayoutPersistence.Serialize(first.Layout!), LayoutPersistence.Serialize(second.Layout!));
        Assert.Contains(first.Lines, l => l.InstanceName == "" && l.Text.StartsWith("Root: TL1 "));

        var wider = DesignedCellLayout.At(p.CellDir, new Dictionary<string, Value> { ["g"] = new Value(2e-3) },
                                          p.Workspace, p.Tech, p.TechPath);
        long um = wider.Layout!.DbuPerMicron;
        var tl1 = Box(wider.Layout, p.LayoutDir, "TL1");
        var tl2 = Box(wider.Layout, p.LayoutDir, "TL2");
        Assert.Equal(tl1.MaxX + 2000 * um, (tl2.MinX + tl2.MaxX) / 2);
    }

    /// <summary>Drawn metal's words (Form, Model) reach its generator as text, so a polygon MSHAPE is placed with its shape
    /// — here offset 200 µm on from TL2 pin 2.</summary>
    [Fact]
    public void APolygonMShape_IsPlacedWithItsShape()
    {
        var p = SdlChecker.Prepare("O1-offset-gap", _scratch);
        // Drawn clear of every wire, so its pin is on no net: no net joins it, only its offset places it.
        var shape = new EditableComponent { Symbol = SymbolKind.MShape, InstanceName = "SH1", X = 3000, Y = 2000,
                                            Offset = new OffsetPlacement { Anchor = "TL2.2", Dx = "200", Unit = "um" } };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
            shape.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit });
        p.Model.Components.Add(shape);

        var plan = p.Plan();
        Assert.DoesNotContain(plan.NoLayoutWarnings, w => w.StartsWith("SH1"));
        var layout = p.Applied(plan);
        long um = layout.DbuPerMicron;
        var sh1 = Box(layout, p.LayoutDir, "SH1");
        Assert.Equal((100 * um, 100 * um), (sh1.MaxX - sh1.MinX, sh1.MaxY - sh1.MinY));   // the default 100 µm square
        Assert.True(Box(layout, p.LayoutDir, "TL2").MaxX + 200 * um == sh1.MinX,       // its pin 1, on its left edge
                    $"SH1 at {sh1.MinX}, TL2 ends {Box(layout, p.LayoutDir, "TL2").MaxX}:\n" + string.Join("\n", plan.Lines.Select(l => l.Text)));
    }

    /// <summary>R-pcd2-4: both designs pass the checker, and O1's gap is exactly g.</summary>
    [Fact]
    public void BothOffsetDesigns_PassTheChecker()
    {
        foreach (string design in new[] { "O1-offset-gap", "O2-offset-turned" })
        {
            var result = SdlChecker.Check(design, Path.Combine(_scratch, design));
            Assert.True(result.Problems.Count == 0, $"{design}:\n  " + string.Join("\n  ", result.Problems));
        }
        var o1 = SdlChecker.Check("O1-offset-gap", Path.Combine(_scratch, "gap"));
        string dir = Path.Combine(_scratch, "gap", "ws", "O1-offset-gap", "layout");
        Assert.Equal(500 * o1.Layout.DbuPerMicron, Box(o1.Layout, dir, "TL2").MinX - Box(o1.Layout, dir, "TL1").MaxX);
    }
}
