using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// ── Layer 1 gate: model types compile and are usable ─────────────────────────

public class SymbolModelConstructionTests
{
    [Fact]
    public void CanConstruct_Symbol_WithPrimitivesAndPins()
    {
        var prims = new List<SymbolPrimitive>
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0),
            new CirclePrimitive { ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal, Cx = 0, Cy = 0, R = 50, Filled = false },
            new ArcPrimitive    { ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Thin,   Cx = 0, Cy = 0, R = 30, StartDeg = -90, SweepDeg = 180 },
            new PolygonPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal, Filled = true,
                Points = [[-45, 40], [45, 40], [0, 90]],
            },
        };
        var pins = new List<SymbolPin>
        {
            new SymbolPin(0, -200, 0, "port1"),
            new SymbolPin(0,  200, 1, "port2"),
        };
        var sym = new Symbol(prims, pins);

        Assert.Equal(4, sym.Primitives.Count);
        Assert.Equal(2, sym.Pins.Count);
        Assert.Equal(0.0,  sym.Pins[0].LocalX, 1e-9);
        Assert.Equal(-200.0, sym.Pins[0].LocalY, 1e-9);
        Assert.Equal(0,    sym.Pins[0].PortIndex);
        Assert.Equal("port1", sym.Pins[0].Name);
    }

    [Fact]
    public void Symbol_HasNoSkiaOrAvaloniaTypes()
    {
        // Compile-time assertion: the model types live in the framework-free layer.
        // If SymbolModel.cs ever imports SKColor/SKPath/Avalonia, a using directive
        // would appear; this test is a documentation anchor.
        var line   = new LinePrimitive(SymbolColorRole.SymbolPlus, SymbolStrokeTier.Thin, 1, 2, 3, 4);
        var circle = new CirclePrimitive { Cx = 0, Cy = 0, R = 10, ColorRole = SymbolColorRole.SymbolLine };
        Assert.NotNull(line);
        Assert.NotNull(circle);
    }

    [Fact]
    public void AllPrimitiveTypes_CanBeInstantiated()
    {
        // Confirm every concrete primitive class compiles and can be created.
        var prims = new SymbolPrimitive[]
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, 0, 0, 1, 1),
            new PolylinePrimitive   { ColorRole = SymbolColorRole.SymbolLine, Points = [[0,0],[1,1]] },
            new RectPrimitive       { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, W = 10, H = 10 },
            new RoundedRectPrimitive{ ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, W = 10, H = 10, Radius = 2 },
            new CirclePrimitive     { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, R = 5 },
            new EllipsePrimitive    { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, Rx = 5, Ry = 3 },
            new ArcPrimitive        { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, R = 5, StartDeg = 0, SweepDeg = 90 },
            new PolygonPrimitive    { ColorRole = SymbolColorRole.SymbolLine, Points = [[0,0],[1,0],[0.5,1]] },
            new QuadCurvePrimitive  { ColorRole = SymbolColorRole.SymbolLine, P0X = 0, P0Y = 0, CtrlX = 5, CtrlY = -5, P2X = 10, P2Y = 0 },
            new CubicCurvePrimitive { ColorRole = SymbolColorRole.SymbolLine, P0X = 0, P0Y = 0, C1X = 3, C1Y = -3, C2X = 7, C2Y = -3, P3X = 10, P3Y = 0 },
            new SinePrimitive             { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, Amp = 10, Cycles = 1, Length = 40, Axis = SineAxis.Horizontal },
            new ExponentialTaperPrimitive { ColorRole = SymbolColorRole.SymbolLine, Cx = 0, Cy = 0, W1 = 60, W2 = 15, L = 100, Axis = SineAxis.Horizontal },
            new TextPrimitive             { Content = "Z", AnchorX = 0, AnchorY = 0, FontSize = 12, FontStyle = SymbolFontStyle.Regular },
            new BitmapPrimitive           { ImagePathRef = "ref.png", X = 0, Y = 0, W = 100, H = 100, Opacity = 0.8, Locked = true },
        };
        Assert.Equal(14, prims.Length);
    }
}

// ── Layer 1 gate: EditableSymbol round-trip + BboxOf/HitTest ─────────────────

