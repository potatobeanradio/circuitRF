using System;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The chart pane: the <c>Plot</c> the evaluator fills, its host, the gripper overlay and the drag
/// loop (<c>brief-smith-5-chart.md</c>; <c>docs/design/smith-chart.md</c> §5.4, §4.3).
/// </summary>
public sealed partial class SmithChartViewModel
{
    // ── the host ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The plot host — one <see cref="DataDisplayViewModel"/> with exactly one container in it, laid
    /// out by this document rather than by a canvas.
    /// </summary>
    /// <remarks>
    /// <b>A bare <c>PlotControl</c> is not enough, and two windows have already paid for finding
    /// that out</b> (the Match Designer in 2026-08, railRF in 2026-09). A <c>PlotControl</c> asks its
    /// HOST for the next marker index, the info-box view model, the selected markers and the
    /// container to export, and a host that is null answers "nothing" to all four — <i>silently</i>.
    /// The one that bites last is the container:
    /// <c>PlotExporter.CopyPlotToClipboardAsync</c> opens with <c>if (container is null) return;</c>,
    /// so a plot hosted without one produces NO clipboard content, raises nothing, and looks exactly
    /// like a successful copy (<c>R-smith5-5</c>). Brief 7's copy is written against this being here.
    ///
    /// <para><b>It is not a Data Display document.</b> Nothing is persisted through it, no data-source
    /// library is loaded into it, and the plot cannot be added to or deleted.</para>
    /// </remarks>
    public DataDisplayViewModel PlotHost { get; } =
        new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="ChartPlot"/> — what the marker info boxes are
    /// placed in and what brief 7's copy exports.</summary>
    public PlotContainerViewModel ChartContainer { get; private set; } = null!;

    /// <summary>The one Smith plot. <b>The same instance for the life of the document</b>: it is
    /// REFILLED on every edit rather than replaced, so a binding on it never goes stale and the
    /// user's own pan, zoom and markers survive a rebuild.</summary>
    public Plot ChartPlot => ChartContainer.PlotVM.Plot;

    /// <summary>The grippers, as a <see cref="CircuitRF.Ui.DataDisplay.Controls.IPlotOverlay"/>.</summary>
    public SmithGripperOverlay ChartOverlay { get; private set; } = null!;

    /// <summary>The last evaluation — what the traces were filled from and what the overlay
    /// draws.</summary>
    internal SmithChartScene Scene { get; private set; } = SmithChartScene.Empty;

    /// <summary>
    /// The chart canvas's size in pixels, which the adaptive trajectory sampler measures its chord
    /// error in. The view reports the real one; until it does, the Data Display's own square-plot
    /// default box stands in.
    /// </summary>
    /// <remarks>
    /// <b>A tolerance means nothing without knowing what unit it is in.</b> The sampler is
    /// deliberately not defaulted below the firewall for that reason, and a curve is as smooth as
    /// the ZOOM deserves and no smoother — so a headless caller gets a nominal box rather than an
    /// arbitrary point count.
    /// </remarks>
    public (double W, double H) ChartCanvasSize
    {
        get => _chartCanvasSize;
        set
        {
            if (!(value.W > 0) || !(value.H > 0)) return;
            if (Math.Abs(value.W - _chartCanvasSize.W) < 1.0
             && Math.Abs(value.H - _chartCanvasSize.H) < 1.0) return;
            _chartCanvasSize = value;
            RebuildChart();
        }
    }
    private (double W, double H) _chartCanvasSize =
        (SmithPlotBuilder.NominalCanvas, SmithPlotBuilder.NominalCanvas);

    private void BuildChartHost()
    {
        ChartContainer = PlotHost.AddPlot(PlotType.Smith, FreqUnit.GHz,
                                          left: 0, top: 0,
                                          width:  DataDisplayViewModel.DefaultSquareSize,
                                          height: DataDisplayViewModel.DefaultSquareSize);

        // WHAT A SMITH CHART PLOT IS, said in one place — SmithPlotBuilder.Configure, which is also
        // what a headless `circuitrf smith` applies to the plot it has to create for itself
        // (R-smith10-1). Panning LOCKED and the readout fixed; both are invisible in a picture and
        // change what the plot DOES, which is exactly the kind of setting a second copy loses.
        SmithPlotBuilder.Configure(ChartPlot);

        PlotHost.SelectOnly((PlotContainerViewModel?)null);

        ChartOverlay = new SmithGripperOverlay(this);
    }

