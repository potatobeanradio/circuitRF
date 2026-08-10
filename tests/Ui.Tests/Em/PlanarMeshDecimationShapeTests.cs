// Owner report, 2026-08-09: "The mesh for the MKLOPF looks wrong when zoomed out (appears as a rect),
// but appears correct when I zoom in a little."
//
// The low-zoom branch decimates the real grid rather than collapsing to a bounding rectangle — the
// fix for the PREVIOUS mesh-at-low-zoom report. But it emitted each kept vertical across the mesh's
// whole y-extent and each kept horizontal across its whole x-extent. For a straight MLIN that is
// right by accident: its metal FILLS its own bounding box, so a full-span line lies on real cell
// edges the whole way. A TAPER's cells occupy a wedge inside a far larger box, so the full-span lines
// painted the box — the bounding rectangle that decimation exists to avoid, arriving back through a
// different door. Zooming in leaves the branch entirely, which is why it appeared to fix itself.
//
// These tests are shaped around that distinction: a MESH THAT DOES NOT FILL ITS OWN BOUNDING BOX,
// rendered below the pixel floor, must leave the empty corners empty.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMeshDecimationShapeTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// A taper: 200 µm wide at x = 0, 4000 µm wide at the far end. Its bounding box is many times the
    /// area of the metal, which is the property the bug turned on — an MLIN cannot express it.
    /// </summary>
    private static (LayoutView View, Technology Tech) TaperLayout()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var signal = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        var key = signal.DrawingLayers[0];

        const long len = 20_000L * Dbu;
        const long narrow = 100L * Dbu;     // half-width at x = 0
        const long wide = 2_000L * Dbu;     // half-width at x = len

        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        view.Shapes.Add(new PolygonShape
        {
            Layer = key,
            Xy = [0, narrow, len, wide, len, -wide, 0, -narrow],
        });
        return (view, tech);
    }

    private static PlanarMeshReport BuildMesh(LayoutView view, Technology tech)
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-decim.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource(Path.Combine(Path.GetTempPath(), "a.clay"), view, tech, Dbu),
        };
        vm.Refresh();
        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);
        return vm.PlanarMeshReport;
    }

    private const int Size = 400;

    /// <summary>Mesh-attributable pixels, as a bitmap: the differential of a render with the mesh
    /// against the identical render without. Every set pixel is the mesh's by construction — a colour
    /// probe could not say that, since the mesh is drawn over the metal it meshes.</summary>
    private static bool[,] MeshMask(LayoutView view, Technology tech, PlanarMeshReport mesh, LayoutViewport vp)
    {
        using var withMesh = SKSurface.Create(new SKImageInfo(Size, Size));
        LayoutRenderer.Draw(withMesh.Canvas, view, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            ShowPlanarMesh = true, PlanarMesh = mesh, BaseDir = "",
        });

        using var without = SKSurface.Create(new SKImageInfo(Size, Size));
        LayoutRenderer.Draw(without.Canvas, view, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = "",
        });

        using var a = SKBitmap.FromImage(withMesh.Snapshot());
        using var b = SKBitmap.FromImage(without.Snapshot());

        var mask = new bool[Size, Size];
        for (int x = 0; x < Size; x++)
            for (int y = 0; y < Size; y++)
                mask[x, y] = a.GetPixel(x, y) != b.GetPixel(x, y);
        return mask;
    }

    private static int CountIn(bool[,] mask, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int x = Math.Max(0, x0); x < Math.Min(Size, x1); x++)
            for (int y = Math.Max(0, y0); y < Math.Min(Size, y1); y++)
                if (mask[x, y]) n++;
        return n;
    }

    /// <summary>A viewport framing the taper's own bounding box with a little margin, at a zoom low
    /// enough that the mesh is below <c>PlanarMeshMinCellDevicePixels</c> and therefore decimated.</summary>
    private static LayoutViewport LowZoom(LayoutView view)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        return LayoutViewport.ZoomToFit(bb, Size, Size, 0.05);
    }

    /// <summary>
    /// Screen rectangle for a world rectangle, clamped to the surface — so a probe region is stated
    /// in the geometry's OWN coordinates rather than guessed as a fraction of the canvas. That guess
    /// is what made the first version of this test vacuous: the taper is 5:1, so zoom-to-fit leaves
    /// the top and bottom quarters of a square canvas empty of the mesh's BOUNDING BOX too, and the
    /// probe passed with the bug fully present.
    /// </summary>
    private static (int X0, int Y0, int X1, int Y1) ScreenRect(
        LayoutViewport vp, double wx0, double wy0, double wx1, double wy1)
    {
        int x0 = (int)Math.Floor(vp.WorldToScreenX(Math.Min(wx0, wx1)));
        int x1 = (int)Math.Ceiling(vp.WorldToScreenX(Math.Max(wx0, wx1)));
        // Screen Y is flipped, so the world MAXIMUM maps to the smaller screen row.
        int y0 = (int)Math.Floor(vp.WorldToScreenY(Math.Max(wy0, wy1)));
        int y1 = (int)Math.Ceiling(vp.WorldToScreenY(Math.Min(wy0, wy1)));
        return (x0, y0, x1, y1);
    }

    // ── The headline ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADecimatedTaperMesh_LeavesTheEmptyPARTOFItsBoundingBoxEMPTY()
    {
        var (view, tech) = TaperLayout();
        var mesh = BuildMesh(view, tech);
        var vp = LowZoom(view);

        // Confirm this render really is on the decimated branch — otherwise the test proves nothing.
        double pxPerMetre = vp.Zoom * Dbu * 1e6;
        Assert.True(Math.Min(mesh.MinCellEdgeM, mesh.MaxCellEdgeM) * pxPerMetre < 2.5,
                    "fixture is not below the pixel floor — it would not exercise decimation");

        var mask = MeshMask(view, tech, mesh, vp);
        Assert.True(CountIn(mask, 0, 0, Size, Size) > 200, "the mesh did not render at low zoom");

        // Inside the bounding box, OUTSIDE the metal: over the narrow end, between the taper's own
        // edge and the box's top. At x ≤ 10% of the length the half-width is under 290 µm, so the
        // band y ∈ [1000, 2000] µm holds no cells at all — while the box reaches ±2000 µm throughout.
        // Full-span verticals painted straight through here; real cell edges cannot.
        const long len = 20_000L * Dbu;
        var (px0, py0, px1, py1) = ScreenRect(vp, 0, 1_000L * Dbu, len / 10, 2_000L * Dbu);
        int aboveNarrowEnd = CountIn(mask, px0, py0, px1, py1);

        Assert.True(px1 - px0 >= 8 && py1 - py0 >= 3,
                    $"probe region is too small to be meaningful ({px1 - px0}×{py1 - py0} px)");
        Assert.Equal(0, aboveNarrowEnd);
    }

    [Fact]
    public void TheDecimatedMesh_StillCoversTheMetalItMeshes()
    {
        // The complement of the test above: thinning must not become hiding. The wide end of the
        // taper is solid metal and must carry mesh pixels.
        var (view, tech) = TaperLayout();
        var mesh = BuildMesh(view, tech);
        var vp = LowZoom(view);
        var mask = MeshMask(view, tech, mesh, vp);

        const long len = 20_000L * Dbu;
        var (qx0, qy0, qx1, qy1) = ScreenRect(vp, len * 9 / 10, -1_500L * Dbu, len, 1_500L * Dbu);
        int wideEnd = CountIn(mask, qx0, qy0, qx1, qy1);
        Assert.True(wideEnd > 20, $"the wide end should be meshed; got {wideEnd} pixels");
    }

    [Fact]
    public void AStraightLine_IsUnaffected_ItsMetalFillsItsOwnBox()
    {
        // The case that was right before this fix and must stay right: a rectangle's cells DO fill
        // its bounding box, so its decimated mesh legitimately spans corner to corner.
        var tech = StarterTechnologies.Pcb2Layer();
        var signal = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        view.Shapes.Add(new RectShape
        { Layer = signal.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 20_000L * Dbu, Y2 = 2_900L * Dbu });

        var mesh = BuildMesh(view, tech);
        var mask = MeshMask(view, tech, mesh, LowZoom(view));

        Assert.True(CountIn(mask, 0, 0, Size, Size) > 200, "the mesh did not render at low zoom");
    }
}
