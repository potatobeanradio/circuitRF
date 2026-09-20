using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using RfCore;
using SkiaSharp;

namespace CircuitRF.Render.Smith;

/// <summary>
/// One trace on the chart and the name a marker on it is stored against
/// (<c>brief-smith-8-overlays-markers.md</c> <c>R-smith8-5</c>).
/// </summary>
/// <remarks>
/// A `.cdd` nests its markers under their trace and needs no such thing. Every trace on THIS chart
/// is derived and is rebuilt from the design on each edit, so the association has to be written
/// down, and it is written as the trace's LABEL — an element's name, <c>load</c>, or an overlay's
/// file and quantity — because an index moves when an element is deleted.
/// </remarks>
/// <param name="Key">The label a marker is stored against — see <c>SmithPlotBuilder.Add</c>.</param>
/// <param name="Trace">The trace itself.</param>
/// <param name="ElementIndex">
/// Which cascade element's trajectory this is, or −1 for everything that is not one — the arcs, the
/// band, the load and generator points, the overlays.
/// <b>Carried rather than recovered from the label</b>, because the label is the element's own NAME
/// and a caller matching on it would be matching on something the user can type; and rather than
/// from the position in the list, which moves with the arcs, the band and the overlay count. It is
/// what lets a selection highlight find its curve (<c>ApplyElementHighlight</c>).
/// </param>
public readonly record struct SmithTraceKey(string Key, Trace Trace, int ElementIndex = -1);

/// <summary>An overlay that resolved, ready to go on the plot.</summary>
/// <remarks>
/// <b>Resolution is not this file's</b> — <see cref="SmithOverlays.Load"/> does it through the
/// `.cdd`'s own <c>PlotConfigLoader.LoadTrace</c>, so the caller can report what failed and why
/// while the chart carries on drawing everything that did resolve (<c>R-smith8-2</c>).
///
/// <para><b>There is no Visible flag and there must not be one</b> (<c>R-smith12-6</c>):
/// <c>TraceProperties.Enabled</c> is read by nothing, and a hidden trace would still sit in the
/// trace list, the legend and the Add Marker menu. On a Data Display you delete the trace; here the
/// card's trash is the same affordance.</para>
/// </remarks>
public readonly record struct SmithOverlayTrace(string Key, Trace Trace);

/// <summary>
/// Builds the chart's <c>Plot</c> from the evaluator — <b>the traces, and nothing that draws</b>
/// (<c>brief-smith-5-chart.md</c> <c>R-smith5-1</c>, <c>R-smith5-2</c>,
/// <c>docs/design/smith-chart.md</c> §5.4).
/// </summary>
/// <remarks>
/// <b>There is one Smith renderer and this is not a second one.</b> The chart is a Data Display
/// <c>Plot</c> in <c>PlotType.Smith</c> rendered by <c>PlotControl</c>, which brings the grid, the
/// arcs, the zoom-aware numbering, pan, zoom, marker add / drag / hit-test, the inspector, the
/// context menu and the axis limits — none of it re-implemented here. This file fills the plot's
/// <c>Traces</c> collection and sets its window; the transient chrome that a trace cannot express —
/// the grippers, the arrowheads, the load-point labels — is <see cref="SmithGripperOverlay"/>'s,
/// over the top.
///
/// <para>That is railRF's choice rather than harmonicaRF's, and the reason is specific: harmonicaRF
/// wrote its own canvas because it had a frame budget to defend and a contour pipeline to schedule,
/// and this tool has neither — a whole re-evaluation is a few hundred complex divides. A second
/// Smith renderer would drift from the first and the difference would be invisible until someone
/// compared a screenshot with an export.</para>
///
/// <para><b>Every trace is cube-bound with no source file</b>, which is what every synthetic trace in
/// this repository is: <c>Trace</c> has no constructor that does not take an <c>SNP</c> and the cube
/// path ignores it, so a one-point placeholder carries the identity and <c>SetCubeData</c> carries
/// the numbers. On a complex plot a complex cube's points ARE (Re Γ, Im Γ), which is why no
/// transform is applied and none may be.</para>
/// </remarks>
public static class SmithPlotBuilder
{
    /// <summary>The nominal canvas the trajectory sampler measures its chord error in when the view
    /// has not yet reported a real one — the Data Display's own square-plot default box.</summary>
    public const double NominalCanvas = 420.0;

