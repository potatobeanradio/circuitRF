using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates 3, 4, 6, 7 (brief-L4a-gdsii-interchange.md): every primitive, holes, arcs, path end styles,
/// labels, plain instances and arrays round-trip through <see cref="GdsiiWriter"/>/<see
/// cref="GdsiiReader"/> to geometry equal to the original modulo the four documented conversions
/// (curve flattening, keyholing, bitmap omission, label-as-TEXT); boundaries are explicitly closed;
/// keyholed area matches within tolerance.
/// </summary>
public class LayoutGdsiiRoundTripTests
{
    private static readonly GdsiiUnits Units = new(0.000001, 1e-9); // 1 µm user unit, 1 nm DBU

    private static (GdsiiUnits units, List<InterchangeStructure> structures) WriteThenRead(
        IReadOnlyList<InterchangeStructure> structures)
    {
        using var ms = new MemoryStream();
        GdsiiWriter.Write(ms, structures, Units, tech: null);
        ms.Position = 0;

        var reader = GdsiiReader.Open(ms);
        var result = reader.ReadStructures().ToList();
        return (reader.Units, result);
    }

    [Fact]
    public void Rect_RoundTrips_AsClosedBoundaryPolygon()
    {
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 500 };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [rect], [])]);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(structs[0].Shapes));
        Assert.Equal(new LayerKey(1, 0), poly.Layer);
        Assert.Equal(new long[] { 0, 0, 1000, 0, 1000, 500, 0, 500 }, poly.Xy);
    }

    [Fact]
    public void PolygonWithHoles_RoundTrips_ToOneKeyholedContour_AreaWithinTolerance()
    {
        // A 1000x1000 square (CCW) with a 200x200 hole (CW — opposite winding, per R10b's convention
        // for a valid hole ring) — area = 1,000,000 - 40,000 = 960,000.
        var poly = new PolygonShape
        {
            Layer = new LayerKey(2, 0),
            Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
            Holes = [[400, 600, 600, 600, 600, 400, 400, 400]],
        };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [poly], [])]);

        var result = Assert.IsType<PolygonShape>(Assert.Single(structs[0].Shapes));
        Assert.Null(result.Holes); // GDSII cannot express holes — re-import stays keyholed (§3.1a)

        double area = Math.Abs(ShoelaceArea(result.Xy));
        Assert.InRange(area, 950_000, 970_000); // within tolerance of the true 960,000
    }

    [Fact]
    public void Boundary_IsExplicitlyClosed_FirstPointEqualsLast()
    {
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 };
        using var ms = new MemoryStream();
        GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [rect], [])], Units, null);
        ms.Position = 0;

        var records = new GdsiiRecordReader(ms);
        while (records.TryReadNext(out var rec))
        {
            if (rec.Type != GdsiiRecordType.Xy) continue;
            var xy = rec.AsInt32Array();
            Assert.Equal(xy[0], xy[^2]);
            Assert.Equal(xy[1], xy[^1]);
            return;
        }
        Assert.Fail("No XY record found for the BOUNDARY.");
    }

    [Fact]
    public void CircleAndRoundedRect_FlattenOnExport_AndReimportAsPolygon()
    {
        // Radius comfortably larger than the default 1000-DBU flatten tolerance so the sagitta-bounded
        // subdivision actually approximates a circle (a radius near/below the tolerance legitimately
        // flattens to as few as 3 segments — that is correct behavior, not a bug, just the wrong shape
        // for this test's "does it approximate pi*r^2" assertion).
        var circle = new CircleShape { Layer = new LayerKey(3, 0), Cx = 0, Cy = 0, R = 50_000 };
        var rr = new RoundedRectShape { Layer = new LayerKey(3, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 100 };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [circle, rr], [])]);

        Assert.Equal(2, structs[0].Shapes.Count);
        Assert.All(structs[0].Shapes, s => Assert.IsType<PolygonShape>(s));
        var circlePoly = (PolygonShape)structs[0].Shapes[0];
        double area = Math.Abs(ShoelaceArea(circlePoly.Xy));
        double expected = Math.PI * 50_000 * 50_000;
        Assert.InRange(area, expected * 0.95, expected * 1.02); // inscribed-polygon approximation underestimates
    }

    [Fact]
    public void ArcBearingCurve_FlattensOnExport_AndReimportsAsPolygonWithinTolerance()
    {
        // A closed curve built from 4 quarter-arcs (radius 50,000 — well above the default 1000-DBU
        // tolerance) — a genuine arc-bearing Curve, distinct from Circle/RoundedRect.
        // Bulge = tan(sweep/4); a 90° quarter-arc is tan(22.5°) — the same constant
        // LayoutRendererTests.ClosedCurve_OfFourQuarterArcs_FillsLikeACircle uses.
        const long r = 50_000;
        double bulge = Math.Tan(Math.PI / 8.0);
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [r, 0, 0, r, -r, 0, 0, -r],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
            ],
        };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [curve], [])]);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(structs[0].Shapes));
        double area = Math.Abs(ShoelaceArea(poly.Xy));
        double expected = Math.PI * r * r;
        Assert.InRange(area, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void PolygonWithTwoHoles_KeyholesIntoOneContour_AreaWithinTolerance_CountReported()
    {
        // 2000x2000 square (CCW) with two 200x200 holes (CW) — area = 4,000,000 - 2*40,000 = 3,920,000.
        var poly = new PolygonShape
        {
            Layer = new LayerKey(2, 0),
            Xy = [0, 0, 2000, 0, 2000, 2000, 0, 2000],
            Holes =
            [
                [400, 600, 600, 600, 600, 400, 400, 400],
                [1400, 1600, 1600, 1600, 1600, 1400, 1400, 1400],
            ],
        };

        using var ms = new MemoryStream();
        var summary = GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [poly], [])], Units, null);
        Assert.Equal(2, summary.HolesKeyholed);

        ms.Position = 0;
        var reader = GdsiiReader.Open(ms);
        var structs = reader.ReadStructures().ToList();
        var result = Assert.IsType<PolygonShape>(Assert.Single(structs[0].Shapes));
        Assert.Null(result.Holes); // one self-touching contour — GDSII cannot express two separate holes

        double area = Math.Abs(ShoelaceArea(result.Xy));
        Assert.InRange(area, 3_900_000, 3_940_000);
    }

    [Theory]
    [InlineData(PathEndStyle.Flush)]
    [InlineData(PathEndStyle.Round)]
    [InlineData(PathEndStyle.Square)]
    [InlineData(PathEndStyle.Extended)]
    public void Path_EachEndStyle_RoundTripsExactly(PathEndStyle end)
    {
        var path = new PathShape
        {
            Layer = new LayerKey(4, 0), Xy = [0, 0, 1000, 0], Width = 200, End = end,
        };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [path], [])]);

        var result = Assert.IsType<PathShape>(Assert.Single(structs[0].Shapes));
        Assert.Equal(end, result.End);
        Assert.Equal(200, result.Width);
        Assert.Equal(path.Xy, result.Xy);
    }

    [Fact]
    public void Label_RoundTrips()
    {
        var label = new LabelShape
        {
            Layer = new LayerKey(5, 0), X = 100, Y = 200, Text = "OUT", Height = 300, Rotation = LayoutRotation.R90,
        };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [label], [])]);

        var result = Assert.IsType<LabelShape>(Assert.Single(structs[0].Shapes));
        Assert.Equal("OUT", result.Text);
        Assert.Equal(100, result.X);
        Assert.Equal(200, result.Y);
        Assert.Equal(300, result.Height);
        Assert.Equal(LayoutRotation.R90, result.Rotation);
        Assert.False(result.IsPort);
    }

    [Fact]
    public void PortLabel_RoundTrips_IsPortSurvives()
    {
        var label = new LabelShape { Layer = new LayerKey(5, 0), X = 0, Y = 0, Text = "gate", Height = 100, IsPort = true };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [label], [])]);
        Assert.True(((LabelShape)structs[0].Shapes[0]).IsPort);
    }

    [Fact]
    public void PlainInstance_RoundTrips_AsSref()
    {
        var inst = new LayoutInstance { CellRef = "CELL1", X = 500, Y = -300, Rot = LayoutRotation.R90, MirrorX = false, Mag = 1.5 };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [], [inst])]);

        var result = Assert.Single(structs[0].Instances);
        Assert.Equal("CELL1", result.CellRef);
        Assert.Equal(500, result.X);
        Assert.Equal(-300, result.Y);
        Assert.Equal(LayoutRotation.R90, result.Rot);
        Assert.False(result.MirrorX);
        Assert.Equal(1.5, result.Mag, 9);
        Assert.Equal(1, result.Rows);
        Assert.Equal(1, result.Cols);
    }

    [Fact]
    public void FiveByFiveArray_RoundTrips_AsArefWithCorrectPitchAndCounts()
    {
        var inst = new LayoutInstance
        {
            CellRef = "VIA", X = 0, Y = 0, Rows = 5, Cols = 5, PitchX = 1000, PitchY = 2000,
        };
        var (_, structs) = WriteThenRead([new InterchangeStructure("TOP", [], [inst])]);

        var result = Assert.Single(structs[0].Instances);
        Assert.Equal(5, result.Rows);
        Assert.Equal(5, result.Cols);
        Assert.Equal(1000, result.PitchX);
        Assert.Equal(2000, result.PitchY);
    }

    [Fact]
    public void Hierarchy_Survives_InstancesAndArraysNotFlattenedGeometry()
    {
        var child = new InterchangeStructure("CHILD", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }], []);
        var top = new InterchangeStructure("TOP", [], [new LayoutInstance { CellRef = "CHILD", X = 0, Y = 0, Rows = 5, Cols = 5, PitchX = 100, PitchY = 100 }]);
        var (_, structs) = WriteThenRead([child, top]);

        Assert.Equal(2, structs.Count);
        var topResult = structs.Single(s => s.Name == "TOP");
        Assert.Empty(topResult.Shapes);
        var inst = Assert.Single(topResult.Instances);
        Assert.Equal(5, inst.Rows);
        Assert.Equal(5, inst.Cols);
    }

    [Fact]
    public void Bitmap_NeverExported_SkippedWithCountReported()
    {
        var bmp = new BitmapShape { Layer = new LayerKey(1, 0), ImagePathRef = "x.png", X = 0, Y = 0, W = 100, H = 100 };
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };

        using var ms = new MemoryStream();
        var summary = GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [bmp, rect], [])], Units, null);

        Assert.Equal(1, summary.BitmapsSkipped);
        ms.Position = 0;
        var structs = GdsiiReader.Open(ms).ReadStructures().ToList();
        Assert.Single(structs[0].Shapes); // only the rect survived
    }

    [Fact]
    public void PlainInstance_WritesStransAsBitArray_NotInt2()
    {
        // Regression for the KLayout-caught bug: STRANS must carry GdsiiDataType.BitArray on the
        // wire, not Int2 — both decode identically as raw bytes, which is exactly why this codebase's
        // own (datatype-agnostic) reader missed it, but a strict third-party reader does not.
        var inst = new LayoutInstance { CellRef = "CELL1", X = 0, Y = 0, Mag = 1.0 };
        using var ms = new MemoryStream();
        GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [], [inst])], Units, null);
        ms.Position = 0;

        var records = new GdsiiRecordReader(ms);
        while (records.TryReadNext(out var rec))
        {
            if (rec.Type != GdsiiRecordType.Strans) continue;
            Assert.Equal(GdsiiDataType.BitArray, rec.DataType);
            return;
        }
        Assert.Fail("No STRANS record found for the instance.");
    }

    [Fact]
    public void Path_WritesRecordsInCanonicalOrder_PathTypeBeforeWidth()
    {
        // Regression for a real bug caught by KLayout: the spec's canonical PATH element order is
        // LAYER, DATATYPE, PATHTYPE, WIDTH, [BGNEXTN, ENDEXTN], XY — PATHTYPE strictly before WIDTH.
        // Every individual record here is otherwise correctly framed (self-describing length/type),
        // so a lenient, type-dispatching reader (this codebase's own GdsiiReader included) round-trips
        // fine regardless of order — but KLayout enforces the canonical sequence and desyncs its own
        // element parser when it isn't followed. Verified directly against the wire bytes, not just
        // this codebase's own (order-insensitive) reader.
        var path = new PathShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Extended };
        using var ms = new MemoryStream();
        GdsiiWriter.Write(ms, [new InterchangeStructure("TOP", [path], [])], Units, null);
        ms.Position = 0;

        var records = new GdsiiRecordReader(ms);
        var order = new List<GdsiiRecordType>();
        while (records.TryReadNext(out var rec))
        {
            if (rec.Type == GdsiiRecordType.Path) order.Clear();
            order.Add(rec.Type);
            if (rec.Type == GdsiiRecordType.EndEl && order.Contains(GdsiiRecordType.Path)) break;
        }

        Assert.Equal(
            [
                GdsiiRecordType.Path, GdsiiRecordType.Layer, GdsiiRecordType.Datatype,
                GdsiiRecordType.PathType, GdsiiRecordType.Width,
                GdsiiRecordType.BgnExtn, GdsiiRecordType.EndExtn,
                GdsiiRecordType.Xy, GdsiiRecordType.EndEl,
            ],
            order);
    }

    [Fact]
    public void Units_RoundTrip()
    {
        var (units, _) = WriteThenRead([new InterchangeStructure("TOP", [], [])]);
        Assert.Equal(Units.UserUnitMeters, units.UserUnitMeters, 15);
        Assert.True(Math.Abs(Units.DbUnitMeters - units.DbUnitMeters) < 1e-15);
    }

    private static double ShoelaceArea(long[] xy)
    {
        int n = xy.Length / 2;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += (double)xy[2 * i] * xy[2 * j + 1] - (double)xy[2 * j] * xy[2 * i + 1];
        }
        return sum / 2.0;
    }
}
