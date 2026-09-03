using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 6 — a polygon with two holes exports as a HATCH with island boundaries and re-imports
/// with both holes intact (§3.1a R10b: each hole inside the outer ring, non-intersecting).</summary>
public class DxfHoleHatchTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static DxfReader ExportThenRead(LayoutShape shape)
    {
        var structures = new List<InterchangeStructure> { new("TOP", [shape], []) };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, structures, "TOP", null, 1000, new DxfExportOptions());
        using var sr = new StringReader(sw.ToString());
        return DxfReader.Read(sr);
    }

    [Fact]
    public void PolygonWithTwoHoles_ExportsAsHatch_ReimportsWithBothHolesIntact()
    {
        var poly = new PolygonShape
        {
            Layer = LayerA,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes =
            [
                [10_000, 10_000, 30_000, 10_000, 30_000, 30_000, 10_000, 30_000],
                [50_000, 50_000, 70_000, 50_000, 70_000, 70_000, 50_000, 70_000],
            ],
        };

        var reader = ExportThenRead(poly);
        var imported = Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape;
        var importedPoly = Assert.IsType<PolygonShape>(imported);

        Assert.NotNull(importedPoly.Holes);
        Assert.Equal(2, importedPoly.Holes!.Count);

        // Outer ring area matches (within a whole-DBU rounding tolerance).
        double origArea = Math.Abs(LayoutGeometry.SignedArea(poly.Xy)) / 2.0;
        double impArea = Math.Abs(LayoutGeometry.SignedArea(importedPoly.Xy)) / 2.0;
        Assert.Equal(origArea, impArea, 1.0);

        foreach (var hole in importedPoly.Holes)
            Assert.True(AllPointsInsideRing(hole, importedPoly.Xy), "Every hole vertex must lie inside the outer ring (§3.1a R10b).");
    }

    [Fact]
    public void ArcBearingCurveWithHole_ExportsAsHatch_ArcSurvivesExactly()
    {
        double bulge = Math.Tan(Math.PI / 2.0 / 4.0);
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
            Holes = [[20_000, 20_000, 40_000, 20_000, 40_000, 40_000, 20_000, 40_000]],
        };

        var reader = ExportThenRead(curve);
        var imported = Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape;
        var importedCurve = Assert.IsType<CurveShape>(imported);
        Assert.NotNull(importedCurve.Holes);
        Assert.Single(importedCurve.Holes!);
        Assert.NotNull(importedCurve.Edges);
        Assert.Contains(importedCurve.Edges!, e => e.Kind == EdgeKind.Arc && Math.Abs(e.Bulge - bulge) < 1e-9);
    }

    private static bool AllPointsInsideRing(long[] hole, long[] outer)
    {
        for (int i = 0; i < hole.Length; i += 2)
            if (!PointInPolygon(hole[i], hole[i + 1], outer)) return false;
        return true;
    }

    private static bool PointInPolygon(long px, long py, long[] ring)
    {
        int n = ring.Length / 2;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            bool crosses = (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }
}
