// C2's named risk: recovering a pin's NAME, connecting WIDTH and OUTWARD DIRECTION from artwork that
// carries only a box and, sometimes, a label sitting inside it.
//
// A wrong answer here renders perfectly — the geometry is untouched and only the connectivity is
// wrong — so these fixtures are built to reproduce, in miniature, the exact real device shapes that
// each rule exists for. Every one is synthetic; the repository commits no third-party artwork.
//
// Measured on a real process's device library (56 cells, 104 pins) while these rules were written:
// 52 pins named and EVERY name a genuine terminal (G/S/D/TIE, B/C/E, VDD/PAD/VSS), zero names taken
// from a model or a description.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public class PinInferenceTests
{
    private static readonly LayerKey PinLayer   = new(8, 2);
    private static readonly LayerKey PinLayerB  = new(5, 2);
    private static readonly LayerKey TextLayer  = new(63, 0);
    private static readonly LayerKey DrawLayer  = new(8, 0);

    /// <summary>A technology whose purposes are what the inference keys on — nothing else matters here.</summary>
    private static Technology Tech() => new()
    {
        Layers =
        [
            new LayerDef { Key = PinLayer,  Name = "M1.pin",   Purpose = "pin",     Color = new Rgba(1, 1, 1) },
            new LayerDef { Key = PinLayerB, Name = "Poly.pin", Purpose = "pin",     Color = new Rgba(2, 2, 2) },
            new LayerDef { Key = TextLayer, Name = "TEXT",     Purpose = "drawing", Color = new Rgba(3, 3, 3) },
            new LayerDef { Key = DrawLayer, Name = "M1",       Purpose = "drawing", Color = new Rgba(4, 4, 4) },
        ],
    };

    private static RectShape Box(LayerKey layer, long x1, long y1, long x2, long y2)
        => new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static LabelShape Label(string text, long x, long y, LayerKey? layer = null)
        => new() { Layer = layer ?? TextLayer, Text = text, X = x, Y = y, Height = 100 };

    // ── naming ────────────────────────────────────────────────────────────────

    [Fact]
    public void ACellThatLabelsItsTerminalsKeepsThoseNames()
    {
        // The bipolar shape: three pins, three labels, one inside each. Real cells measured this way
        // produced B/C/E, G/S/D/TIE, VDD/PAD/VSS — all genuine terminal names.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, -1000, -1000, 1000, 1000),
            Box(PinLayer,  -400, -900, 400, -700), Label("B", 0, -800),
            Box(PinLayer,  -400,  700, 400,  900), Label("C", 0,  800),
            Box(PinLayerB, -200, -100, 200,  100), Label("E", 0,    0),
        };

        var r = PinInference.Infer("dev", shapes, Tech());

        Assert.Equal(3, r.Pins.Count);
        Assert.Equal(["B", "C", "E"], r.Pins.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(r.Pins, p => Assert.Equal(PinNameSource.InferredFromLabel, p.NameSource));
    }

    [Fact]
    public void OneLabelOverlappingOnePinOfSeveral_NamesNothing()
    {
        // THE headline case, and the one that would otherwise ship wrong and invisible. A real
        // transistor cell carries a single descriptive label at its centre, and that centre falls
        // inside the GATE's pin box — so plain containment names the gate after the device's model.
        // The cell is not labelling its terminals; one overlap out of three is an annotation.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, -105, 870, 405),
            Box(PinLayerB, 370, -105, 500, 405),          // gate, spanning the cell, centred
            Box(PinLayer,   70,   20, 230, 280),          // source
            Box(PinLayer,  640,   20, 800, 280),          // drain
            Label("nmos_lv_core", 435, 150),              // dead centre — inside the gate box
        };

        var r = PinInference.Infer("nmos", shapes, Tech());

        Assert.Equal(3, r.Pins.Count);
        Assert.All(r.Pins, p => Assert.Null(p.Name));
        Assert.All(r.Pins, p => Assert.Equal(PinNameSource.None, p.NameSource));
        Assert.Contains(r.Notes, n => n.Contains("annotation that happens to overlap"));
    }

    [Fact]
    public void ALabelInsideSeveralPinsNamesNoneOfThem()
    {
        // The capacitor shape: two stacked plates, both covering the cell, so the cell's own labels
        // fall inside BOTH pins. Nothing can be concluded and nothing is.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 7000, 7000),
            Box(PinLayer,  0, 0, 7000, 7000),
            Box(PinLayerB, 300, 300, 6600, 6600),
            Label("cmim", 3400, 3400),
            Label("c=74.6f", 3400, 3400),
        };

        var r = PinInference.Infer("cmim", shapes, Tech());

        Assert.Equal(2, r.Pins.Count);
        Assert.All(r.Pins, p => Assert.Null(p.Name));
    }

    [Fact]
    public void TwoPinsLabelledOutOfThree_IsStillSystematic()
    {
        // Real cells do this (a bipolar variant named only two of its three terminals). Two labels
        // landing in two different pins is deliberate labelling, not coincidence — so the names are
        // kept and the unnamed pin is reported rather than the whole assignment being thrown away.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, -1000, -1000, 1000, 1000),
            Box(PinLayer,  -400, -900, 400, -700), Label("B", 0, -800),
            Box(PinLayer,  -400,  700, 400,  900), Label("C", 0,  800),
            Box(PinLayerB, -200, -100, 200,  100),
        };

        var r = PinInference.Infer("dev", shapes, Tech());

        Assert.Equal(2, r.Pins.Count(p => p.Name is not null));
        Assert.Contains(r.Notes, n => n.Contains("were named from labels"));
    }

    [Fact]
    public void ASinglePinWithASingleLabelIsNamed()
    {
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(PinLayer, 800, 400, 1000, 600),
            Label("dant", 900, 500),
        };

        var pin = Assert.Single(PinInference.Infer("dantenna", shapes, Tech()).Pins);
        Assert.Equal("dant", pin.Name);
    }

    [Fact]
    public void NothingAsksWhatTheTextSays()
    {
        // A rule that rejected names "looking like" model numbers would be knowledge about one
        // supplier's habits living inside circuitRF, and would fail on the next kit. Proof: the exact
        // model-looking string IS accepted when the cell labels its pins systematically.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, -1000, 0, 1000, 1000),
            Box(PinLayer, -900, 400, -700, 600), Label("nmos_lv_core", -800, 500),
            Box(PinLayer,  700, 400,  900, 600), Label("also_a_model", 800, 500),
        };

        var names = PinInference.Infer("dev", shapes, Tech()).Pins.Select(p => p.Name).ToList();

        Assert.Contains("nmos_lv_core", names);
        Assert.Contains("also_a_model", names);
    }

    // ── geometry ──────────────────────────────────────────────────────────────

    [Theory]
    // pin box placed on each side of a square cell → faces that way, and the position is the midpoint
    // of the edge it presents, not the box's centre.
    [InlineData(800, 400, 1000, 600,   0.0, 1000, 500, 200)]   // right
    [InlineData(  0, 400,  200, 600, 180.0,    0, 500, 200)]   // left
    [InlineData(400, 800,  600, 1000, 90.0,  500, 1000, 200)]  // top
    [InlineData(400,   0,  600,  200, 270.0, 500,    0, 200)]  // bottom
    public void APinFacesAwayFromTheCell_AndItsWidthIsTheEdgeItPresents(
        long x1, long y1, long x2, long y2, double deg, long px, long py, long width)
    {
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(PinLayer, x1, y1, x2, y2),
        };

        var pin = Assert.Single(PinInference.Infer("c", shapes, Tech()).Pins);

        Assert.Equal(deg,   pin.OutwardDeg);
        Assert.Equal(px,    pin.XDbu);
        Assert.Equal(py,    pin.YDbu);
        Assert.Equal(width, pin.WidthDbu);
        Assert.Equal(PinDirectionSource.Geometry, pin.DirectionSource);
    }

    [Fact]
    public void AVeryWideCellDoesNotReadEveryPinAsFacingSideways()
    {
        // Offsets are compared as FRACTIONS of the cell's own size. Against raw distances, a cell ten
        // times wider than it is tall makes every pin's x-offset dominate, and a pin plainly sitting
        // on the top edge is reported as facing right.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 10000, 400),
            Box(PinLayer, 3000, 300, 3400, 400),        // near the top, well left of centre
        };

        var pin = Assert.Single(PinInference.Infer("c", shapes, Tech()).Pins);

        Assert.Equal(90.0, pin.OutwardDeg);
    }

    [Fact]
    public void ACentralPinIsReportedAmbiguous_NotQuietlyGivenASide()
    {
        // A real case, not a degenerate one: a gate spanning the full cell height, or a bipolar's
        // emitter contact in the middle. Position genuinely says nothing, so a coin flip would be a
        // wrong answer that renders. It falls back to the pin's own shape — a box presents its short
        // edges, so a tall thin pin faces up — and says it did.
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, -105, 870, 405),
            Box(PinLayerB, 370, -105, 500, 405),
        };

        var r   = PinInference.Infer("nmos", shapes, Tech());
        var pin = Assert.Single(r.Pins);

        Assert.Equal(PinDirectionSource.Ambiguous, pin.DirectionSource);
        Assert.Equal(90.0, pin.OutwardDeg);                 // tall and thin → presents its top edge
        Assert.Equal(130,  pin.WidthDbu);
        Assert.Contains(r.Notes, n => n.Contains("sits centrally"));
    }

    [Fact]
    public void ALabelNeverStretchesTheCellExtent()
    {
        // A label is an anchor point that may sit far outside the drawn geometry. Letting one into the
        // extent moves the centre that every direction is measured against — so a pin plainly on the
        // right would start reporting left.
        var withStray = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(PinLayer, 800, 400, 1000, 600),
            Label("far away", 90000, 500),
        };

        var pin = Assert.Single(PinInference.Infer("c", withStray, Tech()).Pins);

        Assert.Equal(0.0, pin.OutwardDeg);
    }

    [Fact]
    public void PinOrderIsDeterministic_BecauseTheKitsOwnDeclarationKeysOnIt()
    {
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(PinLayer,  800, 400, 1000, 600),
            Box(PinLayerB, 400, 800,  600, 1000),
            Box(PinLayer,    0, 400,  200,  600),
        };
        var reversed = Enumerable.Reverse(shapes).ToList();

        var a = PinInference.Infer("c", shapes,   Tech()).Pins;
        var b = PinInference.Infer("c", reversed, Tech()).Pins;

        Assert.Equal(a.Select(p => (p.Layer, p.XDbu, p.YDbu)), b.Select(p => (p.Layer, p.XDbu, p.YDbu)));
        Assert.Equal(PinLayerB, a[0].Layer);   // layer 5 sorts before layer 8
    }

    [Fact]
    public void ShapesOnANonPinPurposeAreNotPins()
    {
        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(DrawLayer, 800, 400, 1000, 600),
        };

        Assert.Empty(PinInference.Infer("c", shapes, Tech()).Pins);
    }

    [Fact]
    public void WithNoTechnologyNothingIsAPin_RatherThanEverything()
    {
        // Purposes are what identify a pin, and they live in the technology. Guessing from the layer
        // number alone would make every drawn shape a pin.
        var shapes = new List<LayoutShape> { Box(PinLayer, 800, 400, 1000, 600) };

        Assert.Empty(PinInference.Infer("c", shapes, tech: null).Pins);
    }

    // ── what the kit states beside itself ─────────────────────────────────────

    [Fact]
    public void ADeclarationBesideTheKitWinsOverGeometry()
    {
        var rules = new PinInferenceRules
        {
            Cells =
            {
                ["nmos"] = new CellPinDeclaration
                {
                    Pins = { ["#0"] = new PinDeclaration { Name = "G", OutwardDeg = 270, WidthDbu = 99 } },
                },
            },
        };

        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, -105, 870, 405),
            Box(PinLayerB, 370, -105, 500, 405),
        };

        var pin = Assert.Single(PinInference.Infer("nmos", shapes, Tech(), rules).Pins);

        Assert.Equal("G", pin.Name);
        Assert.Equal(PinNameSource.Declared, pin.NameSource);
        Assert.Equal(270.0, pin.OutwardDeg);
        Assert.Equal(PinDirectionSource.Declared, pin.DirectionSource);
        Assert.Equal(99, pin.WidthDbu);
        Assert.Equal(-105, pin.YDbu);          // the position followed the stated direction
    }

    [Fact]
    public void ADeclarationMayBeKeyedByTheInferredName()
    {
        // Keying only by ordinal would be unusable for a kit correcting one pin of many; keying only
        // by name is circular when the name is what is being supplied. Both work.
        var rules = new PinInferenceRules
        {
            Cells = { ["dev"] = new CellPinDeclaration { Pins = { ["C"] = new PinDeclaration { OutwardDeg = 0 } } } },
        };

        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, -1000, -1000, 1000, 1000),
            Box(PinLayer,  -400, -900, 400, -700), Label("B", 0, -800),
            Box(PinLayer,  -400,  700, 400,  900), Label("C", 0,  800),
        };

        var pins = PinInference.Infer("dev", shapes, Tech(), rules).Pins;

        var c = Assert.Single(pins, p => p.Name == "C");
        Assert.Equal(0.0, c.OutwardDeg);
        Assert.Equal(PinDirectionSource.Declared, c.DirectionSource);

        var b = Assert.Single(pins, p => p.Name == "B");
        Assert.Equal(PinDirectionSource.Geometry, b.DirectionSource);   // untouched
    }

    [Fact]
    public void ADeclarationForAnotherCellDoesNotLeakIn()
    {
        var rules = new PinInferenceRules
        {
            Cells = { ["other"] = new CellPinDeclaration { Pins = { ["#0"] = new PinDeclaration { Name = "X" } } } },
        };

        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, 0, 0, 1000, 1000),
            Box(PinLayer, 800, 400, 1000, 600),
        };

        Assert.Null(Assert.Single(PinInference.Infer("dev", shapes, Tech(), rules).Pins).Name);
    }

    [Fact]
    public void AnAbsentDeclarationIsSilent_APresentUnreadableOneIsReported()
    {
        // Two different situations needing two different answers. Nearly every kit states nothing, so
        // reporting that would be noise on every import; a file that IS there and cannot be read is a
        // problem the user can act on.
        string dir = Directory.CreateTempSubdirectory("crf-pins-").FullName;
        try
        {
            var absent = PinInferenceRules.Load(Path.Combine(dir, "nothing.json"), out string? p1);
            Assert.Null(p1);
            Assert.Equal(["pin"], absent.PinPurposes);

            string bad = Path.Combine(dir, "pins.json");
            File.WriteAllText(bad, "{ not json");
            PinInferenceRules.Load(bad, out string? p2);
            Assert.NotNull(p2);
            Assert.Contains("could not be read", p2);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ADeclarationRoundTripsThroughItsOwnFileFormat()
    {
        string dir = Directory.CreateTempSubdirectory("crf-pins-").FullName;
        try
        {
            string path = Path.Combine(dir, "pins.json");
            File.WriteAllText(path, """
                {
                  "pinPurposes": ["pin", "terminal"],
                  "labelPurposes": ["text"],
                  "cells": {
                    "nmos": { "pins": { "#0": { "name": "G", "outwardDeg": 270, "widthDbu": 130 } } }
                  }
                }
                """);

            var rules = PinInferenceRules.Load(path, out string? problem);

            Assert.Null(problem);
            Assert.Equal(["pin", "terminal"], rules.PinPurposes);
            Assert.Equal(["text"], rules.LabelPurposes);
            Assert.Equal("G",  rules.Cells["nmos"].Pins["#0"].Name);
            Assert.Equal(270,  rules.Cells["nmos"].Pins["#0"].OutwardDeg);
            Assert.Equal(130,  rules.Cells["nmos"].Pins["#0"].WidthDbu);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DeclaredLabelPurposesRestrictWhichLabelsMayName()
    {
        // Empty means "any label", which is the right DEFAULT — measured on a real process, terminal
        // labels sit on a general text layer rather than on the matching pin layer, so requiring them
        // to agree finds none. A kit that knows better may narrow it.
        var rules = new PinInferenceRules { LabelPurposes = ["pin"] };

        var shapes = new List<LayoutShape>
        {
            Box(DrawLayer, -1000, -1000, 1000, 1000),
            Box(PinLayer, -400, -900, 400, -700), Label("B", 0, -800),
            Box(PinLayer, -400,  700, 400,  900), Label("C", 0,  800),
        };

        Assert.All(PinInference.Infer("dev", shapes, Tech(), rules).Pins, p => Assert.Null(p.Name));
        Assert.All(PinInference.Infer("dev", shapes, Tech()).Pins,        p => Assert.NotNull(p.Name));
    }
}
