using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NumFlat;
using RfCore;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// The Designer's two response plots and its three exports (match.md §9.6, §9.9).
/// </summary>
/// <remarks>
/// <b>The traces come from the real engine, not from the synthesis's own evaluator.</b> The netlist
/// is the FULL design — the ladder including the absorbed elements, terminated in the two port
/// resistances — elaborated and run through <c>SParameterEngine</c>. That is the response the user is
/// judging, and running it through the ordinary analysis path means the plot cannot quietly disagree
/// with what a simulation of the same circuit would say.
///
/// <para><b>The two port references are R1 and R2, and nothing renormalises them.</b> The Terms carry
/// the network's own end resistances, so the S-parameters come out referenced to them and the trace's
/// Z0-override stays OFF — see <c>RESOLVED.md</c> on the Data Display Z0 override, where an
/// unconditional renormalisation turned a real -20 dB match into -4 dB.</para>
/// </remarks>
public sealed partial class MatchDesignerViewModel
{
    /// <summary>
    /// The Data Display machinery the two response plots are hosted ON.
    /// </summary>
    /// <remarks>
    /// <b>The plots were bare <c>PlotControl</c>s, and four of the owner's 2026-08-20 reports were the
    /// same missing piece</b>: no marker info box appeared, the plot's own <c>Copy</c> did nothing,
    /// the axis and marker colours came out of <c>RenderTheme.Light</c> whatever the application
    /// theme was, and the background did not match the Data Display's. Every one of those lives on
    /// <see cref="PlotContainerViewModel"/> / <see cref="DataDisplayViewModel"/> — a
    /// <c>PlotControl</c> asks its host for the marker index, the info-box VM, the selected markers
    /// and the container to export, and a host that is null answers "nothing" to all four, silently.
    ///
    /// <para>So the Designer now HAS a host: one <see cref="DataDisplayViewModel"/> with exactly two
    /// containers in it, laid out by this window rather than by a canvas. That is the whole change —
    /// the plots, the markers, the info boxes, the inspector and the clipboard are the Data
    /// Display's, and the Designer only decides where the two boxes sit and which menu items it does
    /// not want (see <c>MatchDesignerWindow</c>).</para>
    ///
    /// <para><b>It is not a Data Display document.</b> Nothing is persisted, no datasource library is
    /// loaded into it, plots cannot be added or deleted, and its undo stack is its own — the
    /// Designer's edits still go on the owning schematic's stack.</para>
    /// </remarks>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(),
                                                        addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="MagnitudePlot"/>.</summary>
    public PlotContainerViewModel MagnitudeContainer { get; private set; } = null!;

    /// <summary>The container holding <see cref="PhasePlot"/>.</summary>
    public PlotContainerViewModel PhaseContainer { get; private set; } = null!;

    /// <summary>|S11| and |S21| against frequency.</summary>
    public Plot MagnitudePlot => MagnitudeContainer.PlotVM.Plot;

    /// <summary>S21 phase and group delay.</summary>
    public Plot PhasePlot => PhaseContainer.PlotVM.Plot;

