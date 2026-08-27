using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Rotate and mirror re-draw the picture; they never re-wire the circuit</b> (owner, 2026-08-26).
///
/// <para>The oracle is the extracted netlist's own connectivity, taken as a PARTITION rather than as
/// net names: for each net, which (instance, terminal) sit on it. Net names are generated in
/// discovery order, so a bug that swaps two pins can leave the names looking plausible while the
/// circuit has changed — comparing the partition is what makes a pin swap visible. Every test here
/// asserts the partition survives the operation, which is the property the owner asked for.</para>
///
/// <para>Both operations carry the selection as ONE RIGID BODY — see
/// <see cref="SchematicGroupTransform"/> — which is what makes a pin-to-pin contact with no wire
/// between it survive, and is the owner's own second instruction here. The wire-level re-attachment
/// in <see cref="PinFollowReroute"/> then handles what the rigid body cannot: the wire with one end
/// inside the selection and one end outside it.</para>
///
/// <para>Real failures pinned here, all reproduced against <c>NetExtractor</c> before the fix:</para>
/// <list type="bullet">
///   <item>Two resistors in series, both selected, rotated once: <c>R1[n1,n2] R2[n2,n3]</c> came back
///   as <c>R1[n1,n1] R2[n1,n2]</c> — the re-routed wire's first leg was laid straight across R1's own
///   far pin and shorted it out.</item>
///   <item>Two TLINs in series, both selected, mirrored horizontally: <c>T1[n1,n2] T2[n2,n3]</c> came
///   back as <c>T1[n1,n2] T2[n3,n1]</c> — mirror moved no wire at all, so T2's pins traded places
///   underneath a wire that had not moved.</item>
///   <item>A variadic part (an SDD with NumPorts=3) rotated: the wires on the pins that only exist
///   above two ports were left behind, because the re-route asked for the two-port default set.</item>
/// </list>
/// </summary>
public class RotateMirrorConnectivityTests
{
    /// <summary>
    /// The circuit's connectivity, independent of how the nets happen to get named: one line per
    /// net, listing the terminals on it as <c>Instance.Terminal</c>, everything sorted.
    /// </summary>
    private static string Connectivity(SchematicEditModel m)
    {
        var byNet = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var inst in NetExtractor.Extract(m).TestBench.Instances)
            for (int k = 0; k < inst.NetBindings.Count; k++)
            {
                if (!byNet.TryGetValue(inst.NetBindings[k], out var members))
                    byNet[inst.NetBindings[k]] = members = [];
                members.Add($"{inst.InstanceName}.{k}");
            }