    // ── the palette ──────────────────────────────────────────────────────────

    /// <summary>
    /// The colour of element <paramref name="elementIndex"/>'s trajectory, <b>by index into the
    /// application's own order rather than by naming a colour</b> — the Match Designer's finding,
    /// so a change to the palette moves this tool with it.
    /// </summary>
    public static int ColorIndexFor(int elementIndex)
        => TraceProperties.LineColorOrder[
               ((elementIndex % TraceProperties.LineColorOrder.Length)
                + TraceProperties.LineColorOrder.Length) % TraceProperties.LineColorOrder.Length];

    /// <summary>The same colour as a Skia colour — what the overlay draws element
    /// <paramref name="elementIndex"/>'s gripper in, so the ring and its curve match by
    /// construction (§5.4: <i>a small hollow ring in the trajectory's own colour</i>).</summary>
    public static SKColor ColorFor(int elementIndex)
        => TraceProperties.ColorLUT[ColorIndexFor(elementIndex)];

    /// <summary>The colour of the load points and of node 0's anchor — grey, because they are the
    /// READING rather than one of the moves, and a reading in a curve's colour reads as belonging to
    /// that curve.</summary>
    public static SKColor ReadingColor => TraceProperties.ColorLUT[5];

    // ── the scene ────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates the design once, for both the traces and the overlay.
    /// </summary>
    /// <param name="canvas">The chart's canvas size, which the adaptive sampler measures its chord
    /// error in. <b>Supplied rather than defaulted inside the sampler</b> — a tolerance means
    /// nothing without knowing what unit it is in, and the whole point of the adaptive sampler is
    /// that a curve is as smooth as the ZOOM deserves and no smoother.</param>
    /// <param name="window">The window the LAST frame was drawn in, which is what makes the
    /// sampler's canvas map right during a drag. A null window is the fit the first frame has not
    /// taken yet, and the unit disc is the honest stand-in for it.</param>
    public static SmithChartScene BuildScene(
        SmithDesign design, string? documentDirectory,
        (double W, double H) canvas, PlotRect? window)
    {
        ArgumentNullException.ThrowIfNull(design);

        double z0 = design.Chart.Z0Ohm;
        double f  = design.DesignFrequencyHz;

        // The sampler's world→canvas map, built from the window the last frame used. A Plot with no
        // traces on it autoscales to the unit disc, which is exactly the fallback wanted here.
        var probe = new Plot(PlotType.Smith, FreqUnit.GHz);
        if (window is { } w) probe.Axes.Window = w;
        var tf = PlotRenderer.BuildTransforms(probe, canvas);

        (double X, double Y) ToCanvas(Complex g)
        {
            var p = tf.PrimaryToCanvas(g.Real, g.Imaginary);
            return (p.X, p.Y);
        }

        SmithNode[]                    nodes;
        IReadOnlyList<SmithTrajectory> curves;
        try
        {
            nodes  = SmithCascade.Evaluate(design, f, documentDirectory);
            curves = SmithCascade.Trajectories(design, f, ToCanvas, documentDirectory: documentDirectory);
        }
        catch (Exception)
        {
            // The refusal half of the status strip is already saying what is wrong, with its numbers
            // in it. An empty chart beside that sentence is the honest picture; a partial one would
            // invite the reader to trust the part that drew.
            return SmithChartScene.Empty;
        }

        var loads   = new List<SmithLoadPoint>();
        var targets = new List<SmithGeneratorPoint>();
        var dropped = new List<double>();

        // EVERY GENERATOR-TABLE ROW GETS A LOAD POINT (owner instruction, 2026-09-19). The table is
        // the set of frequencies this design is about, so the chart shows where the load lands at
        // each of them; the swept band is the OPTIONAL extra, and it adds frequencies rather than
        // replacing these.
        for (int rowIndex = 0; rowIndex < design.Generator.Rows.Count; rowIndex++)
        {
            var row = design.Generator.Rows[rowIndex];
            try
            {
                var rowNodes = SmithCascade.Evaluate(design, row.FrequencyHz, documentDirectory);
                loads.Add(new SmithLoadPoint(
                    SmithCascade.Gamma(rowNodes[^1].Z, z0),
                    FrequencyLabel(row.FrequencyHz),
                    IsDesignFrequency: row.FrequencyHz == f));

                // THE GENERATOR ITSELF, at this row's own frequency — Γ(Z_gen), not Γ(conj(Z_gen))
                // (owner report, 2026-09-19: the glyph rendered at the conjugate of what the table
                // says). It used to be drawn at the conjugate-match TARGET, which is the mirror of
                // this point about the real axis and is therefore indistinguishable from it on a
                // real generator — so the one glyph the table can be checked against was the one
                // place the table's own numbers were not.
                targets.Add(new SmithGeneratorPoint(rowIndex, SmithCascade.Gamma(rowNodes[0].Z, z0)));
            }
            catch (Exception)
            {
                // One row that cannot be evaluated — an S1P or S2P element whose file does not span
                // it — drops that row and no other, and SAYS SO. It used to drop it silently, which
                // is the one outcome the rule above cannot tolerate: a chart with three generator
                // rows and two load points on it looks exactly like a chart with two rows.
                dropped.Add(row.FrequencyHz);
            }
        }

        // THE DESIGN FREQUENCY NEED NOT BE A ROW (§3.6) and the strip reports it, so it is drawn
        // whether or not the table happens to contain it. Without this the one point the numbers
        // along the bottom of the window are about would be missing from the picture.
        if (!loads.Any(p => p.IsDesignFrequency))
            loads.Add(new SmithLoadPoint(SmithCascade.Gamma(nodes[^1].Z, z0),
                                         FrequencyLabel(f), IsDesignFrequency: true));

        // ── the constant-Q arcs (R-smith9-1) ─────────────────────────────────
        //
        //  CLOSED FORM, and none of it is here: SmithQArcs owns the circle and the in-disc range, so
        //  brief 10's headless render draws the same two arcs this window does. Q is guaranteed
        //  finite and positive by SmithDesign.Refusal, and the guard below is what keeps a document
        //  the strip is ALREADY complaining about from throwing on its way to the screen.
        Complex[] qInd = [], qCap = [];
        if (design.ConstantQ.Enabled && double.IsFinite(design.ConstantQ.Q) && design.ConstantQ.Q > 0)
        {
            qInd = SmithQArcs.Arc(design.ConstantQ.Q, inductive: true);
            qCap = SmithQArcs.Arc(design.ConstantQ.Q, inductive: false);
        }

        // ── the swept band (R-smith9-4) ──────────────────────────────────────
        var band = SmithBand.Evaluate(design, documentDirectory);

        return new SmithChartScene
        {
            Nodes            = nodes,
            NodeGamma        = [.. nodes.Select(n => SmithCascade.Gamma(n.Z, z0))],
            Trajectories     = curves,
            LoadPoints       = loads,
            GeneratorPoints  = targets,
            QArcInductive    = qInd,
            QArcCapacitive   = qCap,
            Band             = band.Gamma,
            Note             = dropped.Count > 0 ? DroppedRowNote(dropped) : null,
        };
    }