    /// <summary>
    /// Creates the two containers. Called once from the view-model's own constructor path, before
    /// anything reads <see cref="MagnitudePlot"/>.
    /// </summary>
    /// <remarks>
    /// <b>Axes panning starts LOCKED</b> (owner, 2026-08-20: "have the plot's Lock Axes Panning set to
    /// true when user first opens Match Designer"). These two plots are read-outs of a design that is
    /// being edited underneath them: every committed edit re-runs the response and autoscales, so a
    /// pan is undone by the next keystroke anyway — and a user who had dragged the window off the
    /// trace would see an empty plot and read it as a broken design. The menu item still toggles it.
    /// </remarks>
    private void BuildPlotHost()
    {
        MagnitudeContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz,
                                              left: 0, top: 0, width: PlotWidth, height: PlotHeight);
        PhaseContainer     = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz,
                                              left: 0, top: PlotHeight, width: PlotWidth, height: PlotHeight);
        MagnitudePlot.Axes.LockedPanning = true;
        PhasePlot.Axes.LockedPanning     = true;
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
    }

    /// <summary>
    /// Removes every SELECTED marker from its trace, as one undoable step on the plot host's own
    /// stack — what the Delete key does on a Data Display canvas.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <c>DataDisplayViewModel.DeleteSelected</c>.</b> That method also removes
    /// selected PLOT CONTAINERS, and this window's two plots are not deletable — the AXAML disables
    /// their own Delete Plot for the same reason (there is nothing to delete them from, and their
    /// traces are rebuilt from the design on every edit). A shared gesture that could silently take a
    /// plot with the marker would be worse than no gesture.
    /// </remarks>
    [RelayCommand]
    public void DeleteSelectedMarkers()
    {
        foreach (var box in PlotHost.MarkerInfoBoxes.Where(b => b.IsSelected).ToList())
            box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);
    }

    /// <summary>Seed logical width of one response plot — the view re-sizes both to the pane.</summary>
    public const double PlotWidth = 340.0;

    /// <summary>Seed logical height, at the golden ratio the Data Display's own new plots open at.</summary>
    public const double PlotHeight = PlotWidth / 1.618;

    /// <summary>The SNP the plots are built from — the design response, as the engine computed it.</summary>
    public SNP? ResponseSnp { get; private set; }

    /// <summary>Non-empty when the response could not be computed, with the reason.</summary>
    [ObservableProperty] private string _responseError = "";

    /// <summary>How far outside the band the plots run, as a fraction of the band.</summary>
    public double PlotBandFraction
    {
        get => _design.PlotBandFraction;
        set
        {
            if (value == _design.PlotBandFraction || !(value >= 0) || value > 10) return;
            _design.PlotBandFraction = value;
            UpdatePlots();
            Commit();
        }
    }

    /// <summary>How many points the plots run at.</summary>
    public int PlotPoints
    {
        get => _design.PlotPoints;
        set
        {
            if (value == _design.PlotPoints || value < 2 || value > 20001) return;
            _design.PlotPoints = value;
            UpdatePlots();
            Commit();
        }
    }

    /// <summary>
    /// The plotted frequency grid: the design's EFFECTIVE outer band widened by
    /// <see cref="PlotBandFraction"/>.
    /// </summary>
    /// <remarks>
    /// <b>Outer, so a multiband design shows every band AND every gap</b> (match.md §18.7) — the gap
    /// mismatch is the design working, and a plot that cropped to one band would hide it. For a
    /// single band the effective outer pair IS (F1, F2), so nothing about the single-band plot moves.
    /// </remarks>
    public double[] PlotFrequencies()
    {
        // EffectiveBands.Outer, not (F1, F4): a tri-band design's outer edge is F6, and cropping to
        // F4 would leave the third band off the plot entirely.
        var (outerLo, outerHi) = _design.Effective.Outer;
        double span = outerHi - outerLo;
        double pad = span * Math.Max(0.0, _design.PlotBandFraction);
        double lo = Math.Max(outerLo - pad, span * 1e-6);
        double hi = outerHi + pad;
        int n = Math.Max(2, _design.PlotPoints);

        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = lo + (hi - lo) * i / (n - 1.0);
        return f;
    }

    /// <summary>
    /// Re-runs the design response and rebuilds both plots. Held for the duration of a slider drag
    /// (brief §5) and run once on release; never held for the ladder or the element values, which
    /// track the slider live.
    /// </summary>
    public void UpdatePlots()
    {
        var network = _rebuild?.Network;
        if (network is null)
        {
            ResponseSnp = null;
            MagnitudePlot.Traces.Clear();
            PhasePlot.Traces.Clear();
            // ── A SYNTHESIS REFUSAL IS NOT REPORTED HERE ─────────────────────
            //
            // Owner-reported, 2026-08-28: a tri-band design that reaches nothing covers the window in
            // warning text — the same refusal was rendered THREE times, in the Solutions panel and
            // twice under the plots (this line, and the status card's own Status.Refusal.Message,
            // which is the identical string because RefreshStatus takes it from the same _rebuild).
            //
            // The Solutions panel is where a refusal belongs: it is the panel that lists what the
            // whole cross-product found, so it is the one that can say nothing was found AND what to
            // change (see MatchAdvice). Under the plots the sentence is pure repetition of a fact the
            // empty plots already state, and it pushes the numeric readouts off screen.
            //
            // ResponseError itself stays — it still carries the things the Solutions panel cannot
            // know about: an infinite element from a transform on its bound, an engine failure on a
            // network that DID synthesise, and an export failure. Those are not "no solution".
            // The refused end still turns red, which is the signal that survives.
            ResponseError = "";
            AnnounceRebuiltPlots();
            return;
        }

        // A transform parked a part in 1e9 from its pole produces an infinite element: exact,
        // response-preserving in the limit, and not a circuit any engine can be handed. Refusing here
        // keeps the ladder, the grid and the status strip usable and says why — writing "Infinity"
        // into a netlist instead surfaces as an unresolved-NAME error from the expression engine,
        // three layers away from the transform that caused it.
        var bad = network.Elements.FirstOrDefault(e => !double.IsFinite(e.Value));
        if (bad is not null)
        {
            ResponseSnp = null;
            ResponseError =
                $"{bad.Name} came out {(double.IsNaN(bad.Value) ? "undefined" : "infinite")} — a "
                + "transform is sitting on its positivity threshold. Move it off the bound, or lock "
                + "another and let the linkage put the ratio somewhere it fits.";
            BuildPlots();
            return;
        }

        try
        {
            ResponseSnp = RunResponse(network, PlotFrequencies());
            ResponseError = "";
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            // A design whose elements the engine refuses is a reported state, not a crash on a
            // render pass — the ladder and the status strip stay usable and say why.
            ResponseSnp = null;
            ResponseError = $"The design response could not be computed: {e.Message}";
        }

        BuildPlots();
    }

    /// <summary>
    /// Runs one design response. Public so a test can take the same SNP the plots do, rather than a
    /// second computation that could agree with itself.
    /// </summary>
    public static SNP RunResponse(MatchNetwork network, double[] frequencies)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(frequencies);

        var (lib, tb) = new CnlReader().Read(BuildNetlist(network));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(netlist, frequencies);

        var cube = ds["S"];
        int nf = cube.Axes[0].Length, np = cube.Axes[1].Length;
        var raw = cube.ComplexValues;
        var mats = new Mat<Complex>[nf];
        for (int f = 0; f < nf; f++)
        {
            mats[f] = new Mat<Complex>(np, np);
            for (int i = 0; i < np; i++)
                for (int j = 0; j < np; j++)
                    mats[f][i, j] = raw[f * np * np + i * np + j];
        }

        // Port 1's reference. The two ends genuinely differ, and SNP is uniform-only by design —
        // which is exactly why the traces carry SourceZ0PerPort and never renormalise (below).
        return new SNP(cube.Axes[0].Values, mats, MatrixType.S, MatrixFormat.RI,
                       new Complex(network.R1, 0.0));
    }

    /// <summary>
    /// The full design as a netlist: the ladder <b>including</b> the absorbed elements, terminated in
    /// the network's own two end resistances.
    /// </summary>
    /// <remarks>
    /// <b>Including the absorbed elements is the point.</b> The COMPONENT is the ladder minus them
    /// (MN-2), because they belong to the external network — but the response the user is judging is
    /// the whole thing, terminations and all. A netlist of the component alone would plot a network
    /// nobody is building.
    ///
    /// <para>Elements are named by position (<c>E0</c>, <c>E1</c>, ...) rather than by their ladder
    /// names: a Norton product is called <c>L1_N1_2</c>, and an instance name is not the place to find
    /// out which punctuation a netlist reader accepts.</para>
    /// </remarks>
    public static string BuildNetlist(MatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        var sb = new StringBuilder();
        sb.Append("Term:MT1  p1 0  Num=1 Z=").AppendLine(Num(network.R1));

        string current = "p1";
        int mint = 0;
        for (int i = 0; i < network.Elements.Count; i++)
        {
            var e = network.Elements[i];
            char type = e.Type == ElementType.L ? 'L' : 'C';
            if (e.IsShunt)
            {
                sb.Append(type).Append(":E").Append(i).Append("  ").Append(current)
                  .Append(" 0  ").Append(type).Append('=').AppendLine(Num(e.Value));
                continue;
            }
            string next = $"__mn{++mint}";
            sb.Append(type).Append(":E").Append(i).Append("  ").Append(current).Append(' ').Append(next)
              .Append("  ").Append(type).Append('=').AppendLine(Num(e.Value));
            current = next;
        }

        sb.Append("Term:MT2  ").Append(current).Append(" 0  Num=2 Z=").AppendLine(Num(network.R2));
        return sb.ToString();
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ── The plots ─────────────────────────────────────────────────────────────

    private void BuildPlots()
    {
        MagnitudePlot.Traces.Clear();
        PhasePlot.Traces.Clear();

        if (ResponseSnp is not { } snp)
        {
            AnnounceRebuiltPlots();
            return;
        }

        var perPort = PortReferences();

        var s11 = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db, false, PrimaryStyle())
            { SourceZ0PerPort = perPort };
        s11.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);

        // S21 reads the RIGHT axis (owner, 2026-08-20). |S11| runs to −40 dB and below while |S21|
        // sits within a decibel of 0; sharing one axis spends the whole scale on the return loss and
        // renders the insertion loss as a flat line on the ceiling. Two axes give each its own range.
        var s21 = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db, true, SecondaryStyle())
            { SourceZ0PerPort = perPort };
        s21.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
        MagnitudePlot.Traces.Add(s11);
        MagnitudePlot.Traces.Add(s21);
        MagnitudePlot.CustomTitleOn = true;
        // Named for what is ON it, not for what it is of (owner, 2026-08-20). "Design response" was
        // true of both plots and so distinguished neither.
        MagnitudePlot.CustomTitle = "Return and Insertion Loss";
        MagnitudePlot.Autoscale(force: true);

        var phase = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Phase, false, PrimaryStyle())
            { SourceZ0PerPort = perPort };
        phase.BuildPath(PlotType.Rect, PhasePlot.FreqUnits);
        PhasePlot.Traces.Add(phase);

        var delay = GroupDelayTrace(snp, PhasePlot.FreqUnits);
        if (delay is not null) PhasePlot.Traces.Add(delay);
        PhasePlot.CustomTitleOn = true;
        PhasePlot.CustomTitle = "Phase and Group Delay";
        PhasePlot.Autoscale(force: true);

        AnnounceRebuiltPlots();
    }

    /// <summary>
    /// Tells the host that both plots have been rebuilt: <b>the info boxes, the bindings, and the
    /// repaint</b>. The one exit every rebuild takes, so no path can announce two of the three.
    /// </summary>
    /// <remarks>
    /// <para><b>OnPlotChanged</b> — the traces the markers were on have just been REPLACED, since an
    /// edit rebuilds both plots from scratch, so the host has to be told or its info boxes go on
    /// pointing at <c>Trace</c> objects that are no longer in any plot. It is the same notification
    /// <c>PlotContainerView</c> raises on the canvas, and it drops exactly the boxes whose markers are
    /// gone.</para>
    ///
    /// <para><b>RequestPlotRedraw is the part that was missing</b> (owner-reported, 2026-08-28:
    /// applying a solution sometimes leaves the plots looking unchanged, including across a move
    /// between two response families that should look nothing alike).
    /// Nothing here ever invalidated the <c>PlotControl</c>. The two plot MODELS are the same two
    /// objects for the window's whole life — a rebuild clears their <c>Traces</c> and refills them —
    /// so <c>OnPropertyChanged(nameof(MagnitudePlot))</c> re-pushes an unchanged reference through the
    /// binding, and <c>OnPlotChanged</c> only raises property changes on the container's own view-model.
    /// Neither is a repaint. The control redrew when something ELSE happened to invalidate it — the
    /// pointer crossing it, a resize, the window being re-activated — which is precisely a change that
    /// appears "sometimes", and appears the moment the user moves the mouse to look closer.
    /// <c>PlotContainerViewModel.PlotNeedsRedraw</c> is the seam the Data Display already uses for
    /// exactly this, and <c>WirePlotHost</c> in the code-behind has it wired to
    /// <c>PlotControl.InvalidateVisual</c> — it simply had no caller on this side.</para>
    /// </remarks>
    private void AnnounceRebuiltPlots()
    {
        MagnitudeContainer.OnPlotChanged(this, EventArgs.Empty);
        PhaseContainer.OnPlotChanged(this, EventArgs.Empty);

        OnPropertyChanged(nameof(MagnitudePlot));
        OnPropertyChanged(nameof(PhasePlot));

        MagnitudeContainer.RequestPlotRedraw();
        PhaseContainer.RequestPlotRedraw();
    }

    /// <summary>
    /// The style a FIRST trace gets, and <see cref="SecondaryStyle"/> the style a second one gets.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20: "use the same plot colors as the data display — same shade of red and
    /// same shade of blue for all plot traces."</b> They were not: a <c>Trace</c> built with no
    /// properties takes <c>LineColorOrder[0]</c>, so BOTH traces on each plot came out the same red.
    /// The Data Display's own second trace is <c>new Trace(src, incrementColorBy: 1)</c>, which is
    /// <c>LineColorOrder[1]</c> — so these two read the SHARED order table by index rather than
    /// naming a colour, and a change to the application's palette moves this window with it.
    /// </remarks>
    private static TraceProperties PrimaryStyle() => StyleAt(0);

    /// <inheritdoc cref="PrimaryStyle"/>
    private static TraceProperties SecondaryStyle() => StyleAt(1);

    private static TraceProperties StyleAt(int order)
    {
        int index = TraceProperties.LineColorOrder[order];
        var props = new TraceProperties();
        props.LineColorIndex   = index;
        props.MarkerColorIndex = index;
        props.FillColorIndex   = index;
        // Custom is set by every setter above; clearing it keeps these traces reading as
        // palette-default rather than user-overridden, which is what they are.
        props.Custom = false;
        return props;
    }

    /// <summary>The two ports' TRUE references — R1 and R2, which genuinely differ.</summary>
    private Complex[] PortReferences()
    {
        var network = _rebuild?.Network;
        return network is null
            ? [new Complex(50, 0), new Complex(50, 0)]
            : [new Complex(network.R1, 0), new Complex(network.R2, 0)];
    }

    /// <summary>
    /// The group-delay trace, in nanoseconds against the secondary axis.
    /// </summary>
    /// <remarks>
    /// <b>The delay itself is <c>RFNetwork.GroupDelay</c>'s</b> — RfCore grew a group-delay metric on
    /// 2026-08-19 (it is a Data Display derived parameter now, beside μ and μ′), and this window was
    /// only ever computing its own because at the time there was nothing to call. There is exactly
    /// one −dφ/dω in the application again.
    ///
    /// <para>What stays local is how it is PLOTTED. <c>DependentVarFormat</c> has Db, Mag, Phase,
    /// Real and Imaginary and nothing else, and a sixth member would have to mean something for a
    /// Smith trace, a table cell, a marker readout and a persisted <c>.cdd</c> — none of which wanted
    /// it. The already-reduced numbers go in through <c>Trace.SetCubeData</c> with the transform
    /// baked, the seam the Data Display already uses for any value it has reduced itself, so the
    /// trace renders, autoscales and reads out like any other.</para>
    /// </remarks>
    /// <summary>
    /// What the group-delay trace is called — <b>the application's own spelling of the quantity,
    /// units included</b>, read from the derived-parameter table rather than written out here so the
    /// two cannot drift.
    /// </summary>
    /// <remarks>
    /// The Designer computes this delay itself (see <see cref="GroupDelayTrace"/> for why it is not a
    /// <c>DerivedParameters</c> trace), but it is the SAME quantity in the same nanoseconds, and a
    /// user reading one plot after the other must not be told it is two.
    /// </remarks>
    public static string TraceGroupDelayName => DerivedParameters.GroupDelay.Description();

    /// <inheritdoc cref="TraceGroupDelayName"/>
    public static Trace? GroupDelayTrace(SNP snp, FreqUnit freqUnit)
    {
        ArgumentNullException.ThrowIfNull(snp);
        var f = snp.Frequencies;
        if (f.Length < 3 || snp.Ports < 2) return null;

        var tauNs = RFNetwork.GroupDelay(snp);
        for (int i = 0; i < tauNs.Length; i++) tauNs[i] *= 1e9;

        var trace = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Real,
                              secondaryAxis: true, properties: SecondaryStyle())
        {
            // NAMED WITH ITS UNIT, and named the way the rest of the application already names this
            // quantity — DerivedParameters.GroupDelay's own Description(), character for character
            // (owner, 2026-08-28: the right-hand y-axis label needs the group delay's units on it).
            //
            // On the CubeName rather than as a custom Y2 label on the plot, because that is the one
            // string the axis label, the marker readout and the info box all derive from
            // (TraceLabeler.BuildCubeQuantity): a custom axis label would have put the unit on the
            // axis and left the marker beside it reading a bare number with no unit at all. It also
            // keeps the label in the TRACE's own colour, which a custom label does not.
            CubeName = TraceGroupDelayName,
        };
        trace.SetCubeData(f, null, tauNs, "freq", "Hz", PlotType.Rect, freqUnit, transformBaked: true);
        return trace;
    }

    // ── Exports (§9.9) ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the design response as Touchstone.
    /// </summary>
    /// <remarks>
    /// <b>match.md §9.9 asks for "the per-port references R1/R2 written as the file's own", and
    /// Touchstone cannot express that</b> — the option line carries ONE real R, and
    /// <c>TouchstoneIO</c>'s own per-port note prints the uniform value N times for the same reason.
    /// The data is written unrenormalised (it is referenced to R1 and R2, and renormalising it to hide
    /// the format's limit would change the numbers), the option line carries R1, and the header
    /// comments state both references so a reader is not left to infer one.
    /// </remarks>
    public void ExportTouchstone(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (ResponseSnp is not { } snp || _rebuild?.Network is not { } net)
            throw new InvalidOperationException("There is no design response to export.");

        writer.WriteLine($"! circuitRF Match Designer — {InstanceName}");
        writer.WriteLine($"! Band {Num(_design.F1)} .. {Num(_design.F2)} Hz, order {_design.Order}, {_design.Response}");
        writer.WriteLine($"! Port 1 reference {Num(net.R1)} ohm, port 2 reference {Num(net.R2)} ohm.");
        writer.WriteLine("! Touchstone carries ONE reference on its option line; the data below is NOT");
        writer.WriteLine("! renormalised, so port 2 is referenced to its own value above, not to the R= shown.");
        TouchstoneIO.Write(snp, writer, MatrixFormat.MA);
    }

    /// <summary>The component listing — the same rows as the grid view.</summary>
    public string ComponentListingCsv() => ElementsCsv;

    /// <summary>The prototype g-values, for anyone checking the synthesis against a published table.</summary>
    public string PrototypeGValuesCsv()
    {
        var g = _rebuild?.Basis.G ?? [];
        var sb = new StringBuilder();
        sb.AppendLine("index,g");
        for (int i = 0; i < g.Length; i++)
            sb.Append(i).Append(',').AppendLine(g[i].ToString("G12", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
