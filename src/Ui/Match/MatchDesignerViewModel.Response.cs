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
using CommunityToolkit.Mvvm.ComponentModel;
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
    /// <summary>|S11| and |S21| against frequency.</summary>
    [ObservableProperty] private Plot _magnitudePlot = new(PlotType.Rect, FreqUnit.GHz);

    /// <summary>S21 phase and group delay.</summary>
    [ObservableProperty] private Plot _phasePlot = new(PlotType.Rect, FreqUnit.GHz);

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

    /// <summary>The plotted frequency grid: the design band widened by <see cref="PlotBandFraction"/>.</summary>
    public double[] PlotFrequencies()
    {
        double span = _design.F2 - _design.F1;
        double pad = span * Math.Max(0.0, _design.PlotBandFraction);
        double lo = Math.Max(_design.F1 - pad, span * 1e-6);
        double hi = _design.F2 + pad;
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
            ResponseError = _rebuild?.Refusal is { } r ? r.Message : "";
            OnPropertyChanged(nameof(MagnitudePlot));
            OnPropertyChanged(nameof(PhasePlot));
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
            OnPropertyChanged(nameof(MagnitudePlot));
            OnPropertyChanged(nameof(PhasePlot));
            return;
        }

        var perPort = PortReferences();

        var s11 = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourceZ0PerPort = perPort };
        s11.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
        var s21 = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db) { SourceZ0PerPort = perPort };
        s21.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
        MagnitudePlot.Traces.Add(s11);
        MagnitudePlot.Traces.Add(s21);
        MagnitudePlot.CustomTitleOn = true;
        MagnitudePlot.CustomTitle = "Design response";
        MagnitudePlot.Autoscale(force: true);

        var phase = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Phase) { SourceZ0PerPort = perPort };
        phase.BuildPath(PlotType.Rect, PhasePlot.FreqUnits);
        PhasePlot.Traces.Add(phase);

        var delay = GroupDelayTrace(snp, PhasePlot.FreqUnits);
        if (delay is not null) PhasePlot.Traces.Add(delay);
        PhasePlot.CustomTitleOn = true;
        PhasePlot.CustomTitle = "S21 phase and group delay";
        PhasePlot.Autoscale(force: true);

        OnPropertyChanged(nameof(MagnitudePlot));
        OnPropertyChanged(nameof(PhasePlot));
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
    public static Trace? GroupDelayTrace(SNP snp, FreqUnit freqUnit)
    {
        ArgumentNullException.ThrowIfNull(snp);
        var f = snp.Frequencies;
        if (f.Length < 3 || snp.Ports < 2) return null;

        var tauNs = RFNetwork.GroupDelay(snp);
        for (int i = 0; i < tauNs.Length; i++) tauNs[i] *= 1e9;

        var trace = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Real, secondaryAxis: true)
        {
            CubeName = "GroupDelay",
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
