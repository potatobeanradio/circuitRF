using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Coverage for two DxfWriter/DxfExportOptions paths that shipped with zero tests: the narrow
/// mixed-Arc-and-Cubic-in-one-ring fallback (DxfWriter.WriteCurve, counted via
/// DxfExportSummary.MixedArcCubicApproximated), and the two opt-in export flags
/// (FlattenSplinesToPolyline, PathAsOutlinePolygon). Both are implemented and wired through the
/// export dialog but were previously exercised by nothing.
/// </summary>
public class DxfExportOptionsCoverageTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static (DxfExportSummary Summary, string Text) Export(LayoutShape shape, DxfExportOptions options)
    {
        var structures = new List<InterchangeStructure> { new("TOP", [shape], []) };
        using var sw = new StringWriter();
        var summary = DxfWriter.Write(sw, structures, "TOP", null, 1000, options);
        return (summary, sw.ToString());
    }

    private static DxfImportedShape ImportOneShape(string text)
    {
        using var sr = new StringReader(text);
        var reader = DxfReader.Read(sr);
        return Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes);
    }

    [Fact]
    public void MixedArcAndCubicInSameRing_FlattensOnlyTheCubic_ArcBulgeStaysExact_ReportsApproximation()
    {
        double bulge = Math.Tan(Math.PI / 2.0 / 4.0); // 90deg sweep
        var curve = new CurveShape
        {
            Layer = LayerA,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 120_000, C1Y = 30_000, C2X = 120_000, C2Y = 70_000 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var (summary, text) = Export(curve, new DxfExportOptions());

        // Neither a SPLINE (can't carry the arc) nor a plain bulge-per-vertex-only case — this is the
        // one combination genuinely unrepresentable by either single-entity form.
        Assert.Equal(1, summary.MixedArcCubicApproximated);
        Assert.DoesNotContain("SPLINE", text);
        Assert.Contains("LWPOLYLINE", text);

        var imported = Assert.IsType<CurveShape>(ImportOneShape(text).Shape);
        Assert.NotNull(imported.Edges);
        // The arc edge itself must never be touched by the cubic-only flatten (R-L4b-1).
        Assert.Contains(imported.Edges!, e => e.Kind == EdgeKind.Arc && Math.Abs(e.Bulge - bulge) < 1e-9);
        // The cubic edge is gone — approximated to one or more straight chords, never a Cubic edge.
        Assert.DoesNotContain(imported.Edges!, e => e.Kind == EdgeKind.Cubic);
        // The ring's start/end vertices survive exactly; only the interior cubic shape is approximate.
        Assert.Equal(curve.Xy[0], imported.Xy[0]);
        Assert.Equal(curve.Xy[1], imported.Xy[1]);
    }

    [Fact]
    public void FlattenSplinesToPolyline_Option_ExportsLwpolylineNotSpline_ReportsFlag_ImportsAsPolygonNotCurve()
    {
        // All-cubic ring (no arc, no holes) — the default path (tested elsewhere) emits an exact
        // closed SPLINE chain. With FlattenSplinesToPolyline set, the same ring must instead flatten
        // to a plain closed LWPOLYLINE, at the cost of exactness.
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

        var (defaultSummary, defaultText) = Export(curve, new DxfExportOptions());
        Assert.False(defaultSummary.SplineFlattenedToPolyline);
        Assert.Contains("SPLINE", defaultText);

        var (flattenedSummary, flattenedText) = Export(curve, new DxfExportOptions(FlattenSplinesToPolyline: true));
        Assert.True(flattenedSummary.SplineFlattenedToPolyline);
        Assert.DoesNotContain("SPLINE", flattenedText);
        Assert.Contains("LWPOLYLINE", flattenedText);

        // Flattened form loses the exact cubic — it re-imports as a plain PolygonShape (an
        // all-Line ring), not the CurveShape the un-flattened SPLINE path round-trips to exactly.
        var imported = ImportOneShape(flattenedText).Shape;
        Assert.IsType<PolygonShape>(imported);

        // The flattened polygon still approximates the original curve's TIGHT extent closely — compare
        // against LayoutFlattener's own tight-tessellation bbox, not LayoutGeometry.BboxOf's conservative
        // convex-hull-of-control-points bound (which is deliberately looser than the true curve).
        var referenceRing = LayoutFlattener.Flatten(curve, LayoutFlattener.DefaultTolDbu)[0];
        var referenceBbox = RingBbox(referenceRing);
        var importedBbox = LayoutGeometry.BboxOf(imported);
        long tol = 2000; // 2000 DBU = 2 um at 1000 DBU/um — comfortably inside flatten tolerance
        Assert.InRange(importedBbox.MinX, referenceBbox.MinX - tol, referenceBbox.MinX + tol);
        Assert.InRange(importedBbox.MaxX, referenceBbox.MaxX - tol, referenceBbox.MaxX + tol);
    }

    [Fact]
    public void PathAsOutlinePolygon_Option_ExportsClosedOutlineInsteadOfWidthPolyline_ImportsAsPolygon()
    {
        var path = new PathShape
        {
            Layer = LayerA,
            Xy = [0, 0, 10_000, 0],
            Width = 1000,
            End = PathEndStyle.Flush,
        };

        // Default: parametric open LWPOLYLINE with a constant-width group (43), centerline intact.
        var (defaultSummary, defaultText) = Export(path, new DxfExportOptions());
        Assert.Equal(0, defaultSummary.PathsFlattenedForCubic);
        var defaultImported = ImportOneShape(defaultText).Shape;
        Assert.IsType<PathShape>(defaultImported);

        // §1.2's outline option: the SAME path exports as its geometric outline (a closed polygon),
        // trading the editable centerline for an exact stroked-outline shape.
        var (_, outlineText) = Export(path, new DxfExportOptions(PathAsOutlinePolygon: true));
        Assert.Contains("LWPOLYLINE", outlineText);

        var outlineImported = ImportOneShape(outlineText).Shape;
        var outlinePoly = Assert.IsType<PolygonShape>(outlineImported); // closed, no width group -> polygon, not a Path

        // A flush-capped straight horizontal path of width 1000 inflates to a 10000 x 1000 rectangle
        // centered on the centerline — verify the outline's bbox matches that, not the zero-height
        // centerline bbox a Path's own Xy would report.
        var bbox = LayoutGeometry.BboxOf(outlinePoly);
        long tol = 50; // integer Clipper2 rounding
        Assert.InRange(bbox.MinX, 0 - tol, 0 + tol);
        Assert.InRange(bbox.MaxX, 10_000 - tol, 10_000 + tol);
        Assert.InRange(bbox.MinY, -500 - tol, -500 + tol);
        Assert.InRange(bbox.MaxY, 500 - tol, 500 + tol);
    }

    private static Bbox RingBbox(long[] ring)
    {
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        for (int i = 0; i < ring.Length; i += 2)
        {
            minX = Math.Min(minX, ring[i]); maxX = Math.Max(maxX, ring[i]);
            minY = Math.Min(minY, ring[i + 1]); maxY = Math.Max(maxY, ring[i + 1]);
        }
        return new Bbox(minX, minY, maxX, maxY);
    }
}
