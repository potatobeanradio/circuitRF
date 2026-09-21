// ================================================================
//  RailPartDiscoveryTests.cs — brief-railrf-26-parts-from-the-board.md §8
//
//  THE PARTS TABLE HAD NO PRODUCER. Not "it lost some rows": `new RailPart` appeared nowhere in
//  src/ except the reader, `RailPartOrigin.Bom` was assigned by nothing at all, and the window had
//  no add-part gesture — so the only way a part had ever appeared in railRF was somebody writing
//  the JSON, and a board with fifty-five placed footprints opened with column headings over
//  nothing.
//
//  ── THE FIRST TEST IS THE ONE THAT MATTERS ────────────────────────────────────────────────────
//
//  R-rail26-2's classification table is the whole correctness question. The failure it exists to
//  prevent is a series resistor, a ferrite or a LOAD turning into a shunt capacitor with a
//  library-defaulted ESR: the curve that comes out of that is smooth, plausible and wrong, and
//  nothing on the window would say so. Every other gate here would pass just as happily against a
//  discovery that offered everything it found.
//
//  So test 1 is one synthetic board carrying one of each row of that table, and it asserts what is
//  NOT offered as hard as what is. The pull-up resistor on it — one pad on the rail, one on a
//  signal net, BOTH over the ground plane — is the case pure containment cannot see, and it is
//  there because the plane sits under every pad on a real board.
//
//  The window gates run on the SHIPPED example, because that is a real board with real pads, a real
//  netlist and a real placement file, and a synthetic stand-in for it would only prove that the
//  stand-in matched.
//
//  One test per CLAIM the brief makes, not one per row.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailPartDiscoveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-disc-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ THE SYNTHETIC BOARD ═══════════════════════════════════════════════════════════════════
    //
    // Two conductors and one core: a rail pour on TOP and a reference plane on BOT that reaches
    // under everything, which is what makes containment alone ambiguous and is why every real board
    // with an inner plane has this shape.

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private const double CopperSigma = 5.8e7;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static Technology Tech()
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static Path64 Box(long x1, long y1, long x2, long y2) =>
        [new Point64(x1, y1), new Point64(x2, y1), new Point64(x2, y2), new Point64(x1, y2)];

    /// <summary>One pad of each row of R-rail26-2's table, on one board.</summary>
    private static IReadOnlyList<PdnPad> Pads() =>
    [
        // Two caps, rail → reference. THE ONLY TWO THAT MAY BE OFFERED.
        new PdnPad("C1", "1", "VDD", Mm(2),  Mm(1), PdnPadSource.BoardNetlist),
        new PdnPad("C1", "2", "GND", Mm(2),  Mm(3), PdnPadSource.BoardNetlist),
        new PdnPad("C2", "1", "VDD", Mm(4),  Mm(1), PdnPadSource.BoardNetlist),
        new PdnPad("C2", "2", "GND", Mm(4),  Mm(3), PdnPadSource.BoardNetlist),

        // A resistor spanning the rail twice — a series element, whose terminals are the user's
        // statement about the topology and not something to infer.
        new PdnPad("R1", "1", "VDD", Mm(6),  Mm(1), PdnPadSource.BoardNetlist),
        new PdnPad("R1", "2", "VDD", Mm(8),  Mm(1), PdnPadSource.BoardNetlist),

        // A cap across the reference alone. NO pad on this rail — not this rail's business.
        new PdnPad("C3", "1", "GND", Mm(10), Mm(3), PdnPadSource.BoardNetlist),
        new PdnPad("C3", "2", "GND", Mm(12), Mm(3), PdnPadSource.BoardNetlist),

        // A three-pad regulator with a pad on the rail. A LOAD, never a capacitor.
        new PdnPad("U1", "VDD", "VDD", Mm(14), Mm(1), PdnPadSource.BoardNetlist),
        new PdnPad("U1", "GND", "GND", Mm(14), Mm(3), PdnPadSource.BoardNetlist),
        new PdnPad("U1", "OUT", "SDA", Mm(16), Mm(3), PdnPadSource.BoardNetlist),

        // THE PULL-UP. Geometrically identical to C1 and C2 — one pad on the rail pour, one over
        // the plane — and it is not decoupling. Only its NET says so.
        new PdnPad("R2", "1", "VDD", Mm(18), Mm(1), PdnPadSource.BoardNetlist),
        new PdnPad("R2", "2", "SDA", Mm(18), Mm(3), PdnPadSource.BoardNetlist),
    ];

    private static PdnRailRegionSet Regions() =>
        PdnRailRegions.Walk(
            new Dictionary<LayerKey, Paths64>
            {
                [Top] = [Box(0, 0, Mm(20), Mm(2))],                       // the rail pour
                [Bot] = [Box(Mm(-2), Mm(-5), Mm(22), Mm(5))],             // the reference plane
            },
            Tech(),
            [.. Pads().Select(p => new PdnNetPoint(p.Net!, p.X, p.Y))],
            railNet: "VDD",
            referenceLayer: Bot,
            referenceNet: "GND",
            extraRailSeeds: []);

    private static RailSpec Rail() => new() { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };

    private static RailDiscoveryRequest Request(BomTable? bom = null, RailSpec? rail = null) => new()
    {
        Rail         = rail ?? Rail(),
        Pads         = Pads(),
        Regions      = Regions(),
        Bom          = bom,
        ReferenceNet = "GND",
    };

    // ══ 1 — THE CLASSIFICATION TABLE, IN ONE FIXTURE ══════════════════════════════════════════

    /// <summary>
    /// <b>R-rail26-2, every row of it.</b> Offered: exactly the two caps. Everything else that
    /// touches the rail comes back in the skipped list with its own reason, and the part that does
    /// not touch it is silent.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that says a load never becomes a capacitor.</b> A three-pad regulator
    /// offered as decoupling would be given a library-defaulted ESR and a plausible resonance, and
    /// nothing anywhere on the window would say the curve was of a circuit the board is not.
    ///
    /// <para><b>R2 is the case containment alone cannot see</b> — a pull-up, one pad on the rail
    /// pour and one over the same ground plane every other pad on the board sits over. The region
    /// set carries only the reference LAYER's copper, so the exact galvanic-ambiguity test
    /// <c>PdnRailRegions.ReferenceNetOn</c> uses is not available from it; the pad's own NET is what
    /// vetoes it. On a board that names no nets the veto cannot fire and this part would be
    /// offered — stated in <c>RailPartDiscovery</c>'s header rather than discovered later.</para>
    /// </remarks>
    [Fact]
    public void TheTable_OffersTheTwoCaps_AndNamesWhyForEveryPartThatTouchesTheRail()
    {
        var found = RailPartDiscovery.Discover(Request());
        foreach (string line in found.Lines) output.WriteLine(line);

        Assert.Equal(RailDiscoveryState.Discovered, found.State);
        Assert.Equal(["C1", "C2"], found.Offered.Select(c => c.Refdes));

        // The other three that TOUCH the rail, each with its own reason.
        Assert.Equal(["R1", "R2", "U1"], found.Skipped.Select(s => s.Refdes));
        Assert.Equal(RailDiscoverySkip.SpansTheRail,
                     found.Skipped.Single(s => s.Refdes == "R1").Reason);
        Assert.Equal(RailDiscoverySkip.NotOnTheReturnPath,
                     found.Skipped.Single(s => s.Refdes == "R2").Reason);
        Assert.Equal(RailDiscoverySkip.MoreThanTwoPads,
                     found.Skipped.Single(s => s.Refdes == "U1").Reason);

        // The cap across the reference alone is in NEITHER list — "no pad on the rail" is the one
        // row of the table that is SILENT, because a board's other nets are not this rail's
        // business and listing them would bury the three that are.
        Assert.DoesNotContain("C3", found.Skipped.Select(s => s.Refdes));
        Assert.DoesNotContain("C3", found.Offered.Select(c => c.Refdes));

        // R-rail26-4: both counts are on the face, because a designer told that two were added and
        // not that three were skipped cannot tell whether their bulk capacitor is one of the three.
        Assert.Contains("2 two-terminal parts", found.OfferSentence);
        Assert.Contains("3 more parts", found.SkippedSentence);
        Assert.Contains("1 spans the rail twice", found.SkippedSentence);
        Assert.Contains("1 has more than two pads", found.SkippedSentence);

        // R-rail26-3a. NULL, and that is the whole precedence: RailPartResolver takes a typed
        // mounting inductance over a computed one, so a number written onto the row here would
        // freeze today's via geometry into one a re-layout could no longer move — and
        // RailMountingBasis would then report it as typed.
        Assert.All(found.Parts, p => Assert.Null(p.MountingInductanceHenries));

        // Shunt, which is what a decoupling row IS, and fitted.
        Assert.All(found.Parts, p => Assert.Equal(RailPartConnection.Shunt, p.Connection));
        Assert.All(found.Parts, p => Assert.True(p.Mounted));
    }

    // ══ 2 — THE PART NUMBER COMES FROM THE BOM OR FROM NOWHERE ════════════════════════════════

    /// <summary>
    /// <b>R-rail26-3, all three of its bullets.</b> One BOM row fills the part number and marks the
    /// origin; two rows for one refdes leave it EMPTY rather than choosing; no BOM at all leaves it
    /// empty with <see cref="RailPartOrigin.Artwork"/>.
    /// </summary>
    /// <remarks>
    /// <b>And nothing is ever derived from the footprint</b> (§8 test 3). An 0402 land pattern is a
    /// case size; it is not a capacitance, an ESR or a dielectric class, and a row invented from one
    /// would be exactly the defaulted number this tool marks and counts everywhere else. So the
    /// board says <i>C1 is here and it bridges the rail</i> and stops — the row is listed AS
    /// unresolved, which is a state the parts table already renders.
    ///
    /// <para>The two-row case is <c>RebuildParts</c>' own rule arriving one step earlier: one
    /// internal part number sits in front of a list of approved manufacturers, and taking the first
    /// silently is what produces a plausible model from the WRONG manufacturer's part. Discovery
    /// must not undo that by choosing here.</para>
    /// </remarks>
    [Fact]
    public void ThePartNumber_ComesFromTheBom_OrFromNowhere_AndNeverFromTheFootprint()
    {
        // ── no BOM ────────────────────────────────────────────────────────────────────────────
        foreach (var part in RailPartDiscovery.Discover(Request()).Parts)
        {
            Assert.Equal("", part.PartNumber);
            Assert.Equal(RailPartOrigin.Artwork, part.Origin);
        }

        // ── one row for C1, two for C2 ────────────────────────────────────────────────────────
        var bom = Bom(
            new BomRow("C1", "PN-100N", "100n", "0402", "X7R 100nF 0402 16V"),
            new BomRow("C2", "PN-1U-A", "1u",   "0603", "X7R 1uF 0603 10V"),
            new BomRow("C2", "PN-1U-B", "1u",   "0603", "X7R 1uF 0603 10V"));

        var found = RailPartDiscovery.Discover(Request(bom));
        foreach (string line in found.Lines) output.WriteLine(line);

        var c1 = found.Parts.Single(p => p.Refdes == "C1");
        Assert.Equal("PN-100N", c1.PartNumber);
        Assert.Equal(RailPartOrigin.Bom, c1.Origin);

        var c2 = found.Parts.Single(p => p.Refdes == "C2");
        Assert.Equal("", c2.PartNumber);
        Assert.Equal(RailPartOrigin.Artwork, c2.Origin);
        Assert.Contains(found.Notes, n => n.Contains("C2", StringComparison.Ordinal));

        // NOTHING was taken from the land pattern, which both rows name and neither carries.
        Assert.DoesNotContain(found.Parts, p => p.PartNumber.Contains("0402", StringComparison.Ordinal));
        Assert.DoesNotContain(found.Parts, p => p.PartNumber.Contains("0603", StringComparison.Ordinal));
    }

    private static BomTable Bom(params BomRow[] rows) =>
        new("bom.csv", null, ',', rows, rows.Length, 0, [], []);

    // ══ 3 — IT LANDS AFTER THE FIRST SOLVE, NOT AT IMPORT ═════════════════════════════════════

    /// <summary>
    /// <b>R-rail26-2b.</b> Both halves of the predicate come from the extraction, so with no regions
    /// there is nothing to discover — and the state says WHY rather than coming back as an empty
    /// offer indistinguishable from a board with nothing on it.
    /// </summary>
    /// <remarks>
    /// <b>Derived from whether the regions are THERE, not from whether the reference was
    /// confirmed.</b> A rail whose run refused has a confirmed reference and no regions, and an
    /// offer computed from the confirmation alone would come up empty with nothing saying why.
    /// </remarks>
    [Fact]
    public void WithNoRegions_NothingIsDiscovered_AndTheStateSaysWhy()
    {
        var none = RailPartDiscovery.Discover(new RailDiscoveryRequest
        {
            Rail = Rail(), Pads = Pads(), Regions = null, ReferenceNet = "GND",
        });

        Assert.Equal(RailDiscoveryState.NotExtracted, none.State);
        Assert.False(none.HasOffer);
        Assert.Empty(none.Skipped);
        Assert.Empty(none.Lines);

        // A walk that found no rail copper is the same state — the reference may well be confirmed.
        var empty = RailPartDiscovery.Discover(new RailDiscoveryRequest
        {
            Rail    = Rail(),
            Pads    = Pads(),
            Regions = new PdnRailRegionSet([], [], "No copper was found on any layer.", []),
        });
        Assert.Equal(RailDiscoveryState.NotExtracted, empty.State);
    }

    // ══ 4 — THE WINDOW: IDEMPOTENT, AND ONE UNDO ══════════════════════════════════════════════

    /// <summary>
    /// <b>R-rail26-4a and R-rail26-4b, on the shipped example with its rows taken out.</b> Every one
    /// of its fourteen hand-authored parts is found again off the board, adding is idempotent, and
    /// ONE undo takes the whole batch back.
    /// </summary>
    /// <remarks>
    /// <b>The example is the oracle.</b> Its fourteen rows were typed by hand against this very
    /// artwork, so "discovery finds them" is a comparison against an independently-written answer
    /// rather than against discovery's own output.
    ///
    /// <para><b>One undo is not a convenience.</b> Twenty-four separate entries would be
    /// twenty-four presses to take back one button — <c>SetPartsMounted</c> has already settled that
    /// a batch is the real gesture, and this rides the same <c>QueueResolve</c> funnel the undo
    /// stack hangs off, so there is no per-site push to forget.</para>
    /// </remarks>
    [Fact]
    public void AddingIsIdempotent_AndOneUndoTakesTheWholeBatchBack()
    {
        var (vm, typed) = StrippedExample();
        vm.RunCommand.Execute(null);

        output.WriteLine(string.Join("\n", vm.PartOffer.Lines));

        // Found off the board: exactly the thirteen shunt rows somebody typed against this artwork.
        // The ferrite that was left in is neither offered nor reported — R-rail26-4a, a refdes the
        // rail already carries is not something to tell the user about.
        Assert.Equal(typed.OrderBy(r => r, StringComparer.OrdinalIgnoreCase),
                     vm.PartOffer.Offered.Select(c => c.Refdes));
        Assert.DoesNotContain("FB1", vm.PartOffer.Offered.Select(c => c.Refdes));
        Assert.DoesNotContain("FB1", vm.PartOffer.Skipped.Select(s => s.Refdes));

        Assert.True(vm.HasPartOffer);
        int offered = vm.PartOffer.Offered.Count;
        int kept = vm.SelectedRail!.Parts.Count;

        Assert.Equal(offered, vm.AddDiscoveredParts());
        Assert.Equal(kept + offered, vm.SelectedRail!.Parts.Count);

        // R-rail26-4a. Pressing it again adds nothing, and there is nothing left to press.
        Assert.Equal(0, vm.AddDiscoveredParts());
        Assert.False(vm.HasPartOffer);

        // R-rail26-4b. ONE entry, for the whole batch.
        Assert.True(vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.Equal(kept, vm.SelectedRail!.Parts.Count);
        Assert.False(vm.CanUndo);
    }

    // ══ 5 — THE EMPTY PANE SAYS WHERE ROWS COME FROM ══════════════════════════════════════════

    /// <summary>
    /// <b>R-rail26-5.</b> The sentence in an empty parts table changes with the state it is in.
    /// </summary>
    /// <remarks>
    /// <b>This is the part of the report that is a defect on its own.</b> The card was column
    /// headings over nothing with no sentence anywhere saying why, and a pane that is empty and
    /// silent reads as a broken pane — which is what was reported.
    /// </remarks>
    [Fact]
    public void TheEmptyPaneSentence_ChangesWithTheState()
    {
        // ── no rail ───────────────────────────────────────────────────────────────────────────
        var bare = new RailRfViewModel(new RailDocument { Name = "empty" }, null)
        {
            PostToUi = a => a(), RunOffThread = (work, _) => Task.FromResult(work()),
        };
        Assert.Contains("No rail yet", bare.PartsEmptyText);

        // ── a rail, no board ──────────────────────────────────────────────────────────────────
        // The ferrite the solve needs is taken out for THIS step only, because the sentence is
        // about an EMPTY table and a document with one row has nothing to say about where rows
        // come from — it already has one.
        var (vm, _) = StrippedExample(loadReferences: false);
        vm.SelectedRail!.Parts.Clear();
        vm.RebuildParts();
        Assert.Contains("No board yet", vm.PartsEmptyText);

        // ── a board, nothing extracted yet ────────────────────────────────────────────────────
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        Assert.Contains("has not been extracted yet", vm.PartsEmptyText);

        // ── extracted, and there is something to add ──────────────────────────────────────────
        // The ferrite goes back for the solve that needs it — its DC resistance is what bridges
        // this rail's two galvanically separate regions — and comes out again straight after, so
        // the pane is genuinely empty while the regions are there.
        var (withFerrite, _) = StrippedExample();
        withFerrite.RunCommand.Execute(null);
        vm = withFerrite;
        vm.SelectedRail!.Parts.Clear();
        vm.RebuildParts();

        Assert.Contains("add the", vm.PartsEmptyText);
        Assert.Contains($"{vm.PartOffer.Offered.Count}", vm.PartsEmptyText!);

        // And the ferrite, no longer a row, IS reported now — it spans the rail twice, so it is a
        // series element and never a shunt capacitor (R-rail26-2).
        Assert.Contains(vm.PartOffer.Skipped,
                        k => k.Refdes == "FB1" && k.Reason == RailDiscoverySkip.SpansTheRail);

        // ── extracted, and there is not ───────────────────────────────────────────────────────
        // The rail's own net name is moved off the board, so no pad lands on rail copper and every
        // part is silent. The REGIONS are untouched — they are keyed by rail name on the result —
        // so this is the discovered-and-found-nothing state and not the not-extracted one.
        vm.SelectedRail!.NetName = "no such net";
        vm.RebuildParts();
        Assert.Equal(RailDiscoveryState.Discovered, vm.PartOffer.State);
        Assert.Contains("No two-terminal part sits between", vm.PartsEmptyText);

        // And it is silent the moment there IS a row, which is every ordinary document.
        vm.SelectedRail!.Parts.Add(new RailPart { Refdes = "C99" });
        vm.RebuildParts();
        Assert.Null(vm.PartsEmptyText);
    }

    // ══ 6 — THE VERB AND THE WINDOW AGREE ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-rail26-1 and R-rail26-7, on one document.</b> The same refdeses the window is offering
    /// are the ones <c>circuitrf rail</c> prints — and the verb writes NONE of them into the
    /// <c>.crail</c>.
    /// </summary>
    /// <remarks>
    /// <b>That is why discovery is in <c>src/Design</c> and not in the view model.</b> Two surfaces
    /// coming to different conclusions about one board is the divergence this whole rule exists
    /// against, and it is what lets an out-of-process agent see the parts the window is offering and
    /// write them itself — <c>Authoring.cs</c>' standing rule that once a document exists the way to
    /// change it is to WRITE it.
    /// </remarks>
    [Fact]
    public void TheVerbPrintsWhatTheWindowOffers_AndWritesNoneOfIt()
    {
        var (vm, _) = StrippedExample();
        vm.RunCommand.Execute(null);

        string crail = vm.DocumentPath!;
        string before = File.ReadAllText(crail);

        var (exit, stdout, stderr) = RunCli("rail", crail);
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        // The headline, verbatim from the same sentence the window shows.
        Assert.Contains(vm.PartOffer.OfferSentence, stderr, StringComparison.Ordinal);

        // And every refdes, on the verb's own naming line — a count alone cannot be written back.
        foreach (string refdes in vm.PartOffer.Offered.Select(c => c.Refdes))
            Assert.Contains(refdes, stderr, StringComparison.Ordinal);

        // R-rail26-7: it REPORTS. The document is the bytes it was.
        Assert.Equal(before, File.ReadAllText(crail));
    }

    // ══ 7 — THE SHIPPED EXAMPLE IS UNCHANGED ══════════════════════════════════════════════════

    /// <summary>
    /// <b>§8 test 7.</b> Its fourteen typed rows stay fourteen and nothing is offered twice —
    /// R-rail26-4a on the one document every other railRF gate in this repository is driven on.
    /// </summary>
    /// <remarks>
    /// <b>The DataSet is not compared here.</b> Discovery writes nothing, so the run cannot move:
    /// what would make the example's numbers change is a row being ADDED, and this is the gate that
    /// says none is. <c>PowerRailExampleTests</c> holds the published figures themselves.
    /// </remarks>
    [Fact]
    public void TheShippedExample_KeepsItsFourteenRows_AndIsOfferedNothing()
    {
        var vm = Example(ExamplePath());
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RunCommand.Execute(null);

        Assert.Equal(14, vm.SelectedRail!.Parts.Count);
        Assert.Equal(14, vm.Parts.Count);
        Assert.Null(vm.PartsEmptyText);

        Assert.False(vm.HasPartOffer);
        Assert.Equal(0, vm.AddDiscoveredParts());
        Assert.Equal(14, vm.SelectedRail!.Parts.Count);

        // Every one of them was TYPED and stays typed — nothing here rewrites an origin.
        Assert.All(vm.SelectedRail!.Parts, p => Assert.Equal(RailPartOrigin.Typed, p.Origin));
    }

    // ── the fixtures ──────────────────────────────────────────────────────────────────────────

    /// <summary>The shipped example, copied to a scratch tree with every part row taken out.</summary>
    /// <remarks>
    /// <b>Copied rather than edited in place</b> — these gates add rows and save nothing, but a test
    /// that writes to <c>examples/</c> is one bad day from a committed diff nobody meant.
    /// </remarks>
    private (RailRfViewModel Vm, IReadOnlyList<string> Typed) StrippedExample(
        bool loadReferences = true)
    {
        string source = Path.GetDirectoryName(Path.GetDirectoryName(ExamplePath()))!;
        string dest = Path.Combine(_root, "Power Rail");
        CopyTree(source, dest);

        string crail = Path.Combine(dest, "Sensor board", "Sensor board.crail");
        var doc = RailDocumentIo.LoadFromFile(crail);
        var rail = doc.Rails[0];

        // THE SERIES ELEMENT STAYS. FB1's DC resistance is what bridges this rail's two galvanically
        // separate regions, so a document without it is a board whose load is not reachable at all —
        // the fast model refuses it by name, no extraction happens, and there are no regions for
        // anything here to be about. Taking out the thirteen SHUNT rows is the state this brief is
        // about; taking out the ferrite as well is a different and broken board.
        var typed = rail.Parts.Where(p => !p.IsSeries).Select(p => p.Refdes).ToList();
        for (int i = rail.Parts.Count - 1; i >= 0; i--)
            if (!rail.Parts[i].IsSeries) rail.Parts.RemoveAt(i);

        File.WriteAllText(crail, RailDocumentIo.SerializeUnvalidated(doc));

        var vm = Example(crail);
        if (loadReferences) Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        return (vm, typed);
    }

    private static RailRfViewModel Example(string crail)
    {
        Assert.True(File.Exists(crail), $"no .crail at {crail}");
        return new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
    }

    private static string ExamplePath() => Path.Combine(
        RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(RailPartDiscoveryTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
