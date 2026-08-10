// Owner reports, 2026-08-09:
//
//   (1) "Mesh does not render at low zoom levels. Need to see it at all zoom levels."
//   (2) "Mesh needs to be included in the bitmap/pdf/emf rendering to clipboard."
//   (3) "Pasting the selected geometry with ports has a glitch. In the pasted rendering I only see
//        pieces of the ports (they are cut off at the lower area) and I do not see my MLIN geometry."
//
// (1) had a real, measurable cause: below 2.5 device pixels per cell the overlay collapsed to the
// mesh's own bounding RECTANGLE — which lands exactly on the artwork's outline and therefore reads as
// "nothing rendered". Measured before the fix: 13,085 differing pixels at 3e-5 zoom, then 610 at 1e-5.
// It now decimates the real grid instead, so the picture degrades continuously rather than falling
// off a cliff.
//
// (3) was TWO faults with one shape: the graphic export built its transient view from payload.Shapes
// alone (so every placed PCell was absent), and sized its page from LayoutGeometry.BboxOf — and a
// LabelShape's stored bbox is a POINT, so the ports' glyphs and markers hung off the page.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMeshZoomAndExportTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private readonly string _root;

    public PlanarMeshZoomAndExportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crfMeshZoom_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static (LayoutView View, Technology Tech, LayerKey Signal) LineLayout()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var signal = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        var key = signal.DrawingLayers[0];
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });
        return (view, tech, key);
    }

    private static PlanarMeshReport BuildMesh(LayoutView view, Technology tech)
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-meshzoom.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource(Path.Combine(Path.GetTempPath(), "a.clay"), view, tech, Dbu),
        };
        vm.Refresh();
        vm.BuildPlanarMesh();
        return vm.PlanarMeshReport!;
    }

    /// <summary>Pixels that DIFFER between a render with the mesh and the identical render without —
    /// every differing pixel is mesh-attributable by construction, which a colour probe is not (the
    /// mesh is drawn over the metal it meshes).</summary>
    private static int MeshPixels(LayoutView view, Technology tech, PlanarMeshReport mesh, double zoom)
    {
        var vp = new LayoutViewport(-2_000_000, -2_000_000, zoom, 400, 400);

        using var withMesh = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(withMesh.Canvas, view, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            ShowPlanarMesh = true, PlanarMesh = mesh, BaseDir = "",
        });

        using var without = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(without.Canvas, view, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = "",
        });

        using var a = SKBitmap.FromImage(withMesh.Snapshot());
        using var b = SKBitmap.FromImage(without.Snapshot());

        int n = 0;
        for (int x = 0; x < 400; x++)
            for (int y = 0; y < 400; y++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    [Theory]
    [InlineData(3.0e-5)]
    [InlineData(1.0e-5)]
    [InlineData(3.0e-6)]
    [InlineData(1.0e-6)]
    public void TheMeshIsVisibleAtEveryZoom_NotOnlyWhenItsCellsClearThePixelFloor(double zoom)
    {
        var (view, tech, _) = LineLayout();
        var mesh = BuildMesh(view, tech);

        Assert.True(MeshPixels(view, tech, mesh, zoom) > 0,
            $"the mesh drew nothing at zoom {zoom:E1} — it must degrade, never disappear");
    }

    [Fact]
    public void BelowThePixelFloor_TheMeshStillReadsAsAGrid_NotAsABareOutline()
    {
        // The gate with teeth. At 1e-5 the design is ~200 x 29 device pixels, so a bounding-rectangle
        // outline — what this branch used to draw — is only a few hundred pixels of dashed perimeter
        // (measured: 610). A decimated GRID is several thousand. The threshold sits far above the
        // former and far below the latter, so neither reading can be mistaken for the other.
        var (view, tech, _) = LineLayout();
        var mesh = BuildMesh(view, tech);

        int painted = MeshPixels(view, tech, mesh, 1.0e-5);
        Assert.True(painted > 1500,
            $"expected a decimated grid below the pixel floor, got {painted} pixels — about what a " +
            "bare bounding rectangle would draw");
    }

    // ── The clipboard graphic ─────────────────────────────────────────────────────────────────

    private (LayoutFragment.Payload Payload, Technology Tech, string BaseDir) PortsPlusInstanceSelection()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var signal = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        var key = signal.DrawingLayers[0];

        // The owner's own shape: a generated cell holding the metal, instanced, with two port labels.
        var cellDir = CellFolder.CreateCellFolder(_root, "MLIN_gen");
        var cellView = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        cellView.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = -533_400, X2 = 10_185_400, Y2 = 533_400 });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), cellView);

        var parentDir = CellFolder.CreateCellFolder(_root, "MLin");
        string baseDir = CellFolder.SubFolderPath(parentDir, ViewType.Layout);

        var payload = new LayoutFragment.Payload { DbuPerMicron = Dbu };
        payload.Shapes.Add(new LabelShape
        {
            Layer = key, X = 0, Y = 0, Text = "P1", Height = 1_016_000,
            IsPort = true, PortDirection = LayoutRotation.R0,
        });
        payload.Shapes.Add(new LabelShape
        {
            Layer = key, X = 10_185_400, Y = 0, Text = "P2", Height = 1_016_000,
            IsPort = true, PortDirection = LayoutRotation.R180,
        });
        payload.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "MLIN_gen"), X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1,
        });
        return (payload, tech, baseDir);
    }

    [Fact]
    public void TheGraphicExport_IncludesInstancedGeometry_NotJustTheTopLevelShapes()
    {
        var (payload, tech, baseDir) = PortsPlusInstanceSelection();
        var ctx = LayoutClipboard.MakeExportContext(payload, tech, LayoutRenderTheme.Light, false, baseDir);

        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);

        // The instance's own metal must be in the page: without it the picture is two port glyphs and
        // nothing else, which is exactly what was reported.
        var shapesOnly = LayoutClipboard.MakeExportContext(
            StripInstances(payload), tech, LayoutRenderTheme.Light, false, baseDir);
        var svgNoInstance = LayoutClipboard.TryRenderToSvg(shapesOnly);
        Assert.NotNull(svgNoInstance);
        Assert.True(svg!.Value.Svg.Length > svgNoInstance!.Value.Svg.Length,
            "the instanced conductor must contribute geometry to the exported page");
    }

    [Fact]
    public void TheExportPage_IsSizedFromWhatIsPainted_SoPortGlyphsAreNotCropped()
    {
        // A LabelShape's stored bbox is a POINT. Sizing the page from raw geometry bounds is what
        // cropped the ports off the bottom; the page must span the port markers and their text too.
        var (payload, tech, baseDir) = PortsPlusInstanceSelection();

        var ctx = LayoutClipboard.MakeExportContext(payload, tech, LayoutRenderTheme.Light, false, baseDir);
        var bounds = LayoutClipboard.SelectionBoundsForTests(ctx);
        Assert.NotNull(bounds);

        // The conductor alone is 10,185,400 x 1,066,800 DBU. A port marker spans its width and the
        // label's text sits beside it, so the page must be TALLER than the bare metal.
        Assert.True(bounds!.Value.WorldH > 1_066_800,
            $"page height {bounds.Value.WorldH} does not clear the port markers (metal alone is 1,066,800)");
        Assert.True(bounds.Value.WorldW >= 10_185_400);
    }

    [Fact]
    public void TheMeshRidesAlongInTheGraphic_ButNeverInThePastePayload()
    {
        var (payload, tech, baseDir) = PortsPlusInstanceSelection();
        var mesh = BuildMesh(LineLayout().View, tech);

        var withMesh = LayoutClipboard.MakeExportContext(
            payload, tech, LayoutRenderTheme.Light, false, baseDir, mesh);
        var without = LayoutClipboard.MakeExportContext(
            payload, tech, LayoutRenderTheme.Light, false, baseDir);

        var a = LayoutClipboard.TryRenderToSvg(withMesh);
        var b = LayoutClipboard.TryRenderToSvg(without);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.Value.Svg.Length > b!.Value.Svg.Length, "the mesh must appear in the exported graphic");

        // …and the JSON — what circuitRF's own paste reads — carries no mesh at all. A mesh belongs to
        // an EM setup, not to geometry, so pasting into another layout must not bring one along.
        string json = LayoutFragment.Serialize(payload);
        Assert.DoesNotContain("Mesh", json, StringComparison.OrdinalIgnoreCase);
    }

    private static LayoutFragment.Payload StripInstances(LayoutFragment.Payload p)
    {
        var copy = new LayoutFragment.Payload { DbuPerMicron = p.DbuPerMicron };
        copy.Shapes.AddRange(p.Shapes);
        return copy;
    }
}
