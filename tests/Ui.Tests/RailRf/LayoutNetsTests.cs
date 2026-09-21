// ================================================================
//  LayoutNetsTests.cs — brief-authored-board-2-net-identity.md §6, the gate for what a pad is
//  CONNECTED TO on a board somebody drew.
//
//  ── THE RULE BEING GATED (the owner's, 2026-09-20) ────────────────────────────────────────────
//
//      net(pad) = the schematic's own binding for that refdes and that pin, where a schematic
//                 resolves; else the net stated on the copper the pad lands on
//
//  ── TWO ROWS HERE PASS FOR A DEFECT EVERY OTHER ROW MISSES, AND THEY ARE THE POINT ────────────
//
//  §6.5 is the one that says the PARTITION is being used at all: an implementation that reads `Net`
//  off each shape individually passes every other row in this file and fails only that one, because
//  only it asks whether naming one trace named the pour and the vias too.
//
//  §6.6 is the one defect that would look correct on a board with one capacitor on it: a land
//  pattern is ONE CELL SHARED BY EVERY PLACEMENT OF IT, so a `Net` stamped inside it puts every
//  instance on one net. With one instance placed that is indistinguishable from working.
//
//  §6.2's fixture is deliberately ASYMMETRIC for the reason brief 1's own §7.2 is: a swap between
//  pin order and port order is invisible on a symmetric part, and it is exactly the swap
//  PdnMountingLoop cannot detect.
// ================================================================

