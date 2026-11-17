using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A part placed onto a wire with two of its pins on it clears the stretch of wire between them, so
/// the part is in series with the run instead of shorted by it — but only at PLACEMENT, and only
/// when the cut provably changes no circuit. See <see cref="SeriesPartInsertion"/>.
///
/// <para>Most of these drive an IProbe because it is the shorter fixture — its pins are one
/// connection pitch apart rather than the lumped primitives' four — but the KIND is not what the
/// rule turns on: it is asked geometrically, so the R/L/C half is the same code and is gated by the
/// two-terminal cases at the bottom. <c>WSProbePlacementTests</c> holds the WSProbe one.</para>
///
/// <para>Grid snap is off in these fixtures on purpose: it lets a junction be put at the MIDDLE of
/// the span between the two pins, which the shipped 100-unit connection grid has no coordinate for
/// (the probe's pins are exactly one pitch apart). The rule under test is geometric, not grid-bound.</para>
/// </summary>
public class SeriesPartPlacementClearsWireTests
{
    // R0 IProbe pins sit at (X, Y+100) and (X+100, Y+100) — so this places the two pins at
    // (px, py) and (px+100, py).
    private const double PinDrop = 100.0;

    private static (SchematicEditModel Model, SchematicViewModel Vm) MakeVm()
    {
        var model = new SchematicEditModel { GridSnap = false };
        return (model, new SchematicViewModel(model));
    }