    /// <summary>
    /// The sentence a generator row with no load point puts in the status strip, <b>naming the
    /// frequencies</b> (owner instruction, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>A dropped row is reported, never simply absent.</b> Every generator-table frequency is
    /// supposed to carry a load point; the only thing that can stop one is a file element whose
    /// Touchstone does not span that frequency, and a chart quietly a point short looks exactly like
    /// a chart of a shorter table. The frequencies are spelled by <see cref="FrequencyLabel"/> — the
    /// strip's own spelling — so the sentence and the chart cannot disagree about a frequency.
    /// </remarks>
    private static string DroppedRowNote(IReadOnlyList<double> hz)
        => $"No load point could be drawn at {string.Join(", ", hz.Select(FrequencyLabel))} — a file "
         + "element does not span that frequency, so the cascade cannot be evaluated there. Every "
         + "other generator-table row is drawn.";

    /// <summary>The status strip's own spelling of a frequency, so the label on the chart and the
    /// sentence along the bottom cannot disagree about which point is which.</summary>
    public static string FrequencyLabel(double hz)
        => MatchValueFormat.FormatWithUnit(hz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 4);

    // ── the plot itself ──────────────────────────────────────────────────────

    /// <summary>
    /// A chart <c>Plot</c>, with the two settings this tool's own chart carries.
    /// </summary>
    /// <remarks>
    /// <b>ONE place decides what a Smith Chart plot IS</b> (<c>R-smith10-1</c>). The window does not
    /// call this — its plot comes with the container <c>DataDisplayViewModel.AddPlot</c> creates —
    /// so it calls <see cref="Configure"/> on that one instead, and a headless caller with no
    /// container calls this. What must not happen is a second list of settings in a verb: the flags
    /// below are invisible in a picture and change what the plot DOES, so a copy that drifted would
    /// be found by somebody noticing that a headless chart behaves unlike the window's.
    /// </remarks>
    public static Plot NewChartPlot() => Configure(new Plot(PlotType.Smith, FreqUnit.GHz));