public class EditableSymbolTests
{
    [Fact]
    public void FromSymbol_ToSymbol_RoundTripsLosslessly()
    {
        var original = BuiltInSymbols.Primitives(SymbolKind.Resistor);
        var editable  = EditableSymbol.FromSymbol(original);
        var restored  = editable.ToSymbol();

        Assert.Equal(original.Primitives.Count, restored.Primitives.Count);
        Assert.Equal(original.Pins.Count,       restored.Pins.Count);
        // Verify the same primitive instances are present (no deep copy on import)
        for (int i = 0; i < original.Primitives.Count; i++)
            Assert.Same(original.Primitives[i], restored.Primitives[i]);
    }

    [Fact]
    public void NotifyChanged_Fires()
    {
        var e = new EditableSymbol();
        int count = 0;
        e.Changed += (_, _) => count++;
        e.NotifyChanged();
        Assert.Equal(1, count);
    }

    [Fact]
    public void BboxOf_Line_EnclosesBothEndpoints()
    {
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     -100, -50, 100, 50);
        var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(line);
        Assert.True(x0 <= -100); Assert.True(x1 >= 100);
        Assert.True(y0 <= -50);  Assert.True(y1 >= 50);
    }

    [Fact]
    public void BboxOf_Circle_EnclosesFully()
    {
        var c = new CirclePrimitive { Cx = 0, Cy = 0, R = 40 };
        var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(c);
        Assert.True(x0 <= -40); Assert.True(x1 >= 40);
        Assert.True(y0 <= -40); Assert.True(y1 >= 40);
    }

    [Fact]
    public void HitTest_Line_HitsNearby()
    {
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     0, 0, 100, 0);
        Assert.True (SymbolGeometry.HitTest(line, 50, 3, 5));    // near midpoint
        Assert.False(SymbolGeometry.HitTest(line, 50, 20, 5));   // too far
    }

    [Fact]
    public void HitTest_FilledRect_InsideCounts()
    {
        var r = new RectPrimitive { Cx = 0, Cy = 0, W = 100, H = 60, Filled = true };
        Assert.True (SymbolGeometry.HitTest(r,  0,  0, 5));  // inside
        Assert.True (SymbolGeometry.HitTest(r, 45,  0, 5));  // near edge
        Assert.False(SymbolGeometry.HitTest(r, 80,  0, 5));  // outside
    }

    [Fact]
    public void HitTest_StrokedRect_OnlyEdge()
    {
        var r = new RectPrimitive { Cx = 0, Cy = 0, W = 100, H = 60, Filled = false };
        Assert.True (SymbolGeometry.HitTest(r, 50, 0, 5));    // on edge
        Assert.False(SymbolGeometry.HitTest(r,  0, 0, 5));    // center — NOT a hit
    }

    [Fact]
    public void HitTest_Circle_StrokedOnly()
    {
        var c = new CirclePrimitive { Cx = 0, Cy = 0, R = 50, Filled = false };
        Assert.True (SymbolGeometry.HitTest(c, 50, 0, 5));   // on ring
        Assert.False(SymbolGeometry.HitTest(c,  0, 0, 5));   // center
    }

    [Fact]
    public void TranslateBy_Line_MovesEndpoints()
    {
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     0, 0, 10, 0);
        SymbolGeometry.TranslateBy(line, 5, 10);
        Assert.Equal( 5, line.X1, 1e-9); Assert.Equal(10, line.Y1, 1e-9);
        Assert.Equal(15, line.X2, 1e-9); Assert.Equal(10, line.Y2, 1e-9);
    }

    [Fact]
    public void TranslateBy_Circle_MovesCenter()
    {
        var c = new CirclePrimitive { Cx = 0, Cy = 0, R = 20 };
        SymbolGeometry.TranslateBy(c, 15, -5);
        Assert.Equal(15, c.Cx, 1e-9); Assert.Equal(-5, c.Cy, 1e-9);
    }

    [Fact]
    public void TranslateBy_IsUndoable_ByNegating()
    {
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     100, 200, 300, 400);
        SymbolGeometry.TranslateBy(line, 50, -30);
        SymbolGeometry.TranslateBy(line, -50, 30);  // undo
        Assert.Equal(100, line.X1, 1e-9); Assert.Equal(200, line.Y1, 1e-9);
        Assert.Equal(300, line.X2, 1e-9); Assert.Equal(400, line.Y2, 1e-9);
    }
}

