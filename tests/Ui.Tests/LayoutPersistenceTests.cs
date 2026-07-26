using System.IO;
using System.IO.Compression;
using System.Text;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutPersistenceTests
{
    // ── Fixture: one of every shape type + arc-bearing Curve/Path + Net + instances ────────────

    private static LayoutView BuildFullFixture()
    {
        var view = new LayoutView
        {
            DbuPerMicron = 1000,
            DisplayUnit  = LayoutUnit.Mil,
            SnapDbu      = 1000,
            AngleMode    = AngleMode.AnyAngle,
            TechRef      = "../../tech/pcb-2layer.ctech",
        };

        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), Net = "RFin", X1 = 0, Y1 = 0, X2 = 2_900_000, Y2 = 20_000_000 });
        view.Shapes.Add(new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 500, 0, 500, 300, 0, 300] });
        view.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 600_000, CornerRadius = 150_000 });
        view.Shapes.Add(new CircleShape { Layer = new LayerKey(2, 0), Net = "GND", Cx = 4_000_000, Cy = 1_000_000, R = 300_000 });

        view.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 2_000_000, 0, 2_000_000, 2_000_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
            FlattenTolDbu = 1000,
        });

        view.Shapes.Add(new PathShape
        {
            Layer = new LayerKey(1, 0),
            Net = "RFin",
            Xy = [0, 0, 5_000_000, 0, 5_000_000, 3_000_000],
            Width = 2_900_000,
            End = PathEndStyle.Flush,
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
            ],
            FlattenTolDbu = 1000,
        });

        view.Shapes.Add(new ViaShape { Layer = new LayerKey(3, 0), X = 100_000, Y = 100_000, PadSize = 500_000, DrillSize = 200_000, LandingLayer = new LayerKey(2, 0) });
        view.Shapes.Add(new LabelShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, Text = "P1", Height = 500_000, Rotation = LayoutRotation.R0, IsPort = true });

        view.Instances.Add(new LayoutInstance { CellRef = "../../inductor_2n5", X = 100_000, Y = 0, Rot = LayoutRotation.R90, MirrorX = false });
        view.Instances.Add(new LayoutInstance { CellRef = "../../via_cell", X = 0, Y = 0, Rows = 4, Cols = 4, PitchX = 50_000, PitchY = 50_000 });

        return view;
    }

    // ── Gate 3: display-unit change is a serialization no-op ─────────────────

    [Fact]
    public void DisplayUnitChange_IsSerializationNoOp_ExceptDisplayUnitToken()
    {
        var view = BuildFullFixture();

        view.DisplayUnit = LayoutUnit.Um;
        var jsonUm = LayoutPersistence.Serialize(view);

        view.DisplayUnit = LayoutUnit.Mil;
        var jsonMil = LayoutPersistence.Serialize(view);

        Assert.NotEqual(jsonUm, jsonMil);

        var linesUm  = jsonUm.Split('\n').Where(l => !l.TrimStart().StartsWith("\"DisplayUnit\"")).ToArray();
        var linesMil = jsonMil.Split('\n').Where(l => !l.TrimStart().StartsWith("\"DisplayUnit\"")).ToArray();
        Assert.Equal(string.Join('\n', linesUm), string.Join('\n', linesMil));
    }

    // ── Gate 4: .clay round-trips byte-identically ────────────────────────────

    [Fact]
    public void Clay_RoundTrip_ByteIdentical()
    {
        var view = BuildFullFixture();
        var json1 = LayoutPersistence.Serialize(view);
        var restored = LayoutPersistence.Deserialize(json1);
        var json2 = LayoutPersistence.Serialize(restored);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Clay_SaveLoadFile_RoundTrips()
    {
        var view = BuildFullFixture();
        var tmp = Path.GetTempFileName();
        try
        {
            LayoutPersistence.SaveToFile(tmp, view);
            var restored = LayoutPersistence.LoadFromFile(tmp);
            Assert.Equal(LayoutPersistence.Serialize(view), LayoutPersistence.Serialize(restored));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ── Gate 6: format_version reject-on-mismatch ─────────────────────────────

    [Fact]
    public void Clay_NewerFormatVersion_ThrowsInvalidDataException()
    {
        var json = LayoutPersistence.Serialize(new LayoutView());
        var broken = json.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");

        Assert.Throws<InvalidDataException>(() => LayoutPersistence.Deserialize(broken));
    }

    // ── Gate 7: gzip sniff ─────────────────────────────────────────────────────

    [Fact]
    public void Clay_GzippedFile_LoadsSameAsPlainTextFile()
    {
        var view = BuildFullFixture();
        var json = LayoutPersistence.Serialize(view);

        var plainPath = Path.GetTempFileName();
        var gzPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(plainPath, json);

            using (var fs = File.Create(gzPath))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            using (var writer = new StreamWriter(gz, Encoding.UTF8))
                writer.Write(json);

            var fromPlain = LayoutPersistence.LoadFromFile(plainPath);
            var fromGz = LayoutPersistence.LoadFromFile(gzPath);

            Assert.Equal(LayoutPersistence.Serialize(fromPlain), LayoutPersistence.Serialize(fromGz));
        }
        finally
        {
            File.Delete(plainPath);
            File.Delete(gzPath);
        }
    }

    // ── Misc ────────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_NullTechRef_NotWrittenToJson()
    {
        var view = new LayoutView();
        var json = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("TechRef", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_ShapeWithoutNet_NetNotWritten()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        var json = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("\"Net\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShorterThanExpectedEdges_PaddedWithLineOnLoad()
    {
        var view = new LayoutView();
        view.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 100, 0, 100, 100],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }], // 1 edge, expects 3
        });

        var restored = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        var curve = Assert.IsType<CurveShape>(Assert.Single(restored.Shapes));

        Assert.Equal(3, curve.Edges!.Count);
        Assert.Equal(EdgeKind.Arc, curve.Edges[0].Kind);
        Assert.Equal(EdgeKind.Line, curve.Edges[1].Kind);
        Assert.Equal(EdgeKind.Line, curve.Edges[2].Kind);
    }
}
