using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>DxfExport.Analyze/Write — hierarchy collection, unresolved-reference reporting, and block
/// name mangling, mirroring LayoutGdsiiExportTests' coverage for the DXF orchestrator.</summary>
public class DxfExportPlanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-export-plan-test-").FullName;

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
    public void Analyze_Hierarchy_CollectsEveryReachableCell()
    {
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        var plan = DxfExport.Analyze(topDir, null, 1000);
        Assert.Equal(2, plan.Structures.Count);
        Assert.Empty(plan.UnresolvedInstanceReferences);
        Assert.True(plan.CanWrite);
    }

    [Fact]
    public void Analyze_UnresolvedInstanceCellRef_Reported_NotSilent()
    {
        var topDir = CreateCell("TOP", v => v.Instances.Add(new LayoutInstance { CellRef = "../DoesNotExist", X = 0, Y = 0, Mag = 1.0 }));
        var plan = DxfExport.Analyze(topDir, null, 1000);

        Assert.Single(plan.UnresolvedInstanceReferences);
        Assert.Contains("DoesNotExist", plan.UnresolvedInstanceReferences[0]);
        Assert.True(plan.CanWrite);
    }

    [Fact]
    public void Analyze_ReportsBlockNameMapping_ForNonTrivialCellName()
    {
        // Unlike GDSII's restricted charset, DXF block/layer names permit spaces and most
        // punctuation — only a small set (</>/"/:/;/?/*/|/,/=/`) is actually illegal. A comma is a
        // legal FILESYSTEM path component (so CellFolder.CreateCellFolder accepts the cell name) but
        // an illegal DXF block name, so it must still be mangled on export.
        var cellDir = CreateCell("My,Amp", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        var plan = DxfExport.Analyze(cellDir, null, 1000);

        Assert.True(plan.BlockNameByCellName.ContainsKey("My,Amp"));
        Assert.NotEqual("My,Amp", plan.BlockNameByCellName["My,Amp"]);
    }

    [Fact]
    public void Write_ProducesNonEmptyFile_PreviewMatchesActualWrite()
    {
        var cellDir = CreateCell("TOP", v =>
        {
            v.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 50_000 });
            v.Shapes.Add(new BitmapShape { Layer = new LayerKey(2, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 10, H = 10 });
        });

        var plan = DxfExport.Analyze(cellDir, null, 1000);
        var options = new DxfExportOptions();
        var preview = DxfExport.Preview(plan, options);
        Assert.Equal(1, preview.BitmapsSkipped);

        var outPath = Path.Combine(_dir, "out.dxf");
        var summary = DxfExport.Write(outPath, plan, options);
        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 0);
        Assert.Equal(preview.BitmapsSkipped, summary.BitmapsSkipped);
    }
}
