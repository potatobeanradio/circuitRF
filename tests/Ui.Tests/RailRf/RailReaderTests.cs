// ================================================================
//  RailReaderTests.cs — brief-railrf-2-companion-readers.md §5
//
//  The three companion readers: the placement table, the BILL OF MATERIALS, and the part library.
//  Pure model tests: no window, no app host, no Avalonia. Everything under test lives in
//  src/Design/Layout/Interchange and src/Design/RailRf, below the UI firewall, and these are here
//  rather than in a project of their own because that is where src/Design's existing tests already
//  are.
//
//  ── WHAT THIS FILE PROVES, STATED PLAINLY SO A GREEN SUITE IS NOT MISREAD (R-rail2-13) ────────
//
//  THESE FIXTURES ARE SYNTHETIC. THEY PROVE THE READERS PARSE THEIR OWN OUTPUT. THEY DO NOT PROVE
//  THE READERS PARSE A REAL EXPORT.
//
//  That is not a weakness to be apologised for, it is the deliberate arrangement: the owner's
//  decision (2026-09-18, railrf.md §8.5) is that the reference package arrives during MANUAL
//  TESTING, after the briefs are implemented. So §7's importer gate is built in two tiers and both
//  ship — this committed synthetic tier, which runs on every clone, and a guarded real-bytes tier at
//  the foot of this file, which SKIPS WITH A REASON until the package lands, exactly as
//  RfCore.Tests' own unavailable loadpull fixtures already do.
//
//  The synthetic tier carries a case per R-rail2-14 shape allowance, so those cases are already
//  green or red on the day the package arrives — which is the whole value of deferring with a plan
//  rather than deferring and hoping.
//
//  When real files arrive they are committed ANONYMISED: the repo carries no company, vendor or
//  product names, so part numbers and library prefixes are rewritten to the same SHAPE.
//
//  One test per CLAIM the brief makes, not one per field.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailReaderTests
{
    /// <summary>The artwork's own resolution for every fixture here. 1 mm is 1,000,000 DBU.</summary>
    private const int Dbu = 1000;

    private const long Mm = 1_000_000;

    // ── the placement fixture ────────────────────────────────────────────────
    //
    // Four 0402s in a row, written against PIN 1. The footprint puts pin 1 at +0.2 mm from the
    // symbol origin, the second pad at +1.2 mm, and the body centre at +0.7 mm — three DISTINCT
    // offsets, which is what makes the three origins three different readings of the same rows.

    private const string PlacementText = """
        # Units: mm
        # Origin: pin 1
        Ref,Footprint,PosX,PosY,Rot,Side
        C1,0402,10.0,10.0,0,top
        C2,0402,20.0,10.0,0,top
        C3,0402,30.0,10.0,0,top
        C4,0402,40.0,10.0,0,top
        """;

    /// <summary>The same rows with the origin record removed — R-rail2-2's refusal case.</summary>
    private const string PlacementNoOrigin = """
        # Units: mm
        Ref,Footprint,PosX,PosY,Rot,Side
        C1,0402,10.0,10.0,0,top
        C2,0402,20.0,10.0,0,top
        C3,0402,30.0,10.0,0,top
        C4,0402,40.0,10.0,0,top
        """;

    private static Dictionary<string, FootprintGeometry> Footprints(bool bottomArtwork = true) => new()
    {
        ["0402"] = new FootprintGeometry(
            PadOffsets: [(200_000, 0), (1_200_000, 0)],     // pin 1 FIRST
            BodyCentreOffset: (700_000, 0),
            HasBottomSideArtwork: bottomArtwork),
    };

    /// <summary>
    /// The artwork's pads, as the Gerber and drill readers built them — never from the placement file
    /// (R-rail2-1). Both pads of each of the four parts, read at PIN 1 … plus two NEIGHBOURING pads
    /// 0.2 mm along from C1 and C2, which is exactly the failure the origin refusal exists against:
    /// read at the symbol origin, two of the four parts land on their neighbour's pad instead.
    /// </summary>
    private static List<(long X, long Y)> ArtworkPads()
    {
        var pads = new List<(long, long)>();
        foreach (long x in new[] { 10 * Mm, 20 * Mm, 30 * Mm, 40 * Mm })
        {
            pads.Add((x, 10 * Mm));
            pads.Add((x + 1_000_000, 10 * Mm));
        }
        pads.Add((10 * Mm + 200_000, 10 * Mm));
        pads.Add((20 * Mm + 200_000, 10 * Mm));
        return pads;
    }

    // ── R-rail2-2 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_2_UnstatedPlacementOriginIsARefusalNamingTheFlag()
    {
        var table = PlacementFile.Read("place.csv", PlacementNoOrigin, Dbu);

        Assert.NotNull(table.Refusal);
        Assert.Contains(PlacementFile.OriginFlag, table.Refusal);
        Assert.Null(table.Origin);
        Assert.Equal(PlacementOriginEvidence.Unstated, table.OriginEvidence);

        // Nothing may be used — BoardNetlist's own contract — but the ROW COUNT survives, because an
        // import dialog asking the origin question wants to say how many parts it is asking about.
        Assert.Empty(table.Rows);
        Assert.Equal(4, table.ParsedRowCount);
    }

    [Fact]
    public void RailRf_R_rail2_2_AStatedOriginIsReadAndSaidToBeDeclared()
    {
        var table = PlacementFile.Read("place.csv", PlacementText, Dbu);

        Assert.Null(table.Refusal);
        Assert.Equal(PlacementOrigin.PinOne, table.Origin);
        Assert.Equal(PlacementOriginEvidence.Declared, table.OriginEvidence);

        // And the units too, on BoardNetlistUnitsEvidence's exact shape.
        Assert.Equal(LayoutUnit.Mm, table.Units);
        Assert.Equal(BoardNetlistUnitsEvidence.Declared, table.UnitsEvidence);

        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(10 * Mm, table.Rows[0].X);
        Assert.Equal(10 * Mm, table.Rows[0].Y);
        Assert.Equal("0402", table.Rows[0].Footprint);
    }

    // ── R-rail2-3 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_3_TheThreeOriginsGiveThreeLandedCountsAndNothingIsChosen()
    {
        var pads = ArtworkPads();
        var footprints = Footprints();
        var landings = new Dictionary<PlacementOrigin, PlacementLanding>();

        foreach (var origin in new[]
                 { PlacementOrigin.SymbolOrigin, PlacementOrigin.BodyCentre, PlacementOrigin.PinOne })
        {
            // The origin is STATED for the run, exactly as the flag or the dialog states it.
            var table = PlacementFile.Read("place.csv", PlacementNoOrigin, Dbu, origin);
            Assert.Null(table.Refusal);
            Assert.Equal(PlacementOriginEvidence.Chosen, table.OriginEvidence);

            landings[origin] = PlacementFile.CheckLanding(table, pads, tolerance: 50_000, footprints);
        }

        // Three different answers from one file — which is the whole reason the origin is asked for.
        Assert.Equal(4, landings[PlacementOrigin.PinOne].Landed);
        Assert.Equal(0, landings[PlacementOrigin.PinOne].Unlanded);

        Assert.Equal(2, landings[PlacementOrigin.SymbolOrigin].Landed);
        Assert.Equal(2, landings[PlacementOrigin.SymbolOrigin].Unlanded);

        Assert.Equal(0, landings[PlacementOrigin.BodyCentre].Landed);
        Assert.Equal(4, landings[PlacementOrigin.BodyCentre].Unlanded);

        // A COUNT, and the refdes behind it — never a re-choice. Nothing in the reader ranked these.
        Assert.Equal(["C3", "C4"], landings[PlacementOrigin.SymbolOrigin].UnlandedRefdes);
        Assert.Distinct(landings.Values.Select(l => l.Landed).ToList());
    }

    // ── R-rail2-4 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_4_AMirroredRowWhoseFootprintHasNoBottomArtworkIsReportedByName()
    {
        const string mirrored = """
            # Units: mm
            # Origin: pin 1
            Ref,Footprint,PosX,PosY,Rot,Side
            C1,0402,10.0,10.0,0,top
            C2,0402,20.0,10.0,0,bottom
            """;

        var table = PlacementFile.Read("place.csv", mirrored, Dbu);
        Assert.Null(table.Refusal);
        Assert.False(table.Rows[0].Mirror);
        Assert.True(table.Rows[1].Mirror);

        var landing = PlacementFile.CheckLanding(
            table, ArtworkPads(), 50_000, Footprints(bottomArtwork: false));

        // Reported, not assumed — and the refdes is named, which is what makes it actionable.
        Assert.Equal(["C2"], landing.MirrorMismatches);
    }

    // ── R-rail2-5 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_5_TheDescriptionIsParsedAndAnUnrecognisedFieldStaysNull()
    {
        var parsed = BomFile.ParseDescription("MLCC 10n0 50V 0402 X7R ±10%");

        Assert.Equal("X7R", parsed.DielectricClass);
        Assert.Equal(50, parsed.VoltageRatingV);
        Assert.Equal("0402", parsed.CaseCode);
        Assert.Equal(10, parsed.TolerancePercent);

        // A description with no class parses its voltage and leaves the class NULL — asserted as
        // null, NOT as a default. Brief 11's ESR fallback keys on this field, and a defaulted one
        // produces a part quietly given X7R's dissipation factor.
        var noClass = BomFile.ParseDescription("Aluminium electrolytic 100u 16V");
        Assert.Null(noClass.DielectricClass);
        Assert.Equal(16, noClass.VoltageRatingV);
        Assert.Null(noClass.CaseCode);
        Assert.Null(noClass.TolerancePercent);
    }

    // ── R-rail2-6 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_6_AnAggressorIsPreFilledOnlyWhereAFrequencyCouldBeRead()
    {
        const string text = """
            Refdes,Part Number,Value,Description
            Y1,PN-0001,32.768kHz,Crystal 32.768kHz 12.5pF
            U3,PN-0002,,DC-DC buck converter 2.2MHz
            U4,PN-0003,,DC-DC buck converter
            R7,PN-0004,10k,Thick film 10k 1% 0402
            """;

        var bom = BomFile.Read("bom.csv", text);
        Assert.Null(bom.Refusal);

        var found = bom.RecognisedAggressors.OrderBy(a => a.FrequencyHz).ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal(32768, found[0].FrequencyHz, 3);
        Assert.Equal(2.2e6, found[1].FrequencyHz, 3);

        // Shown as RECOGNISED rather than typed, because a pre-filled frequency nobody checked is
        // exactly the one that will be wrong.
        Assert.All(found, a => Assert.Equal(RailAggressorOrigin.Bom, a.Origin));

        // A part it cannot classify contributes NO ROW rather than a guessed one — and the converter
        // that states no frequency is COUNTED rather than invented.
        Assert.DoesNotContain(found, a => a.Name.StartsWith("R7", StringComparison.Ordinal));
        Assert.Contains(bom.Diagnostics, d => d.Contains("state no frequency", StringComparison.Ordinal));
    }

    // ── R-rail2-8 ────────────────────────────────────────────────────────────

    [Theory]
    // The design note's own two rows: a 1 µF part resonating at 5.31 MHz is 898 pH, and a 33 nF part
    // at 39.1 MHz is 502 pH.
    [InlineData(1e-6, 5.31e6, 898e-12)]
    [InlineData(33e-9, 39.1e6, 502e-12)]
    public void RailRf_R_rail2_8_InductanceIsDerivedFromCAndF0(double c, double f0, double expected)
    {
        var row = new PartLibraryRow
        {
            PartNumber = "PN-0001", CapacitanceFarads = c, SelfResonantFrequencyHz = f0,
        };

        Assert.NotNull(row.DerivedInductanceHenries);
        Assert.Equal(expected, row.DerivedInductanceHenries!.Value, expected * 0.01);
        Assert.Equal(row.DerivedInductanceHenries, row.InductanceHenries);
    }

    [Fact]
    public void RailRf_R_rail2_8_AContradictoryStatedInductanceIsReportedAndTheDerivedOneIsUsed()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-0001",
            CapacitanceFarads = 1e-6,
            SelfResonantFrequencyHz = 5.31e6,
            StatedInductanceHenries = 1.6e-9,        // the mounting loop, not the part — out by ~78 %
        });

        var row = library.Rows[0];
        Assert.Equal(row.DerivedInductanceHenries, row.InductanceHenries);      // the DERIVED one is used
        Assert.True(row.InductanceDisagreement > PartLibrary.InductanceTolerance);

        var reported = library.InductanceDisagreements();
        Assert.Single(reported);
        Assert.Contains("PN-0001", reported[0], StringComparison.Ordinal);
        Assert.Contains("derived value was used", reported[0], StringComparison.Ordinal);

        // And a row that AGREES is not reported.
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-0002",
            CapacitanceFarads = 33e-9,
            SelfResonantFrequencyHz = 39.1e6,
            StatedInductanceHenries = 502e-12,
        });
        Assert.Single(library.InductanceDisagreements());
    }

    // ── R-rail2-9 ────────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_9_TheThreeEsrStatesAreRepresentedAndTheFourthIsCounted()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow { PartNumber = "PN-CLASS", DielectricClass = "X7R" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "PN-STATED", DielectricClass = "X7R", EsrOhms = 0.004 });
        library.Rows.Add(new PartLibraryRow { PartNumber = "PN-FILE", DielectricClass = "X7R", ModelRef = "models/pn-file.s2p" });
        library.Rows.Add(new PartLibraryRow { PartNumber = "PN-NOTHING" });

        // ClassDefault is the NORMAL case (Q-15), not the degraded one.
        Assert.Equal(EsrProvenance.ClassDefault, library.Part("PN-CLASS")!.EsrBasis);
        Assert.Equal(EsrProvenance.Stated, library.Part("PN-STATED")!.EsrBasis);
        Assert.Equal(EsrProvenance.Measured, library.Part("PN-FILE")!.EsrBasis);

        // A row with no ESR and no class resolves NOTHING, and is counted rather than filled in.
        Assert.Null(library.Part("PN-NOTHING")!.EsrBasis);

        var coverage = library.Coverage(["PN-CLASS", "PN-STATED", "PN-FILE", "PN-NOTHING"]);
        Assert.Equal(["PN-NOTHING"], coverage.WithoutEsrBasis);
        Assert.Equal(["PN-CLASS"], coverage.Indicative);
    }

    // ── R-rail2-10 ───────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_10_BiasCurveCoverageIsReportedByPartNumber()
    {
        var library = new PartLibrary();
        for (int i = 1; i <= 8; i++)
        {
            var row = new PartLibraryRow { PartNumber = $"PN-{i:0000}", DielectricClass = "X7R" };
            if (i <= 5) row.BiasCurve.Add(new PartBiasPoint(0, 1e-6));
            library.Rows.Add(row);
        }

        var coverage = library.Coverage(Enumerable.Range(1, 8).Select(i => $"PN-{i:0000}"));

        Assert.Equal(8, coverage.Referenced);
        Assert.Equal(8, coverage.Known);
        Assert.Equal(5, coverage.WithBiasCurve);
        Assert.Equal(["PN-0006", "PN-0007", "PN-0008"], coverage.WithoutBiasCurve);
        Assert.Contains("3 have no bias curve", coverage.Summary, StringComparison.Ordinal);
    }

    // ── R-rail2-11 ───────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_11_AnAttachedFileOverridesTheRowAndTheWinnerIsReported()
    {
        var library = new PartLibrary { BaseDirectory = "/parts" };
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-0001",
            CapacitanceFarads = 1e-6,
            SelfResonantFrequencyHz = 5.31e6,
            EsrOhms = 0.004,
            DielectricClass = "X7R",
            ModelRef = "models/pn-0001.s2p",
        });
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-0002", CapacitanceFarads = 1e-6, SelfResonantFrequencyHz = 5.31e6,
            DielectricClass = "X7R",
        });

        var file = library.ResolveModel("PN-0001");
        Assert.Equal(PartModelSource.AttachedFile, file.Source);
        Assert.EndsWith("pn-0001.s2p", file.FilePath!, StringComparison.Ordinal);
        Assert.Equal(EsrProvenance.Measured, file.EsrBasis);       // the file beats the row's own ESR
        Assert.Contains("overrides the library row", file.Summary, StringComparison.Ordinal);

        var row = library.ResolveModel("PN-0002");
        Assert.Equal(PartModelSource.LibraryRow, row.Source);
        Assert.Null(row.FilePath);
    }

    // ── R-rail2-12 ───────────────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_12_AGerberHandedToThePlacementReaderIsNamedAsAGerber()
    {
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,0.200*%
            D10*
            X10000000Y10000000D03*
            M02*
            """;

        var table = PlacementFile.Read("top.gbr", gerber, Dbu);

        Assert.NotNull(table.Refusal);
        Assert.Contains("Gerber", table.Refusal, StringComparison.Ordinal);

        // And through the IMPORT'S OWN classifier rather than a second rule, so the two doors give
        // the same answer.
        Assert.Equal(GerberFileKind.Artwork, GerberFileClassifier.ClassifyContent("top.gbr", gerber).Kind);
    }

    [Fact]
    public void RailRf_R_rail2_12_AHeaderMatchingBothSignaturesIsRefusedNamingBothFlags()
    {
        // Reference, part number AND coordinates: genuinely both, and not distinguishable on column
        // count. One more refusal is cheaper than a board whose parts are all at (0, 0).
        const string both = """
            Refdes,Part Number,Description,PosX,PosY
            C1,PN-0001,MLCC 100n 16V 0402 X7R,10.0,10.0
            C2,PN-0001,MLCC 100n 16V 0402 X7R,20.0,10.0
            """;

        var placement = PlacementFile.Read("ambiguous.csv", both, Dbu, PlacementOrigin.PinOne);
        Assert.NotNull(placement.Refusal);
        Assert.Contains("--placement", placement.Refusal, StringComparison.Ordinal);
        Assert.Contains("--bom", placement.Refusal, StringComparison.Ordinal);

        var bom = BomFile.Read("ambiguous.csv", both);
        Assert.NotNull(bom.Refusal);
        Assert.Contains("--placement", bom.Refusal, StringComparison.Ordinal);
        Assert.Contains("--bom", bom.Refusal, StringComparison.Ordinal);

        // Neither claims it in the classifier either, for the same reason.
        var kind = GerberFileClassifier.ClassifyContent("ambiguous.csv", both).Kind;
        Assert.Equal(GerberFileKind.Other, kind);
    }

    // ── R-rail2-14 item 1: the join is ONE-TO-MANY ───────────────────────────

    [Fact]
    public void RailRf_R_rail2_14_OneReferenceMayCarryMoreThanOneRowAndNoneIsChosen()
    {
        // Behind one internal part number sits a list of approved manufacturers with their own item
        // codes (§2.2), so one row per approved manufacturer is an ordinary document.
        const string text = """
            Refdes,Part Number,Manufacturer Part Number,Description
            C1,PN-0001,AAA-100N-16-0402,MLCC 100n 16V 0402 X7R
            C1,PN-0001,BBB-104K16X7R,MLCC 100n 16V 0402 X7R
            C2,PN-0002,AAA-1U0-10-0603,MLCC 1u0 10V 0603 X5R
            """;

        var bom = BomFile.Read("bom.csv", text);
        Assert.Null(bom.Refusal);

        var c1 = bom.RowsFor("C1");
        Assert.Equal(2, c1.Count);
        Assert.Equal(["AAA-100N-16-0402", "BBB-104K16X7R"], c1.Select(r => r.ManufacturerItem));
        Assert.Single(bom.RowsFor("C2"));

        // Reported rather than resolved — taking the first silently is the shape that produces a
        // plausible model from the wrong manufacturer's part.
        Assert.Equal(2, bom.Ambiguous["C1"]);
        Assert.Contains(bom.Diagnostics, d => d.Contains("none was chosen", StringComparison.Ordinal));

        // And the part number is still ONE part number — which is what the model attaches to.
        Assert.Equal(2, bom.PartNumbers.Count);
    }

    // ── R-rail2-14 item 2: a reference cell may be a RANGE or a LIST ─────────

    [Theory]
    [InlineData("C1-C9", 9, "C1", "C9")]
    [InlineData("C1-9", 9, "C1", "C9")]
    [InlineData("C1,C2,C3", 3, "C1", "C3")]
    [InlineData("C1 C2 C3", 3, "C1", "C3")]
    [InlineData("C7", 1, "C7", "C7")]
    public void RailRf_R_rail2_14_AReferenceCellIsASetAndOneReferenceIsTheOneElementCase(
        string cell, int count, string first, string last)
    {
        var set = RefdesCell.Parse(cell);

        Assert.Equal(count, set.Refdes.Count);
        Assert.Equal(first, set.Refdes[0]);
        Assert.Equal(last, set.Refdes[^1]);
        Assert.Equal(count > 1, set.Expanded);
    }

    [Fact]
    public void RailRf_R_rail2_14_AFragmentThatIsNotARangeIsKeptVerbatimAndReported()
    {
        // Different prefixes, and a descending pair. Neither is expanded, because an expansion that
        // guesses produces references that are NOT on the board and those look real.
        foreach (string cell in new[] { "C1-R9", "C10-C2" })
        {
            var set = RefdesCell.Parse(cell);
            Assert.Equal([cell], set.Refdes);
            Assert.Equal([cell], set.Unexpanded);
        }

        // And the bill of materials reports them rather than dropping them.
        var bom = BomFile.Read("bom.csv", """
            Refdes,Part Number,Description
            C1-R9,PN-0001,MLCC 100n 16V 0402 X7R
            C2,PN-0002,MLCC 1u0 10V 0603 X5R
            """);
        Assert.Contains(bom.Diagnostics, d => d.Contains("NOT expanded", StringComparison.Ordinal));
    }

    // ── R-rail2-14 item 3: there may be NO header row at all ─────────────────

    /// <summary>
    /// <b>No header row is a refusal that names a remedy which EXISTS, and never a positional
    /// guess.</b>
    /// </summary>
    /// <remarks>
    /// <b>This test used to assert the refusal named <c>--columns</c>, and that was pinning a
    /// falsehood</b> (field report, 2026-09-22). Nothing in <c>src/Cli</c> parses that flag, and
    /// <c>netlist --placement</c>/<c>--bom</c> WRITE those tables out of a layout rather than
    /// reading one in — so the sentence sent a designer who met it in a WINDOW looking for a command
    /// line, and there was nothing at either end. The refusal itself is right and stays: column
    /// order is not a standard, and a positional reading puts the rotation in the Y column silently.
    /// What is asserted now is that the remedy named is one somebody can actually take.
    /// </remarks>
    [Fact]
    public void RailRf_R_rail2_14_NoHeaderRowIsARefusalNamingARealRemedyAndNeverAPositionalGuess()
    {
        const string headerless = """
            C1,PN-0001,MLCC 100n 16V 0402 X7R
            C2,PN-0002,MLCC 1u0 10V 0603 X5R
            C3,PN-0002,MLCC 1u0 10V 0603 X5R
            """;

        var refused = BomFile.Read("bom.csv", headerless);
        Assert.NotNull(refused.Refusal);
        Assert.Empty(refused.Rows);
        Assert.DoesNotContain("--columns", refused.Refusal, StringComparison.Ordinal);
        Assert.Contains("Add a header row", refused.Refusal, StringComparison.Ordinal);

        // Named, and it reads. Column order is not a standard, so the caller states it.
        var read = BomFile.Read("bom.csv", headerless, columns: ["Refdes", "Part Number", "Description"]);
        Assert.Null(read.Refusal);
        Assert.Equal(3, read.Rows.Count);
        Assert.Equal("PN-0001", read.Rows[0].PartNumber);
        Assert.Equal("X7R", read.Rows[0].Parsed.DielectricClass);

        // The placement reader refuses the same way, for the same reason — and ITS remedy is a
        // control: railRF asks at import and the .crail records the answer, which is the one
        // headless route there has ever been.
        var placement = PlacementFile.Read("place.csv", "C1,0402,10.0,10.0\nC2,0402,20.0,10.0", Dbu,
                                           PlacementOrigin.PinOne);
        Assert.NotNull(placement.Refusal);
        Assert.True(PlacementFile.HasNoHeader(placement));
        Assert.DoesNotContain("--columns", placement.Refusal, StringComparison.Ordinal);
        Assert.Contains("Say what the columns are", placement.Refusal, StringComparison.Ordinal);
    }

    // ── R-rail2-14 item 4: text-file mechanics, and the naming hazard ────────

    [Fact]
    public void RailRf_R_rail2_14_ByteOrderMarkQuotedDelimitersCarriageReturnsAndTrailingSeparators()
    {
        // A UNICODE BYTE ORDER MARK (U+FEFF) — which in this feature is NEVER the bill of materials —
        // then CR LF line endings, a semicolon delimiter, a quoted field carrying the delimiter and a
        // doubled quote, and a trailing separator on every row.
        string text = "﻿" + string.Join("\r\n",
        [
            "Refdes;Part Number;Description;",
            "C1;PN-0001;\"MLCC 100n; 16V; 0402 X7R\";",
            "C2;PN-0002;\"the 1u0 \"\"bulk\"\" part\";",
        ]) + "\r\n";

        var bom = BomFile.Read("bom.csv", text);

        Assert.Null(bom.Refusal);
        Assert.Equal(';', bom.Delimiter);
        Assert.Equal(2, bom.Rows.Count);

        // The byte order mark did not become part of the first column's name.
        Assert.Equal("C1", bom.Rows[0].Refdes);
        Assert.Equal("MLCC 100n; 16V; 0402 X7R", bom.Rows[0].Description);
        Assert.Equal("the 1u0 \"bulk\" part", bom.Rows[1].Description);

        // And the delimiter inside the quotes did not become a column.
        Assert.Equal("X7R", bom.Rows[0].Parsed.DielectricClass);
        Assert.Contains(bom.Diagnostics, d => d.Contains("BYTE ORDER MARK", StringComparison.Ordinal));
    }

    // ── the .crlib document ──────────────────────────────────────────────────

    [Fact]
    public void RailRf_R_rail2_7_TheLibraryIsKeyedByPartNumberAndSurvivesARoundTrip()
    {
        var library = new PartLibrary { Name = "house parts" };
        var row = new PartLibraryRow
        {
            PartNumber = "PN-0001",
            Description = "MLCC 1u0 10V 0603 X5R",
            Footprint = "0603",
            DielectricClass = "X5R",
            VoltageRatingV = 10,
            CapacitanceFarads = 1e-6,
            SelfResonantFrequencyHz = 5.31e6,
            EsrOhms = 0.004,
            ModelRef = "models/pn-0001.s2p",
        };
        row.BiasCurve.Add(new PartBiasPoint(5, 0.4e-6));
        row.BiasCurve.Add(new PartBiasPoint(0, 1.0e-6));         // written out of order on purpose
        library.Rows.Add(row);

        var back = PartLibraryIo.Deserialize(PartLibraryIo.Serialize(library));

        var read = back.Part("pn-0001");                          // the key is case-insensitive
        Assert.NotNull(read);
        Assert.Equal(1e-6, read!.CapacitanceFarads);
        Assert.Equal(EsrProvenance.Measured, read.EsrBasis);

        // The curve is sorted by bias on the way in, because an interpolation over an unsorted curve
        // is a plausible wrong number rather than an error.
        Assert.Equal([0, 5], read.BiasCurve.Select(p => p.BiasVolts));

        // Two rows for one part number is a refusal, on the way OUT as well as in.
        library.Rows.Add(new PartLibraryRow { PartNumber = "PN-0001" });
        Assert.Throws<InvalidDataException>(() => PartLibraryIo.Serialize(library));
    }

    // ================================================================
    //  The GUARDED REAL-BYTES TIER (R-rail2-13).
    //
    //  Everything above is synthetic and proves only that the readers parse their own output. These
    //  run against the reference package when it lands, and SKIP WITH A REASON until then — never
    //  fail — exactly as RfCore.Tests' proprietary loadpull fixtures already do.
    //
    //  When the files arrive they are committed ANONYMISED (railrf.md §8.5): part numbers and
    //  library prefixes rewritten to the same SHAPE, and grepped for company, vendor and product
    //  names before anything under testdata/ is committed.
    // ================================================================

    private const string ReferenceDir = "testdata/railrf-reference";
    private const string HowToObtain =
        "the reference package (Gerbers, drill, board netlist, placement and bill of materials) " +
        "arrives during manual testing; drop it in testdata/railrf-reference, anonymised.";

    [FixtureFact(ReferenceDir + "/placement.csv", HowToObtain)]
    public void RailRf_R_rail2_13_TheRealPlacementExportIsRead()
    {
        string path = FixturePaths.Require(ReferenceDir + "/placement.csv");
        var table = PlacementFile.ReadFile(path, Dbu, PlacementOrigin.PinOne);

        Assert.NotNull(table);
        Assert.Null(table!.Refusal);
        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows, r => Assert.NotEmpty(r.Refdes));
    }

    [FixtureFact(ReferenceDir + "/bom.csv", HowToObtain)]
    public void RailRf_R_rail2_13_TheRealBillOfMaterialsIsRead()
    {
        string path = FixturePaths.Require(ReferenceDir + "/bom.csv");
        var bom = BomFile.ReadFile(path);

        Assert.NotNull(bom);
        Assert.Null(bom!.Refusal);
        Assert.NotEmpty(bom.Rows);

        // The half that matters: what the descriptions actually yielded, which is the number that
        // says whether R-rail2-5's parse is worth anything on real text.
        Assert.Contains(bom.Rows, r => r.Parsed.Any);
    }

    [FixtureFact(ReferenceDir + "/library.crlib", HowToObtain)]
    public void RailRf_R_rail2_13_TheRealPartLibraryIsReadAndItsCoverageIsReported()
    {
        var library = PartLibraryIo.LoadFromFile(FixturePaths.Require(ReferenceDir + "/library.crlib"));
        Assert.NotEmpty(library.Rows);

        var coverage = library.Coverage(library.Rows.Select(r => r.PartNumber));
        Assert.Equal(library.Rows.Count, coverage.Known);
    }
}
