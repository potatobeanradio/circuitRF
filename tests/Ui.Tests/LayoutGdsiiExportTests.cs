using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 8 (coordinate overflow reported by name, nothing written) and R-L4a-3's fidelity
/// plan (curve/hole/bitmap counts, structure-name mapping) computed by the SAME write path the real
/// export uses (a dry run into <see cref="Stream.Null"/>).</summary>
public class LayoutGdsiiExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gdsii-export-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        var layoutPath = Path.Combine(layoutDir, $"{name}.clay");
        LayoutPersistence.SaveToFile(layoutPath, view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    [Fact]
    public void Analyze_CoordinateOverflow_ReportsOffenderByName_CanWriteFalse()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = (long)int.MaxValue + 1000, Y2 = 100 }));

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.False(plan.CanWrite);
        Assert.NotEmpty(plan.CoordinateOverflowOffenders);
        Assert.Contains(plan.CoordinateOverflowOffenders, o => o.Contains("RectShape"));
    }

    [Fact]
    public void Write_WhenPlanCannotWrite_Throws_NoFileWritten()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = (long)int.MaxValue + 1000, Y2 = 100 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        var outPath = Path.Combine(_dir, "out.gds");

        Assert.Throws<GdsiiExportException>(() => GdsiiExport.Write(outPath, plan));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Analyze_ReportsCurveHoleAndBitmapCounts_MatchingWhatWriteActuallyDoes()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 });
            v.Shapes.Add(new PolygonShape
            {
                Layer = new LayerKey(2, 0),
                Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
                Holes = [[400, 600, 600, 600, 600, 400, 400, 400]],
            });
            v.Shapes.Add(new BitmapShape { Layer = new LayerKey(3, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 });
        });

        var plan = GdsiiExport.Analyze(cellDir, null, 1000);
        Assert.True(plan.CanWrite);
        Assert.Equal(1, plan.CurvedShapesFlattened);
        Assert.Equal(1, plan.HolesKeyholed);
        Assert.Equal(1, plan.BitmapsSkipped);

        // The preview must not disagree with the actual write — same counts, produced by GdsiiWriter.Write itself.
        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);
        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 0);
    }

    [Fact]
    public void Analyze_ReportsStructureNameMapping_ForNonTrivialCellName()
    {
        var cellDir = CreateCell("My Amp!", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = GdsiiExport.Analyze(cellDir, null, 1000);

        Assert.True(plan.StructureNameByCellName.ContainsKey("My Amp!"));
        Assert.NotEqual("My Amp!", plan.StructureNameByCellName["My Amp!"]);
    }

    [Fact]
    public void Analyze_Hierarchy_CollectsEveryReachableCell()
    {
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        var plan = GdsiiExport.Analyze(topDir, null, 1000);
        Assert.Equal(2, plan.Structures.Count);
        Assert.Empty(plan.UnresolvedInstanceReferences);
    }

    [Fact]
    public void Analyze_UnresolvedInstanceCellRef_Reported_NotSilent()
    {
        // Regression for the exact mistake a hand-typed relative path fell into (see
        // LayoutGdsiiTransformTests's own history): an instance whose CellRef does not resolve to any
        // reachable cell must be reported, not silently exported as a dangling reference a GDSII
        // viewer would show with no explanation.
        var topDir = CreateCell("TOP", v => v.Instances.Add(
            new LayoutInstance { CellRef = "../DoesNotExist", X = 0, Y = 0, Mag = 1.0 }));

        var plan = GdsiiExport.Analyze(topDir, null, 1000);

        Assert.Single(plan.UnresolvedInstanceReferences);
        Assert.Contains("DoesNotExist", plan.UnresolvedInstanceReferences[0]);
        Assert.True(plan.CanWrite); // a dangling reference doesn't block export — it's the source
                                    // design's own pre-existing state, reported rather than refused
    }
}
