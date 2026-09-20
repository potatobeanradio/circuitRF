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
// ── WHAT A MARKER MAY TOUCH: THE CURVES, AND NOTHING ELSE ─────────────────────────────────────
//
// Of the thirteen traces this plot carries on the shipped example, two are |Z| curves and the rest
// are the mask edge and the aggressor lines — annotation, drawn as traces only because §11.1 forbids
// a bespoke chart. They carry TWO points, not the sweep's 201, and the owner reported both halves of
// what that does (2026-09-19): a multi-marker reads every other trace at its own X SAMPLE, so at
// index 137 each of the eleven printed "NaN"; and the Add Marker submenu lists one row per trace, so
// the same eleven were offered as places to put a marker and buried the two that answer anything.
//
// So each is marked Trace.IsAnnotation, which is the Data Display's own word for "drawn like a
// trace, not treated as one by any marker".
//
// Marked at the TRACE rather than filtered at each caller: an info box is built by
// Trace.BuildMarkerBoxLines, which the renderer MEASURES with and then DRAWS with, so a filter
// applied at only one of the two gives a box the wrong size for its contents — and the menu, the
// double-click and the readout would each need their own copy of the same rule.

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
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// Removes every SELECTED marker from its trace, as one undoable step on the plot host's own
    /// stack — what the Delete key does on a Data Display canvas.
    /// </summary>
    /// <remarks>
    /// <b>The window had no Delete at all</b> (owner, 2026-09-19): a marker could be added by
    /// double-clicking the plot and selected by clicking its info box, and then the only way to be
    /// rid of it was the info box's own close. Every other plot surface in the application answers
    /// the key, so on this one it read as broken rather than absent.
    ///
    /// <para><b>Deliberately not <c>DataDisplayViewModel.DeleteSelected</c></b> — the Match
    /// Designer's own reason, which holds here identically. That method also removes selected PLOT
    /// CONTAINERS, and this window's one plot is not deletable: there is nothing to delete it from
    /// and its traces are rebuilt from the document on every solve. A shared gesture that could
    /// silently take the plot with the marker would be worse than no gesture.</para>
    ///
    /// <para><b>Delete only, not Backspace</b>, for the reason the Match Designer's AXAML states:
    /// this window's left column is <c>InlineEditText</c> rows, which are focusable at rest without
    /// being text fields, and a Backspace landing on one would remove a marker the user was not
    /// looking at.</para>
    /// </remarks>
    [RelayCommand]
    public void DeleteSelectedMarkers()
    {
        foreach (var box in PlotHost.MarkerInfoBoxes.Where(b => b.IsSelected).ToList())
            box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);

        // Removal goes through the plot host's undo stack, whose StateChanged raises ContentChanged
        // — so this is belt-and-braces rather than the only route. It is here because a Delete that
        // did not mark the document is the one case where the omission loses a reading rather than
        // an addition, and that is worth not depending on another class's event ordering.
        CaptureMarkers();
    }

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

        // ── THE TRACE SET IS THIS WINDOW'S (owner, 2026-09-19) ────────────────────────────────
        //
        // The Plot Inspector opens on this plot now, so the Y unit can be changed — but every trace
        // here is rebuilt from the sweep on each re-solve, which happens on every committed edit.
        // A trace added in the panel would be gone by the next keystroke and one removed would be
        // back, so the Add and the trash are hidden rather than offered and then undone.
        ImpedancePlot.IsFixedReadout = true;

        // AND ITS DATA IS THIS WINDOW'S TOO, which is the part that does not work by default: the
        // inspector re-resolves every cube trace from its SOURCE on each edit, and the ordinary
        // source is the data-source library — files on disk. railRF has none. Without this the first
        // touch of the panel emptied every curve, 425 points to 0, silently. See RailPlotDataSources.
        ImpedanceContainer.Inspector.SetDataSources(new RailPlotDataSources(SweepByModel));

        // What the user picked in the panel has to reach the MASK and the aggressor lines, which are
        // not cards and cannot be changed there — R-rail12-3's own rule, and the owner's: the target
        // traces must adapt to any scale the impedance traces are given.
        ImpedanceContainer.Inspector.PlotNeedsRedraw += OnInspectorChangedThePlot;

        // AXES PANNING STARTS LOCKED, for the Match Designer's own reason: this plot is a read-out of
        // a design being edited underneath it, every committed edit re-solves and autoscales, and a
        // user who had dragged the window off the trace would see an empty plot and read it as a
        // broken design. The menu item still toggles it.
        ImpedancePlot.Axes.LockedPanning = true;

        // A PDN band is four decades (§2.2's own default is 10 kHz … 200 MHz) and a linear frequency
        // axis spends almost all of it in the top decade — the same reason RailBand is log-spaced.
        ImpedancePlot.Axes.XScale = AxisScale.Log;

        PlotHost.SelectOnly((PlotContainerViewModel?)null);

        // MARKERS ARE DOCUMENT STATE, and this is the channel they are noticed on — the same one a
        // `.cdd` document's own dirty check runs off, so an info-box drag and a rename are seen as
        // well as an add and a move. See RailRfViewModel.Markers.cs.
        PlotHost.ContentChanged += (_, _) => CaptureMarkers();
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
            Parts   = resolver.ResolveAll(rail.Parts, rail.NominalVoltageV, ComputedMounting(rail)),
            Sources = sources,
            Model   = kind,

            // The BOARD's units, exactly as BuildRequest passes them to the DC run (owner,
            // 2026-09-19). Without it every port this sweep names — the mask verdict's rows, the
            // plot's own trace labels, the coincidence table — took PdnSweepRequest's default and
            // printed a coordinate anchor as a bare DBU integer, which is the one spelling the whole
            // of RailLengthFormat exists to stop. The DC side passed it and the frequency side did
            // not, so the two halves of one window disagreed about the same port.
            LengthFormat = BoardLengthFormat(),

            // ── R-rail14-4: ON, because §4.4 says it is not optional ───────────────────────────
            //
            // "Plane resonances are narrow and a log grid steps straight over one." The anti-
            // resonance table, the mask verdict and the coincidence rows are all read off this
            // curve's own axis, so a peak the grid stepped over is missing from three answers at
            // once and the window has nothing to show that it is. Every point the search adds is
            // solved and is reported in the result's notes, which is where the strip reads them
            // from; a PDN point is one sparse complex solve, so the budget costs milliseconds.
            Sampling = PdnSamplingSettings.Default,
        };
    }

    /// <summary>
    /// Brief 13's mounting loop, per part, from the board's own via geometry — or null where there
    /// is no board to read it from.
    /// </summary>
    /// <remarks>
    /// <b>§6 makes P1 artwork-OPTIONAL and §2.2 says a computed value is a DEFAULT, not a fact.</b>
    /// So this fills in only the rows nobody typed — <c>RailPartResolver</c> is what applies that
    /// precedence, and <c>RailMountingBasis</c> is what tells a reader on the parts table which they
    /// are looking at. A part whose via geometry could not be read is simply absent from this map
    /// and keeps whatever P1 gave it, because a part that cannot be located is not a part with a
    /// zero mounting loop.
    /// </remarks>
    private Dictionary<string, double>? ComputedMounting(RailSpec rail)
    {
        if (Board is not { } board) return null;

        var request = new PdnMountingLoopRequest
        {
            Rail = rail,
            Shapes = board.Shapes,
            Technology = board.Technology,
            DbuPerMicron = board.DbuPerMicron,
            Pads = board.Pads,
            ReferenceNet = board.ReferenceNet,
        };

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var loop in PdnMountingLoopExtractor.ComputeAll(
                     request, rail.Parts.Select(p => p.Refdes).Where(r => r.Length > 0)))
            if (loop.Henries is { } henries && henries > 0)
                map[loop.Refdes] = henries;

        return map.Count > 0 ? map : null;
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
    //  the record at all — the same rule PortLines and BreakdownRows already follow.

    /// <summary>The mask verdict per observation port — the pass or the violations, with margins.</summary>
    public IReadOnlyList<string> MaskLines =>
        Sweep is { } s
            ? [.. s.Ports.Select(p => $"{p.NameIn(BoardLengthFormat())}: {p.MaskReport.Describe()}")]
            : [];

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
        AnnounceCardVisibility();
        RebuildImpedancePlot();
    }

    /// <summary>True while there is something to say beside the curve.</summary>
    public bool HasImpedanceMessage => ImpedanceMessage.Length > 0;

    // ── the traces ────────────────────────────────────────────────────────────────────────────

    // ── THE Y UNIT, AND WHO OWNS IT ───────────────────────────────────────────────────────────

    /// <summary>
    /// What the curves are drawn in — <b>one unit for the whole plot</b>.
    /// </summary>
    /// <remarks>
    /// <b>dBΩ by default, and dB20 is not the same as dB</b> (owner, 2026-09-19). The curve was
    /// authored as <c>dB(Z[…])</c>, and <c>dB</c> in this vocabulary is <c>10·log₁₀</c> — a POWER
    /// decibel — while the mask edge beside it was built with <c>20·log₁₀</c>. Measured on the
    /// shipped fixture: a 200 mΩ target drew at −13.979 and a curve at 0.311 Ω drew at −5.076
    /// rather than −10.152, so a design sitting exactly on its ceiling read as seven decibels clear
    /// of it. The mask VERDICT is computed in ohms by <c>PdnSweep</c> and was always right; it was
    /// the picture that disagreed with it. |Z| in dBΩ is 20·log₁₀.
    ///
    /// <para><b>Plot-wide rather than per-trace.</b> The user changes it on one card; every curve
    /// and every mask adopts it. A plot showing one reading in dBΩ and the other in ohms would put
    /// two quantities on one axis under one label.</para>
    /// </remarks>
    internal CubeTransform ImpedanceTransform { get; private set; } = CubeTransform.dB20;

    /// <summary>The curves of the last rebuild, by (reading, port) — what carries a user's own
    /// edits across the next one. See <see cref="RebuildImpedancePlot"/>.</summary>
    private readonly Dictionary<string, Trace> _curves = new(StringComparer.Ordinal);

    /// <summary>The aggressor lines of the last rebuild — re-spanned whenever the window moves.</summary>
    private readonly List<Trace> _aggressorLines = [];

    private static string CurveKey(PdnModelKind kind, int portIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}|{portIndex}");

    /// <summary>What the axis is called, in whatever unit the curves are currently in.</summary>
    private string ImpedanceYLabel => ImpedanceTransform switch
    {
        CubeTransform.dB20              => "|Z| (dBΩ)",
        CubeTransform.dB10 or CubeTransform.dB => "|Z| (dB, 10·log₁₀)",
        CubeTransform.Real              => "Re(Z) (Ω)",
        CubeTransform.Imag              => "Im(Z) (Ω)",
        CubeTransform.Phase             => "∠Z (°)",
        _                               => "|Z| (Ω)",
    };

    /// <summary>
    /// The user changed something in the Plot Inspector. If it was the Y unit, everything that is
    /// not a card follows it.
    /// </summary>
    /// <remarks>
    /// <b>The mask and the aggressor lines have no cards</b> (they are annotation — see
    /// <c>Trace.IsAnnotation</c>), so the panel cannot carry a change to them and this is what does.
    /// Read off the curves rather than handed in, because the inspector's own contract is that it
    /// writes the trace and says the plot needs redrawing; it has no notion of a plot-wide unit.
    /// </remarks>
    private void OnInspectorChangedThePlot(object? sender, EventArgs e)
    {
        if (_curves.Count == 0) return;

        var picked = _curves.Values.First().Transform;
        if (picked == ImpedanceTransform) { RespanAggressors(); return; }

        ImpedanceTransform = picked;
        ApplyImpedanceUnit();
    }

    /// <summary>
    /// Puts <see cref="ImpedanceTransform"/> on every curve and every mask, re-labels the axis and
    /// re-frames the plot.
    /// </summary>
    private void ApplyImpedanceUnit()
    {
        var plot = ImpedancePlot;

        foreach (var curve in _curves.Values)
        {
            curve.SetDisplayTransform(ImpedanceTransform);
            curve.BuildPath(PlotType.Rect, FreqUnit.MHz);
        }

        // The mask carries OHMS and takes the same transform, which is the whole reason it is stored
        // raw — see MaskTrace. A mask is a magnitude ceiling and has no phase, so on a phase axis it
        // is removed rather than drawn somewhere meaningless.
        foreach (var mask in plot.Traces.Where(t => t.IsAnnotation && !t.ExcludeFromAutoscale).ToList())
        {
            if (ImpedanceTransform == CubeTransform.Phase) { plot.Traces.Remove(mask); continue; }
            mask.Transform = ImpedanceTransform;
            mask.BuildPath(PlotType.Rect, FreqUnit.MHz);
        }

        plot.CustomYLabelOn = true;
        plot.CustomYLabel   = ImpedanceYLabel;
        plot.SetAxesViewport();
        plot.Autoscale(force: true);

        RespanAggressors();
        AnnounceRebuiltPlot();
    }

    /// <summary>
    /// Re-draws each aggressor line to span the CURRENT Y window.
    /// </summary>
    /// <remarks>
    /// A vertical line is a two-point trace from the bottom of the window to the top, so it has to
    /// be re-cut whenever the window moves — and it is marked
    /// <see cref="Trace.ExcludeFromAutoscale"/> so that it never moves the window itself, which is
    /// what stops the two chasing each other.
    /// </remarks>
    private void RespanAggressors()
    {
        if (_aggressorLines.Count == 0) return;

        var window = ImpedancePlot.Axes.Window;
        double lo = Math.Min(window.Top, window.Bottom);
        double hi = Math.Max(window.Top, window.Bottom);
        if (!(hi > lo)) return;

        foreach (var line in _aggressorLines)
        {
            if (line.CubeXValues is not { Count: > 0 } xs) continue;
            double hz = xs[0];
            line.SetCubeData([hz, hz], null, [lo, hi], "freq", "Hz", PlotType.Rect, FreqUnit.MHz,
                             transformBaked: true);
        }
    }

    // ── the traces ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds every trace on the impedance plot: the mask, one |Z| curve per observation port per
    /// model kind, and one line per aggressor harmonic.
    /// </summary>
    /// <remarks>
    /// <b>It runs on every solve, which is every committed edit — so it must not throw away what the
    /// user did in the Plot Inspector.</b> Before this, a Y unit picked in the panel, a colour
    /// chosen on a card and every marker on the plot lasted exactly until the next keystroke in the
    /// specification column, because the whole trace list is replaced here. Curves are therefore
    /// matched across the rebuild on a key that survives it — the READING and the PORT, which is
    /// what a curve IS — and their transform, their custom styling and their markers come with them.
    /// </remarks>
    public void RebuildImpedancePlot()
    {
        var plot = ImpedancePlot;

        var previous = new Dictionary<string, Trace>(_curves, StringComparer.Ordinal);
        _curves.Clear();
        _aggressorLines.Clear();
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

        // The unit the user is in, carried from the curves that were on the plot a moment ago.
        if (previous.Count > 0) ImpedanceTransform = previous.Values.First().Transform;

        var primary = SweepByModel[kinds[^1]];
        int colour = 0;

        // THE CURVES ARE BUILT FIRST and added second: the mask has to be drawn in the unit they are
        // in, and it has to sit BEHIND them.
        var curves = new List<Trace>();
        foreach (var kind in kinds)
        {
            var sweep = SweepByModel[kind];
            if (sweep.Data is not { } data) continue;

            foreach (var port in sweep.Ports)
            {
                if (CurveTrace(data, port, kind, colour++) is not { } curve) continue;

                string key = CurveKey(kind, port.Index);
                if (previous.TryGetValue(key, out var was)) Carry(was, curve);

                _curves[key] = curve;
                curves.Add(curve);
            }
        }

        foreach (var port in primary.Ports)
            if (port.Mask is { } mask && MaskTrace(mask, port.Name) is { } masked)
                plot.Traces.Add(masked);

        foreach (var curve in curves) plot.Traces.Add(curve);

        // ── THE TITLE NAMES THE PICTURE AND NOTHING ELSE (owner, 2026-09-19) ──────────────────
        //
        // It used to carry the model kinds and, below, what the vertical lines were. Both are
        // already on screen — the status strip states the model kind on every frame, and "both
        // models in hand" sits beside it — so the title was a third copy of one of them growing a
        // clause at a time, on the narrowest panel in the window.
        plot.CustomTitleOn = true;
        plot.CustomTitle   = "|Z| over frequency";

        // ── ONE Y LABEL, not one per trace (owner, 2026-09-19) ────────────────────────────────
        //
        // A Rect plot with no custom Y label draws a ROTATED LABEL COLUMN PER LEFT-AXIS TRACE in
        // the Skia margin (AxesRenderer.DrawTitleAndAxisLabels), which is the right default when a
        // plot holds three curves of different quantities. This plot holds one quantity and as many
        // traces as the design has ports, models, masks and aggressor harmonics — thirteen on the
        // shipped example — so the column stack took most of the panel's width and left the curves
        // in a sliver. Every trace here is |Z| in the SAME unit, so the honest label is one label,
        // and it names whichever unit that is.
        plot.CustomYLabelOn = true;
        plot.CustomYLabel   = ImpedanceYLabel;

        // AND THE VIEWPORT HAS TO BE RECOMPUTED FOR IT. Plot.SetAxesViewport sizes the left margin
        // from the number of label COLUMNS, which a plot-wide Y label makes one — but it runs off
        // the Traces collection changing, and the label is set after the last add. Without this the
        // margin keeps the width it was given while the label column stack was still thirteen deep.
        plot.SetAxesViewport();

        plot.Autoscale(force: true);

        // AFTER the autoscale, because each line is cut TO the window. They are also excluded from
        // it (Trace.ExcludeFromAutoscale) so that a later autoscale — the Plot Inspector performs
        // one on every edit — cannot read last frame's window back as this frame's data.
        _aggressorLines.AddRange(AggressorTraces(primary, plot));
        foreach (var line in _aggressorLines) plot.Traces.Add(line);

        // The panel shows one card per trace and syncs itself only when a trace arrives THROUGH it,
        // which none of these did — so without this it opens on the cards of traces that no longer
        // exist, or, as it did, on none at all.
        ImpedanceContainer.Inspector.ReloadTraceCards();

        // THE DOCUMENT'S OWN MARKERS, once per CURVE — the first rebuild in which that curve exists.
        // After that `Carry` above is what moves the live objects across a re-solve, and the document
        // is written back from them. Per curve rather than per plot because the accurate reading
        // arrives later than the fast one; see RailRfViewModel.Markers.cs.
        RestoreMarkers();

        AnnounceRebuiltPlot();
    }

    /// <summary>
    /// Moves what the user chose from the curve that is going away onto the one replacing it.
    /// </summary>
    /// <remarks>
    /// <b>Styling comes over only when it is the USER's.</b> <c>TraceProperties.Custom</c> is
    /// already the flag for that — <see cref="Style"/> clears it on every trace this file builds —
    /// so a palette default stays a palette default and follows a change to the palette, while a
    /// colour somebody picked survives the next re-solve.
    ///
    /// <para><b>The markers come over as OBJECTS</b>, not as copies: the info boxes are rebuilt from
    /// the plot's own traces by <c>OnPlotChanged</c>, and a marker rebuilt as an equal-but-different
    /// object would lose its selection, its box position and its m-number.</para>
    /// </remarks>
    private static void Carry(Trace was, Trace now)
    {
        if (was.Properties.Custom) now.Properties = was.Properties;

        foreach (var marker in was.Markers) now.Markers.Add(marker);
    }

    /// <summary>
    /// One port's |Z| as an ordinary cube-bound trace over the sweep's own Z cube, in whatever unit
    /// <see cref="ImpedanceTransform"/> currently names.
    /// </summary>
    /// <remarks>
    /// <b>The spec is authored as text and resolved by <see cref="CubeTraceSpecParser"/></b> rather
    /// than by filling an <c>AxisSlice[]</c> here. That parser is what the trace card and the
    /// <c>plot</c> verb both use, and it is where R-rail12-2's rule lives: an integer on an
    /// <c>i</c>/<c>j</c> axis is a 1-BASED PORT NUMBER. Building the slice by hand here would be a
    /// second place that convention is decided, and the two would differ by one silently — which on
    /// a reciprocal Z matrix draws a curve that is not wrong-looking at all.
    ///
    /// <para><b>The spec carries NO transform.</b> It used to read <c>dB(Z[…])</c>, which put the
    /// unit in the expression TEXT — where the trace card cannot reach it, and where it silently
    /// disagreed with the mask beside it. See <see cref="ImpedanceTransform"/>.</para>
    /// </remarks>
    private Trace? CurveTrace(DataSet data, PdnPortImpedance port, PdnModelKind kind, int colour)
    {
        int number = port.Index + 1;
        string spec = string.Create(CultureInfo.InvariantCulture, $"Z[:, {number}, {number}]");

        if (!CubeTraceSpecParser.TryParse(spec, data, out string cubeName, out var slice,
                                          out var transform, out _))
            return null;

        // Fast dashed, Accurate solid — so when both are on the plot (§2.9 rule 4) which is which
        // reads off the picture rather than off the legend.
        var trace = CubeTrace(cubeName, slice, transform, spec,
                              Style(colour, kind == PdnModelKind.Accurate
                                                ? LineType.Solid : LineType.Dashed, width: 1.0));

        // WHICH READING this curve is of. The Plot Inspector re-resolves a cube trace from its
        // source on every edit, and this is what it resolves against — see RailPlotDataSources.
        trace.SourcePath = RailPlotDataSources.PathFor(kind);

        trace.SetDisplayTransform(ImpedanceTransform);
        TraceResolve.SetCubeDataFrom(trace, data, PlotType.Rect, FreqUnit.MHz);
        return trace.Points.Count > 0 ? trace : null;
    }

    /// <summary>
    /// The target, as a trace of its own stated points — <b>in OHMS</b>.
    /// </summary>
    /// <remarks>
    /// §2.4 draws the mask as a shaded ceiling; what is drawn here is its EDGE, because that is the
    /// part the Data Display already renders and §11.1 forbids a bespoke chart to get the shading.
    /// It carries the mask's own points and not the sweep's grid, so a two-point flat target is two
    /// points — a ceiling resampled onto 201 frequencies would read as data.
    ///
    /// <para><b>The values are RAW OHMS and the unit is the trace's <c>Transform</c>, which is the
    /// whole of how a ceiling follows the curve it is judging</b> (owner, 2026-09-19: the target
    /// traces must adapt to any scale the impedance traces are given). It used to be stored with
    /// <c>20·log₁₀</c> already applied, so it could only ever be read in one unit — and, worse, in a
    /// DIFFERENT one from the curve beside it. A real-valued cube trace applies its transform at
    /// path-build time (<c>Trace.BuildCubePath</c>), so changing it and rebuilding is all this
    /// takes.</para>
    /// </remarks>
    private Trace? MaskTrace(PdnMask mask, string portName)
    {
        // A ceiling on a magnitude has no phase to be drawn at.
        if (ImpedanceTransform == CubeTransform.Phase) return null;

        var x = mask.Points.Select(p => p.FrequencyHz).ToArray();
        var y = mask.Points.Select(p => p.LimitOhms).ToArray();
        if (x.Length < 2) return null;

        var trace = CubeTrace($"target({portName})", slice: null, ImpedanceTransform,
                              $"target({portName})", Style(2, LineType.Dashed, width: 1.0));

        // NOT DATA — see the note in this file's header. It still FRAMES the plot, which is why it
        // is not also excluded from the autoscale: a ceiling off the top of the window is a verdict
        // the reader cannot see.
        trace.IsAnnotation = true;
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
            // DASHED AND LIGHT, never a solid full-weight stroke: a solid vertical line the same
            // weight as a curve reads as data that went off the scale, which is exactly how it was
            // read (owner, 2026-09-19). LineType has two members here, so dashed is the whole of
            // the vocabulary; the weight carries the rest, and the fundamental is still the heavier
            // of the two so which line a peak landed on stays readable.
            var trace = CubeTrace($"{name} × {harmonic}", slice: null, CubeTransform.None,
                                  $"{name} × {harmonic}",
                                  Style(3, LineType.Dashed, harmonic == 1 ? 0.75 : 0.4));

            // NOT DATA, and NOT ALLOWED TO SET THE WINDOW — see this file's header and
            // Trace.ExcludeFromAutoscale. transformBaked, because these two numbers are already in
            // the axis's own unit: they ARE the window, so a transform applied to them would be
            // applied twice.
            trace.IsAnnotation         = true;
            trace.ExcludeFromAutoscale = true;
            trace.SetCubeData([hz, hz], null, [lo, hi], "freq", "Hz", PlotType.Rect, FreqUnit.MHz,
                              transformBaked: true);
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
