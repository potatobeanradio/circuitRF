using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// Every fixture here is SYNTHETIC — hand-written to exercise the record grammar. Nothing in this
// file is copied from, or names, any kit. The reader is a format reader; these tests test the
// format, which is the only thing that generalises to the next kit that ships one of these files.

public sealed class DsnSymbolReaderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DsnSymbolReadResult Read(string body) =>
        DsnSymbolReader.Read(new StringReader(body), "fallback");

    /// <summary>A minimal well-formed file: header, one schematic view, caller's body, close.</summary>
    private static string Wrap(string body, string name = "SYM_A") => string.Join("\n",
    [
        "1     7.707    0 0",
        $"10    1    \"{name}\"    2    1    0    0    341    0",
        "20    0    \"\"    0 0 0 0 0    2 -3 1    1    0    \"schematic.prf\" \"schematic.lay\"",
        "44    0    -1000    1000    1000    1    0    0",
        body,
        "21",
    ]);

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_HonoursBothQuotingStyles_KeepingSpacedValuesWhole()
    {
        var t = DsnSymbolReader.Tokenize("62    0    167    9   \"Arial For CAE\"   `RF in`");

        Assert.Equal(["62", "0", "167", "9", "Arial For CAE", "RF in"], t);
    }

    [Fact]
    public void Tokenize_EmptyQuotedToken_IsPreservedAsAnEmptyString()
    {
        var t = DsnSymbolReader.Tokenize("20    0    \"\"    1");

        Assert.Equal(["20", "0", "", "1"], t);
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    [Fact]
    public void TwoPointPolyline_BecomesALine_WithYNegated()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    2    0 0 500 400 1    0    0    0    0    0    0    0    0",
            "60    4    0    2    0 0 500 400 1    0    0    0    0",
            "70    0 0    500 400",
        ])));

        var line = Assert.IsType<LinePrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(0.0, line.X1, 6);
        Assert.Equal(0.0, line.Y1, 6);
        Assert.Equal(500.0, line.X2, 6);
        Assert.Equal(-400.0, line.Y2, 6);   // file is Y-up; symbol space is Y-down
    }

    [Fact]
    public void ThreePointPolyline_BecomesAPolyline_NotALine()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    2    0 0 500 400 1    0    0    0    0    0    0    0    0",
            "60    4    0    3    0 0 500 400 1    0    0    0    0",
            "70    0 0    500 0    500 400",
        ])));

        var pl = Assert.IsType<PolylinePrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(3, pl.Points.Count);
        Assert.Equal(-400.0, pl.Points[2][1], 6);
    }

    [Fact]
    public void PointsSpanningSeveralGeometryLines_AreAccumulatedIntoOneObject()
    {
        // The format wraps long point lists across multiple records; a reader that took only the
        // first would silently truncate the shape.
        var r = Read(Wrap(string.Join("\n",
        [
            "50    2    0 0 600 300 1    0    0    0    0    0    0    0    0",
            "60    4    0    7    0 0 600 300 1    0    0    0    0",
            "70    0 0    100 0    200 100    300 100    400 200",
            "70    500 200    600 300",
        ])));

        var pl = Assert.IsType<PolylinePrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(7, pl.Points.Count);
        Assert.Equal(600.0, pl.Points[6][0], 6);
        Assert.Equal(-300.0, pl.Points[6][1], 6);
    }

    [Fact]
    public void ClosedPolygonObject_BecomesAnUnfilledPolygon()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    1    0 0 400 400 1    0    0    0    0    0    0    0    0",
            "60    4    0    4    0 0 400 400 1    0    0    0    0",
            "70    0 0    400 0    400 400    0 400",
        ])));

        var poly = Assert.IsType<PolygonPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.False(poly.Filled);
        Assert.Equal(4, poly.Points.Count);
    }

    [Fact]
    public void RectangleObject_UsesItsOwnBoundingBox_NoGeometryRecordNeeded()
    {
        var r = Read(Wrap("50    7    100 -300 400 -100 1    0    0    0    0    0    0    0    0"));

        var rect = Assert.IsType<RectPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(250.0, rect.Cx, 6);
        Assert.Equal(200.0, rect.Cy, 6);    // -(-300 + -100)/2
        Assert.Equal(300.0, rect.W, 6);
        Assert.Equal(200.0, rect.H, 6);
    }

    [Fact]
    public void FullSweep_BecomesACircle_NotAnArc()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    5    -200 -200 200 200 1    0    0    0    0    0    0    0    0",
            "60    1    0    4    -200 -200 200 200 1    0    0    0    0",
            "70    200 0    0 0    360000 0    0 0",
        ])));

        var c = Assert.IsType<CirclePrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(200.0, c.R, 6);
        Assert.Equal(0.0, c.Cx, 6);
        Assert.Equal(0.0, c.Cy, 6);
    }

    [Fact]
    public void PartialSweep_BecomesAnArc_WithHandednessFlippedByTheYAxisReflection()
    {
        // A quarter turn counter-clockwise in the file's Y-up frame is the SAME physical direction
        // as a quarter turn clockwise-negative in circuitRF's Y-down frame. Getting this wrong
        // still draws an arc — a mirrored one — so it is asserted explicitly.
        var r = Read(Wrap(string.Join("\n",
        [
            "50    5    -100 -100 100 100 1    0    0    0    0    0    0    0    0",
            "60    1    0    4    -100 -100 100 100 1    0    0    0    0",
            "70    100 0    0 0    90000 0    0 0",
        ])));

        var a = Assert.IsType<ArcPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(100.0, a.R, 6);
        Assert.Equal(0.0, a.StartDeg, 6);
        Assert.Equal(-90.0, a.SweepDeg, 6);
    }

    [Fact]
    public void ZeroRadiusArc_IsSkippedAndReported_NeverDrawn()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    5    0 0 0 0 1    0    0    0    0    0    0    0    0",
            "60    1    0    4    0 0 0 0 1    0    0    0    0",
            "70    0 0    0 0    90000 0    0 0",
        ])));

        Assert.Empty(r.Symbol!.Primitives);
        Assert.Contains(r.Diagnostics, d => d.Contains("zero radius", StringComparison.OrdinalIgnoreCase));
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Text_IsCentredOnItsOwnBoundingBox_RegardlessOfTheRecordsAnchorFields()
    {
        // Two files in hand disagree about whether the text record's own x/y is the box centre or
        // its min corner. The box is unambiguous in both, so the reader anchors from the box.
        var r = Read(Wrap(string.Join("\n",
        [
            "50    6    100 200 300 400 1    0    0    0    0    0    0    0    0",
            "62    0    200    9    100 200 0    1    0    0    0    12    0   \"Arial For CAE\"   `Vgs`",
        ])));

        var t = Assert.IsType<TextPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal("Vgs", t.Content);
        Assert.Equal(200.0, t.AnchorX, 6);
        Assert.Equal(-300.0, t.AnchorY, 6);
        Assert.Equal(200.0, t.FontSize, 6);
        Assert.Equal(SymbolTextAlign.Center, t.Align);
        Assert.Equal(SymbolTextVAlign.Middle, t.VAlign);
    }

    [Fact]
    public void TextWithNoContent_ProducesNoPrimitive()
    {
        var r = Read(Wrap("50    6    100 200 300 400 1    0    0    0    0    0    0    0    0"));

        Assert.Empty(r.Symbol!.Primitives);
    }

    // ── Stroke thickness ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("1", SymbolStrokeTier.Thin)]
    [InlineData("2", SymbolStrokeTier.Normal)]
    [InlineData("4", SymbolStrokeTier.Thick)]
    public void LineThicknessProperty_MapsOntoTheStrokeTiers(string raw, SymbolStrokeTier expected)
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    2    0 0 500 0 1    0    0    0    0    0    0    0    0",
            $"90    \"line_thickness_prop\"  1  1 0 `{raw}`",
            "60    4    0    2    0 0 500 0 1    0    0    0    0",
            "70    0 0    500 0",
        ])));

        var line = Assert.IsType<LinePrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(expected, line.StrokeTier);
    }

    // ── Pins ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pins_CarryNameAndNumber_AreYFlipped_AndSnapToTheConnectionGrid()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "42    1    2    \"gate\"      1    2    0    0 0 180000    0    0   \"\"",
            "42    2    2    \"drain\"     2    1    0    500 500 90000    0    0   \"\"",
            "42    3    2    \"source\"    3    0    0    500 -500 -90000    0    0   \"\"",
            "42    4    2    \"thermal\"   4    2    0    250 -500 180000    0    0   \"\"",
        ])));

        Assert.Equal(4, r.Pins.Count);
        Assert.Equal(["gate", "drain", "source", "thermal"], r.Pins.Select(p => p.Name));
        Assert.Equal([1, 2, 3, 4], r.Pins.Select(p => p.Number));

        // Y-up → Y-down: a pin ABOVE the origin in the file sits at NEGATIVE y in symbol space.
        Assert.Equal(500.0, r.Pins[1].X, 6);
        Assert.Equal(-500.0, r.Pins[1].Y, 6);
        Assert.Equal(500.0, r.Pins[2].Y, 6);

        // 250 is off the P=100 grid and must be snapped, or the pin can never be wired.
        Assert.Equal(300.0, r.Pins[3].X, 6);

        foreach (var p in r.Pins)
        {
            Assert.Equal(0.0, p.X % 100.0, 6);
            Assert.Equal(0.0, p.Y % 100.0, 6);
        }

        Assert.Equal(4, r.Symbol!.PortCount);
        Assert.Equal(4, r.Symbol.Pins.Count);
    }

    [Fact]
    public void PinsThatCollideAfterSnapping_AreBothKeptAndReported_NeverSilentlyMerged()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "42    1    2    \"a\"    1    0    0    0 0 0    0    0   \"\"",
            "42    2    2    \"b\"    2    0    0    20 0 0    0    0   \"\"",
        ])));

        Assert.Equal(2, r.Pins.Count);
        Assert.Equal(r.Pins[0].X, r.Pins[1].X, 6);
        Assert.Contains(r.Diagnostics, d => d.Contains("same point", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SymbolWithNoPins_StillImportsItsArtwork_AndSaysItCannotBeWired()
    {
        var r = Read(Wrap("50    7    0 0 400 400 1    0    0    0    0    0    0    0    0"));

        Assert.True(r.Success);
        Assert.Single(r.Symbol!.Primitives);
        Assert.Empty(r.Pins);
        Assert.Contains(r.Diagnostics, d => d.Contains("cannot be wired", StringComparison.OrdinalIgnoreCase));
    }

    // ── View selection ────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheSchematicView_IsRead_LayoutGeometryIsIgnored()
    {
        string file = string.Join("\n",
        [
            "1     7.707    0 0",
            "10    1    \"SYM_B\"    2    1    0    0    341    0",
            "20    0    \"\"    0 0 0 0 0    2 -3 1    1    0    \"schematic.prf\" \"schematic.lay\"",
            "44    0    0    1000    1000    1    0    0",
            "50    7    0 0 100 100 1    0    0    0    0    0    0    0    0",
            "42    1    2    \"p1\"    1    0    0    0 0 0    0    0   \"\"",
            "21",
            "20    1    \"\"    0 0 0 0 0    1 -4 1    0    0    \"layout.prf\" \"layout.lay\"",
            "44    0    0    90000    90000    1    0    0",
            "50    7    0 0 900 900 1    0    0    0    0    0    0    0    0",
            "42    9    2    \"lay\"    9    0    0    900 900 0    0    0   \"\"",
            "21",
        ]);

        var r = DsnSymbolReader.Read(new StringReader(file));

        Assert.Equal("SYM_B", r.Name);
        var rect = Assert.IsType<RectPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(100.0, rect.W, 6);
        Assert.Equal(["p1"], r.Pins.Select(p => p.Name));
    }

    [Fact]
    public void FileWithOnlyALayoutView_ReportsThatNoSchematicViewExists()
    {
        string file = string.Join("\n",
        [
            "1     7.707    0 0",
            "10    1    \"SYM_C\"    2    1    0    0    341    0",
            "20    1    \"\"    0 0 0 0 0    1 -4 1    0    0    \"layout.prf\" \"layout.lay\"",
            "50    7    0 0 900 900 1    0    0    0    0    0    0    0    0",
            "21",
        ]);

        var r = DsnSymbolReader.Read(new StringReader(file));

        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Contains("no schematic view", StringComparison.OrdinalIgnoreCase));
    }

    // ── Scale ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DeclaredViewBoundingBox_DrivesTheScale_NotJustWhereTheContentHappensToSit()
    {
        // A symbol whose visible content sits in one corner must not be blown up to fill the band.
        string file = string.Join("\n",
        [
            "20    0    \"\"    0 0 0 0 0    2 -3 1    1    0    \"schematic.prf\" \"schematic.lay\"",
            "44    0    0    2000    2000    1    0    0",
            "50    7    0 0 40 40 1    0    0    0    0    0    0    0    0",
            "21",
        ]);

        var r = DsnSymbolReader.Read(new StringReader(file));

        Assert.Equal(1.0, r.Scale, 6);
        var rect = Assert.IsType<RectPrimitive>(Assert.Single(r.Symbol!.Primitives));
        Assert.Equal(40.0, rect.W, 6);
    }

    [Fact]
    public void DegenerateDeclaredBox_FallsBackToMeasuringTheContent()
    {
        string file = string.Join("\n",
        [
            "20    0    \"\"    0 0 0 0 0    2 -3 1    1    0    \"schematic.prf\" \"schematic.lay\"",
            "44    0    0    0    0    1    0    0",
            "50    7    0 0 1000 1000 1    0    0    0    0    0    0    0    0",
            "21",
        ]);

        var r = DsnSymbolReader.Read(new StringReader(file));

        Assert.Equal(1.0, r.Scale, 6);
    }

    [Fact]
    public void Scale_IsOneForADrawingAlreadyInASensibleRange()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    7    0 0 1000 1000 1    0    0    0    0    0    0    0    0",
            "42    1    2    \"p1\"    1    0    0    0 0 0    0    0   \"\"",
        ])));

        Assert.Equal(1.0, r.Scale, 6);
    }

    [Theory]
    [InlineData(1000.0, 1.0)]
    [InlineData(10.0, 100.0)]          // authored in a unit 100× smaller
    [InlineData(500_000.0, 0.01)]      // authored in a unit 100× larger
    public void Scale_IsAPowerOfTenChosenFromTheExtent_SoAnyDrawingUnitLandsLegible(double extent, double expected)
    {
        double scale = DsnSymbolReader.ChooseScale(extent);

        Assert.Equal(expected, scale, 6);
        Assert.InRange(extent * scale, 300.0, 30_000.0);
    }

    [Fact]
    public void ChooseScale_DegenerateExtent_FallsBackToOne_NeverDividesByZero()
    {
        Assert.Equal(1.0, DsnSymbolReader.ChooseScale(0.0), 6);
        Assert.Equal(1.0, DsnSymbolReader.ChooseScale(double.NaN), 6);
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void ArbitraryText_IsRefusedCleanly_NotMisparsed()
    {
        var r = DsnSymbolReader.Read(new StringReader("this is not a symbol file\nnor is this\n"));

        Assert.False(r.Success);
        Assert.NotEmpty(r.Diagnostics);
    }

    [Fact]
    public void EmptyInput_IsRefusedCleanly()
    {
        var r = DsnSymbolReader.Read(new StringReader(""));

        Assert.False(r.Success);
        Assert.NotEmpty(r.Diagnostics);
    }

    [Fact]
    public void TruncatedRecords_AreSkipped_AndTheRestOfTheSymbolStillImports()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    7",                                     // truncated object header
            "42    1    2    \"p1\"",                       // truncated pin
            "50    7    0 0 400 400 1    0    0    0    0    0    0    0    0",
            "42    2    2    \"good\"    2    0    0    0 0 0    0    0   \"\"",
        ])));

        Assert.True(r.Success);
        Assert.Single(r.Symbol!.Primitives);
        Assert.Equal(["good"], r.Pins.Select(p => p.Name));
    }

    [Fact]
    public void UnknownObjectKind_IsSkippedAndNamedInTheDiagnostics_NeverSilentlyDropped()
    {
        var r = Read(Wrap(string.Join("\n",
        [
            "50    93    0 0 400 400 1    0    0    0    0    0    0    0    0",
            "42    1    2    \"p1\"    1    0    0    0 0 0    0    0   \"\"",
        ])));

        Assert.Empty(r.Symbol!.Primitives);
        Assert.Contains(r.Diagnostics, d => d.Contains("93"));
    }

    [Fact]
    public void MissingSymbolName_FallsBackToTheCallerSuppliedName()
    {
        string file = string.Join("\n",
        [
            "20    0    \"\"    0 0 0 0 0    2 -3 1    1    0    \"schematic.prf\" \"schematic.lay\"",
            "50    7    0 0 400 400 1    0    0    0    0    0    0    0    0",
            "21",
        ]);

        var r = DsnSymbolReader.Read(new StringReader(file), "from_the_file_name");

        Assert.Equal("from_the_file_name", r.Name);
    }

    [Fact]
    public void ReadFile_MissingPath_ReturnsAFailureRatherThanThrowing()
    {
        var r = DsnSymbolReader.ReadFile(Path.Combine(Path.GetTempPath(), "no-such-symbol-file.dsn"));

        Assert.False(r.Success);
        Assert.NotEmpty(r.Diagnostics);
    }
}
