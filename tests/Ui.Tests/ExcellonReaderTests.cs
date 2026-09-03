// Gate for docs/sonnet-briefs/brief-L4f-excellon-drill-and-vias.md. Every fixture here is
// HAND-AUTHORED, following L4e's own rule: nothing committed under testdata/, nothing that names a
// vendor, tool or product. What that does NOT prove is recorded in src/Ui/RESOLVED.md's completion
// note.
//
// Gate 16: counters only. There is no wall-clock assertion anywhere in this file.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public class ExcellonReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("excellon-reader-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ExcellonReadResult Read(string text, DrillFormatOverride? overrides = null) =>
        ExcellonReader.Read(text, LayoutUnits.DefaultDbuPerMicron, overrides);

    // ── Gate 2: a file that declares everything, and a file that declares nothing ──────────────────

    [Fact]
    public void FullHeader_ParsesDirectly_AndNothingIsInferred()
    {
        var result = Read("""
            M48
            ;FILE_FORMAT=2:4
            INCH,TZ
            T1C0.0135
            %
            G90
            G05
            T1
            X010000Y005000
            M30
            """);

        Assert.Null(result.Refusal);
        Assert.False(result.Format.RequiredAGuess);
        Assert.Equal(GerberUnit.Inches, result.Format.Unit);
        Assert.Equal(DrillFormatEvidence.UnitsKeyword, result.Format.UnitEvidence);
        Assert.Equal(DrillFormatEvidence.FormatComment, result.Format.DigitsEvidence);
        Assert.Equal(DrillFormatEvidence.UnitsKeyword, result.Format.ZeroOmissionEvidence);
        Assert.Equal(2, result.Format.IntegerDigits);
        Assert.Equal(4, result.Format.DecimalDigits);

        // TZ keeps the TRAILING zeros, so the suppressed ones are the LEADING ones — the inversion
        // against Gerber's %FS that ExcellonFormat's header names.
        Assert.Equal(GerberZeroOmission.Leading, result.Format.ZeroOmission);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(25_400_000, hit.X);   // 1.0000 inch
        Assert.Equal(12_700_000, hit.Y);   // 0.5000 inch
        Assert.Equal(342_900, hit.DiameterDbu);
    }

    [Fact]
    public void NoHeaderAtAll_InfersTheFormat_AndTheEvidenceNamesEverySourceItUsed()
    {
        var result = Read("""
            T1C0.35
            T1
            X05000Y05000
            M30
            """);

        Assert.Null(result.Refusal);
        Assert.True(result.Format.RequiredAGuess);

        // Source 4: the tool diameters. 0.35 written to two decimals is millimetres; nothing else in
        // a file this bare is as unambiguous.
        Assert.Equal(GerberUnit.Millimetres, result.Format.Unit);
        Assert.Equal(DrillFormatEvidence.ToolDiameters, result.Format.UnitEvidence);
        Assert.Equal(DrillFormatEvidence.Defaulted, result.Format.DigitsEvidence);
        Assert.Equal(DrillFormatEvidence.Defaulted, result.Format.ZeroOmissionEvidence);

        Assert.Equal(3, result.Format.Evidence.Count);
        Assert.Contains(result.Format.Evidence, e => e.Contains("tool table", StringComparison.Ordinal));
        Assert.Contains(result.Format.Evidence, e => e.Contains("Digit format", StringComparison.Ordinal) &&
                                                     e.Contains("DEFAULTED", StringComparison.Ordinal));
        Assert.Contains(result.Format.Evidence, e => e.Contains("Zero suppression", StringComparison.Ordinal) &&
                                                     e.Contains("DEFAULTED", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("INFERRED", StringComparison.Ordinal));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(5_000_000, hit.X);    // 3:3 metric, leading zeros suppressed: 5.000 mm
    }

    [Fact]
    public void InchToolDiameters_AreRecognizedByTheirSpelling()
    {
        Assert.Equal(GerberUnit.Inches, ExcellonFormat.InferUnitFromToolDiameters(["0.0138", "0.0236"]));
        Assert.Equal(GerberUnit.Millimetres, ExcellonFormat.InferUnitFromToolDiameters(["0.35", "0.80"]));
        Assert.Equal(GerberUnit.Millimetres, ExcellonFormat.InferUnitFromToolDiameters(["1.905"]));
        Assert.Null(ExcellonFormat.InferUnitFromToolDiameters([]));
    }

    // ── Gate 3: the extents cross-check, stated as a number ────────────────────────────────────────

    private const string FourHitsInMillimetres = """
        M48
        METRIC,TZ,000.000
        T1C0.30
        %
        G90
        T1
        X001000Y001000
        X009000Y001000
        X009000Y009000
        X001000Y009000
        M30
        """;

    [Fact]
    public void ExtentsCrossCheck_AgreesWhenTheFormatIsRight_AndNamesTheFactorWhenItIsNot()
    {
        var artwork = new DrillExtents(true, 0, 0, 10_000_000, 10_000_000);   // a 10 mm square

        var right = Read(FourHitsInMillimetres);
        var ok = ExcellonReader.CrossCheckExtents(right, artwork);
        Assert.True(ok.Agrees);
        Assert.Equal(0, ok.HitsOutside);

        // The same text read as INCHES — the classic silent failure, a board 25.4x too large.
        var wrong = Read(FourHitsInMillimetres, new DrillFormatOverride(Unit: GerberUnit.Inches));
        var bad = ExcellonReader.CrossCheckExtents(wrong, artwork);

        Assert.False(bad.Agrees);
        Assert.Equal(4, bad.HitsOutside);
        Assert.Equal(4, bad.HitCount);
        Assert.Equal(20.32, bad.WidthRatio, 3);     // 8 mm of hits read as 8 inch, against a 10 mm board
        Assert.Contains("20.32", bad.Report, StringComparison.Ordinal);
        Assert.Contains("4 of 4 hits", bad.Report, StringComparison.Ordinal);
    }

    // ── Gate 4: zero suppression, both spellings, on the same coordinate text ──────────────────────

    private const string OneIntegerFiveDecimals = """
        ;FILE_FORMAT=1:5
        METRIC
        T1C0.30
        T1
        X05Y05
        M30
        """;

    [Fact]
    public void TheSameCoordinateText_MeansTwoDifferentThings_UnderTheTwoSuppressionConventions()
    {
        // 1 integer + 5 decimal digits. "X05" is 0.50000 mm under TRAILING suppression (pad right)
        // and 0.00005 mm under LEADING suppression (the digits are the last ones) — four orders of
        // magnitude apart on the identical text.
        var leading = Read(OneIntegerFiveDecimals, new DrillFormatOverride(ZeroOmission: GerberZeroOmission.Leading));
        var trailing = Read(OneIntegerFiveDecimals, new DrillFormatOverride(ZeroOmission: GerberZeroOmission.Trailing));

        Assert.Equal(50, Assert.Single(leading.Hits).X);              // 0.00005 mm
        Assert.Equal(500_000, Assert.Single(trailing.Hits).X);        // 0.50000 mm

        Assert.Equal(GerberZeroOmission.Leading, leading.Format.ZeroOmission);
        Assert.Equal(GerberZeroOmission.Trailing, trailing.Format.ZeroOmission);
        Assert.Equal(DrillFormatEvidence.Override, trailing.Format.ZeroOmissionEvidence);
    }

    [Theory]
    [InlineData("LZ", GerberZeroOmission.Trailing)]   // leading zeros KEPT -> trailing suppressed
    [InlineData("TZ", GerberZeroOmission.Leading)]    // trailing zeros KEPT -> leading suppressed
    public void TheLzTzWord_IsReadAsTheZerosKEPT_NotTheZerosOmitted(string word, GerberZeroOmission expected)
    {
        var result = Read($";FILE_FORMAT=1:5\nMETRIC,{word}\nT1C0.30\nT1\nX05Y05\nM30\n");

        Assert.Equal(expected, result.Format.ZeroOmission);
        Assert.Equal(DrillFormatEvidence.UnitsKeyword, result.Format.ZeroOmissionEvidence);
        Assert.Contains(result.Format.Evidence, e => e.Contains("zeros KEPT", StringComparison.Ordinal));
    }

    [Fact]
    public void ADecimalPointFile_SidestepsBothQuestions()
    {
        // R-L4f-2's third form, and what circuitRF's own ExcellonWriter emits.
        var result = Read("M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\nX1.500000Y-2.000000\nM30\n");

        Assert.True(result.Format.DecimalCoordinates);
        Assert.False(result.Format.RequiredAGuess);
        Assert.Equal(DrillFormatEvidence.DecimalCoordinates, result.Format.ZeroOmissionEvidence);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(1_500_000, hit.X);
        Assert.Equal(-2_000_000, hit.Y);
    }

    // ── Gate 5: what must be refused ───────────────────────────────────────────────────────────────

    [Fact]
    public void ABinaryDrillFile_IsRefusedByName_AndNothingIsImported()
    {
        byte[] binary = [0x4D, 0x00, 0x1F, 0x80, 0x02, 0x03, 0x11, 0x7F, 0x05, 0x00];
        using var stream = new MemoryStream(binary);
        var result = ExcellonReader.Read(stream);

        Assert.NotNull(result.Refusal);
        Assert.Contains("BINARY", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("ASCII", result.Refusal, StringComparison.Ordinal);
        Assert.Empty(result.Hits);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public void ADrillListing_IsNotAcceptedAsADrillFile()
    {
        var result = Read("""
            Drill Report for board
            Tool      Size        Plated     Count
            T1        0.0135 in   yes        142
            T2        0.0400 in   yes        24
            Total holes drilled: 166
            """);

        Assert.NotNull(result.Refusal);
        Assert.Contains("listing or report", result.Refusal, StringComparison.Ordinal);
        Assert.Empty(result.Hits);
    }

    // ── Gates 6 and 7: the tool table ──────────────────────────────────────────────────────────────

    [Fact]
    public void AToolDefinedInTheBody_AlsoSelectsItself_SoItsHolesAreActuallyDrilled()
    {
        // A dialect with NO M48 header and NO separate T<n> select line: the file opens with `%`, then
        // a tool definition, then that tool's coordinates, then the next definition and its own. Read
        // as a definition only, no tool is ever current and every hit is dropped as "hole with no tool
        // selected" — measured at 751 of 751 on one real board, counted but never imported, and a
        // board that arrives with no holes at all.
        var result = Read("""
            %
            T1C.01378F095S3
            X050000Y057579
            X050000Y057776
            T2C.055
            X065748Y079232
            M30
            """);

        Assert.Null(result.Refusal);
        Assert.Equal(2, result.Tools.Count);
        Assert.Equal(3, result.Hits.Count);
        Assert.DoesNotContain(result.SkippedConstructCounts, kv => kv.Key.Contains("no tool selected"));

        Assert.Equal([1, 1, 2], result.Hits.Select(h => h.Tool));
        Assert.Equal(350_012, result.Hits[0].DiameterDbu);     // .01378 inch
        Assert.Equal(1_397_000, result.Hits[2].DiameterDbu);   // .055 inch
    }

    [Fact]
    public void AToolDefinedInsideAnM48Header_DoesNotSelectItself()
    {
        // The other half of the rule, and why it is gated on the header rather than applied always:
        // declaring T1..T3 up front has not selected T3, so a body that drills without ever selecting
        // is a file we cannot read — not one to guess the last-declared tool for.
        var result = Read("""
            M48
            METRIC
            T1C0.30
            T2C0.80
            T3C1.50
            %
            G90
            X1.0Y1.0
            M30
            """);

        Assert.Null(result.Refusal);
        Assert.Equal(3, result.Tools.Count);
        Assert.Empty(result.Hits);
        Assert.Contains(result.SkippedConstructCounts, kv => kv.Key.Contains("no tool selected"));
    }

    [Fact]
    public void ThreeDistinctDiameters_YieldThreeTools_AndEachHitCarriesItsOwn()
    {
        var result = Read("""
            M48
            METRIC
            T1C0.30
            T2C0.80
            T3C1.50
            %
            G90
            T1
            X1.0Y1.0
            T2
            X2.0Y1.0
            T3
            X3.0Y1.0
            X4.0Y1.0
            M30
            """);

        Assert.Equal(3, result.Tools.Count);
        Assert.Equal([300_000, 800_000, 1_500_000], result.Tools.Select(t => t.DiameterDbu));
        Assert.Equal([300_000, 800_000, 1_500_000, 1_500_000], result.Hits.Select(h => h.DiameterDbu));
        Assert.True(result.ToolDiametersExact);
    }

    [Fact]
    public void AnInchToolAtFiveDecimals_LandsOnExactDbu_AndSoDoNegativeCoordinates()
    {
        // 0.01250 inch = 317,500 DBU exactly; the coordinate is the negative case L4e's R-L4e-2 names,
        // retested here because drill coordinates commonly go negative (the origin sits at the board
        // centre) and a truncating cast is wrong ONLY for those.
        var result = Read("""
            M48
            INCH,TZ
            ;FILE_FORMAT=2:5
            T1C0.01250
            %
            G90
            T1
            X-0100000Y-0050000
            M30
            """);

        Assert.True(result.ToolDiametersExact);
        Assert.True(result.CoordinatesExact);
        Assert.Equal(0.0, result.WorstCaseRoundingErrorDbu);
        Assert.Equal(317_500, Assert.Single(result.Tools).DiameterDbu);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(-25_400_000, hit.X);
        Assert.Equal(-12_700_000, hit.Y);
    }

    [Fact]
    public void AnInchFileAtSixDecimals_RoundsAndReportsTheWorstCaseAsANumber()
    {
        // 1e-6 inch is 25.4 DBU — the one inexact row. -7 * 25.4 = -177.8, which ROUNDS to -178; a
        // cast would truncate toward zero to -177.
        var result = Read("M48\nINCH,TZ\n;FILE_FORMAT=2:6\nT1C0.0125\n%\nG90\nT1\nX-0000007Y-0000007\nM30\n");

        Assert.False(result.CoordinatesExact);
        Assert.Equal(0.5, result.WorstCaseRoundingErrorDbu);
        Assert.Equal(-178, Assert.Single(result.Hits).X);
    }

    [Fact]
    public void AToolDiameterThatIsNotAWholeDbu_IsRoundedAndReported()
    {
        var result = Read("M48\nINCH\n;FILE_FORMAT=2:4\nT1C0.0123456\n%\nG90\nT1\nX010000Y010000\nM30\n");

        Assert.False(result.ToolDiametersExact);
        Assert.Contains(result.Diagnostics, d => d.Contains("rounded", StringComparison.Ordinal));
        Assert.Equal(313_578, Assert.Single(result.Tools).DiameterDbu);   // 0.0123456 in = 313,578.24 DBU
    }

    // ── Gate 8: plated / non-plated, all three spellings ───────────────────────────────────────────

    [Fact]
    public void PlatingFromTypeSections_SplitsTheToolTable()
    {
        var result = Read("""
            M48
            METRIC
            ;TYPE=PLATED
            T1C0.30
            T2C0.60
            ;TYPE=NON_PLATED
            T3C3.20
            %
            G90
            T1
            X1.0Y1.0
            T3
            X5.0Y5.0
            M30
            """);

        Assert.Equal([true, true, false], result.Tools.Select(t => t.Plated));

        // The file carries BOTH sections, so it has no single file-level plating — the distinction
        // lives on the tools, and claiming a file-level answer here would be a quiet lie.
        Assert.Null(result.Plated);
        Assert.Equal(true, result.Hits[0].Plated);
        Assert.Equal(false, result.Hits[1].Plated);
        Assert.Contains(result.Diagnostics, d => d.Contains("flattened again on export", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("board-PTH.drl", true)]
    [InlineData("board-NPTH.drl", false)]
    [InlineData("board-non-plated.drl", false)]
    [InlineData("board.drl", null)]
    public void PlatingFromTwoFilesDistinguishedOnlyByName(string fileName, bool? expected) =>
        Assert.Equal(expected, ExcellonReader.PlatingFromFileName(fileName));

    [Theory]
    [InlineData("Plated", true)]
    [InlineData("NonPlated", false)]
    public void PlatingFromAttributeComments(string word, bool expected)
    {
        var result = Read($"""
            M48
            METRIC
            ; #@! TF.FileFunction,{word},1,4,PTH
            ; #@! TA.AperFunction,{word},PTH,ViaDrill
            T1C0.30
            %
            G90
            T1
            X1.0Y1.0
            M30
            """);

        Assert.Equal(expected, result.Plated);
        Assert.Equal(expected, Assert.Single(result.Tools).Plated);
        Assert.Equal("ViaDrill", result.Tools[0].Function);
        Assert.Contains(result.Diagnostics, d => d.Contains("flattened again on export", StringComparison.Ordinal));
    }

    // ── Gate 9: the layer span, and blind/buried vias ──────────────────────────────────────────────

    private static string SpanFile(string plating, int from, int to, string kind, double x) => $"""
        M48
        METRIC
        ; #@! TF.FileFunction,{plating},{from},{to},{kind}
        ; #@! TA.AperFunction,{plating},PTH,ViaDrill
        T1C0.30
        %
        G90
        T1
        X{x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}Y1.000
        M30
        """;

    [Fact]
    public void ASetOfPerLayerPairDrillFiles_YieldsThroughBlindAndBuriedVias()
    {
        LayerKey[] copper = [new(1, 0), new(2, 0), new(3, 0), new(4, 0)];
        var drillLayer = new LayerKey(90, 0);

        var through = Read(SpanFile("Plated", 1, 4, "PTH", 1.0));
        var blind = Read(SpanFile("Plated", 1, 2, "Blind", 2.0));
        var buried = Read(SpanFile("Plated", 2, 3, "Buried", 3.0));

        Assert.Equal(new DrillSpan(1, 4, "PTH", true), through.Span);
        Assert.Equal(new DrillSpan(1, 2, "Blind", true), blind.Span);
        Assert.Equal(new DrillSpan(2, 3, "Buried", true), buried.Span);
        Assert.True(through.Span!.IsThroughHole);
        Assert.False(blind.Span!.IsThroughHole);

        var t = DrillViaPairing.MapSpan(through.Span, drillLayer, copper);
        var b = DrillViaPairing.MapSpan(blind.Span, drillLayer, copper);
        var u = DrillViaPairing.MapSpan(buried.Span, drillLayer, copper);

        Assert.Equal(drillLayer, t.Barrel);
        Assert.Equal(copper[0], t.Landing);
        Assert.Equal(copper[3], t.FarSide);
        Assert.Equal(copper[0], b.Landing);
        Assert.Equal(copper[1], b.FarSide);
        Assert.Equal(copper[1], u.Landing);
        Assert.Equal(copper[2], u.FarSide);
        Assert.Contains("one landing layer and not two", u.Note, StringComparison.Ordinal);

        // Reading only the through-hole file loses the blind and buried holes — they are not in it.
        // This is the assertion that a set is a SET: three files, three holes, and the two extra ones
        // exist only because the other two files were read.
        Assert.Single(through.Hits);
        Assert.Equal(1_000_000, through.Hits[0].X);
        Assert.Equal([1_000_000, 2_000_000, 3_000_000],
            new[] { through, blind, buried }.Select(r => r.Hits[0].X));
    }

    [Fact]
    public void NoDeclaredSpan_AssumesThroughHole_AndSaysSo()
    {
        var result = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX1.0Y1.0\nM30\n");

        Assert.Null(result.Span);
        Assert.Contains(result.Diagnostics, d => d.Contains("assumed to go through the whole board", StringComparison.Ordinal));

        var mapped = DrillViaPairing.MapSpan(result.Span, new LayerKey(90, 0), [new(1, 0), new(2, 0)]);
        Assert.Equal(new LayerKey(1, 0), mapped.Landing);
        Assert.Contains("No layer span was declared", mapped.Note, StringComparison.Ordinal);
    }

    // ── Gate 10: a slot is ONE opening ─────────────────────────────────────────────────────────────

    [Fact]
    public void ARoutedSlot_IsOnePathShape_NotTwoHoles()
    {
        var result = Read("""
            M48
            METRIC
            T1C1.00
            %
            G90
            T1
            G00X1.000Y1.000
            M15
            G01X3.000Y1.000
            M16
            G05
            M30
            """);

        Assert.Empty(result.Hits);
        var slot = Assert.Single(result.Slots);
        Assert.Equal([1_000_000, 1_000_000, 3_000_000, 1_000_000], slot.Xy);
        Assert.Equal(1_000_000, slot.WidthDbu);

        var paired = DrillViaPairing.Pair([], result, new LayerKey(90, 0));
        var path = Assert.Single(paired.Slots);
        Assert.Equal(PathEndStyle.Round, path.End);
        Assert.Equal(1_000_000, path.Width);
        Assert.Equal(new LayerKey(90, 0), path.Layer);
    }

    [Fact]
    public void TheCannedG85Slot_IsAlsoOneOpening()
    {
        var result = Read("M48\nMETRIC\nT1C1.00\n%\nG90\nT1\nX1.000Y1.000G85X3.000Y1.000\nM30\n");

        Assert.Empty(result.Hits);
        var slot = Assert.Single(result.Slots);
        Assert.Equal([1_000_000, 1_000_000, 3_000_000, 1_000_000], slot.Xy);
    }

    // ── R-L4f-8: the remaining syntax ──────────────────────────────────────────────────────────────

    [Fact]
    public void IncrementalCoordinates_AreNotReadAsAbsolute()
    {
        var result = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX1.000Y1.000\nG91\nX1.000Y0.000\nX1.000Y0.000\nM30\n");

        Assert.Equal([1_000_000, 2_000_000, 3_000_000], result.Hits.Select(h => h.X));
        Assert.Equal([1_000_000, 1_000_000, 1_000_000], result.Hits.Select(h => h.Y));
    }

    [Fact]
    public void TheRepeatForm_MultipliesTheLastHit()
    {
        var result = Read("M48\nMETRIC\n;FILE_FORMAT=3:3\nT1C0.30\n%\nG90\nT1\nX001000Y001000\nR03X001000\nM30\n");

        Assert.Equal(4, result.Hits.Count);
        Assert.Equal([1_000_000, 2_000_000, 3_000_000, 4_000_000], result.Hits.Select(h => h.X));
        Assert.All(result.Hits, h => Assert.Equal(1_000_000, h.Y));
    }

    [Fact]
    public void UnknownCommands_AreReportedByName_OnceWithACount()
    {
        var result = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nG37\nX1.0Y1.0\nG37\nM61\nM30\n");

        Assert.Equal(2, result.UnknownCommandCounts["G37"]);
        Assert.Equal(1, result.UnknownCommandCounts["M61"]);
        Assert.Single(result.Hits);
    }

    // ── Gates 11-15: rebuilding vias ───────────────────────────────────────────────────────────────

    private static readonly LayerKey DrillKey = new(1, 0);
    private static readonly LayerKey CopperKey = new(2, 0);

    private static GerberImportedShape Flash(long x, long y, long radius, LayerKey layer, string? function = null) =>
        new(new CircleShape { Cx = x, Cy = y, R = radius, Layer = layer }, function, null, null);

    [Fact]
    public void APadAFewNanometresOffTheHit_StillPairs()
    {
        // A third-party set's artwork and drill file are written by different halves of one tool, in
        // different units and digit formats, and they do not always land on the same DBU. One measured
        // four-layer board put a blind via's pad at (177.500001, -45.000012) mm against a drill hit at
        // (177.5, -45.0) — 12 nanometres apart, in files that unquestionably describe one via. Under
        // bit-exact pairing every blind and buried via on that board became an unpaired hole plus an
        // orphaned pad, silently and by 12 nm.
        var artwork = new[] { Flash(1, -12, 250_000, CopperKey) };
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX0.000Y0.000\nM30\n");

        var result = DrillViaPairing.Pair(artwork, drill, DrillKey, CopperKey);

        var via = Assert.Single(result.Vias);
        Assert.Equal(0, via.X);                 // the HOLE defines the via centre, not the pad
        Assert.Equal(0, via.Y);
        Assert.Equal(500_000, via.PadSize);
        Assert.Empty(result.RemainingArtwork);
    }

    [Fact]
    public void PairingIsExact_SoANeighbouringPadIsLeftAlone()
    {
        // A via at the origin, and an unrelated pad 100 microns away — the fine-pitch case a tolerance
        // would pair wrongly, and would pair wrongly most often on exactly the dense boards where it
        // matters (R-L4f-9). The snap that absorbs the nanometres above is one micron, two orders of
        // magnitude short of reaching this neighbour and three short of the tightest pitch in
        // circulation, which is what bounds it.
        var artwork = new[]
        {
            Flash(0, 0, 250_000, CopperKey),
            Flash(100_000, 0, 250_000, CopperKey),
        };
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX0.000Y0.000\nM30\n");

        var result = DrillViaPairing.Pair(artwork, drill, DrillKey, CopperKey);

        var via = Assert.Single(result.Vias);
        Assert.Equal(0, via.X);
        Assert.Equal(500_000, via.PadSize);
        Assert.Equal(300_000, via.DrillSize);

        // The neighbour is untouched: still artwork, not consumed into a via.
        var remaining = Assert.Single(result.RemainingArtwork);
        Assert.Equal(100_000, ((CircleShape)remaining.Shape).Cx);
    }

    [Fact]
    public void ADeclaredComponentDrill_IsNotAVia_EvenWhenItsGeometryLooksLikeOne()
    {
        // Deliberately built to fool a size heuristic: the VIA has the big pad and the big drill, the
        // COMPONENT hole has the small ones. R-L4f-10: where the file declares it, the classification
        // is a lookup — and a heuristic that overrides a declaration is a bug, so there is no size
        // test anywhere in the pairing to overturn this.
        var artwork = new[]
        {
            Flash(0, 0, 900_000, CopperKey),            // via pad, 1.8 mm
            Flash(5_000_000, 0, 300_000, CopperKey),    // component pad, 0.6 mm
        };
        var drill = Read("""
            M48
            METRIC
            ; #@! TA.AperFunction,Plated,PTH,ViaDrill
            T1C1.20
            ; #@! TA.AperFunction,Plated,PTH,ComponentDrill
            T2C0.40
            %
            G90
            T1
            X0.000Y0.000
            T2
            X5.000Y0.000
            M30
            """);

        var result = DrillViaPairing.Pair(artwork, drill, DrillKey, CopperKey);

        var via = Assert.Single(result.Vias);
        Assert.Equal(0, via.X);
        Assert.Equal(1_800_000, via.PadSize);
        Assert.Equal(1_200_000, via.DrillSize);
        Assert.Equal(1, result.DeclaredVias);
        Assert.Equal(0, result.InferredVias);

        var hole = Assert.Single(result.ComponentHoles);
        Assert.Equal(5_000_000, hole.Cx);
        Assert.Equal(200_000, hole.R);
        Assert.Equal(DrillKey, hole.Layer);

        // The component pad stays in the copper artwork; only the via's pad was consumed.
        var remaining = Assert.Single(result.RemainingArtwork);
        Assert.Equal(5_000_000, ((CircleShape)remaining.Shape).Cx);
    }

    [Fact]
    public void WithNoDeclarationAtAll_BothAreRebuiltAsVias_AndTheSummarySaysTheDistinctionWasNotAvailable()
    {
        var artwork = new[] { Flash(0, 0, 250_000, CopperKey), Flash(5_000_000, 0, 250_000, CopperKey) };
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX0.000Y0.000\nX5.000Y0.000\nM30\n");

        var result = DrillViaPairing.Pair(artwork, drill, DrillKey, CopperKey);

        Assert.Equal(2, result.Vias.Count);
        Assert.Equal(0, result.DeclaredVias);
        Assert.Equal(2, result.InferredVias);
        Assert.Contains(result.Diagnostics, d => d.Contains("distinction was not available", StringComparison.Ordinal));
    }

    [Fact]
    public void AViaPadAttributeOnTheArtworkSide_CountsAsADeclaration()
    {
        var artwork = new[] { Flash(0, 0, 250_000, CopperKey, "ViaPad") };
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX0.000Y0.000\nM30\n");

        var result = DrillViaPairing.Pair(artwork, drill, DrillKey, CopperKey);

        Assert.Single(result.Vias);
        Assert.Equal(1, result.DeclaredVias);
        Assert.Equal(0, result.InferredVias);
    }

    [Fact]
    public void AnUnpairedHit_BecomesACircleOnTheDrillLayer_CountedAndReported()
    {
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX1.000Y1.000\nX2.000Y1.000\nM30\n");

        var result = DrillViaPairing.Pair([], drill, DrillKey, CopperKey);

        Assert.Empty(result.Vias);
        Assert.Equal(2, result.UnpairedHoles.Count);
        Assert.All(result.UnpairedHoles, c => Assert.Equal(DrillKey, c.Layer));
        Assert.All(result.UnpairedHoles, c => Assert.Equal(150_000, c.R));
        Assert.Contains(result.Diagnostics, d => d.Contains("2 drill hit(s) had no copper flash", StringComparison.Ordinal));
    }

    [Fact]
    public void AHitWithNoDrillLayerToLandOn_IsARefusal_NotACircleOnWhateverWasNearest()
    {
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX1.000Y1.000\nM30\n");

        var result = DrillViaPairing.Pair([], drill, drillLayer: null);

        Assert.NotNull(result.Refusal);
        Assert.Contains("no drill layer to land on", result.Refusal, StringComparison.Ordinal);
        Assert.Empty(result.Vias);
        Assert.Empty(result.UnpairedHoles);
        Assert.Empty(result.Slots);
    }

    // ── Gate 11: the round trip, and the orientation proof ─────────────────────────────────────────

    private static Technology TechWithDrillLayer() => new()
    {
        Name = "T",
        Layers =
        [
            new LayerDef { Key = DrillKey, Name = "Drill", Color = new Rgba(0, 0, 0), Interchange = new InterchangeMapping(null, null, "DRILL", "TXT", "Drill,PTH") },
            new LayerDef { Key = CopperKey, Name = "Copper", Color = new Rgba(0xC0, 0x80, 0x20), Interchange = new InterchangeMapping(null, null, "COPPER", "GTL", "Copper,L1,Top") },
        ],
        Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [DrillKey] }] },
    };

    private LayoutView CreateCell(string name, Action<LayoutView> populate)
    {
        string cellDir = CellFolder.CreateCellFolder(_dir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);
        _cellDirs[name] = cellDir;
        return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{name}.clay"));
    }

    private readonly Dictionary<string, string> _cellDirs = [];

    private GerberExport.WriteResult Export(string cellName, string outFolder, LayoutView view) =>
        GerberExport.Write(Path.Combine(_dir, outFolder), "TOP",
            GerberExport.Analyze(_cellDirs[cellName], TechWithDrillLayer(), LayoutUnits.DefaultDbuPerMicron, view, null));

    /// <summary>The Gerber file minus its own creation timestamp, which the format carries by design
    /// and which therefore cannot be part of a byte-identity comparison.</summary>
    private static string[] LinesWithoutTimestamp(string path) =>
        [.. File.ReadAllLines(path).Where(l => !l.StartsWith("%TF.CreationDate", StringComparison.Ordinal))];

    [Fact]
    public void ADesignExportedByL4c_ReimportsWithItsViasRebuilt_AndReExportsToTheSameBytes()
    {
        var view = CreateCell("ORIGINAL", v => v.Shapes.Add(new ViaShape
        {
            Layer = DrillKey, LandingLayer = CopperKey,
            X = 1_000_000, Y = 2_000_000, PadSize = 500_000, DrillSize = 300_000,
        }));
        var first = Export("ORIGINAL", "out1", view);

        // The pad flash is COPPER and lands in the COPPER file — ViaShape's own doc comment
        // ("LandingLayer is the pad's own copper layer — the PAD") and brief-L4c-gerber-export.md §5
        // ("Via contributes both a pad flash IN COPPER and a drill hit"). This assertion read .TXT —
        // the DRILL layer's file — until L4h's round trip caught the writer grouping a via by its
        // barrel: the drill layer got a copper file of its own, the re-import identified that file as
        // a second drill layer, and export2 came back one file short of export1. See
        // GerberExport.GerberLayerOf.
        string artworkPath = first.FilesWritten.Single(f => f.EndsWith(".GTL", StringComparison.Ordinal));
        string drillPath = first.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal));
        Assert.DoesNotContain(first.FilesWritten, f => f.EndsWith(".TXT", StringComparison.Ordinal));

        // What L4g does: read both halves, assign each file's own layer, and pair them.
        var artwork = GerberReader.Read(File.ReadAllText(artworkPath));
        foreach (var imported in artwork.Shapes) imported.Shape.Layer = CopperKey;
        var drill = ExcellonReader.Read(File.ReadAllText(drillPath));
        var paired = DrillViaPairing.Pair(artwork.Shapes, drill, DrillKey, CopperKey);

        var via = Assert.Single(paired.Vias);
        Assert.Equal(500_000, via.PadSize);
        Assert.Equal(300_000, via.DrillSize);
        Assert.Equal(1_000_000, via.X);
        Assert.Equal(2_000_000, via.Y);

        // The flash was CONSUMED into the via: leaving it behind would re-export the same copper
        // twice and drill a second hole through it.
        Assert.Empty(paired.RemainingArtwork);

        // R-L4f-9 / L4d's R-L4d-10: the barrel-vs-landing orientation is proven by EXPORTING and
        // comparing, never by reading the two fields back. Byte identity on both files is the proof.
        var rebuilt = CreateCell("REBUILT", v => v.Shapes.Add(via));
        var second = Export("REBUILT", "out2", rebuilt);

        Assert.Equal(
            LinesWithoutTimestamp(artworkPath),
            LinesWithoutTimestamp(second.FilesWritten.Single(f => f.EndsWith(".GTL", StringComparison.Ordinal))));
        Assert.Equal(
            File.ReadAllBytes(drillPath),
            File.ReadAllBytes(second.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal))));
    }

    [Fact]
    public void GettingTheOrientationBackwards_PutsCopperWhereTheHoleShouldBe()
    {
        // The deliberate wrong answer, so the gate above is known to be able to fail. Swapping the two
        // layer fields moves the pad flash out of the COPPER file and into the drill layer's — an
        // export that renders perfectly plausibly and is completely wrong: the fab's copper file then
        // has no annular ring at all, only a hole (ViaShape's own doc comment says exactly this).
        //
        // The two expectations below were the other way round until L4h. They were not describing the
        // model's contract; they were describing what the writer happened to do, which is the failure
        // mode L4h's round trip exists to catch — a reader and a writer wrong in the same direction.
        var right = CreateCell("RIGHT", v => v.Shapes.Add(new ViaShape
        {
            Layer = DrillKey, LandingLayer = CopperKey,
            X = 1_000_000, Y = 2_000_000, PadSize = 500_000, DrillSize = 300_000,
        }));
        var wrong = CreateCell("WRONG", v => v.Shapes.Add(new ViaShape
        {
            Layer = CopperKey, LandingLayer = DrillKey,
            X = 1_000_000, Y = 2_000_000, PadSize = 500_000, DrillSize = 300_000,
        }));

        var a = Export("RIGHT", "outR", right);
        var b = Export("WRONG", "outW", wrong);

        Assert.Contains(a.FilesWritten, f => f.EndsWith(".GTL", StringComparison.Ordinal));
        Assert.DoesNotContain(a.FilesWritten, f => f.EndsWith(".TXT", StringComparison.Ordinal));
        Assert.Contains(b.FilesWritten, f => f.EndsWith(".TXT", StringComparison.Ordinal));
        Assert.DoesNotContain(b.FilesWritten, f => f.EndsWith(".GTL", StringComparison.Ordinal));

        // And swapping the two SIZE fields is visible in the drill file's own tool table.
        var sized = CreateCell("SIZES", v => v.Shapes.Add(new ViaShape
        {
            Layer = DrillKey, LandingLayer = CopperKey,
            X = 1_000_000, Y = 2_000_000, PadSize = 300_000, DrillSize = 500_000,
        }));
        var c = Export("SIZES", "outS", sized);

        string drilledRight = File.ReadAllText(a.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal)));
        string drilledWrong = File.ReadAllText(c.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal)));
        Assert.Contains("T1C0.300000", drilledRight, StringComparison.Ordinal);
        Assert.Contains("T1C0.500000", drilledWrong, StringComparison.Ordinal);
    }

    // ── Gate 14: an unpaired hit survives a re-export as a hole ────────────────────────────────────

    [Fact]
    public void TheCirclesFromUnpairedHits_ReExportAsDrillHits()
    {
        var drill = Read("M48\nMETRIC\nT1C0.30\n%\nG90\nT1\nX1.000Y1.000\nX2.000Y1.000\nM30\n");
        var paired = DrillViaPairing.Pair([], drill, DrillKey, CopperKey);
        Assert.Equal(2, paired.UnpairedHoles.Count);

        var view = CreateCell("UNPAIRED", v =>
        {
            foreach (var hole in paired.UnpairedHoles) v.Shapes.Add(hole);
        });
        var written = Export("UNPAIRED", "outU", view);

        // R-via-5: a bare circle on a drill-function layer IS a hole to the exporter, so nothing that
        // came in as a hit leaves as nothing.
        Assert.Equal(2, written.DrillHitsWritten);
        Assert.Equal(1, written.DrillToolsDefined);
        string text = File.ReadAllText(written.FilesWritten.Single(f => f.EndsWith(".drl", StringComparison.Ordinal)));
        Assert.Contains("X1.000000Y1.000000", text, StringComparison.Ordinal);
        Assert.Contains("X2.000000Y1.000000", text, StringComparison.Ordinal);
    }
}