using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class LayoutNetsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-ab2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // OFF BOTH AXES and in a deliberately reversed order — pin "2" is the FIRST pin of the list, so
    // an index join puts it on the FIRST net. §6.2.
    private static readonly (string Name, long X, long Y)[] AsymmetricLand =
        [("2", Um(-400), Um(250)), ("1", Um(400), Um(-150))];

    private static readonly (string Name, long X, long Y)[] PlainLand =
        [("1", Um(-400), Um(250)), ("2", Um(400), Um(-150))];

    // ══ 1. A schematic beside the artwork names the pads ════════════════════════════════════════

    /// <summary><b>§6.1, R-ab2-1c.</b> Two capacitors between <c>+3V3</c> and <c>GND</c>; every pad
    /// comes back with the schematic's own name, and nobody typed one on the copper.</summary>
    [Fact]
    public void ASchematicBesideTheArtworkNamesEveryPad()
    {
        var fx = Board([Placed("Land", "C1", PlainLand, Mm(5), Mm(3)),
                        Placed("Land", "C2", PlainLand, Mm(9), Mm(3))]);
        WriteSchematic(fx, TwoPartRail());

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null);

        Assert.Equal("+3V3", NetOf(resolved, "C1", "1"));
        Assert.Equal("GND",  NetOf(resolved, "C1", "2"));
        Assert.Equal("+3V3", NetOf(resolved, "C2", "1"));
        Assert.Equal("GND",  NetOf(resolved, "C2", "2"));

        Assert.Equal(["+3V3", "GND"], resolved.Nets);
        Assert.Equal(PdnNetOrigin.Schematic, resolved.NetOrigin);
        Assert.Equal("nets from the schematic", PdnNetSummary.Describe(resolved.NetOrigin));
    }

    // ══ 2. Port order, not pad order ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§6.2.</b> A part whose pin <c>"2"</c> binds the FIRST net — the pad on pin 2 must carry
    /// it. Reversing the join is the R-ab1-4 swap and it is invisible on a symmetric fixture, so
    /// this land's pins are at different places AND in the reverse of their numeric order.
    /// </summary>
    [Fact]
    public void ThePadTakesItsPortsNetAndNotItsPositionsNet()
    {
        var fx = Board([Placed("Rev", "C1", AsymmetricLand, Mm(5), Mm(3))]);
        WriteSchematic(fx, TwoPartRail());

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null);

        // Pin "2" is port 0, which binds the FIRST net. The pad's COORDINATE is the other one's.
        Assert.Equal("+3V3", NetOf(resolved, "C1", "2"));
        Assert.Equal("GND",  NetOf(resolved, "C1", "1"));

        var pin2 = Assert.Single(resolved.Pads, p => p.Refdes == "C1" && p.Pin == "2");
        Assert.Equal((Mm(5) + Um(-400), Mm(3) + Um(250)), (pin2.X, pin2.Y));
    }

    // ══ 3. A hand-placed instance takes the stamped path ════════════════════════════════════════

    /// <summary><b>§6.3, R-ab2-1c.</b> It has no <c>SchematicId</c>, so nothing in the extraction is
    /// about it — even with a schematic that resolves perfectly well for the parts beside it.</summary>
    [Fact]
    public void AHandPlacedInstanceTakesTheStampedPathEvenWithASchematicPresent()
    {
        var fx = Board(
            [Placed("Land", "C1", PlainLand, Mm(5), Mm(3)), HandPlaced("Land", "C9", Mm(20), Mm(3))],
            v => v.Shapes.Add(new RectShape
            {
                Layer = Top, Net = "VBAT",
                X1 = Mm(18), Y1 = Mm(2), X2 = Mm(22), Y2 = Mm(4),
            }));
        WriteSchematic(fx, TwoPartRail());

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx));

        Assert.Equal("+3V3", NetOf(resolved, "C1", "1"));      // the schematic's
        Assert.Equal("VBAT", NetOf(resolved, "C9", "1"));      // the copper's
        Assert.Equal("VBAT", NetOf(resolved, "C9", "2"));

        Assert.Equal(PdnNetOrigin.Schematic | PdnNetOrigin.Artwork, resolved.NetOrigin);
        Assert.Equal("nets from the schematic and stated on the artwork",
                     PdnNetSummary.Describe(resolved.NetOrigin));
    }

    // ══ 4. Conflicts suppress the WHOLE schematic path ══════════════════════════════════════════

    /// <summary>
    /// <b>§6.4, R-ab2-1e.</b> Not per part. <c>ExtractionResult.Conflicts</c> means the extraction
    /// disagreed with itself somewhere, and taking the uncontested half of a contested extraction
    /// is how a board ends up half-named with nothing to say which half.
    /// </summary>
    [Fact]
    public void ConflictsSuppressTheWholeSchematicPathAndAreReported()
    {
        var fx = Board([Placed("Land", "C1", PlainLand, Mm(5), Mm(3)),
                        Placed("Land", "C2", PlainLand, Mm(9), Mm(3))]);

        // Two DIFFERENT labels on one physical net — the conflict NetExtractor reports.
        var model = TwoPartRail();
        model.NetLabels.Add(new EditableNetLabel { X = 300, Y = -200, Name = "VCC" });
        WriteSchematic(fx, model);

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null);

        Assert.All(resolved.Pads, p => Assert.Null(p.Net));
        Assert.Empty(resolved.Nets);
        Assert.Equal(PdnNetOrigin.None, resolved.NetOrigin);

        // The summary says the whole path was suppressed, and the extractor's own conflicts follow
        // it — reported, not merely counted.
        string why = resolved.Notes[0];
        foreach (string n in resolved.Notes) output.WriteLine(n);
        Assert.Contains("naming conflict", why, StringComparison.Ordinal);
        Assert.Contains(resolved.Notes, n => n.Contains("'+3V3'", StringComparison.Ordinal)
                                          && n.Contains("'VCC'", StringComparison.Ordinal));
    }

    // ══ 5. One stamp names a whole pour ═════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§6.5, R-ab2-2b. THIS IS THE ROW THAT SAYS THE PARTITION IS BEING USED AT ALL.</b> A trace,
    /// a pour and three vias; stamp the TRACE only. An implementation that reads <c>Net</c> off each
    /// shape individually passes every other row in this file and fails this one.
    /// </summary>
    [Fact]
    public void OneStampNamesTheWholeConnectedPiece()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };

        // The trace, stamped. The pour on the SAME layer, overlapping it. Three vias down to BOT,
        // each landing on both.
        view.Shapes.Add(new RectShape { Layer = Top, Net = "+3V3", X1 = 0, Y1 = 0, X2 = Mm(10), Y2 = Mm(1) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Mm(8), Y1 = 0, X2 = Mm(20), Y2 = Mm(10) });
        for (int i = 0; i < 3; i++)
            view.Shapes.Add(new ViaShape
            {
                Layer = Via, LandingLayer = Bot, X = Mm(10 + 2 * i), Y = Mm(5),
                DrillSize = Um(200), PadSize = Um(400),
            });

        var pieces = PdnCopperPieces.Build(view.Shapes, TechFixture());

        for (int i = 0; i < view.Shapes.Count; i++)
            Assert.Equal("+3V3", pieces.NameOfShape(i));

        // And a pad landing anywhere on it takes the name, on its own layer (R-ab2-2d).
        Assert.Equal("+3V3", pieces.NameAt(Mm(15), Mm(6), Top));
        Assert.Null(pieces.NameAt(Mm(15), Mm(6), Bot));   // the pour is not on BOT; the vias' pads are

        // And the vias are net POINTS now, which is what lets an inner-layer pour be recognised
        // (R-ab2-2e) — before this they contributed one only where somebody stamped that very via.
        var points = PdnLayoutPads.NetPointsOf(view, [], pieces);
        Assert.Equal(3, points.Count);
        Assert.All(points, p => Assert.Equal("+3V3", p.Net));
    }

    // ══ 6. A sub-cell stamp does NOT leak ═══════════════════════════════════════════════════════

    /// <summary>
    /// <b>§6.6, R-ab2-2a.</b> A land pattern is one cell shared by every placement of it. Three
    /// instances, three unnamed pads — and the one defect that would look correct on a board with
    /// one capacitor on it.
    /// </summary>
    [Fact]
    public void ANetStampedInsideASharedLandPatternNamesNothing()
    {
        // The land's own pad shapes carry the net, which is the mistake. Pin is a property of the
        // pattern; net is a property of the board.
        Land("Leaky", PlainLand, padNet: "+3V3");

        var fx = Board([
            Place("Leaky", "C1", Mm(5),  Mm(3)),
            Place("Leaky", "C2", Mm(9),  Mm(3)),
            Place("Leaky", "C3", Mm(13), Mm(3)),
        ]);

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx));

        Assert.Equal(6, resolved.Pads.Count);
        Assert.All(resolved.Pads, p => Assert.Null(p.Net));
        Assert.Empty(resolved.Nets);
    }

    // ══ 7. Two names on one piece is a refusal ══════════════════════════════════════════════════

    /// <summary>
    /// <b>§6.7, R-ab2-2c.</b> Either the artwork shorts two nets or one of the labels is wrong, and
    /// both readings are things a user must see. Picking one produces a plausible board and buries
    /// a short.
    /// </summary>
    [Fact]
    public void TwoNamesOnOnePieceIsARefusalNamingBoth()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Top, Net = "+3V3", X1 = 0, Y1 = 0, X2 = Mm(10), Y2 = Mm(1) });
        view.Shapes.Add(new RectShape { Layer = Top, Net = "GND",  X1 = Mm(8), Y1 = 0, X2 = Mm(20), Y2 = Mm(1) });

        var pieces = PdnCopperPieces.Build(view.Shapes, TechFixture());

        string why = Assert.Single(pieces.Refusals);
        output.WriteLine(why);
        Assert.Contains("'+3V3'", why, StringComparison.Ordinal);
        Assert.Contains("'GND'", why, StringComparison.Ordinal);

        // NOTHING on that piece took a name, and the rest of a board would be unaffected.
        Assert.Empty(pieces.Nets);
        Assert.Null(pieces.NameAt(Mm(5), Um(500), Top));
    }

    // ══ 8. The gesture: one undo, and it reports its reach first ════════════════════════════════

    /// <summary>
    /// <b>§6.8, R-ab2-3a and R-ab2-3c.</b> Name Net… writes the same field through the same setter
    /// as the Properties Inspector's own Net row — so a commit across a whole pour is ONE undo entry
    /// — and it states its blast radius before committing, counted from the partition rather than
    /// guessed.
    /// </summary>
    [Fact]
    public void TheGestureReportsItsReachAndCommitsAsOneUndoEntry()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(10), Y2 = Mm(1) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Mm(8), Y1 = 0, X2 = Mm(20), Y2 = Mm(10) });
        view.Shapes.Add(new ViaShape
        {
            Layer = Via, LandingLayer = Bot, X = Mm(12), Y = Mm(5),
            DrillSize = Um(200), PadSize = Um(400),
        });

        var vm = new LayoutEditorViewModel(view) { Technology = TechFixture() };

        var reach = vm.NetNameReachAt(Mm(5), Um(500), Um(10));
        Assert.NotNull(reach);
        output.WriteLine(reach!.Sentence);
        Assert.Equal(3, reach.ShapeIndices.Count);
        Assert.Equal("Names this piece and the 2 shapes joined to it.", reach.Sentence);
        Assert.Empty(reach.ExistingNames);

        // Nothing has been written yet — the reach is counted BEFORE the commit.
        Assert.All(view.Shapes, s => Assert.Null(s.Net));

        vm.ApplyNetName(reach, "+3V3");
        Assert.All(view.Shapes, s => Assert.Equal("+3V3", s.Net));
        Assert.Equal(["+3V3"], vm.NetNamesOnBoard());

        // ONE undo entry across all three (R-ab2-3a) — the multi-select path, not three commands.
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.All(view.Shapes, s => Assert.Null(s.Net));
        Assert.False(vm.UndoRedo.CanUndo);

        // R-ab2-3d: a second naming over a named piece says what it will replace.
        vm.UndoRedo.Redo();
        var again = vm.NetNameReachAt(Mm(15), Mm(6), Um(10));
        Assert.Equal(["+3V3"], again!.ExistingNames);
    }

    /// <summary>
    /// <b>§6.8b.</b> Whether the menu row EXISTS is a hit test; what it reaches is the partition.
    /// The two were one call, so every right-click that landed on any shape partitioned the whole
    /// document's copper on the UI thread to decide whether one row was there — which is ~0.5 s at
    /// 5,000 shapes and ~5 s at 20,000, and this window has one compositor.
    ///
    /// <para>The structural property, rather than a clock: the seeds are what was CLICKED and the
    /// reach is what that is joined to, so a seed set that had expanded to the connected piece is a
    /// seed set that had already paid for the partition.</para>
    /// </summary>
    [Fact]
    public void TheMenuRowsPresenceIsAHitTestAndNotThePartition()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(10), Y2 = Mm(1) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Mm(8), Y1 = 0, X2 = Mm(20), Y2 = Mm(10) });

        var vm = new LayoutEditorViewModel(view) { Technology = TechFixture() };

        var seeds = vm.NetNameSeedsAt(Mm(5), Um(500), Um(10));
        Assert.NotNull(seeds);
        Assert.Equal([0], seeds);                       // the clicked shape ALONE — no walk yet

        Assert.Null(vm.NetNameSeedsAt(Mm(25), Mm(25), Um(10)));   // off every shape: no row at all

        // And the reach off those same seeds is still the whole joined piece, unchanged.
        var reach = vm.NetNameReachFor(seeds!);
        Assert.Equal(2, reach.ShapeIndices.Count);
        Assert.Equal("Names this piece and the 1 shape joined to it.", reach.Sentence);
    }

    // ══ 9 & 10. The window offers a drawn board's nets, and stops saying the wrong thing ════════

    /// <summary>
    /// <b>§6.9 and §6.10.</b> The pick list offers what the SCHEMATIC named — and the sentence that
    /// says no board netlist named any nets is ABSENT over it.
    /// </summary>
    /// <remarks>
    /// <b>§6.10 is the row that would be missed.</b> Nothing fails when a false sentence is printed:
    /// the window would go on working, the user would go on being told to click the pour, and no
    /// test that asserted the list's CONTENTS would notice. It is railRF brief 25's R-rail25-4b trap
    /// in its second instance, so both halves are asserted here — present on a board that names
    /// nothing, absent on one whose nets came from a schematic.
    /// </remarks>
    [Fact]
    public void ThePickListOffersADrawnBoardsNetsAndTheWrongSentenceIsAbsent()
    {
        var fx = Board([Placed("Land", "C1", PlainLand, Mm(5), Mm(3))]);

        // Before the schematic exists: nothing named a net, the sentence is TRUE, and it shows.
        var bare = OpenWindowOn(fx, "Bare");
        Assert.Empty(bare.AvailableNets);
        Assert.True(bare.HasNoPickableNets);
        Assert.Equal("", bare.NetOriginText);

        // And the click-the-pour gesture is AVAILABLE, which it must stay either way (R-ab2-4c) —
        // but it is armed by the button and not by a bare click (owner, 2026-09-20), so what the
        // board carries before the press is nothing at all.
        Assert.Null(bare.BoardOverlayLayer.PourPick);
        Assert.True(bare.CanPickFromBoard);

        WriteSchematic(fx, TwoPartRail());

        var named = OpenWindowOn(fx, "Named");
        Assert.Equal(["+3V3", "GND"], named.AvailableNets.Select(r => r.Name));
        Assert.True(named.HasPickableNets);
        Assert.False(named.HasNoPickableNets);     // §6.10: the sentence is ABSENT
        Assert.Equal("nets from the schematic", named.NetOriginText);
        Assert.True(named.CanPickFromBoard);
    }

    // ══ 11. Divergence is reported and the run still completes ══════════════════════════════════

    /// <summary>
    /// <b>§6.11, R-ab2-5a and R-ab2-5c.</b> Reported, never resolved, never ranked, never a refusal:
    /// the run proceeds on R-ab1-3a's precedence, because a stale export is an ordinary mid-design
    /// state and a tool that refused to solve on one would be a tool nobody runs mid-design.
    /// </summary>
    [Fact]
    public void ANetlistThatDisagreesWithTheArtworkIsReportedAndTheRunProceeds()
    {
        var fx = Board([Placed("Land", "C1", PlainLand, Mm(5), Mm(3))]);
        WriteSchematic(fx, TwoPartRail());

        // The `.ipc` says C1.1 is on GND; the schematic says +3V3. And it stands 15 mm away.
        var netlist = Netlist(("C1", "1", "GND"), ("C1", "2", "GND"));

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, netlist);

        var net = Assert.Single(resolved.Divergences, d => d.Sentence.Contains("is on '", StringComparison.Ordinal));
        output.WriteLine(net.Sentence);
        Assert.Equal(("C1", "1"), (net.Refdes, net.Pin));
        Assert.Contains("'GND'", net.Sentence, StringComparison.Ordinal);
        Assert.Contains("'+3V3'", net.Sentence, StringComparison.Ordinal);

        // Once per PAIR, never once per pad: two pins, two position lines, and no repeats.
        var moved = resolved.Divergences.Where(d => d.Sentence.Contains("stands at", StringComparison.Ordinal)).ToList();
        Assert.Equal(["1", "2"], moved.Select(d => d.Pin));
        foreach (var d in moved) output.WriteLine(d.Sentence);

        // The RUN proceeds, on the netlist's reading. Nothing was refused and nothing was ranked.
        Assert.Equal(2, resolved.FromBoardNetlist);
        Assert.Equal(0, resolved.FromArtwork);
        Assert.All(resolved.Pads, p => Assert.Equal("GND", p.Net));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static readonly LayerKey Via = new(9, 0);

    private sealed record Fx(LayoutView View, string Clay, Technology Tech, string CellDir);

    private static string? NetOf(RailArtwork.RailPadResolution r, string refdes, string pin) =>
        Assert.Single(r.Pads, p => p.Refdes == refdes && p.Pin == pin).Net;

    /// <summary>The artwork as the EXTRACTION reads it — root copper plus every land inside an
    /// instance, which is what a pad has to be standing on.</summary>
    private static IReadOnlyList<LayoutShape> Flat(Fx fx) =>
        RailArtwork.FlattenedShapes(fx.View, fx.Clay, fx.Tech);

    private Fx Board(LayoutInstance[] instances, Action<LayoutView>? addShapes = null)
    {
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        string techPath = Path.Combine(_root, "tech", "Board.ctech");
        if (!File.Exists(techPath)) TechPersistence.SaveToFile(techPath, tech);
        if (!File.Exists(Path.Combine(_root, ".cws")))
            WorkspacePersistence.SaveToFile(
                Path.Combine(_root, ".cws"),
                new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        addShapes?.Invoke(view);
        foreach (var i in instances) view.Instances.Add(i);

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);
        return new Fx(view, clay, tech, cellDir);
    }

    /// <summary>A land pattern cell — pins plus a copper pad under each.</summary>
    /// <param name="padNet">§6.6's mistake, on purpose: a net stamped INSIDE the shared cell.</param>
    private void Land(string name, (string Name, long X, long Y)[] pins, string? padNet = null)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        string file = Path.Combine(layoutDir, name + ".clay");
        if (File.Exists(file)) return;

        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pinName, x, y) in pins)
        {
            cell.Pins.Add(new LayoutPin { Name = pinName, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            cell.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pinName, Net = padNet,
                X1 = x - Um(125), Y1 = y - Um(125), X2 = x + Um(125), Y2 = y + Um(125),
            });
        }
        LayoutPersistence.SaveToFile(file, cell);
    }

    private LayoutInstance Place(string cell, string? refdes, long x, long y) => new()
    {
        CellRef = Path.Combine("..", "..", cell),
        X = x, Y = y, Mag = 1.0, RefDes = refdes,
    };

    /// <summary>A placement the SCHEMATIC owns — its <c>SchematicId</c> is what R-ab2-1c joins on.</summary>
    private LayoutInstance Placed(
        string cell, string schematicId, (string Name, long X, long Y)[] pins, long x, long y)
    {
        Land(cell, pins);
        var inst = Place(cell, null, x, y);
        inst.SchematicId = schematicId;
        return inst;
    }

    /// <summary>A placement nobody's schematic knows about — a hand-typed <c>RefDes</c> and no
    /// <c>SchematicId</c>, which is R-ab2-1c's stamped path even with a schematic present.</summary>
    private LayoutInstance HandPlaced(string cell, string refdes, long x, long y)
    {
        Land(cell, PlainLand);
        return Place(cell, refdes, x, y);
    }

    /// <summary>
    /// The board's own sibling schematic — <c>&lt;cell&gt;/schematic/Board.csch</c>, which is the
    /// walk R-ab2-1a performs from the artwork's own path.
    /// </summary>
    private static void WriteSchematic(Fx fx, SchematicEditModel model)
    {
        string dir = CellFolder.SubFolderPath(fx.CellDir, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        SchematicPersistence.SaveToFile(Path.Combine(dir, "Board.csch"), model, "Board");
    }

    /// <summary>Two capacitors across <c>+3V3</c> and <c>GND</c>. A capacitor's pins are at
    /// (0,-200) and (0,+200), so port 0 is the TOP one.</summary>
    private static SchematicEditModel TwoPartRail()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor, X = 0, Y = 0 });
        m.Components.Add(new EditableComponent { InstanceName = "C2", Symbol = SymbolKind.Capacitor, X = 400, Y = 0 });

        var top = new EditableWire(); top.Points.AddRange([(0.0, -200.0), (400.0, -200.0)]);
        var bot = new EditableWire(); bot.Points.AddRange([(0.0, 200.0), (400.0, 200.0)]);
        m.Wires.Add(top);
        m.Wires.Add(bot);

        m.NetLabels.Add(new EditableNetLabel { X = 200, Y = -200, Name = "+3V3" });
        m.NetLabels.Add(new EditableNetLabel { X = 200, Y = 200, Name = "GND" });
        return m;
    }

    /// <summary>A board netlist standing every part at one point, so a pad that came from it is told
    /// from a pad that came from the artwork by its COORDINATE and not only by its stamp.</summary>
    private static BoardNetlist Netlist(params (string Refdes, string Pin, string Net)[] rows)
        => new("Board.ipc", null, BoardNetlistUnits.MillimetreThousandth, BoardNetlistUnitsEvidence.Declared,
               "BOARD",
               [.. rows.Select((r, i) => new BoardNetlistRecord(
                   317, r.Net, r.Refdes, r.Pin, false, null, null, 1, Mm(20), Mm(20), i + 1))],
               rows.Length, 0, 0, default, []);

    /// <summary>A railRF window opened on a bare <c>.clay</c>, through the document path the project
    /// tree uses.</summary>
    private RailRfViewModel OpenWindowOn(Fx fx, string name)
    {
        var doc = new RailDocument
        {
            Name = name,
            ArtworkCellRef = Path.GetRelativePath(fx.CellDir, fx.Clay),
        };
        string crail = Path.Combine(fx.CellDir, name + ".crail");
        RailDocumentIo.SaveToFile(crail, doc);

        var vm = new RailRfViewModel(doc, crail) { PostToUi = a => a() };
        vm.LoadDocumentReferences();
        return vm;
    }

    private static Technology TechFixture()
    {
        var tech = new Technology { Name = "Board" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = Bot, Name = "BOT", ZOrder = 1, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = Via, Name = "VIA", ZOrder = 2, Color = new Rgba(120, 120, 120, 255) },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "V1", DrawingLayers = [Via],
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true,
            },
        ];
        return tech;
    }
}
