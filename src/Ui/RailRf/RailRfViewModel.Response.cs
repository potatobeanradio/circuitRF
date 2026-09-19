// The |Z| plot — the curves, the mask and the aggressor lines
// (brief-railrf-12-impedance.md R-rail12-3; railrf.md §2.4 "Over frequency", §11.1).
//
// ── NO BESPOKE CHART, AND THAT IS THE WHOLE DESIGN ─────────────────────────────────────────────
//
// §11.1: "Plots are PlotControls in rectangular mode, FED FROM A DataSet — never a bespoke chart."
// So there is no drawing code in this file at all. The curve is an ordinary cube-bound Trace over
// PdnSweep's own Z cube, resolved through the same CubeTraceSpecParser the trace card and the
// `plot` verb use — which is what makes R-rail12-2's port-number convention one convention rather
// than two. The mask is a trace. The aggressors are traces. What this file decides is which traces
// exist and in what order, and nothing else.
//
// ── dB ON THE Y AXIS, AND THE REASON IS NOT COSMETIC ───────────────────────────────────────────
//
// §2.4 asks for log-log. This display's Y axis is linear only (Axes.XScale is the one that has a
// Log member), and a PDN curve spans four decades of ohms, so a linear Y renders the whole low band
// as a flat line on the floor. dB relative to one ohm is the same picture with the axis it can
// have — AND it is the axis every number in this brief is already quoted in: a mask margin, an
// anti-resonance's margin and a removal ranking are all decibels, so on this axis a margin is a
// distance a reader can measure off the picture rather than a number in a table beside it.
//
// ── THE AGGRESSOR LINES GO ON LAST, AND THAT IS LOAD-BEARING ───────────────────────────────────
//
// A vertical line is a two-point trace spanning the Y window, and this display has no per-trace
// autoscale exclusion — so a line added BEFORE the autoscale would set the Y range to its own
// endpoints and every curve would be squashed to nothing. They are therefore built after
// Autoscale, from the window the autoscale chose. Moving that call is the one edit to this file
// that produces a plot that looks broken for a reason nobody would guess.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// The plot host — one <see cref="DataDisplayViewModel"/> with exactly one container in it, laid
    /// out by this window rather than by a canvas.
    /// </summary>
    /// <remarks>
    /// <b>A bare <c>PlotControl</c> is not enough, and the Match Designer already paid for finding
    /// that out</b>: a <c>PlotControl</c> asks its HOST for the marker index, the info-box view model,
    /// the selected markers and the container to export, and a host that is null answers "nothing" to
    /// all four — silently. So the markers, the info boxes, the theme and the clipboard here are the
    /// Data Display's own, and this window only decides where the box sits.
    ///
    /// <para><b>It is not a Data Display document.</b> Nothing is persisted, no datasource library is
    /// loaded into it, and the plot cannot be added to or deleted.</para>
    /// </remarks>
    public DataDisplayViewModel PlotHost { get; } =
        new(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="ImpedancePlot"/>.</summary>
    public PlotContainerViewModel ImpedanceContainer { get; private set; } = null!;

    /// <summary>|Z| against frequency, with its mask and the aggressor lines.</summary>
    public Plot ImpedancePlot => ImpedanceContainer.PlotVM.Plot;

    /// <summary>Seed logical width of the plot — the view re-sizes it to the pane.</summary>
    public const double PlotWidth = 320.0;

    /// <summary>Seed logical height, at the golden ratio the Data Display's own new plots open at.</summary>
    public const double PlotHeight = PlotWidth / 1.618;

    private void BuildPlotHost()
    {
        ImpedanceContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.MHz,
                                              left: 0, top: 0, width: PlotWidth, height: PlotHeight);

        // AXES PANNING STARTS LOCKED, for the Match Designer's own reason: this plot is a read-out of
        // a design being edited underneath it, every committed edit re-solves and autoscales, and a
        // user who had dragged the window off the trace would see an empty plot and read it as a
        // broken design. The menu item still toggles it.
        ImpedancePlot.Axes.LockedPanning = true;

        // A PDN band is four decades (§2.2's own default is 10 kHz … 200 MHz) and a linear frequency
        // axis spends almost all of it in the top decade — the same reason RailBand is log-spaced.
        ImpedancePlot.Axes.XScale = AxisScale.Log;

        PlotHost.SelectOnly((PlotContainerViewModel?)null);
    }

    // ── the frequency answer, filed by model kind ──────────────────────────────────────────────

    /// <summary>
    /// The sweep, filed under the model kind that produced it.
    /// </summary>
    /// <remarks>
    /// <b>§2.9's fourth rule, and R-rail12-3 is the half that is visible</b>: running Accuracy keeps
    /// the fast curve on the plot beside the accurate one, so the error is measured on this design
    /// rather than promised in a document. Same shape as <see cref="ByModel"/>, and cleared by the
    /// same call for the same reason — a fast curve beside an accurate one from a different board is
    /// worse than no comparison.
    /// </remarks>
    public Dictionary<PdnModelKind, PdnSweepResult> SweepByModel { get; } = [];

    /// <summary>
    /// What actually runs a sweep. <see cref="PdnSweep.Run"/> in the application; injectable for the
    /// same two reasons <see cref="SolveFunc"/> is.
    /// </summary>
    internal Func<PdnSweepRequest, PdnSweepResult> SweepFunc { get; set; } = PdnSweep.Run;

    /// <summary>The sweep behind the numbers on screen, or null.</summary>
    public PdnSweepResult? Sweep =>
        Current is { } c && SweepByModel.TryGetValue(c.Kind, out var s) ? s : null;

    /// <summary>True once both readings' curves are on the plot — R-rail12-3's own condition.</summary>
    public bool HasBothImpedanceCurves =>
        SweepByModel.ContainsKey(PdnModelKind.Fast) && SweepByModel.ContainsKey(PdnModelKind.Accurate);

    /// <summary>
    /// What the frequency answer has to say that is not a curve: a refusal, or the notes and
    /// warnings that travel with the numbers (§9 — the indicative marking among them).
    /// </summary>
    public string ImpedanceMessage { get; private set; } = "";

    /// <summary>
    /// The request for one model kind, or null where this document cannot be swept yet.
    /// </summary>
    /// <remarks>
    /// <b>Built on the UI thread and run off it</b>, exactly as <see cref="BuildRequest"/> is: it
    /// reads the document's own rail, part and source rows, which the user is editing.
    /// </remarks>
    internal PdnSweepRequest? BuildSweepRequest(PdnModelKind kind)
    {
        if (SelectedRail is not { } rail || rail.Loads.Count == 0) return null;

        var resolver = new RailPartResolver(PartLibrary ?? new PartLibrary());

        var sources = new List<RailSourceModel>(rail.Sources.Count);
        for (int i = 0; i < rail.Sources.Count; i++)
        {
            var row = rail.Sources[i];
            // A source's own published output-impedance curve is "a part like any other" (Q-5), so it
            // is read through the resolver's own reader rather than a second one — see
            // RailPartResolver.ReadMeasured for why that matters.
            var measured = row.TouchstoneRef is { Length: > 0 } reference
                ? resolver.ReadMeasured(ResolveRelative(reference), out _)
                : null;
            sources.Add(RailSourceLife.Of(row, i, measured));
        }

        return new PdnSweepRequest
        {
            Rail    = rail,
            Parts   = resolver.ResolveAll(rail.Parts, rail.NominalVoltageV),
            Sources = sources,
            Model   = kind,
        };
    }

    /// <summary>A document-relative reference, against the <c>.crail</c>'s own folder.</summary>
    private string ResolveRelative(string reference) =>
        DocumentPath is { Length: > 0 } path && !System.IO.Path.IsPathRooted(reference)
            ? System.IO.Path.GetFullPath(
                  System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", reference))
            : reference;

    // ── the tables §2.4 asks for, as the docked list reads them ───────────────────────────────
    //
    //  Projected to strings HERE rather than bound through each record's own Describe() in the
    //  AXAML: Describe is a METHOD and a compiled binding cannot call one. Doing it here also means
    //  the window and a headless report print the same sentence, which is why Describe exists on
    //  the record at all — the same rule PortLines and BreakdownLines already follow.

    /// <summary>The mask verdict per observation port — the pass or the violations, with margins.</summary>
    public IReadOnlyList<string> MaskLines =>
        Sweep is { } s ? [.. s.Ports.Select(p => $"{p.Name}: {p.MaskReport.Describe()}")] : [];

    /// <summary>
    /// §2.4's short list — every anti-resonance within a stated fraction of an aggressor line, worst
    /// first. <b>The sentence the tool exists to produce</b>, so it is a list of its own rather than
    /// a column of the anti-resonance table.
    /// </summary>
    public IReadOnlyList<string> CoincidenceLines =>
        Sweep is { } s ? [.. s.Coincidences.Select(c => c.Describe())] : [];

    /// <summary>§2.4's anti-resonance table, every row naming its two contributors.</summary>
    public IReadOnlyList<string> AntiResonanceLines =>
        Sweep is { } s
            ? [.. s.Ports.SelectMany(p => p.Peaks)
                         .OrderByDescending(p => p.PeakOhms)
                         .Select(p => p.Describe())]
            : [];

    /// <summary>§2.4's capacitor ranking — how much the worst violation grows without each part.</summary>
    public IReadOnlyList<string> RemovalLines =>
        Sweep is { } s ? [.. s.Removal.Select(r => r.Describe())] : [];

    /// <summary>True while the coincidence list has a row. Bound rather than a count, because
    /// Avalonia's default converter does not turn an <c>int</c> into a visibility.</summary>
    public bool HasCoincidences => CoincidenceLines.Count > 0;

    /// <inheritdoc cref="HasCoincidences"/>
    public bool HasMaskVerdict => MaskLines.Count > 0;

    /// <inheritdoc cref="HasCoincidences"/>
    public bool HasAntiResonances => AntiResonanceLines.Count > 0;

    /// <inheritdoc cref="HasCoincidences"/>
    public bool HasRemovalRanking => RemovalLines.Count > 0;

    /// <summary>Files one finished sweep and rebuilds the plot. Called from the solve's own finish.</summary>
    private void AcceptSweep(PdnModelKind kind, PdnSweepResult? sweep)
    {
        if (sweep is { Refusal: null }) SweepByModel[kind] = sweep;

        ImpedanceMessage =
            sweep is null ? ""
            : sweep.Refusal is { } why ? why
            : string.Join(" ", sweep.Warnings.Concat(sweep.Notes));

        AnnounceSweepChanged();
    }

    private void ClearSweeps()
    {
        SweepByModel.Clear();
        ImpedanceMessage = "";
        AnnounceSweepChanged();
    }

    private void AnnounceSweepChanged()
    {
        OnPropertyChanged(nameof(Sweep));
        OnPropertyChanged(nameof(HasBothImpedanceCurves));
        OnPropertyChanged(nameof(ImpedanceMessage));
        OnPropertyChanged(nameof(HasImpedanceMessage));
        OnPropertyChanged(nameof(MaskLines));
        OnPropertyChanged(nameof(CoincidenceLines));
        OnPropertyChanged(nameof(AntiResonanceLines));
        OnPropertyChanged(nameof(RemovalLines));
        OnPropertyChanged(nameof(HasMaskVerdict));
        OnPropertyChanged(nameof(HasCoincidences));
        OnPropertyChanged(nameof(HasAntiResonances));
        OnPropertyChanged(nameof(HasRemovalRanking));
        RebuildImpedancePlot();
    }

    /// <summary>True while there is something to say beside the curve.</summary>
    public bool HasImpedanceMessage => ImpedanceMessage.Length > 0;

    // ── the traces ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds every trace on the impedance plot: the mask, one |Z| curve per observation port per
    /// model kind, and one line per aggressor harmonic.
    /// </summary>
    public void RebuildImpedancePlot()
    {
        var plot = ImpedancePlot;
        plot.Traces.Clear();

        // Fast first so an Accuracy run lands ON TOP of the curve it is being compared with, rather
        // than under it — §2.9's fourth rule is about reading the two together.
        var kinds = new[] { PdnModelKind.Fast, PdnModelKind.Accurate }
            .Where(SweepByModel.ContainsKey).ToArray();

        if (kinds.Length == 0)
        {
            AnnounceRebuiltPlot();
            return;
        }

        var primary = SweepByModel[kinds[^1]];
        int colour = 0;

        // The mask FIRST, so it sits behind the curves it is judging.
        foreach (var port in primary.Ports)
            if (port.Mask is { } mask && MaskTrace(mask, port.Name) is { } masked)
                plot.Traces.Add(masked);

        foreach (var kind in kinds)
        {
            var sweep = SweepByModel[kind];
            if (sweep.Data is not { } data) continue;

            foreach (var port in sweep.Ports)
                if (CurveTrace(data, port, kind, colour++) is { } curve)
                    plot.Traces.Add(curve);
        }

        plot.CustomTitleOn = true;
        plot.CustomTitle = kinds.Length > 1
            ? $"|Z| at each observation port — {ModelName(kinds[0])} and {ModelName(kinds[^1])}"
            : $"|Z| at each observation port — {ModelName(kinds[0])}";

        plot.Autoscale(force: true);

        // AFTER the autoscale. See this file's own header.
        foreach (var line in AggressorTraces(primary, plot)) plot.Traces.Add(line);

        AnnounceRebuiltPlot();
    }

    private static string ModelName(PdnModelKind kind) =>
        kind == PdnModelKind.Fast ? "Fast model" : "Accuracy";

    /// <summary>
    /// One port's |Z| in dBΩ, as an ordinary cube-bound trace over the sweep's own Z cube.
    /// </summary>
    /// <remarks>
    /// <b>The spec is authored as text and resolved by <see cref="CubeTraceSpecParser"/></b> rather
    /// than by filling an <c>AxisSlice[]</c> here. That parser is what the trace card and the
    /// <c>plot</c> verb both use, and it is where R-rail12-2's rule lives: an integer on an
    /// <c>i</c>/<c>j</c> axis is a 1-BASED PORT NUMBER. Building the slice by hand here would be a
    /// second place that convention is decided, and the two would differ by one silently — which on
    /// a reciprocal Z matrix draws a curve that is not wrong-looking at all.
    /// </remarks>
    private static Trace? CurveTrace(DataSet data, PdnPortImpedance port, PdnModelKind kind, int colour)
    {
        int number = port.Index + 1;
        string spec = string.Create(CultureInfo.InvariantCulture, $"dB(Z[:, {number}, {number}])");

        if (!CubeTraceSpecParser.TryParse(spec, data, out string cubeName, out var slice,
                                          out var transform, out _))
            return null;

        // Fast dashed, Accurate solid — so when both are on the plot (§2.9 rule 4) which is which
        // reads off the picture rather than off the legend.
        var trace = CubeTrace(cubeName, slice, transform, spec,
                              Style(colour, kind == PdnModelKind.Accurate
                                                ? LineType.Solid : LineType.Dashed, width: 1.0));
        TraceResolve.SetCubeDataFrom(trace, data, PlotType.Rect, FreqUnit.MHz);
        return trace.Points.Count > 0 ? trace : null;
    }

    /// <summary>
    /// The target, as a trace of its own stated points in dBΩ.
    /// </summary>
    /// <remarks>
    /// §2.4 draws the mask as a shaded ceiling; what is drawn here is its EDGE, because that is the
    /// part the Data Display already renders and §11.1 forbids a bespoke chart to get the shading.
    /// It carries the mask's own points and not the sweep's grid, so a two-point flat target is two
    /// points — a ceiling resampled onto 201 frequencies would read as data.
    /// </remarks>
    private static Trace? MaskTrace(PdnMask mask, string portName)
    {
        var x = mask.Points.Select(p => p.FrequencyHz).ToArray();
        var y = mask.Points.Select(p => 20.0 * Math.Log10(p.LimitOhms)).ToArray();
        if (x.Length < 2) return null;

        var trace = CubeTrace($"target({portName})", slice: null, CubeTransform.None,
                              $"target({portName})", Style(2, LineType.Dashed, width: 1.0));
        trace.SetCubeData(x, null, y, "freq", "Hz", PlotType.Rect, FreqUnit.MHz);
        return trace;
    }

    /// <summary>
    /// One vertical line per aggressor harmonic, spanning the Y window the autoscale chose.
    /// </summary>
    /// <remarks>
    /// §2.2's own reason for the whole feature: <i>"a 9 dB peak nothing excites is not a problem and
    /// a 3 dB peak sitting on the converter's fifth harmonic is."</i> The fundamental is drawn at
    /// full weight and the harmonics lighter, which is §2.4's own description and is the difference
    /// between a plot that says which line a peak landed on and one that says only that some line is
    /// near it.
    /// </remarks>
    private static List<Trace> AggressorTraces(PdnSweepResult sweep, Plot plot)
    {
        var lines = new List<Trace>();
        var rail = sweep.Ports.Count > 0 ? sweep : null;
        if (rail is null) return lines;

        double lo = Math.Min(plot.Axes.Window.Top, plot.Axes.Window.Bottom);
        double hi = Math.Max(plot.Axes.Window.Top, plot.Axes.Window.Bottom);
        if (!(hi > lo)) return lines;

        foreach (var (name, hz, harmonic) in sweep.AggressorLines)
        {
            if (hz < sweep.FrequenciesHz[0] || hz > sweep.FrequenciesHz[^1]) continue;

            // The fundamental at full weight, its harmonics lighter — §2.4's own description, and
            // the difference between a plot that says WHICH line a peak landed on and one that says
            // only that some line is near it.
            var trace = CubeTrace($"{name} × {harmonic}", slice: null, CubeTransform.None,
                                  $"{name} × {harmonic}",
                                  Style(3, LineType.Solid, harmonic == 1 ? 1.0 : 0.5));
            trace.SetCubeData([hz, hz], null, [lo, hi], "freq", "Hz", PlotType.Rect, FreqUnit.MHz);
            lines.Add(trace);
        }

        return lines;
    }

    /// <summary>
    /// A cube-bound trace with nothing in it yet.
    /// </summary>
    /// <remarks>
    /// <b><c>Trace</c> has no constructor that does not take an <c>SNP</c></b>, and the cube path
    /// ignores it — so a one-point placeholder is what every cube-bound trace in this repository is
    /// built on (<c>PlotInspectorViewModel</c> and <c>HarmonicaTracePicker</c> both do exactly
    /// this). Setting <c>CubeName</c> is what puts <c>BuildPath</c> on the cube branch, which is the
    /// branch that survives a frequency-unit change.
    /// </remarks>
    private static Trace CubeTrace(
        string cubeName, AxisSlice[]? slice, CubeTransform transform, string expression,
        TraceProperties style)
    {
        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real,
                              secondaryAxis: false, style)
        {
            CubeName   = cubeName,
            Slice      = slice,
            Transform  = transform,
            Expression = expression,
        };
        return trace;
    }

    /// <summary>
    /// The palette, by INDEX into the application's own order rather than by naming a colour — the
    /// Match Designer's own finding: a change to the palette then moves this window with it.
    /// </summary>
    private static TraceProperties Style(int order, LineType type, double width)
    {
        int index = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        var props = new TraceProperties
        {
            LineColorIndex   = index,
            MarkerColorIndex = index,
            FillColorIndex   = index,
            LineEnabled      = true,
            LineType         = type,
            LineWidth        = width,
        };
        // LAST, because every setter above raises it. These traces are rebuilt from the result on
        // every solve and are never a user's own styling, so they read as palette-default.
        props.Custom = false;
        return props;
    }

    /// <summary>
    /// Tells the host the plot has been rebuilt: the info boxes, the binding and the repaint.
    /// </summary>
    /// <remarks>
    /// All three, for the Match Designer's own reason — <c>OnPlotChanged</c> drops the info boxes
    /// whose markers are gone, the property change re-pushes the (unchanged) plot reference through
    /// the binding, and <c>RequestPlotRedraw</c> is the only one of the three that is actually a
    /// repaint. Without the last one the control redraws when something else happens to invalidate
    /// it, which reads as "sometimes it does not update".
    /// </remarks>
    private void AnnounceRebuiltPlot()
    {
        ImpedanceContainer.OnPlotChanged(this, EventArgs.Empty);
        OnPropertyChanged(nameof(ImpedancePlot));
        ImpedanceContainer.RequestPlotRedraw();
    }
}