        return string.Join("\n", byNet.Values
            .Select(ms => string.Join(" ", ms.OrderBy(s => s, StringComparer.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    private static EditableComponent Comp(SymbolKind kind, string name, double x, double y) =>
        new() { InstanceName = name, Symbol = kind, X = x, Y = y };

    private static void Wire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
    }

    // ── Rotate ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two resistors in series, both selected, rotated: the chain stays a chain.
    ///
    /// <para>This is the owner's own report. The resistor's pins are at local (0,±200), so a
    /// quarter turn lays them out horizontally at the same Y — which is exactly the row the old
    /// horizontal-first L used for its first leg, straight across the pin at the other end.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoComponentsInSeries_RotatedTogether_KeepEveryConnection(bool clockwise)
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        Wire(m, (0, 200), (0, 400));

        var before = Connectivity(m);
        new RotateCommand(m, [r1.Id, r2.Id], clockwise).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// The same fixture, stated as the thing that actually went wrong: R1's two terminals must not
    /// end up on one net. Kept separate from the partition check because a short is the failure the
    /// user sees — the part simply stops doing anything — and it deserves to fail by name.
    /// </summary>
    [Fact]
    public void RotatingTogether_DoesNotShortTheSymbolTheWireIsFollowing()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        Wire(m, (0, 200), (0, 400));

        new RotateCommand(m, [r1.Id, r2.Id], clockwise: false).Execute();

        foreach (var inst in NetExtractor.Extract(m).TestBench.Instances)
            Assert.True(inst.NetBindings[0] != inst.NetBindings[1],
                        $"{inst.InstanceName} was shorted by the re-route");
    }

    /// <summary>
    /// A three-terminal part with all three pins wired to different neighbours — the case where a
    /// pin swap is invisible in the net NAMES and only the partition catches it.
    /// </summary>
    [Fact]
    public void AThreeTerminalPart_RotatedWithItsNeighbours_KeepsEachWireOnItsOwnPin()
    {
        var m  = new SchematicEditModel();
        var q1 = Comp(SymbolKind.FetStatz, "Q1", 0, 0);          // g(-200,0) d(0,-200) s(0,200)
        var rg = Comp(SymbolKind.Resistor, "RG", -1000, 0);      // pins (0,±200) local
        var rd = Comp(SymbolKind.Resistor, "RD", 0, -1000);
        var rs = Comp(SymbolKind.Resistor, "RS", 0, 1000);
        m.Components.Add(q1);
        m.Components.Add(rg);
        m.Components.Add(rd);
        m.Components.Add(rs);
        Wire(m, (-200, 0), (-1000, 0), (-1000, 200));
        Wire(m, (0, -200), (0, -800));
        Wire(m, (0, 200), (0, 800));

        var before = Connectivity(m);
        new RotateCommand(m, [q1.Id, rg.Id, rd.Id, rs.Id], clockwise: true).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A variadic part's real pins are followed, not the two-port default set.
    ///
    /// <para>An SDD with NumPorts=3 has six pins, two of which (at local y = ±300) do not exist on
    /// the two-port symbol at all. The old code asked <c>SymbolPortDefs.For(comp.Symbol)</c> — the
    /// convenience overload, which assumes two ports — so a wire on one of those pins was simply
    /// left where it lay while the pin turned away from under it. The same hole covered every cell
    /// instance and kit part (their pins live in the referenced symbol, so the answer was an empty
    /// list and NO wire moved) and every SnP whose RefNode/PinConfig/Pitch shift its pins.</para>
    /// </summary>
    [Fact]
    public void AVariadicParts_ExtraPins_AreFollowedToo()
    {
        var m   = new SchematicEditModel();
        var sdd = Comp(SymbolKind.Sdd, "SDD1", 0, 0);
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "3" });
        var r1 = Comp(SymbolKind.Resistor, "R1", -1000, -300);
        m.Components.Add(sdd);
        m.Components.Add(r1);
        Assert.Equal(6, m.PortDefsOf(sdd).Count);                // pins at (-200,±300) exist only here

        Wire(m, (-200, -300), (-1000, -300), (-1000, -100));     // onto R1's lower pin
        var before = Connectivity(m);

        new RotateCommand(m, [sdd.Id], clockwise: false).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A DETACHED port drags nothing. The user has explicitly disconnected it, so a wire that merely
    /// passes over the spot is not attached to it and must be left alone — the same exclusion
    /// <c>ComputeConnectivityGeometry</c> makes when it decides what is connected.
    /// </summary>
    [Fact]
    public void ADetachedPin_DoesNotDragTheWireLyingOverIt()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        r1.DetachedPorts.Add(1);                                  // the pin at (0, 200)
        Wire(m, (0, 200), (0, 400));

        new RotateCommand(m, [r1.Id], clockwise: false).Execute();

        Assert.Equal([(0.0, 200.0), (0.0, 400.0)], m.Wires[0].Points);
    }

    // ── Mirror ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two TLINs in series, both selected, mirrored horizontally. A TLIN's pins are at local
    /// (∓200, 0), so a horizontal mirror swaps them exactly — and mirror used to move no wire at
    /// all, which put pin 1's wire on pin 2 with the schematic looking untouched.
    /// </summary>
    [Fact]
    public void MirrorHorizontal_KeepsEachWireOnItsOwnPin()
    {
        var m  = new SchematicEditModel();
        var t1 = Comp(SymbolKind.Tline, "T1", 0, 0);
        var t2 = Comp(SymbolKind.Tline, "T2", 1000, 0);
        m.Components.Add(t1);
        m.Components.Add(t2);
        Wire(m, (200, 0), (800, 0));

        var before = Connectivity(m);
        new MirrorCommand(m, [t1.Id, t2.Id], horizontal: true).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A vertical mirror is the flag flip plus a half turn, so it moves the pins that a horizontal
    /// one leaves alone — a resistor's (0, ±200) pair trades ends. Its wires have to follow.
    /// </summary>
    [Fact]
    public void MirrorVertical_KeepsEachWireOnItsOwnPin()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        Wire(m, (0, 200), (0, 400));

        var before = Connectivity(m);
        new MirrorCommand(m, [r1.Id, r2.Id], horizontal: false).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A three-terminal part mirrored on its own, with each pin going somewhere different — the
    /// case a symmetric two-pin fixture cannot see.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AThreeTerminalPart_Mirrored_KeepsEachWireOnItsOwnPin(bool horizontal)
    {
        var m  = new SchematicEditModel();
        var q1 = Comp(SymbolKind.FetStatz, "Q1", 0, 0);
        var rg = Comp(SymbolKind.Resistor, "RG", -1000, 0);
        var rd = Comp(SymbolKind.Resistor, "RD", 0, -1000);
        var rs = Comp(SymbolKind.Resistor, "RS", 0, 1000);
        m.Components.Add(q1);
        m.Components.Add(rg);
        m.Components.Add(rd);
        m.Components.Add(rs);
        Wire(m, (-200, 0), (-1000, 0), (-1000, 200));
        Wire(m, (0, -200), (0, -800));
        Wire(m, (0, 200), (0, 800));

        var before = Connectivity(m);
        new MirrorCommand(m, [q1.Id], horizontal).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Undo puts the wires back too, not just the orientation — mirror now moves wires, so its Undo
    /// has to restore them or the operation is not reversible.
    /// </summary>
    [Fact]
    public void Undo_RestoresTheOrientationAndTheWiresTogether()
    {
        var m  = new SchematicEditModel();
        var t1 = Comp(SymbolKind.Tline, "T1", 0, 0);
        var t2 = Comp(SymbolKind.Tline, "T2", 1000, 0);
        m.Components.Add(t1);
        m.Components.Add(t2);
        Wire(m, (200, 0), (800, 0));

        var before      = Connectivity(m);
        var beforePts   = m.Wires[0].Points.ToList();

        var mirror = new MirrorCommand(m, [t1.Id, t2.Id], horizontal: true);
        mirror.Execute();
        mirror.Undo();

        Assert.False(t1.MirrorX);
        Assert.Equal(beforePts, m.Wires[0].Points);
        Assert.Equal(before, Connectivity(m));

        var rotate = new RotateCommand(m, [t1.Id, t2.Id], clockwise: false);
        rotate.Execute();
        rotate.Undo();

        Assert.Equal(SymbolRotation.R0, t1.Rotation);
        Assert.Equal(beforePts, m.Wires[0].Points);
        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// Four turns of the same selection is the identity — orientation AND connectivity. A re-route
    /// that quietly drifts a wire onto a neighbouring pin would survive one rotation looking fine
    /// and show up here.
    /// </summary>
    [Fact]
    public void FourRotations_ReturnTheCircuitToWhereItStarted()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        Wire(m, (0, 200), (0, 400));

        var before = Connectivity(m);
        for (int i = 0; i < 4; i++)
        {
            new RotateCommand(m, [r1.Id, r2.Id], clockwise: false).Execute();
            Assert.Equal(before, Connectivity(m));
        }

        Assert.Equal(SymbolRotation.R0, r1.Rotation);
        Assert.Equal(SymbolRotation.R0, r2.Rotation);
    }

    // ── Touching pins (owner's second instruction) ────────────────────────────

    /// <summary>
    /// <b>Two components abutted pin-to-pin, with NO wire between them, are still abutted
    /// afterwards.</b> This is the owner's own statement of the rule, and it is the case that forced
    /// the rigid-body transform: a connection made of nothing but an overlap has no wire to
    /// re-route, so the only way to keep it is to not break it — one pivot, everything carried.
    /// </summary>
    [Theory]
    [InlineData("rot-ccw")]
    [InlineData("rot-cw")]
    [InlineData("mirror-h")]
    [InlineData("mirror-v")]
    public void PinsThatTouch_AreStillTouchingAfterwards(string op)
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);        // pins (0,-200) (0,200)
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 400);      // pins (0, 200) (0,600)
        m.Components.Add(r1);
        m.Components.Add(r2);
        Assert.Equal(m.PortWorldOf(r1, m.PortDefsOf(r1)[1]), m.PortWorldOf(r2, m.PortDefsOf(r2)[0]));

        var before = Connectivity(m);
        Run(m, op, [r1.Id, r2.Id]);

        Assert.Equal(m.PortWorldOf(r1, m.PortDefsOf(r1)[1]), m.PortWorldOf(r2, m.PortDefsOf(r2)[0]));
        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// The same, on a chain of three with a mix of contacts and a wire — the arrangement where a
    /// per-symbol spin scatters the parts in three different directions at once.
    /// </summary>
    [Theory]
    [InlineData("rot-ccw")]
    [InlineData("mirror-h")]
    public void AChainOfAbuttedPartsAndWires_SurvivesIntact(string op)
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 400);      // abutted to R1 at (0,200)
        var r3 = Comp(SymbolKind.Resistor, "R3", 0, 1200);     // wired to R2
        m.Components.Add(r1);
        m.Components.Add(r2);
        m.Components.Add(r3);
        Wire(m, (0, 600), (0, 1000));

