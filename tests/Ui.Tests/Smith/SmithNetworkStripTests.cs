using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The network strip: the projection, the element operations, the sliders and the mirror
/// (<c>brief-smith-6-network-strip.md</c> §4; <c>docs/design/smith-chart.md</c> §5.5, §5.6).
/// <b>One test per claim.</b>
/// </summary>
/// <remarks>
/// <b>Every claim here is drivable with no display.</b> The projection is a
/// <see cref="SmithNetworkModel"/> call over the schematic editor's own read model; the operations,
/// the sliders and the mirror are the view model's; the only things a running Avalonia would add are
/// the pixels and the pointer. (<c>Ui.Tests</c> may call no Avalonia runtime API — see its
/// <c>.csproj</c>.)
/// </remarks>
public sealed class SmithNetworkStripTests
{
    private const double DesignHz = 2.0e9;

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = 50.0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    private static SmithElement L(double henry, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.L, Placement = placement, Name = name,
                   Values = { LHenry = henry } };

    private static SmithElement C(double farad, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.C, Placement = placement, Name = name,
                   Values = { CFarad = farad } };

    /// <summary>The four-element cascade §4's first claim is about: two series parts and two shunt
    /// arms, so every case in the projection is exercised by one drawing.</summary>
    private static SmithDesign FourElements() => Design(
        L(3.3e-9,  SmithPlacement.Series, "L1"),
        C(1.2e-12, SmithPlacement.Shunt,  "C1"),
        L(1.5e-9,  SmithPlacement.Series, "L2"),
        C(0.8e-12, SmithPlacement.Shunt,  "C2"));

    private static Complex[] Nodes(SmithDesign d)
        => [.. SmithCascade.Evaluate(d, d.Chart.DesignFrequencyHz).Select(n => n.Z)];

