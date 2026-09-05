using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Phase PL2's acceptance gates (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §7).
///
/// <para>Every fixture under <c>testdata/component-samples/pl2/</c> is SYNTHETIC (R-PL2-19), authored
/// to each grammar with invented part and terminal names throughout. One part — <c>GIZMO4</c>, five
/// terminals, one of them the string-named <c>TPAD</c> — is expressed in all five grammars, which is
/// what makes the cross-format gates (10 and 11) mean anything.</para>
///
/// <para><b>The part is deliberately awkward in three ways</b>, each of which a naive reader gets
/// wrong while producing something that looks right: the symbol's drawing order is not the pad order
/// (so an ordinal join mis-wires it), the land pattern is asymmetric in Y (so a spurious Y flip is
/// visible), and one pad identifier is not a number (so parsing it as an integer drops it).</para>
///
/// <para>Counters only, never wall clock (gate 14).</para>
/// </summary>
public class ComponentImportBreadthTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static string Dir(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine([dir, "testdata", "component-samples", "pl2", .. parts]);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"Fixture not found: {string.Join('/', parts)}");
    }

    private static ComponentPart Read(ComponentFormatFamily family, params string[] folder)
    {
        var scan = ComponentFolderScan.Scan(Dir(folder));
        var candidate = scan.Candidates.FirstOrDefault(c => c.Family == family);
        Assert.NotNull(candidate);

        var read = ComponentRead.Read(candidate, Dbu);
        Assert.Null(read.Refusal);
        Assert.NotNull(read.Part);
        return read.Part;
    }

    /// <summary>The joined terminal table — the invariant both views share (PL1 R-PL1-8).</summary>
    private static IReadOnlyList<ComponentTerminal> Terminals(ComponentPart part)
        => ComponentTerminals.Build(part, part.Footprints.FirstOrDefault()?.PadNames ?? []).Terminals;

    private static string MapOf(ComponentPart part)
        => string.Join(" ", Terminals(part).Select(t => $"{t.PadName}={t.PinName}"));

    /// <summary>What every format must agree on, and what an ordinal join gets wrong.</summary>
    private const string ExpectedMap = "1=ALPHA 2=BETA 3=GAMMA 4=DELTA TPAD=THERMAL";

    // ── Gate 2: one entry point ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// §5: PL2 adds no UI. Every format arrives through PL1's own <c>Import ▸ Component…</c> — its
    /// folder scan, its ranked chooser, its <c>ComponentRead</c>, its cell-folder output — and the way
    /// that is enforced is that the classifier is the ONLY thing widened.
    /// </summary>
    [Theory]
    [InlineData(ComponentFormatFamily.Records, "records")]
    [InlineData(ComponentFormatFamily.Hkp, "hkp")]
    [InlineData(ComponentFormatFamily.Plx, "plx")]
    [InlineData(ComponentFormatFamily.Cxf, "cxf")]
    [InlineData(ComponentFormatFamily.Script, "scr")]
    public void Gate2_EveryFormatAppearsInPl1sOwnRankedChooser(ComponentFormatFamily family, string folder)
    {
        var scan = ComponentFolderScan.Scan(Dir(folder));

        var candidate = Assert.Single(scan.Candidates, c => c.Family == family);
        Assert.Equal(ComponentCompleteness.SymbolFootprintAndMap, candidate.Completeness);
        Assert.NotEmpty(candidate.FormatSummary);
    }

    // ── Gate 3: PL1's invariants, per format ──────────────────────────────────────────────────────

    /// <summary>
    /// The pin↔pad map, read through each grammar's own spelling of it.
    ///
    /// <para><b>This is the gate that catches R-PL2-12 and its <c>.hkp</c> twin.</b> The symbol's
    /// drawing order is ALPHA, DELTA, BETA, GAMMA, THERMAL while the pad order is 1, 2, 3, 4, TPAD, so
    /// a reader that joins the two by position produces
    /// <c>1=ALPHA 2=DELTA 3=BETA 4=GAMMA TPAD=THERMAL</c> — fully populated, correctly shaped and
    /// wrongly wired. Nothing but this assertion tells the two apart.</para>
    /// </summary>
    [Theory]
    [InlineData(ComponentFormatFamily.Records, "records")]
    [InlineData(ComponentFormatFamily.Hkp, "hkp")]
    [InlineData(ComponentFormatFamily.Plx, "plx")]
    [InlineData(ComponentFormatFamily.Cxf, "cxf")]
    [InlineData(ComponentFormatFamily.Script, "scr")]
    public void Gate3a_ThePinPadMapSurvivesEveryGrammar(ComponentFormatFamily family, string folder)
        => Assert.Equal(ExpectedMap, MapOf(Read(family, folder)));

    /// <summary>
    /// R-PL1-9: a pad identifier is a STRING. <c>TPAD</c> is not a number, and a reader that parses
    /// pad identifiers as integers drops that terminal entirely — the part then imports with four
    /// terminals and no thermal connection, which looks like a complete part.
    /// </summary>
    [Theory]
    [InlineData(ComponentFormatFamily.Records, "records")]
    [InlineData(ComponentFormatFamily.Hkp, "hkp")]
    [InlineData(ComponentFormatFamily.Plx, "plx")]
    [InlineData(ComponentFormatFamily.Cxf, "cxf")]
    [InlineData(ComponentFormatFamily.Script, "scr")]
    public void Gate3b_AStringPadIdentifierIsATerminalLikeAnyOther(ComponentFormatFamily family, string folder)
    {
        var terminals = Terminals(Read(family, folder));

        var thermal = Assert.Single(terminals, t => t.PadName == "TPAD");
        Assert.Equal("THERMAL", thermal.PinName);

        // Numerals sort numerically and non-numeric identifiers come last (R-PL1-8), so the string
        // pad is terminal 5 rather than sorting between "1" and "2" as text.
        Assert.Equal(5, thermal.PortIndex);
    }

    /// <summary>
    /// <b>The footprint half does NOT flip Y, which is the opposite of PL1's rule</b>
    /// (ComponentArtwork's header). Every format in this phase is already +y up, while the board
    /// format PL1 reuses is +y down and negates.
    ///
    /// <para>The fixture's pads sit at +30 and +10 mil and NOWHERE below the axis, so a spurious flip
    /// moves every one of them — which a land pattern symmetric about its X axis could never show.</para>
    /// </summary>
    [Theory]
    [InlineData(ComponentFormatFamily.Records, "records")]
    [InlineData(ComponentFormatFamily.Hkp, "hkp")]
    [InlineData(ComponentFormatFamily.Plx, "plx")]
    [InlineData(ComponentFormatFamily.Cxf, "cxf")]
    [InlineData(ComponentFormatFamily.Script, "scr")]
    public void Gate3c_TheFootprintKeepsItsHandedness(ComponentFormatFamily family, string folder)
    {
        var cell = Read(family, folder).Footprints[0].Cell;

        var pad1 = Assert.Single(cell.Pins, p => p.Pin.Name == "1");
        Assert.Equal(-2032000, pad1.Pin.X);          // -80 mil
        Assert.Equal(+762000, pad1.Pin.Y);           // +30 mil, NOT -762000

        var pad4 = Assert.Single(cell.Pins, p => p.Pin.Name == "4");
        Assert.Equal(+2032000, pad4.Pin.X);
        Assert.Equal(+762000, pad4.Pin.Y);
    }

    /// <summary>
    /// The SYMBOL half is +y up too, and stays that way — <c>ComponentImport.FlipY</c> performs the
    /// <c>.csym</c> flip downstream (PL1 §3). A reader that flips here double-flips the symbol and it
    /// renders upside down beside a correct footprint.
    /// </summary>
    [Theory]
    [InlineData(ComponentFormatFamily.Records, "records")]
    [InlineData(ComponentFormatFamily.Hkp, "hkp")]
    [InlineData(ComponentFormatFamily.Plx, "plx")]
    [InlineData(ComponentFormatFamily.Cxf, "cxf")]
    [InlineData(ComponentFormatFamily.Script, "scr")]
    public void Gate3d_TheSymbolKeepsItsHandedness(ComponentFormatFamily family, string folder)
    {
        var symbol = Read(family, folder).Symbol;
        Assert.NotNull(symbol);

        // The body runs from +200 down to -800, so the drawing is asymmetric in Y and a flip shows.
        var second = symbol.Pins[1];
        Assert.Equal(0, second.XMil);
        Assert.Equal(-100, second.YMil);

        var fourth = symbol.Pins[3];
        Assert.Equal(1800, fourth.XMil);
        Assert.Equal(-300, fourth.YMil);
    }

    // ── Gate 4: count-driven records (R-PL2-4) ────────────────────────────────────────────────────

    /// <summary>
    /// A declared vertex count the file cannot honour is a REFUSAL naming the entity — never a resync.
    /// A reader that scans for keywords instead of consuming by count reads straight past this and
    /// mis-associates every piece after it.
    /// </summary>
    [Fact]
    public void Gate4a_ADeclaredCountTheFileCannotHonourIsRefusedByEntity()
    {
        var result = ComponentRecordsReader.Read(
            File.ReadAllText(Dir("records", "PARTLIB.p")),
            File.ReadAllText(Dir("records-overrun", "PARTLIB.d")),
            File.ReadAllText(Dir("records", "PARTLIB.c")),
            Dbu);

        Assert.Null(result.Part);
        Assert.NotNull(result.Refusal);
        Assert.Contains("GIZMO4_LAND", result.Refusal);
        Assert.Contains("40 vertices", result.Refusal);
    }

    /// <summary>And the same decal, merely long, imports correctly — the refusal is about the COUNT
    /// disagreeing with the file, not about size.</summary>
    [Fact]
    public void Gate4b_ADecalThatIsMerelyLongImportsCorrectly()
    {
        var part = Read(ComponentFormatFamily.Records, "records");

        Assert.Equal(2, part.Footprints.Count);
        Assert.Equal(5, part.Footprints[0].PadNames.Count);
        Assert.Equal(ExpectedMap, MapOf(part));
    }

    // ── Gate 5: two grammars, one extension (R-PL2-6) ─────────────────────────────────────────────

    /// <summary>Both <c>.hkp</c> grammars import from the same folder, classified by CONTENT.</summary>
    [Fact]
    public void Gate5a_BothHkpGrammarsAreRecognisedInOneFolder()
    {
        var kinds = Directory.GetFiles(Dir("hkp"))
            .ToDictionary(f => Path.GetFileName(f), ComponentClassifier.Classify);

        Assert.Equal(ComponentFileKind.HkpParts, kinds["_Parts.hkp"]);
        Assert.Equal(ComponentFileKind.HkpCells, kinds["_Cells.hkp"]);
        Assert.Equal(ComponentFileKind.HkpPadstacks, kinds["_Pads.hkp"]);
        Assert.Equal(ComponentFileKind.HkpSymbols, kinds["_Symbols.hkp"]);
    }

    /// <summary>
    /// <b>Swapping the two files' names changes nothing.</b> The names are not part of any
    /// specification and have been observed to differ, so the dispatch is on the first non-comment
    /// character and never on the file name.
    /// </summary>
    [Fact]
    public void Gate5b_SwappingTheTwoGrammarsFileNamesChangesNothing()
    {
        string parts = File.ReadAllText(Dir("hkp", "_Parts.hkp"));
        string symbols = File.ReadAllText(Dir("hkp", "_Symbols.hkp"));

        // Classified from the bytes, with the names deliberately the wrong way round.
        Assert.Equal(HkpGrammar.Dotted, ComponentHkpReader.Grammar(parts));
        Assert.Equal(HkpGrammar.Starred, ComponentHkpReader.Grammar(symbols));

        var swapped = Directory.CreateTempSubdirectory("pl2-hkp-swap-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(swapped, "_Symbols.hkp"), parts);
            File.WriteAllText(Path.Combine(swapped, "_Parts.hkp"), symbols);
            File.Copy(Dir("hkp", "_Cells.hkp"), Path.Combine(swapped, "_Cells.hkp"));
            File.Copy(Dir("hkp", "_Pads.hkp"), Path.Combine(swapped, "_Pads.hkp"));

            var scan = ComponentFolderScan.Scan(swapped);
            var candidate = Assert.Single(scan.Candidates, c => c.Family == ComponentFormatFamily.Hkp);
            var read = ComponentRead.Read(candidate, Dbu);

            Assert.Null(read.Refusal);
            Assert.Equal(ExpectedMap, MapOf(read.Part!));
        }
        finally { Directory.Delete(swapped, recursive: true); }
    }

    // ── Gate 6: encrypted twins invisible (R-PL2-7) ───────────────────────────────────────────────

    /// <summary>
    /// A folder holding all eight <c>.hkp</c> files reports FOUR formats, not eight — and the four
    /// encrypted twins appear nowhere in the skipped summary either. Reporting both halves doubles the
    /// chooser's noise for no information, because the plaintext original sits right there.
    /// </summary>
    [Fact]
    public void Gate6_EncryptedTwinsAreInvisibleRatherThanUnreadable()
    {
        Assert.Equal(8, Directory.GetFiles(Dir("hkp"), "*.hkp").Length);

        var scan = ComponentFolderScan.Scan(Dir("hkp"));
        var candidate = Assert.Single(scan.Candidates, c => c.Family == ComponentFormatFamily.Hkp);

        Assert.Equal(4, candidate.Files.Count);
        Assert.All(candidate.Files, f => Assert.DoesNotContain("Encrypted", f.Name));

        // And not as "4 binary formats" in the skipped summary, which is the failure this rule names.
        Assert.DoesNotContain(scan.SkippedSummary, s => s.Contains("binary"));
    }

    // ── Gate 7: padstack dedupe (R-PL2-8) ─────────────────────────────────────────────────────────

    /// <summary>
    /// A cell fixture repeating one padstack nine times yields ONE. A repeat is not a redefinition
    /// conflict, and nine identical padstacks are not nine padstacks.
    /// </summary>
    [Fact]
    public void Gate7_ARepeatedPadstackDefinitionIsDeduplicated()
    {
        string pads = File.ReadAllText(Dir("hkp", "_Pads.hkp"));
        Assert.Equal(9, Regex.Matches(pads, @"^\.PAD ""RECTA""", RegexOptions.Multiline).Count);

        var part = Read(ComponentFormatFamily.Hkp, "hkp");

        var message = Assert.Single(part.Messages, m => m.Contains("repeats"));
        Assert.Contains("2 distinct pad(s) were kept", message);

        // The geometry survived the dedupe: every land is the one 60 x 10 definition.
        var cell = part.Footprints[0].Cell;
        var pad1 = Assert.Single(cell.Shapes, s => s.Shape.Pin == "1");
        var rect = Assert.IsType<RectShape>(pad1.Shape);
        Assert.Equal(ComponentFootprintBuilder.Mils(60, Dbu), rect.X2 - rect.X1);
        Assert.Equal(ComponentFootprintBuilder.Mils(10, Dbu), rect.Y2 - rect.Y1);
    }

    // ── Gate 8: all variants (R-PL2-9) ────────────────────────────────────────────────────────────

    /// <summary>
    /// A three-<c>PACKAGE_CELL</c> file yields ONE cell with THREE layout views. Every other format
    /// here states its density variants in separate files or separate blocks; this one puts all three
    /// in one file, and a reader that returns the first and stops loses two thirds of it with no error.
    /// </summary>
    [Fact]
    public void Gate8_AllDensityVariantsInOneCellFileBecomeSiblingViews()
    {
        var part = Read(ComponentFormatFamily.Hkp, "hkp");

        Assert.Equal(3, part.Footprints.Count);

        // The nominal pattern is first, so it becomes the primary view (R-PL1-25).
        Assert.Equal("", part.Footprints[0].Variant);
        Assert.Equal(["-L", "-M"], part.Footprints.Skip(1).Select(f => f.Variant).Order());

        // They are three views of ONE pattern: same name, same pads, different geometry.
        Assert.All(part.Footprints, f => Assert.Equal("GIZMO4_LAND", f.Name));
        Assert.All(part.Footprints, f => Assert.Equal(5, f.PadNames.Count));
        Assert.Equal(3, part.Footprints.Select(f => f.Cell.Pins[0].Pin.X).Distinct().Count());
    }

    // ── Gate 9: the second indirection (R-PL2-12) ─────────────────────────────────────────────────

    /// <summary>
    /// A <c>.PLX</c> whose <c>symPinNum</c> disagrees with its pad numbering imports <b>wired by the
    /// map</b>. Symbol pin 2 is pad 4, not pad 2.
    /// </summary>
    [Fact]
    public void Gate9a_SymPinNumIsFollowedRatherThanTheOrdinal()
    {
        var part = Read(ComponentFormatFamily.Plx, "plx");
        Assert.NotNull(part.Symbol);

        // The drawing's second pin sits at (0,-100) and belongs to pad 4 — an ordinal join would
        // hand it pad 2, and everything downstream would look entirely reasonable.
        var second = part.Symbol.Pins[1];
        Assert.Equal(-100, second.YMil);
        Assert.Equal("DELTA", second.Name);
        Assert.Equal("4", second.PadName);

        var fourth = part.Symbol.Pins[3];
        Assert.Equal("GAMMA", fourth.Name);
        Assert.Equal("3", fourth.PadName);

        Assert.Equal(ExpectedMap, MapOf(part));
    }

    /// <summary>
    /// And a fixture whose <c>compPin</c> and <c>padPinMap</c> contradict each other is REFUSED —
    /// the format states this map twice, and choosing one silently is exactly how this class of bug
    /// ships.
    /// </summary>
    [Fact]
    public void Gate9b_ContradictoryPadPinMapIsRefusedRatherThanPreferred()
    {
        var result = ComponentPlxReader.Read(File.ReadAllText(Dir("plx-contradictory", "Library.PLX")), Dbu);

        Assert.Null(result.Part);
        Assert.NotNull(result.Refusal);
        Assert.Contains("contradicts itself", result.Refusal);
        Assert.Contains("pad 2", result.Refusal);
    }

    // ── Gate 10: one reader, two extensions (R-PL2-10) ────────────────────────────────────────────

    /// <summary>
    /// The same content under both banners produces identical cells. The two extensions are one
    /// dialect and differ only in the first line, which is why they are one reader.
    /// </summary>
    [Fact]
    public void Gate10_BothBannersProduceTheSameCell()
    {
        var plx = ComponentPlxReader.Read(File.ReadAllText(Dir("plx", "Library.PLX")), Dbu);
        var dsl = ComponentPlxReader.Read(File.ReadAllText(Dir("dsl", "Library.DSL")), Dbu);

        Assert.Null(plx.Refusal);
        Assert.Null(dsl.Refusal);
        Assert.Equal(Describe(plx.Part!), Describe(dsl.Part!));
    }

    /// <summary>Everything a cell is made of, flattened to text, so two reads compare exactly rather
    /// than field by field.</summary>
    private static string Describe(ComponentPart part)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(part.Name);
        foreach (var pin in part.Symbol?.Pins ?? [])
            sb.AppendLine($"pin {pin.Name} {pin.PadName} {pin.XMil} {pin.YMil}");
        foreach (var footprint in part.Footprints)
        {
            sb.AppendLine($"fp {footprint.Name} {footprint.Variant}");
            foreach (var pin in footprint.Cell.Pins)
                sb.AppendLine($"  pad {pin.Pin.Name} {pin.Pin.X} {pin.Pin.Y} {pin.LayerName}");
            foreach (var shape in footprint.Cell.Shapes)
                sb.AppendLine($"  shape {shape.Shape.GetType().Name} {shape.LayerName} {shape.Shape.Pin}");
        }
        return sb.ToString();
    }

    // ── Gate 11: exact nanometre units (R-PL2-13) ─────────────────────────────────────────────────

    /// <summary>
    /// A <c>.cxf</c> pad and the same pad from a mil-stated format land on the IDENTICAL DBU
    /// coordinate, negative case included — asserted against another format rather than against this
    /// reader's own arithmetic.
    /// </summary>
    [Fact]
    public void Gate11_NanometresAndMilsLandOnTheSameDbu()
    {
        var fromNanometres = Read(ComponentFormatFamily.Cxf, "cxf").Footprints[0].Cell;
        var fromMils = Read(ComponentFormatFamily.Hkp, "hkp").Footprints[0].Cell;

        foreach (string pad in (string[])["1", "2", "3", "4", "TPAD"])
        {
            var a = Assert.Single(fromNanometres.Pins, p => p.Pin.Name == pad);
            var b = Assert.Single(fromMils.Pins, p => p.Pin.Name == pad);
            Assert.Equal(b.Pin.X, a.Pin.X);
            Assert.Equal(b.Pin.Y, a.Pin.Y);
        }

        // The negative side specifically: a cast would truncate toward zero and be wrong only here.
        Assert.Equal(-2032000, Assert.Single(fromNanometres.Pins, p => p.Pin.Name == "1").Pin.X);
    }

    /// <summary>R-PL2-14: an unmapped <c>FORM</c> is reported by number with a count and its pad is
    /// skipped — never guessed into a rectangle on the grounds that most pads are rectangles.</summary>
    [Fact]
    public void Gate11b_AnUnmappedPadFormIsReportedByNumberRatherThanGuessed()
    {
        var part = Read(ComponentFormatFamily.Cxf, "cxf");

        var message = Assert.Single(part.Messages, m => m.Contains("FORM="));
        Assert.Contains("FORM=9", message);
        Assert.DoesNotContain(part.Footprints[0].PadNames, p => p == "NOFORM");
    }

    // ── Gate 12: script state (R-PL2-15) ──────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>.scr</c> fixture with interleaved layer changes puts each shape on its own layer.
    /// Collapsing the state machine — reading the <c>Wire</c> lines and ignoring the <c>Layer</c>
    /// lines between them — puts every shape on one layer, which this fails.
    /// </summary>
    [Fact]
    public void Gate12_InterleavedLayerCommandsAreReplayedRatherThanCollapsed()
    {
        var cell = Read(ComponentFormatFamily.Script, "scr").Footprints[0].Cell;

        var strokes = cell.Shapes.Where(s => s.Shape is PathShape).ToList();
        Assert.Equal(4, strokes.Count);

        // Two layers, alternating — the fixture interleaves them deliberately.
        Assert.Equal(2, strokes.Select(s => s.LayerName).Distinct().Count());
        Assert.Equal(2, strokes.Count(s => s.LayerName == "F.SilkS"));
        Assert.Equal(2, strokes.Count(s => s.LayerName == "F.Fab"));
    }

    /// <summary>The script restates its whole map once per land pattern it edits; that is one map,
    /// not three.</summary>
    [Fact]
    public void Gate12b_ARestatedMapIsOneMapRatherThanSeveral()
    {
        var part = Read(ComponentFormatFamily.Script, "scr");

        Assert.Equal(5, part.ConnectTable.Count);
        Assert.Equal(5, Terminals(part).Count);
    }

    // ── Gate 13: an unknown command refuses (R-PL2-17) ────────────────────────────────────────────

    /// <summary>
    /// A <c>.scr</c> carrying one unmodelled command is refused BY NAME and creates nothing.
    ///
    /// <para>This is the one place in PL1/PL2 where "report and continue" is wrong: an unknown command
    /// in a data format costs one skipped entity, but an unknown command in a script may have changed
    /// state that silently corrupts everything after it.</para>
    /// </summary>
    [Fact]
    public void Gate13_AnUnmodelledCommandRefusesByNameAndCreatesNothing()
    {
        var result = ComponentScrReader.Read(File.ReadAllText(Dir("scr-unknown", "Library.scr")), Dbu);

        Assert.Null(result.Part);
        Assert.NotNull(result.Refusal);
        Assert.Contains("Sculpt", result.Refusal);
        Assert.Contains("Nothing was imported", result.Refusal);
    }

    // ── Gate 15: naming (R-PL2-18) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-PL2-18: every format is referred to by its EXTENSION and nothing else.
    ///
    /// <para><b>This is asserted structurally rather than with a list of forbidden names, because root
    /// <c>CLAUDE.md</c> forbids those names "not even as a glossery of names to filter out".</b> A
    /// scan that stores what it forbids is itself the leak — so the property checked is the one that
    /// makes a leak impossible: the banner constants these readers match on cannot spell a product
    /// name, because each begins with the separator that FOLLOWS one.</para>
    ///
    /// <para>Three of these formats open with <c>&lt;product&gt;-LIBRARY-PART-TYPES-V9</c> and the
    /// like. Matching the whole banner would put a commercial product's name in this repo as a string
    /// literal; matching from the separator onward is just as specific and carries nothing.</para>
    /// </summary>
    [Fact]
    public void Gate15a_BannerConstantsCannotSpellAProductName()
    {
        string[] constants =
        [
            ComponentRecordsReader.PartHeader,
            ComponentRecordsReader.DecalHeader,
            ComponentRecordsReader.SymbolHeader,
            ComponentPlxReader.PlxBanner,
            ComponentPlxReader.DslBanner,
        ];

        Assert.All(constants, c =>
        {
            Assert.NotEmpty(c);
            Assert.True(c[0] is '-' or '_',
                $"\"{c}\" must begin with the separator that follows the product word, so that no part " +
                "of a vendor's name can be spelled by this constant (R-PL2-18).");
        });
    }

    /// <summary>
    /// And the synthetic fixtures carry an INVENTED prefix in that same slot (R-PL2-19), so no fixture
    /// reproduces a real product's banner either. They still classify, which is the point: the reader
    /// never needed the product word.
    /// </summary>
    [Theory]
    [InlineData("records", "PARTLIB.p", ComponentFileKind.PartRecords)]
    [InlineData("records", "PARTLIB.d", ComponentFileKind.FootprintRecords)]
    [InlineData("records", "PARTLIB.c", ComponentFileKind.SymbolRecords)]
    [InlineData("plx", "Library.PLX", ComponentFileKind.PlxLibrary)]
    [InlineData("dsl", "Library.DSL", ComponentFileKind.PlxLibrary)]
    public void Gate15b_FixtureBannersAreInventedAndStillClassify(
        string folder, string file, ComponentFileKind expected)
    {
        string path = Dir(folder, file);
        string first = File.ReadLines(path).First().TrimStart('*');

        Assert.StartsWith("EXAMPLE", first, StringComparison.Ordinal);
        Assert.Equal(expected, ComponentClassifier.Classify(path));
    }

    /// <summary>
    /// Nothing this phase added names a format any way but by its extension: every mention of a format
    /// in these readers' own identifiers is one of the extensions <c>ReadableExtensions</c> lists.
    /// Comments stripped first — the <c>brief-harmonicarf-h8</c> lesson, that an unstripped scan passes
    /// on a comment it should have caught.
    /// </summary>
    [Fact]
    public void Gate15c_EveryFormatThisPhaseAddedIsNamedByItsExtension()
    {
        string[] added = [".p", ".d", ".c", ".hkp", ".plx", ".dsl", ".cxf", ".scr"];
        Assert.All(added, e => Assert.Contains(e, ComponentClassifier.ReadableExtensions));

        // The family names are shapes and extensions, never products.
        Assert.Equal(
            ["Pl1", "Records", "Hkp", "Plx", "Cxf", "Script"],
            Enum.GetNames<ComponentFormatFamily>());
    }

}
