// Gate for docs/sonnet-briefs/brief-L4e-gerber-import-reader.md. Every fixture here is HAND-AUTHORED
// (§11): a hand-authored fixture is worth less as a dialect test than a real one, but it costs nothing
// to redistribute and names no vendor, tool or product. Which dialect properties therefore went
// untested is recorded in src/Ui/RESOLVED.md's completion note.
//
// R-L4e-21: counters only. There is no wall-clock assertion anywhere in this file.

using CircuitRF.Ui.Layout.Interchange;

using System.Text;

namespace CircuitRF.Ui.Tests;

public class GerberReaderTests
{
    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static GerberReadResult Read(string body) => GerberReader.Read(body);

    // ── Gate 2: exact units, and the negative-coordinate trap (R-L4e-2) ───────

    [Theory]
    [InlineData("24", -10_000)]     // 1e-4 inch units: -1.0000 inch
    [InlineData("25", -100_000)]    // 1e-5 inch units: -1.00000 inch
    public void InchFormat_AtFourAndFiveDecimals_LandsOnExactDbu_IncludingNegativeCoordinates(
        string digitPair, long rawCoordinate)
    {
        var result = Read(
            $"%FSLAX{digitPair}Y{digitPair}*%\n%MOIN*%\n%ADD10C,0.010*%\nD10*\n" +
            $"X{rawCoordinate}Y{rawCoordinate}D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.True(result.CoordinatesExact);
        Assert.Equal(0.0, result.WorstCaseRoundingErrorDbu);

        var circle = Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(-25_400_000, circle.Cx);
        Assert.Equal(-25_400_000, circle.Cy);
    }

    [Fact]
    public void InchFormat_AtSixDecimals_ImportsWithRoundingAndReportsTheWorstCaseAsANumber()
    {
        // 1e-6 inch is 25.4 DBU — the one inexact row of R-L4e-2's table.
        var result = Read("%FSLAX26Y26*%\n%MOIN*%\n%ADD10C,0.010*%\nD10*\nX-7Y-7D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.False(result.CoordinatesExact);
        Assert.Equal(0.5, result.WorstCaseRoundingErrorDbu);

        // -7 * 25.4 = -177.8. ROUNDED is -178. A cast would truncate toward zero to -177 — the bug
        // that survives any fixture drawn in the first quadrant.
        var circle = Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(-178, circle.Cx);
        Assert.Equal(-178, circle.Cy);
    }

    [Fact]
    public void MillimetreFormat_IsExactAtSixDecimals()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\nX-1500000Y0D03*\nM02*\n");
        Assert.True(result.CoordinatesExact);
        Assert.Equal(-1_500_000, Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape).Cx);
    }

