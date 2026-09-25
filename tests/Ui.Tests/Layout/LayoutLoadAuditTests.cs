// What reading a .clay notices and does not refuse it for (LayoutLoadAudit): a key the reader ignored,
// and a vertex-list shape with too few vertices to be a shape. Found by the F0 spike, whose hand-written
// ground plane said "Points" where the format says "Xy", loaded as a polygon with NO vertices, and
// passed `circuitrf check` with 0 errors (docs/design/em-3d-f0-findings.md §7).

using System;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Drc;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class LayoutLoadAuditTests
{
    /// <summary>The F0 case exactly: the cause (an ignored key) is reported before its symptom (an
    /// empty polygon), both at the shape's position in the file, at the severities check exits on.</summary>
    [Fact]
    public void APolygonSpeltWithPoints_IsReportedAsAnIgnoredKeyAndThenAsAnEmptyShape()
    {
        var view = LayoutPersistence.Deserialize("""
            { "FormatVersion": 1, "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 0, "X2": 10, "Y2": 10 },
                { "$type": "Poly", "Layer": { "Layer": 2, "Datatype": 0 }, "Points": [0, 0, 100, 0, 100, 100] }
            ], "Instances": [] }
            """);

        Assert.Equal(2, view.LoadFindings.Count);
        var (ignored, empty) = (view.LoadFindings[0], view.LoadFindings[1]);

        Assert.Equal(LayoutLoadFindingKind.UnknownField, ignored.Kind);
        Assert.False(ignored.IsError);                  // a newer file carries keys this build lacks
        Assert.Contains("'Points'", ignored.Message);
        Assert.Contains("a Poly shape", ignored.Message);
        Assert.Contains("Shapes[1]", ignored.Message);

        Assert.Equal(LayoutLoadFindingKind.DegenerateShape, empty.Kind);
        Assert.True(empty.IsError);                     // nothing circuitRF writes produces one
        Assert.Contains("Shapes[1]", empty.Message);
        Assert.Contains("0 vertices", empty.Message);
    }

    /// <summary>No false positive on anything the writer writes — every shape kind and every nested
    /// record. The LABEL'S PortLayer is the case that matters: it is a <c>LayerKey?</c>, whose contract
    /// the serializer describes as an object with no properties, and the first version of this audit
    /// called its every key unknown on eight of the repository's own layouts.</summary>
    [Fact]
    public void EveryShapeKindAndNestedRecord_ReadsBackWithNoFindings()
    {
        var m1 = new LayerKey(1, 0);
        var view = new LayoutView { TechRef = "t.ctech" };
        view.Shapes.Add(new RectShape { Layer = m1, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10, Net = "A" });
        view.Shapes.Add(new PolygonShape { Layer = m1, Xy = [0, 0, 90, 0, 90, 90, 0, 90],
                                           Holes = [[10, 10, 20, 10, 20, 20]] });
        view.Shapes.Add(new RoundedRectShape { Layer = m1, X1 = 0, Y1 = 0, X2 = 50, Y2 = 50, CornerRadius = 5 });
        view.Shapes.Add(new CircleShape { Layer = m1, Cx = 5, Cy = 5, R = 3 });
        view.Shapes.Add(new CurveShape { Layer = m1, Xy = [0, 0, 100, 0],
                                         Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }, new LayoutEdge()] });
        view.Shapes.Add(new PathShape { Layer = m1, Xy = [0, 0, 100, 0], Width = 10, Edges = [new LayoutEdge()] });
        view.Shapes.Add(new ViaShape { Layer = new LayerKey(9, 0), X = 5, Y = 5, PadSize = 6, DrillSize = 3,
                                       LandingLayer = m1 });
        view.Shapes.Add(new LabelShape { Layer = m1, X = 0, Y = 5, Text = "1", Height = 4, IsPort = true,
                                         PortLayer = m1 });
        view.Shapes.Add(new BitmapShape { Layer = m1, ImagePathRef = "under.png" });
        view.Pins.Add(new LayoutPin { Name = "in", X = 0, Y = 5, Layer = m1 });
        view.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10, Y2 = 0 });
        view.DrcWaivers.Add(new DrcWaiver { Key = "k", Reason = "r", RuleName = "M1 min width" });
        view.Instances.Add(new LayoutInstance { CellRef = "../Other", X = 3, Y = 4 });

        var reread = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));

        Assert.Empty(reread.LoadFindings);
        Assert.Equal(view.Shapes.Count, reread.Shapes.Count);
    }

    /// <summary>One bad key on many shapes is ONE line, not one per shape — the GUI posts these to the
    /// Messages panel on every fresh load. And what a read ignored is reported, never written back: a
    /// save carries neither the key nor the property that caught it.</summary>
    [Fact]
    public void OneIgnoredKeyOnManyShapes_IsOneFinding_AndASaveWritesNeitherItNorTheCapture()
    {
        var clean = new LayoutView();
        for (int i = 0; i < 500; i++)
            clean.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = i, Y1 = 0, X2 = i + 1, Y2 = 1 });
        string json = LayoutPersistence.Serialize(clean)
            .Replace("\"$type\": \"Rect\",", "\"$type\": \"Rect\", \"Colour\": \"red\",");
        Assert.Equal(500, json.Split("\"Colour\"").Length - 1);   // the fixture really carries the key

        var view = LayoutPersistence.Deserialize(json);

        var finding = Assert.Single(view.LoadFindings);
        Assert.Contains("'Colour'", finding.Message);
        Assert.Contains("on 500 of them", finding.Message);
        Assert.Equal(LayoutPersistence.Serialize(clean), LayoutPersistence.Serialize(view));
    }
}
