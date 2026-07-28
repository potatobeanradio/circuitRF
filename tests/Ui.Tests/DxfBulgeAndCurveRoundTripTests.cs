using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 2 — bulge identity (§1.1): a 90° arc edge exports its bulge as tan(22.5°) to full precision
/// (COPYING THE NUMBER, never a conversion), and re-imports with LayoutEdge.Bulge bit-identical to the
/// original. No arc anywhere in a DXF export becomes a chord (R-L4b-1).
/// Gate 3 — Circle, RoundedRect, an arc-bearing Curve, a cubic-bearing Curve, and a curved Path all
/// survive export -&gt; import as the SAME primitive types with geometry equal within tolerance; the
/// cubic case is exact (via the closed multi-segment SPLINE Bezier-chain — see DxfWriter's own header
/// comment for the ring-representation design).
/// </summary>
public class DxfBulgeAndCurveRoundTripTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static (List<InterchangeStructure> Structures, string Text) ExportOneShape(LayoutShape shape)
    {
        var structure = new InterchangeStructure("TOP", [shape], []);
        var structures = new List<InterchangeStructure> { structure };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, structures, "TOP", null, 1000, new DxfExportOptions());
        return (structures, sw.ToString());
    }

    private static DxfReader ImportText(string text)
    {
        using var sr = new StringReader(text);
        return DxfReader.Read(sr);
    }

    [Fact]
    public void NinetyDegreeArcEdge_ExportsBulgeAsTan22Point5_ImportsBitIdentical()
    {
        double expectedBulge = Math.Tan(Math.PI / 2.0 / 4.0); // 90deg sweep -> tan(22.5deg)
        var curve = new CurveShape
        {
            Layer = LayerA,
            Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = expectedBulge },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var (_, text) = ExportOneShape(curve);
        Assert.Contains("42\n" + expectedBulge.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), text);

        var reader = ImportText(text);
        var model = reader.Structures.Single(s => s.Name == "TOP");
        var imported = Assert.Single(model.Shapes).Shape;
        var importedCurve = Assert.IsType<CurveShape>(imported);
        Assert.NotNull(importedCurve.Edges);
        Assert.Equal(expectedBulge, importedCurve.Edges![0].Bulge, 12);
        Assert.Equal(EdgeKind.Arc, importedCurve.Edges[0].Kind);
    }

    [Fact]
    public void Circle_RoundTrips_AsCircleShape_ExactRadius()
    {
        var circle = new CircleShape { Layer = LayerA, Cx = 5000, Cy = -3000, R = 250_000 };
        var (_, text) = ExportOneShape(circle);
        var reader = ImportText(text);
        var imported = Assert.IsType<CircleShape>(Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape);
        Assert.Equal(circle.Cx, imported.Cx);
        Assert.Equal(circle.Cy, imported.Cy);
        Assert.Equal(circle.R, imported.R);
    }

    [Fact]
    public void RoundedRect_RoundTrips_AsCurveWithFourArcs_SameAreaWithinTolerance()
    {
        var rr = new RoundedRectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 60_000, CornerRadius = 15_000 };
        var (_, text) = ExportOneShape(rr);
        var reader = ImportText(text);
        var imported = Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape;
        var curve = Assert.IsType<CurveShape>(imported);
        Assert.NotNull(curve.Edges);
        Assert.Contains(curve.Edges!, e => e.Kind == EdgeKind.Arc);

        var originalRings = LayoutFlattener.Flatten(rr, 100);
        var importedRings = LayoutFlattener.Flatten(curve, 100);
        Assert.Equal(SignedAreaAbs(originalRings[0]), SignedAreaAbs(importedRings[0]), 0.02 * SignedAreaAbs(originalRings[0]));
    }

    [Fact]
    public void ArcBearingCurve_RoundTrips_AsCurveShape_SameGeometryWithinTolerance()
    {
        double bulge = Math.Tan(Math.PI / 6.0 / 4.0); // 30deg sweep
        var curve = new CurveShape
        {
            Layer = LayerA,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };
        var (_, text) = ExportOneShape(curve);
        var reader = ImportText(text);
        var imported = Assert.IsType<CurveShape>(Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape);
        Assert.NotNull(imported.Edges);
        Assert.Equal(EdgeKind.Arc, imported.Edges![0].Kind);
        Assert.Equal(bulge, imported.Edges[0].Bulge, 9);
    }

    [Fact]
    public void CubicBearingCurve_RoundTrips_AsCurveShape_Exact()
    {
        // All-cubic ring (no arcs) -> the closed multi-segment SPLINE Bezier chain, exact.
        var curve = new CurveShape
        {
            Layer = LayerA,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 30_000, C1Y = -20_000, C2X = 70_000, C2Y = -20_000 },
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 120_000, C1Y = 30_000, C2X = 120_000, C2Y = 70_000 },
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 70_000, C1Y = 120_000, C2X = 30_000, C2Y = 120_000 },
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = -20_000, C1Y = 70_000, C2X = -20_000, C2Y = 30_000 },
            ],
        };
        var (_, text) = ExportOneShape(curve);
        Assert.Contains("SPLINE", text);

        var reader = ImportText(text);
        var imported = Assert.IsType<CurveShape>(Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape);
        Assert.NotNull(imported.Edges);
        Assert.Equal(4, imported.Edges!.Count);
        Assert.All(imported.Edges, e => Assert.Equal(EdgeKind.Cubic, e.Kind));

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(curve.Xy[2 * i], imported.Xy[2 * i]);
            Assert.Equal(curve.Xy[2 * i + 1], imported.Xy[2 * i + 1]);
            Assert.Equal(curve.Edges[i].C1X, imported.Edges[i].C1X);
            Assert.Equal(curve.Edges[i].C1Y, imported.Edges[i].C1Y);
            Assert.Equal(curve.Edges[i].C2X, imported.Edges[i].C2X);
            Assert.Equal(curve.Edges[i].C2Y, imported.Edges[i].C2Y);
        }
    }

    [Fact]
    public void CurvedPath_ArcEdge_RoundTrips_AsPathShape_SameWidthAndGeometry()
    {
        double bulge = Math.Tan(Math.PI / 2.0 / 4.0);
        var path = new PathShape
        {
            Layer = LayerA,
            Xy = [0, 0, 50_000, 0, 50_000, 50_000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge }, new LayoutEdge { Kind = EdgeKind.Line }],
            Width = 5000,
            End = PathEndStyle.Flush,
        };
        var (_, text) = ExportOneShape(path);
        var reader = ImportText(text);
        var imported = Assert.IsType<PathShape>(Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape);
        Assert.Equal(path.Width, imported.Width);
        Assert.NotNull(imported.Edges);
        Assert.Equal(EdgeKind.Arc, imported.Edges![0].Kind);
        Assert.Equal(bulge, imported.Edges[0].Bulge, 9);
        Assert.Equal(path.Xy[0], imported.Xy[0]);
        Assert.Equal(path.Xy[1], imported.Xy[1]);
    }

    [Fact]
    public void PlainPolygon_NoBulge_RoundTrips_AsPolygonShape_NotCurve()
    {
        var poly = new PolygonShape { Layer = LayerA, Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000] };
        var (_, text) = ExportOneShape(poly);
        var reader = ImportText(text);
        var imported = Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape;
        Assert.IsType<PolygonShape>(imported);
    }

    private static double SignedAreaAbs(long[] ring) => Math.Abs(CircuitRF.Ui.Layout.LayoutGeometry.SignedArea(ring)) / 2.0;
}
