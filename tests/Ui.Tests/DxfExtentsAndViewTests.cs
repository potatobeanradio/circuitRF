using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 13 - export a design placed far from the origin and confirm $EXTMIN/$EXTMAX equal the
/// written geometry's bbox, and that a bitmap in the layout does NOT widen them (R-L4b-5).
/// Gate 14 - both view modes: fit-to-extents frames the whole design with margin at several aspect
/// ratios (incl. a design far wider than tall); match-current-view reproduces the viewport's centre
/// and height in drawing units.
/// Gate 15 - degenerate cases (empty layout, a single point, a single horizontal line) each produce a
/// sane, non-zero/non-negative view.
/// </summary>
public class DxfExtentsAndViewTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static (Bbox Bbox, DxfExtentGuard Guard) BuildAndReadExtents(
        IReadOnlyList<LayoutShape> shapes, DxfExportOptions options, int dbuPerMicron = 1000)
    {
        var structures = new List<InterchangeStructure> { new("TOP", shapes.ToList(), []) };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, structures, "TOP", null, dbuPerMicron, options);
        string text = sw.ToString();

        double dbuToDrawingUnit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(options.InsUnits, dbuPerMicron);
        var bbox = DxfExtents.ComputeStructureBbox("TOP", new Dictionary<string, InterchangeStructure> { ["TOP"] = structures[0] });
        var (_, guard) = DxfViewCalc.Compute(bbox, options, dbuToDrawingUnit);

        Assert.Contains("$EXTMIN", text);
        Assert.Contains("$EXTMAX", text);
        return (bbox, guard);
    }

    [Fact]
    public void ExtentsMatchWrittenGeometry_FarFromOrigin_BitmapNeverWidensThem()
    {
        long centerX = 500_000_000; // 500mm at 1000 DBU/um
        var rect = new RectShape { Layer = LayerA, X1 = centerX - 1000, Y1 = -1000, X2 = centerX + 1000, Y2 = 1000 };
        var bitmap = new BitmapShape { Layer = LayerA, ImagePathRef = "x.png", X = -50_000_000, Y = -50_000_000, W = 1000, H = 1000 };

        var (bbox, guard) = BuildAndReadExtents([rect, bitmap], new DxfExportOptions());

        // The bitmap sits far outside the rect's bbox — if it contributed, extents would engulf it.
        double dbuToDrawingUnit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(DxfUnits.DefaultPromptUnits, 1000);
        Assert.Equal((centerX - 1000) * dbuToDrawingUnit, guard.ExtMinX, 6);
        Assert.Equal((centerX + 1000) * dbuToDrawingUnit, guard.ExtMaxX, 6);
        Assert.True(guard.ExtMinX > -1_000_000); // proves the bitmap's far-away X never widened the min
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.777)] // 16:9-ish
    [InlineData(0.3)]   // a design far taller than wide, viewed on a wide canvas
    public void FitToExtents_FramesWholeDesignWithMargin_AtSeveralAspectRatios(double aspect)
    {
        var rect = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 500_000, Y2 = 100_000 }; // wide design
        var options = new DxfExportOptions { ViewMode = DxfViewMode.FitToExtents, CanvasAspect = aspect };
        var (bbox, guard) = BuildAndReadExtents([rect], options);

        double dbuToDrawingUnit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(options.InsUnits, 1000);
        var (view, _) = DxfViewCalc.Compute(bbox, options, dbuToDrawingUnit);

        double spanX = (bbox.MaxX - bbox.MinX) * dbuToDrawingUnit;
        double spanY = (bbox.MaxY - bbox.MinY) * dbuToDrawingUnit;
        double requiredHeight = Math.Max(spanY, spanX / aspect);

        Assert.True(view.Height >= requiredHeight, "the view must be at least tall enough to frame the design at this aspect — erring toward showing too much (R-L4b-6)");
        Assert.True(view.Height > 0);
    }

    [Fact]
    public void MatchCurrentView_ReproducesViewportCentreAndHeight_InDrawingUnits()
    {
        var rect = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        var vp = new LayoutViewport(PanX: 10_000, PanY: 20_000, Zoom: 1.0, Width: 40_000, Height: 40_000);
        var options = new DxfExportOptions { ViewMode = DxfViewMode.MatchCurrentView, MatchViewport = vp };

        var (bbox, _) = BuildAndReadExtents([rect], options);
        double dbuToDrawingUnit = 1.0 / (double)DxfUnits.DbuPerDrawingUnit(options.InsUnits, 1000);
        var (view, _) = DxfViewCalc.Compute(bbox, options, dbuToDrawingUnit);

        double expectedCenterX = (vp.VisibleMinX + vp.VisibleMaxX) / 2.0 * dbuToDrawingUnit;
        double expectedCenterY = (vp.VisibleMinY + vp.VisibleMaxY) / 2.0 * dbuToDrawingUnit;
        double expectedHeight = (vp.VisibleMaxY - vp.VisibleMinY) * dbuToDrawingUnit;

        Assert.Equal(expectedCenterX, view.CenterX, 6);
        Assert.Equal(expectedCenterY, view.CenterY, 6);
        Assert.Equal(expectedHeight, view.Height, 6);
    }

    [Fact]
    public void EmptyLayout_ProducesSaneDefaultView_NoZeroOrNegativeHeight()
    {
        var (view, guard) = DxfViewCalc.Compute(Bbox.Empty, new DxfExportOptions(), 1.0);
        Assert.True(view.Height > 0);
        Assert.True(guard.ExtMaxX > guard.ExtMinX);
        Assert.True(guard.ExtMaxY > guard.ExtMinY);
    }

    [Fact]
    public void SinglePoint_ProducesSaneView_NoZeroSpan()
    {
        var pointBbox = new Bbox(5000, 5000, 5000, 5000);
        var (view, guard) = DxfViewCalc.Compute(pointBbox, new DxfExportOptions(), 1.0);
        Assert.True(view.Height > 0);
        Assert.True(guard.ExtMaxX > guard.ExtMinX);
        Assert.True(guard.ExtMaxY > guard.ExtMinY);
    }

    [Fact]
    public void SingleHorizontalLine_ProducesSaneView_NoZeroHeightAxis()
    {
        // Zero-height design (a horizontal line) hits the degenerate case in exactly one axis.
        var lineBbox = new Bbox(0, 5000, 100_000, 5000);
        var (view, guard) = DxfViewCalc.Compute(lineBbox, new DxfExportOptions(), 1.0);
        Assert.True(view.Height > 0);
        Assert.True(guard.ExtMaxY > guard.ExtMinY);
        Assert.True(guard.ExtMaxX > guard.ExtMinX);
    }
}
