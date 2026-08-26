using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 5 (brief-L4a-gdsii-interchange.md): all 8 rotation/mirror combinations round-trip through a
/// real GDSII export + import to the SAME rendered result, verified by off-screen pixel comparison —
/// the test that catches reflect-after-rotate (§2.1 item 4).
/// </summary>
public sealed class LayoutGdsiiTransformTests : IDisposable
{
    private static readonly LayerKey LayerA = new(1, 0);
    private readonly string _originalDir;
    private readonly string _importedDir;

    public LayoutGdsiiTransformTests()
    {
        _originalDir = Directory.CreateTempSubdirectory("gdsii-xform-orig-").FullName;
        _importedDir = Directory.CreateTempSubdirectory("gdsii-xform-import-").FullName;
        CellLayoutResolver.InvalidateUnder(_originalDir);
        CellLayoutResolver.InvalidateUnder(_importedDir);
        // A broken/unresolved instance placeholder draws text via SkiaFonts.PlexRegular, which cannot
        // load without a live Avalonia app host — same seam the L3a renderer tests already established.
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateUnder(_originalDir);
        CellLayoutResolver.InvalidateUnder(_importedDir);
        Directory.Delete(_originalDir, recursive: true);
        Directory.Delete(_importedDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 0.6, ZOrder = 0, Visible = true, Selectable = true }],
    };

    /// <summary>Genuinely asymmetric under rotation/mirror — same fixture L3a's own gate 2 uses, for
    /// the same reason (a symmetric shape would pass by accident).</summary>
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
    public void GdsiiRoundTrip_AllEightRotationMirrorCombos_RendersPixelIdenticalToOriginal(
        LayoutRotation rot, bool mirror)
    {
        var leafDir = CreateCell(_originalDir, "Leaf", v => v.Shapes.Add(LShape()));
        string? cellRef = null; // filled in once TOP's own layout dir is known, below
        var topDir = CreateCell(_originalDir, "TOP", v => v.Instances.Add(
            new LayoutInstance { CellRef = cellRef!, X = 200, Y = 200, Rot = rot, MirrorX = mirror, Mag = 1.0 }));

        // CreateCell needed TOP's own layout dir to exist before a correct relative CellRef could be
        // computed, so the instance above is rewritten in place with the real, computed path — never
        // a hand-typed "../Leaf", which is ONE level too shallow (Leaf and TOP are both direct
        // siblings under _originalDir, so the correct relative path from TOP's OWN layout/ folder is
        // "../../Leaf", not "../Leaf" — a hand-typed guess here previously produced an instance that
        // silently failed to resolve, and BOTH the "original" and "round-tripped" renders below drew
        // the identical broken-instance placeholder, passing the pixel-identity assertion vacuously
        // without ever exercising real geometry. Caught by the owner opening the sample export in
        // KLayout and seeing unresolved-reference placeholder text instead of real shapes.
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var realCellRef = Path.GetRelativePath(topLayoutDir, leafDir);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances[0].CellRef = realCellRef;
        LayoutPersistence.SaveToFile(Path.Combine(topLayoutDir, "TOP.clay"), topView);

        // Positively confirm the instance actually resolves — the ONLY way to be sure the pixel
        // comparison below exercises real geometry rather than two matching broken placeholders.
        var resolution = CellLayoutResolver.Resolve(realCellRef, topLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);

        var tech = MakeTech();
        var vp = new LayoutViewport(-500, -500, 0.5, 400, 400);
        var originalPixels = RenderPixels(topDir, "TOP", tech, vp);

        // Export the whole TOP hierarchy to a real .gds file, then import it fresh into a SEPARATE
        // directory — exercising the complete write+read pipeline, not just the transform codec.
        var plan = GdsiiExport.Analyze(topDir, tech, 1000);
        Assert.True(plan.CanWrite);
        var gdsPath = Path.Combine(_originalDir, "export.gds");
        GdsiiExport.Write(gdsPath, plan);

        using var stream = File.OpenRead(gdsPath);
        var importResult = GdsiiImport.Import(stream, _importedDir, tech, destDbuPerMicron: 1000, preferSourceResolution: false);
        Assert.False(importResult.Cancelled);

        var importedTopDir = Path.Combine(_importedDir, importResult.CellNameByStructureName["TOP"]);
        var importedTopCellName = importResult.CellNameByStructureName["TOP"];

        // Same positive-resolution check on the round-tripped side — the exported GDSII must
        // actually contain a resolvable SREF, not a dangling reference to a structure absent from
        // the file (which is exactly what a KLayout-visible "unresolved reference" placeholder means).
        var importedTopLayoutDir = CellFolder.SubFolderPath(importedTopDir, ViewType.Layout);
        var importedTopView = LayoutPersistence.LoadFromFile(Path.Combine(importedTopLayoutDir, $"{importedTopCellName}.clay"));
        var importedInst = Assert.Single(importedTopView.Instances);
        var importedResolution = CellLayoutResolver.Resolve(importedInst.CellRef, importedTopLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, importedResolution.State);

        var importedPixels = RenderPixels(importedTopDir, importedTopCellName, tech, vp);

        Assert.Equal(originalPixels, importedPixels);
    }
}