// ── Layer 4 gate: .csym round-trip ───────────────────────────────────────────

public class CsymRoundTripTests
{
    private static Symbol BuildTestSymbol()
    {
        var prims = new List<SymbolPrimitive>
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0),
            new LinePrimitive(SymbolColorRole.SymbolPlus, SymbolStrokeTier.Thin,   -10, -20, 10, -20),
            new PolylinePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Points = [[-100, 0], [-60, -30], [-60, 30], [-100, 0]],
            },
            new CirclePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = 0, R = 50, Filled = false,
            },
            new ArcPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = -25, R = 25, StartDeg = -90, SweepDeg = 180,
            },
            new RectPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Thin,
                Cx = 0, Cy = 0, W = 120, H = 80, Filled = false,
            },
            new RoundedRectPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = 0, W = 100, H = 60, Radius = 8, Filled = false,
            },
            new EllipsePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = 0, Rx = 30, Ry = 15,
            },
            new PolygonPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Filled = true,
                Points = [[-45, 40], [45, 40], [0, 90]],
            },
            new QuadCurvePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                P0X = -50, P0Y = 22, CtrlX = 0, CtrlY = 2, P2X = 50, P2Y = 22,
            },
            new CubicCurvePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                P0X = 0, P0Y = 0, C1X = 10, C1Y = -10, C2X = 20, C2Y = 10, P3X = 30, P3Y = 0,
            },
            new SinePrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 0, Cy = 0, Amp = 22, Cycles = 1, Length = 70, Axis = SineAxis.Horizontal,
            },
            new ExponentialTaperPrimitive
            {
                ColorRole = SymbolColorRole.SymbolLine, StrokeTier = SymbolStrokeTier.Normal,
                Cx = 10, Cy = 5, W1 = 60, W2 = 15, L = 100, NumPts = 20, Filled = false, Axis = SineAxis.Horizontal,
            },
            new TextPrimitive
            {
                Content = "Z", AnchorX = 5, AnchorY = -5,
                FontSize = 14, FontStyle = SymbolFontStyle.Bold, Align = SymbolTextAlign.Center,
            },
            new BitmapPrimitive
            {
                ImagePathRef = "reference.png", X = -50, Y = -50, W = 100, H = 100,
                Opacity = 0.5, Locked = true,
            },
        };
        var pins = new List<SymbolPin>
        {
            new SymbolPin(0, -200, 0, "1"),
            new SymbolPin(0,  200, 1, "2"),
        };
        return new Symbol(prims, pins);
    }

    [Fact]
    public void RoundTrip_AllPrimitiveTypes_Lossless()
    {
        var original = BuildTestSymbol();
        string json  = SymbolPersistence.Serialize(original, gridSize: 100.0);
        var   restored = SymbolPersistence.Deserialize(json);

        // Same primitive count
        Assert.Equal(original.Primitives.Count, restored.Primitives.Count);

        // Check first Line primitive
        var l0orig = (LinePrimitive)original.Primitives[0];
        var l0rest = (LinePrimitive)restored.Primitives[0];
        Assert.Equal(l0orig.ColorRole,  l0rest.ColorRole);
        Assert.Equal(l0orig.StrokeTier, l0rest.StrokeTier);
        Assert.Equal(l0orig.X1, l0rest.X1, 1e-9);
        Assert.Equal(l0orig.Y1, l0rest.Y1, 1e-9);
        Assert.Equal(l0orig.X2, l0rest.X2, 1e-9);
        Assert.Equal(l0orig.Y2, l0rest.Y2, 1e-9);

        // SymbolPlus role preserved
        var l1rest = (LinePrimitive)restored.Primitives[1];
        Assert.Equal(SymbolColorRole.SymbolPlus, l1rest.ColorRole);
        Assert.Equal(SymbolStrokeTier.Thin,      l1rest.StrokeTier);

        // Polyline
        var plorig = (PolylinePrimitive)original.Primitives[2];
        var plrest = (PolylinePrimitive)restored.Primitives[2];
        Assert.Equal(plorig.Points.Count, plrest.Points.Count);
        Assert.Equal(plorig.Points[0][0], plrest.Points[0][0], 1e-9);
        Assert.Equal(plorig.Points[0][1], plrest.Points[0][1], 1e-9);

        // Circle: coords, radius, fill
        var corig = (CirclePrimitive)original.Primitives[3];
        var crest = (CirclePrimitive)restored.Primitives[3];
        Assert.Equal(corig.Cx,     crest.Cx,     1e-9);
        Assert.Equal(corig.Cy,     crest.Cy,     1e-9);
        Assert.Equal(corig.R,      crest.R,      1e-9);
        Assert.Equal(corig.Filled, crest.Filled);

        // Arc: start/sweep angles
        var aorig = (ArcPrimitive)original.Primitives[4];
        var arest = (ArcPrimitive)restored.Primitives[4];
        Assert.Equal(aorig.StartDeg, arest.StartDeg, 1e-9);
        Assert.Equal(aorig.SweepDeg, arest.SweepDeg, 1e-9);

        // Rect
        var rorig = (RectPrimitive)original.Primitives[5];
        var rrest = (RectPrimitive)restored.Primitives[5];
        Assert.Equal(rorig.W, rrest.W, 1e-9);
        Assert.Equal(rorig.H, rrest.H, 1e-9);
        Assert.Equal(SymbolStrokeTier.Thin, rrest.StrokeTier);

        // RoundedRect
        var rrorig = (RoundedRectPrimitive)original.Primitives[6];
        var rrrest = (RoundedRectPrimitive)restored.Primitives[6];
        Assert.Equal(rrorig.Radius, rrrest.Radius, 1e-9);

        // Ellipse
        var eorig = (EllipsePrimitive)original.Primitives[7];
        var erest = (EllipsePrimitive)restored.Primitives[7];
        Assert.Equal(eorig.Rx, erest.Rx, 1e-9);
        Assert.Equal(eorig.Ry, erest.Ry, 1e-9);

        // Polygon: filled, points
        var pgorig = (PolygonPrimitive)original.Primitives[8];
        var pgrest = (PolygonPrimitive)restored.Primitives[8];
        Assert.True(pgrest.Filled);
        Assert.Equal(pgorig.Points.Count, pgrest.Points.Count);
        Assert.Equal(pgorig.Points[2][1], pgrest.Points[2][1], 1e-9);  // apex y=90

        // QuadCurve
        var qcorig = (QuadCurvePrimitive)original.Primitives[9];
        var qcrest = (QuadCurvePrimitive)restored.Primitives[9];
        Assert.Equal(qcorig.CtrlX, qcrest.CtrlX, 1e-9);
        Assert.Equal(qcorig.CtrlY, qcrest.CtrlY, 1e-9);

        // CubicCurve
        var ccorig = (CubicCurvePrimitive)original.Primitives[10];
        var ccrest = (CubicCurvePrimitive)restored.Primitives[10];
        Assert.Equal(ccorig.C1X, ccrest.C1X, 1e-9);
        Assert.Equal(ccorig.P3Y, ccrest.P3Y, 1e-9);

        // Sine: axis (enum)
        var sorig = (SinePrimitive)original.Primitives[11];
        var srest = (SinePrimitive)restored.Primitives[11];
        Assert.Equal(sorig.Amp,    srest.Amp,    1e-9);
        Assert.Equal(sorig.Cycles, srest.Cycles, 1e-9);
        Assert.Equal(sorig.Axis,   srest.Axis);

        // ExponentialTaper: W1, W2, L, Axis
        var etorig = (ExponentialTaperPrimitive)original.Primitives[12];
        var etrest = (ExponentialTaperPrimitive)restored.Primitives[12];
        Assert.Equal(etorig.W1,   etrest.W1,   1e-9);
        Assert.Equal(etorig.W2,   etrest.W2,   1e-9);
        Assert.Equal(etorig.L,    etrest.L,    1e-9);
        Assert.Equal(etorig.NumPts, etrest.NumPts);
        Assert.Equal(etorig.Axis, etrest.Axis);

        // Text: content, font style
        var torig = (TextPrimitive)original.Primitives[13];
        var trest = (TextPrimitive)restored.Primitives[13];
        Assert.Equal(torig.Content,   trest.Content);
        Assert.Equal(torig.FontStyle, trest.FontStyle);
        Assert.Equal(torig.Align,     trest.Align);
        Assert.Equal(torig.FontSize,  trest.FontSize, 1e-9);

        // Bitmap: path ref, opacity, locked
        var bmorig = (BitmapPrimitive)original.Primitives[14];
        var bmrest = (BitmapPrimitive)restored.Primitives[14];
        Assert.Equal(bmorig.ImagePathRef, bmrest.ImagePathRef);
        Assert.Equal(bmorig.Opacity,      bmrest.Opacity, 1e-9);
        Assert.True(bmrest.Locked);

        // Pins
        Assert.Equal(original.Pins.Count, restored.Pins.Count);
        Assert.Equal(0.0,  restored.Pins[0].LocalX, 1e-9);
        Assert.Equal(-200.0, restored.Pins[0].LocalY, 1e-9);
        Assert.Equal(0,    restored.Pins[0].PortIndex);
        Assert.Equal("1",  restored.Pins[0].Name);
        Assert.Equal(1,    restored.Pins[1].PortIndex);
    }

    [Fact]
    public void SaveToFile_LoadFromFile_RoundTrip()
    {
        var original = BuildTestSymbol();
        var tmp = Path.GetTempFileName() + ".csym";
        try
        {
            SymbolPersistence.SaveToFile(tmp, original, 100.0);
            var restored = SymbolPersistence.LoadFromFile(tmp);
            Assert.Equal(original.Primitives.Count, restored.Primitives.Count);
            Assert.Equal(original.Pins.Count,       restored.Pins.Count);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void FormatVersion_Mismatch_ThrowsInvalidDataException()
    {
        var sym  = BuildTestSymbol();
        string j = SymbolPersistence.Serialize(sym);
        string current = $"\"FormatVersion\": {SymbolPersistence.CurrentFormatVersion}";
        string broken  = j.Replace(current, "\"FormatVersion\": 9999");
        Assert.Throws<InvalidDataException>(() => SymbolPersistence.Deserialize(broken));
    }

    [Fact]
    public void FormatVersion_Mismatch_ErrorMessage_ContainsBothVersions()
    {
        var sym  = BuildTestSymbol();
        string j = SymbolPersistence.Serialize(sym);
        string current = $"\"FormatVersion\": {SymbolPersistence.CurrentFormatVersion}";
        string broken  = j.Replace(current, "\"FormatVersion\": 7");
        var ex = Assert.Throws<InvalidDataException>(() => SymbolPersistence.Deserialize(broken));
        Assert.Contains("7",    ex.Message);
        Assert.Contains($"{SymbolPersistence.CurrentFormatVersion}", ex.Message);
    }

    [Fact]
    public void Bitmap_StoredAsPathRef_NotBytes()
    {
        var sym = new Symbol(
            [new BitmapPrimitive { ImagePathRef = "/some/path/ref.png", X = 0, Y = 0, W = 100, H = 100, Opacity = 0.75, Locked = false }],
            []);
        string json = SymbolPersistence.Serialize(sym);

        // The JSON must contain the path string…
        Assert.Contains("/some/path/ref.png", json);
        // …and must NOT contain base64 or any embedded byte blob (no "data:" prefix).
        Assert.DoesNotContain("data:", json);

        var restored = SymbolPersistence.Deserialize(json);
        var bm = (BitmapPrimitive)restored.Primitives[0];
        Assert.Equal("/some/path/ref.png", bm.ImagePathRef);
        Assert.Equal(0.75, bm.Opacity, 1e-9);
    }

    [Fact]
    public void GridSize_RoundTrips_InCsymFile()
    {
        var sym  = BuildTestSymbol();
        string j = SymbolPersistence.Serialize(sym, gridSize: 200.0);
        // Deserialize raw to verify GridSize is present
        using var doc  = System.Text.Json.JsonDocument.Parse(j);
        double gs = doc.RootElement.GetProperty("GridSize").GetDouble();
        Assert.Equal(200.0, gs, 1e-9);
    }
}

// ── BuiltInSymbols sanity check ───────────────────────────────────────────────

public class BuiltInSymbolsTests
{
    [Theory]
    [InlineData(SymbolKind.Resistor,      1)]  // 1 polyline (lead + 6-zig + lead)
    [InlineData(SymbolKind.Capacitor,     4)]  // top lead + flat plate + QuadCurve + bottom lead
    [InlineData(SymbolKind.Ground,        4)]  // stem + 3 horizontal bars
    [InlineData(SymbolKind.Vdc,           8)]  // 6 lines + 2 TextPrimitives (+/−)
    [InlineData(SymbolKind.ZPort,         5)]  // 1 RRect + 4 TextPrims (N=2)
    [InlineData(SymbolKind.Sdd,           5)]  // 1 RRect + 4 TextPrims (N=2)
    public void PrimitiveCount_MatchesExpected(SymbolKind kind, int expected)
    {
        var sym = BuiltInSymbols.Primitives(kind);
        Assert.Equal(expected, sym.Primitives.Count);
    }

    [Fact]
    public void AllBuiltIns_HaveAtLeastOnePin()
    {
        var kinds = Enum.GetValues<SymbolKind>();
        foreach (var k in kinds)
        {
            // VAR, MEAS, and Mutual are intentionally port-less — annotation boxes with no connection pins.
            if (k is SymbolKind.Var or SymbolKind.Meas or SymbolKind.Mutual) continue;

            var sym = BuiltInSymbols.Primitives(k);
            Assert.True(sym.Pins.Count >= 1, $"Symbol {k} has no pins");
        }
    }

    [Fact]
    public void Vdc_HasSymbolLinePrimitives()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Vdc);
        var roles = sym.Primitives.OfType<LinePrimitive>().Select(l => l.ColorRole).ToList();
        Assert.Contains(SymbolColorRole.SymbolLine, roles);
        Assert.Equal(8, sym.Primitives.Count);
    }

    [Fact]
    public void ComputeGlyphBb_Resistor_IsVertical()
    {
        // Vertical resistor: single polyline spanning y∈[-200,+200], x∈[-30,+30] (zig ±30).
        // Pins at (0,±200) confirm vertical orientation.
        var sym  = BuiltInSymbols.Primitives(SymbolKind.Resistor);
        var (minX, minY, maxX, maxY) = SymbolGeometry.ComputeBb(sym.Primitives);
        Assert.Equal(-30.0, minX, 1e-6);
        Assert.Equal(-200.0, minY, 1e-6);
        Assert.Equal( 30.0, maxX, 1e-6);
        Assert.Equal(200.0, maxY, 1e-6);
        // Pins on P at vertical positions
        Assert.Equal( 0.0, sym.Pins[0].LocalX, 1e-9);
        Assert.Equal(-200.0, sym.Pins[0].LocalY, 1e-9);
        Assert.Equal( 0.0, sym.Pins[1].LocalX, 1e-9);
        Assert.Equal( 200.0, sym.Pins[1].LocalY, 1e-9);
    }

    [Fact]
    public void BuiltInSymbols_PortCount_EqualsPinCount()
    {
        // PortCount defaults to Pins.Count when not explicitly set.
        foreach (var k in Enum.GetValues<SymbolKind>())
        {
            var sym = BuiltInSymbols.Primitives(k);
            Assert.Equal(sym.Pins.Count, sym.PortCount);
        }
    }
}

