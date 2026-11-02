// A PORT'S TYPE BELONGS TO THE PORT, NOT TO ITS PLACE IN A LIST.
//
// Owner question, 2026-09-14, in three scenarios: with geometry snapping on, can dragging P1 around
// a trace turn it into an internal port? And if P1 is typed internal and then DELETED, can P2 become
// internal — drawn as one, or actually be one?
//
// The drag half was already safe (the edge rule in LayoutEditorViewModel.RecomputeMoveDelta pulls an
// untyped port onto the conductor's boundary, so the "deep in the metal" inference never fires). The
// delete half was not, and not only in the drawing: `EmSetup.PortKinds` was read by the port's
// POSITION in the number-sorted list, so deleting P1 slid P2 into slot 0 and P2 was extracted,
// meshed and solved as an internal port — ground path grown underneath it, a complete and plausible
// s-matrix returned for a structure nobody drew. Typing P2 and deleting P1 walked the type onto P3
// the same way. `PortZ0s` had the identical defect beside it.
//
// The slot became `portNumber - 1`, which fixed it for both lists. On 2026-09-14 the TYPE went
// further and left the `.cem` altogether: it is `LabelShape.PortKind` now, on the port itself, so
// there is no slot for it to slide along and the whole class of defect is unrepresentable. These
// tests keep asserting the behaviour — deleting a port must not retype its neighbours — because that
// is the property, not the mechanism. `PortZ0s` still uses the slot rule and is still tested for it.
//
// A set whose numbers skip one is refused outright since DFR33-1 (a Touchstone file numbers its ports by
// position), so ports 2 and 3 left alone can no longer be extracted at all. Each scenario therefore draws a
// FRESH, untyped P1 where the deleted one stood — the numbers are 1…N again, and the deleted port's type and
// impedance must reach neither the newcomer nor its neighbours.

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class PortTypeIdentityTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);
    private const long Tol = 300 * Dbu;

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static RectShape Line() =>
        new() { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };

    private static LabelShape Port(string text, double xMm, double yMm, LayoutRotation? dir = null) =>
        new()
        {
            Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.5),
            IsPort = true, PortDirection = dir,
        };

    private static PlanarProblem Problem(params LayoutShape[] shapes)
    {
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    /// <summary>Extraction exactly as <c>EmRunService</c> and the panel both perform it.</summary>
    private static EmPortExtractionResult Extract(EmSetup setup, params LayoutShape[] shapes) =>
        EmPortExtraction.Extract(
            shapes, Problem(shapes), Dbu, setup.ResolvePortZ0, LayoutUnit.Um,
            EmPortExtraction.DefaultGroundPathWidthM(StarterTechnologies.Pcb2Layer()));

    private static EmSetup Setup() =>
        new() { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };

    /// <summary>Types the port NUMBERED <paramref name="portNumber"/> — on the label, which is where
    /// a port's type lives. The whole scenario this file is about is a port with that number being
    /// deleted, so the fixture states the type the way a user does and then simply leaves the label
    /// out of the shape list.</summary>
    private static LayoutShape[] Typed(int portNumber, PlanarPortKind kind, params LayoutShape[] shapes)
    {
        foreach (var l in shapes.OfType<LabelShape>())
            if (l.IsPort && l.Text == portNumber.ToString()) l.PortKind = kind;
        return shapes;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Deleting a port must not retype its neighbours
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fixture's own non-vacuity: while P1 is there, P1 really is the internal one.</summary>
    [Fact]
    public void BeforeAnythingIsDeleted_TheTypeIsOnThePortItWasAssignedTo()
    {
        var r = Extract(Setup(), Typed(1, PlanarPortKind.Internal,
                        Line(), Port("1", 0, 1.45), Port("2", 20, 1.45)));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(PlanarPortKind.Internal, r.Ports[0].Kind);
        Assert.Equal(PlanarPortKind.Edge,     r.Ports[1].Kind);
    }

    /// <summary>Scenario 1a: P1 typed internal, then P1 deleted. P2 stays an edge port.</summary>
    [Fact]
    public void DeletingTheTypedPort_LeavesTheSurvivorAnEdgePort()
    {
        // P1 was typed internal and has since been DELETED — so it is simply not here; a fresh, untyped P1 replaces it.
        var r = Extract(Setup(), Line(), Port("1", 0, 1.45), Port("2", 20, 1.45));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, r.Rows[1].Number);
        Assert.Equal(PlanarPortKind.Edge, r.Ports[0].Kind);   // the new P1 states nothing and stands on the end face
        Assert.Equal(PlanarPortKind.Edge, r.Ports[1].Kind);
    }

    /// <summary>Scenario 1b: P1 typed internal, P3 added, then P1 deleted. Neither survivor takes
    /// P1's type — each keeps its OWN, which for a port that states nothing is what the artwork under
    /// it says: P2 stands on the conductor's end face and is an edge port, P3 stands in the middle of
    /// the metal and is an internal port. Under the arrangement this replaces both read "edge",
    /// because an unstated slot in the `.cem` answered Edge whatever the drawing showed.</summary>
    [Fact]
    public void DeletingTheTypedPortWithAThirdPresent_LeavesEachSurvivorItsOwnType()
    {
        var r = Extract(Setup(), Line(), Port("1", 0, 1.45), Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal([1, 2, 3], r.Rows.Select(x => x.Number));
        Assert.Equal(PlanarPortKind.Edge,     r.Ports[0].Kind);   // the new P1, untyped, on the other end face
        Assert.Equal(PlanarPortKind.Edge,     r.Ports[1].Kind);   // P2, on the end face
        Assert.Equal(PlanarPortKind.Internal, r.Ports[2].Kind);   // P3, mid-metal
    }

    /// <summary>Scenario 1c, the mirror: the type must not WALK either. P2 is the typed one and
    /// stays the typed one when P1 goes. Before the fix this read "P2=Edge P3=Internal" — the type
    /// had slid one slot along. P3 states nothing and stands mid-metal, so it is an internal port on
    /// its own account, which is a different sentence with the same words.</summary>
    [Fact]
    public void DeletingAnUNtypedPort_DoesNotWalkTheTypeOntoItsNeighbour()
    {
        var r = Extract(Setup(), Typed(2, PlanarPortKind.Internal,
                        Line(), Port("1", 0, 1.45), Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0)));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal($"P1={PlanarPortKind.Edge} P2={PlanarPortKind.Internal} P3={PlanarPortKind.Internal}",
                     $"P1={r.Ports[0].Kind} P2={r.Ports[1].Kind} P3={r.Ports[2].Kind}");

        // …and P3's is its ARTWORK's answer, not a copy of P2's: it states nothing at all.
        Assert.Null(r.Rows[2].Label.PortKind);
    }

    /// <summary>The same rule for the reference impedance, which shared the defect: an override on
    /// port 3 stays on port 3 when port 1 is deleted.</summary>
    [Fact]
    public void AReferenceImpedanceOverrideStaysOnItsOwnPortToo()
    {
        var setup = new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        setup.PortZ0s.AddRange([new Complex(50, 0), new Complex(50, 0), new Complex(75, 0)]);

        var r = Extract(setup, Line(), Port("1", 0, 1.45), Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(new Complex(50, 0), r.Ports[1].Z0);   // port 2
        Assert.Equal(new Complex(75, 0), r.Ports[2].Z0);   // port 3 keeps its own
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel writes to the port's slot, not to the row's position
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static EmSetupEditorViewModel Editor(params LabelShape[] ports)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-portid-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        string path  = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup { Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Line());
        foreach (var p in ports) view.Shapes.Add(p);

        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
            // What WorkspaceViewModel supplies: the panel EDITS the drawing, because that is where a
            // port's type lives.
            SetPortKind = (label, kind) => { label.PortKind = kind; view.NotifyChanged(); },
        };
        vm.Refresh();
        return vm;
    }

    private static LabelShape PortNamed(EmSetupEditorViewModel vm, string text)
    {
        var source = vm.ResolveLayout!.Invoke(vm.Working.LayoutRef)!;
        return source.View.Shapes.OfType<LabelShape>().Single(l => l.IsPort && l.Text == text);
    }

    /// <summary>A layout whose ports are numbered 2 and 3 — which is what a layout LOOKS like after
    /// P1 is deleted. Retyping the first ROW must land on port 2's own LABEL; there is no slot for it
    /// to miss any more, and this is what says so.</summary>
    [Fact]
    public void ThePanelWritesTheTypeToThePortsOwnLabel()
    {
        var vm = Editor(Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0));
        Assert.Equal([2, 3], vm.PortRows.Select(r => r.PortNumber));

        vm.PortRows[0].Kind = PlanarPortKind.InternalDeltaGap;

        Assert.Equal(PlanarPortKind.InternalDeltaGap, PortNamed(vm, "2").PortKind);
        Assert.Null(PortNamed(vm, "3").PortKind);            // port 3 untouched, and still unstated
        Assert.Empty(vm.Working.PortKinds);                  // and the .cem keeps no second copy

        // …and it survives the round trip to the row it came from, so the panel reads back what it
        // wrote rather than the slot it happens to sit at.
        vm.Refresh();
        Assert.Equal(PlanarPortKind.InternalDeltaGap, vm.PortRows[0].Kind);
        // Port 3 stands in the middle of the metal and states nothing, so its own artwork answers.
        Assert.Equal(PlanarPortKind.Internal,         vm.PortRows[1].Kind);
    }

    /// <summary>The same for the impedance, and it is the same list mechanism.</summary>
    [Fact]
    public void ThePanelWritesTheImpedanceToThePortsOwnSlot()
    {
        var vm = Editor(Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0));

        vm.PortRows[1].Text = "75";
        vm.CommitPortRow(1);

        Assert.Equal(new Complex(75, 0), vm.Working.ResolvePortZ0(2));   // port 3
        vm.Refresh();
        Assert.Equal("75", vm.PortRows[1].Text);
    }

    /// <summary>The layout is told NOTHING — it reads the same field the run does, so the mark on
    /// screen and the port the run drives cannot disagree. This replaces a test that checked the
    /// anchor-keyed mark list was addressed by port number; that channel is gone.</summary>
    [Fact]
    public void TheDrawingAndTheRunReadTheSameField()
    {
        var vm = Editor(Port("2", 20, 1.45), Port("3", 10, 1.45, LayoutRotation.R0));
        vm.PortRows[0].Kind = PlanarPortKind.InternalDeltaGap;
        vm.Refresh();

        var source = vm.ResolveLayout!.Invoke(vm.Working.LayoutRef)!;
        var conductorAt = LayoutPortDirection.LookupFor(source.View.Shapes);

        Assert.Equal(PlanarPortKind.InternalDeltaGap,
                     LayoutPortDirection.KindOf(conductorAt, PortNamed(vm, "2")));
        Assert.Equal(PlanarPortKind.Internal,   // mid-metal, and states nothing
                     LayoutPortDirection.KindOf(conductorAt, PortNamed(vm, "3")));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Every existing .cem still means what it meant
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The compatibility guard, now for the MIGRATION: a `.cem` written before the type
    /// moved onto the drawing still carries `PortKinds`, and the one door
    /// (<c>EmPortKindMigration</c>) carries it onto the labels by port NUMBER — which for the
    /// contiguous 1..N numbering every layout this tool creates has is the position it always was,
    /// so nothing moves.</summary>
    [Fact]
    public void ALegacySetupsPortKindsStillMeanWhatTheyMeant()
    {
        var setup = new EmSetup
        {
            Name = "legacy", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PortKinds = [PlanarPortKind.Edge, PlanarPortKind.InternalDeltaGap, PlanarPortKind.Edge],
        };
        string json = EmSetupPersistence.Serialize(setup);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));

        LayoutShape[] shapes = [Line(),
            Port("1", 0, 1.45), Port("2", 10, 1.45, LayoutRotation.R0), Port("3", 20, 1.45)];
        Assert.Equal(1, EmPortKindMigration.ApplyInMemory(shapes, back.PortKinds));

        var r = Extract(back, shapes);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(PlanarPortKind.Edge,             r.Ports[0].Kind);
        Assert.Equal(PlanarPortKind.InternalDeltaGap, r.Ports[1].Kind);
        Assert.Equal(PlanarPortKind.Edge,             r.Ports[2].Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The drag half — already correct, and now held that way
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (LayoutEditorViewModel Vm, LabelShape P1) DragFixture(bool snap, bool cemClaims,
                                                                         params LayoutShape[] metal)
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1_000 * Dbu };
        foreach (var m in metal) view.Shapes.Add(m);

        var p1 = Port("1", 0, 1.45, LayoutRotation.R0);
        view.Shapes.Add(p1);

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Select,
            GeometrySnapEnabled = snap,
        };
        // "Claimed" now means the LABEL states its type, which is what the Port tool stamps at
        // placement. Unstated is the other half: a port from a .clay written before the field existed.
        if (cemClaims) p1.PortKind = PlanarPortKind.Edge;
        return (vm, p1);
    }

    /// <summary>What the renderer draws this port as — the label's own type where it states one, the
    /// drawing's inference where it does not. The ONE call everything makes.</summary>
    private static PlanarPortKind Drawn(LayoutEditorViewModel vm, LabelShape port) =>
        LayoutPortDirection.KindOf(
            LayoutConductorLookup.LookupFor(vm.Model, vm.Technology, vm.InstanceBaseDir), port);

    private static void Drag(LayoutEditorViewModel vm, long fx, long fy, long tx, long ty)
    {
        vm.OnPointerPressed(fx, fy, KeyModifiers.None, 1, 0, 0, Tol);
        vm.OnPointerMoved(tx, ty, leftDown: true, KeyModifiers.None, 0, 0, Tol);
        vm.OnPointerReleased(tx, ty, KeyModifiers.None);
    }

    /// <summary>
    /// <b>With geometry snapping on, there is nowhere to drop an edge port that makes it internal.</b>
    /// Swept over the trace and well past it, with and without a `.cem` claiming the layout — the
    /// second case is the one that relies on the edge rule alone, since there is no stored answer to
    /// fall back on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithSnappingOn_DraggingAnEdgePortAnywhereKeepsItAnEdgePort(bool cemClaims)
    {
        var bad = new List<string>();
        for (double x = -3; x <= 23; x += 0.37)
        for (double y = -2; y <=  5; y += 0.29)
        {
            var (vm, p1) = DragFixture(snap: true, cemClaims, Line());
            Drag(vm, p1.X, p1.Y, Mm(x), Mm(y));
            if (Drawn(vm, p1) is var k && k != PlanarPortKind.Edge)
                bad.Add($"drop ({x:F2}, {y:F2}) mm drew {k}");
        }
        Assert.True(bad.Count == 0, $"{bad.Count} drops drew a non-edge port:\n" +
                                    string.Join("\n", bad.Take(20)));
    }

    /// <summary>The same sweep over a conductor made of TWO overlapping rectangles — an L bend, where
    /// the boundary the drop snaps to belongs to one arm and can lie inside the other.</summary>
    [Fact]
    public void WithSnappingOn_AnLBendHasNoInteriorLandingSpotEither()
    {
        var arms = new LayoutShape[]
        {
            new RectShape { Layer = TopCopper, X1 = 0,        Y1 = 0, X2 = Mm(12), Y2 = Mm(2.9) },
            new RectShape { Layer = TopCopper, X1 = Mm(9.1),  Y1 = 0, X2 = Mm(12), Y2 = Mm(12)  },
        };

        var bad = new List<string>();
        for (double x = -2; x <= 14; x += 0.31)
        for (double y = -2; y <= 14; y += 0.31)
        {
            var (vm, p1) = DragFixture(snap: true, cemClaims: false, arms);
            Drag(vm, p1.X, p1.Y, Mm(x), Mm(y));
            if (Drawn(vm, p1) is var k && k != PlanarPortKind.Edge)
                bad.Add($"drop ({x:F2}, {y:F2}) mm drew {k}");
        }
        Assert.True(bad.Count == 0, $"{bad.Count} drops drew a non-edge port:\n" +
                                    string.Join("\n", bad.Take(20)));
    }

    /// <summary>The non-vacuity guard for both sweeps above, and the documented way to get an
    /// interior port by dragging: turn geometry snapping OFF and drop it mid-metal. Without this the
    /// sweeps would pass against a renderer that had simply stopped inferring anything.</summary>
    [Fact]
    public void WithSnappingOff_ADropMidMetalIsDrawnAsAnInteriorPort()
    {
        var (vm, p1) = DragFixture(snap: false, cemClaims: false, Line());
        vm.ToggleSnapDbuEnabled();

        Drag(vm, p1.X, p1.Y, Mm(10), Mm(1.45));

        Assert.Equal(Mm(10), p1.X);
        Assert.Equal(PlanarPortKind.Internal, Drawn(vm, p1));
    }
}
