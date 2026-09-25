// ================================================================
//  LayoutNetlistTests.cs — the gate for brief-lvs-3-layout-netlist.md §8.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  A `.clay` plus its technology becomes an LvsNetlist: devices with named terminals, nets that
//  came from GEOMETRY ALONE, and ground. Flat, one technology, no comparison.
//
//  Every net assertion is against hand arithmetic laid out in the fixture — never against another
//  circuitRF path, which would only prove the two agree. Every finding is asserted by DIAGNOSTIC
//  ID and never by prose (R-aut1-8): the id is the contract a caller filters on, and a test that
//  pins a sentence teaches people not to improve sentences.
//
//  ── ONE NARROWING OF THE BRIEF, AND IT HAS ITS OWN TEST ───────────────────────────────────────
//
//  R-lvs3-6b says every piece on a ground-reference conductor's drawing layers is net 0. Three of
//  the four shipped PCB technologies flag their BOTTOM COPPER as the reference, and a two-layer
//  board routes signals there — so read literally, every bottom-side trace becomes net 0 and the
//  most common shipped technology reads as one enormous short. Only an UNDRAWN reference is
//  inferred, which is the gap (G4) the note is actually about; ADrawnGroundReferenceIsOrdinaryCopper
//  is the gate on that, and src/Design/RESOLVED.md records why.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class LayoutNetlistTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs3-" + Guid.NewGuid().ToString("N")[..12]);

    public LayoutNetlistTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    private static readonly LayerKey Top  = new(1, 0);
    private static readonly LayerKey Bot  = new(2, 0);
    private static readonly LayerKey Silk = new(5, 0);   // declared, and NO stackup entry claims it
    private static readonly LayerKey ViaL = new(9, 0);

    // ══ 1 — the divider, against hand arithmetic ═════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> Two resistors in series: three nets, two devices, four terminals — and the
    /// middle net is the one both inner pads are on.
    /// </summary>
    /// <remarks>
    /// The arithmetic is in <see cref="Divider"/> and nowhere else: pad A of R1 sits alone, pads B
    /// of R1 and A of R2 are bridged by one polygon that reaches neither outer pad, and pad B of R2
    /// sits alone. Nothing about that reading comes from a schematic — there is not one.
    /// </remarks>
    [Fact]
    public void ATwoResistorDividerIsThreeNetsTwoDevicesAndFourTerminals()
    {
        var netlist = Read(Divider(0, mirror: false));

        Assert.Equal(["R1", "R2"], netlist.Devices.Select(d => d.Path));
        Assert.Equal(4, netlist.Devices.Sum(d => d.Terminals.Count));
        Assert.Equal(3, netlist.Nets.Count);

        int r1a = NetOf(netlist, "R1", "A"), r1b = NetOf(netlist, "R1", "B");
        int r2a = NetOf(netlist, "R2", "A"), r2b = NetOf(netlist, "R2", "B");

        Assert.Equal(r1b, r2a);                       // the middle net, and the only shared one
        Assert.Equal(4, new[] { r1a, r1b, r2b }.Distinct().Count() + 1);
        Assert.All(netlist.Nets, n => Assert.Equal(2, n.Pins.Count is 1 or 2 ? 2 : 0));
    }

    /// <summary>
    /// <b>Gate 2.</b> The same board at 90°, at 217° and mirrored: every terminal lands on the
    /// same net as before.
    /// </summary>
    /// <remarks>
    /// <b>The fixture's pins are off BOTH axes</b> — the WB-C trap in its second form. A mirror
    /// about a symmetric land is a no-op and a test of it proves nothing at all, so <c>A</c> sits
    /// at (-400, +150) µm and <c>B</c> at (+400, -250) µm: no reflection and no cardinal rotation
    /// maps either onto the other or onto itself.
    ///
    /// <para>217° is the non-cardinal case, which sends every rectangle through
    /// <c>LayoutRotationPromotion</c> on the way out of the flatten. The connectivity must survive
    /// a rectangle becoming a polygon, which is exactly what this asserts.</para>
    /// </remarks>
    [Theory]
    [InlineData(90.0, false)]
    [InlineData(217.0, false)]
    [InlineData(0.0, true)]
    public void RotationMirrorAndANonCardinalAngleLandOnTheSameNets(double deg, bool mirror)
    {
        Assert.Equal(Signature(Read(Divider(0, false))), Signature(Read(Divider(deg, mirror))));
    }

    // ══ 3, 4 — what a via joins ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3.</b> A pad on the top and a pad on the bottom are one net when a barrel bridges
    /// them, and two nets when it does not — an offset staircase of metal, with the two metals
    /// never unioned to each other.
    /// </summary>
    /// <remarks>
    /// <b>The second half is the oracle.</b> "They are one net" on its own is also what a board
    /// whose partition had collapsed into one piece would say. Reading the identical board with
    /// the barrel deleted is what makes the first reading mean the via.
    /// </remarks>
    [Fact]
    public void AViaJoinsTwoLayersAndWithoutItTheyAreTwoNets()
    {
        Assert.Single(Read(Staircase(withVia: true)).Nets);
        Assert.Equal(2, Read(Staircase(withVia: false)).Nets.Count);
    }

    /// <summary>
    /// <b>Gate 4.</b> A pin on an inner layer reaches the plane through a barrel that only
    /// <i>passes</i> it — every conductor the barrel crosses, not only its two span ends.
    /// </summary>
    /// <remarks>
    /// The defect this is about read a four-layer board's inner power plane as a galvanically
    /// separate island while the picture showed it plainly connected, and it could not fail loudly
    /// because an extra island is what railRF calls ordinary on imported artwork.
    /// </remarks>
    [Fact]
    public void PinsOnAnInnerLayerReachAPlaneTheBarrelOnlyPasses()
    {
        var netlist = Read(FourLayerStack());

        Assert.Equal(4, netlist.Devices.Count);
        Assert.Single(netlist.Devices.SelectMany(d => d.Terminals).Select(t => t.NetIndex).Distinct());
    }

    // ══ 5, 6 — ground ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> On the shipped MMIC technology a backside via reaches net <c>"0"</c>, and
    /// <c>lvs.ground.reference-undrawn</c> is reported with the right count.
    /// </summary>
    /// <remarks>
    /// <c>Backside Metal</c> is a conductor with <c>DrawingLayers: []</c>, so read naively every
    /// backside via terminates in mid-air and every grounded device on the one technology an MMIC
    /// designer is most likely to start from reads as open.
    ///
    /// <para>The INFO is the half that matters and the owner asked for it by name: the metal is
    /// not drawn, so there is nothing on the screen to look at and nothing to select. The first
    /// time the inference is wrong the design reads as perfectly connected, and that sentence is
    /// the only reason anyone would notice.</para>
    /// </remarks>
    [Fact]
    public void OnTheMmicTechnologyABacksideViaReachesNetZeroAndTheRunSaysSo()
    {
        var netlist = Read(GroundedDie(ShippedTechnologies.Load("mmic-GaAs_2LM_100um")));

        int net = NetOf(netlist, "Q1", "S");
        Assert.Equal("0", netlist.Nets[net].Label);

        var undrawn = Assert.Single(netlist.Notes, n => n.Id == "lvs.ground.reference-undrawn");
        Assert.Equal(2, undrawn.Arguments["vias"]);          // the fixture places exactly two
        Assert.Equal("Backside Metal", undrawn.Arguments["stackupEntry"]);
    }

    /// <summary>
    /// <b>Gate 6.</b> No ground-reference conductor: a warning, ground pins read as ordinary
    /// opens, and the run completes.
    /// </summary>
    /// <remarks>
    /// A warning and not an error, because a die with no backside metal — grounded only through
    /// bondwires to a package — is a real and correct design (R-lvs3-6d).
    /// </remarks>
    [Fact]
    public void WithNoGroundReferenceConductorTheRunCompletesAndNothingIsNetZero()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        foreach (var layer in tech.Stackup.Layers) layer.IsGroundReference = false;

        var netlist = Read(GroundedDie(tech));

        Assert.Contains(netlist.Notes, n => n.Id == "lvs.ground.no-reference-conductor");
        Assert.DoesNotContain(netlist.Notes, n => n.Id == "lvs.ground.reference-undrawn");
        Assert.DoesNotContain(netlist.Nets, n => n.Label == "0");
        Assert.NotEmpty(netlist.Devices);
    }

    /// <summary>
    /// <b>The narrowing, and the reason it exists.</b> A ground-reference conductor that DRAWS is
    /// ordinary copper: two unconnected bottom-side traces stay two nets.
    /// </summary>
    /// <remarks>
    /// Three of the four shipped PCB technologies flag their bottom copper, correctly — the flag
    /// was added so a microstrip's substrate resolution would find the right plane — and a
    /// two-layer board routes signals on that layer. R-lvs3-6b read literally makes every
    /// bottom-side trace net 0 and the whole board one short, with no symptom but a passing LVS.
    /// The shipped four-layer technology flags Bottom Copper beside its real inner plane, so no
    /// rule keyed on the flag alone can tell a plane from a routing layer.
    /// </remarks>
    [Fact]
    public void ADrawnGroundReferenceIsOrdinaryCopperAndNotOneNet()
    {
        var netlist = Read(TwoBottomTraces());

        Assert.Equal(2, netlist.Nets.Count);
        Assert.DoesNotContain(netlist.Nets, n => n.Label == "0");
        Assert.DoesNotContain(netlist.Notes, n => n.Id == "lvs.ground.reference-undrawn");
    }

    // ══ 7, 8, 9 — which instances are devices ════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 7.</b> One case of each classification rule, on one board: a <c>SchematicId</c>
    /// placement, a designator-only placement, a <c>PartKind</c> placement, a kit cell whose
    /// <c>.ccell</c> declares ports, a via-fence cell and an unclassified copper cell.
    /// </summary>
    /// <remarks>
    /// <b>The kit cell is the one that matters</b> (R-lvs3-3b): it carries no designator, no part
    /// kind and no schematic id, and it is a device purely because its <c>.ccell</c> says it has
    /// ports. That is what makes a user-authored PDK work with nothing registered anywhere.
    ///
    /// <para>The unclassified warning is <b>once per cell TYPE</b> and the fixture places it
    /// twice, because a via fence is one cell placed forty times and forty copies of one sentence
    /// is a report nobody reads.</para>
    /// </remarks>
    [Fact]
    public void EachClassificationRuleAnswersOnce()
    {
        var netlist = Read(ClassificationBoard());

        // "@2" is the PartKind placement and "@3" the kit cell: NEITHER carries a designator, so
        // the path is its position — which is the honest identity and is what the report will name.
        Assert.Equal(["@2", "@3", "R9", "U1"], netlist.Devices.Select(d => d.Path).Order());

        var unclassified = Assert.Single(netlist.Notes, n => n.Id == "lvs.cell.unclassified");
        Assert.Equal(2, unclassified.Arguments["placements"]);
        Assert.Equal("Blob", unclassified.Arguments["cellName"]);
    }

    /// <summary>
    /// <b>Gate 8.</b> Two placements sharing a designator is reported, and reported FIRST.
    /// </summary>
    /// <remarks>
    /// R-lvs3-3e. It makes tier 1 meaningless and every downstream finding derived from it
    /// misleading — a user chasing "R1 is on the wrong net" would be chasing whichever R1 the walk
    /// reached first. <c>DesignatorPool</c> already answered the question; nothing asked it at the
    /// right time.
    /// </remarks>
    [Fact]
    public void DuplicateDesignatorsAreReportedBeforeAnythingElse()
    {
        var netlist = Read(Divider(0, false, secondDesignator: "R1"));

        Assert.Equal("lvs.device.duplicate-designator", netlist.Notes[0].Id);
        Assert.Equal("R1", netlist.Notes[0].Arguments["designator"]);
        Assert.Equal(2, netlist.Notes[0].Arguments["count"]);
    }

    /// <summary>
    /// <b>Gate 9.</b> A 2×3 array of a device cell is six devices, all carrying one designator;
    /// a 1×20 array of a via cell is none.
    /// </summary>
    /// <remarks>
    /// The path is the identity and the designator is not (R-lvs3-4b) — every element of an array
    /// carries the same one, which is correct and is what <c>PlacedPins</c> already does for pads.
    /// The via fence is the shape that makes arrays look wrong if the classification is got wrong,
    /// so it is here rather than in gate 7.
    /// </remarks>
    [Fact]
    public void AnArrayOfADeviceCellIsOneDevicePerElementAndAnArrayOfAViaCellIsNone()
    {
        var netlist = Read(Arrays());

        Assert.Equal(
            ["R1[0,0]", "R1[0,1]", "R1[0,2]", "R1[1,0]", "R1[1,1]", "R1[1,2]"],
            netlist.Devices.Select(d => d.Path).Order());
        Assert.All(netlist.Devices, d => Assert.Equal("R1", d.Designator));
    }

    // ══ 10, 11, 12 — terminals and pins ══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 10.</b> A cell with no derivable terminal map is a device with no terminals, and it
    /// is still in the count.
    /// </summary>
    /// <remarks>
    /// R-lvs3-5a. Dropping it would make the two sides disagree about how many parts there are,
    /// for a reason the report never gave.
    /// </remarks>
    [Fact]
    public void ACellWithNoTerminalMapIsStillADeviceWithNoTerminals()
    {
        var netlist = Read(UnmappableDevice());

        var device = Assert.Single(netlist.Devices);
        Assert.Empty(device.Terminals);
        Assert.Contains(netlist.Notes, n => n.Id == "lvs.device.no-terminal-map");
    }

    /// <summary>
    /// <b>Gate 11.</b> A bonded terminal whose two pads land on different nets is reported, not
    /// resolved to one.
    /// </summary>
    /// <remarks>
    /// One terminal is one connection. Two source pads on two nets is either a break in the bond
    /// or a short, and nothing here can tell which — so nothing picks one.
    /// </remarks>
    [Fact]
    public void ABondedTerminalSplitAcrossTwoNetsIsReportedAndNotResolved()
    {
        var netlist = Read(SplitSourceFet());

        var split = Assert.Single(netlist.Notes, n => n.Id == "lvs.terminal.split-across-nets");
        Assert.Equal(2, split.Arguments["nets"]);
        Assert.Equal("S", split.Arguments["terminal"]);
    }

    /// <summary>
    /// <b>Gate 12.</b> A pin on a layer the stackup does not describe reports
    /// <c>lvs.pin.no-copper</c> <b>naming the layer</b>.
    /// </summary>
    /// <remarks>
    /// The name is the whole point. "Your technology does not call Silk a conductor" and "your pad
    /// is not connected" look identical from here and have entirely different fixes.
    /// </remarks>
    [Fact]
    public void APinOnALayerTheStackupDoesNotDescribeNamesTheLayer()
    {
        var netlist = Read(PadOnAnUnclaimedLayer());

        var open = Assert.Single(netlist.Notes, n => n.Id == "lvs.pin.no-copper");
        Assert.Equal("Silk", open.Arguments["layerName"]);
        Assert.Equal("5/0", open.Arguments["layer"]);
    }

    // ══ 13, 14 — the two refusals ════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 13.</b> Nothing a schematic says can change the answer — the same board read with a
    /// correct schematic beside it and with a deliberately wrong one gives an identical netlist.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that catches overview §1a.</b> <c>PdnLayoutNets</c>' governing rule is
    /// "net(pad) = the schematic's binding, else the net stated on the copper", which is exactly
    /// right for railRF and catastrophic here: LVS would ask the artwork what the artwork says,
    /// get the schematic's answer back, and every net on every design would match. No exception,
    /// no warning, no finding — a tool that passes everything.
    /// </remarks>
    [Fact]
    public void NothingASchematicSaysCanChangeTheAnswer()
    {
        var board = Divider(0, false);

        WriteSchematicBeside(board, "R1", "R2");
        string correct = Signature(Read(board));

        WriteSchematicBeside(board, "C7", "L3", "Q9");
        Assert.Equal(correct, Signature(Read(board)));
    }

    /// <summary>
    /// <b>Gate 14.</b> Over the flatten ceiling there is no netlist, and the note says so.
    /// </summary>
    /// <remarks>
    /// <c>RailArtwork</c>'s own rule (R-ab1-5d): a confident netlist over geometry the run never
    /// saw is worse than no netlist, because nothing about it looks wrong.
    /// </remarks>
    [Fact]
    public void OverTheFlattenCeilingThereIsNoNetlistAndTheNoteSaysSo()
    {
        var netlist = Read(OverTheCeiling());

        Assert.Empty(netlist.Devices);
        Assert.Empty(netlist.Nets);
        Assert.Contains(netlist.Notes, n => n.Id == "lvs.layout.over-flatten-ceiling");
    }

    // ══ 15, 16 — the two structural gates ════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 15.</b> Nothing under <c>src/Design/Layout/Lvs/</c> unions geometry, walks a via
    /// span, or flattens.
    /// </summary>
    /// <remarks>
    /// Comment-stripped, because these files' own prose names every one of those things in
    /// explaining why it does not do them — and a scan a comment could defeat is a scan that would
    /// have to be silenced the first time somebody documented the rule.
    ///
    /// <para>Each forbidden call has exactly one implementation somewhere else, and the whole
    /// series is built on that staying true: a board whose connectivity the DRC, railRF and LVS
    /// disagree about is a bug none of them reports.</para>
    /// </remarks>
    [Fact]
    public void NothingUnderLvsUnionsGeometryWalksAViaSpanOrWrites()
    {
        string[] forbidden =
        [
            "Clipper", "InflatePaths", "BooleanOp", "Paths64",          // geometry is Extraction's
            "SpanFromLayer", "SpanToLayer",                             // the via walk is DrcConnectivity's
            "FlattenAllLevels", "FlattenOneLevel",                      // no second flatten
            "SaveToFile", "WriteAllText", "WriteAllBytes", "StreamWriter", "Directory.Create",
        ];

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Design", "Layout", "Lvs"), "*.cs"))
        {
            string source = StripComments(File.ReadAllText(file));
            foreach (string name in forbidden)
                if (source.Contains(name, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {name}");
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// <b>Gate 16.</b> The tagged flatten is the SAME walk: on a fixture with nested instances it
    /// emits the identical shapes, in the identical order, shape for shape — and a tag on each.
    /// </summary>
    /// <remarks>
    /// <c>LayoutDesignFlatten.Flatten</c>'s own output is what Gerber export and the DRC depend on
    /// byte for byte, so R-lvs3-2a is that it does not change. What is asserted here is the half a
    /// new test can assert: the two forms are one function body and cannot drift. That the output
    /// itself is unchanged is held by <c>LayoutDesignFlattenTests</c> and the Gerber gates, which
    /// were written before this brief and still pass.
    /// </remarks>
    [Fact]
    public void TheTaggedFlattenEmitsExactlyWhatTheExistingOneDoes()
    {
        var board = Divider(0, false);

        var plain  = LayoutDesignFlatten.Flatten(board.View, board.CellDir, board.Tech, null, null);
        var tagged = LayoutDesignFlatten.FlattenTagged(board.View, board.CellDir, board.Tech, null, null);

        Assert.Equal(Shapes(plain.Shapes), Shapes([.. tagged.Shapes.Select(t => t.Shape)]));

        // The root's own trace carries no placement; both resistors' pads carry theirs.
        Assert.Contains(tagged.Shapes, t => t.InstancePath.Length == 0);
        Assert.Equal(["", "R1", "R2"], tagged.Shapes.Select(t => t.InstancePath).Distinct().Order());
        Assert.Contains(tagged.Shapes, t => t is { InstancePath: "R1", SubCellPin: "A" });
    }

    // ══ A part that IS copper ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A line's own metal joins its two ends, and a galvanic reading therefore shorted every series
    /// line and every open stub (field report, 2026-09-24). A line is a body: its terminals are read
    /// at its ends — on the pad under an end, on another line's end abutting it, or alone.
    /// </summary>
    /// <remarks>
    /// Two lines end to end with a part's pad under the first one's start, and a third line that
    /// touches nothing. By hand: A.1 is on the pad's net, A.2 and B.1 are one junction with no
    /// copper of their own, and B.2, C.1 and C.2 are each alone — five nets, where the old reading
    /// found two (A+B as one piece, C as another).
    /// </remarks>
    [Fact]
    public void ALineIsABodyItsEndsAreItsTerminalsAndAbuttingEndsAreOneNet()
    {
        var tech = TwoLayerTech(groundReference: false);
        string pad = PartCell("Pad1", Top, ("P", 0, 0));
        string line = LineCell("Line2mm", Mm(2), Um(500));

        var netlist = Read(MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(pad,  layoutDir, Mm(1), Mm(5), 0, false, "P1"));
            view.Instances.Add(Place(line, layoutDir, Mm(1), Mm(5), 0, false, "A"));
            view.Instances.Add(Place(line, layoutDir, Mm(3), Mm(5), 0, false, "B"));
            view.Instances.Add(Place(line, layoutDir, Mm(1), Mm(9), 0, false, "C"));
        }));

        Assert.Equal(NetOf(netlist, "P1", "P"), NetOf(netlist, "A", "1"));
        Assert.Equal(NetOf(netlist, "A", "2"), NetOf(netlist, "B", "1"));
        Assert.NotEqual(NetOf(netlist, "A", "1"), NetOf(netlist, "A", "2"));
        Assert.NotEqual(NetOf(netlist, "C", "1"), NetOf(netlist, "C", "2"));
        Assert.Equal(5, new[]
        {
            NetOf(netlist, "A", "1"), NetOf(netlist, "A", "2"), NetOf(netlist, "B", "2"),
            NetOf(netlist, "C", "1"), NetOf(netlist, "C", "2"),
        }.Distinct().Count());

        // An end on nothing but its own line is an open end, not a pad that missed its copper.
        Assert.DoesNotContain(netlist.Notes, n => n.Id == "lvs.pin.no-copper");
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Board(LayoutView View, string Clay, string CellDir, Technology Tech);

    private static LvsNetlist Read(Board b) =>
        LayoutRead.Read(b.View, b.Clay, b.CellDir, b.Tech);

    /// <summary>The net one device's named terminal is on. Fails the test rather than returning a
    /// sentinel, so a fixture that stopped producing the terminal is a loud failure.</summary>
    private static int NetOf(LvsNetlist netlist, string path, string terminal)
    {
        var device = Assert.Single(netlist.Devices, d => d.Path == path);
        return Assert.Single(device.Terminals, t => t.Name == terminal).NetIndex;
    }

    /// <summary>Everything the comparison would read, as one string — devices, their terminals'
    /// nets, and each net's label and pin set. <b>Provenance is deliberately left out</b>: it is
    /// the one part a rotation legitimately changes (R-lvs3-1b).</summary>
    private static string Signature(LvsNetlist netlist)
    {
        var sb = new StringBuilder();
        foreach (var d in netlist.Devices)
        {
            sb.Append(d.Path).Append('|').Append(d.Designator).Append('|').Append(d.Type.Kind).Append(':');
            foreach (var t in d.Terminals) sb.Append(t.Port).Append('=').Append(t.Name).Append('@').Append(t.NetIndex).Append(',');
            sb.AppendLine();
        }
        foreach (var n in netlist.Nets)
            sb.Append(n.Index).Append('/').Append(n.Label ?? "-").Append('/')
              .AppendJoin(';', n.Pins.Select(p => $"{p.Device}.{p.Terminal}")).AppendLine();
        sb.AppendJoin(',', netlist.BoundaryNets).AppendLine();
        foreach (var note in netlist.Notes) sb.AppendLine(note.Id);
        return sb.ToString();
    }

    private static string Shapes(IReadOnlyList<LayoutShape> shapes) =>
        JsonSerializer.Serialize(shapes, new JsonSerializerOptions { WriteIndented = true });

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two two-pin parts in series, optionally with the whole board rotated and/or mirrored about
    /// its origin.
    /// </summary>
    /// <remarks>
    /// <b>The pins are off both axes on purpose</b> — see gate 2. The bridging polygon reaches
    /// pad B of the left part and pad A of the right part and NEITHER outer pad, which is what
    /// makes three nets the hand answer rather than one or four.
    /// </remarks>
    private Board Divider(double deg, bool mirror, string secondDesignator = "R2")
    {
        var tech = TwoLayerTech(groundReference: true);
        string part = PartCell("R0402", Top, ("A", -Um(400), Um(150)), ("B", Um(400), -Um(250)));

        return MakeBoard(tech, (view, layoutDir) =>
        {
            foreach (var (refdes, x) in new[] { ("R1", Mm(2)), (secondDesignator, Mm(6)) })
                view.Instances.Add(Place(part, layoutDir, x, Mm(2), deg, mirror, refdes));

            // x ∈ [2 mm + 300 µm, 6 mm − 300 µm]: past R1's B pad, short of R2's A pad, and
            // nowhere near either outer pad. y spans both inner pads' centres.
            view.Shapes.Add(Polygon(Top,
                (Mm(2) + Um(300), Mm(2) - Um(350)), (Mm(6) - Um(300), Mm(2) - Um(350)),
                (Mm(6) - Um(300), Mm(2) + Um(250)), (Mm(2) + Um(300), Mm(2) + Um(250)),
                deg, mirror));
        });
    }

    /// <summary>Top metal to a barrel to bottom metal, with the two metals never unioned to each
    /// other — they are on different layers, so only the barrel can join them.</summary>
    private Board Staircase(bool withVia)
    {
        var tech = TwoLayerTech(groundReference: false);
        string onTop = PartCell("PadT", Top, ("P", 0, 0));
        string onBot = PartCell("PadB", Bot, ("P", 0, 0));

        return MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(onTop, layoutDir, Mm(2), Mm(5), 0, false, "D1"));
            view.Instances.Add(Place(onBot, layoutDir, Mm(8), Mm(5), 0, false, "D2"));

            view.Shapes.Add(Rect(Top, Mm(2) - Um(200), Mm(5) - Um(100), Mm(5) + Um(100), Mm(5) + Um(100)));
            view.Shapes.Add(Rect(Bot, Mm(5) - Um(100), Mm(5) - Um(100), Mm(8) + Um(200), Mm(5) + Um(100)));

            if (withVia)
                view.Shapes.Add(new ViaShape { Layer = ViaL, X = Mm(5), Y = Mm(5), DrillSize = Um(300) });
        });
    }

    /// <summary>A pad on each of four conductors and one barrel through all of them — the case
    /// where the inner planes are only PASSED.</summary>
    private Board FourLayerStack()
    {
        var tech = FourLayerTech();
        var layers = new[] { Top, new LayerKey(2, 0), new LayerKey(3, 0), new LayerKey(4, 0) };

        return MakeBoard(tech, (view, layoutDir) =>
        {
            for (int i = 0; i < layers.Length; i++)
            {
                string cell = PartCell("L" + i, layers[i], ("P", 0, 0));
                view.Instances.Add(Place(cell, layoutDir, Mm(5), Mm(5), 0, false, "D" + i));
            }
            view.Shapes.Add(new ViaShape { Layer = ViaL, X = Mm(5), Y = Mm(5), DrillSize = Um(200) });
        });
    }

    /// <summary>A three-terminal part whose source pad is grounded through two backside vias.</summary>
    private Board GroundedDie(Technology tech)
    {
        var metal1 = new LayerKey(1, 0);
        string fet = PartCell("Q", metal1, ("G", -Um(40), 0), ("D", Um(40), 0), ("S", 0, -Um(60)));

        return MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(fet, layoutDir, Um(500), Um(500), 0, false, "Q1"));

            // Two barrels under the source pad. Each lands on Metal1 and reaches Backside Metal,
            // which draws nothing at all — which is the whole point.
            foreach (long dx in new[] { -Um(10), Um(10) })
                view.Shapes.Add(new ViaShape
                {
                    Layer = new LayerKey(8, 0), X = Um(500) + dx, Y = Um(440),
                    DrillSize = Um(15), LandingLayer = metal1, PadSize = Um(20),
                });
        });
    }

    /// <summary>Two unconnected traces on the conductor the shipped PCB technologies flag as the
    /// ground reference — and it DRAWS, so they are ordinary copper.</summary>
    private Board TwoBottomTraces()
    {
        var tech = TwoLayerTech(groundReference: true);
        string pad = PartCell("PadB2", Bot, ("P", 0, 0));

        return MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(pad, layoutDir, Mm(2), Mm(2), 0, false, "D1"));
            view.Instances.Add(Place(pad, layoutDir, Mm(8), Mm(2), 0, false, "D2"));
        });
    }

    /// <summary>One placement per classification rule, plus the two that are interconnect.</summary>
    private Board ClassificationBoard()
    {
        var tech = TwoLayerTech(groundReference: true);
        string part = PartCell("P2", Top, ("P", 0, 0));
        string kit  = PartCell("KitPart", Top, ("P", 0, 0));       // ports declared, nothing else
        string fence = InterconnectCell("Fence", viaOnly: true);
        string blob  = InterconnectCell("Blob", viaOnly: false);

        return MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(part, layoutDir, Mm(1), Mm(1), 0, false, null, schematicId: "U1"));
            view.Instances.Add(Place(part, layoutDir, Mm(2), Mm(1), 0, false, "R9"));
            view.Instances.Add(Place(part, layoutDir, Mm(3), Mm(1), 0, false, null, partKind: "Resistor"));
            view.Instances.Add(Place(kit,  layoutDir, Mm(4), Mm(1), 0, false, null));
            view.Instances.Add(Place(fence, layoutDir, Mm(5), Mm(1), 0, false, null));
            view.Instances.Add(Place(blob, layoutDir, Mm(6), Mm(1), 0, false, null));
            view.Instances.Add(Place(blob, layoutDir, Mm(7), Mm(1), 0, false, null));
        });
    }

    /// <summary>A 2×3 array of a device cell beside a 1×20 array of a via cell.</summary>
    private Board Arrays()
    {
        var tech = TwoLayerTech(groundReference: true);
        string part = PartCell("A2", Top, ("P", 0, 0));
        string fence = InterconnectCell("ViaFence", viaOnly: true);

        return MakeBoard(tech, (view, layoutDir) =>
        {
            var device = Place(part, layoutDir, Mm(1), Mm(1), 0, false, "R1");
            device.Rows = 2; device.Cols = 3; device.PitchX = Mm(1); device.PitchY = Mm(1);
            view.Instances.Add(device);

            var vias = Place(fence, layoutDir, Mm(6), Mm(1), 0, false, null);
            vias.Rows = 1; vias.Cols = 20; vias.PitchX = Um(300);
            view.Instances.Add(vias);
        });
    }

    /// <summary>A cell whose symbol pins and layout pins neither match by name nor qualify to be
    /// read positionally — and whose <c>.ccell</c> declares ports, so it is a device.</summary>
    private Board UnmappableDevice()
    {
        var tech = TwoLayerTech(groundReference: true);
        string cell = PartCell("Stuck", Top, [("X", -Um(400), 0), ("Y", Um(400), 0)], ["A", "B"]);

        return MakeBoard(tech, (view, layoutDir) =>
            view.Instances.Add(Place(cell, layoutDir, Mm(2), Mm(2), 0, false, "U5")));
    }

    /// <summary>A three-port part with TWO source pads — one terminal, two pins — placed so the
    /// two land on copper that is not joined.</summary>
    private Board SplitSourceFet()
    {
        var tech = TwoLayerTech(groundReference: true);
        string fet = PartCell("FetTwoSource", Top,
            [("G", -Um(600), 0), ("D", Um(600), 0), ("S", -Um(200), -Um(500)), ("S", Um(200), -Um(500))],
            ["G", "D", "S"]);

        return MakeBoard(tech, (view, layoutDir) =>
            view.Instances.Add(Place(fet, layoutDir, Mm(2), Mm(2), 0, false, "Q1")));
    }

    /// <summary>One pad on a layer the technology declares and no stackup entry claims.</summary>
    private Board PadOnAnUnclaimedLayer()
    {
        var tech = TwoLayerTech(groundReference: true);
        string cell = PartCell("Misplaced", Silk, ("P", 0, 0));

        return MakeBoard(tech, (view, layoutDir) =>
        {
            view.Instances.Add(Place(cell, layoutDir, Mm(2), Mm(2), 0, false, "U1"));
            view.Shapes.Add(Rect(Top, Mm(5), Mm(5), Mm(6), Mm(6)));   // so there IS a partition
        });
    }

    /// <summary>An array whose resulting shape count is twice the ceiling. Nothing is
    /// materialized: the estimate multiplies one cell's count by rows×cols.</summary>
    private Board OverTheCeiling()
    {
        var tech = TwoLayerTech(groundReference: true);
        string cell = InterconnectCell("Huge", viaOnly: false);

        return MakeBoard(tech, (view, layoutDir) =>
        {
            var many = Place(cell, layoutDir, 0, 0, 0, false, null);
            many.Rows = 1_000; many.Cols = 1_000; many.PitchX = Um(10); many.PitchY = Um(10);
            view.Instances.Add(many);
        });
    }

    // ── Fixture machinery ───────────────────────────────────────────────────────────────────────

    private int _boards;

    private Board MakeBoard(Technology tech, Action<LayoutView, string> build)
    {
        string techDir = Path.Combine(_root, "tech");
        Directory.CreateDirectory(techDir);
        TechPersistence.SaveToFile(Path.Combine(techDir, "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string name = "Board" + _boards++;
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        build(view, layoutDir);

        string clay = Path.Combine(layoutDir, name + ".clay");
        LayoutPersistence.SaveToFile(clay, view);
        return new Board(view, clay, cellDir, tech);
    }

    /// <summary>
    /// A cell folder with a primary symbol and a primary layout: one named layout pin per entry,
    /// a square pad of copper under it, and a symbol pin per name.
    /// </summary>
    /// <param name="symbolPinNames">The symbol's own pins, when they are not simply the layout
    /// pins' names — which is how the underivable and the two-pads-one-terminal cases are made.</param>
    private string PartCell(
        string name, LayerKey layer, (string Pin, long X, long Y)[] pins,
        IReadOnlyList<string>? symbolPinNames = null)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        if (File.Exists(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay")))
            return cellDir;   // one cell, placed many times

        var ports = symbolPinNames ?? [.. pins.Select(p => p.Pin).Distinct()];
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol([], [.. ports.Select((n, i) => new SymbolPin(0, i * 100, i + 1, n))], ports.Count));

        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x, y) in pins)
        {
            view.Pins.Add(new LayoutPin { Name = pin, X = x, Y = y, WidthDbu = Um(250), Layer = layer });
            var pad = Rect(layer, x - Um(125), y - Um(125), x + Um(125), y + Um(125));
            pad.Pin = pin;      // what a land pattern's own pads carry, and all they may carry
            view.Shapes.Add(pad);
        }
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay"), view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = ports.Count;
        CellPersistence.SaveToFile(ccellPath, ccell);

        return cellDir;
    }

    private string PartCell(string name, LayerKey layer, params (string Pin, long X, long Y)[] pins)
        => PartCell(name, layer, pins, null);

    /// <summary>A two-terminal line: ONE rectangle of top copper, pin 1 at its start and pin 2 at
    /// its end — what the microstrip generators draw.</summary>
    private string LineCell(string name, long length, long width)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        if (File.Exists(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay")))
            return cellDir;

        string[] ports = ["1", "2"];
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"),
            new Symbol([], [.. ports.Select((n, i) => new SymbolPin(0, i * 100, i + 1, n))], ports.Length));

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Pins.Add(new LayoutPin { Name = "1", X = 0,      Y = 0, WidthDbu = width, Layer = Top });
        view.Pins.Add(new LayoutPin { Name = "2", X = length, Y = 0, WidthDbu = width, Layer = Top });
        view.Shapes.Add(Rect(Top, 0, -width / 2, length, width / 2));
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay"), view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = ports.Length;
        CellPersistence.SaveToFile(ccellPath, ccell);

        return cellDir;
    }

    /// <summary>A cell with no symbol, no ports and no designator — interconnect. A via fence
    /// draws on the VIA layer and is silent; a nameless slab of copper is R-lvs3-3c's warning.</summary>
    private string InterconnectCell(string name, bool viaOnly)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        if (File.Exists(Path.Combine(layoutDir, name + ".clay"))) return cellDir;

        var view = new LayoutView { DbuPerMicron = Dbu };
        if (viaOnly) view.Shapes.Add(new ViaShape { Layer = ViaL, X = 0, Y = 0, DrillSize = Um(200) });
        else view.Shapes.Add(Rect(Top, -Um(200), -Um(200), Um(200), Um(200)));
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), view);

        return cellDir;
    }

    private static LayoutInstance Place(
        string cellDir, string layoutDir, long x, long y, double deg, bool mirror,
        string? refdes, string? schematicId = null, string? partKind = null)
    {
        var (px, py) = Xf(x, y, deg, mirror);
        return new LayoutInstance
        {
            CellRef = Path.GetRelativePath(layoutDir, cellDir),
            X = px, Y = py, Mag = 1.0,
            RotationDegrees = deg, MirrorX = mirror,
            RefDes = refdes, SchematicId = schematicId, PartKind = partKind,
        };
    }

    /// <summary>The whole-board transform: mirror X first, then rotate — exactly the order
    /// <see cref="LayoutInstanceTransform.TransformPoint"/> applies, so a point transformed here
    /// and a pin transformed there land in the same place.</summary>
    private static (long X, long Y) Xf(long x, long y, double deg, bool mirror)
    {
        double mx = mirror ? -x : x;
        var (c, s) = LayoutAngle.CosSin(deg);
        return ((long)Math.Round(mx * c - y * s), (long)Math.Round(mx * s + y * c));
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static PolygonShape Polygon(
        LayerKey layer, (long X, long Y) a, (long X, long Y) b, (long X, long Y) c, (long X, long Y) d,
        double deg, bool mirror)
    {
        var xy = new List<long>();
        foreach (var (x, y) in new[] { a, b, c, d })
        {
            var (px, py) = Xf(x, y, deg, mirror);
            xy.Add(px); xy.Add(py);
        }
        return new PolygonShape { Layer = layer, Xy = [.. xy] };
    }

    private static void WriteSchematicBeside(Board board, params string[] instanceNames)
    {
        string dir = CellFolder.SubFolderPath(board.CellDir, ViewType.Schematic);
        Directory.CreateDirectory(dir);

        var model = new SchematicEditModel();
        foreach (string name in instanceNames)
            model.Components.Add(new EditableComponent { InstanceName = name, Symbol = SymbolKind.Resistor });

        File.WriteAllText(Path.Combine(dir, "Board.csch"), SchematicPersistence.Serialize(model));
    }

    private static Technology TwoLayerTech(bool groundReference)
    {
        var tech = new Technology { Name = "TwoLayer" };
        tech.Layers =
        [
            new LayerDef { Key = Top,  Name = "Top",    ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot,  Name = "Bottom", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = Silk, Name = "Silk",   ZOrder = 2, Color = new Rgba(220, 220, 220, 255) },
            new LayerDef { Key = ViaL, Name = "Via",    ZOrder = 3, Color = new Rgba(120, 120, 120, 255) },
        ];
        tech.Stackup.Layers =
        [
            Conductor("Top", Top, false),
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaL],
                SpanFromLayer = "Top", SpanToLayer = "Bottom",
            },
            Dielectric(),
            Conductor("Bottom", Bot, groundReference),
        ];
        return tech;
    }

    private static Technology FourLayerTech()
    {
        var tech = new Technology { Name = "FourLayer" };
        var inner1 = new LayerKey(2, 0);
        var inner2 = new LayerKey(3, 0);
        var bottom = new LayerKey(4, 0);

        tech.Layers =
        [
            new LayerDef { Key = Top,    Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = inner1, Name = "IN1", ZOrder = 1, Color = new Rgba(90, 160, 90, 255) },
            new LayerDef { Key = inner2, Name = "IN2", ZOrder = 2, Color = new Rgba(160, 160, 60, 255) },
            new LayerDef { Key = bottom, Name = "BOT", ZOrder = 3, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = ViaL,   Name = "VIA", ZOrder = 4, Color = new Rgba(120, 120, 120, 255) },
        ];
        tech.Stackup.Layers =
        [
            Conductor("TOP", Top, false),
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaL],
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
            Dielectric(), Conductor("IN1", inner1, false),
            Dielectric(), Conductor("IN2", inner2, false),
            Dielectric(), Conductor("BOT", bottom, false),
        ];
        return tech;
    }

    private static StackupLayer Conductor(string name, LayerKey key, bool groundReference) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7,
        DrawingLayers = [key], IsGroundReference = groundReference,
    };

    private static StackupLayer Dielectric() => new()
    {
        Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02,
    };

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