    /// <summary>
    /// Re-evaluates the design and refills the plot. Called after every committed edit, every undo,
    /// every snapshot restore and every pointer move of a gripper drag.
    /// </summary>
    /// <remarks>
    /// <b>One evaluation feeds both halves</b> — the traces and the overlay read the same
    /// <see cref="SmithChartScene"/>. Evaluating twice is how a handle ends up off the curve it
    /// belongs to during a drag.
    ///
    /// <para><b>The full plot-changed pipeline runs only when a drag is NOT in flight.</b> It
    /// rebuilds the label strips and the marker info boxes, which is right after an edit and wasted
    /// twenty times a second under a pointer; a redraw request is what a drag needs and all it
    /// needs.</para>
    /// </remarks>
    internal void RebuildChart()
    {
        if (ChartContainer is null) return;   // still constructing

        bool dragging = _dragBefore is not null;

        // THE TRACE SET IS BEING REPLACED BY THIS DOCUMENT, so the inspector's own
        // PlotStructureChanged — which ReloadTraceCards below raises — is not a user adding or
        // removing anything and must not be harvested. Without this the harvest would push an edit,
        // the edit would rebuild the chart, and the rebuild would reload the cards again.
        _rebuildingChart = true;
        try
        {
            Scene      = SmithPlotBuilder.BuildScene(_design, DocumentDirectory, _chartCanvasSize, _lastWindow);
            _traceKeys = SmithPlotBuilder.Fill(ChartPlot, Scene, _design, autoscale: !dragging,
                                               ResolveOverlays());
            _lastWindow = ChartPlot.Axes.Window;

            if (!dragging)
            {
                ChartContainer.OnPlotChanged(this, EventArgs.Empty);

                // railRF's own line, for railRF's own reason: a plot whose traces are produced by
                // something else has to SAY when it has replaced them, or the panel opens on the
                // cards of traces that no longer exist — or, as railRF's did, on none at all. This
                // document builds its inspector before it has any traces, so without this the Plot
                // Properties panel of a saved `.csmith` would open empty every time.
                ChartContainer.Inspector.ReloadTraceCards();
            }
            ChartContainer.RequestPlotRedraw();
        }
        finally { _rebuildingChart = false; }
    }

    /// <summary>True while <see cref="RebuildChart"/> is replacing the plot's traces — see there.</summary>
    private bool _rebuildingChart;

    /// <summary>The window the last frame was drawn in — what the adaptive sampler's canvas map is
    /// built from, so a zoomed-in curve is sampled for the zoom it is actually drawn at.</summary>
    private PlotRect? _lastWindow;

    /// <summary>
    /// Records the chart's window into the document after a pan or a zoom.
    /// </summary>
    /// <remarks>
    /// <b>Not an edit</b>, on the splitters' own terms (<c>R-smith4-3</c>): it writes into the
    /// document's own settings so the chart reopens where it was left, but it pushes NO undo entry
    /// and raises no dirty mark. Prompting to save because someone scrolled is how a close prompt
    /// stops meaning anything; the position rides along on the next real save.
    ///
    /// <para>A stored window is what <see cref="SmithChartSettings.Window"/> calls "not fit", so
    /// from here on the chart keeps the user's framing rather than re-fitting under every edit.</para>
    /// </remarks>
    public void CaptureChartWindow()
    {
        // THE ADMITTANCE GRID IS HARVESTED HERE TOO, and for the same reason the window is: it is
        // toggled from the chart's own context menu, which is PlotControl's and knows nothing about
        // this document. PlotChanged is the one event that says "something about the plot the host
        // may want to keep changed", so both settings come back through it. Like the window, it is
        // NOT an edit — no undo entry and no dirty mark; it rides along on the next real save.
        _design.Chart.ShowAdmittanceGrid = ChartPlot.ShowSmithAdmittanceGrid;

        var w = ChartPlot.Axes.Window;
        if (!(w.Width > 0) || !(w.Height > 0)) return;

        _design.Chart.Window = new SmithWindow
        {
            MinX = w.Left, MaxX = w.Left + w.Width,
            MinY = Math.Min(w.Top, w.Top + w.Height),
            MaxY = Math.Max(w.Top, w.Top + w.Height),
        };
        _lastWindow = w;
    }

