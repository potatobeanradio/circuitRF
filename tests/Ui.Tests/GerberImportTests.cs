// Gate for docs/sonnet-briefs/brief-L4g-gerber-import-orchestration.md §7.
//
// Every fixture here is HAND-AUTHORED, following L4e's and L4f's precedent (their §11 / §6): a
// hand-authored file set is worth less as a dialect test than a real one, but it costs nothing to
// redistribute and names no vendor, tool or product. What that leaves untested is recorded in
// src/Ui/RESOLVED.md's completion note.
//
// Gate 18: COUNTERS ONLY. There is no wall-clock assertion anywhere in this file.

using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public class GerberImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-import-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // -- Fixtures ---------------------------------------------------------------------------------

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    /// <summary>One artwork file: a single circular flash at (x, y) mm, optionally declaring its own
    /// file function.</summary>
    private static string Artwork(string? fileFunction = null, double xMm = 1.0, double yMm = 1.0)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        long x = (long)Math.Round(xMm * 1_000_000);
        long y = (long)Math.Round(yMm * 1_000_000);
        return MmHeader + attribute + "%ADD10C,0.400*%\nD10*\n" + $"X{x}Y{y}D03*\n" + "M02*\n";
    }

    /// <summary>A drill file with one 0.3 mm hit, at the same place as <see cref="Artwork"/>'s flash so
    /// the two pair into a via.</summary>
    private static string Drill(double xMm = 1.0, double yMm = 1.0) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" +
        $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string fileName, string content)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];

    private static GerberImport.ImportResult Import(
        string sourceDir, string parentDir, string name, Technology? destTech = null,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? dialog = null,
        GerberImport.ResolveDrillFormat? drillFormat = null) =>
        GerberImport.Import(FilesIn(sourceDir), parentDir, name, destTech, 1000, dialog, drillFormat);

    private static LayoutView LoadCell(GerberImport.ImportResult result)
    {
        string layoutDir = CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout);
        return LayoutPersistence.LoadFromFile(Directory.EnumerateFiles(layoutDir, "*.clay").Single());
    }

    // -- Gate 2: classification is by CONTENT, extension second -----------------------------------

    [Fact]
    public void AFolderOfMixedFiles_ImportsOnlyTheArtworkAndTheDrillData_AndClassifiesByContent()
    {
        var dir = Folder("mixed");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", Drill());
        // A human-readable drill LISTING - it shares its stem with the drill file and names the same
        // tools, which is exactly the file an extension-first classifier takes for drill data.
        Write(dir, "board-drill.txt", "Tool  Size     Count\n----  -------  -----\nT1    0.30 mm      1\nTotal          1\n");
        Write(dir, "board-pos.csv", "Ref,Val,Package,PosX,PosY,Rot,Side\nR1,10k,0402,1.0,1.0,0,top\n");
        Write(dir, "board.pdf", "%PDF-1.4\n binary\n");

        var result = Import(dir, _root, "mixed_import");

        Assert.False(result.Cancelled);
        Assert.Equal(["board.gtl"], result.Layers.Select(l => l.FileName));
        Assert.Equal(
            ["board-drill.txt", "board-pos.csv", "board.pdf"],
            result.SkippedFiles.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void RenamingAFileToAMisleadingExtension_DoesNotChangeWhatItIs()
    {
        var dir = Folder("misleading");
        // Artwork wearing a drill file's extension, and drill data wearing artwork's.
        Write(dir, "board.drl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gtl", Drill());

        var result = Import(dir, _root, "misleading_import");

        Assert.False(result.Cancelled);
        Assert.Equal(["board.drl"], result.Layers.Select(l => l.FileName));

        // The drill data was read as drill data: it paired with the artwork's flash into a via.
        Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
    }

    // -- Gate 3: every skipped file is named once -------------------------------------------------

    [Fact]
    public void EverySkippedFileIsNamedExactlyOnceInTheSummary()
    {
        var dir = Folder("skips");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "notes.md", "This folder holds the board artwork.\n");
        Write(dir, "bom.csv", "Ref,Value\nR1,10k\n");

        var result = Import(dir, _root, "skips_import");

        foreach (string skipped in new[] { "notes.md", "bom.csv" })
        {
            int mentions = result.Messages.Count(m => m.StartsWith($"Skipped {skipped} ", StringComparison.Ordinal));
            Assert.Equal(1, mentions);
        }
    }

    // -- Gate 4: sibling drill data is OFFERED, never imported silently ---------------------------

    [Fact]
    public void DrillDataInASiblingFolder_IsOfferedAsACandidate_AndImportingWithoutItProducesNoVias()
    {
        var artworkDir = Folder(Path.Combine("board-out", "gerber"));
        var drillDir = Folder(Path.Combine("board-out", "drill"));
        Write(artworkDir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(drillDir, "board.drl", Drill());

        var result = Import(artworkDir, _root, "sibling_import");

        Assert.False(result.Cancelled);
        Assert.Equal(["board.drl"], result.DrillCandidates.Select(Path.GetFileName));
        Assert.Empty(LoadCell(result).Shapes.OfType<ViaShape>());
        Assert.Contains(result.Messages, m => m.Contains("NOT imported", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptingTheOfferedSiblingDrillFile_RebuildsTheVia()
    {
        var artworkDir = Folder(Path.Combine("board-out2", "gerber"));
        var drillDir = Folder(Path.Combine("board-out2", "drill"));
        Write(artworkDir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(drillDir, "board.drl", Drill());

        var offered = Import(artworkDir, _root, "offer_only");
        var accepted = GerberImport.Import(
            [.. FilesIn(artworkDir), .. offered.DrillCandidates], _root, "accepted_import", null, 1000);

        Assert.Single(LoadCell(accepted).Shapes.OfType<ViaShape>());
    }

    // -- Gate 5: an artwork-only set imports, and says no vias were reconstructed ------------------

    [Fact]
    public void AnArtworkOnlySet_ImportsCleanly_AndSaysNoViasWereReconstructed()
    {
        var dir = Folder("artwork-only");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));

        var result = Import(dir, _root, "artwork_only_import");

        Assert.False(result.Cancelled);
        Assert.Single(LoadCell(result).Shapes);
        Assert.Contains(result.Messages, m =>
            m.Contains("No drill data was read", StringComparison.Ordinal) &&
            m.Contains("no vias were reconstructed", StringComparison.Ordinal));
    }

    // -- Gate 6: rung 1 - %TF.FileFunction, no heuristic and no dialog -----------------------------

    [Fact]
    public void ASetCarryingFileFunction_IsIdentifiedWithNoHeuristicAndNoDialog()
    {
        var dir = Folder("x2");
        // Names deliberately say NOTHING - only the attribute can identify these.
        Write(dir, "a.gbr", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "b.gbr", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "c.gbr", Artwork("Soldermask,Top", xMm: 3.0));
        Write(dir, "d.gbr", Artwork("Legend,Top", xMm: 4.0));

        bool dialogShown = false;
        var result = Import(dir, _root, "x2_import", dialog: _ => { dialogShown = true; return null; });

        Assert.False(dialogShown);
        Assert.False(result.Cancelled);
        Assert.All(result.Layers, l => Assert.Equal(GerberLayerRung.FileFunction, l.Rung));
        Assert.All(result.Layers, l => Assert.False(l.IdentityGuessed));
        Assert.Equal(
            ["Bottom Copper", "Silk Top", "Soldermask Top", "Top Copper"],
            result.Layers.Select(l => l.LayerName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // -- Gate 7: rung 2 - a real L4c export re-identifies exactly against its own technology -------

    [Fact]
    public void ASetExportedByL4c_ReImportsEveryLayerExactly_ByItsTechnologysGerberSuffix_WithNoDialog()
    {
        var tech = new Technology
        {
            Name = "Two Layer",
            Layers =
            [
                new LayerDef { Key = new LayerKey(1, 0), Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E),
                    Interchange = new InterchangeMapping(null, null, null, "GTL", null) },
                new LayerDef { Key = new LayerKey(2, 0), Name = "Bottom Copper", Color = new Rgba(0x3E, 0x7A, 0xC8),
                    Interchange = new InterchangeMapping(null, null, null, "GBL", null) },
            ],
        };

        var cellDir = CellFolder.CreateCellFolder(_root, "SRC");
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 500_000, Y2 = 500_000 });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = 300_000, Y2 = 300_000 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "SRC.clay"), view);
        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        ccell.PrimaryLayout = "SRC.clay";
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell);

        var reloaded = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, "SRC.clay"));
        var plan = GerberExport.Analyze(cellDir, tech, 1000, reloaded, null);
        Assert.True(plan.CanWrite);
        string exportDir = Folder("exported");
        GerberExport.Write(exportDir, "SRC", plan);

        bool dialogShown = false;
        var result = Import(exportDir, _root, "reimport", tech, dialog: _ => { dialogShown = true; return null; });

        Assert.False(dialogShown);
        Assert.False(result.Cancelled);
        // The export writes a .gbrjob but no %TF.FileFunction (the technology declares none), so the
        // GerberSuffix aliases are the rung that has to carry this - which is exactly the loop
        // R-L4g-5 rung 2 exists to close.
        Assert.All(result.Layers, l => Assert.Equal(GerberLayerRung.TechnologySuffix, l.Rung));
        Assert.Equal(
            ["Bottom Copper", "Top Copper"],
            result.Layers.Select(l => l.LayerName).OrderBy(n => n, StringComparer.Ordinal));
    }

    // -- Gate 8: a heuristic identification is reported AS A GUESS, by name ------------------------

    [Fact]
    public void AHeuristicIdentification_IsReportedAsAGuess_ByName()
    {
        var dir = Folder("heuristic");
        Write(dir, "board-top-copper.gbr", Artwork());
        Write(dir, "board-bottom-copper.gbr", Artwork(xMm: 2.0));

        var result = Import(dir, _root, "heuristic_import");

        Assert.All(result.Layers, l => Assert.Equal(GerberLayerRung.Heuristic, l.Rung));
        Assert.All(result.Layers, l => Assert.True(l.IdentityGuessed));
        Assert.Contains(result.Messages, m =>
            m.StartsWith("GUESSED from the file name", StringComparison.Ordinal) &&
            m.Contains("board-top-copper.gbr", StringComparison.Ordinal) &&
            m.Contains("Top Copper", StringComparison.Ordinal) &&
            m.Contains("board-bottom-copper.gbr", StringComparison.Ordinal) &&
            m.Contains("Bottom Copper", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeuristicTableIsGeneric_AndReadsTheConventionalExtensionFamilyStructurally()
    {
        var dir = Folder("extfamily");
        Write(dir, "board.gtl", Artwork());
        Write(dir, "board.gbs", Artwork(xMm: 2.0));
        Write(dir, "board.gto", Artwork(xMm: 3.0));

        var result = Import(dir, _root, "extfamily_import");

        Assert.Equal(
            ["Silk Top", "Soldermask Bottom", "Top Copper"],
            result.Layers.Select(l => l.LayerName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(result.Layers, l => Assert.True(l.IdentityGuessed));
    }

    /// <summary>
    /// A six-layer set that names its outer copper "Top Layer"/"Bottom Layer" and numbers the ones
    /// between them "Layer 2".."Layer 5". Without the numbered-mid-layer row this imported as TWO
    /// copper layers and four unidentified drawing layers — and only conductors reach the stackup and
    /// the copper order, so two thirds of the board quietly left the part of the import the EM path
    /// reads. "Layer 2" counts the whole stack from the top, so it is the FIRST inner layer.
    /// </summary>
    [Fact]
    public void ANumberedMidLayer_IsReadAsInnerCopper_AndOrderedByItsNumber()
    {
        var dir = Folder("numbered-layers");
        Write(dir, "artwork_top_layer.art", Artwork());
        Write(dir, "artwork_layer_2.art", Artwork(xMm: 2.0));
        Write(dir, "artwork_layer_3.art", Artwork(xMm: 3.0));
        Write(dir, "artwork_bottom_layer.art", Artwork(xMm: 4.0));

        var result = Import(dir, _root, "numbered_layers_import");

        Assert.Equal(
            ["Bottom Copper", "Inner 1", "Inner 2", "Top Copper"],
            result.Layers.Select(l => l.LayerName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(result.Messages, m =>
            m.Contains("top to bottom, is: Top Copper, Inner 1, Inner 2, Bottom Copper",
                       StringComparison.Ordinal));
    }

    /// <summary>The other half of that row: it matches only when a NUMBER follows the word, so a name
    /// that merely contains "layer" is not silently promoted to copper.</summary>
    [Fact]
    public void AnUnnumberedLayerInAName_IsNotReadAsCopper()
    {
        var dir = Folder("unnumbered-layer");
        Write(dir, "board_mask_layer.art", Artwork());

        var result = Import(dir, _root, "unnumbered_layer_import");

        Assert.DoesNotContain(result.Messages, m =>
            m.Contains("copper layer(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pour painted with %LPC composites the whole layer, which unions every pad into the copper
    /// around it — so there is no discrete flash left for a drill hit to pair with, however exactly
    /// the two files agree. Telling that user their drill file belongs to a different board is not a
    /// hedge, it is wrong, and it is the common case on any real board with a ground pour.
    /// </summary>
    [Fact]
    public void WhenCopperWasComposited_TheUnpairedHolesAreExplainedByThat_NotByAMismatchedDrillFile()
    {
        var dir = Folder("composited-copper");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\nG01*\n" +
            "%LPD*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\n" +
            "%LPC*%\nG36*\nX2000000Y2000000D02*\nX2000000Y3000000D01*\nX3000000Y3000000D01*\n" +
            "X3000000Y2000000D01*\nX2000000Y2000000D01*\nG37*\n%LPD*%\nM02*\n");
        Write(dir, "board.drl", Drill(xMm: 5.0, yMm: 5.0));

        var result = Import(dir, _root, "composited_copper_import");

        Assert.Contains(result.Messages, m =>
            m.Contains("had to be composited", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Messages, m =>
            m.Contains("do not belong to the same board", StringComparison.Ordinal));
    }

    // ── Vias carved back out of a composited pour ────────────────────────────────────────────────

    /// <summary>A pour with a via pad in it and a clearance elsewhere, so the layer composites: 10 mm
    /// of copper, a 0.8 mm pad flashed at (5,5), and a clear square well away from both.</summary>
    private static string PourWithPad(string fileFunction, double padXMm = 5.0, double padYMm = 5.0) =>
        MmHeader + $"%TF.FileFunction,{fileFunction}*%\nG01*\n" +
        "%LPD*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\n" +
        "%LPC*%\nG36*\nX8000000Y8000000D02*\nX8000000Y9000000D01*\nX9000000Y9000000D01*\n" +
        "X9000000Y8000000D01*\nX8000000Y8000000D01*\nG37*\n" +
        $"%LPD*%\n%ADD10C,0.800*%\nD10*\nX{(long)Math.Round(padXMm * 1_000_000)}Y{(long)Math.Round(padYMm * 1_000_000)}D03*\n" +
        "M02*\n";

    /// <summary>
    /// THE PAYOFF: a pad that compositing merged into the pour still pairs with its hole, so a real
    /// board's vias are rebuilt instead of every hole coming back as a bare circle. Before this, a
    /// board with a ground pour reconstructed ZERO vias — and <c>ViaShape</c> on a via-bound layer is
    /// what the planar extractor reads as a via, so the board simulated with none in it.
    /// </summary>
    [Fact]
    public void APadCompositedIntoAPour_StillPairsWithItsHole_AndBecomesAVia()
    {
        var dir = Folder("carve-via");
        Write(dir, "board.gtl", PourWithPad("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", Drill(xMm: 5.0, yMm: 5.0));

        var result = Import(dir, _root, "carve_via_import");

        var via = Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
        Assert.Equal(5_000_000, via.X);
        Assert.Equal(5_000_000, via.Y);
        Assert.Equal(800_000, via.PadSize);      // the pad's REAL size, recovered from the flash
        Assert.Equal(300_000, via.DrillSize);
        Assert.Contains(result.Messages, m => m.Contains("cut back out of the pour", StringComparison.Ordinal));
    }

    /// <summary>
    /// AND THE COPPER IS UNCHANGED. That is what makes the carve safe rather than a re-drawing of the
    /// board: the pad was verified to lie wholly inside the pour before it was claimed, so cutting the
    /// disc out and letting the via put the same disc back cancel exactly. Measured as AREA, against
    /// the identical artwork imported with no drill file at all.
    /// </summary>
    [Fact]
    public void CarvingAViaOutOfAPour_LeavesTheLayersCopperUnchanged()
    {
        var withDrill = Folder("carve-area-drill");
        Write(withDrill, "board.gtl", PourWithPad("Copper,L1,Top,Signal"));
        Write(withDrill, "board.drl", Drill(xMm: 5.0, yMm: 5.0));

        var noDrill = Folder("carve-area-plain");
        Write(noDrill, "board.gtl", PourWithPad("Copper,L1,Top,Signal"));

        double before = CopperArea(LoadCell(Import(noDrill, _root, "carve_area_plain_import")));
        var after = LoadCell(Import(withDrill, _root, "carve_area_drill_import"));

        double carvedCopper = CopperArea(after);
        double padArea = after.Shapes.OfType<ViaShape>()
            .Sum(v => Math.PI * (v.PadSize / 2.0) * (v.PadSize / 2.0));

        // The pad disc is FLATTENED to a polygon when it is cut out, so the two differ by the
        // inscribed-polygon deficit of one 0.8 mm circle and nothing else.
        Assert.True(carvedCopper < before, "the pour did not lose the pad it gave to the via");
        Assert.Equal(before, carvedCopper + padArea, before * 1e-3);
    }

    /// <summary>A SOLDER MASK OPENING IS NOT A VIA PAD. The candidate ranking's last resort was "a
    /// flash on any layer at all", which on a real board took the mask clearance around each mounting
    /// hole and turned six 4.6 mm openings into 4.6 mm copper pads — sitting on a pour that has a
    /// deliberate hole exactly there.</summary>
    [Fact]
    public void AMaskOpeningAtAHole_IsNotTakenAsTheViaPad()
    {
        var dir = Folder("mask-not-pad");
        // Copper nowhere near the hole; the only flash at (5,5) is the mask opening.
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal", xMm: 1.0, yMm: 1.0));
        Write(dir, "board.gts", Artwork("Soldermask,Top", xMm: 5.0, yMm: 5.0));
        Write(dir, "board.drl", Drill(xMm: 5.0, yMm: 5.0));

        var cell = LoadCell(Import(dir, _root, "mask_not_pad_import"));

        Assert.Empty(cell.Shapes.OfType<ViaShape>());
        Assert.Single(cell.Shapes.OfType<CircleShape>().Where(c => c.Cx == 5_000_000 && c.R == 150_000));
    }

    /// <summary>Total filled area of every conductor-ish shape in the cell, vias excluded — the vias
    /// are added back separately by the caller, which is the whole point of the comparison.</summary>
    private static double CopperArea(LayoutView view)
    {
        double total = 0;
        foreach (var shape in view.Shapes)
            switch (shape)
            {
                case ViaShape: break;
                case CircleShape c: total += Math.PI * c.R * (double)c.R; break;
                case RectShape r: total += Math.Abs((r.X2 - r.X1) * (double)(r.Y2 - r.Y1)); break;
                case PolygonShape p:
                    total += RingArea(p.Xy);
                    foreach (var hole in p.Holes ?? []) total -= RingArea(hole);
                    break;
            }
        return total;
    }

    private static double RingArea(long[] xy)
    {
        double a = 0;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            int j = (i + 2) % xy.Length;
            a += (double)xy[i] * xy[j + 1] - (double)xy[j] * xy[i + 1];
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>A drill DRAWING is a dimensioned fabrication sheet whose tool legend sits beside the
    /// board, not drill data — putting it on the layer the hits go to dragged that layer's extent far
    /// outside the board outline.</summary>
    [Fact]
    public void ADrillDrawing_DoesNotLandOnTheDrillLayer()
    {
        var dir = Folder("drill-drawing");
        Write(dir, "artwork_drill_drawing.art", Artwork());

        var result = Import(dir, _root, "drill_drawing_import");

        Assert.Equal("Drill Map", Assert.Single(result.Layers).LayerName);
    }

    /// <summary>
    /// The measurement R-L4g's completion note asks for, pinned as a test: on one full eleven-file
    /// board the generic rung-3 table names <b>9 of 11</b> layers exactly as the declared attributes
    /// do, and the two it does not are the inner copper layers — where it gets the SIDE and the ORDER
    /// right and only the index label differs.
    ///
    /// <para>That gap is a property of the format, not of the table. Two conventions for the
    /// <c>g&lt;n&gt;</c> extension are in circulation and they disagree by one: some sets number the
    /// whole copper stack (so <c>.g2</c> is the first inner layer, which is what this fixture is
    /// written in) and some number only the mid layers (so <c>.g1</c> is). A real four-layer board
    /// carrying <c>.gtl</c>, <c>.g1</c>, <c>.g2</c>, <c>.gbl</c> is the counter-example to the first
    /// reading, and reading <c>.g1</c> as top copper there produced two layers both called "Top
    /// Copper" and a copper order of top/top/inner/bottom. So the table reads every <c>g&lt;n&gt;</c>
    /// as inner copper, and the index is the one thing it does not claim to know — reported as a guess
    /// like every rung-3 answer, and settled exactly by an attribute or a job file.</para>
    /// </summary>
    [Fact]
    public void TheGenericHeuristic_NamesTheSameLayersAsTheDeclaredAttributes_ExceptTheInnerCopperIndex()
    {
        (string Name, string Function)[] set =
        [
            ("board.gtl", "Copper,L1,Top,Signal"),
            ("board.g2",  "Copper,L2,Inr,Plane"),
            ("board.g3",  "Copper,L3,Inr,Plane"),
            ("board.gbl", "Copper,L4,Bot,Signal"),
            ("board.gts", "Soldermask,Top"),
            ("board.gbs", "Soldermask,Bot"),
            ("board.gto", "Legend,Top"),
            ("board.gbo", "Legend,Bot"),
            ("board.gtp", "Paste,Top"),
            ("board.gbp", "Paste,Bot"),
            ("board.gko", "Profile,NP"),
        ];

        var declaredDir = Folder("cascade-declared");
        var strippedDir = Folder("cascade-stripped");
        for (int i = 0; i < set.Length; i++)
        {
            Write(declaredDir, set[i].Name, Artwork(set[i].Function, xMm: 1.0 + i));
            Write(strippedDir, set[i].Name, Artwork(null, xMm: 1.0 + i));
        }

        var declared = Import(declaredDir, _root, "cascade_declared_import");
        var stripped = Import(strippedDir, _root, "cascade_stripped_import");

        Assert.All(declared.Layers, l => Assert.Equal(GerberLayerRung.FileFunction, l.Rung));
        Assert.All(stripped.Layers, l => Assert.Equal(GerberLayerRung.Heuristic, l.Rung));

        var byFile = declared.Layers.ToDictionary(l => l.FileName, l => l.LayerName);
        var disagreed = stripped.Layers
            .Where(g => byFile[g.FileName] != g.LayerName)
            .ToDictionary(g => g.FileName, g => g.LayerName);

        // 9 of 11 exactly, and the two that differ are named: both inner copper, both still inner
        // copper, both still in the file's own order relative to each other.
        Assert.Equal(
            new Dictionary<string, string> { ["board.g2"] = "Inner 2", ["board.g3"] = "Inner 3" },
            disagreed);
        Assert.Equal(["Inner 1", "Inner 2"], new[] { byFile["board.g2"], byFile["board.g3"] });

        // The copper stack still comes out top, inner, inner, bottom — the part a wrong answer here
        // would break (R-L4g-10), and the part the index label does not touch.
        Assert.Contains(stripped.Messages, m =>
            m.Contains("Top Copper, Inner 2, Inner 3, Bottom Copper", StringComparison.Ordinal));
    }

    [Fact]
    public void ADotG1_IsNeverTopCopper_BecauseTheSetThatHasOneAlsoHasADotGtl()
    {
        // The `g<n>` extension is ambiguous by one between two live conventions, and this is the
        // reading that decides it: a real four-layer set spells its outer layers `.gtl`/`.gbl` and its
        // inner ones `.g1`/`.g2`. Read `.g1` as top copper and the set has TWO layers called "Top
        // Copper", both ranked "Top", and a copper order of top/top/inner/bottom — a silently wrong
        // stack, which is exactly what R-L4g-10 exists to prevent.
        var dir = Folder("g1-inner");
        Write(dir, "board.gtl", Artwork(xMm: 1.0));
        Write(dir, "board.g1", Artwork(xMm: 2.0));
        Write(dir, "board.g2", Artwork(xMm: 3.0));
        Write(dir, "board.gbl", Artwork(xMm: 4.0));

        var result = Import(dir, _root, "g1_inner_import");

        Assert.Equal(
            ["Top Copper", "Inner 1", "Inner 2", "Bottom Copper"],
            new[] { "board.gtl", "board.g1", "board.g2", "board.gbl" }
                .Select(f => result.Layers.Single(l => l.FileName == f).LayerName));

        Assert.Distinct(result.Layers.Select(l => l.LayerName));
        Assert.Contains(result.Messages, m =>
            m.Contains("Top Copper, Inner 1, Inner 2, Bottom Copper", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGenericTable_ReadsBothSpellingsOfASide_NotOnlyTheLongOne()
    {
        // "Bot" is the format's OWN abbreviation — it is what a FileFunction says — so a set naming
        // its files that way is spelling the side generically, not privately. The table had "bottom",
        // "back", "top" and "front" but not "bot", so such a set had its top layers guessed and its
        // bottom layers dropped to the mapping dialog: an asymmetry nothing announced.
        var dir = Folder("bot-spelling");
        Write(dir, "top_silk.gbr", Artwork(xMm: 1.0));
        Write(dir, "bot_silk.gbr", Artwork(xMm: 2.0));
        Write(dir, "top_mask.gbr", Artwork(xMm: 3.0));
        Write(dir, "bot_mask.gbr", Artwork(xMm: 4.0));

        var result = Import(dir, _root, "bot_spelling_import");

        Assert.Equal(
            ["Silk Top", "Silk Bottom", "Soldermask Top", "Soldermask Bottom"],
            new[] { "top_silk.gbr", "bot_silk.gbr", "top_mask.gbr", "bot_mask.gbr" }
                .Select(f => result.Layers.Single(l => l.FileName == f).LayerName));
    }

    [Fact]
    public void AGuessedZeroSuppression_IsSettledAgainstTheArtworksOwnExtent()
    {
        // R-L4f-1's evidence source 5, used as EVIDENCE rather than only printed: it is the strongest
        // source on the ladder, and it is free here because this is the only place holding both
        // readers' output. The drill file below declares no units, no format and no LZ/TZ word; its
        // tool table settles the unit and nothing settles the suppression. The coordinate words are
        // SHORT, which is where the two conventions part company: at 2:4, "X1Y05" is 0.0001 x 0.0005
        // inch with the leading zeros omitted and 10.0000 x 5.0000 inch with the trailing ones. Only
        // the second lands anywhere near the board.
        var dir = Folder("settled-by-artwork");
        Write(dir, "board.gtl", MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\n" +
            "%ADD10C,0.400*%\nD10*\nX254000000Y127000000D03*\nM02*\n");   // 10.0 x 5.0 inch, in mm
        Write(dir, "board.drl", "T1C0.0135\nX1Y05\nM30\n");

        var result = Import(dir, _root, "settled_by_artwork_import");

        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m =>
            m.Contains("settled against the artwork instead", StringComparison.Ordinal) &&
            m.Contains("trailing zeros suppressed", StringComparison.Ordinal));

        // ...and the hole landed on the pad, so the two rejoined into a via.
        var via = Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
        Assert.Equal(254_000_000, via.X);
        Assert.Equal(127_000_000, via.Y);
    }

    // -- Gate 9: the source extension is recorded as GerberSuffix ----------------------------------

    [Fact]
    public void EachImportedLayerCarriesItsSourceExtensionAsItsGerberSuffix()
    {
        var dir = Folder("suffix");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));

        var result = Import(dir, _root, "suffix_import");
        var tech = TechPersistence.LoadFromFile(result.TechPath!);

        // Without this, a re-export names its files from a synthetic fallback suffix instead of the
        // names the import read (GerberExport.Write's own "G{layer}_{datatype}" fallback), and L4h's
        // byte-identity gate cannot pass - the two file sets stop being comparable at all.
        Assert.Equal(
            ["gbl", "gtl"],
            tech.Layers.Select(l => l.Interchange?.GerberSuffix ?? "").OrderBy(s => s, StringComparer.Ordinal));
    }

    // -- Gate 10: a NEW technology, and the workspace's own is untouched ---------------------------

    [Fact]
    public void TheImportWritesItsOwnCtechInTheImportFolder_AndLeavesTheWorkspaceTechnologyUnmodified()
    {
        var destTech = new Technology
        {
            Name = "Workspace",
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Top Copper", Color = new Rgba(1, 2, 3) }],
        };
        int layersBefore = destTech.Layers.Count;
        string nameBefore = destTech.Layers[0].Name;
        var colorBefore = destTech.Layers[0].Color;
        var interchangeBefore = destTech.Layers[0].Interchange;
        int stackupBefore = destTech.Stackup.Layers.Count;

        var dir = Folder("owntech");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbs", Artwork("Soldermask,Bot", xMm: 2.0));

        var result = Import(dir, _root, "owntech_import", destTech);

        Assert.NotNull(result.TechPath);
        Assert.Equal(result.ImportDir, Path.GetDirectoryName(result.TechPath));
        Assert.True(File.Exists(result.TechPath));

        // The .clay resolves against it, by its own relative TechRef.
        var view = LoadCell(result);
        Assert.NotNull(view.TechRef);
        string layoutDir = CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout);
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(layoutDir, view.TechRef!))));

        // The divergence from board import, asserted directly.
        Assert.Equal(layersBefore, destTech.Layers.Count);
        Assert.Equal(nameBefore, destTech.Layers[0].Name);
        Assert.Equal(colorBefore, destTech.Layers[0].Color);
        Assert.Equal(interchangeBefore, destTech.Layers[0].Interchange);
        Assert.Equal(stackupBefore, destTech.Stackup.Layers.Count);
    }

    // -- Gate 11: a stackup from a job file, and nothing inferred from a material name -------------

    private const string JobFileWithStackup = """
        {
          "Header": { "GenerationSoftware": { "Vendor": "test", "Application": "test", "Version": "1" } },
          "GeneralSpecs": { "LayerNumber": 2, "BoardThickness": 1.6 },
          "FilesAttributes": [
            { "Path": "board.gtl", "FileFunction": "Copper,L1,Top,Signal", "FilePolarity": "Positive" },
            { "Path": "board.gbl", "FileFunction": "Copper,L2,Bot,Signal", "FilePolarity": "Positive" }
          ],
          "MaterialStackup": [
            { "Type": "Legend",     "Notes": "top legend" },
            { "Type": "SolderMask", "Thickness": 0.01 },
            { "Type": "Copper",     "Name": "Top Copper",    "Thickness": 0.035 },
            { "Type": "Dielectric", "Name": "Core", "Material": "A-LAMINATE-TRADE-NAME",
              "Thickness": 1.5, "DielectricConstant": 4.4, "LossTangent": 0.02 },
            { "Type": "Copper",     "Name": "Bottom Copper", "Thickness": 0.035 },
            { "Type": "SolderMask", "Thickness": 0.01 }
          ]
        }
        """;

    [Fact]
    public void AJobFilesMaterialStackup_BecomesConductorsAndDielectrics_InTheFilesOwnOrder()
    {
        var dir = Folder("jobstack");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.gbrjob", JobFileWithStackup);

        var result = Import(dir, _root, "jobstack_import");
        var stackup = TechPersistence.LoadFromFile(result.TechPath!).Stackup;
        var electrical = stackup.Layers.Where(l => l.Kind != StackupKind.Via).ToList();

        Assert.Equal(
            [StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor],
            electrical.Select(l => l.Kind));
        Assert.Equal(35_000, electrical[0].ThicknessDbu);       // 0.035 mm at 1000 DBU/um
        Assert.Equal(1_500_000, electrical[1].ThicknessDbu);    // 1.5 mm
        Assert.Equal(4.4, electrical[1].Epsr);
        Assert.Equal(0.02, electrical[1].TanD);

        // The conductors link to the copper artwork the cascade resolved, top to bottom.
        Assert.Single(electrical[0].DrawingLayers);
        Assert.Single(electrical[2].DrawingLayers);
        Assert.NotEqual(electrical[0].DrawingLayers[0], electrical[2].DrawingLayers[0]);

        // Defaults, NAMED as defaults - and nothing inferred from the material's trade name.
        Assert.Equal(PcbStackupMapping.DefaultCopperConductivitySm, electrical[0].SigmaSm);
        Assert.Equal(1.0, electrical[1].Mur);
        Assert.Contains(result.Messages, m =>
            m.Contains("so defaulted: conductor conductivity", StringComparison.Ordinal) &&
            m.Contains("relative permeability", StringComparison.Ordinal) &&
            m.Contains("neither was inferred from the file's material names", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Messages, m => m.Contains("A-LAMINATE-TRADE-NAME", StringComparison.Ordinal));
    }

    [Fact]
    public void AJobFileThatOmitsPermittivityAndLossTangent_LeavesThemUnset_AndSaysWhichAreMissing()
    {
        var dir = Folder("jobstack-bare");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbrjob", """
            {
              "FilesAttributes": [ { "Path": "board.gtl", "FileFunction": "Copper,L1,Top,Signal" } ],
              "MaterialStackup": [
                { "Type": "Copper", "Thickness": 0.035 },
                { "Type": "Dielectric", "Thickness": 1.5 }
              ]
            }
            """);

        var result = Import(dir, _root, "jobstack_bare_import");
        var dielectric = TechPersistence.LoadFromFile(result.TechPath!).Stackup.Layers
            .Single(l => l.Kind == StackupKind.Dielectric);

        // Unset is StackupLayer's own default - vacuum - and the message is what stops it reading as a
        // measurement.
        Assert.Equal(1.0, dielectric.Epsr);
        Assert.Equal(0.0, dielectric.TanD);
        Assert.Contains(result.Messages, m =>
            m.Contains("relative permittivity (1 dielectric(s) left unset)", StringComparison.Ordinal) &&
            m.Contains("loss tangent (1 dielectric(s) left unset)", StringComparison.Ordinal));
    }

    // -- Gate 12: no job file, no stackup, and NO fabricated substrate -----------------------------

    [Fact]
    public void ASetWithNoJobFile_YieldsNoSubstrate_AndOneMessageSaysSo()
    {
        var dir = Folder("nojob");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));

        var result = Import(dir, _root, "nojob_import");
        var stackup = TechPersistence.LoadFromFile(result.TechPath!).Stackup;

        // A test that asserted a plausible default here would be asserting the bug: an invented stackup
        // is worse than none, because nothing downstream will ever question it and it WILL be simulated.
        Assert.Empty(stackup.Layers.Where(l => l.Kind is StackupKind.Conductor or StackupKind.Dielectric));
        Assert.Contains(result.Messages, m =>
            m.Contains("no job-file stackup", StringComparison.Ordinal) &&
            m.Contains("left EMPTY and no substrate was invented", StringComparison.Ordinal) &&
            m.Contains("Before the EM path can run", StringComparison.Ordinal));
    }

    // -- Gate 13: order is DECLARED, or it is reported as a guess ----------------------------------

    [Fact]
    public void AFourCopperSetWithX2_RanksExactly_AndReportsNoGuess()
    {
        var dir = Folder("order-declared");
        Write(dir, "a.gbr", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "b.gbr", Artwork("Copper,L2,Inr,Plane", xMm: 2.0));
        Write(dir, "c.gbr", Artwork("Copper,L3,Inr,Plane", xMm: 3.0));
        Write(dir, "d.gbr", Artwork("Copper,L4,Bot,Signal", xMm: 4.0));

        var result = Import(dir, _root, "order_declared_import");

        Assert.All(result.Layers, l => Assert.False(l.OrderGuessed));
        Assert.Contains(result.Messages, m =>
            m.Contains("Copper stack order was DECLARED for all 4 copper layer(s)", StringComparison.Ordinal) &&
            m.Contains("Top Copper, Inner 1, Inner 2, Bottom Copper", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Messages, m => m.Contains("GUESSED for", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameSetStrippedOfItsAttributesAndJobFile_SaysWhichLayersWereOrderedByGuess()
    {
        var dir = Folder("order-guessed");
        Write(dir, "board-top-copper.gbr", Artwork());
        Write(dir, "board-inner-copper.gbr", Artwork(xMm: 2.0));
        Write(dir, "board-bottom-copper.gbr", Artwork(xMm: 3.0));

        var result = Import(dir, _root, "order_guessed_import");

        Assert.All(result.Layers, l => Assert.True(l.OrderGuessed));
        Assert.Contains(result.Messages, m =>
            m.Contains("Copper stack order was GUESSED for 3 of 3 copper layer(s)", StringComparison.Ordinal) &&
            m.Contains("Top Copper, Inner Copper, Bottom Copper", StringComparison.Ordinal));
    }

    // -- Gate 14: colours come from FallbackPalette, never from a colour comment -------------------

    [Fact]
    public void AColourCommentIsIgnored_AndTwoImportsOfTheSameSetProduceTheSameColours()
    {
        var dir = Folder("colour");
        // A G04 comment is one tool's private annotation: not portable, and honouring it would make two
        // imports of the same board look different depending on who generated the files.
        Write(dir, "board.gtl", MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\nG04 Layer_Color=16711680*\n" +
            "%ADD10C,0.400*%\nD10*\nX1000000Y1000000D03*\nM02*\n");

        var first = TechPersistence.LoadFromFile(Import(dir, _root, "colour_a").TechPath!);
        var second = TechPersistence.LoadFromFile(Import(dir, _root, "colour_b").TechPath!);

        var key = first.Layers[0].Key;
        Assert.Equal(FallbackPalette.For(key).Color, first.Layers[0].Color);
        Assert.Equal(first.Layers[0].Color, second.Layers[0].Color);
        Assert.NotEqual(new Rgba(0xFF, 0x00, 0x00), first.Layers[0].Color);
    }

    // -- Gate 15: one flat cell - no LayoutInstance, whatever the input ----------------------------

    [Fact]
    public void NoLayoutInstanceIsEverCreated_NotEvenForAStepAndRepeatPanel()
    {
        var dir = Folder("flat");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.400*%\nD10*\n" +
            "%SRX2Y2I5.0J5.0*%\nX1000000Y1000000D03*\n%SR*%\nM02*\n");
        Write(dir, "board.drl", Drill());

        var result = Import(dir, _root, "flat_import");
        var view = LoadCell(result);

        Assert.Empty(view.Instances);
        Assert.Equal(4, view.Shapes.Count(s => s is ViaShape or CircleShape));
    }

    [Fact]
    public void DeclaredComponentAndPadAttributes_RideOntoTheShapes_ButBuildNoHierarchy()
    {
        var dir = Folder("componentattrs");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.400*%\nD10*\n" +
            "%TO.C,R1*%\n%TO.P,R1,2*%\nX1000000Y1000000D03*\n%TD*%\nM02*\n");

        var view = LoadCell(Import(dir, _root, "componentattrs_import"));

        var shape = Assert.Single(view.Shapes);
        Assert.Equal("R1", shape.Component);
        // L4e carries the %TO.P attribute's WHOLE value, which the format spells as
        // "<component>,<pad>" - this phase carries what the reader read, it does not re-parse it.
        Assert.Equal("R1,2", shape.Pin);
        Assert.Empty(view.Instances);
    }

    // -- Gate 16: nothing left behind -------------------------------------------------------------

    [Fact]
    public void ACancelledLayerMapping_LeavesNoFolder()
    {
        var dir = Folder("cancel");
        // A name and an extension nothing recognizes: rung 4, so the dialog is what settles it.
        Write(dir, "layer_one.zzz", Artwork());

        var before = Directory.GetDirectories(_root);
        var result = Import(dir, _root, "cancel_import",
            new Technology { Name = "W", Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Metal" }] },
            dialog: _ => null);

        Assert.True(result.Cancelled);
        Assert.Null(result.CellDir);
        Assert.Null(result.ImportDir);
        Assert.Equal(before, Directory.GetDirectories(_root));
    }

    [Fact]
    public void AFailedImport_LeavesNoFolder()
    {
        var dir = Folder("fail");
        // %IPNEG is refused by name (L4e's R-L4e-14), so nothing can be read from this set at all.
        Write(dir, "board.gtl", MmHeader + "%IPNEG*%\n%ADD10C,0.400*%\nD10*\nX1000000Y1000000D03*\nM02*\n");

        var before = Directory.GetDirectories(_root);
        var result = Import(dir, _root, "fail_import");

        Assert.True(result.Cancelled);
        Assert.Null(result.ImportDir);
        Assert.Equal(before, Directory.GetDirectories(_root));
    }

    // -- Gate 17: a composited layer is named as composited ----------------------------------------

    [Fact]
    public void ALayerPaintedWithClearPolarity_IsNamedAsComposited()
    {
        var dir = Folder("composite");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,2.000*%\n%ADD11C,0.500*%\n" +
            "D10*\nX1000000Y1000000D03*\n%LPC*%\nD11*\nX1000000Y1000000D03*\n%LPD*%\nM02*\n");
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 5.0));

        var result = Import(dir, _root, "composite_import");

        var composited = Assert.Single(result.Layers.Where(l => l.Composited));
        Assert.Equal("board.gtl", composited.FileName);
        Assert.Contains(result.Messages, m =>
            m.StartsWith("board.gtl", StringComparison.Ordinal) &&
            m.Contains("COMPOSITED for polarity", StringComparison.Ordinal));

        // And the layer that needed no compositing is not reported as composited.
        Assert.False(result.Layers.Single(l => l.FileName == "board.gbl").Composited);
    }

    // -- R-L4g-16: the stroke count is actionable, not decorative ----------------------------------

    [Fact]
    public void AVectorFilledPour_NamesTheLayer_TheCount_AndTheMergeAction()
    {
        var dir = Folder("pour");
        var body = new System.Text.StringBuilder(MmHeader);
        body.Append("%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.050*%\nD10*\n");
        for (int i = 0; i < 250; i++)
            body.Append($"X0Y{i * 50_000}D02*\nX5000000Y{i * 50_000}D01*\n");
        body.Append("M02*\n");
        Write(dir, "board.gtl", body.ToString());

        var result = Import(dir, _root, "pour_import");

        Assert.Contains(result.Messages, m =>
            m.Contains("Top Copper arrived as 250 separate strokes", StringComparison.Ordinal) &&
            m.Contains("Merge action", StringComparison.Ordinal));
    }

    // -- R-L4g-17: say what comes next, once ------------------------------------------------------

    [Fact]
    public void TheSummarySaysToCropBeforeSettingUpEmPorts_Once()
    {
        var dir = Folder("cropadvice");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));

        var result = Import(dir, _root, "cropadvice_import");

        Assert.Equal(1, result.Messages.Count(m =>
            m.Contains("before setting up EM ports", StringComparison.Ordinal) &&
            m.Contains("crop", StringComparison.Ordinal)));
    }

    // -- R-L4g-13: a second import of the same set does not merge into the first one's folder ------

    [Fact]
    public void ASecondImportOfTheSameSet_GetsItsOwnFolder()
    {
        var dir = Folder("twice");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));

        var first = Import(dir, _root, "twice_import");
        var second = Import(dir, _root, "twice_import");

        Assert.NotEqual(first.ImportDir, second.ImportDir);
        Assert.Equal("twice_import", Path.GetFileName(first.ImportDir));
        Assert.Equal("twice_import_2", Path.GetFileName(second.ImportDir));
    }

    // -- R-L4h-6's callback: asked only when the inference actually had to guess -------------------

    [Fact]
    public void TheDrillFormatCallback_IsNotCalledWhenTheFileDeclaresItsOwnFormat()
    {
        var dir = Folder("drillformat-declared");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", Drill());

        bool asked = false;
        var result = Import(dir, _root, "drillformat_declared_import",
            drillFormat: (_, _, _, _) => { asked = true; return new GerberImport.DrillFormatChoice(null); });

        Assert.False(asked);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void CancellingTheDrillFormatPrompt_AbortsTheWholeImport_AndLeavesNoFolder()
    {
        var dir = Folder("drillformat-guess");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        // No units keyword and no suppression word: the inference has to guess (L4f's own section 2).
        Write(dir, "board.drl", "M48\nT1C0.300\n%\nG90\nT1\nX0010000Y0010000\nM30\n");

        var before = Directory.GetDirectories(_root);
        var result = Import(dir, _root, "drillformat_guess_import", drillFormat: (_, _, _, _) => null);

        Assert.True(result.Cancelled);
        Assert.Equal(before, Directory.GetDirectories(_root));
    }
}
