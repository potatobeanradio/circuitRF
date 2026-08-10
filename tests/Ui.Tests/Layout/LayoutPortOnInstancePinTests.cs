// Owner report, 2026-08-09: "the Port snapping (and resultant port width) is incorrect with adding a
// port to Port1 of my MKLOPF component in my MLIN.clay".
//
// Reproduced from that design's own numbers. The MKlopf taper's generated cell spans
// x 0…63,287,191 and y -605,790…8,626,877 DBU, with pin 1 at (0, 0) carrying a width of 1,058,174 DBU
// and pin 2 at the far end carrying 7,093,754. A port on pin 1 used to resolve against the INSTANCE
// BOUNDING BOX — the whole 63 × 9 mm envelope — so it reported 9,232,667 DBU of width (8.7× the real
// value) and drew its reference plane at the box's mid-height, 4.01 mm from where pin 1 actually is.
//
// The fixture below is that geometry, scaled down but keeping the property that makes it bite: a
// tapered conductor whose bounding box is far wider than the end the port sits on.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortOnInstancePinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crfPortPin-" + Guid.NewGuid().ToString("N")[..8]);

    public LayoutPortOnInstancePinTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const long NarrowEnd = 1_000_000;   // the end the port sits on
    private const long WideEnd   = 7_000_000;   // the far end
    private const long Length    = 63_000_000;

    /// <summary>
    /// A tapered cell: narrow at x = 0 (pin 1, facing out along −x̂), wide at x = Length (pin 2).
    /// Its bounding box is 63 mm × 7 mm — nowhere near the 1 mm of metal at pin 1, which is exactly
    /// the property that made the bbox fallback wrong.
    /// </summary>
    private string MakeTaperCell(string name = "Taper")
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var sub = new LayoutView { DbuPerMicron = 1000 };
        sub.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy =
            [
                0, NarrowEnd / 2,
                Length, WideEnd / 2,
                Length, -WideEnd / 2,
                0, -NarrowEnd / 2,
            ],
        });
        sub.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = NarrowEnd, OutwardDeg = 180, Layer = new LayerKey(1, 0) });
        sub.Pins.Add(new LayoutPin { Name = "2", X = Length, Y = 0, WidthDbu = WideEnd, OutwardDeg = 0, Layer = new LayerKey(1, 0) });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), sub);

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        ccell.PrimaryLayout = name + ".clay";
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell);
        return cellDir;
    }

    private (LayoutView View, string BaseDir) TopWithTaper(long instX = 0, long instY = 0,
        LayoutRotation rot = LayoutRotation.R0, bool mirrorX = false, double mag = 1.0)
    {
        string cellDir = MakeTaperCell();
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_root, cellDir),
            X = instX, Y = instY, Rot = rot, MirrorX = mirrorX, Mag = mag,
        });
        return (top, _root);
    }

    // ── The headline ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APortOnATaperedInstancesPin_ReportsThePinsWidth_NotTheWholeEnvelopes()
    {
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var label = new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true, Height = 100_000 };
        var hint = LayoutPortDirection.Resolve(lookup, label);

        var h = Assert.NotNull(hint);
        Assert.Equal(NarrowEnd, h.WidthDbu);

        // The bbox answer this replaces, stated so the test says what it is guarding against: the
        // envelope is 7× wider than the end the port names.
        Assert.NotEqual(WideEnd, h.WidthDbu);
    }

    [Fact]
    public void TheReferencePlane_SitsOnThePin_NotAtTheEnvelopesMidHeight()
    {
        // The taper is deliberately placed OFF the origin and OFF-CENTRE in y, which is what turns a
        // mid-height plane into a visibly wrong one — the reported symptom.
        var (top, baseDir) = TopWithTaper(instX: 19_558_000, instY: -177_800);
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var label = new LabelShape { X = 19_558_000, Y = -177_800, Text = "P1", IsPort = true, Height = 100_000 };
        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup, label));

        Assert.Equal(19_558_000, h.PlaneX);
        Assert.Equal(-177_800, h.PlaneY);
    }

    [Fact]
    public void TheDirection_IsThePinsOwnInward_NotTheNearestBoxSide()
    {
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        // Pin 1 faces out along −x̂, so current flows IN along +x̂.
        var p1 = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));
        Assert.Equal(LayoutRotation.R0, p1.Direction);

        // Pin 2 faces out along +x̂ — current flows in along −x̂, and its width is the wide end's.
        var p2 = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = Length, Y = 0, Text = "P2", IsPort = true }));
        Assert.Equal(LayoutRotation.R180, p2.Direction);
        Assert.Equal(WideEnd, p2.WidthDbu);
    }

    [Fact]
    public void TheArrowHasOnlyTheMetalAHEADOfThePin_NotTheWholeLength()
    {
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        // Pin 2 sits at the far end: there is no metal ahead of it in its own +x̂ sense, so the
        // length must be measured back along −x̂ (its actual direction), not reported as the full run.
        var p2 = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = Length, Y = 0, Text = "P2", IsPort = true }));
        Assert.Equal(Length, p2.LengthDbu);

        var p1 = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));
        Assert.Equal(Length, p1.LengthDbu);
    }

    // ── The fallback must survive ───────────────────────────────────────────────────────────────

    [Fact]
    public void APortOnTheMetalButNotAtAPin_StillFallsBackToTheBox()
    {
        // Mid-taper: a real point on real metal that names no pin. There is genuinely nothing better
        // to say than the envelope, and saying nothing at all would lose the marker entirely.
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var info = Assert.NotNull(lookup(Length / 2, 0));
        Assert.Null(info.Pin);   // no pin claimed — this point names none

        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = Length / 2, Y = 0, Text = "P1", IsPort = true }));

        // The box's own nearest-side inference, unchanged from before this fix. Which side it picks
        // for a point in the middle of a wide box is pre-existing behaviour covered elsewhere; what
        // matters here is only that the answer is the BOX's, measured across whatever it inferred.
        Assert.Equal(LayoutPortDirection.WidthAcross(info.Box, h.Direction), h.WidthDbu);
        Assert.NotEqual(NarrowEnd, h.WidthDbu);
    }

    [Fact]
    public void ATopLevelShape_IsUnaffected_StillMeasuredFromItsBox()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -50_000, X2 = 400_000, Y2 = 50_000 });
        var lookup = LayoutPortDirection.LookupFor(view, tech: null, _root);

        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));

        Assert.Equal(LayoutRotation.R0, h.Direction);
        Assert.Equal(100_000, h.WidthDbu);
        Assert.Equal(0, h.PlaneX);
    }

    // ── The transform has to carry the direction, not just the position ─────────────────────────

    [Theory]
    [InlineData(LayoutRotation.R0,   false, LayoutRotation.R0)]
    [InlineData(LayoutRotation.R90,  false, LayoutRotation.R90)]
    [InlineData(LayoutRotation.R180, false, LayoutRotation.R180)]
    [InlineData(LayoutRotation.R270, false, LayoutRotation.R270)]
    [InlineData(LayoutRotation.R0,   true,  LayoutRotation.R180)]
    [InlineData(LayoutRotation.R90,  true,  LayoutRotation.R270)]
    public void ThePinsDirection_IsCarriedThroughTheInstancesOwnTransform(
        LayoutRotation rot, bool mirrorX, LayoutRotation expected)
    {
        var (top, baseDir) = TopWithTaper(rot: rot, mirrorX: mirrorX);
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        // Pin 1 sits at the cell origin, so it stays at (0,0) under every rotation/mirror here —
        // which isolates the DIRECTION as the only thing under test.
        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));
        Assert.Equal(expected, h.Direction);
        Assert.Equal(NarrowEnd, h.WidthDbu);
    }

    [Fact]
    public void MagnificationScalesThePinsWidth()
    {
        var (top, baseDir) = TopWithTaper(mag: 2.0);
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));
        Assert.Equal(NarrowEnd * 2, h.WidthDbu);
    }

    // ── A stated direction still overrules the geometry ─────────────────────────────────────────

    [Fact]
    public void AStatedDirectionMatchingThePin_KeepsThePinsExactWidthAndPlane()
    {
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup, new LabelShape
        {
            X = 0, Y = 0, Text = "P1", IsPort = true, PortDirection = LayoutRotation.R0,
        }));

        Assert.False(h.Inferred);
        Assert.Equal(NarrowEnd, h.WidthDbu);
        Assert.Equal(0, h.PlaneX);
        Assert.Equal(0, h.PlaneY);
    }

    [Fact]
    public void AStatedDirectionTHEUSERROTATED_FallsBackToMeasuringTheBoxAcrossTheirAxis()
    {
        // The user has overruled the geometry. A pin's width is measured across the pin's OWN axis,
        // so it no longer answers the question being asked — measuring the box across the chosen
        // axis is coarser but honest.
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        var h = Assert.NotNull(LayoutPortDirection.Resolve(lookup, new LabelShape
        {
            X = 0, Y = 0, Text = "P1", IsPort = true, PortDirection = LayoutRotation.R90,
        }));

        Assert.False(h.Inferred);
        Assert.Equal(Length, h.WidthDbu);   // the box's own width, across R90
    }

    // ── The tool must stamp the SAME direction the marker will infer ────────────────────────────

    [Fact]
    public void DirectionAt_AgreesWithWhatResolveInfers_SoAPlacedPortNeverDisagreesWithItsMarker()
    {
        var (top, baseDir) = TopWithTaper();
        var lookup = LayoutPortDirection.LookupFor(top, tech: null, baseDir);

        foreach (var (x, y) in new (long, long)[] { (0, 0), (Length, 0), (Length / 2, 0) })
        {
            var info = Assert.NotNull(lookup(x, y));
            var stamped = LayoutPortDirection.DirectionAt(info, x, y);
            var inferred = Assert.NotNull(LayoutPortDirection.Resolve(lookup,
                new LabelShape { X = x, Y = y, Text = "P1", IsPort = true })).Direction;
            Assert.Equal(inferred, stamped);
        }
    }

    // ── A pin with nothing to say about width must not report a zero-width port ─────────────────

    [Fact]
    public void APinStatingNoWidth_FallsThroughToTheBox_RatherThanReportingZero()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "NoWidth");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var sub = new LayoutView { DbuPerMicron = 1000 };
        sub.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -50_000, X2 = 400_000, Y2 = 50_000 });
        sub.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 0, OutwardDeg = 180, Layer = new LayerKey(1, 0) });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "NoWidth.clay"), sub);
        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        ccell.PrimaryLayout = "NoWidth.clay";
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell);

        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(_root, cellDir), Mag = 1.0 });

        var h = Assert.NotNull(LayoutPortDirection.Resolve(
            LayoutPortDirection.LookupFor(top, tech: null, _root),
            new LabelShape { X = 0, Y = 0, Text = "P1", IsPort = true }));

        Assert.Equal(100_000, h.WidthDbu);
    }
}