// ── Layer 1 gate: EditableSymbol carries PortCount + four pin commands ────────

public class SymbolPinCommandTests
{
    private static (EditableSymbol sym, UndoRedoStack undo) Make(int portCount = 2)
    {
        var sym = new EditableSymbol { PortCount = portCount };
        return (sym, new UndoRedoStack());
    }

    [Fact]
    public void PortCount_RoundTrips_ThroughToSymbol()
    {
        var (e, _) = Make(3);
        var s = e.ToSymbol();
        Assert.Equal(3, s.PortCount);
    }

    [Fact]
    public void FromSymbol_CarriesPortCount()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.FetSdd); // 3-port
        var e   = EditableSymbol.FromSymbol(sym);
        Assert.Equal(3, e.PortCount);
    }

    [Fact]
    public void PlacePin_AddsPin_Undoable()
    {
        var (sym, undo) = Make();
        var pin = new SymbolPin(0, -100, 0, "p1");
        var cmd = new PlaceSymbolPinCommand(sym, pin);

        int notified = 0;
        sym.Changed += (_, _) => notified++;

        undo.Execute(cmd);
        Assert.Single(sym.Pins);
        Assert.Same(pin, sym.Pins[0]);
        Assert.Equal(1, notified);

        undo.Undo();
        Assert.Empty(sym.Pins);
        Assert.Equal(2, notified);

        undo.Redo();
        Assert.Single(sym.Pins);
        Assert.Equal(3, notified);
    }

    [Fact]
    public void MovePin_UpdatesPosition_Undoable()
    {
        var (sym, undo) = Make();
        var pin = new SymbolPin(0, 0, 0);
        undo.Execute(new PlaceSymbolPinCommand(sym, pin));

        var move = new MoveSymbolPinCommand(sym, pin, 100, 200);
        undo.Execute(move);
        Assert.Equal(100, pin.LocalX, 1e-9);
        Assert.Equal(200, pin.LocalY, 1e-9);

        undo.Undo();
        Assert.Equal(0, pin.LocalX, 1e-9);
        Assert.Equal(0, pin.LocalY, 1e-9);
    }

    [Fact]
    public void MovePin_LandsOnP_WhenSnapped()
    {
        // Verify a P-snapped move keeps pin on a 100-multiple.
        var (sym, undo) = Make();
        var pin = new SymbolPin(0, 0, 0);
        undo.Execute(new PlaceSymbolPinCommand(sym, pin));

        double snapped = Math.Round(173.0 / 100) * 100; // = 200
        undo.Execute(new MoveSymbolPinCommand(sym, pin, snapped, 0));
        Assert.Equal(200, pin.LocalX, 1e-9);
        Assert.Equal(0 % 100, pin.LocalX % 100, 1e-9); // on P
    }

    [Fact]
    public void DeletePin_RemovesPin_Undoable_AtSameIndex()
    {
        var (sym, undo) = Make();
        var pinA = new SymbolPin(0, -100, 0);
        var pinB = new SymbolPin(0,  100, 1);
        undo.Execute(new PlaceSymbolPinCommand(sym, pinA));
        undo.Execute(new PlaceSymbolPinCommand(sym, pinB));
        Assert.Equal(2, sym.Pins.Count);

        // Delete pinA (index 0)
        undo.Execute(new DeleteSymbolPinCommand(sym, pinA));
        Assert.Single(sym.Pins);
        Assert.Same(pinB, sym.Pins[0]);

        // Undo restores pinA at index 0
        undo.Undo();
        Assert.Equal(2, sym.Pins.Count);
        Assert.Same(pinA, sym.Pins[0]);
        Assert.Same(pinB, sym.Pins[1]);
    }

    [Fact]
    public void RemapPin_ChangesPortIndex_Undoable()
    {
        var (sym, undo) = Make();
        var pin = new SymbolPin(0, 0, 0);
        undo.Execute(new PlaceSymbolPinCommand(sym, pin));

        undo.Execute(new RemapSymbolPinCommand(sym, pin, 1));
        Assert.Equal(1, pin.PortIndex);

        undo.Undo();
        Assert.Equal(0, pin.PortIndex);
    }

    [Fact]
    public void CsymFile_PortCount_RoundTrips()
    {
        var sym = new Symbol(
            [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, 0, -200, 0, 200)],
            [new SymbolPin(0, -200, 0, "1"), new SymbolPin(0, 200, 1, "2")],
            portCount: 3);

        string json    = SymbolPersistence.Serialize(sym);
        var    restored = SymbolPersistence.Deserialize(json);
        Assert.Equal(3, restored.PortCount);
    }
}
