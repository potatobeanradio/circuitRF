using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The chart pane, its grippers and the drag's undo contract (brief-smith-5-chart.md §5;
/// <c>docs/design/smith-chart.md</c> §5.4, §4.3). <b>One test per claim.</b>
///
/// <para><b>Everything about the chart that matters is drivable with no display, and that is not an
/// accident.</b> The <c>Plot</c> and its traces are built by <see cref="SmithPlotBuilder"/>, the
/// gesture is <c>SmithGripperOverlay</c>'s over a <c>TransformSet</c> the renderer hands out, and the
/// undo entry is the view model's — so the only things a running Avalonia would add are the pixels.
/// The two claims that are genuinely about the CONTROL — the hit-test ORDER and the four providers —
/// are source scans, which is this project's own standing fallback (<c>Ui.Tests</c> may call no
/// Avalonia runtime API; see its <c>.csproj</c>, and <c>SmithWindowTests</c>' header).</para>
/// </summary>
public sealed class SmithChartTests
{
    private const double DesignHz = 2.0e9;
    private const double ChartZ0  = 50.0;

    /// <summary>The canvas every hit test in this file is measured in — the Data Display's own
    /// square-plot default box, which is also what the view model opens on.</summary>
    private static readonly (double W, double H) Canvas = (420.0, 420.0);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    /// <summary>The <c>AuthoringCliVerbTests</c> stripper, verbatim — a rule stated in a comment is
    /// not a rule the code follows.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static SmithElement L(double henry, SmithPlacement placement = SmithPlacement.Series,
                                  string name = "L1")
        => new()
        {
            Kind      = SmithElementKind.L,
            Placement = placement,
            Name      = name,
            Values    = { LHenry = henry },
        };

    private static SmithElement C(double farad, SmithPlacement placement = SmithPlacement.Shunt,
                                  string name = "C1")
        => new()
        {
            Kind      = SmithElementKind.C,
            Placement = placement,
            Name      = name,
            Values    = { CFarad = farad },
        };

    /// <summary>A generator table with three rows around the design frequency, so the load points
    /// and the conjugate targets both have something to be.</summary>
    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    private static Complex LoadGamma(SmithDesign d)
        => SmithCascade.Gamma(SmithCascade.Evaluate(d, d.DesignFrequencyHz)[^1].Z, d.Chart.Z0Ohm);

    /// <summary>The transform the chart's own frame is drawn with — what a hit test has to be
    /// measured in, because a gripper is found in CANVAS pixels.</summary>
    private static TransformSet Tf(SmithChartViewModel vm)
        => PlotRenderer.BuildTransforms(vm.ChartPlot, Canvas);

    /// <summary>Where node <paramref name="k"/> of the walk currently sits, in canvas pixels.</summary>
    private static (double X, double Y) NodeAt(SmithChartViewModel vm, int k)
    {
        var nodes = SmithCascade.Evaluate(vm.Design, vm.Design.DesignFrequencyHz);
        var g     = SmithCascade.Gamma(nodes[k].Z, vm.Design.Chart.Z0Ohm);
        var p     = Tf(vm).PrimaryToCanvas(g.Real, g.Imaginary);
        return (p.X, p.Y);
    }

    /// <summary>The Γ a canvas point stands for — how a pointer position reaches
    /// <c>IPlotOverlay.DragTo</c>.</summary>
    private static Complex GammaAt(SmithChartViewModel vm, double x, double y)
    {
        var (wx, wy) = Tf(vm).PrimaryFromCanvas((float)x, (float)y);
        return new Complex(wx, wy);
    }

    /// <summary>
    /// A plotted point IS the Γ the evaluator produced.
    /// </summary>
    /// <remarks>
    /// <b>Compared in SINGLE precision, because that is what a point is.</b> <c>Trace.Points</c> is a
    /// list of <c>Vector2</c> — the geometry the renderer strokes — so the honest claim is that the
    /// double the evaluator returned reached it with nothing but the narrowing applied. A tolerance
    /// in decimal places would state the same thing less exactly and would go quietly slack the
    /// moment the numbers got larger.
    /// </remarks>
    private static void AssertGamma(Complex expected, System.Numerics.Vector2 point)
    {
        Assert.Equal((float)expected.Real,      point.X);
        Assert.Equal((float)expected.Imaginary, point.Y);
    }

    /// <summary>Undoes until there is nothing left, and reports how many entries there were — the
    /// stack exposes <c>CanUndo</c> rather than a count, and counting by emptying it is exactly what
    /// "one gesture is one undo entry" is a claim about.</summary>
    private static int DrainUndo(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        return n;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The plot is what the evaluator said
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every curve on the chart came out of the evaluator, and the last one ENDS at the load.</b>
    /// </summary>
    /// <remarks>
    /// The trace count is stated term by term rather than as a number, because the interesting
    /// failure is a missing KIND of trace — no load points, no targets — and a bare "6" would pass a
    /// build that drew two trajectories and two target traces.
    /// </remarks>
    [Fact]
    public void ThePlotIsWhatTheEvaluatorSaid()
    {
        var design = Design(L(3.3e-9), C(1.2e-12), L(1.5e-9, name: "L2"));
        var vm     = new SmithChartViewModel(design);

        var byName = vm.ChartPlot.Traces.ToDictionary(t => t.CubeName!, StringComparer.Ordinal);

        // One per ENABLED element, named after it.
        Assert.Contains("L1", byName.Keys);
        Assert.Contains("C1", byName.Keys);
        Assert.Contains("L2", byName.Keys);

        // The load points: one trace for the design frequency and one for the other rows. The table
        // has three rows and the design frequency is one of them, so the secondary trace holds two.
        Assert.Equal(1, byName["load (design f)"].Points.Count);
        Assert.Equal(2, byName["load"].Points.Count);

        // One generator glyph per generator-table row.
        Assert.Equal(3, byName["Zgen"].Points.Count);

        // …and the swept band, which is always drawn across the table's own span (owner
        // instruction, 2026-09-19) rather than being a setting that could be off.
        Assert.Equal(SmithBand.Points, byName["band"].Points.Count);

        Assert.Equal(7, vm.ChartPlot.Traces.Count);

        // …and the last trajectory ENDS where the walk does. This is the join between the two
        // halves: the traces and the status strip are one evaluation or they are two answers.
        var last     = byName["L2"];
        var expected = LoadGamma(design);
        AssertGamma(expected, last.Points[^1]);

        // The design-frequency load point is that same Γ, which is what the strip reports.
        AssertGamma(expected, byName["load (design f)"].Points[0]);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. A drag moves everything downstream
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The specification's headline behaviour:</b> dragging a mid-cascade element moves it and
    /// every node after it, and changes no other element's value.
    /// </summary>
    [Fact]
    public void ADragMovesEverythingDownstream()
    {
        var design = Design(L(3.3e-9), C(1.2e-12), L(1.5e-9, name: "L2"), C(0.8e-12, name: "C2"));
        var vm     = new SmithChartViewModel(design);

        var before = SmithCascade.Evaluate(design, DesignHz)
                                 .Select(n => n.Z).ToArray();
        double[] values = [.. design.Elements.Select(e => SmithInverse.Current(e, SmithComponentMap.DefaultParameter(e.Kind)))];

        // Node 1 is the output of element 0 — the first L. Drag it somewhere else on its own arc.
        var start = NodeAt(vm, 1);
        Assert.True(vm.BeginGripperDrag(1));
        vm.DragGripperTo(GammaAt(vm, start.X + 25, start.Y - 25));
        vm.EndGripperDrag(cancelled: false);

        var after = SmithCascade.Evaluate(vm.Design, DesignHz).Select(n => n.Z).ToArray();

        // Node 0 is the generator and does not move.
        Assert.Equal(before[0], after[0]);

        // Nodes 1 … 4 all moved.
        for (int k = 1; k < before.Length; k++)
            Assert.True((after[k] - before[k]).Magnitude > 1e-9,
                $"node {k} did not move: {before[k]} → {after[k]}");

        // Elements 1, 2 and 3 kept their values — exactly ONE parameter of ONE element changed.
        for (int i = 1; i < vm.Design.Elements.Count; i++)
            Assert.Equal(values[i],
                SmithInverse.Current(vm.Design.Elements[i],
                                     SmithComponentMap.DefaultParameter(vm.Design.Elements[i].Kind)), 15);

        Assert.NotEqual(values[0], SmithInverse.Current(vm.Design.Elements[0], SmithParameter.L));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. One drag is ONE undo entry (R-smith5-8)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Press, twenty moves, release — one entry; one Undo lands exactly on the before-state; and
    /// <i>n</i> drags take <i>n</i> undos.</b>
    /// </summary>
    /// <remarks>
    /// This is the regression the Match Designer paid for: a two-way-bound slider's coercing
    /// write-back reached an unguarded setter DURING <c>Undo</c>, so every undo ADDED an entry, redo
    /// was wiped, and eight edits took fourteen undos to unwind
    /// (<c>src/Ui/Match/RESOLVED.md</c>). Counting is therefore the test — not "an entry exists".
    /// </remarks>
    [Fact]
    public void OneDragIsOneUndoEntry()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9), C(1.2e-12)));
        Assert.False(vm.UndoRedo.CanUndo);

        string before = SmithDesignIo.SerializeUnvalidated(vm.Design);

        var start = NodeAt(vm, 1);
        Assert.True(vm.BeginGripperDrag(1));
        for (int i = 1; i <= 20; i++)
            vm.DragGripperTo(GammaAt(vm, start.X + i, start.Y - i));
        vm.EndGripperDrag(cancelled: false);

        Assert.True(vm.UndoRedo.CanUndo);
        Assert.NotEqual(before, SmithDesignIo.SerializeUnvalidated(vm.Design));

        vm.UndoRedo.Undo();
        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));
        Assert.False(vm.UndoRedo.CanUndo);      // …so there was exactly one

        // And n drags take n undos.
        vm.UndoRedo.Redo();
        const int N = 5;
        for (int i = 0; i < N; i++)
        {
            var from = NodeAt(vm, 1);
            Assert.True(vm.BeginGripperDrag(1));
            vm.DragGripperTo(GammaAt(vm, from.X + 6, from.Y + 4));
            vm.EndGripperDrag(cancelled: false);
        }

        // N drags plus the one redone above.
        Assert.Equal(N + 1, DrainUndo(vm));
        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. Escape mid-drag restores and pushes nothing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An abandoned gesture leaves no trace on the undo stack at all</b> — not an entry that
    /// undoes to the same place, none.
    /// </summary>
    [Fact]
    public void EscapeMidDragRestoresAndPushesNothing()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9), C(1.2e-12)));
        string before = SmithDesignIo.SerializeUnvalidated(vm.Design);

        var start = NodeAt(vm, 1);
        Assert.True(vm.BeginGripperDrag(1));
        for (int i = 1; i <= 8; i++)
            vm.DragGripperTo(GammaAt(vm, start.X + i * 3, start.Y - i * 3));

        Assert.NotEqual(before, SmithDesignIo.SerializeUnvalidated(vm.Design));   // it WAS live

        vm.EndGripperDrag(cancelled: true);

        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.False(vm.UndoRedo.CanRedo);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. Node 0 does not drag (R-smith5-7)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The generator is an ANCHOR</b>: hit-testing it returns no handle, so the press falls
    /// through to the control and pans like any other empty spot — and asking the view model to drag
    /// it is refused.
    /// </summary>
    /// <remarks>
    /// A drag there would have to guess which generator-table row it meant (§4.3). The anchor is
    /// still DRAWN, so the walk has a visible start.
    /// </remarks>
    [Fact]
    public void NodeZeroDoesNotDrag()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9), C(1.2e-12)));

        var (x, y) = NodeAt(vm, 0);
        Assert.Null(vm.ChartOverlay.HitTest(x, y, Tf(vm)));

        Assert.False(vm.BeginGripperDrag(0));
        Assert.False(vm.IsDraggingGripper);

        // …and the two real nodes DO answer, so the null above is about node 0 rather than about the
        // hit test being inert.
        var (x1, y1) = NodeAt(vm, 1);
        Assert.Equal(new SmithGripperHandle(1), vm.ChartOverlay.HitTest(x1, y1, Tf(vm)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. Hit-test order, and the fall-through that leaves panning alone
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The overlay is asked FIRST, and it answers null wherever it has nothing</b> — the two
    /// halves of <c>R-smith5-6</c>'s seam rule.
    /// </summary>
    /// <remarks>
    /// A gripper coincident with a marker is unreachable if the marker is asked first, and the marker
    /// is the thing a user can move out of the way. The ORDER is a property of
    /// <c>PlotControl.OnPointerPressed</c> and is asserted by reading it: <c>Ui.Tests</c> may call no
    /// Avalonia runtime API, so a real press cannot be synthesised here. What CAN be driven directly
    /// is driven directly — the overlay's own answer off a gripper, which is what makes the press
    /// reach the pan branch at all.
    /// </remarks>
    [Fact]
    public void TheOverlayIsAskedFirst_AndAnswersNullOffAGripper()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9), C(1.2e-12)));

        // Empty chart: no handle, so the press falls through to the control's marker test and then to
        // the pan.
        var (x, y) = NodeAt(vm, 1);
        Assert.Null(vm.ChartOverlay.HitTest(x + 40, y + 40, Tf(vm)));

        string pressed = StripComments(ReadRepoFile("src/Ui/DataDisplay/Controls/PlotControl.cs"));
        int overlay = pressed.IndexOf("OverlayHitTest(_dragStartScreen,", StringComparison.Ordinal);
        int vswr    = pressed.IndexOf("HitTestVswrLocus(e.GetPosition(this))", StringComparison.Ordinal);
        int marker  = pressed.IndexOf("HitTestMarker(e.GetPosition(this))", StringComparison.Ordinal);

        Assert.True(overlay > 0, "PlotControl no longer asks the overlay on press.");
        Assert.True(overlay < vswr,   "The overlay must be asked BEFORE the VSWR locus.");
        Assert.True(overlay < marker, "The overlay must be asked BEFORE the control's own markers.");

        // And the branch is guarded on an overlay being present AND answering, which is what makes
        // Overlay = null the whole of the old behaviour.
        // THE SHIFT MODIFIER IS PASSED THROUGH (owner instruction, 2026-09-19): the generator
        // glyphs are grabbable only while it is held, and a hit test that could not see it would
        // make them grabbable always or never.
        Assert.Contains("if (Overlay is { } overlay", pressed);
        Assert.Contains("OverlayHitTest(_dragStartScreen,", pressed);
        Assert.Contains("e.KeyModifiers.HasFlag(KeyModifiers.Shift)) is { } handle)", pressed);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. The container provider is set (R-smith5-5)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The chart's <c>PlotControl</c> is given all four providers and the overlay.</b>
    /// </summary>
    /// <remarks>
    /// Asserted on its own rather than left to brief 7, because the failure is silent:
    /// <c>PlotExporter.CopyPlotToClipboardAsync</c> opens with <c>if (container is null) return;</c>,
    /// so a plot hosted without a container produces no clipboard content, raises nothing, and looks
    /// exactly like a successful copy. The Match Designer and railRF have each shipped a round
    /// missing this wiring.
    /// </remarks>
    [Fact]
    public void TheChartPlotIsGivenItsContainerAndItsOverlay()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9)));

        // The container itself exists and is the ONE the view hands over — two containers over one
        // plot would number markers independently.
        Assert.NotNull(vm.ChartContainer);
        Assert.Same(vm.ChartContainer.PlotVM.Plot, vm.ChartPlot);
        Assert.Equal(PlotType.Smith, vm.ChartPlot.PlotType);

        string view = StripComments(ReadRepoFile("src/Ui/Views/Smith/SmithChartView.axaml.cs"));

        Assert.Contains("plot.ContainerProvider           = () => container;", view);
        Assert.Contains("plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;", view);
        Assert.Contains("plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;", view);
        Assert.Contains("plot.SelectedMarkersProvider     = container.GetSelectedMarkers;", view);
        Assert.Contains("plot.Overlay = vm.ChartOverlay;", view);

        // And the marker info boxes have somewhere to be drawn from the start — railRF shipped a
        // round in which markers existed before a panel could host their boxes.
        string markup = ReadRepoFile("src/Ui/Views/Smith/SmithChartView.axaml");
        Assert.Contains("MarkerInfoBoxLayer", markup);
        Assert.Contains("ViewModel.PlotHost.MarkerInfoBoxes", markup);
        Assert.Contains("<ddvw:MarkerInfoBoxView/>", markup);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8. Chrome is excluded from autoscale; a node outside the disc is not clamped
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A wildly mismatched generator's target cannot set the window, and an active element that
    /// leaves the unit circle is DRAWN there.</b>
    /// </summary>
    /// <remarks>
    /// Clamping to the disc would be a lie about a stability result (<c>R-smith5-4</c>). The Smith
    /// plot's own autoscale already enforces a unit-circle MINIMUM and grows past it when the data
    /// asks, which is why this brief adds no clamp and no floor of its own — it only has to keep the
    /// chrome out of the measurement.
    /// </remarks>
    [Fact]
    public void ChromeIsExcludedFromAutoscale_AndANodeOutsideTheDiscIsNotClamped()
    {
        // A Z1P with negative resistance: the node it produces has |Γ| > 1.
        var active = new SmithElement
        {
            Kind      = SmithElementKind.Z1P,
            Placement = SmithPlacement.Series,
            Name      = "Z1",
            Values    = { ImpedanceOhm = new Complex(-40.0, 0.0) },
        };

        var vm = new SmithChartViewModel(Design(active));

        var load = LoadGamma(vm.Design);
        Assert.True(load.Magnitude > 1.0,
            $"the fixture is meant to leave the unit circle; |Γ| = {load.Magnitude}");

        // It is on the plot, unclamped, at exactly the Γ the evaluator produced.
        var loadTrace = vm.ChartPlot.Traces.Single(t => t.CubeName == "load (design f)");
        AssertGamma(load, loadTrace.Points[0]);

        // …and the window grew to hold it rather than stopping at the disc.
        var w = vm.ChartPlot.Axes.Window;
        double halfSpan = Math.Max(w.Width, Math.Abs(w.Height)) / 2.0;
        Assert.True(halfSpan > 1.0,
            $"the window stayed inside the unit disc ({halfSpan}), so the node was clamped.");

        // The chrome is out of that measurement: the generator glyphs are annotation, and excluded.
        var targets = vm.ChartPlot.Traces.Single(t => t.CubeName == "Zgen");
        Assert.True(targets.ExcludeFromAutoscale);
        Assert.True(targets.IsAnnotation);

        // A reading is NOT excluded — a load point off the top of the window would be a verdict the
        // reader cannot see, which is the same rule railRF's mask carries.
        Assert.False(loadTrace.ExcludeFromAutoscale);
        Assert.False(loadTrace.IsAnnotation);
    }
}