        var before = Connectivity(m);
        Run(m, op, [r1.Id, r2.Id, r3.Id]);

        Assert.Equal(before, Connectivity(m));
        Assert.Equal(m.PortWorldOf(r1, m.PortDefsOf(r1)[1]), m.PortWorldOf(r2, m.PortDefsOf(r2)[0]));
    }

    // ── Rigid-body behaviour ──────────────────────────────────────────────────

    /// <summary>
    /// The wire between two selected parts is CARRIED, not re-drawn: its bends are the user's, and a
    /// rigid transform has no reason to throw them away. A three-vertex wire comes out with three
    /// vertices, rigidly mapped.
    /// </summary>
    [Fact]
    public void TheWireBetweenTwoSelectedParts_IsCarriedWhole()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 600, 600);
        m.Components.Add(r1);
        m.Components.Add(r2);
        Wire(m, (0, 200), (0, 400), (600, 400));               // an L the user drew

        var before = Connectivity(m);
        new RotateCommand(m, [r1.Id, r2.Id], clockwise: false).Execute();

        Assert.Equal(3, m.Wires[0].Points.Count);
        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A rigid transform is only safe if it lands back on the connection grid — connectivity here is
    /// coincidence within half a world unit, so a group left half a pitch out is a group connected to
    /// nothing. The pivot is snapped for exactly this reason; with an odd number of parts at odd
    /// spacings the un-snapped centroid falls between grid lines.
    /// </summary>
    [Fact]
    public void TheGroupLandsBackOnTheConnectionGrid()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 500);      // centroid y = 250 — off the 100 grid
        var r3 = Comp(SymbolKind.Resistor, "R3", 300, 500);
        m.Components.Add(r1);
        m.Components.Add(r2);
        m.Components.Add(r3);
        Wire(m, (0, 200), (0, 300));

        new RotateCommand(m, [r1.Id, r2.Id, r3.Id], clockwise: false).Execute();

        foreach (var c in m.Components)
        {
            Assert.Equal(0.0, c.X % m.GridSize);
            Assert.Equal(0.0, c.Y % m.GridSize);
        }
        foreach (var (x, y) in m.Wires[0].Points)
        {
            Assert.Equal(0.0, x % m.GridSize);
            Assert.Equal(0.0, y % m.GridSize);
        }
    }

    /// <summary>
    /// One component on its own still turns in place. The pivot is its own origin, so "place a part,
    /// press R" re-orients it and never slides it — the behaviour this editor has always had, and the
    /// reason the group pivot is special-cased rather than applied uniformly.
    /// </summary>
    [Fact]
    public void ASingleComponent_TurnsInPlace()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 300, 700);
        m.Components.Add(r1);

        new RotateCommand(m, [r1.Id], clockwise: false).Execute();

        Assert.Equal(300, r1.X);
        Assert.Equal(700, r1.Y);
        Assert.Equal(SymbolRotation.R90, r1.Rotation);
    }

    /// <summary>
    /// <b>Mirror is a real reflection now, including on a rotated symbol.</b> It used to toggle the
    /// mirror flag and leave the rotation alone, which reflects about the symbol's OWN x axis — after
    /// a quarter turn that is the world's Y axis, so Mirror Horizontal on an R90 part mirrored it
    /// vertically (here, visibly: it did nothing at all to a resistor). The correct rule negates the
    /// rotation, and a horizontal flip of a horizontal resistor swaps which end each pin is on.
    /// </summary>
    [Fact]
    public void MirrorHorizontal_OnARotatedSymbol_ActuallyReflectsIt()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        r1.Rotation = SymbolRotation.R90;                      // lying horizontally
        m.Components.Add(r1);
        var pin0Before = m.PortWorldOf(r1, m.PortDefsOf(r1)[0]);
        var pin1Before = m.PortWorldOf(r1, m.PortDefsOf(r1)[1]);
        Assert.NotEqual(pin0Before, pin1Before);

        new MirrorCommand(m, [r1.Id], horizontal: true).Execute();

        Assert.Equal(pin1Before, m.PortWorldOf(r1, m.PortDefsOf(r1)[0]));
        Assert.Equal(pin0Before, m.PortWorldOf(r1, m.PortDefsOf(r1)[1]));
    }

    /// <summary>A rotated symbol's wires survive a mirror too — the reflection moved its pins, so
    /// this is the case the old no-op mirror could not have got wrong and the new one could.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MirroringARotatedSymbol_KeepsEachWireOnItsOwnPin(bool horizontal)
    {
        var m  = new SchematicEditModel();
        var q1 = Comp(SymbolKind.FetStatz, "Q1", 0, 0);
        q1.Rotation = SymbolRotation.R270;
        var rg = Comp(SymbolKind.Resistor, "RG", 0, -1000);
        var rd = Comp(SymbolKind.Resistor, "RD", -1000, 0);
        var rs = Comp(SymbolKind.Resistor, "RS", 1000, 0);
        m.Components.Add(q1);
        m.Components.Add(rg);
        m.Components.Add(rd);
        m.Components.Add(rs);
        foreach (var def in m.PortDefsOf(q1))
        {
            var (px, py) = m.PortWorldOf(q1, def);
            Wire(m, (px, py), (px * 3, py * 3));
        }

        var before = Connectivity(m);
        new MirrorCommand(m, [q1.Id], horizontal).Execute();

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>Four mirrors of the same axis, and four turns, are each the identity — orientation,
    /// position and connectivity. A pivot that drifted a grid step per operation shows up here.</summary>
    [Fact]
    public void RepeatedOperations_ReturnTheGroupExactlyToWhereItStarted()
    {
        var m  = new SchematicEditModel();
        var r1 = Comp(SymbolKind.Resistor, "R1", 0, 0);
        var r2 = Comp(SymbolKind.Resistor, "R2", 0, 400);
        var r3 = Comp(SymbolKind.Resistor, "R3", 0, 1200);
        m.Components.Add(r1);
        m.Components.Add(r2);
        m.Components.Add(r3);
        Wire(m, (0, 600), (0, 1000));

        var before = Connectivity(m);
        var places = m.Components.Select(c => (c.X, c.Y, c.Rotation, c.MirrorX)).ToList();
        string[] ids = [r1.Id, r2.Id, r3.Id];

        for (int i = 0; i < 4; i++) new RotateCommand(m, ids, clockwise: false).Execute();
        Assert.Equal(places, m.Components.Select(c => (c.X, c.Y, c.Rotation, c.MirrorX)).ToList());
        Assert.Equal(before, Connectivity(m));

        for (int i = 0; i < 2; i++) new MirrorCommand(m, ids, horizontal: true).Execute();
        Assert.Equal(places, m.Components.Select(c => (c.X, c.Y, c.Rotation, c.MirrorX)).ToList());
        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>Runs one of the four gestures by name, so a fixture can be checked against all of
    /// them without four near-identical copies.</summary>
    private static void Run(SchematicEditModel m, string op, string[] ids)
    {
        switch (op)
        {
            case "rot-ccw":  new RotateCommand(m, ids, clockwise: false).Execute(); break;
            case "rot-cw":   new RotateCommand(m, ids, clockwise: true).Execute();  break;
            case "mirror-h": new MirrorCommand(m, ids, horizontal: true).Execute(); break;
            default:         new MirrorCommand(m, ids, horizontal: false).Execute(); break;
        }
    }
}