    // ── the gripper drag (R-smith5-7, R-smith5-8) ────────────────────────────

    /// <summary>
    /// The whole design as it stood when the drag began — the before-value of the gesture's ONE undo
    /// entry. Non-null exactly while a drag is in flight, which is also what suppresses the
    /// autoscale and the full plot-changed pipeline.
    /// </summary>
    private string? _dragBefore;

    private int            _dragNode      = -1;
    private int            _dragElement   = -1;
    private SmithParameter _dragParameter = SmithParameter.None;
    private Complex        _dragZIn;
    private string         _dragDescription = "";

    /// <summary>True while a gripper is being dragged.</summary>
    public bool IsDraggingGripper => _dragBefore is not null;

    /// <summary>
    /// What a pinned drag is saying — the parameter and the limit it stopped at, or null.
    /// </summary>
    /// <remarks>
    /// <b>The sentence is <see cref="SmithInverse"/>'s, surfaced rather than re-written.</b> A drag
    /// that asked for a negative inductance pins at zero and says so; the handle KEEPS TRACKING the
    /// cursor while it does (<c>R-smith3-4</c>), because a handle that stops moving reads as a
    /// broken drag rather than as a limit.
    /// </remarks>
    public string? DragPin
    {
        get => _dragPin;
        private set
        {
            if (_dragPin == value) return;
            _dragPin = value;
            OnPropertyChanged();
        }
    }
    private string? _dragPin;

    /// <summary>
    /// A gripper was pressed. <b>The before-value is captured HERE</b> and the undo entry is pushed
    /// on release (<c>R-smith5-8</c>).
    /// </summary>
    /// <returns>False when this node cannot be dragged — node 0, an out-of-range index, or a file
    /// element, none of which the overlay offers a handle for in the first place.</returns>
    internal bool BeginGripperDrag(int nodeIndex)
    {
        if (_dragBefore is not null) EndGripperDrag(cancelled: true);

        if (nodeIndex <= 0 || nodeIndex >= Scene.Nodes.Count) return false;

        int elementIndex = Scene.Nodes[nodeIndex].ElementIndex;
        if (elementIndex < 0 || elementIndex >= _design.Elements.Count) return false;

        var element = _design.Elements[elementIndex];
        if (SmithComponentMap.UsesFile(element.Kind)) return false;

        var p = SmithComponentMap.ActiveParameterOf(element);
        if (p == SmithParameter.None) return false;

        _dragNode      = nodeIndex;
        _dragElement   = elementIndex;
        _dragParameter = p;

        // THE INPUT IMPEDANCE IS CAPTURED ONCE, at the press. It is node k−1 of the walk, which is
        // UPSTREAM of the element being dragged and therefore cannot move while this drag runs —
        // and reading it back out of a scene that a pinned value may have emptied is how a drag
        // stops responding halfway through.
        _dragZIn = Scene.Nodes[nodeIndex - 1].Z;

        _dragBefore      = SmithDesignIo.SerializeUnvalidated(_design);
        _dragDescription = $"Drag {(string.IsNullOrWhiteSpace(element.Name) ? element.Kind.ToString() : element.Name)} {p}";
        DragPin          = null;
        return true;
    }

    /// <summary>
    /// The pointer moved to <paramref name="gamma"/>. Solves the inverse, writes the value and
    /// redraws — <b>and pushes nothing</b>.
    /// </summary>
    internal void DragGripperTo(Complex gamma)
    {
        if (_dragBefore is null || _dragElement < 0) return;

        InverseResult result;
        try
        {
            result = SmithInverse.Solve(_design, _dragElement, _dragParameter, _dragZIn, gamma,
                                        _design.DesignFrequencyHz, _design.Chart.Z0Ohm);
        }
        catch (Exception)
        {
            // Solve throws only for a gripper that should never have existed — a file element, or a
            // parameter the kind does not expose. BeginGripperDrag refuses both, so reaching here is
            // a programming error rather than something the user did; the drag simply does nothing
            // rather than taking the window down under the pointer.
            return;
        }

        SmithInverse.Apply(_design.Elements[_dragElement], _dragParameter, result.Value);
        DragPin = result.PinReason;
        RefreshDerived();
    }

