using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 4 (§1.3, arrays are native) — a 5x5 array exports as ONE INSERT with COLROW + pitch and
/// re-imports as ONE LayoutInstance with Rows=Cols=5, not 25 placements.
/// Gate 5 (R-L4b-2) — all 8 rotation/mirror combinations round-trip through a real DXF export+import
/// to the SAME rendered result, off-screen pixel comparison — mirrors LayoutGdsiiTransformTests exactly,
/// this time proving DXF's DIRECT (no +180) mirror mapping.
/// </summary>
public sealed class DxfArrayAndMirrorTests : IDisposable
{
    private static readonly LayerKey LayerA = new(1, 0);
    private readonly string _originalDir;
    private readonly string _importedDir;

    public DxfArrayAndMirrorTests()
    {
        _originalDir = Directory.CreateTempSubdirectory("dxf-array-mirror-orig-").FullName;
        _importedDir = Directory.CreateTempSubdirectory("dxf-array-mirror-import-").FullName;
        CellLayoutResolver.InvalidateAll();
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateAll();
        Directory.Delete(_originalDir, recursive: true);
        Directory.Delete(_importedDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 0.6, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static PolygonShape LShape() => new()
    {
        Layer = LayerA,
        Xy = [0, 0, 300, 0, 300, 100, 100, 100, 100, 300, 0, 300],
    };

    private string CreateCell(string rootDir, string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(rootDir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
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
    public void FiveByFiveArray_ExportsAsOneInsert_ReimportsAsOneInstance_RowsColsFive()
    {
        var leafDir = CreateCell(_originalDir, "Leaf", v => v.Shapes.Add(LShape()));
        var topDir = CreateCell(_originalDir, "TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(topLayoutDir, leafDir),
            X = 0, Y = 0, Mag = 1.0, Rows = 5, Cols = 5, PitchX = 1000, PitchY = 2000,
        });
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        var plan = DxfExport.Analyze(topDir, null, 1000);
        var dxfPath = Path.Combine(_originalDir, "array.dxf");
        DxfExport.Write(dxfPath, plan, new DxfExportOptions());

        string text = File.ReadAllText(dxfPath);
        int insertCount = System.Text.RegularExpressions.Regex.Matches(text, "\nINSERT\n").Count;
        Assert.Equal(2, insertCount); // one array INSERT inside the TOP block + one root INSERT in ENTITIES

        using var stream = File.OpenRead(dxfPath);
        var result = DxfImport.Import(stream, _importedDir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);

        var importedTopDir = Path.Combine(_importedDir, result.CellNameByBlockName["TOP"]);
        var importedTopLayoutDir = CellFolder.SubFolderPath(importedTopDir, ViewType.Layout);
        var importedTopView = LayoutPersistence.LoadFromFile(Path.Combine(importedTopLayoutDir, $"{result.CellNameByBlockName["TOP"]}.clay"));

        var inst = Assert.Single(importedTopView.Instances);
        Assert.Equal(5, inst.Rows);
        Assert.Equal(5, inst.Cols);
        Assert.Equal(1000, inst.PitchX);
        Assert.Equal(2000, inst.PitchY);
    }

    private static byte[] RenderPixels(string cellDir, string cellName, Technology tech, LayoutViewport vp)
    {
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{cellName}.clay"));

        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = layoutDir };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    [Theory]
    [InlineData(LayoutRotation.R0, false)]
    [InlineData(LayoutRotation.R90, false)]
    [InlineData(LayoutRotation.R180, false)]
    [InlineData(LayoutRotation.R270, false)]
    [InlineData(LayoutRotation.R0, true)]
    [InlineData(LayoutRotation.R90, true)]
    [InlineData(LayoutRotation.R180, true)]
    [InlineData(LayoutRotation.R270, true)]
    public void DxfRoundTrip_AllEightRotationMirrorCombos_RendersPixelIdenticalToOriginal(LayoutRotation rot, bool mirror)
    {
        var leafDir = CreateCell(_originalDir, "Leaf", v => v.Shapes.Add(LShape()));
        var topDir = CreateCell(_originalDir, "TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var realCellRef = Path.GetRelativePath(topLayoutDir, leafDir);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = realCellRef, X = 200, Y = 200, Rot = rot, MirrorX = mirror, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        var resolution = CellLayoutResolver.Resolve(realCellRef, topLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);

        var tech = MakeTech();
        var vp = new LayoutViewport(-500, -500, 0.5, 400, 400);
        var originalPixels = RenderPixels(topDir, "TOP", tech, vp);

        var plan = DxfExport.Analyze(topDir, tech, 1000);
        var dxfPath = Path.Combine(_originalDir, $"export-{rot}-{mirror}.dxf");
        DxfExport.Write(dxfPath, plan, new DxfExportOptions());

        using var stream = File.OpenRead(dxfPath);
        var importResult = DxfImport.Import(stream, _importedDir, tech, destDbuPerMicron: 1000);
        Assert.False(importResult.Cancelled);

        var importedTopCellName = importResult.CellNameByBlockName["TOP"];
        var importedTopDir = Path.Combine(_importedDir, importedTopCellName);
        var importedTopLayoutDir = CellFolder.SubFolderPath(importedTopDir, ViewType.Layout);
        var importedTopView = LayoutPersistence.LoadFromFile(Path.Combine(importedTopLayoutDir, $"{importedTopCellName}.clay"));
        var importedInst = Assert.Single(importedTopView.Instances);
        var importedResolution = CellLayoutResolver.Resolve(importedInst.CellRef, importedTopLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, importedResolution.State);

        var importedPixels = RenderPixels(importedTopDir, importedTopCellName, tech, vp);

        Assert.Equal(originalPixels, importedPixels);
    }
}