    /// <summary>Undoes until there is nothing left, reporting how many entries there were — the stack
    /// exposes <c>CanUndo</c> rather than a count, and counting by emptying it is exactly what "one
    /// gesture is one undo entry" is a claim about.</summary>
    private static int DrainUndo(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        return n;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The projection is a real schematic (R-smith6-1)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A four-element cascade produces the expected component kinds, one ground per shunt column,
    /// and spine wires in the GAPS — not across the series bodies.</b>
    /// </summary>
    /// <remarks>
    /// The two wire claims are the Designer's own findings restated as assertions, because both
    /// produce a drawing that looks almost right: a ground per ELEMENT puts two grounds on one lead
    /// and none on another, and a port-to-port spine lays a second wire across every series body,
    /// where it reads as a short until someone zooms in.
    /// </remarks>
    [Fact]
    public void TheProjectionIsARealSchematic()
    {
        // The empty cascade is a STATIC on the projection — the canvas reads it before a document is
        // bound — so a build that threw on one would be a type-initializer failure with no useful
        // stack. Cheapest place to find out is here.
        Assert.NotEmpty(SmithNetworkProjection.Empty.Model.Components);

        var p = SmithNetworkModel.Build(FourElements(), documentDirectory: null);

        // ── the kinds, named rather than counted ─────────────────────────────
        var byName = p.Model.Components.ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

        Assert.Equal(SymbolKind.Inductor,  byName["L1"].Symbol);
        Assert.Equal(SymbolKind.Capacitor, byName["C1"].Symbol);
        Assert.Equal(SymbolKind.Inductor,  byName["L2"].Symbol);
        Assert.Equal(SymbolKind.Capacitor, byName["C2"].Symbol);

        // The generator IS an impedance to ground, so it is a TermG; the load end is a Pin, because
        // this tool has no load element and nothing terminates the cascade.
        Assert.Equal(SymbolKind.TermG, byName[SmithNetworkModel.GeneratorName].Symbol);
        Assert.Equal(SymbolKind.Pin,   byName[SmithNetworkModel.LoadName].Symbol);

        // ── one ground per shunt COLUMN, on that column's lower pin ──────────
        var grounds = p.Model.Components.Where(c => c.Symbol == SymbolKind.Ground).ToList();
        Assert.Equal(["C1" + SmithNetworkModel.GroundNameSuffix,
                      "C2" + SmithNetworkModel.GroundNameSuffix],
                     grounds.Select(g => g.InstanceName).Order().ToArray());

        foreach (var shunt in new[] { "C1", "C2" })
        {
            var arm = p.Edit.Components.Single(c => c.InstanceName == shunt);
            var gnd = p.Edit.Components.Single(c => c.InstanceName == shunt + SmithNetworkModel.GroundNameSuffix);
            var (px, py) = arm.GetPortWorldCoord(1);

            Assert.Equal(px, gnd.X, 9);
            Assert.Equal(py, gnd.Y, 9);   // ON the pin — so there is no wire to draw
        }

        // ── the spine runs in the gaps and never through a series body ───────
        var series = new[] { "L1", "L2" }
            .Select(n => p.Edit.Components.Single(c => c.InstanceName == n).X)
            .ToArray();

        foreach (var wire in p.Edit.Wires)
        {
            var (x0, y0) = wire.Points[0];
            var (x1, y1) = wire.Points[^1];

            Assert.Equal(SmithNetworkModel.SpineY, y0, 9);
            Assert.Equal(SmithNetworkModel.SpineY, y1, 9);

            foreach (double bodyX in series)
                Assert.False(x0 < bodyX && bodyX < x1,
                    $"a spine wire runs from {x0} to {x1}, straight across the series body at {bodyX} — "
                  + "a built-in glyph carries its own leads, so a wire drawn over one is a second wire.");
        }

        // …and every gap IS bridged: the drawing is one connected run, not four floating parts. Every
        // pin of every element reports itself connected, which is the renderer's own test and not a
        // second one written here.
        foreach (var c in p.Model.Components.Where(c => c.Symbol != SymbolKind.Pin))
            Assert.All(c.Ports, port =>
                Assert.Equal(PortConnectionState.Connected, port.State));

        // ── a junction dot where a shunt arm taps the spine, and NOWHERE else ─
        // The editor's own auto-dot rule marks any pin that coincides with another endpoint, which on
        // a hand-wired page is right and on a projection puts eleven dots on a five-element strip: by
        // construction every series lead tip sits on a wire end and every ground sits on its own pin.
        // A dot means a BRANCH, and the only branch in a cascade is a shunt tap.
        Assert.Equal(
            [(1400.0, 0.0), (2800.0, 0.0)],
            p.Model.ConnectionDots.Select(dt => (dt.X, dt.Y)).OrderBy(t => t.X).ToArray());
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. Mirroring is view-only (R-smith6-6)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The mirror changes the DRAWING and nothing else</b> — no element value, no node impedance,
    /// no chart point — <b>and the symbols really turn round with their positions</b>.
    /// </summary>
    /// <remarks>
    /// The second half is the one with a plausible wrong version. <c>MirrorX</c> is applied in the
    /// symbol's LOCAL frame, before the rotation, so setting it on a part standing at <c>R270</c>
    /// flips the glyph across its own wire and leaves its two pins exactly where they were — a
    /// drawing whose bodies reflected while their ends did not. Asserting the pin COORDINATES, and as
    /// an exact negation rather than "somewhere on the other side", is what separates the two.
    /// </remarks>
    [Fact]
    public void MirroringIsViewOnly()
    {
        var vm = new SmithChartViewModel(FourElements());

        string valuesBefore = SmithDesignIo.SerializeUnvalidated(vm.Design);
        var    nodesBefore  = Nodes(vm.Design);
        var    chartBefore  = ChartPoints(vm);
        var    before       = vm.Network;

        vm.ToggleMirrorCommand.Execute(null);
        var after = vm.Network;

        Assert.True(vm.MirrorNetwork);

        // ── nothing electrical moved ─────────────────────────────────────────
        var nodesAfter = Nodes(vm.Design);
        Assert.Equal(nodesBefore.Length, nodesAfter.Length);
        for (int k = 0; k < nodesBefore.Length; k++)
            Assert.Equal(nodesBefore[k], nodesAfter[k]);

        Assert.Equal(chartBefore, ChartPoints(vm));

        // The ONE document field that may differ is the mirror flag itself.
        vm.Design.View.MirrorNetwork = false;
        Assert.Equal(valuesBefore, SmithDesignIo.SerializeUnvalidated(vm.Design));
        vm.Design.View.MirrorNetwork = true;

        // ── every symbol turned round, and every pin reflected about x = 0 ───
        foreach (var a in before.Edit.Components)
        {
            var b = after.Edit.Components.Single(c => c.InstanceName == a.InstanceName);

            Assert.False(a.MirrorX);
            Assert.True(b.MirrorX, $"{a.InstanceName} kept its handedness while its position reflected.");

            Assert.Equal(-a.X, b.X, 9);
            Assert.Equal(a.Y,  b.Y, 9);

            // How many pins the part has is the RENDER model's answer, which is the same
            // SymbolPortDefs call the drawing made — not a count written out again here.
            int pins = before.Model.Components.Single(c => c.InstanceName == a.InstanceName).Ports.Count;
            for (int i = 0; i < pins; i++)
            {
                var (ax, ay) = a.GetPortWorldCoord(i);
                var (bx, by) = b.GetPortWorldCoord(i);

                Assert.Equal(-ax, bx, 9);
                Assert.Equal(ay,  by, 9);
            }
        }
    }

    /// <summary>Every plotted point on the chart, in order — what "the chart did not move" is a claim
    /// about.</summary>
    private static List<System.Numerics.Vector2> ChartPoints(SmithChartViewModel vm)
        => [.. vm.ChartPlot.Traces.OrderBy(t => t.CubeName, StringComparer.Ordinal)
                                  .SelectMany(t => t.Points)];

    // ═════════════════════════════════════════════════════════════════════════
    //  3. Mirror is one undo entry (R-smith6-6)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A flip is one undo entry and one Undo takes it back</b> — the Data Display's own precedent
    /// that a persisted view change is undoable.
    /// </summary>
    [Fact]
    public void MirrorIsOneUndoEntry()
    {
        var vm = new SmithChartViewModel(FourElements());
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.False(vm.MirrorNetwork);

        vm.ToggleMirrorCommand.Execute(null);

        Assert.True(vm.MirrorNetwork);
        Assert.True(vm.IsDirty);           // a view setting that IS an edit

        vm.UndoRedo.Undo();

        Assert.False(vm.MirrorNetwork);
        Assert.False(vm.UndoRedo.CanUndo);  // …so there was exactly one

        // And the projection followed the undo rather than staying flipped.
        Assert.All(vm.Network.Edit.Components, c => Assert.False(c.MirrorX));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. Insert, Add, Delete and reorder (R-smith6-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Insert places BEFORE the selection, Add appends, Delete closes the chain, and a reorder
    /// moves the element with its values.</b>
    /// </summary>
    /// <remarks>
    /// The reorder half is the one worth asserting by VALUE. An implementation that re-created the
    /// element from its kind at the new index would put the right part in the right place with the
    /// registry's default in it, which reads as a value that changed when nothing about the part did.
    /// </remarks>
    [Fact]
    public async System.Threading.Tasks.Task InsertPlacesBeforeTheSelectionAndAddAppends()
    {
        var vm = new SmithChartViewModel(FourElements());

        // ── Add appends, nearest the load ────────────────────────────────────
        await vm.AddElementCommand.ExecuteAsync(Entry(SmithElementKind.R, SmithPlacement.Series));

        Assert.Equal(["L1", "C1", "L2", "C2", "R1"], Names(vm));
        Assert.Equal(4, vm.SelectedElementIndex);

        // ── Insert places before the selected element ────────────────────────
        vm.SelectElement(2);                                     // L2
        await vm.InsertElementCommand.ExecuteAsync(Entry(SmithElementKind.C, SmithPlacement.Shunt));

        Assert.Equal(["L1", "C1", "C3", "L2", "C2", "R1"], Names(vm));
        Assert.Equal(2, vm.SelectedElementIndex);                // the new one is selected

        // ── Delete closes the chain ──────────────────────────────────────────
        vm.SelectElement(2);
        vm.DeleteElementCommand.Execute(null);
        Assert.Equal(["L1", "C1", "L2", "C2", "R1"], Names(vm));

        // ── Reorder carries the element, not just its slot ───────────────────
        vm.SelectElement(0);                                     // L1, 3.3 nH
        double value = vm.Design.Elements[0].Values.LHenry;

        vm.MoveElement(0, 3);

        Assert.Equal(["C1", "L2", "C2", "L1", "R1"], Names(vm));
        Assert.Equal(value, vm.Design.Elements[3].Values.LHenry);
        Assert.Equal(3, vm.SelectedElementIndex);                // the selection followed the part

        // The drawing followed too: L1 now occupies the fourth column.
        Assert.Equal(vm.Network.ColumnX[3],
                     vm.Network.Edit.Components.Single(c => c.InstanceName == "L1").X, 9);
    }

    private static SmithElementMenuEntry Entry(SmithElementKind kind, SmithPlacement placement)
        => SmithChartViewModel.ElementMenu.Single(r => r.Kind == kind && r.Placement == placement);

    private static string[] Names(SmithChartViewModel vm)
        => [.. vm.Design.Elements.Select(e => e.Name)];

    // ═════════════════════════════════════════════════════════════════════════
    //  5. A disabled element keeps its values and its place (R-smith6-2)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Disabling an element contributes nothing to the walk and loses nothing about the part.</b>
    /// </summary>
    /// <remarks>
    /// Asserted from BOTH sides, because either alone would pass a wrong build: the evaluator's node
    /// array is one shorter and skips it (brief 2's <c>R-smith2-8</c>, seen from here), and the
    /// element is still in the list, still at index 1, still 1.2 pF. It costs one boolean and it is
    /// the difference between trying something and losing it.
    /// </remarks>
    [Fact]
    public void ADisabledElementKeepsItsValuesAndItsPlace()
    {
        var vm = new SmithChartViewModel(FourElements());
        vm.SelectElement(1);                                     // C1, shunt, 1.2 pF

        Assert.Equal(5, Nodes(vm.Design).Length);                // generator + four elements

        vm.SelectedElementEnabled = false;

        // ── absent from the walk ─────────────────────────────────────────────
        var nodes = SmithCascade.Evaluate(vm.Design, DesignHz);
        Assert.Equal(4, nodes.Length);
        Assert.DoesNotContain(1, nodes.Select(n => n.ElementIndex));

        // ── and nothing about it was lost ────────────────────────────────────
        Assert.Equal(["L1", "C1", "L2", "C2"], Names(vm));
        Assert.Equal(1.2e-12, vm.Design.Elements[1].Values.CFarad);
        Assert.False(vm.Design.Elements[1].Enabled);

        // It still draws, on the schematic's OWN convention — and as a SHUNT element, an open rather
        // than a short: skipping a shunt element is an open circuit, and skipping a series one is a
        // short through it. Drawing both the same would be a true picture of a different circuit.
        var drawn = vm.Network.Edit.Components.Single(c => c.InstanceName == "C1");
        Assert.Equal(DisableState.Open, drawn.Disable);

        vm.SelectElement(0);                                     // L1, series
        vm.SelectedElementEnabled = false;
        Assert.Equal(DisableState.Short,
                     vm.Network.Edit.Components.Single(c => c.InstanceName == "L1").Disable);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. The active parameter follows the last-touched slider (R-smith6-5)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The last slider touched becomes the element's active parameter, and the chart's gripper then
    /// drags THAT parameter and no other.</b>
    /// </summary>
    /// <remarks>
    /// The second half is the whole point of the first. A marked row that the gripper ignored would be
    /// a label saying something untrue about a handle, which is worse than no label — so the test
    /// drags the gripper and asserts which of the SRLC's three numbers moved.
    /// </remarks>
    [Fact]
    public void TheActiveParameterFollowsTheLastTouchedSlider()
    {
        var srlc = new SmithElement
        {
            Kind = SmithElementKind.Srlc, Placement = SmithPlacement.Series, Name = "SRLC1",
            Values = { ROhm = 0.4, LHenry = 1.0e-9, CFarad = 2.0e-12 },
        };
        var vm = new SmithChartViewModel(Design(srlc));
        vm.SelectElement(0);

        // §3.3's default for an SRLC is L — the reactive part, because dragging the loss of a lossy
        // part is a move along the trajectory nobody reaches for first.
        Assert.Equal(SmithParameter.L, SmithComponentMap.ActiveParameterOf(vm.Design.Elements[0]));
        Assert.Equal([SmithParameter.R, SmithParameter.L, SmithParameter.C],
                     vm.SliderRows.Select(r => r.Parameter).ToArray());

        // Touch the C row — a drag, which is what a user does.
        var cRow = vm.SliderRows.Single(r => r.Parameter == SmithParameter.C);
        vm.BeginSliderDrag();
        cRow.Position = cRow.Position + 0.05;
        vm.EndSliderDrag();

        Assert.Equal(SmithParameter.C, SmithComponentMap.ActiveParameterOf(vm.Design.Elements[0]));
        Assert.True(cRow.IsActive);
        Assert.False(vm.SliderRows.Single(r => r.Parameter == SmithParameter.L).IsActive);

        // ── and the gripper now drags C, and only C ──────────────────────────
        var v = vm.Design.Elements[0].Values;
        (double r0, double l0, double c0) = (v.ROhm, v.LHenry, v.CFarad);

        var tf    = PlotRenderer.BuildTransforms(vm.ChartPlot, (420.0, 420.0));
        var nodes = SmithCascade.Evaluate(vm.Design, DesignHz);
        var g1    = SmithCascade.Gamma(nodes[1].Z, vm.Design.Chart.Z0Ohm);
        var start = tf.PrimaryToCanvas(g1.Real, g1.Imaginary);

        Assert.True(vm.BeginGripperDrag(1));
        var (wx, wy) = tf.PrimaryFromCanvas(start.X + 14, start.Y - 14);
        vm.DragGripperTo(new Complex(wx, wy));
        vm.EndGripperDrag(cancelled: false);

        var after = vm.Design.Elements[0].Values;
        Assert.Equal(r0, after.ROhm);
        Assert.Equal(l0, after.LHenry);
        Assert.NotEqual(c0, after.CFarad);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. A slider drag is one undo entry (R-smith6-4)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>One drag is one undo entry, and <i>n</i> drags are <i>n</i> undos.</b>
    /// </summary>
    /// <remarks>
    /// This is the regression the Match Designer paid for: a two-way-bound slider's coercing
    /// write-back reached an unguarded setter DURING <c>Undo</c>, so every undo ADDED an entry, redo
    /// was wiped, and eight edits took fourteen undos to unwind (<c>src/Ui/Match/RESOLVED.md</c>).
    /// Counting is therefore the test — not "an entry exists".
    /// </remarks>
    [Fact]
    public void ASliderDragIsOneUndoEntry()
    {
        var vm = new SmithChartViewModel(FourElements());
        vm.SelectElement(0);
        Assert.False(vm.UndoRedo.CanUndo);

        string before = SmithDesignIo.SerializeUnvalidated(vm.Design);
        var    row    = vm.SliderRows.Single();

        vm.BeginSliderDrag();
        for (int i = 1; i <= 20; i++) row.Position = row.Position + 0.01;
        vm.EndSliderDrag();

        Assert.NotEqual(before, SmithDesignIo.SerializeUnvalidated(vm.Design));
        Assert.Equal(1, DrainUndo(vm));
        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));

        // n drags, n undos.
        const int N = 5;
        for (int i = 0; i < N; i++)
        {
            var r = vm.SliderRows.Single();
            vm.BeginSliderDrag();
            r.Position = r.Position + 0.02;
            vm.EndSliderDrag();
        }

        Assert.Equal(N, DrainUndo(vm));
        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));

        // An abandoned press-and-release that moved nothing leaves no trace at all — not an entry
        // that undoes to the same place, none.
        vm.BeginSliderDrag();
        vm.EndSliderDrag();
        Assert.False(vm.UndoRedo.CanUndo);

        // ── and a stored range can never COERCE the value ────────────────────
        // A RangeBase clamps its Value into [Minimum, Maximum] the instant either is published, and
        // the clamped number writes back through the two-way binding — so a stored range that
        // excluded the value would change the design from a notification, with no gesture behind it.
        // That is the same defect by a route nobody looks at, so the range stands down instead.
        vm.SelectElement(0);
        var stored = vm.SliderRows.Single();
        stored.SetRange(1.0e-12, 2.0e-12);                    // the element is 3.3 nH — far outside
        string clean = SmithDesignIo.SerializeUnvalidated(vm.Design);

        stored.NotifyAll();
        _ = (stored.Minimum, stored.Maximum, stored.Position);

        Assert.InRange(stored.Position, stored.Minimum, stored.Maximum);
        Assert.Equal(3.3e-9, vm.Design.Elements[0].Values.LHenry);
        Assert.Equal(clean, SmithDesignIo.SerializeUnvalidated(vm.Design));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8. A file element has nothing to drag (R-smith6-4)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Selecting an <c>S1P</c> or an <c>S2P</c> shows NO slider rows</b> — its value is a file.
    /// </summary>
    /// <remarks>
    /// What it shows instead is its file reference, its port count and its frequency span, read
    /// through <c>SmithCascade.FileSummary</c> so the path is resolved by the rule the evaluator
    /// resolves it by. A reference that does not resolve reports the evaluator's own refusal here
    /// rather than an empty panel — a file that has moved is exactly what this line exists to make
    /// visible.
    /// </remarks>
    [Fact]
    public void AFileElementShowsNoSliderRows()
    {
        var snp = new SmithElement
        {
            Kind = SmithElementKind.S2P, Placement = SmithPlacement.Series, Name = "S1",
            FileRef = "nowhere/thru.s2p",
        };
        var vm = new SmithChartViewModel(Design(L(3.3e-9, SmithPlacement.Series, "L1"), snp));

        vm.SelectElement(0);
        Assert.Single(vm.SliderRows);            // the inductor has one
        Assert.Null(vm.SelectedFileSummary);

        vm.SelectElement(1);
        Assert.Empty(vm.SliderRows);             // the file element has none
        Assert.True(vm.HasFileSummary);
        Assert.Contains("thru.s2p", vm.SelectedFileSummary);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  9. A line's F_ref is where it was placed (R-smith6-3)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A TLIN placed at 2 GHz carries <c>F_ref</c> = 2 GHz, a later design-frequency change leaves
    /// it alone, and the strip says so ONCE.</b>
    /// </summary>
    /// <remarks>
    /// The default is the registry's 1 GHz and this is the one place the tool overrides it, for
    /// <c>R-smith2-3</c>'s reason: a line whose reference frequency followed the chart would be a
    /// different physical line every time the chart was retuned, and the load points would stop
    /// meaning anything. Once, and not once per edit, because the behaviour is permanent — a note
    /// repeated on every frequency edit would make the strip's one line useless for everything else.
    /// </remarks>
    [Fact]
    public async System.Threading.Tasks.Task ATlinKeepsTheReferenceFrequencyItWasPlacedAt()
    {
        var vm = new SmithChartViewModel(Design());
        Assert.Equal(2e9, vm.Design.Chart.DesignFrequencyHz);

        await vm.AddElementCommand.ExecuteAsync(Entry(SmithElementKind.Tline, SmithPlacement.Series));

        // Re-read through the DOCUMENT at every step, never through a captured element. Every
        // committed edit replaces the whole design (SmithChartViewModel.ApplySnapshot), so an element
        // captured before one is a detached copy that would pass "it did not change" for free —
        // which is the one thing this test must not do.
        Assert.Equal(2e9,   Line(vm).Values.ReferenceFrequencyHz);

        // The rest of it IS the registry's, with no second set of defaults here.
        Assert.Equal(50.0,  Line(vm).Values.Z0Ohm);
        Assert.Equal(90.0,  Line(vm).Values.ElectricalLengthDeg);
        Assert.Equal("TL1", Line(vm).Name);

        // ── retuning leaves it behind, and says so once ──────────────────────
        Assert.Null(vm.StripNotice);

        vm.DesignFrequencyEntry = "2.1 GHz";

        Assert.Equal(2.1e9, vm.Design.Chart.DesignFrequencyHz, 3);
        Assert.Equal(2e9,   Line(vm).Values.ReferenceFrequencyHz);
        Assert.NotNull(vm.StripNotice);
        Assert.Contains("TL1", vm.StripNotice);
        Assert.False(vm.ShowStatusLine);          // the note replaces the reading, never joins it

        // …and only once. The next committed edit clears it — a note that outlived its occasion
        // would hide the strip's reading indefinitely — and a second retune says nothing new.
        vm.DesignFrequencyEntry = "2.2 GHz";

        Assert.Null(vm.StripNotice);
        Assert.True(vm.ShowStatusLine);
        Assert.Equal(2e9, Line(vm).Values.ReferenceFrequencyHz);
    }

    private static SmithElement Line(SmithChartViewModel vm) => vm.Design.Elements.Single();
}