    /// <summary>
    /// The drag finished. <b>ONE undo entry, pushed here, carrying the value captured on
    /// press</b> — or, when <paramref name="cancelled"/>, the before-state restored and nothing
    /// pushed at all (<c>R-smith5-8</c>).
    /// </summary>
    /// <remarks>
    /// <b>This is a requirement, not a style preference.</b> The Match Designer shipped a defect
    /// where a two-way-bound slider's coercing write-back reached an unguarded setter DURING
    /// <c>Undo</c>, so every undo ADDED an entry, redo was wiped, and eight edits took fourteen
    /// undos to unwind (<c>src/Ui/Match/RESOLVED.md</c>). The rule that comes out of it — a control's
    /// write-back is not an edit — is why every pointer move above goes to
    /// <see cref="RefreshDerived"/> and never to <see cref="Edit"/>, and why the only push in the
    /// whole gesture is the one below.
    ///
    /// <para><b>A drag that changed nothing pushes nothing either</b>: a press and release on a
    /// gripper is a click, and a stack full of no-ops is the same defect by a slower route.</para>
    /// </remarks>
    internal void EndGripperDrag(bool cancelled)
    {
        if (_dragBefore is not { } before) return;

        _dragBefore    = null;
        _dragNode         = -1;
        _dragElement      = -1;
        _dragParameter    = SmithParameter.None;
        _dragGeneratorRow = -1;
        DragPin           = null;

        // THE Q FLAG IS CLEARED HERE and not only in EndQDrag, because this is the single exit both
        // gestures leave by. BeginGripperDrag and BeginQDrag each force-cancel an in-flight drag
        // through this method, so a Q drag abandoned that way used to leave _draggingQ standing —
        // and the next pointer move would have been routed to DragQTo while a gripper was under the
        // hand. The overlay cannot reach that order today; a flag whose correctness depends on the
        // caller's order is one that stops being correct when the caller changes.
        _draggingQ = false;

        if (cancelled)
        {
            ApplySnapshot(before);
            return;
        }

        string after = SmithDesignIo.SerializeUnvalidated(_design);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            RefreshDerived();
            return;
        }

