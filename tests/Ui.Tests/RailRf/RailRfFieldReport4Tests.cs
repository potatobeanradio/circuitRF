// ================================================================
//  RailRfFieldReport4Tests.cs
//
//  A fourth round of outside railRF use, 2026-09-22, on an imported two-sided Gerber board with an
//  inner plane and a hand-drawn schematic whose footprints the designer dragged onto the artwork.
//  One test per CLAIM.
//
//  ── WHAT HE SAID, AND WHAT IS ASSERTED ────────────────────────────────────────────────────────
//
//   1. Picking a supply net outlined the whole board, and a crystal-local net "is wrong" too. The
//      copper was right; 30 of his 55 two-pin parts sat at 180° to the schematic's pin order, and
//      each put a schematic net on the ground side of its part. Asserted: a turned capacitor is
//      read from the copper and reported; a tie is left alone; a diode is never turned; and the
//      half turn the window offers puts each land exactly where the other one was.
//
//   2. The same flood happened with NO part turned whenever no reference layer was named yet — a
//      pad is a coordinate and the plane is under every one. Asserted: a pad's net point seeds its
//      own land's layer, not the plane under it.
//
//   3. The window: the turned parts are named with a button that turns them in the layout; a net
//      whose copper also holds other nets' pins says which; the schematic's ground `0` is one row
//      with the measured return; and the parts pane does not tell a user to run while a run is
//      already going.
//
//   4. "I have a gnd layer", answering a warning that conductor GND claims no drawing layer. The
//      warning now names the layer and the editor attaches it in one press; and a fabrication
//      drawing named after its file is no longer reported as unclaimed copper.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfFieldReport4Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-fr4-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey Via = new(9, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    internal static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // Off-centre on purpose: a half turn about the footprint's OWN origin would move these lands off
    // their copper, and only a turn about the lands' midpoint puts each where the other was.
    private static readonly (string Name, long X, long Y)[] Land0402 =
        [("1", Um(-400), Um(250)), ("2", Um(400), Um(-150))];

    // ══ 1 — a two-terminal part at 180° is read from the copper ═════════════════════════════════

    /// <summary>
    /// <b>Three capacitors across one supply strip and one ground strip; the third is placed turned.</b>
    /// Its pin 1 — the schematic's supply side — sits on the ground strip. The copper reads it the
    /// right way round, the resolution names it, and an anchor written <c>C3.1</c> still means the
    /// supply terminal.
    /// </summary>
    [Fact]
    public void ATurnedCapacitorIsReadFromTheCopperAndNamed()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: true);

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx));

        var turned = Assert.Single(resolved.Turned);
        Assert.Equal("C3", turned.Refdes);
        Assert.Contains("C3 is placed at 180°", string.Join("\n", resolved.Notes), StringComparison.Ordinal);

        // Pin 1 keeps the schematic's supply net and now stands on the supply strip.
        var pin1 = Assert.Single(resolved.Pads, p => p.Refdes == "C3" && p.Pin == "1");
        Assert.Equal("+3V3", pin1.Net);
        Assert.True(InSupplyStrip(pin1.X, pin1.Y), $"C3.1 at ({pin1.X}, {pin1.Y}) is not on the supply strip");

        // The two parts placed the right way round are untouched.
        Assert.DoesNotContain(resolved.Turned, t => t.Refdes is "C1" or "C2");
    }

    /// <summary>
    /// <b>A tie is left alone.</b> Two capacitors, no stamp, no third part: each reading of either one
    /// is as consistent with the copper as the other, so nothing may be turned — a guess between two
    /// readings the copper cannot tell apart is the guess the reading exists not to make.
    /// </summary>
    [Fact]
    public void ATieIsNeverTurned()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: true, parts: 2);

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx));

        Assert.Empty(resolved.Turned);
    }

    /// <summary>
    /// <b>A diode is never turned</b> — its reversal is a different circuit, not a bookkeeping
    /// convention. The same board with the third part a diode leaves the schematic's order alone.
    /// </summary>
    [Fact]
    public void ADiodeIsNeverTurned()
    {
        var fx = ThreeCapBoard(SymbolKind.Diode, turnThird: true, thirdOnly: true);

        var resolved = RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx));

        Assert.DoesNotContain(resolved.Turned, t => t.Refdes == "C3");
    }

    /// <summary>
    /// <b>The half turn the window offers puts each land exactly where the other was</b>, on a
    /// footprint whose origin is not between its lands — and turning it makes the reading find
    /// nothing left to turn.
    /// </summary>
    [Fact]
    public void AHalfTurnSwapsTheTwoLandsExactly()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: true);
        var turned = Assert.Single(RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx)).Turned);

        var before = fx.View.Instances[turned.InstanceIndex];
        var after = TurnedParts.HalfTurn(before, turned);

        var land1 = LayoutInstanceTransform.TransformPoint(Land0402[0].X, Land0402[0].Y, after, 0, 0);
        var land2 = LayoutInstanceTransform.TransformPoint(Land0402[1].X, Land0402[1].Y, after, 0, 0);
        Assert.Equal(turned.Land2, land1);
        Assert.Equal(turned.Land1, land2);

        fx.View.Instances[turned.InstanceIndex] = after;
        LayoutPersistence.SaveToFile(fx.Clay, fx.View);
        Assert.Empty(RailArtwork.PadsFor(fx.View, fx.Clay, fx.Tech, null, null, Flat(fx)).Turned);
    }

    // ══ 2 — a pad seeds its own land, not the plane under it ════════════════════════════════════

    /// <summary>
    /// <b>With no reference layer named, a pad over a ground plane seeds the pad and not the
    /// plane.</b> The same point without a layer — a netlist record — still reaches every layer,
    /// which is what it has always done.
    /// </summary>
    [Fact]
    public void APadSeedsItsOwnLandAndNotThePlaneUnderIt()
    {
        var tech = TechFixture();
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(2), Y2 = Mm(1) },         // the rail's pad
            new RectShape { Layer = Bot, X1 = -Mm(20), Y1 = -Mm(20), X2 = Mm(20), Y2 = Mm(20) }, // the plane
        };
        var regions = LayerRegions.Build(shapes, tech);
        var nowhere = new LayerKey(99, 0);   // no reference named yet: exclude nothing

        var landed = Regions.Walk(regions, tech, [new PdnNetPoint("VDD", Mm(1), Um(500), Top)],
                                  "VDD", nowhere, null, []);
        var bare   = Regions.Walk(regions, tech, [new PdnNetPoint("VDD", Mm(1), Um(500))],
                                  "VDD", nowhere, null, []);

        Assert.Equal([Top], landed.Power.SelectMany(i => i.Copper).Select(c => c.Layer).Distinct());
        Assert.Contains(Bot, bare.Power.SelectMany(i => i.Copper).Select(c => c.Layer));
    }

    // ══ 3 — the window ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The window names the turned part and turns it in the layout.</b> With no layout window
    /// open the edit goes to the file; the reading then finds nothing left to turn, and the part's
    /// pins stand where the schematic says.
    /// </summary>
    [Fact]
    public void TheTurnGestureWritesTheLayoutAndTheReadingEmpties()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: true);
        var vm = OpenWindowOn(fx);

        Assert.True(vm.HasPartsReadAsTurned);
        Assert.Equal("Turn C3 in the layout", vm.TurnPartsButtonText);

        vm.TurnPartsCommand.Execute(null);

        Assert.Equal("", vm.TurnPartsProblem);
        Assert.False(vm.HasPartsReadAsTurned);
        Assert.Equal(0.0, LayoutPersistence.LoadFromFile(fx.Clay).Instances[2].RotationDegrees);

        var pin1 = Assert.Single(vm.Board!.Pads, p => p.Refdes == "C3" && p.Pin == "1");
        Assert.True(InSupplyStrip(pin1.X, pin1.Y));
    }

    /// <summary>
    /// <b>A net whose copper also holds other nets' pins says which.</b> Two capacitors, one turned
    /// and nothing to tell which: the supply's outline takes in the ground strip, and the window says
    /// the ground pins are standing on it rather than leaving a wrong-looking picture unexplained.
    /// </summary>
    [Fact]
    public void ANetWhoseCopperHoldsOtherNetsPinsSaysWhich()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: true, parts: 2);
        var vm = OpenWindowOn(fx);

        vm.SelectNet("+3V3");

        Assert.NotNull(vm.NetPreview);
        Assert.Contains("'GND' (2 pins)", vm.NetPreviewNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The schematic's ground is one row with the measured return.</b> A ground symbol names its
    /// net <c>0</c>; the plane is stamped <c>GND</c>. Once the reference is confirmed and measured,
    /// the list offers <c>GND</c> once, marked as both.
    /// </summary>
    [Fact]
    public void TheSchematicGroundIsOneRowWithTheMeasuredReturn()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: false, groundPlane: true);
        var vm = OpenWindowOn(fx, rail: true);

        Assert.Contains(vm.AvailableNets, r => r.Name == "0");      // nothing merged before a measurement

        vm.SelectedReferenceLayer = vm.ReferenceLayerOptions.Single(o => o.Name == "BOT");
        vm.ConfirmReferenceCommand.Execute(null);
        vm.RefreshNetMarks();

        Assert.Equal("GND", vm.ReferenceReturnNet);
        Assert.DoesNotContain(vm.AvailableNets, r => r.Name == "0");
        var ground = Assert.Single(vm.AvailableNets, r => r.Name == "GND");
        Assert.Contains("also the schematic's '0'", ground.Mark, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>While a run is extracting the rail, the empty parts pane says so</b> — it had been telling
    /// the designer to confirm the reference and run, directly above a strip saying "solving…".
    /// </summary>
    [Fact]
    public void TheEmptyPartsPaneSaysARunIsInFlight()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: false);
        var vm = OpenWindowOn(fx, rail: true);

        Assert.StartsWith("This rail's copper has not been extracted yet.", vm.PartsEmptyText);
        vm.IsSolving = true;
        Assert.StartsWith("railRF is extracting this rail's copper now.", vm.PartsEmptyText);
    }

    // ══ 4 — the conductor with no drawing layer, and the drawing that is not copper ═════════════

    /// <summary>
    /// <b>The warning names the layer, and one press attaches it.</b> The designer's own stackup: an
    /// inner conductor <c>GND</c> with nothing attached, a drawing layer <c>gnd</c> nothing claims,
    /// and a fabrication drawing named after its file with the suffix <c>FAB</c> — which is not
    /// copper and must not be offered, nor reported as unclaimed copper.
    /// </summary>
    [Fact]
    public void TheUnattachedPlaneIsNamedAndAttachedInOnePress()
    {
        var gnd = new LayerKey(3, 0);
        var fab = new LayerKey(10, 0);
        var tech = TechFixture();
        tech.Layers.Add(new LayerDef { Key = gnd, Name = "gnd", Color = new Rgba(1, 2, 3, 255) });
        tech.Layers.Add(new LayerDef
        {
            Key = fab, Name = "BOARD-1234FAB", Color = new Rgba(4, 5, 6, 255),
            Interchange = new InterchangeMapping(null, null, null, "FAB", null, null),
        });
        tech.Stackup.Layers.Insert(2, new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "GND", ThicknessDbu = Um(18), SigmaSm = 5.8e7,
        });
        tech.Stackup.Layers.Insert(3, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(300), Epsr = 4.3, TanD = 0.02,
        });
        // A second unattached plane, whose name matches nothing: `gnd` is the sole candidate for it
        // too, and offering it there as well attached one plane's copper to two conductors.
        tech.Stackup.Layers.Insert(4, new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "PWR", ThicknessDbu = Um(18), SigmaSm = 5.8e7,
        });
        tech.Stackup.Layers.Insert(5, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Core2", ThicknessDbu = Um(300), Epsr = 4.3, TanD = 0.02,
        });

        var problem = Assert.Single(TechValidation.Analyze(tech), p => p.Fix is not null);
        Assert.Contains("Drawing layer 'gnd'", problem.Message, StringComparison.Ordinal);
        Assert.Equal(gnd, problem.Fix!.Layer);

        var unclaimed = PdnUnclaimedCopper.On(tech, [
            new RectShape { Layer = gnd, X1 = 0, Y1 = 0, X2 = Mm(1), Y2 = Mm(1) },
            new RectShape { Layer = fab, X1 = 0, Y1 = 0, X2 = Mm(1), Y2 = Mm(1) }]);
        Assert.Equal([gnd], unclaimed.Select(u => u.Layer));
        Assert.Contains("conductor 'GND'", PdnUnclaimedCopper.Sentence(unclaimed, tech), StringComparison.Ordinal);

        var editor = new TechEditorViewModel(Path.Combine(_root, "t.ctech"), tech);
        editor.SelectedTabIndex = 1;
        var fix = Assert.Single(editor.ActiveTabFixes);
        editor.ApplyTechFixCommand.Execute(fix);

        Assert.Equal([gnd], editor.Working.Stackup.Layers.Single(l => l.Name == "GND").DrawingLayers);
        Assert.DoesNotContain(editor.ValidationProblems, p => p.Fix is not null);

        editor.UndoCommand.Execute(null);
        Assert.Empty(editor.Working.Stackup.Layers.Single(l => l.Name == "GND").DrawingLayers);
    }

    // ── Opening a .crail shows the window before its board is read (field report, 2026-09-23) ──

    /// <summary>A `.crail` on the three-cap board, opened the way the window opens one: the board read
    /// is DEFERRED, so the test holds the moment between the window appearing and the board arriving.</summary>
    private (RailRfViewModel Vm, Action ReadBoard) OpenDeferred()
    {
        var fx = ThreeCapBoard(SymbolKind.Capacitor, turnThird: false);
        var doc = new RailDocument { Name = "Board", ArtworkCellRef = Path.GetRelativePath(fx.CellDir, fx.Clay) };
        doc.Rails.Add(new RailSpec { Name = "+3V3", NetName = "+3V3" });
        string crail = Path.Combine(fx.CellDir, "Board.crail");
        RailDocumentIo.SaveToFile(crail, doc);

        Action? pending = null;
        var vm = new RailRfViewModel(doc, crail) { PostToUi = a => a() };
        vm.ReadCopperOffThread = work => { pending = work; return System.Threading.Tasks.Task.CompletedTask; };
        return (vm, () => pending!());
    }

    [Fact]
    public void Opening_ShowsTheDocumentFirst_AndTheBoardWhenItHasBeenRead()
    {
        var (vm, readBoard) = OpenDeferred();
        int calls = 0;
        vm.BeginLoadDocumentReferences(_ => calls++);

        // The window is up with the document's own content; the board is being read, not missing.
        Assert.Equal(["+3V3"], vm.Rails);
        Assert.Null(vm.Board);
        Assert.True(vm.IsOpeningBoard);
        Assert.False(vm.ShowsNoBoardYet);
        Assert.False(vm.CanRun);
        Assert.Contains("still being read", vm.RunBlockedReason, StringComparison.Ordinal);
        Assert.Equal(0, calls);

        readBoard();

        Assert.NotNull(vm.Board);
        Assert.False(vm.IsOpeningBoard);
        Assert.False(vm.IsDirty);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Opening_ClosedBeforeTheBoardIsRead_DropsTheRead()
    {
        var (vm, readBoard) = OpenDeferred();
        int calls = 0;
        vm.BeginLoadDocumentReferences(_ => calls++);

        vm.Dispose();
        readBoard();

        Assert.Null(vm.Board);
        Assert.Equal(0, calls);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A railRF window opened on the fixture's <c>.clay</c>, with the copper read inline.</summary>
    internal static RailRfViewModel OpenWindowOn(Fx fx, bool rail = false)
    {
        var doc = new RailDocument
        {
            Name = "Board",
            ArtworkCellRef = Path.GetRelativePath(fx.CellDir, fx.Clay),
        };
        if (rail) doc.Rails.Add(new RailSpec { Name = "+3V3", NetName = "+3V3" });

        string crail = Path.Combine(fx.CellDir, "Board.crail");
        RailDocumentIo.SaveToFile(crail, doc);

        var vm = new RailRfViewModel(doc, crail) { PostToUi = a => a() };
        vm.ReadCopperOffThread = work => { work(); return System.Threading.Tasks.Task.CompletedTask; };
        vm.LoadDocumentReferences();
        return vm;
    }


    internal sealed record Fx(LayoutView View, string Clay, Technology Tech, string CellDir);

    private static IReadOnlyList<LayoutShape> Flat(Fx fx) => RailArtwork.FlattenedShapes(fx.View, fx.Clay, fx.Tech);

    // The supply strip holds every capacitor's pin-1 land as the schematic means it; the ground strip
    // every pin-2 land. See ThreeCapBoard for the coordinates.
    private static bool InSupplyStrip(long x, long y) =>
        x >= Mm(4.4) && x <= Mm(12.8) && y >= Mm(3.1) && y <= Mm(3.4);

    /// <summary>
    /// <paramref name="parts"/> parts across a supply strip and a ground strip, at x = 5, 9, 13 mm.
    /// The first two are placed the schematic's way round; the third, where
    /// <paramref name="turnThird"/>, is turned 180° about its own origin — so its pin 2 lands on the
    /// supply strip and its pin 1 on the ground strip.
    /// </summary>
    internal Fx ThreeCapBoard(
        SymbolKind kind, bool turnThird, int parts = 3, bool thirdOnly = false, bool groundPlane = false)
    {
        var tech = TechFixture();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "Board.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Board.ctech") });

        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        WriteLandCell("Land0402");

        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.Combine("..", "..", "tech", "Board.ctech") };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Mm(4.4), Y1 = Mm(3.1), X2 = Mm(12.8), Y2 = Mm(3.4) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Mm(5.2), Y1 = Mm(2.55), X2 = Mm(13.6), Y2 = Mm(2.9) });

        // A bottom plane stamped GND, stitched to the ground strip by five vias — so the measured
        // return is GND by five stitching vias to three ground pins.
        if (groundPlane)
        {
            view.Shapes.Add(new RectShape { Layer = Bot, Net = "GND", X1 = Mm(0), Y1 = Mm(0), X2 = Mm(20), Y2 = Mm(6) });
            foreach (double vx in new[] { 6.0, 7.5, 10.0, 11.5, 12.5 })
                view.Shapes.Add(new ViaShape
                {
                    Layer = Via, LandingLayer = Bot, X = Mm(vx), Y = Mm(2.7),
                    DrillSize = Um(150), PadSize = Um(250),
                });
        }

        long[] xs = [Mm(5), Mm(9), Mm(13)];
        for (int i = 0; i < parts; i++)
        {
            var inst = new LayoutInstance
            {
                CellRef = Path.Combine("..", "..", "Land0402"),
                X = xs[i], Y = Mm(3), Mag = 1.0, SchematicId = $"C{i + 1}",
            };
            if (turnThird && i == parts - 1) inst.RotationDegrees = 180;
            view.Instances.Add(inst);
        }

        string clay = Path.Combine(layoutDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var model = new SchematicEditModel();
        for (int i = 0; i < parts; i++)
            model.Components.Add(new EditableComponent
            {
                InstanceName = $"C{i + 1}",
                Symbol = thirdOnly && i < parts - 1 ? SymbolKind.Capacitor : kind,
                X = 400 * i, Y = 0,
            });
        var top = new EditableWire(); top.Points.AddRange([(0.0, -200.0), (400.0 * (parts - 1), -200.0)]);
        var bot = new EditableWire(); bot.Points.AddRange([(0.0, 200.0), (400.0 * (parts - 1), 200.0)]);
        model.Wires.Add(top);
        model.Wires.Add(bot);
        model.NetLabels.Add(new EditableNetLabel { X = 200, Y = -200, Name = "+3V3" });
        if (groundPlane) model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 0, Y = 200 });
        else model.NetLabels.Add(new EditableNetLabel { X = 200, Y = 200, Name = "GND" });

        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        SchematicPersistence.SaveToFile(Path.Combine(schDir, "Board.csch"), model, "Board");

        return new Fx(view, clay, tech, cellDir);
    }

    private void WriteLandCell(string name)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x, y) in Land0402)
        {
            cell.Pins.Add(new LayoutPin { Name = pin, X = x, Y = y, WidthDbu = Um(250), Layer = Top });
            cell.Shapes.Add(new RectShape
            {
                Layer = Top, Pin = pin,
                X1 = x - Um(125), Y1 = y - Um(125), X2 = x + Um(125), Y2 = y + Um(125),
            });
        }
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + ".clay"), cell);
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
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Via, Name = "V1", DrawingLayers = [Via], SpanFromLayer = "TOP", SpanToLayer = "BOT" },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP", ThicknessDbu = Um(200), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot], IsGroundReference = true },
        ];
        return tech;
    }
}
