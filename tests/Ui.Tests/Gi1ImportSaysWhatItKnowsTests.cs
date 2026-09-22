// Gate for docs/sonnet-briefs/brief-gi1-report-says-less-than-it-knows.md.
//
// Three facts that were established during an import and then contradicted, discarded or undersold.
// None of the three was a parsing bug — every number was already right — so every assertion here is
// about what the import SAYS and what it BUILDS, never about what it read.
//
// Fixtures are hand-authored, following L4e/L4f/L4g's precedent: worth less than a real set as a
// dialect test, costs nothing to redistribute, names no tool or product.
//
// COUNTERS AND CONTENT ONLY. No wall-clock assertion anywhere in this file.

using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class Gi1ImportSaysWhatItKnowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gi1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(string? fileFunction = null, double xMm = 1.0, double yMm = 1.0)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        long x = (long)Math.Round(xMm * 1_000_000);
        long y = (long)Math.Round(yMm * 1_000_000);
        return MmHeader + attribute + "%ADD10C,0.400*%\nD10*\n" + $"X{x}Y{y}D03*\n" + "M02*\n";
    }

    private static string Drill(double xMm = 1.0, double yMm = 1.0, string? typeSection = null) =>
        "M48\nMETRIC\n" + (typeSection is { Length: > 0 } t ? t + "\n" : "") +
        "T1C0.300000\n%\nG90\nG05\nT1\n" + $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    /// <summary>A file that ROUTES and never drills: one canned slot, no plain hits.</summary>
    private static string RoutOnly() =>
        "M48\nMETRIC\nT1C1.000000\n%\nG90\nG05\nT1\nX1.000000Y1.000000G85X3.000000Y1.000000\nM30\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string content) =>
        File.WriteAllText(Path.Combine(dir, fileName), content);

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];

    private GerberImport.ImportResult Import(string sourceDir, string name) =>
        GerberImport.Import(FilesIn(sourceDir), _root, name, null, 1000, null, null);

    private static Technology TechOf(GerberImport.ImportResult r) =>
        TechPersistence.LoadFromFile(r.TechPath!);

    private static ExcellonReadResult Read(string text)
    {
        using var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text));
        return ExcellonReader.Read(ms, 1000);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-gi1-1 — the headline and the evidence are two views of one object and must never disagree
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static DrillFormatInference Inference(
        DrillFormatEvidence zeroEvidence, GerberZeroOmission omission, bool decimalCoordinates) =>
        new()
        {
            Unit = GerberUnit.Millimetres, UnitEvidence = DrillFormatEvidence.UnitsKeyword,
            IntegerDigits = 3, DecimalDigits = 4, DigitsEvidence = DrillFormatEvidence.CoordinateWidth,
            ZeroOmission = omission, ZeroOmissionEvidence = zeroEvidence,
            DecimalCoordinates = decimalCoordinates, Evidence = [],
        };

    /// <summary>
    /// The bug in one line: <c>ZeroOmission</c> carries a NOMINAL <c>Leading</c> on both rungs that
    /// mean "the question does not arise", precisely because the value is unused there. Rendering
    /// from it printed "leading zeros suppressed" against an evidence line reading "Zero
    /// suppression: none".
    /// </summary>
    [Theory]
    [InlineData(DrillFormatEvidence.CoordinateWidth)]
    [InlineData(DrillFormatEvidence.DecimalCoordinates)]
    public void WhenTheEvidenceSaysNothingIsSuppressed_TheHeadlineDoesNotNameAConvention(
        DrillFormatEvidence evidence)
    {
        bool decimals = evidence == DrillFormatEvidence.DecimalCoordinates;
        string rendered = Inference(evidence, GerberZeroOmission.Leading, decimals).ToString();

        Assert.DoesNotContain("suppress", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mm 3:4", rendered, StringComparison.Ordinal);
    }

    /// <summary>The rungs that DO settle a convention still name it — this fix must not flatten
    /// every case into "nothing suppressed".</summary>
    [Theory]
    [InlineData(GerberZeroOmission.Leading,  "leading zeros suppressed")]
    [InlineData(GerberZeroOmission.Trailing, "trailing zeros suppressed")]
    public void WhenTheFileDidStateAConvention_TheHeadlineStillNamesIt(
        GerberZeroOmission omission, string expected)
    {
        string rendered = Inference(DrillFormatEvidence.UnitsKeyword, omission, false).ToString();
        Assert.Contains(expected, rendered, StringComparison.Ordinal);
    }

    /// <summary>End to end, on the file shape that produced the contradiction: every coordinate
    /// seven digits wide, some carrying leading zeros, no format comment. The reader's own headline
    /// and its own evidence sentence must not argue.</summary>
    [Fact]
    public void AFullWidthDrillFile_RendersAHeadlineThatAgreesWithItsOwnEvidence()
    {
        var read = Read("M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" +
                        "X0012700Y0012700\nX0025400Y0009000\nM30\n");

        Assert.Null(read.Refusal);
        Assert.Equal(DrillFormatEvidence.CoordinateWidth, read.Format.ZeroOmissionEvidence);

        string headline = read.Format.ToString();
        string evidence = string.Join(" ", read.Format.Evidence);

        // The evidence has always said this; the headline used to say the opposite.
        Assert.Contains("Zero suppression: none", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("suppress", headline, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-gi1-2 — plating is settled once and carried, and a non-plated hole is not a conductor
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ADrillFileThatDeclaresItselfNonPlated_MintsAViaEntryThatIsNotPlated()
    {
        var dir = Folder("np");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", Drill(typeSection: ";TYPE=NON_PLATED"));

        var result = Import(dir, "np_import");
        Assert.False(result.Cancelled);

        var via = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(via.Plated);
        // Fill is a fill MODEL and both of its values are metal, so it says nothing here rather than
        // reading "Plated" beside a Plated flag of false.
        Assert.Null(via.Fill);

        Assert.Contains(result.Messages, m => m.Contains("NON-PLATED", StringComparison.Ordinal));
    }

    /// <summary>The null case, and the one that matters most: a drill file that declares nothing is
    /// bit-identical to its behaviour before <c>StackupLayer.Plated</c> existed.</summary>
    [Fact]
    public void ADrillFileThatDeclaresNothing_IsUnchanged_PlatedAndSpanningTheStack()
    {
        var dir = Folder("silent");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", Drill());

        var result = Import(dir, "silent_import");

        var via = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Null(via.Plated);                       // null MEANS plated — nothing was written
        Assert.Equal(ViaFillKind.Plated, via.Fill);
        Assert.DoesNotContain(result.Messages, m => m.Contains("NON-PLATED", StringComparison.Ordinal));
    }

    /// <summary>
    /// The consequence the field exists for. <c>PlanarExtractor.BuildViaBinding</c> is the sole route
    /// from a drawing layer to a via entry, so excluding a non-plated entry there covers the point-via
    /// branch and the region branch at once. Both legs are asserted: the pairing is what makes this a
    /// test of the exclusion rather than of an empty fixture.
    /// </summary>
    [Fact]
    public void ANonPlatedViaEntry_ProducesNoConductorInAnEmRun_AndTheRunSaysSo()
    {
        const int dbu = LayoutUnits.DefaultDbuPerMicron;
        static long Um(double v) => (long)Math.Round(v * dbu);
        static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
            new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };
        static LabelShape Port(LayerKey layer, double x, double y, string n) =>
            new() { Layer = layer, X = Um(x), Y = Um(y), Text = n, Height = Um(20), IsPort = true };

        LayerKey metal1 = new(1, 0), metal2 = new(2, 0), post = new(3, 0);

        List<LayoutShape> Shapes() =>
        [
            Rect(metal1, 0, 0, 120, 100), Rect(metal1, 180, 0, 300, 100),
            Rect(metal2, 20, 0, 280, 100),
            Port(metal1, 0, 50, "P1"), Port(metal1, 300, 50, "P2"),
            Rect(post, 40, 30, 80, 70),
        ];

        // Plated (the null default): the post is a vertical conductor, exactly as before.
        var plated = PlanarExtractor.Extract(Shapes(), StarterTechnologies.MmicGaAs(), dbu, 30e9);
        Assert.True(plated.Ok, plated.Refusal);
        Assert.Single(plated.Problem!.ViaList);

        // The same geometry with that entry marked non-plated: a hole, not metal.
        var tech = StarterTechnologies.MmicGaAs();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via && l.Name == "Metal1-Metal2 Post") l.Plated = false;

        var nonPlated = PlanarExtractor.Extract(Shapes(), tech, dbu, 30e9);
        Assert.True(nonPlated.Ok, nonPlated.Refusal);
        Assert.Empty(nonPlated.Problem!.ViaList);
        Assert.Contains(nonPlated.Notes, n => n.Contains("NON-PLATED", StringComparison.Ordinal));
    }

    /// <summary>Additive and nullable, so a technology that never had an opinion round-trips
    /// unchanged rather than gaining a field.</summary>
    [Fact]
    public void ThePlatedFieldRoundTripsThroughCtech_AndAnUnsetOneStaysUnset()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var entry = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Via);
        Assert.Null(entry.Plated);

        string path = Path.Combine(_root, "roundtrip.ctech");
        TechPersistence.SaveToFile(path, tech);
        Assert.Null(TechPersistence.LoadFromFile(path).Stackup.Layers
            .First(l => l.Kind == StackupKind.Via).Plated);

        entry.Plated = false;
        TechPersistence.SaveToFile(path, tech);
        Assert.False(TechPersistence.LoadFromFile(path).Stackup.Layers
            .First(l => l.Kind == StackupKind.Via).Plated);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-gi1-3 — a file that drilled nothing asserts neither plating nor a span
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A rout file is board outline and cutouts. Its slots are drawn REGIONS on the drill layer, and
    /// the extractor's region branch builds a vertical conductor out of every region on a via-bound
    /// layer — so leaving this entry at the usual "unstated means plated" turns a board's cutouts
    /// into metal shorting the whole stack.
    /// </summary>
    [Fact]
    public void ARoutOnlyFile_MintsALayerThatClaimsNeitherPlatingNorASpan()
    {
        var dir = Folder("rout");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.rou", RoutOnly());

        var result = Import(dir, "rout_import");
        Assert.False(result.Cancelled);

        var via = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(via.Plated);
        Assert.Null(via.Fill);
        Assert.Null(via.SpanFromLayer);
        Assert.Null(via.SpanToLayer);

        Assert.Contains(result.Messages,
            m => m.Contains("routed", StringComparison.Ordinal) &&
                 m.Contains("drilled no holes", StringComparison.Ordinal));
    }

    /// <summary>The same file set with real hits is the control: a drill file that DID drill still
    /// mints a plated entry, so the rule above is keyed on "drilled nothing" and not on the
    /// extension or the presence of slots.</summary>
    [Fact]
    public void AFileThatBothRoutedAndDrilled_IsStillAPlatedViaLayer()
    {
        var dir = Folder("both");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.rou",
            "M48\nMETRIC\nT1C1.000000\n%\nG90\nG05\nT1\n" +
            "X1.000000Y1.000000G85X3.000000Y1.000000\nX5.000000Y5.000000\nM30\n");

        var result = Import(dir, "both_import");

        var via = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal(ViaFillKind.Plated, via.Fill);
        Assert.DoesNotContain(result.Messages, m => m.Contains("drilled no holes", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-plated entry connects nothing, so having no span is its CORRECT state — the validator
    /// must not report the two "spans an unknown conductor layer" problems it reports for a via that
    /// lost its span. Without this the rout-only rule above would trade one bug for two messages on
    /// every set that ships a rout file alongside a job file.
    /// </summary>
    [Fact]
    public void ANonPlatedViaEntryWithNoSpan_IsNotReportedAsSpanningNothing()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var entry = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Via);
        entry.SpanFromLayer = null;
        entry.SpanToLayer = null;

        // Plated (the default): a via with no span IS a problem, and stays one.
        Assert.Equal(2, TechValidation.Analyze(tech)
            .Count(p => p.Message.Contains("spans an unknown conductor layer", StringComparison.Ordinal)));

        entry.Plated = false;
        Assert.DoesNotContain(TechValidation.Analyze(tech),
            p => p.Message.Contains("spans an unknown conductor layer", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-gi1-4 / R-gi1-5 — the numeric prefix the files carry, named as what it is
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The stack order the import REPORTED, as file names, top to bottom.
    ///
    /// <para>Read out of the message because that sentence IS R-gi1-4's deliverable, and translated
    /// back to file names through <c>result.Layers</c> because the layer NAMES a guessed set gets
    /// ("Inner 1", "Inner 2", …) are themselves assigned positionally — asserting on them would be
    /// asserting that the order is the order.</para>
    /// </summary>
    private static List<string> ReportedOrder(GerberImport.ImportResult result)
    {
        string message = Assert.Single(result.Messages,
            m => m.StartsWith("Copper stack order", StringComparison.Ordinal));

        // Two spellings: "…GUESSED … top to bottom, is: A, B." and "…DECLARED … : A, B, top to bottom."
        const string guessed = "top to bottom, is: ";
        string tail;
        int at = message.IndexOf(guessed, StringComparison.Ordinal);
        if (at >= 0)
            tail = message[(at + guessed.Length)..];
        else
        {
            int colon = message.IndexOf("): ", StringComparison.Ordinal);
            Assert.True(colon >= 0, message);
            tail = message[(colon + 3)..];
            const string suffix = ", top to bottom";
            int end = tail.LastIndexOf(suffix, StringComparison.Ordinal);
            Assert.True(end >= 0, message);
            tail = tail[..end];
        }

        var fileByLayer = result.Layers.ToDictionary(l => l.LayerName, l => l.FileName, StringComparer.Ordinal);
        return [.. tail.TrimEnd('.').Split(", ").Select(n => fileByLayer[n])];
    }

    /// <summary>
    /// The inner names are chosen to sort WRONGLY alphabetically — "l10" before "l2" — so a pass
    /// only proves the prefix was used, not that the old tiebreak happened to agree.
    /// </summary>
    private static string PrefixedSet(string dir, bool numberEveryFile)
    {
        // No file function anywhere: every conductor is GUESSED, which is the only state in which
        // the prefix rung runs at all. (Mixing declared and guessed layers in one set orders every
        // declared one before every guessed one — a pre-existing consequence of ranking a real
        // CopperIndex against SideRank's sentinels, and not this phase's to change.)
        Write(dir, "01_top.gtl",  Artwork());
        Write(dir, numberEveryFile ? "02_l2.g2" : "mid.g2", Artwork());
        Write(dir, "03_l10.g3", Artwork());
        Write(dir, "04_l4.g4",  Artwork());
        Write(dir, "05_bot.gbl", Artwork());
        return dir;
    }

    [Fact]
    public void WhenEveryConductorFileCarriesADistinctNumber_TheStackIsOrderedByIt_AndSaysSo()
    {
        var result = Import(PrefixedSet(Folder("prefixed"), numberEveryFile: true), "prefixed_import");
        Assert.False(result.Cancelled);

        string message = Assert.Single(result.Messages,
            m => m.StartsWith("Copper stack order", StringComparison.Ordinal));

        // R-gi1-5: still a GUESS. A prefix is a convention, not a declaration, and a set numbered in
        // export order rather than stack order exists — so this must not read as a ranking.
        Assert.Contains("GUESSED", message, StringComparison.Ordinal);
        Assert.Contains("numeric prefix in the file names", message, StringComparison.Ordinal);

        // "03_l10" before "04_l4" is the pair that settles it: the alphabetical tiebreak this rung
        // replaced sorts "l10" before "l2", so a pass here cannot be the old ordering agreeing.
        Assert.Equal(
            ["01_top.gtl", "02_l2.g2", "03_l10.g3", "04_l4.g4", "05_bot.gbl"],
            ReportedOrder(result));
    }

    /// <summary>All-or-nothing (R-gi1-4). One unnumbered conductor and the prefix says nothing about
    /// where that file goes, so the whole rung is withdrawn rather than applied partially.</summary>
    [Fact]
    public void WhenOneConductorFileHasNoNumber_TheRungIsWithdrawnEntirely()
    {
        var result = Import(PrefixedSet(Folder("partial"), numberEveryFile: false), "partial_import");

        string message = Assert.Single(result.Messages,
            m => m.StartsWith("Copper stack order", StringComparison.Ordinal));

        Assert.Contains("GUESSED", message, StringComparison.Ordinal);
        Assert.DoesNotContain("numeric prefix", message, StringComparison.Ordinal);
    }

    /// <summary>A set the job file or %TF.FileFunction already ranked is untouched: the prefix is a
    /// tiebreak inside an unknown side, never a competitor to a declaration.</summary>
    [Fact]
    public void ADeclaredSet_StillReportsItsOrderAsDeclared_WhateverTheFileNamesSay()
    {
        var dir = Folder("declared");
        Write(dir, "09_top.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "01_bot.gbl", Artwork("Copper,L2,Bot,Signal"));

        var result = Import(dir, "declared_import");

        string message = Assert.Single(result.Messages,
            m => m.StartsWith("Copper stack order", StringComparison.Ordinal));
        Assert.Contains("DECLARED", message, StringComparison.Ordinal);
        Assert.DoesNotContain("numeric prefix", message, StringComparison.Ordinal);

        // 09_top is still on top, though its number is the larger: a declaration is not a tiebreak.
        Assert.Equal(["09_top.gtl", "01_bot.gbl"], ReportedOrder(result));
    }
}