    private static EditableWire AddWire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
        return w;
    }

    private static void PlaceProbe(SchematicViewModel vm, double pinX, double pinY,
                                   SymbolRotation rot = SymbolRotation.R0)
        => vm.CommitPlacement(SymbolKind.IProbe, 2, rot, pinX, pinY - PinDrop);

    private static (double X, double Y)[] Pts(EditableWire w) => w.Points.ToArray();

    // ── The affordance ────────────────────────────────────────────────────────

    [Fact]
    public void PlacedOnHorizontalWire_RemovesOnlyTheSpanBetweenThePins()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));

        PlaceProbe(vm, 200, 0);

        Assert.Single(model.Components);
        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (200.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[1]));
    }

    [Fact]
    public void PlacedOnVerticalWire_RemovesOnlyTheSpanBetweenThePins()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (0, 500));

        // R90 puts both pins on x = X-100, at y = Y and y = Y+100.
        vm.CommitPlacement(SymbolKind.IProbe, 2, SymbolRotation.R90, 100, 200);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (0.0, 200.0)],   Pts(model.Wires[0]));
        Assert.Equal([(0.0, 300.0), (0.0, 500.0)], Pts(model.Wires[1]));
    }

    [Fact]
    public void TheCutAndThePlacement_AreOneUndoEntry()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));

        PlaceProbe(vm, 200, 0);
        vm.UndoRedo.Undo();

        Assert.Empty(model.Components);
        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));

        vm.UndoRedo.Redo();
        Assert.Single(model.Components);
        Assert.Equal(2, model.Wires.Count);
    }

    [Fact]
    public void APinLandingOnTheWiresOwnEnd_LeavesNoStubBehind()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (200, 0), (500, 0));

        PlaceProbe(vm, 200, 0);

        // Nothing survives on the left: whatever held that wire end now meets the probe pin there.
        Assert.Single(model.Wires);
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));
    }

    [Fact]
    public void AWireExactlySpannedByTheProbe_IsRemovedEntirely()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (200, 0), (300, 0));

        PlaceProbe(vm, 200, 0);

        Assert.Empty(model.Wires);
        Assert.Single(model.Components);
    }

    [Fact]
    public void ACornerOutsideTheSpan_Survives()
    {
        var (model, vm) = MakeVm();
        // L-shaped run: down, then right. The probe goes on the horizontal leg.
        AddWire(model, (0, -400), (0, 0), (500, 0));

        PlaceProbe(vm, 200, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, -400.0), (0.0, 0.0), (200.0, 0.0)], Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)],              Pts(model.Wires[1]));
    }

    /// <summary>
    /// The same thing on the SHIPPED grid rather than the free-coordinate fixtures above — the
    /// probe's pins are exactly one connection pitch apart, so this is the gesture a user actually
    /// makes. Both pins read connected afterwards: the run is broken in series, not left dangling.
    /// </summary>
    [Fact]
    public void OnTheShippedGrid_TheProbeLandsInSeriesAndConnectedBothSides()
    {
        var model = new SchematicEditModel();          // grid snap on, 100-unit connection grid
        var vm    = new SchematicViewModel(model);
        AddWire(model, (0, 0), (500, 0));

        vm.CommitPlacement(SymbolKind.IProbe, 2, SymbolRotation.R0, 200, 0 - PinDrop);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (200.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[1]));

        var probe = model.BuildRenderModel().Model.Components[0];
        Assert.Equal(PortConnectionState.Connected, probe.Ports[0].State);
        Assert.Equal(PortConnectionState.Connected, probe.Ports[1].State);
    }

    // ── The refusals: anything in the span means the cut would change the circuit ──

    [Fact]
    public void ATJunctionInsideTheSpan_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        AddWire(model, (250, 0), (250, 400));       // stub tapping the middle of the span

        PlaceProbe(vm, 200, 0);

        Assert.Single(model.Components);
        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));
    }

    [Fact]
    public void ACrossingInsideTheSpan_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        AddWire(model, (250, -400), (250, 400));    // passes straight through the span

        PlaceProbe(vm, 200, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));
    }

    [Fact]
    public void AJunctionDotInsideTheSpan_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        model.Dots.Add(new EditableDot { X = 250, Y = 0 });

        PlaceProbe(vm, 200, 0);

        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));
    }

    [Fact]
    public void AnotherComponentsPinInsideTheSpan_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        // Ground's single pin sits at its own origin.
        vm.CommitPlacement(SymbolKind.Ground, 1, SymbolRotation.R0, 250, 0);

        PlaceProbe(vm, 200, 0);

        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)], Pts(model.Wires[0]));
    }

    [Fact]
    public void ASecondWireLyingAlongTheSpan_LeavesBothAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        AddWire(model, (150, 0), (250, 0));   // duplicate run overlapping half the span

        PlaceProbe(vm, 200, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(150.0, 0.0), (250.0, 0.0)], Pts(model.Wires[1]));
    }

    /// <summary>
    /// A DOUBLED run — two wires drawn along the same line — is cut on BOTH, not refused. Two wires
    /// on one line draw as one line, so the doubling is invisible: refusing left the affordance
    /// silently inert everywhere along that run, with nothing on the sheet to explain it. Removing
    /// the same stretch from every wire that carried it IS the hand edit. Reported against a design
    /// whose input run carried a redundant second wire over part of its length.
    /// </summary>
    [Fact]
    public void TwoWiresCoveringTheWholeSpan_AreBothCut()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        AddWire(model, (100, 0), (400, 0));

        PlaceProbe(vm, 200, 0);

        Assert.Equal(4, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (200.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[1]));
        Assert.Equal([(100.0, 0.0), (200.0, 0.0)], Pts(model.Wires[2]));
        Assert.Equal([(300.0, 0.0), (400.0, 0.0)], Pts(model.Wires[3]));

        // One undo entry still, and it puts both wires back as they were.
        vm.UndoRedo.Undo();
        Assert.Empty(model.Components);
        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (500.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(100.0, 0.0), (400.0, 0.0)], Pts(model.Wires[1]));
    }

    [Fact]
    public void AWireEndingExactlyAtAProbePin_IsNotAJunctionInTheSpan()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (500, 0));
        AddWire(model, (300, 0), (300, 400));   // taps the run AT the second pin, not between

        PlaceProbe(vm, 200, 0);

        // The tap stays connected to the probe's own pin, so the cut is still safe.
        Assert.Equal(3, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (200.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[1]));
        Assert.Equal([(300.0, 0.0), (300.0, 400.0)], Pts(model.Wires[2]));
    }

    [Fact]
    public void PinsOnTwoDifferentWires_LeaveBothAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0),   (200, 0));
        AddWire(model, (300, 0), (500, 0));

        PlaceProbe(vm, 200, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (200.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(300.0, 0.0), (500.0, 0.0)], Pts(model.Wires[1]));
    }

    [Fact]
    public void PlacedOnEmptyCanvas_ChangesNothingButAddsTheProbe()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 900), (500, 900));

        PlaceProbe(vm, 200, 0);

        Assert.Single(model.Components);
        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 900.0), (500.0, 900.0)], Pts(model.Wires[0]));
    }

    // ── Every two-terminal part, not a list of kinds ──────────────────────────

    /// <summary>
    /// The lumped primitives reach the same rule by the same route — a resistor is shorted by the
    /// wire it is dropped on exactly as an ammeter is, and unlike the probe it still SIMULATES that
    /// way, silently contributing nothing. R0 puts the two pins at local (0,±200), so a rotated
    /// instance dropped on a horizontal run spans 400 of it.
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.Resistor)]
    [InlineData(SymbolKind.Capacitor)]
    [InlineData(SymbolKind.NonlinearC)]
    [InlineData(SymbolKind.Diode)]        // its own pin table, not the two-terminal default
    [InlineData(SymbolKind.Tline)]        // already horizontal: pins at local (±200, 0)
    public void ATwoTerminalPartPlacedAlongAWire_CutsTheSpanBetweenItsPins(SymbolKind kind)
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));

        // R90 for the vertical parts turns local (0,±200) into world (∓200, 0); TLIN is already
        // horizontal and R90 would stand it up, so it is placed unrotated. Either way the pins land
        // at (300,0) and (700,0).
        var rot = kind == SymbolKind.Tline ? SymbolRotation.R0 : SymbolRotation.R90;
        vm.CommitPlacement(kind, 2, rot, 500, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));
    }

    /// <summary>
    /// The unrotated gesture: the lumped primitives draw vertically, so the run they break into is
    /// a vertical one. Both pins read connected afterwards — the part is in series, not dangling.
    /// </summary>
    [Fact]
    public void AVerticalPartOnAVerticalWire_LandsInSeriesAndConnectedBothSides()
    {
        var model = new SchematicEditModel();          // shipped grid, snap on
        var vm    = new SchematicViewModel(model);
        AddWire(model, (0, 0), (0, 900));

        vm.CommitPlacement(SymbolKind.Inductor, 2, SymbolRotation.R0, 0, 500);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (0.0, 300.0)],   Pts(model.Wires[0]));
        Assert.Equal([(0.0, 700.0), (0.0, 900.0)], Pts(model.Wires[1]));

        var part = model.BuildRenderModel().Model.Components[0];
        Assert.Equal(PortConnectionState.Connected, part.Ports[0].State);
        Assert.Equal(PortConnectionState.Connected, part.Ports[1].State);
    }

    /// <summary>
    /// Brief DFR26-5 R-dfr26-5-2: a part with more pins qualifies when exactly two of them land on the run
    /// and the rest are off it. An MTEE dropped along a wire is in series between pins 1 and 2, its branch
    /// pin left for the user — before this it shorted its own through line. MTee pins sit at local
    /// (-200,0), (200,0) and (0,200).
    /// </summary>
    [Fact]
    public void AnMTeeDroppedAlongAWire_CutsTheSpanBetweenItsThroughPins()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));

        vm.CommitPlacement(SymbolKind.MTee, 3, SymbolRotation.R0, 500, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));
    }

    /// <summary>The same for an MCROSS, between its two pins on the wire (1 at local (200,0), 3 at (-200,0)).</summary>
    [Fact]
    public void AnMCrossDroppedAlongAWire_CutsTheSpanBetweenItsCollinearPins()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));

        vm.CommitPlacement(SymbolKind.MCross, 4, SymbolRotation.R0, 500, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));
    }

    /// <summary>Every safety condition still holds for a tee: a wire teeing into the span is a third
    /// connection, and cutting under it would re-partition the net.</summary>
    [Fact]
    public void AnMTeeOverAJunctionInsideTheSpan_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));
        AddWire(model, (400, 0), (400, -300));

        vm.CommitPlacement(SymbolKind.MTee, 3, SymbolRotation.R0, 500, 0);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (900.0, 0.0)], Pts(model.Wires[0]));
    }

    /// <summary>The branch pin landing on ANOTHER segment of the wire being cut would stay tied to one of
    /// the pieces — still shorted to a through pin — so nothing is cut.</summary>
    [Fact]
    public void AnMTeeWhoseBranchPinIsOnTheSameWire_LeavesTheWireAlone()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0), (900, 200), (500, 200));

        vm.CommitPlacement(SymbolKind.MTee, 3, SymbolRotation.R0, 500, 0);

        Assert.Single(model.Wires);
        Assert.Equal(4, model.Wires[0].Points.Count);
    }

    /// <summary>
    /// A two-pin part dropped ACROSS a run, connected to nothing, is turned a quarter turn to lie along it
    /// and cut in: an S2P (drawn horizontally) on a vertical wire, port 1 at the top; a resistor (drawn
    /// vertically) on a horizontal wire, pin 1 at the left. Each used to land straddling the run.
    /// </summary>
    [Fact]
    public void AnS2PDroppedAcrossAVerticalWire_IsTurnedAlongItAndCutIn()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (0, 900));

        vm.CommitPlacement(SymbolKind.Snp, 2, SymbolRotation.R0, 0, 500);   // pins would be (±200, 500)

        var part = Assert.Single(model.Components);
        var pins = model.PortDefsOf(part).Select(d => model.PortWorldOf(part, d)).ToList();
        Assert.Equal((0.0, 300.0), pins[0]);   // port 1 at the top
        Assert.Equal((0.0, 700.0), pins[1]);
        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (0.0, 300.0)],   Pts(model.Wires[0]));
        Assert.Equal([(0.0, 700.0), (0.0, 900.0)], Pts(model.Wires[1]));
    }

    [Fact]
    public void AResistorDroppedAcrossAHorizontalWire_IsTurnedAlongItAndCutIn()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));

        vm.CommitPlacement(SymbolKind.Resistor, 2, SymbolRotation.R0, 500, 0);   // pins would be (500, ±200)

        var part = Assert.Single(model.Components);
        var pins = model.PortDefsOf(part).Select(d => model.PortWorldOf(part, d)).ToList();
        Assert.Equal((300.0, 0.0), pins[0]);   // pin 1 at the left
        Assert.Equal((700.0, 0.0), pins[1]);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));
    }

    /// <summary>An MBEND's two pins are at right angles, so no turn lands both on one run: it is never turned
    /// and never cuts.</summary>
    [Fact]
    public void AnMBendDroppedOnAWire_NeverCutsIt()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));

        vm.CommitPlacement(SymbolKind.MBend, 2, SymbolRotation.R0, 700, 0);   // pins at (500,0)/(700,200)

        Assert.Equal(SymbolRotation.R0, Assert.Single(model.Components).Rotation);
        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 0.0), (900.0, 0.0)], Pts(model.Wires[0]));
    }

    /// <summary>A part with a pin already on a wire is a connection somebody made, a shunt to the run: it is
    /// placed as dropped, never turned into series.</summary>
    [Fact]
    public void APartWithAPinOnAWire_IsNotTurned()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 0), (900, 0));       // the resistor's pin 1 lands here: a shunt from this run
        AddWire(model, (0, 200), (900, 200));   // runs through its centre: a turn would put it in series here

        vm.CommitPlacement(SymbolKind.Resistor, 2, SymbolRotation.R0, 500, 200);   // pins (500,0) and (500,400)

        Assert.Equal(SymbolRotation.R0, Assert.Single(model.Components).Rotation);
        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 200.0), (900.0, 200.0)], Pts(model.Wires[1]));
    }

    // ── Scope: placement only ─────────────────────────────────────────────────

    [Fact]
    public void DraggingAPlacedProbeOntoAWire_NeverCutsIt()
    {
        var (model, vm) = MakeVm();
        AddWire(model, (0, 900), (500, 900));      // far from where the probe is placed
        PlaceProbe(vm, 200, 0);
        var probe = Assert.Single(model.Components);

        // Move it so its pins land on (200,900) and (300,900) — the same geometry that WOULD have
        // been cut at placement.
        vm.Execute(new MoveCommand(model,
            [new ComponentMoveSnapshot(probe, probe.X, probe.Y, 200, 900 - PinDrop)],
            [], []));

        Assert.Equal((200.0, 900.0), probe.GetPortWorldCoord(0));
        Assert.Single(model.Wires);
        Assert.Equal([(0.0, 900.0), (500.0, 900.0)], Pts(model.Wires[0]));
    }
}