    [Fact]
    public void TrailingZeroOmission_PadsOnTheRight()
    {
        // %FSTAX46Y46*%: "15" in a 4+6 format with trailing zeros omitted is 1500000000... i.e. the
        // digits are padded on the RIGHT to ten characters, not read as the integer 15.
        var result = Read("%FSTAX46Y46*%\n%MOMM*%\n%ADD10C,0.100*%\nD10*\nX15Y0D03*\nM02*\n");
        Assert.Equal(1_500_000_000, Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape).Cx);
    }

    [Fact]
    public void IncrementalNotation_IsRelativeToTheCurrentPoint_NotAbsolute()
    {
        // R-L4e-3: reading these as absolute is silently catastrophic, so it is supported outright.
        var result = Read("%FSLIX46Y46*%\n%MOMM*%\n%ADD10C,0.100*%\nD10*\n" +
                          "X1000000Y0D03*\nX1000000Y0D03*\nM02*\n");
        var xs = result.Shapes.Select(s => ((CircleShape)s.Shape).Cx).ToArray();
        Assert.Equal([1_000_000L, 2_000_000L], xs);
    }

    // ── Gate 3: modal coordinates and modal D-codes (R-L4e-4) ────────────────

    [Fact]
    public void ModalBlocks_ImportTheSameGeometryAsTheLonghandFile()
    {
        const string longhand = MmHeader + "%ADD10C,0.100*%\nD10*\nG01*\nX0Y0D02*\n" +
            "X1000000Y0D01*\nX1000000Y1000000D01*\nX0Y1000000D01*\nM02*\n";
        // Every draw after the first is a BARE coordinate block: the X or Y word inherits, and the
        // omitted D-code repeats. Files exist in which nearly every block looks like this.
        const string modal = MmHeader + "%ADD10C,0.100*%\nD10*\nG01*\nX0Y0D02*\n" +
            "X1000000D01*\nY1000000*\nX0*\nM02*\n";

        var a = Assert.IsType<PathShape>(Assert.Single(Read(longhand).Shapes).Shape);
        var b = Assert.IsType<PathShape>(Assert.Single(Read(modal).Shapes).Shape);
        Assert.Equal(a.Xy, b.Xy);
        Assert.Equal(a.Width, b.Width);
    }

    // ── Gate 4: the deprecated spellings that are still everywhere (R-L4e-5) ─

    [Fact]
    public void DeprecatedSpellings_AllParse_AndNoneIsRefused()
    {
        // G70 (inch), a zero-padded %ADD010, the obsolete G54 aperture-select prefix, a bare D02 with
        // no coordinates, a stray empty block, and a G04 comment in the middle of it all.
        var result = Read("%FSLAX24Y24*%\nG70*\n%ADD010C,0.004*%\nG04 a comment*\nG54D10*\n" +
                          "X0Y0D02*\nD02*\n*\nX10000Y0D01*\nM02*\n");

        Assert.Null(result.Refusal);
        var path = Assert.IsType<PathShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(101_600, path.Width);                    // 0.004 inch
        Assert.Equal([0L, 0L, 25_400_000L, 0L], path.Xy);     // 1.0000 inch
        Assert.Empty(result.UnknownCommandCounts);
    }

    // ── Gate 5: the four standard aperture shapes, and a hole (R-L4e-7) ──────

    [Fact]
    public void AllFourStandardApertureShapes_FlashAtTheRightSizeAndPlace()
    {
        var result = Read(MmHeader +
            "%ADD10C,0.100*%\n%ADD11R,0.200X0.300*%\n%ADD12O,0.400X0.200*%\n%ADD13P,0.500X6*%\n" +
            "D10*\nX0Y0D03*\n" +
            "D11*\nX1000000Y0D03*\n" +
            "D12*\nX2000000Y0D03*\n" +
            "D13*\nX3000000Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.Equal(4, result.Shapes.Count);

        var circle = Assert.IsType<CircleShape>(result.Shapes[0].Shape);
        Assert.Equal(50_000, circle.R);

        var rect = Assert.IsType<RectShape>(result.Shapes[1].Shape);
        Assert.Equal(200_000, rect.X2 - rect.X1);
        Assert.Equal(300_000, rect.Y2 - rect.Y1);
        Assert.Equal(1_000_000, (rect.X1 + rect.X2) / 2);

        var obround = Assert.IsType<PolygonShape>(result.Shapes[2].Shape);
        var (ow, oh, ocx) = Extent(obround.Xy);
        Assert.Equal(400_000, ow, tolerance: 2_000);
        Assert.Equal(200_000, oh, tolerance: 2_000);
        Assert.Equal(2_000_000, ocx, tolerance: 2_000);

        var hexagon = Assert.IsType<PolygonShape>(result.Shapes[3].Shape);
        Assert.Equal(6, hexagon.Xy.Length / 2);
        var (hw, _, hcx) = Extent(hexagon.Xy);
        Assert.Equal(500_000, hw, tolerance: 2_000);
        Assert.Equal(3_000_000, hcx, tolerance: 2_000);
    }

    [Fact]
    public void HoledAperture_YieldsAShapeWithAHole_AndIsCounted()
    {
        // R-L4e-7: a CircleShape cannot carry a hole, so this is the one case where R-L4e-9's
        // shape-identity mapping cannot apply — which is exactly why it is counted.
        var result = Read(MmHeader + "%ADD10C,0.300X0.100*%\nD10*\nX0Y0D03*\nM02*\n");

        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
        Assert.Contains(result.SkippedConstructCounts, kv => kv.Key.Contains("hole"));
    }

    // ── Gate 6: aperture macros (R-L4e-8) ────────────────────────────────────

    [Fact]
    public void Macro_WithAnExposureZeroPrimitive_YieldsAFlashWithAHole()
    {
        // The exposure modifier of the second circle is 0, so it ERASES within the aperture — how
        // every annular ring and thermal relief in this format is actually drawn.
        var result = Read(MmHeader +
            "%AMRING*\n1,1,$1,0,0*\n1,0,$2,0,0*%\n%ADD10RING,1.0X0.5*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);

        var (w, h, _) = Extent(poly.Xy);
        Assert.Equal(1_000_000, w, tolerance: 4_000);
        Assert.Equal(1_000_000, h, tolerance: 4_000);
        var (hw, hh, _) = Extent(poly.Holes![0]);
        Assert.Equal(500_000, hw, tolerance: 4_000);
        Assert.Equal(500_000, hh, tolerance: 4_000);
    }

    [Fact]
    public void Macro_WithArithmeticModifiers_EvaluatesThem_IncludingLowercaseXAsMultiply()
    {
        // 21 is a centre line: exposure, width, height, centreX, centreY, rotation. The width is
        // "$1x2" — 'x' is MULTIPLICATION in this grammar — and the centre is an expression too.
        var result = Read(MmHeader +
            "%AMEXPR*\n21,1,$1x2,$2/2,($1+$2)-0.75,0,0*%\n%ADD10EXPR,0.5X0.5*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        var (w, h, cx) = Extent(poly.Xy);
        Assert.Equal(1_000_000, w, tolerance: 2);      // 0.5 x 2 mm
        Assert.Equal(250_000, h, tolerance: 2);        // 0.5 / 2 mm
        Assert.Equal(250_000, cx, tolerance: 2);       // (0.5 + 0.5) - 0.75 mm
    }

    [Fact]
    public void MacroExpression_HonoursPrecedenceParenthesesAndBothSpellingsOfMultiply()
    {
        var vars = new Dictionary<int, double> { [1] = 3.0, [2] = 4.0 };
        Assert.Equal(11.0, GerberMacroExpression.Evaluate("$1+$2x2", vars));
        Assert.Equal(14.0, GerberMacroExpression.Evaluate("($1+$2)X2", vars));
        Assert.Equal(-1.0, GerberMacroExpression.Evaluate("$1-$2", vars));
        Assert.Equal(0.75, GerberMacroExpression.Evaluate("$1/$2", vars));
        Assert.Equal(0.0, GerberMacroExpression.Evaluate("$9", vars));      // an unsupplied argument is 0
    }

    [Fact]
    public void MacroVariableAssignment_IsVisibleToLaterBlocks()
    {
        var result = Read(MmHeader +
            "%AMASSIGN*\n$3=$1x2*\n21,1,$3,$3,0,0*%\n%ADD10ASSIGN,0.25*%\nD10*\nX0Y0D03*\nM02*\n");
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        var (w, h, _) = Extent(poly.Xy);
        Assert.Equal(500_000, w, tolerance: 2);
        Assert.Equal(500_000, h, tolerance: 2);
    }

    [Fact]
    public void MacroCommentBlocks_AreComments_NotUnrecognizedPrimitives()
    {
        // Primitive 0 is a COMMENT and its text is free-form prose: real macros carry lines like
        // "0 Free polygon", "0 $1 to $8 corner X", "0 Rectangle with rounded corners". The reader
        // strips whitespace out of every block before parsing it — which every OTHER block needs —
        // so a comment must be recognized first or it becomes "0Freepolygon", parses as no integer,
        // and is reported as a primitive we could not read. Measured on one real four-layer board:
        // 27 distinct phantom "unknown primitive" names over 150 counted occurrences, all comments.
        var result = Read(MmHeader +
            "%AMBOX*\n" +
            "0 Rectangle with rounded corners*\n" +
            "0 $1 to $8 corner X, Y*\n" +
            "0 create outline with 4 corners*\n" +
            "21,1,$1,$1,0,0,0*%\n" +
            "%ADD10BOX,0.500*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.Empty(result.SkippedConstructCounts);
        Assert.Empty(result.UnknownCommandCounts);

        // ...and the geometry the non-comment block declares is still there, unaffected.
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        var (w, h, _) = Extent(poly.Xy);
        Assert.Equal(500_000, w, tolerance: 2);
        Assert.Equal(500_000, h, tolerance: 2);
    }

    [Fact]
    public void AZeroPaddedPrimitiveCode_IsStillThatPrimitive_NotAComment()
    {
        // The comment test is on the LEADING DIGIT RUN being exactly "0", so "01" is primitive 1 and
        // must still draw — the one way a naive "starts with 0" check would silently drop geometry.
        var result = Read(MmHeader + "%AMPAD*\n01,1,0.400,0,0*%\n%ADD10PAD,*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.Empty(result.SkippedConstructCounts);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        var (w, _, _) = Extent(poly.Xy);
        Assert.Equal(400_000, w, tolerance: 4_000);
    }

    [Fact]
    public void MacroThermal_ProducesFourSpokes_AndMoireIsSkippedByName()
    {
        var thermal = Read(MmHeader +
            "%AMTH*\n7,0,0,1.0,0.6,0.2,0*%\n%ADD10TH,*%\nD10*\nX0Y0D03*\nM02*\n");
        Assert.Equal(4, thermal.Shapes.Count);
        Assert.Equal(1, thermal.FlashCount);

        var moire = Read(MmHeader +
            "%AMMOIRE*\n6,0,0,1.0,0.1,0.1,3,0.02,1.2,0*%\n%ADD10MOIRE,*%\nD10*\nX0Y0D03*\nM02*\n");
        Assert.Contains(moire.SkippedConstructCounts, kv => kv.Key.Contains("moire"));
    }

    // ── Gate 7: shape identity, asserted on the types (R-L4e-9) ──────────────

    [Fact]
    public void ShapeIdentity_CircleFlashRectFlashAndRoundCappedStroke_ComeBackAsTheirOwnTypes()
    {
        var result = Read(MmHeader + "%ADD10C,0.200*%\n%ADD11R,0.400X0.100*%\n" +
            "D10*\nX0Y0D03*\n" +
            "D11*\nX5000000Y0D03*\n" +
            "D10*\nX0Y1000000D02*\nX2000000Y1000000D01*\nX2000000Y3000000D01*\nM02*\n");

        Assert.Equal(3, result.Shapes.Count);
        Assert.IsType<CircleShape>(result.Shapes[0].Shape);
        Assert.IsType<RectShape>(result.Shapes[1].Shape);

        var path = Assert.IsType<PathShape>(result.Shapes[2].Shape);
        Assert.Equal(PathEndStyle.Round, path.End);
        Assert.Equal(200_000, path.Width);
        // R-L4e-10: consecutive D01s with the same aperture and no intervening D02 are ONE path.
        Assert.Equal([0L, 1_000_000L, 2_000_000L, 1_000_000L, 2_000_000L, 3_000_000L], path.Xy);
        Assert.Equal(1, result.StrokeCount);
    }

    [Fact]
    public void StrokeWithANonCircularAperture_IsSweptIntoARegion_AndCountedByName()
    {
        var result = Read(MmHeader + "%ADD11R,0.400X0.100*%\nD11*\n" +
            "X0Y0D02*\nX2000000Y0D01*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.All(result.Shapes, s => Assert.IsNotType<PathShape>(s.Shape));
        Assert.Contains(result.SkippedConstructCounts, kv => kv.Key.Contains("non-circular aperture"));
    }

    // ── Gate 8: arcs, in both quadrant modes (R-L4e-11) ──────────────────────

    [Fact]
    public void MultiQuadrantArc_ComesBackAsABulgeEdge_NotAPolyline()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\nG75*\nG01*\n" +
            "X1000000Y0D02*\nG03*\nX0Y1000000I-1000000J0D01*\nM02*\n");

        var path = Assert.IsType<PathShape>(Assert.Single(result.Shapes).Shape);
        var edge = Assert.Single(path.Edges!);
        Assert.Equal(EdgeKind.Arc, edge.Kind);
        Assert.Equal(Math.Tan(Math.PI / 8), edge.Bulge, precision: 9);   // a +90 degree sweep
        Assert.Equal(1, result.ArcCount);
    }

    [Fact]
    public void SingleQuadrantArc_ResolvesTheCorrectOneOfFourCandidateCentres()
    {
        // The fixture is built so that RADIUS ALONE CANNOT DECIDE: with the chord (0,0)->(1.2,0) and
        // |I|=0.6, |J|=0.8, BOTH (0.6, 0.8) and (0.6, -0.8) are exactly 1.0 from both endpoints. Only
        // the "sweep stays within one quadrant" half of R-L4e-11 eliminates the mirror image — under
        // G03 the mirror sweeps 286 degrees. The other two candidates are eliminated on radius.
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\nG74*\nG01*\n" +
            "X0Y0D02*\nG03*\nX1200000Y0I600000J800000D01*\nM02*\n");

        var path = Assert.IsType<PathShape>(Assert.Single(result.Shapes).Shape);
        var edge = Assert.Single(path.Edges!);
        Assert.Equal(EdgeKind.Arc, edge.Kind);
        Assert.Equal(1.0 / 3.0, edge.Bulge, precision: 9);

        var arc = LayoutArc.FromBulge(0L, 0L, 1_200_000L, 0L, edge.Bulge);
        Assert.Equal(600_000.0, arc.Cx, tolerance: 1.0);
        Assert.Equal(800_000.0, arc.Cy, tolerance: 1.0);   // NOT the mirror candidate at -800000
        Assert.Equal(1_000_000.0, arc.R, tolerance: 1.0);
    }

    [Fact]
    public void MultiQuadrantFullCircle_BecomesMoreThanOneEdge_BecauseABulgeCannotExpressAFullTurn()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\nG75*\nG01*\n" +
            "X1000000Y0D02*\nG03*\nX1000000Y0I-1000000J0D01*\nM02*\n");

        var path = Assert.IsType<PathShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(2, path.Edges!.Count);
        Assert.All(path.Edges!, e => Assert.Equal(EdgeKind.Arc, e.Kind));
        Assert.All(path.Edges!, e => Assert.Equal(1.0, e.Bulge, precision: 9));   // two half turns
    }

    // ── Gate 9: regions (R-L4e-12) ───────────────────────────────────────────

    [Fact]
    public void RegionWithAnInnerContour_YieldsAPolygonWithAHole_AndNoDuplicateClosingVertex()
    {
        var result = Read(MmHeader + "G01*\nG36*\n" +
            "X0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\n" +
            "X2000000Y2000000D02*\nX2000000Y8000000D01*\nX8000000Y8000000D01*\nX8000000Y2000000D01*\nX2000000Y2000000D01*\n" +
            "G37*\nM02*\n");

        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(4, poly.Xy.Length / 2);          // the closing vertex is dropped, not stored
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
        Assert.Equal(4, poly.Holes![0].Length / 2);
        Assert.Equal(1, result.RegionCount);
    }

    [Fact]
    public void RegionWithAnArcBoundary_KeepsTheArcAsAnEdge()
    {
        var result = Read(MmHeader + "G75*\nG36*\nG01*\n" +
            "X0Y0D02*\nX1000000Y0D01*\nG03*\nX1000000Y1000000I0J500000D01*\nG01*\nX0Y1000000D01*\nX0Y0D01*\n" +
            "G37*\nM02*\n");

        var curve = Assert.IsType<CurveShape>(Assert.Single(result.Shapes).Shape);
        Assert.Contains(curve.Edges!, e => e.Kind == EdgeKind.Arc);
    }

    [Fact]
    public void DegenerateRegionContour_IsSkippedAndCounted_WithoutLosingTheValidOne()
    {
        var result = Read(MmHeader + "G01*\nG36*\n" +
            "X0Y0D02*\nX1000000Y0D01*\nX0Y0D01*\n" +                                    // degenerate: two distinct vertices
            "X5000000Y0D02*\nX9000000Y0D01*\nX9000000Y4000000D01*\nX5000000Y0D01*\n" +  // a real triangle
            "G37*\nM02*\n");

        Assert.Single(result.Shapes);
        Assert.Contains(result.SkippedConstructCounts, kv => kv.Key.Contains("degenerate region contour"));
    }

    // ── Gate 10: polarity, the decision that shapes the whole importer ───────

    [Fact]
    public void LayerWithNoClearPolarity_KeepsItsPrimitives_AndIsNotComposited()
    {
        var result = Read(MmHeader + "%ADD10C,0.200*%\nD10*\nX0Y0D03*\nX400000Y0D03*\n" +
            "G01*\nX0Y2000000D02*\nX4000000Y2000000D01*\nM02*\n");

        Assert.False(result.Composited);
        Assert.Null(result.CompositeReason);
        // Deliberately asserted on the TYPES: two overlapping circle flashes stay two CircleShapes.
        Assert.Collection(result.Shapes,
            s => Assert.IsType<CircleShape>(s.Shape),
            s => Assert.IsType<CircleShape>(s.Shape),
            s => Assert.IsType<PathShape>(s.Shape));
    }

    [Fact]
    public void LayerThatPaintsAClearObject_IsComposited_ToTheCorrectGeometry_AndSaysSo()
    {
        var result = Read(MmHeader + "G01*\n" +
            "%LPD*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\n" +
            "%LPC*%\nG36*\nX2000000Y2000000D02*\nX2000000Y8000000D01*\nX8000000Y8000000D01*\nX8000000Y2000000D01*\nX2000000Y2000000D01*\nG37*\n" +
            "%LPD*%\nM02*\n");

        Assert.True(result.Composited);
        Assert.NotNull(result.CompositeReason);
        Assert.Contains("%LPC", result.CompositeReason!);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
    }

    [Fact]
    public void ClearPolarityThatPaintsNothing_DoesNotCompositeTheLayer()
    {
        // Compositing is always CORRECT and is therefore tempting as a uniform rule; it destroys shape
        // identity, so it happens only when the file actually painted something clear.
        var result = Read(MmHeader + "%ADD10C,0.200*%\nD10*\n%LPC*%\n%LPD*%\nX0Y0D03*\nM02*\n");

        Assert.False(result.Composited);
        Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape);
    }

    // ── Gate 11: refusals name the command (R-L4e-14) ────────────────────────

    [Fact]
    public void NegativeImage_IsRefusedByName_NeverSilentlyIgnored()
    {
        var result = Read(MmHeader + "%IPNEG*%\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.NotNull(result.Refusal);
        Assert.Contains("%IPNEG", result.Refusal!);
        Assert.Empty(result.Shapes);
    }

    [Fact]
    public void NonIdentityMirror_IsRefusedByName_BecauseAMirroredBoardLooksEntirelyPlausible()
    {
        var result = Read(MmHeader + "%MIA1B0*%\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.NotNull(result.Refusal);
        Assert.Contains("%MI", result.Refusal!);
        Assert.Empty(result.Shapes);
    }

    [Theory]
    [InlineData("%MIA0B0*%")]
    [InlineData("%SFA1.0B1.0*%")]
    [InlineData("%ASAXBY*%")]
    [InlineData("%IPPOS*%")]
    public void IdentityFormsOfTheDeprecatedTransforms_AreAccepted(string command)
    {
        var result = Read(MmHeader + command + "\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");
        Assert.Null(result.Refusal);
        Assert.Single(result.Shapes);
    }

    [Fact]
    public void BlockApertures_AreRefusedByName_RatherThanDroppingTheirGeometry()
    {
        var result = Read(MmHeader + "%ABD12*%\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\n%AB*%\nM02*\n");
        Assert.NotNull(result.Refusal);
        Assert.Contains("%AB", result.Refusal!);
    }

    [Fact]
    public void OffsetIsApplied_BecauseTheImplementationIsTrivial()
    {
        var result = Read(MmHeader + "%OFA1.0B2.0*%\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");
        var circle = Assert.IsType<CircleShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(1_000_000, circle.Cx);
        Assert.Equal(2_000_000, circle.Cy);
    }

    // ── Gate 12: step-and-repeat (R-L4e-15) ──────────────────────────────────

    [Fact]
    public void StepAndRepeat_IsFlattened_ReportsItsFactor_AndCreatesNoInstance()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\n" +
            "%SRX2Y3I5.0J4.0*%\nX0Y0D03*\n%SR*%\nM02*\n");

        Assert.Equal(6, result.StepRepeatFactor);
        Assert.Equal(6, result.Shapes.Count);
        Assert.Empty(result.ToStructure("panel").Instances);

        var centres = result.Shapes.Select(s => (((CircleShape)s.Shape).Cx, ((CircleShape)s.Shape).Cy)).ToHashSet();
        Assert.Contains((0L, 0L), centres);
        Assert.Contains((5_000_000L, 8_000_000L), centres);
    }

    // ── Gate 13: X2 attributes (R-L4e-16/17/18) ──────────────────────────────

    [Fact]
    public void FileApertureAndObjectAttributes_SurviveOntoTheResult()
    {
        var result = Read("%TF.FileFunction,Copper,L1,Top*%\n%TF.FilePolarity,Positive*%\n" + MmHeader +
            "%TA.AperFunction,ViaPad*%\n%ADD10C,0.100*%\n%TD*%\n" +
            "%TO.N,GND*%\n%TO.C,U3*%\n%TO.P,7*%\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Equal("Copper,L1,Top", result.FileFunction);
        Assert.Equal("Positive", result.FilePolarity);

        var shape = Assert.Single(result.Shapes);
        Assert.Equal("ViaPad", shape.AperFunction);
        Assert.Equal("GND", shape.Shape.Net);
        Assert.Equal("U3", shape.Component);
        Assert.Equal("7", shape.Pin);
    }

    [Fact]
    public void BareTd_ClearsEveryObjectAttribute_NotJustOne()
    {
        // R-L4e-18: a bare %TD is how a writer resets state between objects. Treating it as a no-op
        // leaves stale nets and component references attached to every subsequent object.
        var result = Read(MmHeader + "%ADD10C,0.100*%\n%TO.N,GND*%\n%TO.C,U3*%\nD10*\nX0Y0D03*\n" +
            "%TD*%\nX1000000Y0D03*\nM02*\n");

        Assert.Equal("GND", result.Shapes[0].Shape.Net);
        Assert.Equal("U3", result.Shapes[0].Component);
        Assert.Null(result.Shapes[1].Shape.Net);
        Assert.Null(result.Shapes[1].Component);
    }

    [Fact]
    public void AttributeEscapes_AreUndone()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\n%TO.C,R\\u002A1*%\nD10*\nX0Y0D03*\nM02*\n");
        Assert.Equal("R*1", Assert.Single(result.Shapes).Component);
    }

    [Fact]
    public void FilePolarityNegative_DoesNotInvertTheImage()
    {
        // R-L4e-17: a solder mask declares Negative — what the artwork REPRESENTS — while the file
        // itself is painted positive. Reading it as an inversion turns every mask inside out, which
        // renders plausibly and is completely wrong.
        var result = Read("%TF.FileFunction,Soldermask,Top*%\n%TF.FilePolarity,Negative*%\n" + MmHeader +
            "G01*\n%LPD*%\nG36*\nX0Y0D02*\nX1000000Y0D01*\nX1000000Y1000000D01*\nX0Y1000000D01*\nX0Y0D01*\nG37*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.Equal("Negative", result.FilePolarity);
        Assert.False(result.Composited);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes).Shape);
        Assert.Equal(4, poly.Xy.Length / 2);
        Assert.Null(poly.Holes);
    }

    [Fact]
    public void AttributeNamesMatchCaseInsensitively()
    {
        var result = Read("%TF.filefunction,Soldermask,Top*%\n" + MmHeader + "M02*\n");
        Assert.Equal("Soldermask,Top", result.FileFunction);
    }

    // ── Gate 14: unknown commands, once, with a count (R-L4e-6) ──────────────

    [Fact]
    public void UnknownCommands_AreReportedOnceWithACount_AndEverythingElseStillImports()
    {
        var result = Read(MmHeader + "%ZZsomething*%\n%ADD10C,0.100*%\n%ZZagain*%\nG99*\nD10*\nX0Y0D03*\nM02*\n");

        Assert.Null(result.Refusal);
        Assert.Single(result.Shapes);
        Assert.Equal(2, result.UnknownCommandCounts["%ZZ"]);
        Assert.Equal(1, result.UnknownCommandCounts["G99"]);
    }

    // ── Gate 15: vector fill (R-L4e-19) ──────────────────────────────────────

    [Fact]
    public void VectorFilledPour_ImportsEveryStroke_AndReportsTheCount()
    {
        // Older CAM output paints a pour as thousands of parallel strokes rather than as a region. It
        // is correct artwork and is neither editable copper nor meshable, and the user cannot act on
        // what they are not told — so the stroke count comes back on the result.
        const int strokeCount = 500;
        var sb = new StringBuilder(MmHeader).Append("%ADD10C,0.050*%\nD10*\nG01*\n");
        for (int i = 0; i < strokeCount; i++)
            sb.Append($"X0Y{i * 60_000}D02*\nX5000000Y{i * 60_000}D01*\n");
        sb.Append("M02*\n");

        var result = Read(sb.ToString());

        Assert.Equal(strokeCount, result.StrokeCount);
        Assert.Equal(strokeCount, result.Shapes.Count);
        Assert.All(result.Shapes, s => Assert.IsType<PathShape>(s.Shape));
    }

    // ── R-L4e-20: the ceiling refuses before allocating, and names its number ─

    [Fact]
    public void OverTheEntityCeiling_TheFileIsRefusedAndTheNumberIsNamed()
    {
        var sb = new StringBuilder(MmHeader).Append("%ADD10C,0.050*%\nD10*\nG01*\n");
        for (long i = 0; i <= GerberReader.EntityHardCeiling; i++) sb.Append("X0Y0D03*\n");

        var result = GerberReader.Read(sb.ToString());

        Assert.NotNull(result.Refusal);
        Assert.Contains(GerberReader.EntityHardCeiling.ToString("N0"), result.Refusal!);
        Assert.Empty(result.Shapes);
    }

    // ── R-L4e-5: %IN and %LN are recorded, acted on by neither ───────────────

    [Fact]
    public void ImageAndLayerNames_AreRecordedButChangeNothing()
    {
        var result = Read(MmHeader + "%INboard*%\n%LNTopCopper*%\n%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");
        Assert.Equal("board", result.ImageName);
        Assert.Equal("TopCopper", result.LayerName);
        Assert.Single(result.Shapes);
    }

    // ── R-L4e-0: one more consumer of InterchangeStructure ───────────────────

    [Fact]
    public void ToStructure_ReturnsTheSharedNeutralModel_WithLayersLeftToL4g()
    {
        var result = Read(MmHeader + "%ADD10C,0.100*%\nD10*\nX0Y0D03*\nM02*\n");
        var structure = result.ToStructure("top");

        Assert.Equal("top", structure.Name);
        Assert.Single(structure.Shapes);
        Assert.Empty(structure.Instances);
        Assert.Equal(default, structure.Shapes[0].Layer);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Width, height and centre-X of a flat vertex list, in DBU.</summary>
    private static (long W, long H, long Cx) Extent(long[] xy)
    {
        long minX = long.MaxValue, maxX = long.MinValue, minY = long.MaxValue, maxY = long.MinValue;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            minX = Math.Min(minX, xy[i]); maxX = Math.Max(maxX, xy[i]);
            minY = Math.Min(minY, xy[i + 1]); maxY = Math.Max(maxY, xy[i + 1]);
        }
        return (maxX - minX, maxY - minY, (minX + maxX) / 2);
    }
}
