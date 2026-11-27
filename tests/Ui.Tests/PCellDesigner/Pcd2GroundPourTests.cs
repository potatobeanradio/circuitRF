// Owner: PCD-2 (ground under placed designed cells, found on the example workspace).

using System.Runtime.CompilerServices;
using Clipper2Lib;
using CircuitRF.Design.Layout.Lvs;
using Xunit;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>
/// The ground plane and placed designed cells, on a scratch copy of <c>examples/PCell Designer</c>: a cell of microstrip
/// parts gets the parent's pour as its parts would; a cell that draws its own ground (Slotline's slot) is never poured
/// over, in its own layout or a parent's; and LVS on a layout whose only copper is inside a placed cell still knows the
/// technology's ground reference.
/// </summary>
public sealed class Pcd2GroundPourTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-pcd2g-" + Guid.NewGuid().ToString("N")[..12]);

    public Pcd2GroundPourTests()
    {
        string source = Path.Combine(RepoRoot(), "examples", "PCell Designer");
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_ws, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_ws, Path.GetRelativePath(source, file)), overwrite: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort */ }
    }

    private Technology Tech => TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", "microstrip-0508.ctech"));

    /// <summary>Update Layout from Schematic on <paramref name="cell"/>, from an empty layout.</summary>
    private LayoutView LaidOut(string cell)
    {
        string clay = Path.Combine(_ws, cell, "layout", cell + ".clay");
        if (File.Exists(clay)) File.Delete(clay);
        var outcome = LayoutFromSchematic.Run(Path.Combine(_ws, cell, "schematic", cell + ".csch"));
        Assert.Null(outcome.Refusal);
        return LayoutPersistence.LoadFromFile(clay);
    }

    private static Paths64 Pour(LayoutView view, StackupLayer ground) => Clipper.Union(
        new Paths64(view.Shapes.Where(s => s.Generated == GroundArtwork.PourTag && ground.DrawingLayers.Contains(s.Layer))
                               .SelectMany(s => LayoutClipper.ToClipperPaths(s, 1))), FillRule.NonZero);

    private static double Covered(Paths64 pour, Bbox box) => Clipper.Area(Clipper.Intersect(pour,
        new Paths64 { new Path64 { new(box.MinX, box.MinY), new(box.MaxX, box.MinY), new(box.MaxX, box.MaxY), new(box.MinX, box.MaxY) } },
        FillRule.NonZero));

    private static StackupLayer Ground(Technology tech) => tech.Stackup.Layers.Single(l => l.IsGroundReference);

    [Fact]
    public void ABenchPlacingAMicrostripDesignedCell_IsPouredUnderIt()
    {
        var tech = Tech;
        var view = LaidOut("SpurlineBench");
        var cell = CellHierarchy.InstanceBbox(view.Instances.Single(i => i.SchematicId == "X1"), Path.Combine(_ws, "SpurlineBench", "layout"));
        double area = (double)(cell.MaxX - cell.MinX) * (cell.MaxY - cell.MinY);
        Assert.Equal(area, Covered(Pour(view, Ground(tech)), cell), 0);
    }

    [Fact]
    public void ACellDrawingItsOwnGround_IsNotPouredOver_AloneOrPlaced()
    {
        var tech = Tech;
        var ground = Ground(tech);
        Assert.Empty(Pour(LaidOut("Slotline"), ground));

        var bench = LaidOut("SlotlineBench");
        var pour = Pour(bench, ground);
        Assert.NotEmpty(pour);
        // The cell's own ground, as placed: the pour stops at its footprint, so the slot inside it stays open.
        var flat = LayoutDesignFlatten.Flatten(bench, Path.Combine(_ws, "SlotlineBench"), tech, null, resolvedCrossTechMappings: null);
        var own = flat.Shapes.Where(s => s.Generated != GroundArtwork.PourTag && s is not LabelShape && ground.DrawingLayers.Contains(s.Layer))
                             .Aggregate(Bbox.Empty, (b, s) => b.Union(LayoutGeometry.BboxOf(s)));
        Assert.False(own.IsEmpty);
        Assert.Equal(0, Covered(pour, own));
    }

    [Fact]
    public void Lvs_OnALayoutWhoseCopperIsAllInAPlacedCell_KnowsTheGroundReference()
    {
        var findings = LvsRun.Run(Path.Combine(_ws, "SpurlineBench")).Findings;
        Assert.DoesNotContain(findings, f => f.Id == "lvs.ground.no-reference-conductor");
    }

    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
}