    /// <summary>The two settings, applied to a plot somebody else created.</summary>
    /// <remarks>
    /// <b>Panning starts LOCKED</b> (owner instruction, 2026-09-19), which reverses this file's
    /// original choice. The first revision left it unlocked because a press on empty chart should
    /// pan and there is no Data Display canvas here for the move/select gesture to conflict with —
    /// but the gesture this chart actually spends its time on is a DRAG, on a gripper, on a Q arc
    /// or on a marker, and every press that misses one of those by a few pixels slid the chart
    /// instead. The menu item is still there and still says what state it is in; what changed is
    /// which state a chart opens on.
    ///
    /// <para><b>The trace set is the DOCUMENT's</b>: every trace is rebuilt from the design on each
    /// edit, so one added in the Plot Inspector would be gone by the next keystroke and one removed
    /// would be back. railRF's own reasoning, and its own flag.</para>
    /// </remarks>
    public static Plot Configure(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        plot.Axes.LockedPanning = true;
        plot.IsFixedReadout     = true;

        // …BUT THE TRACE SET IS OPEN (R-smith12-1). IsFixedReadout means two things — the plot TYPE
        // and the trace SET — and only the first is wanted here: it is a Smith chart and it stays
        // one, while reference data goes onto it the way it goes onto a Smith chart on a Data
        // Display, through Plot Properties ▸ Add. What makes that affordable is that a trace the
        // user adds is written into the DOCUMENT and restored on the next rebuild; a plot with
        // nowhere to write one down must leave this alone.
        plot.AllowUserTraces    = true;

        // MARKERS ARE FREE HERE (owner instruction, 2026-09-19). On an ordinary Data Display trace a
        // marker is a reading of that trace at a frequency; on a matching chart it is a TARGET the
        // user is aiming the network at, which is a position. Shift while dragging snaps it onto the
        // nearest curve — see Marker.FreePosition, where the two behaviours are set out.
        plot.FreeMarkers        = true;
        return plot;
    }

    // ── the traces ───────────────────────────────────────────────────────────

    /// <summary>
    /// Refills <paramref name="plot"/>'s traces from <paramref name="scene"/>, in
    /// <c>R-smith5-2</c>'s draw order.
    /// </summary>
    /// <param name="autoscale">False while a gripper drag is in flight. <b>A window that re-fitted
    /// on every pointer move would slide the chart out from under the hand holding it</b>, and the
    /// gripper would stop being where the cursor is — which is the same complaint a pinned drag that
    /// stopped tracking produces.</param>
    public static IReadOnlyList<SmithTraceKey> Fill(
        Plot plot, SmithChartScene scene, SmithDesign design, bool autoscale,
        IReadOnlyList<SmithOverlayTrace>? overlays = null)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(design);

        // THE ADMITTANCE GRID IS THE DOCUMENT'S, applied on every refill. It is a Plot setting
        // rather than a Smith-Chart one — the context menu that toggles it is PlotControl's and
        // works on any Smith chart — so this is the one line that keeps the `.csmith`'s own copy and
        // the plot in step, and it is what makes a headless `circuitrf smith` draw the grid the
        // document was saved with.
        plot.ShowSmithAdmittanceGrid = design.Chart.ShowAdmittanceGrid;

        // ONE BATCH OVER THE WHOLE REFILL (owner report, 2026-09-19 — the drag glitch). Clearing and
        // re-adding raises CollectionChanged once per trace, and Plot's handler AUTOSCALES on each
        // one, including on the empty plot the Clear leaves behind. During a gripper or constant-Q
        // drag this window is rebuilt on every pointer move, so the user's own framing was being
        // re-fitted underneath the hand holding it. The window is decided at the bottom of this
        // method and nowhere else.
        using var batch = plot.BeginTraceBatch();

        plot.Traces.Clear();
        var keys = new List<SmithTraceKey>();

