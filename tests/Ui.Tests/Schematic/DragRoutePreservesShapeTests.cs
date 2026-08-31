using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// User-reported drag-follow defects (owner testing, 2026-08-30). Two are DISCONNECTS — a mid-wire
/// tap dropped when the wire's endpoint followed a moved pin — and the rest are shape damage: the
/// follow re-route threw the whole wire away and drew a bare L, so a vertical run came back
/// horizontal and a horizontal run shifted off its row.
///
/// The geometry here is taken from the reporter's own schematics, rebuilt in code.
/// </summary>
public class DragRoutePreservesShapeTests
{
    private static EditableComponent Comp(SymbolKind kind, double x, double y, string name,
                                          SymbolRotation rot = SymbolRotation.R0)
        => new() { Symbol = kind, X = x, Y = y, InstanceName = name, Rotation = rot };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1.0;

    private static string NetOf(SchematicEditModel model, string inst, int port)
    {
        var tb = NetExtractor.Extract(model).TestBench;
        return tb.Instances.First(i => i.InstanceName == inst).NetBindings[port];
    }

    private static string Fmt(IEnumerable<(double X, double Y)> pts)
        => string.Join(" ", pts.Select(p => $"({p.X},{p.Y})"));

    // ── 02.csch: L1 —— (600,-400)..(100,-400) —— C1, with C2 tapping at (400,-400) ──
    private static (SchematicEditModel Model, EditableWire W) Sheet02()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Comp(SymbolKind.Inductor,  800, -400, "L1", SymbolRotation.R270));
        m.Components.Add(Comp(SymbolKind.Capacitor, 100, -200, "C1"));
        m.Components.Add(Comp(SymbolKind.Capacitor, 400, -200, "C2"));
        m.Components.Add(Comp(SymbolKind.Pin,      1300, -400, "Pin1", SymbolRotation.R180));
        var w = Wire((600, -400), (100, -400));
        m.Wires.Add(w);
        m.Wires.Add(Wire((1000, -400), (1200, -400)));
        return (m, w);
    }

    /// <summary>BUG — dragging L1 north drops C2, whose top pin taps the wire mid-span.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Bug_02_DragInductorVertically_KeepsMidSpanTap(double dy)
    {
        var (m, w) = Sheet02();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "L1").Id);
        vm.SimulateDragCommit(0, dy);

        Assert.Equal(NetOf(m, "C1", 0), NetOf(m, "C2", 0));
        Assert.Equal(NetOf(m, "C2", 0), NetOf(m, "L1", 0));
    }

    /// <summary>
    /// ANNOYING — dragging C2 (a mid-span tap) south must not bend the wire that L1 and C1 hold at
    /// both ends. The horizontal run stays on its row; C2 grows its own vertical stub up to it.
    /// </summary>
    [Fact]
    public void Annoying_02_DragTapDown_LeavesTheHeldWireOnItsRow()
    {
        var (m, w) = Sheet02();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(0, 200);

        Assert.All(w.Points, p => Assert.True(Near(p.Y, -400),
            $"held wire left its row: {Fmt(w.Points)}"));
        Assert.Equal(NetOf(m, "C1", 0), NetOf(m, "C2", 0));
    }

    // ── 01.csch ────────────────────────────────────────────────────────────────
    private static SchematicEditModel Sheet01()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Comp(SymbolKind.BjtNpn,   -300, -100, "Q1"));
        m.Components.Add(Comp(SymbolKind.Capacitor, 100, -200, "C1"));
        m.Components.Add(Comp(SymbolKind.Inductor, -300,  800, "L2"));
        m.Components.Add(Comp(SymbolKind.Capacitor, 400, -200, "C2"));
        m.Wires.Add(Wire((-300, -300), (-300, -400), (100, -400)));   // 0 — Q1 collector up, over to C1
        m.Wires.Add(Wire((-300,  100), ( 100,  100), (100,    0)));   // 1
        m.Wires.Add(Wire((-300,  100), (-300,  600)));                // 2 — down to L2 top
        m.Wires.Add(Wire(( 400,    0), ( 400, 1000), (-300, 1000)));  // 3 — C2 bottom round to L2 bottom
        m.Wires.Add(Wire(( 100, -400), ( 400, -400)));                // 4
        return m;
    }

    /// <summary>ANNOYING — nudging C2 sideways must leave wire 3's first leg vertical.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Annoying_01_NudgeC2_FirstLegStaysVertical(double dx)
    {
        var m  = Sheet01();
        var w3 = m.Wires[3];
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(dx, 0);

        Assert.True(Near(w3.Points[0].X, w3.Points[1].X),
            $"first leg is no longer vertical: {Fmt(w3.Points)}");
        Assert.True(Near(w3.Points[^1].Y, 1000) && Near(w3.Points[^2].Y, 1000),
            $"the y=1000 run moved off its row: {Fmt(w3.Points)}");
    }

    /// <summary>ANNOYING — nudging C1 sideways must leave wire 0's horizontal leg at y=-400.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Annoying_01_NudgeC1_HorizontalRunKeepsItsY(double dx)
    {
        var m  = Sheet01();
        var w0 = m.Wires[0];
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C1").Id);
        vm.SimulateDragCommit(dx, 0);

        Assert.True(Near(w0.Points[^1].Y, -400) && Near(w0.Points[^2].Y, -400),
            $"the horizontal run shifted off y=-400: {Fmt(w0.Points)}");
        Assert.True(Near(w0.Points[0].X, -300) && Near(w0.Points[0].Y, -300),
            $"the far end moved: {Fmt(w0.Points)}");
    }

    /// <summary>ANNOYING — nudging L2 sideways must leave wire 3's y=1000 run on its row.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Annoying_01_NudgeL2_BottomRunKeepsItsY(double dx)
    {
        var m  = Sheet01();
        var w3 = m.Wires[3];
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "L2").Id);
        vm.SimulateDragCommit(dx, 0);

        Assert.True(Near(w3.Points[^1].Y, 1000) && Near(w3.Points[^2].Y, 1000),
            $"the y=1000 run shifted: {Fmt(w3.Points)}");
        Assert.True(Near(w3.Points[0].X, 400) && Near(w3.Points[0].Y, 0),
            $"the C2 end moved: {Fmt(w3.Points)}");
    }

    /// <summary>ANNOYING — nudging Q1 sideways must leave wire 0's first leg vertical.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Annoying_01_NudgeQ1_TopLegStaysVertical(double dx)
    {
        var m  = Sheet01();
        var w0 = m.Wires[0];
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "Q1").Id);
        vm.SimulateDragCommit(dx, 0);

        Assert.True(Near(w0.Points[0].X, w0.Points[1].X),
            $"first leg is no longer vertical: {Fmt(w0.Points)}");
        Assert.True(Near(w0.Points[^1].X, 100) && Near(w0.Points[^1].Y, -400),
            $"the C1 end moved: {Fmt(w0.Points)}");
    }

    // ── 03.csch: L1 —— (-1700,-500)..(-1200,-500) —— L2, C1/C2 junction at (-1500,-500) ──
    /// <summary>BUG — nudging L1 vertically drops the C1/C2 junction off the wire.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Bug_03_NudgeInductorVertically_KeepsCapJunction(double dy)
    {
        var m = new SchematicEditModel();
        m.Components.Add(Comp(SymbolKind.Inductor,  -1900, -500, "L1", SymbolRotation.R270));
        m.Components.Add(Comp(SymbolKind.Capacitor, -1500, -700, "C1"));
        m.Components.Add(Comp(SymbolKind.Capacitor, -1500, -300, "C2"));
        m.Components.Add(Comp(SymbolKind.Inductor,  -1000, -500, "L2", SymbolRotation.R270));
        m.Wires.Add(Wire((-1700, -500), (-1200, -500)));

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "L1").Id);
        vm.SimulateDragCommit(0, dy);

        Assert.Equal(NetOf(m, "L1", 1), NetOf(m, "C1", 1));
        Assert.Equal(NetOf(m, "L1", 1), NetOf(m, "C2", 0));
        Assert.Equal(NetOf(m, "L1", 1), NetOf(m, "L2", 0));
    }

    // ── The exact shapes, so a future "tidy-up" cannot quietly go back to the bare L ──────────

    /// <summary>
    /// The wire the user is left with, spelled out. Every one of these was a bare
    /// <c>OrthogonalRoute</c> L before, and each L is a different wire from the one drawn.
    /// </summary>
    [Fact]
    public void TheFollowedGeometry_IsTheOriginalShapeWithOneLegMoved()
    {
        // 02 — L1 north: the horizontal stays on its row, a vertical jog appears under the pin.
        var (m2, w2) = Sheet02();
        var vm2 = new SchematicViewModel(m2);
        vm2.Selection.SelectOne(m2.Components.First(c => c.InstanceName == "L1").Id);
        vm2.SimulateDragCommit(0, -100);
        Assert.Equal("(600,-500) (600,-400) (100,-400)", Fmt(w2.Points));

        // 01 — Q1 left: the vertical leg stays vertical, the horizontal one lengthens.
        var mQ = Sheet01();
        var vmQ = new SchematicViewModel(mQ);
        vmQ.Selection.SelectOne(mQ.Components.First(c => c.InstanceName == "Q1").Id);
        vmQ.SimulateDragCommit(-100, 0);
        Assert.Equal("(-400,-300) (-400,-400) (100,-400)", Fmt(mQ.Wires[0].Points));

        // 01 — C2 left: the long vertical slides across; the y=1000 run does not move.
        var mC = Sheet01();
        var vmC = new SchematicViewModel(mC);
        vmC.Selection.SelectOne(mC.Components.First(c => c.InstanceName == "C2").Id);
        vmC.SimulateDragCommit(-100, 0);
        Assert.Equal("(300,0) (300,1000) (-300,1000)", Fmt(mC.Wires[3].Points));

        // 01 — L2 left: only the leg at the moved pin changes length.
        var mL = Sheet01();
        var vmL = new SchematicViewModel(mL);
        vmL.Selection.SelectOne(mL.Components.First(c => c.InstanceName == "L2").Id);
        vmL.SimulateDragCommit(-100, 0);
        Assert.Equal("(400,0) (400,1000) (-400,1000)", Fmt(mL.Wires[3].Points));
        // …and the two-point wire above L2 keeps its column, growing an elbow at the pin.
        Assert.Equal("(-300,100) (-300,600) (-400,600)", Fmt(mL.Wires[2].Points));
    }

    /// <summary>
    /// The tap that leaves its wire grows a stub instead of bending the wire, and the stub leaves
    /// the wire at a right angle rather than running along it.
    /// </summary>
    [Fact]
    public void Annoying_02_DragTapDown_GrowsAPerpendicularStub()
    {
        var (m, w) = Sheet02();
        int before = m.Wires.Count;
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(0, 200);

        Assert.Equal(before + 1, m.Wires.Count);
        Assert.Equal("(600,-400) (100,-400)", Fmt(w.Points));
        Assert.Equal("(400,-400) (400,-200)", Fmt(m.Wires[^1].Points));
    }

    /// <summary>Undo puts the stub away with the move — one keystroke, per the drag invariant.</summary>
    [Fact]
    public void Annoying_02_TapStub_UndoesWithTheMove()
    {
        var (m, _) = Sheet02();
        int before = m.Wires.Count;
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(0, 200);
        vm.UndoRedo.Undo();

        Assert.Equal(before, m.Wires.Count);
        Assert.Equal(-200, m.Components.First(c => c.InstanceName == "C2").Y);
    }

    /// <summary>
    /// A tap survives even when the wire it taps is ITSELF following a moved endpoint — dragging the
    /// inductor and one capacitor together used to lose the other capacitor twice over.
    /// </summary>
    [Fact]
    public void Bug_02_DragInductorAndTapTogether_KeepsBothConnected()
    {
        var (m, _) = Sheet02();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "L1").Id);
        vm.Selection.Add(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(0, -100);

        Assert.Equal(NetOf(m, "C1", 0), NetOf(m, "L1", 0));
        Assert.Equal(NetOf(m, "C1", 0), NetOf(m, "C2", 0));
    }

    /// <summary>
    /// No leg of a re-routed wire may come to lie ON TOP of another wire's leg running the same way.
    /// Nudging C2 used to drop wire 3's first leg exactly onto wire 2's column at x=-300, where the
    /// reader cannot tell one net from two.
    /// </summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(+100)]
    public void Annoying_01_NudgeC2_DoesNotLayALegOnTopOfAnotherWire(double dx)
    {
        var m  = Sheet01();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(m.Components.First(c => c.InstanceName == "C2").Id);
        vm.SimulateDragCommit(dx, 0);

        for (int a = 0; a < m.Wires.Count; a++)
            for (int b = a + 1; b < m.Wires.Count; b++)
                Assert.False(AnyLegsOverlap(m.Wires[a].Points, m.Wires[b].Points),
                    $"wire {a} {Fmt(m.Wires[a].Points)} lies on wire {b} {Fmt(m.Wires[b].Points)}");
    }

    /// <summary>True when the two polylines share a collinear run of non-zero length.</summary>
    private static bool AnyLegsOverlap(
        IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b)
    {
        for (int i = 0; i < a.Count - 1; i++)
            for (int j = 0; j < b.Count - 1; j++)
            {
                var (p, q) = (a[i], a[i + 1]);
                var (r, t) = (b[j], b[j + 1]);
                bool bothH = Near(p.Y, q.Y) && Near(r.Y, t.Y) && Near(p.Y, r.Y);
                bool bothV = Near(p.X, q.X) && Near(r.X, t.X) && Near(p.X, r.X);
                if (bothH && Overlap(p.X, q.X, r.X, t.X)) return true;
                if (bothV && Overlap(p.Y, q.Y, r.Y, t.Y)) return true;
            }
        return false;
    }

    private static bool Overlap(double a0, double a1, double b0, double b1)
        => System.Math.Min(System.Math.Max(a0, a1), System.Math.Max(b0, b1))
         - System.Math.Max(System.Math.Min(a0, a1), System.Math.Min(b0, b1)) > 1.0;
}