        UndoRedo.Execute(new SmithSnapshotCommand(this, before, after, _dragDescription));
    }

    // ── the generator drag (owner instruction, 2026-09-19) ───────────────────

    /// <summary>The generator-table row a shift-drag is moving, or −1.</summary>
    private int _dragGeneratorRow = -1;

    /// <summary>
    /// A generator glyph was shift-pressed. <b>One gesture is one undo entry</b>, exactly as a
    /// gripper drag is: the before-state is captured here and pushed on release.
    /// </summary>
    /// <returns>False when the row is out of range, which is what the overlay refuses to offer a
    /// handle for.</returns>
    /// <remarks>
    /// <b>This is an edit to node 0 of the walk</b>, which is why it is a modified gesture rather
    /// than an ordinary one. Every other handle on this chart moves a COMPONENT; this one moves the
    /// impedance the whole cascade starts from, so every trajectory, every load point and the band
    /// move with it. An unmodified press there still pans the chart, exactly as it did — the glyph
    /// is deliberately not a thing you can nudge by accident.
    /// </remarks>
    internal bool BeginGeneratorDrag(int rowIndex)
    {
        if (_dragBefore is not null) EndGripperDrag(cancelled: true);

        if (rowIndex < 0 || rowIndex >= _design.Generator.Rows.Count) return false;

        _dragGeneratorRow = rowIndex;
        _dragBefore       = SmithDesignIo.SerializeUnvalidated(_design);
        _dragDescription  = $"Drag generator at {SmithPlotBuilder.FrequencyLabel(_design.Generator.Rows[rowIndex].FrequencyHz)}";
        DragPin           = null;
        return true;
    }

    /// <summary>
    /// The pointer moved to <paramref name="gamma"/>. Writes the row's R and X and redraws —
    /// <b>and pushes nothing</b>.
    /// </summary>
    /// <remarks>
    /// <b>Z = Z₀·(1+Γ)/(1−Γ), which is <see cref="SmithCascade.Gamma"/>'s own inverse</b> and is the
    /// only arithmetic in the gesture: there is nothing to solve, because the handle IS the
    /// quantity. Γ at or outside the unit circle is left alone rather than written — 1 − Γ is zero
    /// at Γ = 1, and a table row set to infinity is a document that refuses on the next keystroke
    /// with the pointer still down.
    ///
    /// <para><b>The FREQUENCY is untouched.</b> The glyphs sit at one frequency each and the table
    /// is kept sorted by frequency; a drag that moved one sideways in frequency would re-sort the
    /// table under the hand holding it.</para>
    /// </remarks>
    internal void DragGeneratorTo(Complex gamma)
    {
        if (_dragBefore is null || _dragGeneratorRow < 0) return;
        if (!double.IsFinite(gamma.Real) || !double.IsFinite(gamma.Imaginary)) return;

        var denom = Complex.One - gamma;
        if (denom.Magnitude < 1e-9) return;

        var z = _design.Chart.Z0Ohm * (Complex.One + gamma) / denom;
        if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary)) return;

        var row = _design.Generator.Rows[_dragGeneratorRow];
        row.ResistanceOhm = z.Real;
        row.ReactanceOhm  = z.Imaginary;

        // THE TABLE IS A LIVE READOUT DURING THIS DRAG (owner instruction): the row's two cells
        // show the impedance under the pointer as it moves. RefreshDerived re-reads every row view
        // model, which is what puts the numbers there, and rebuilds the chart from the same
        // evaluation — so the glyph, the trajectories and the two cells are one answer.
        RefreshDerived();
    }

    // ── the constant-Q drag (R-smith9-2) ─────────────────────────────────────

    /// <summary>
    /// A constant-Q arc was pressed. <b>One gesture is one undo entry</b>, exactly as a gripper drag
    /// is: the before-state is captured here and pushed on release.
    /// </summary>
    /// <returns>False when the pair is off or is carrying a Q that is not one — neither of which
    /// the overlay offers a handle for.</returns>
    internal bool BeginQDrag()
    {
        if (_dragBefore is not null) EndGripperDrag(cancelled: true);

        var q = _design.ConstantQ;
        if (!q.Enabled || !(double.IsFinite(q.Q) && q.Q > 0)) return false;

        // THE SAME before-state, the same in-flight flag and the same suppression of the autoscale
        // and the plot-changed pipeline that a gripper drag uses. A second drag mechanism beside the
        // first is a second place for "one gesture is one undo entry" to stop being true — which is
        // why _dragElement stays −1 here and DragGripperTo is never reached.
        _dragBefore      = SmithDesignIo.SerializeUnvalidated(_design);
        _dragDescription = "Drag constant-Q arcs";
        _draggingQ       = true;
        DragPin          = null;
        return true;
    }

    /// <summary>True while a constant-Q arc is being dragged rather than a gripper.</summary>
    private bool _draggingQ;

    /// <summary>
    /// The pointer moved to <paramref name="gamma"/> with shift <paramref name="shift"/>. Solves for
    /// Q, writes it and redraws — <b>and pushes nothing</b>.
    /// </summary>
    /// <remarks>
    /// <b>Dragging either branch moves both</b>, because they are one setting — which is why nothing
    /// here knows which branch was grabbed. <b>Shift rounds the computed Q to the nearest quarter
    /// BEFORE it is stored</b> (<c>R-smith9-2</c>), so a shift-drag lands on an exact quarter and a
    /// later un-shifted drag starts from that exact quarter rather than from a rounded display of
    /// something else.
    /// </remarks>
    internal void DragQTo(Complex gamma, bool shift)
    {
        if (!_draggingQ || _dragBefore is null) return;

        var result = SmithQArcs.Solve(_design.ConstantQ.Q, gamma, shift);

        _design.ConstantQ.Q = result.Q;
        DragPin             = result.PinReason;
        RefreshDerived();
    }

    /// <summary>The Q drag finished — <b>one undo entry</b>, or none when it changed nothing or was
    /// cancelled. <see cref="EndGripperDrag"/>'s contract, reached by the same route.</summary>
    internal void EndQDrag(bool cancelled)
    {
        if (!_draggingQ) return;
        EndGripperDrag(cancelled);          // …which clears _draggingQ — see there for why.
    }
}