        // ── the constant-Q arcs, FIRST (R-smith9-3) ──────────────────────────
        //
        //  BENEATH THE TRAJECTORIES, which is what first means: a trace's draw order is its order in
        //  this collection, and the arcs are chrome the work is read against rather than part of it.
        //
        //  They carry NO MARKER (IsAnnotation, which is exactly "not a reading" — no Add Marker
        //  entry, no double-click, no row in another trace's readout) and they are out of the
        //  AUTOSCALE (R-smith5-4): the pair passes through Γ = ±1 at every Q, so a chart that framed
        //  them would be pinned to the whole unit disc forever and the cascade would never fill it.
        AddQArc(plot, keys, "Q (inductive)",  scene.QArcInductive);
        AddQArc(plot, keys, "Q (capacitive)", scene.QArcCapacitive);

        // ── one per ENABLED element ──────────────────────────────────────────
        foreach (var curve in scene.Trajectories)
        {
            var element = design.Elements[curve.ElementIndex];

            // An S1P or an S2P has no parameter to sweep, so its curve is a two-point DASHED chord
            // from Γ_in to Γ_out rather than a walk: there is no path between its ends to tell the
            // truth about, and a solid line would claim there is (R-smith2-6).
            var trace = CubeTrace(
                ElementLabel(element, curve.ElementIndex),
                Style(ColorIndexFor(curve.ElementIndex),
                      curve.IsChord ? LineType.Dashed : LineType.Solid, width: 1.0));

            SetGamma(trace, curve.Gamma);
            Add(plot, keys, ElementLabel(element, curve.ElementIndex), trace, curve.ElementIndex);
        }

        // ── the swept band (R-smith9-4) ──────────────────────────────────────
        //
        //  BEFORE the load points, so the points it passes through sit on top of it rather than
        //  under it. It is a READING and not chrome — it is the same load at other frequencies — so
        //  it takes part in the autoscale and it may carry markers, which is the whole of what makes
        //  bandwidth readable off it.
        if (scene.Band.Count > 0)
        {
            var bandProps = Style(ReadingColorIndex, LineType.Solid, width: 0.75);
            bandProps.MarkerEnabled = false;
            bandProps.LineOpacity   = 0.7;
            bandProps.Custom        = false;

            var bandTrace = CubeTrace("band", bandProps);
            SetGamma(bandTrace, scene.Band);
            Add(plot, keys, "band", bandTrace);
        }

        // ── the load points ──────────────────────────────────────────────────
        //
        //  Two traces rather than one, because the emphasis is a MARKER SIZE and that is a per-trace
        //  property. The design frequency's point is the larger of the two (R-smith5-2).
        AddPoints(plot, keys, "load", scene.LoadPoints.Where(p => !p.IsDesignFrequency).Select(p => p.Gamma),
                  ReadingColorIndex, size: 1.8, annotation: false, excludeFromAutoscale: false);
        AddPoints(plot, keys, "load (design f)", scene.LoadPoints.Where(p => p.IsDesignFrequency).Select(p => p.Gamma),
                  ReadingColorIndex, size: 3.6, annotation: false, excludeFromAutoscale: false);

        // ── the generator points ─────────────────────────────────────────────
        //
        //  IsAnnotation is exactly "not a reading": it is drawn like any other trace and excluded
        //  from everything a MARKER does — no Add Marker entry, no double-click, no row in another
        //  trace's readout — which is what R-smith5-2's "un-selectable" asks for, already built.
        //
        //  And excluded from the autoscale (R-smith5-4), because a wildly mismatched generator would
        //  otherwise set the window and squash the cascade the user is actually looking at into a
        //  corner of it.
        //
        //  HALF THE SIZE THE TARGETS WERE (owner instruction, 2026-09-19): the table is a few
        //  frequencies a few percent apart, so these land almost on top of one another, and at 3.0
        //  the cluster read as one blob.
        if (design.Chart.ShowTargets)
            AddPoints(plot, keys, "Zgen", scene.GeneratorPoints.Select(g => g.Gamma),
                      ColorLUTGrey, size: 1.5,
                      annotation: true, excludeFromAutoscale: true, opacity: 0.45,
                      markerType: MarkerType.Plus);

