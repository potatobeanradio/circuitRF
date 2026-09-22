// Gate for docs/sonnet-briefs/brief-gi5-netlist-companion.md.
//
// The board netlist an output set ships beside its artwork. A Gerber import of a real multi-layer
// board prints three apologies in one run — a via and a plated component hole are "indistinguishable
// from artwork alone", a composited layer's "per-object net names are gone", and "no layer span was
// declared" so every hole is assumed to go through the board. All three are true about the ARTWORK
// and none of them is true about the FOLDER, which is where this file starts.
//
// R-gi5-1 IS THE THING TO CHECK IN REVIEW, and several tests below are written to fail if it is ever
// broken: the netlist is EVIDENCE about the artwork and never geometry. No shape is created from it,
// moved by it or deleted because of it.
//
// Fixtures are hand-authored, following L4e/L4f/L4g/GI1/GI4's precedent: worth less than a real set
// as a dialect test, costs nothing to redistribute, and names no tool, product or toolchain. The
// format is a standards-body one, read from public documentation only.
//
// COUNTERS AND CONTENT ONLY. No wall-clock assertion anywhere in this file.

using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class Gi5NetlistCompanionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gi5-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int Dbu = 1000;                       // 1 DBU = 1 nm
    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    // ── Artwork fixtures ─────────────────────────────────────────────────────────────────────

    private static long Mm(double mm) => (long)Math.Round(mm * 1_000_000);

    /// <summary>Discrete pads on one copper layer — the ordinary, un-composited case.</summary>
    private static string Pads(string fileFunction, params (double X, double Y)[] pads) =>
        MmHeader + $"%TF.FileFunction,{fileFunction}*%\nG01*\n%ADD10C,0.400*%\nD10*\n" +
        string.Concat(pads.Select(p => $"X{Mm(p.X)}Y{Mm(p.Y)}D03*\n")) + "M02*\n";

    /// <summary>
    /// A 10 mm copper pour with a clear-polarity clearance in it, which composites the whole layer —
    /// so every pad on it is unioned into the pour and its per-object identity is gone. This is the
    /// fixture R-gi5-6's headline capability needs, and there is no other way to produce it: the loss
    /// is what compositing IS.
    /// </summary>
    private static string PourWithPads(string fileFunction, params (double X, double Y)[] pads) =>
        MmHeader + $"%TF.FileFunction,{fileFunction}*%\nG01*\n" +
        "%LPD*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\n" +
        "%LPC*%\nG36*\nX9200000Y9200000D02*\nX9200000Y9800000D01*\nX9800000Y9800000D01*\n" +
        "X9800000Y9200000D01*\nX9200000Y9200000D01*\nG37*\n%LPD*%\n%ADD10C,0.800*%\nD10*\n" +
        string.Concat(pads.Select(p => $"X{Mm(p.X)}Y{Mm(p.Y)}D03*\n")) + "M02*\n";

    private static string Drill(params (double X, double Y)[] hits) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" +
        string.Concat(hits.Select(h => $"X{h.X:0.000000}Y{h.Y:0.000000}\n")) + "M30\n";

    // ── Netlist fixtures ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One feature record, written in the format's own COLUMNS: the operation code in 1-3, the net
    /// name in 4-17, the reference designator and pin in 20-30, and the letter-tagged tail from
    /// column 31 — the drill field and its plating letter, the access code, and the coordinates.
    /// </summary>
    /// <param name="counts">The coordinate, in counts of whatever resolution the header declares.
    /// Deliberately not millimetres: a netlist's units are a property of the FILE, and every scale
    /// test below depends on being able to write the same digits under a different header.</param>
    private static string Record(
        string net, (int X, int Y) counts, string? component = null, string? pin = null,
        int? drill = null, bool plated = true, int? access = null, int code = 317)
    {
        string reference = component is null ? "" : $"{component}-{pin ?? ""}";
        string tail =
            (drill is { } d ? $"D{d:0000}{(plated ? 'P' : 'U')}" : "") +
            (access is { } a ? $"A{a:00}" : "") +
            $"X{Sign(counts.X)}Y{Sign(counts.Y)}";
        return $"{code:000}{net.PadRight(14)}  {reference.PadRight(11)}{tail}\n";

        static string Sign(int v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("000000");
    }

    /// <param name="unitsCode">The header's units code: 0 and 2 are the two inch resolutions
    /// (0.0001 in and 0.00001 in), 1 is the metric one (0.001 mm).</param>
    private static string Netlist(int unitsCode, params string[] records) =>
        "C  hand-authored fixture\n" +
        "P  JOB       gi5\n" +
        $"P  UNITS     CUST {unitsCode}\n" +
        string.Concat(records) +
        "999\n";

    /// <summary>Metric counts for a millimetre value — 0.001 mm per count.</summary>
    private static int Counts(double mm) => (int)Math.Round(mm * 1000);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

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

    private GerberImport.ImportResult Import(string sourceDir, string name, string? parent = null) =>
        GerberImport.Import(FilesIn(sourceDir), parent ?? _root, name, null, Dbu);

    private static Technology TechOf(GerberImport.ImportResult r) =>
        TechPersistence.LoadFromFile(r.TechPath!);

    private static LayoutView Cell(GerberImport.ImportResult r) =>
        LayoutPersistence.LoadFromFile(ClayPath(r));

    private static string ClayPath(GerberImport.ImportResult r) =>
        Directory.EnumerateFiles(CellFolder.SubFolderPath(r.CellDir!, ViewType.Layout), "*.clay").Single();

    private static string All(GerberImport.ImportResult r) => string.Join("\n", r.Messages);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 1 — a set WITHOUT a usable netlist imports bit-identically (R-gi5-2)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The gate that makes this phase safe to add to a mature importer, and the only one that has to
    /// hold on every set in the world: everything GI5 contributes is additive, so a set whose netlist
    /// contributes nothing must produce THE SAME BYTES as one with no netlist at all — the netlist's
    /// own messages being the entire difference.
    ///
    /// <para>The netlist here is a real, well-formed one for a DIFFERENT board, which is also the
    /// only shape of this test that can be written: a netlist that matches is supposed to change the
    /// document. Both imports go into their own parent folder under the same name, because the
    /// import folder's name is baked into the technology's name and into the <c>.clay</c>'s relative
    /// reference to it.</para>
    /// </summary>
    [Fact]
    public void ANetlistThatContributesNothing_ChangesNoByteOfTheClayOrTheCtech()
    {
        string without = Folder("without");
        Write(without, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1), (3, 3)));
        Write(without, "board.drl", Drill((1, 1), (3, 3)));

        string with = Folder("with");
        foreach (string file in FilesIn(without)) File.Copy(file, Path.Combine(with, Path.GetFileName(file)));
        Write(with, "board.ipc", Netlist(1,
            Record("N$1", (Counts(200), Counts(200)), drill: 300),
            Record("N$2", (Counts(220), Counts(220)), drill: 300)));

        string parentA = Folder("parentA");
        string parentB = Folder("parentB");
        var a = Import(without, "board", parentA);
        var b = Import(with, "board", parentB);

        Assert.Equal(File.ReadAllBytes(ClayPath(a)), File.ReadAllBytes(ClayPath(b)));
        Assert.Equal(File.ReadAllBytes(a.TechPath!), File.ReadAllBytes(b.TechPath!));

        // Every message the netlist-free run produced is still there, in order, and the extra ones
        // are the netlist's own.
        Assert.Equal(a.Messages, b.Messages.Where(a.Messages.Contains).ToList());
        Assert.Contains(b.Messages.Except(a.Messages), m => m.Contains("board.ipc", StringComparison.Ordinal));

        // ...and the netlist-free run says nothing about a netlist at all.
        Assert.DoesNotContain("netlist", All(a), StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 2 — never geometry (R-gi5-1, R-gi5-11)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A netlist naming a pad the artwork does not have creates NO shape. This is the rule the whole
    /// phase rests on, and the failure it prevents is the tempting one: a reader that "helpfully"
    /// draws the missing pad would put copper on a board that has none, from a file that is not a
    /// drawing.
    /// </summary>
    [Fact]
    public void ANetlistNamingAPadTheArtworkDoesNotHave_CreatesNothing_AndSaysSo()
    {
        string bare = Folder("bare");
        Write(bare, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1), (3, 3)));

        string extra = Folder("extra");
        foreach (string file in FilesIn(bare)) File.Copy(file, Path.Combine(extra, Path.GetFileName(file)));
        Write(extra, "board.ipc", Netlist(1,
            Record("SIG1", (Counts(1), Counts(1)), code: 327),
            // On the board, and on no copper: the case the cross-check must NOT swallow, because a
            // record inside the artwork's extent that matches nothing is real information.
            Record("GHOST", (Counts(2), Counts(2)), code: 327)));

        var a = Import(bare, "bare", Folder("pa"));
        var b = Import(extra, "extra", Folder("pb"));

        Assert.Equal(Cell(a).Shapes.Count, Cell(b).Shapes.Count);
        Assert.Contains("matched no copper shape", All(b), StringComparison.Ordinal);

        // And the pad that IS there was still named — the discrepancy is reported without costing
        // the records that did match.
        Assert.Contains(Cell(b).Shapes, s => s.Net == "SIG1");
    }

    /// <summary>R-gi5-11's other half: a hole the netlist names and no drill file drills. Counted,
    /// never drilled.</summary>
    [Fact]
    public void ANetlistNamingAHoleTheDrillFileDoesNotHave_IsCounted_AndNoHoleIsCreated()
    {
        string dir = Folder("hole-skew");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1), (3, 3)));
        Write(dir, "board.drl", Drill((1, 1)));
        Write(dir, "board.ipc", Netlist(1,
            Record("N1", (Counts(1), Counts(1)), drill: 300),
            Record("N2", (Counts(3), Counts(3)), drill: 300)));        // drilled in no file

        var result = Import(dir, "hole_skew");

        Assert.Contains("names 1 hole(s) that no drill file in this set drills", All(result), StringComparison.Ordinal);
        Assert.Single(Cell(result).Shapes.OfType<ViaShape>());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — via versus plated component hole (R-gi5-3), and the apology is GONE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The distinction <c>DrillViaPairing</c> declares unavailable, made directly: a record carrying
    /// a net and no component reference is a via; a record carrying a reference AND a pin is a
    /// component hole. Both holes are identical in the artwork and identical in the drill file —
    /// which is exactly why the artwork alone cannot tell them apart.
    /// </summary>
    [Fact]
    public void TheNetlistSplitsViasFromComponentHoles_AndTheOldApologyIsGone()
    {
        string dir = Folder("split");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2), (5, 5)));
        Write(dir, "board.drl", Drill((2, 2), (5, 5)));
        Write(dir, "board.ipc", Netlist(1,
            Record("VCC", (Counts(2), Counts(2)), drill: 300),                      // no reference: a via
            Record("VCC", (Counts(5), Counts(5)), component: "R1", pin: "1", drill: 300)));

        var result = Import(dir, "split");
        string all = All(result);

        var shapes = Cell(result).Shapes;
        Assert.Single(shapes.OfType<ViaShape>());
        Assert.Contains("classified 2 hole(s) the artwork could not", all, StringComparison.Ordinal);
        Assert.Contains("1 via(s)", all, StringComparison.Ordinal);
        Assert.Contains("1 component hole(s)", all, StringComparison.Ordinal);

        // R-gi5-12: the sentence this replaces is GONE from the run, not printed beside its
        // replacement.
        Assert.DoesNotContain("The distinction was not available.", all, StringComparison.Ordinal);

        // The counterpart, and it is what makes this a test of the netlist rather than of the
        // fixture: the same two files with no netlist beside them still cannot tell.
        string blind = Folder("split-blind");
        foreach (string file in FilesIn(dir))
            if (!file.EndsWith(".ipc", StringComparison.Ordinal))
                File.Copy(file, Path.Combine(blind, Path.GetFileName(file)));
        Assert.Contains("The distinction was not available.", All(Import(blind, "split_blind")),
                        StringComparison.Ordinal);
    }

    /// <summary>R-gi5-3's second half, and R-gi5-12's: a run that reads a netlist and STILL cannot
    /// tell must say THAT, specifically — never fall silent because a netlist happened to be
    /// present.</summary>
    [Fact]
    public void AHoleTheNetlistDoesNotCover_IsReportedAsStillUndecided()
    {
        string dir = Folder("partial");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2), (5, 5)));
        Write(dir, "board.drl", Drill((2, 2), (5, 5)));
        Write(dir, "board.ipc", Netlist(1, Record("VCC", (Counts(2), Counts(2)), drill: 300)));

        string all = All(Import(dir, "partial"));

        Assert.Contains("still not available for these", all, StringComparison.Ordinal);
        Assert.Contains("have no record in the board netlist at all", all, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 4 — non-plated does not conduct (R-gi5-4)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A board's mounting holes are the case that matters: a millimetre-scale barrel modelled as
    /// plated shorts every layer it passes through, and the run completes cleanly and is wrong. The
    /// PAIRING is the assertion — the same set with the marking removed still mints a plated entry —
    /// which is what makes this a test of the netlist rather than of the fixture. That a non-plated
    /// entry then produces no conductor is GI1's own gate
    /// (<c>ANonPlatedViaEntry_ProducesNoConductorInAnEmRun_AndTheRunSaysSo</c>) and is not restated.
    /// </summary>
    [Fact]
    public void ANetlistMarkingHolesNonPlated_MintsAViaEntryThatIsNotPlated()
    {
        string np = Folder("np");
        Write(np, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(np, "board.drl", Drill((2, 2)));
        Write(np, "board.ipc", Netlist(1,
            Record("", (Counts(2), Counts(2)), drill: 300, plated: false)));

        var entry = Assert.Single(TechOf(Import(np, "np")).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(entry.Plated);
        Assert.Null(entry.Fill);                       // a fill model means nothing for a hole
        Assert.Null(entry.WallThicknessDbu);           // nor does a plating thickness

        string p = Folder("p");
        Write(p, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(p, "board.drl", Drill((2, 2)));
        Write(p, "board.ipc", Netlist(1,
            Record("", (Counts(2), Counts(2)), drill: 300, plated: true)));

        var plated = Assert.Single(TechOf(Import(p, "p")).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.True(plated.Plated);
        Assert.Equal(ViaFillKind.Plated, plated.Fill);
    }

    /// <summary>R-gi5-4's last sentence. Two companions of equal standing that disagree are BOTH
    /// reported and NEITHER applied — the drill file itself said nothing, so unstated is what it goes
    /// back to. Picking one would be exactly the silent guess this series exists to remove.</summary>
    [Fact]
    public void WhenTheToolListingAndTheNetlistDisagreeAboutPlating_NeitherWins_AndBothAreNamed()
    {
        string dir = Folder("disagree");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(dir, "board.drl", Drill((2, 2)));
        Write(dir, "tools.rep", "Tool   Size       Plating\nT01    0.300000   NON-PLATED\n" +
                                "T02    1.000000   PLATED\n");
        Write(dir, "board.ipc", Netlist(1,
            Record("GND", (Counts(2), Counts(2)), drill: 300, plated: true)));

        var result = Import(dir, "disagree");
        string all = All(result);

        Assert.Contains("NEITHER was applied", all, StringComparison.Ordinal);
        Assert.Contains("T1 (listing: non-plated, netlist: plated)", all, StringComparison.Ordinal);

        // Unstated means plated, which is what the entry had before either companion spoke.
        var entry = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Null(entry.Plated);
    }

    /// <summary>And the drill file still outranks both — a file is authoritative about itself, the
    /// same rank GI4's R-gi4-3 gives it about its own coordinate format.</summary>
    [Fact]
    public void TheDrillFilesOwnStatementOutranksTheNetlist_AndTheDisagreementIsNamed()
    {
        string dir = Folder("file-wins");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(dir, "board.drl", ";TYPE=NON_PLATED\nM48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\nX2.000000Y2.000000\nM30\n");
        Write(dir, "board.ipc", Netlist(1,
            Record("GND", (Counts(2), Counts(2)), drill: 300, plated: true)));

        var result = Import(dir, "file_wins");

        Assert.Contains("The drill file was preferred", All(result), StringComparison.Ordinal);
        var entry = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(entry.Plated);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 5 — layer span (R-gi5-5)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A buried span, read off two records at one coordinate naming two different layers, applied to
    /// the via entry the import already mints — and NO entry synthesised for it, which is R-gi5-5's
    /// other half and the line between this phase and blind/buried support as a first-class stackup
    /// concept.
    /// </summary>
    [Fact]
    public void ABuriedSpanIsReadAndApplied_AndNoStackupEntryIsSynthesisedForIt()
    {
        string dir = Folder("buried");
        Write(dir, "l1.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(dir, "l2.g2", Pads("Copper,L2,Inr,Signal", (2, 2)));
        Write(dir, "l3.g3", Pads("Copper,L3,Inr,Signal", (2, 2)));
        Write(dir, "l4.gbl", Pads("Copper,L4,Bot,Signal", (2, 2)));
        Write(dir, "board.drl", Drill((2, 2)));
        Write(dir, "board.ipc", Netlist(1,
            Record("N1", (Counts(2), Counts(2)), drill: 300, access: 2),
            Record("N1", (Counts(2), Counts(2)), drill: 300, access: 3)));

        var result = Import(dir, "buried");
        string all = All(result);
        var tech = TechOf(result);

        Assert.Contains("The board netlist states a buried span 2-3", all, StringComparison.Ordinal);
        Assert.DoesNotContain("No layer span was declared", all, StringComparison.Ordinal);
        Assert.Contains("Layer spans found: 2-3", all, StringComparison.Ordinal);

        // ONE via entry, which is one per drill file — the number this import already minted.
        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal(4, conductors.Count);
        Assert.Equal(conductors[1].Name, via.SpanFromLayer);
        Assert.Equal(conductors[2].Name, via.SpanToLayer);
    }

    /// <summary>The through case, which is the one that retires the apology on the boards most people
    /// have: an access code naming both outer surfaces IS a declared through-hole, and saying "no
    /// layer span was declared" beside it was never true of the folder.</summary>
    [Fact]
    public void AThroughSpanFromTheNetlist_RetiresTheNoSpanApology()
    {
        string dir = Folder("through");
        Write(dir, "l1.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(dir, "l2.gbl", Pads("Copper,L2,Bot,Signal", (2, 2)));
        Write(dir, "board.drl", Drill((2, 2)));
        Write(dir, "board.ipc", Netlist(1, Record("N1", (Counts(2), Counts(2)), drill: 300, access: 0)));

        string all = All(Import(dir, "through"));
        Assert.Contains("The board netlist states a through-hole span 1-2", all, StringComparison.Ordinal);
        Assert.DoesNotContain("No layer span was declared", all, StringComparison.Ordinal);
    }

    /// <summary>Two spans in one drill file is one stackup entry that cannot carry both, so NEITHER
    /// is applied and both are named — the same shape as every other "prefer neither" in this
    /// series.</summary>
    [Fact]
    public void TwoDifferentSpansInOneDrillFile_ApplyNeither_AndAreNamed()
    {
        string dir = Folder("two-spans");
        Write(dir, "l1.gtl", Pads("Copper,L1,Top,Signal", (2, 2), (4, 4)));
        Write(dir, "l2.g2", Pads("Copper,L2,Inr,Signal", (2, 2), (4, 4)));
        Write(dir, "l3.g3", Pads("Copper,L3,Inr,Signal", (2, 2), (4, 4)));
        Write(dir, "l4.gbl", Pads("Copper,L4,Bot,Signal", (2, 2), (4, 4)));
        Write(dir, "board.drl", Drill((2, 2), (4, 4)));
        Write(dir, "board.ipc", Netlist(1,
            Record("N1", (Counts(2), Counts(2)), drill: 300, access: 2),
            Record("N1", (Counts(2), Counts(2)), drill: 300, access: 3),
            Record("N2", (Counts(4), Counts(4)), drill: 300, access: 1),
            Record("N2", (Counts(4), Counts(4)), drill: 300, access: 4)));

        string all = All(Import(dir, "two_spans"));
        Assert.Contains("names 2 different layer spans", all, StringComparison.Ordinal);
        Assert.Contains("No layer span was declared", all, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 6 — net names on a COMPOSITED layer (R-gi5-6): the phase's headline
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ONE THAT GIVES SOMETHING BACK. Compositing unions every pad into the pour around it and
    /// destroys their identities permanently — but not their LOCATIONS, and a netlist coordinate
    /// still falls inside the region that swallowed the pad, so the region can be named.
    /// </summary>
    [Fact]
    public void NetNamesAreAttachedToTheCompositedRegionThatContainsTheCoordinate()
    {
        string dir = Folder("composited");
        Write(dir, "board.gtl", PourWithPads("Copper,L1,Top,Signal"));
        Write(dir, "board.ipc", Netlist(1, Record("GND", (Counts(5), Counts(5)), code: 327)));

        var result = Import(dir, "composited");

        // The layer really did composite — otherwise this test is measuring an ordinary flash.
        Assert.Contains("COMPOSITED", All(result), StringComparison.Ordinal);

        var named = Cell(result).Shapes.Where(s => s.Net == "GND").ToList();
        Assert.Single(named);
        Assert.IsType<PolygonShape>(named[0]);

        // R-gi5-12: the loss sentence stops claiming the net names are gone, because they are not.
        Assert.Contains("were recovered from the board netlist", All(result), StringComparison.Ordinal);
        Assert.DoesNotContain("identities and its per-object net names are gone", All(result),
                              StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 7 — ambiguity stays null (R-gi5-9)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A composited pour genuinely contains pads of more than one net. <c>LayoutShape.Net</c> is one
    /// nullable string, so it cannot honestly carry either — and a test asserting it picked one would
    /// be asserting the bug.
    /// </summary>
    [Fact]
    public void ARegionContainingTwoNets_CarriesNeither_AndIsCounted()
    {
        string dir = Folder("ambiguous");
        Write(dir, "board.gtl", PourWithPads("Copper,L1,Top,Signal"));
        Write(dir, "board.ipc", Netlist(1,
            Record("GND", (Counts(3), Counts(3)), code: 327),
            Record("VCC", (Counts(7), Counts(7)), code: 327)));

        var result = Import(dir, "ambiguous");

        Assert.All(Cell(result).Shapes, s => Assert.Null(s.Net));
        Assert.Contains("contain pads of more than one net and were left unnamed",
                        All(result), StringComparison.Ordinal);
    }

    /// <summary>R-gi5-8: several coordinates landing in ONE region is the expected outcome on a
    /// pour, and the report has to read that way rather than as a fault.</summary>
    [Fact]
    public void ManyCoordinatesOfOneNetInOneRegion_IsNotReportedAsAFault()
    {
        string dir = Folder("one-net-pour");
        Write(dir, "board.gtl", PourWithPads("Copper,L1,Top,Signal"));
        Write(dir, "board.ipc", Netlist(1,
            Record("GND", (Counts(3), Counts(3)), code: 327),
            Record("GND", (Counts(5), Counts(5)), code: 327),
            Record("GND", (Counts(7), Counts(7)), code: 327)));

        var result = Import(dir, "one_net_pour");

        Assert.Single(Cell(result).Shapes, s => s.Net == "GND");
        Assert.DoesNotContain("more than one net", All(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE BOUND ON HOW OFTEN GATE 7 FIRES, which is what says whether R-gi5-6's headline
    /// capability is worth anything on a real board. A pad of a second net inside a pour is separated
    /// from it by a CLEARANCE — and compositing turns that clearance into a HOLE in the pour, which
    /// makes the island inside it a separate top-level shape (<c>LayoutClipper.FromClipperTree</c>
    /// recurses into a hole's own islands). So two nets in one composited layer come back as two
    /// shapes with two names, and ambiguity needs the two nets to share ONE region — which is copper
    /// that is galvanically joined, and on a correctly drawn board is not a second net at all.
    /// </summary>
    [Fact]
    public void APadIsolatedInsideAPourByItsClearance_IsItsOwnRegion_AndBothNetsAreNamed()
    {
        string dir = Folder("island");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\nG01*\n" +
            "%LPD*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\n" +
            "%LPC*%\nG36*\nX6000000Y6000000D02*\nX6000000Y8000000D01*\nX8000000Y8000000D01*\n" +
            "X8000000Y6000000D01*\nX6000000Y6000000D01*\nG37*\n" +
            "%LPD*%\nG36*\nX6500000Y6500000D02*\nX6500000Y7500000D01*\nX7500000Y7500000D01*\n" +
            "X7500000Y6500000D01*\nX6500000Y6500000D01*\nG37*\nM02*\n");
        Write(dir, "board.ipc", Netlist(1,
            Record("GND", (Counts(3), Counts(3)), code: 327),        // in the pour
            Record("VCC", (Counts(7), Counts(7)), code: 327)));      // on the island inside its clearance

        var result = Import(dir, "island");
        var shapes = Cell(result).Shapes;

        Assert.Contains("COMPOSITED", All(result), StringComparison.Ordinal);
        Assert.Single(shapes, s => s.Net == "GND");
        Assert.Single(shapes, s => s.Net == "VCC");
        Assert.DoesNotContain("more than one net", All(result), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 8 — the scale cross-check (R-gi5-10)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE USUAL CATASTROPHE, and the reason the cross-check exists. A netlist read at the wrong
    /// scale that matches nothing is at least loud; one read at a wrong scale that still lands inside
    /// the board mislabels every pad on it, silently. The artwork's own extent is what separates the
    /// two — here a file whose header declares the coarse INCH resolution while its coordinates are
    /// metric counts, which puts every record 12 mm off a board that ends at 9.
    /// </summary>
    [Fact]
    public void ANetlistWhoseHeaderDisagreesWithItsCoordinates_IsSettledAgainstTheArtwork()
    {
        string dir = Folder("wrong-scale");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (5, 5), (9, 9)));
        Write(dir, "board.ipc", Netlist(0,                       // says inch; is written in metric counts
            Record("A", (Counts(5), Counts(5)), code: 327),
            Record("B", (Counts(9), Counts(9)), code: 327)));

        var result = Import(dir, "wrong_scale");
        string all = All(result);

        Assert.Contains("DISAGREES with the artwork", all, StringComparison.Ordinal);
        Assert.Contains("settled against the artwork instead", all, StringComparison.Ordinal);

        // And having been settled, it did its job.
        Assert.Contains(Cell(result).Shapes, s => s.Net == "A");
    }

    /// <summary>And when NO resolution the format defines puts the records on the board, the file is
    /// named and used for nothing. Matching it anyway is the one outcome this check exists to
    /// prevent.</summary>
    [Fact]
    public void ANetlistThatFitsAtNoResolution_IsReportedAndUsedForNothing()
    {
        string dir = Folder("no-fit");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1), (3, 3)));
        Write(dir, "board.ipc", Netlist(1,
            Record("A", (Counts(400), Counts(400)), code: 327),
            Record("B", (Counts(420), Counts(420)), code: 327)));

        var result = Import(dir, "no_fit");
        string all = All(result);

        Assert.Contains("No resolution this format defines puts its records on this board", all,
                        StringComparison.Ordinal);
        Assert.All(Cell(result).Shapes, s => Assert.Null(s.Net));
        Assert.Contains("none of them could be used", all, StringComparison.Ordinal);
    }

    /// <summary>A set with drill data and no artwork has no extent to check against, which is not a
    /// reason to refuse the netlist — the cross-check is evidence, not a precondition. But the
    /// strongest source having been UNAVAILABLE and the strongest source having AGREED are different
    /// answers, and the run has to say which one it got.</summary>
    [Fact]
    public void WithNoArtworkToCheckAgainst_TheNetlistIsStillUsed_AndTheAbsentCheckIsSaid()
    {
        string dir = Folder("drill-only");
        Write(dir, "board.drl", Drill((2, 2)));
        Write(dir, "board.ipc", Netlist(1,
            Record("", (Counts(2), Counts(2)), drill: 300, plated: false)));

        var result = Import(dir, "drill_only");
        string all = All(result);

        Assert.Contains("No cross-check against the artwork was possible", all, StringComparison.Ordinal);
        var entry = Assert.Single(TechOf(result).Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(entry.Plated);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 9 — the tolerance is STATED, and the count matched by it is reported (R-gi5-7)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Containment first, nearest-within-tolerance second, and both rungs counted. The tolerance is
    /// one count of the netlist's own coordinate resolution — derived rather than fixed, because the
    /// coarsest resolution this format defines is COARSER than the one-micron snap the drill pairing
    /// uses, so a fixed micron would have missed real matches on every inch-resolution set.
    /// </summary>
    [Fact]
    public void ACoordinateJustOutsideAPad_MatchesByTheNearRung_AndTheToleranceIsNamed()
    {
        string dir = Folder("near");
        // A 0.4 mm pad at (1, 1): its copper ends at x = 1.200 mm. The record sits one count — one
        // micron, at this netlist's metric resolution — beyond that edge.
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1)));
        Write(dir, "board.ipc", Netlist(1, Record("EDGE", (1201, 1000), code: 327)));

        var result = Import(dir, "near");
        string all = All(result);

        Assert.Contains("1 within 1 µm of one", all, StringComparison.Ordinal);
        Assert.Contains("0 coordinate(s) fell inside a shape", all, StringComparison.Ordinal);
        Assert.Contains(Cell(result).Shapes, s => s.Net == "EDGE");
    }

    /// <summary>And containment wins when it is available — a coordinate inside exactly one shape
    /// names that shape and never reaches the near rung.</summary>
    [Fact]
    public void ACoordinateInsideAPad_MatchesByContainment()
    {
        string dir = Folder("inside");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1)));
        Write(dir, "board.ipc", Netlist(1, Record("CORE", (1000, 1000), code: 327)));

        string all = All(Import(dir, "inside"));
        Assert.Contains("1 coordinate(s) fell inside a shape", all, StringComparison.Ordinal);
        Assert.Contains("0 within 1 µm of one", all, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 10 — round trip
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Net names are an ordinary <c>.clay</c> field and survive a save and load — and a
    /// re-export carries them into the artwork's own net attributes, which is what makes the recovery
    /// permanent rather than a display-only annotation.</summary>
    [Fact]
    public void NetNamesSurviveTheClayRoundTrip_AndReachAReExport()
    {
        string dir = Folder("round-trip");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (1, 1)));
        Write(dir, "board.ipc", Netlist(1, Record("RT", (1000, 1000), code: 327)));

        var result = Import(dir, "round_trip");

        string reloaded = Path.Combine(_root, "reloaded.clay");
        LayoutPersistence.SaveToFile(reloaded, Cell(result));
        Assert.Contains(LayoutPersistence.LoadFromFile(reloaded).Shapes, s => s.Net == "RT");

        using var exported = new MemoryStream();
        GerberWriter.Write(exported, null, Cell(result).Shapes, GerberUnits.Resolve(Dbu), null,
                           new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("%TO.N,RT*%", System.Text.Encoding.ASCII.GetString(exported.ToArray()),
                        StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 11 — the reader is in src/Design and touches no UI framework
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The assembly-wide proof is <c>tests/Firewall.Tests</c>; this is the one line that
    /// says which side of it the new reader landed on, so a move that quietly re-homes it fails
    /// here.</summary>
    [Fact]
    public void TheNetlistReaderLivesInTheDesignAssembly()
    {
        var assembly = typeof(BoardNetlistFile).Assembly;
        Assert.Equal("CircuitRF.Design", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                              a => a.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Classification, and the reader on its own
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A fifth file kind, recognised BY CONTENT exactly as the other four are — and one
    /// record is not a netlist, for the same reason one keyword is not a declaration.</summary>
    [Fact]
    public void ANetlistIsAFifthFileKind_RecognisedByContentAndNotByExtension()
    {
        var real = GerberFileClassifier.ClassifyContent(
            "anything.xyz", Netlist(1, Record("A", (1000, 1000)), Record("B", (2000, 2000))));
        Assert.Equal(GerberFileKind.Netlist, real.Kind);

        // One record with a header IS a netlist — that pair is a signature nothing else writes.
        Assert.Equal(GerberFileKind.Netlist,
                     GerberFileClassifier.ClassifyContent("one", Netlist(1, Record("A", (1000, 1000)))).Kind);

        // One record with NO header is a line, not a file.
        Assert.Equal(GerberFileKind.Other,
                     GerberFileClassifier.ClassifyContent("bare", Record("A", (1000, 1000))).Kind);

        // And nothing here can take a file the drill or artwork tests already claimed — gate 2's own
        // doctrine, restated for the new kind.
        Assert.Equal(GerberFileKind.Drill,
                     GerberFileClassifier.ClassifyContent("board.ipc", Drill((1, 1))).Kind);
        Assert.Equal(GerberFileKind.Artwork,
                     GerberFileClassifier.ClassifyContent("board.ipc", Pads("Copper,L1,Top,Signal", (1, 1))).Kind);
    }

    /// <summary>The units record, and the default when there is none — stated as a default, never
    /// silently, which is the GI series' standing rule 2.</summary>
    [Fact]
    public void TheUnitsRecordIsRead_AndItsAbsenceIsReportedAsADefaultRatherThanAsAFact()
    {
        var metric = BoardNetlistFile.Read("n.ipc", Netlist(1, Record("A", (1000, 1000)), Record("B", (2000, 2000))), Dbu);
        Assert.Equal(BoardNetlistUnits.MillimetreThousandth, metric.Units);
        Assert.Equal(BoardNetlistUnitsEvidence.Declared, metric.UnitsEvidence);
        Assert.Equal(1_000_000, metric.Records[0].X);                   // 1 mm

        var inch = BoardNetlistFile.Read("n.ipc", Netlist(0, Record("A", (1000, 1000)), Record("B", (2000, 2000))), Dbu);
        Assert.Equal(2_540_000, inch.Records[0].X);                     // 0.1 in

        string headerless = Record("A", (1000, 1000)) + Record("B", (2000, 2000));
        var defaulted = BoardNetlistFile.Read("n.ipc", headerless, Dbu);
        Assert.Equal(BoardNetlistUnitsEvidence.Defaulted, defaulted.UnitsEvidence);
        Assert.Contains("the format's own default", defaulted.UnitsSummary, StringComparison.Ordinal);
    }

    /// <summary>Conductor route and board-outline records are counted and NOT read. §7: building a
    /// connectivity model from this data is a different piece of work, and the artwork is the sole
    /// source of every coordinate here.</summary>
    [Fact]
    public void RouteAndOutlineRecordsAreCountedAndNotUsed()
    {
        var netlist = BoardNetlistFile.Read("n.ipc", Netlist(1,
            Record("A", (1000, 1000), code: 317, drill: 300),
            Record("B", (2000, 2000), code: 378),
            Record("C", (3000, 3000), code: 389)), Dbu);

        Assert.Single(netlist.Records);
        Assert.Equal(1, netlist.ConductorRecords);
        Assert.Equal(1, netlist.OutlineRecords);
        Assert.Contains(netlist.Diagnostics, d => d.Contains("NOT used", StringComparison.Ordinal));
    }

    /// <summary>R-gi5-3 is read LITERALLY: a reference with no pin settles neither way. A writer that
    /// puts a marker word in the reference field of a via would otherwise have every via on the board
    /// counted as a component hole, silently — so that case is counted and said instead.</summary>
    [Fact]
    public void AReferenceWithNoPin_SettlesNeitherWay_AndIsCounted()
    {
        string dir = Folder("no-pin");
        Write(dir, "board.gtl", Pads("Copper,L1,Top,Signal", (2, 2)));
        Write(dir, "board.drl", Drill((2, 2)));
        Write(dir, "board.ipc", Netlist(1,
            Record("N1", (Counts(2), Counts(2)), component: "VIA", drill: 300)));

        string all = All(Import(dir, "no_pin"));
        Assert.Contains("carrying a component reference but no pin", all, StringComparison.Ordinal);
        Assert.Contains("still not available for these", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// A net name longer than the record's fourteen-column field is written into the header and the
    /// record carries an alias. A reader that ignores that table reports a net name that is WRONG
    /// rather than missing, and nothing downstream would question it — which is why both spellings
    /// of the header record are read.
    /// </summary>
    [Fact]
    public void ANetNameTooLongForItsField_IsExpandedFromTheHeaderTable()
    {
        const string longName = "POWER_RAIL_3V3_FILTERED";
        Assert.True(longName.Length > 14);

        foreach (string header in new[]
                 {
                     $"P  NNAME1    {longName}\n",          // the alias as the keyword
                     $"P  NNAME     1 {longName}\n",        // the alias split into keyword and index
                 })
        {
            var netlist = BoardNetlistFile.Read("n.ipc",
                "P  UNITS     CUST 1\n" + header +
                Record("NNAME1", (1000, 1000), code: 327) +
                Record("PLAIN", (2000, 2000), code: 327) + "999\n", Dbu);

            Assert.Equal(longName, netlist.Records[0].Net);
            Assert.Equal("PLAIN", netlist.Records[1].Net);        // an ordinary name is untouched
            Assert.Contains(netlist.Diagnostics, d => d.Contains("alias", StringComparison.Ordinal));
        }
    }

    /// <summary>R-gi5-11. The artwork's own net attribute outranks the netlist — a file is
    /// authoritative about itself — and the disagreement is counted rather than resolved.</summary>
    [Fact]
    public void WhenTheArtworkAlreadyNamesANet_ItsNameIsKept_AndTheDisagreementIsCounted()
    {
        string dir = Folder("net-skew");
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\nG01*\n%ADD10C,0.400*%\nD10*\n" +
            "%TO.N,FROM_ARTWORK*%\nX1000000Y1000000D03*\n" +
            "%TO.N,AGREED*%\nX3000000Y3000000D03*\nM02*\n");
        Write(dir, "board.ipc", Netlist(1,
            Record("FROM_NETLIST", (1000, 1000), code: 327),
            Record("AGREED", (3000, 3000), code: 327)));

        var result = Import(dir, "net_skew");

        Assert.Contains(Cell(result).Shapes, s => s.Net == "FROM_ARTWORK");
        Assert.DoesNotContain(Cell(result).Shapes, s => s.Net == "FROM_NETLIST");
        Assert.Contains("1 shape(s) already carry a net name from the artwork", All(result),
                        StringComparison.Ordinal);
        Assert.Contains("1 already carried the same name", All(result), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-gi5-13 — the one summary line
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OneSummaryLineCarriesEveryCountThatMatters()
    {
        string dir = Folder("summary");
        Write(dir, "l1.gtl", Pads("Copper,L1,Top,Signal", (2, 2), (5, 5)));
        Write(dir, "l2.gbl", Pads("Copper,L2,Bot,Signal", (2, 2), (5, 5)));
        Write(dir, "board.drl", Drill((2, 2), (5, 5)));
        Write(dir, "board.ipc", Netlist(1,
            Record("VCC", (Counts(2), Counts(2)), drill: 300, access: 0),
            Record("GND", (Counts(5), Counts(5)), component: "R1", pin: "1", drill: 300, access: 0)));

        string line = Assert.Single(Import(dir, "summary").Messages, m => m.StartsWith("Board netlist:", StringComparison.Ordinal));

        foreach (string fragment in new[]
                 {
                     "2 record(s) read", "board.ipc", "naming 2 net(s)",
                     "1 classified as vias", "1 as component holes", "0 not covered by it",
                     "matched by containment", "matched\nnothing".Replace('\n', ' '),
                     "attached", "left unnamed", "Layer spans found: 1-2",
                 })
            Assert.Contains(fragment, line, StringComparison.Ordinal);
    }
}