        // ── the overlays (R-smith8-1, R-smith8-4) ───────────────────────────
        //
        //  LAST, so reference material draws OVER the work rather than under it, and so one added
        //  or removed cannot renumber the trajectories' colours.
        //
        //  They are ordinary traces and this loop is the whole of what makes them one: their
        //  quantity, their renormalization and their autoscale exclusion are the Trace's OWN fields,
        //  restored from the document's trace config by PlotConfigLoader.LoadTrace.
        //
        //  AND THEY DO NOT COME THROUGH CubeTrace, which is what leaves ExcludeFromAxisLabels clear
        //  on them (R-smith12-7): everything this file DERIVES keeps its name off the axes, and the
        //  overlays are the reference data the user chose by name — they are what the axis labels
        //  are for. It is also how the inspector tells the two apart.
        foreach (var overlay in overlays ?? [])
            Add(plot, keys, overlay.Key, overlay.Trace);

        // ── the markers (R-smith8-5) ────────────────────────────────────────
        //
        //  THE DOCUMENT IS THE AUTHORITY, and it has to be: every trace above was just built from
        //  scratch and the markers that were on the previous set went with them. So they are
        //  re-attached here, from `design.Markers`, on every rebuild — which is also what makes an
        //  undo of a marker edit restore the markers rather than only the numbers.
        // EVERY CURVE HERE IS REFERENCED TO THE DOCUMENT'S OWN Z₀ (§3.4), and saying so on the trace
        // is what lets a freely-placed marker report an impedance rather than a Γ and a guess: its
        // readout is Z₀·(1+Γ)/(1−Γ) against this number. An overlay that carries an explicit
        // override is left alone — that one has already been told what it is referenced to.
        var chartZ0 = new Complex(design.Chart.Z0Ohm, 0);
        foreach (var t in plot.Traces)
            if (!t.Z0OverrideEnabled) t.Z0 = chartZ0;

        RestoreMarkers(keys, design);

        plot.SetAxesViewport();

        // ── the window ───────────────────────────────────────────────────────
        //
        //  A stored window is the user's own pan and zoom and wins; null means FIT, which is what
        //  SmithChartSettings.Window's own remark says it means.
        //
        //  THE UNIT-CIRCLE MINIMUM IS THE PLOT'S OWN and is left alone: a node outside the unit
        //  circle — an active S2P, a Z1P with negative R — is DRAWN, because clamping to the disc
        //  would be a lie about a stability result (R-smith5-4).
        if (!autoscale) return keys;

        if (design.Chart.Window is { } stored)
            plot.Axes.Window = new PlotRect(stored.MinX, stored.MinY,
                                            stored.MaxX - stored.MinX, stored.MaxY - stored.MinY);
        else
            plot.Autoscale(force: true);

        return keys;
    }

    /// <summary>
    /// One branch of the constant-Q pair, as a thin dashed chrome trace.
    /// </summary>
    /// <remarks>
    /// <b>The geometry is <see cref="SmithQArcs.Arc"/>'s and nothing here re-derives it</b>
    /// (<c>R-smith9-1</c>). Dashed and grey because the arcs are a RULER laid over the work: a solid
    /// line in a palette colour would read as one more element's trajectory, which is the one thing
    /// they must not look like.
    /// </remarks>
    private static void AddQArc(Plot plot, List<SmithTraceKey> keys, string name,
                                IReadOnlyList<Complex> arc)
    {
        if (arc.Count == 0) return;

        var props = Style(ReadingColorIndex, LineType.Dashed, width: 0.75);
        props.MarkerEnabled = false;
        props.LineOpacity   = 0.55;
        props.Custom        = false;

        var trace = CubeTrace(name, props);
        trace.IsAnnotation         = true;
        trace.ExcludeFromAutoscale = true;
        SetGamma(trace, arc);
        Add(plot, keys, name, trace);
    }

    /// <summary>Puts a trace on the plot and records the name a marker is stored against.</summary>
    /// <remarks>
    /// <b>The key is the LABEL, not the index.</b> An index moves the moment an element is deleted
    /// or an overlay row is removed, and a marker that silently slid onto the next curve would be a
    /// reading reported against the wrong thing. A duplicate label is disambiguated rather than
    /// refused — two elements cannot share a name (<c>SmithDesign.Refusal</c> says so) but two
    /// overlays on the same file and quantity can, and that is not worth stopping a document over.
    /// </remarks>
    private static void Add(Plot plot, List<SmithTraceKey> keys, string name, Trace trace,
                            int elementIndex = -1)
    {
        string key = name;
        for (int n = 2; keys.Any(k => string.Equals(k.Key, key, StringComparison.Ordinal)); n++)
            key = $"{name} ({n})";

        keys.Add(new SmithTraceKey(key, trace, elementIndex));
        plot.Traces.Add(trace);
    }

    /// <summary>
    /// Re-attaches the document's markers to the curves they were taken on.
    /// </summary>
    /// <remarks>
    /// A marker whose trace is no longer on the chart — an element deleted, an overlay row removed
    /// or a file that stopped resolving — <b>lands on the first curve rather than disappearing</b>.
    /// It is a reading somebody took, and the alternative is that deleting one element silently
    /// deletes readings taken on another; the one it lands on is visibly wrong, which is the point.
    /// A chart with no curves at all keeps them in the document and draws none.
    /// </remarks>
    private static void RestoreMarkers(List<SmithTraceKey> keys, SmithDesign design)
    {
        if (keys.Count == 0) return;

        // EVERY TRACE STARTS EMPTY, and that line is not redundant. It used to be: the whole trace
        // set was rebuilt on each refill, so the markers went with the objects that carried them. An
        // OVERLAY is now the SAME Trace object from one rebuild to the next (R-smith12-5a — the
        // inspector's cards and the trace's own markers hold it), so its markers survive the refill
        // and re-attaching the document's would add a second copy of each on every keystroke.
        foreach (var (_, trace, _) in keys) trace.Markers.Clear();

        foreach (var stored in design.Markers)
        {
            var target = keys.FirstOrDefault(
                k => string.Equals(k.Key, stored.TraceName, StringComparison.Ordinal)).Trace
                ?? keys[0].Trace;

            target.Markers.Add(SmithMarkerBridge.ToMarker(stored, target));
        }
    }

    /// <summary>
    /// The markers currently on the chart, as the document stores them — the inverse of
    /// <see cref="RestoreMarkers"/>, and the only way a placement, a drag or a VSWR toggle reaches
    /// the `.csmith`.
    /// </summary>
    public static List<SmithMarker> HarvestMarkers(IReadOnlyList<SmithTraceKey> keys)
    {
        var markers = new List<SmithMarker>();
        foreach (var (key, trace, _) in keys)
            foreach (var m in trace.Markers)
                markers.Add(SmithMarkerBridge.FromMarker(m, key));
        return markers;
    }

    /// <summary>Grey — see <see cref="ReadingColor"/>.</summary>
    private const int ReadingColorIndex = 5;
    private const int ColorLUTGrey      = 5;

    /// <summary>The element's own name, or its kind and placement while it has none — a curve with
    /// no label is a curve nobody can name in the trace list.</summary>
    private static string ElementLabel(SmithElement element, int index)
        => string.IsNullOrWhiteSpace(element.Name)
               ? $"{element.Placement} {element.Kind} [{index}]"
               : element.Name;

    private static void AddPoints(
        Plot plot, List<SmithTraceKey> keys, string name, IEnumerable<Complex> points,
        int colorIndex, double size,
        bool annotation, bool excludeFromAutoscale, double opacity = 1.0,
        MarkerType markerType = MarkerType.Circle)
    {
        var list = points.ToArray();
        if (list.Length == 0) return;

        var props = Style(colorIndex, LineType.Solid, width: 1.0);
        props.LineEnabled   = false;     // points, not a path through them in table order
        props.MarkerEnabled = true;
        props.MarkerType    = markerType;
        props.MarkerSize    = size;
        props.MarkerOpacity = opacity;
        props.Custom        = false;

        var trace = CubeTrace(name, props);
        trace.IsAnnotation         = annotation;
        trace.ExcludeFromAutoscale = excludeFromAutoscale;
        SetGamma(trace, list);
        Add(plot, keys, name, trace);
    }

    /// <summary>
    /// Puts a Γ polyline onto a cube-bound trace.
    /// </summary>
    /// <remarks>
    /// The X array is the sample INDEX and is never read on a complex plot — <c>BuildCubePath</c>
    /// takes the complex values straight to <c>(Re, Im)</c> points there. It still has to be the
    /// same length as the values, because <c>CubeSampleCount</c> is the minimum of the two and a
    /// short X array silently truncates the curve.
    /// </remarks>
    private static void SetGamma(Trace trace, IReadOnlyList<Complex> gamma)
    {
        var x = new double[gamma.Count];
        var v = new Complex[gamma.Count];
        for (int i = 0; i < gamma.Count; i++) { x[i] = i; v[i] = gamma[i]; }

        trace.SetCubeData(x, v, null, "sample", null, PlotType.Smith, FreqUnit.GHz);
    }

    /// <summary>
    /// A cube-bound trace with nothing in it yet.
    /// </summary>
    /// <remarks>
    /// <b><c>Trace</c> has no constructor that does not take an <c>SNP</c></b>, and the cube path
    /// ignores it — so a one-point placeholder is what every synthetic trace in this repository is
    /// built on (<c>RailRfViewModel.CubeTrace</c>, <c>PlotInspectorViewModel</c> and
    /// <c>HarmonicaTracePicker</c> all do exactly this). Setting <c>CubeName</c> is what puts
    /// <c>BuildPath</c> on the cube branch.
    /// </remarks>
    private static Trace CubeTrace(string name, TraceProperties style)
        => new(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex,
               secondaryAxis: false, style)
        {
            CubeName   = name,
            Transform  = CubeTransform.None,
            Expression = name,

            // NOTHING THIS FILE DERIVES PUTS ITS NAME ON AN AXIS (owner report, 2026-09-19). Every
            // trace built here — the trajectories, the load points, the generator points, the band
            // and the constant-Q arcs — took a label column down the side of the chart and a
            // `freq (a to b)` row along the bottom, a dozen of each on an ordinary design, none of
            // which the user asked for by name. The OVERLAYS do not come through this factory: they
            // are the reference data the user chose, and they are what the axis labels are for.
            ExcludeFromAxisLabels = true,
        };

    /// <summary>The trajectories' ordinary line width, and the width the SELECTED element's takes.
    /// </summary>
    public const double ElementLineWidth         = 1.0;

    /// <inheritdoc cref="ElementLineWidth"/>
    public const double ElementLineWidthSelected = 2.75;

    /// <summary>What an UNSELECTED trajectory fades to while another one is selected.</summary>
    public const double ElementLineOpacityFaded  = 0.35;

    /// <summary>
    /// Draws the selected element's trajectory as the one being looked at: thicker, at full opacity,
    /// with the other trajectories faded back (owner instruction, 2026-09-19 — "clicking on a
    /// component needs to highlight the corresponding trace so the user can see which component
    /// contributes which trace").
    /// </summary>
    /// <remarks>
    /// <b>A mutation of the traces already on the plot, not a refill.</b> Selecting is not an edit:
    /// nothing about the design has changed, so re-evaluating the cascade and rebuilding the trace
    /// collection would re-attach every marker and reload every trace card to draw one line thicker.
    /// Only two properties move, and <c>−1</c> puts both of them back — which is what makes
    /// deselecting exactly as cheap, and exactly as complete, as selecting.
    ///
    /// <para><b>Only the trajectories.</b> The load points, the band, the constant-Q arcs and above
    /// all the user's own OVERLAYS are left alone: fading reference data because a component was
    /// clicked would hide the very thing the cascade is being matched to.</para>
    /// </remarks>
    public static void ApplyElementHighlight(IReadOnlyList<SmithTraceKey>? keys, int selectedElementIndex)
    {
        if (keys is null) return;

        bool anySelected = selectedElementIndex >= 0
                        && keys.Any(k => k.ElementIndex == selectedElementIndex);

        foreach (var k in keys)
        {
            if (k.ElementIndex < 0) continue;

            bool selected = k.ElementIndex == selectedElementIndex;

            k.Trace.Properties.LineWidth   = selected ? ElementLineWidthSelected : ElementLineWidth;
            k.Trace.Properties.LineOpacity = anySelected && !selected ? ElementLineOpacityFaded : 1.0;

            // LAST, for Style's own reason: every setter above raises Custom, and these traces are
            // rebuilt from the design on every edit rather than being a user's own styling. A trace
            // left marked Custom would have its palette colour frozen at whatever it happened to be.
            k.Trace.Properties.Custom = false;
        }
    }

    private static TraceProperties Style(int colorIndex, LineType type, double width)
    {
        var props = new TraceProperties
        {
            LineColorIndex   = colorIndex,
            MarkerColorIndex = colorIndex,
            FillColorIndex   = colorIndex,
            LineEnabled      = true,
            LineType         = type,
            LineWidth        = width,
        };
        // LAST, because every setter above raises it. These traces are rebuilt from the design on
        // every edit and are never a user's own styling, so they read as palette-default.
        props.Custom = false;
        return props;
    }
}
