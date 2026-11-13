using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NumFlat;
using RfCore;

namespace CircuitRF.Ui.Filters;

/// <summary>One row of the realization table.</summary>
public sealed class FilterElementRow
{
    /// <summary>The element name in the netlist.</summary>
    public string Name { get; init; } = "";
    /// <summary>Series or shunt, and L or C.</summary>
    public string Kind { get; init; } = "";
    /// <summary>The theoretical value.</summary>
    public string Exact { get; init; } = "";
    /// <summary>The value actually written: exact, an IEC rung, or a library part.</summary>
    public string Used { get; init; } = "";
    /// <summary>The library part, when one was chosen.</summary>
    public string Part { get; init; } = "";
}

/// <summary>
/// The Filter Assistant. The specification is the one on the assistant screenshots — source and
/// load, first element, order, the mask frequencies, Ap and As, and the response family — and the
/// curves are the Data Display's own plots, filled from the ideal response and from the ladder run
/// through the S-parameter engine.
/// </summary>
public sealed partial class FilterDesignerViewModel : ObservableObject
{
    /// <summary>The families, in the order the assistant offers them.</summary>
    public IReadOnlyList<FilterResponse> Responses { get; } = Enum.GetValues<FilterResponse>();

    /// <summary>Lowpass, highpass, bandpass, bandstop.</summary>
    public IReadOnlyList<NetworkForm> Forms { get; } =
        [NetworkForm.Lowpass, NetworkForm.Highpass, NetworkForm.Bandpass, NetworkForm.Bandstop];

    /// <summary>Series or parallel first arm.</summary>
    public IReadOnlyList<FilterFirstElement> FirstElements { get; } = Enum.GetValues<FilterFirstElement>();

    /// <summary>Exact, or an IEC series.</summary>
    public IReadOnlyList<FilterSeriesKind> SeriesKinds { get; } = Enum.GetValues<FilterSeriesKind>();

    /// <summary>The frequency unit the four edge boxes are typed in.</summary>
    public IReadOnlyList<string> Units { get; } = ["Hz", "kHz", "MHz", "GHz"];

    /// <summary>The Data Display the two plots are hosted on. Markers, colours and the clipboard
    /// are its own.</summary>
    public DataDisplayViewModel PlotHost { get; } = new(new DataSourceLibraryViewModel(),
                                                        addEmptyPlot: false, selectEmptyPlot: false);

    /// <summary>The container holding <see cref="MagnitudePlot"/>.</summary>
    public PlotContainerViewModel MagnitudeContainer { get; }

    /// <summary>The container holding <see cref="PhasePlot"/>.</summary>
    public PlotContainerViewModel PhaseContainer { get; }

    /// <summary>|S21| of the ideal response and of the ladder.</summary>
    public Plot MagnitudePlot => MagnitudeContainer.PlotVM.Plot;

    /// <summary>Phase and group delay of the ladder.</summary>
    public Plot PhasePlot => PhaseContainer.PlotVM.Plot;

    /// <summary>The realization, one row per element.</summary>
    public ObservableCollection<FilterElementRow> Elements { get; } = [];

    /// <summary>Inductor series found in the workspace's vendor libraries.</summary>
    public ObservableCollection<FilterLibrarySeries> InductorSeries { get; } = [];

    /// <summary>Capacitor series found in the workspace's vendor libraries.</summary>
    public ObservableCollection<FilterLibrarySeries> CapacitorSeries { get; } = [];

    /// <summary>The netlist of the realization last built, snapped values included.</summary>
    public string Cnl { get; private set; } = "";

    /// <summary>Set when the assistant was opened on a placed Filter, so Apply can write it back.</summary>
    public EditableComponent? Target { get; set; }

    /// <summary>The schematic that owns <see cref="Target"/>.</summary>
    public global::CircuitRF.Ui.ViewModels.SchematicViewModel? Schematic { get; set; }

    /// <summary>A workspace root, so Load library series has somewhere to look.</summary>
    public string? WorkspaceRoot { get; set; }

    [ObservableProperty] private FilterResponse _response = FilterResponse.Butterworth;
    [ObservableProperty] private NetworkForm _form = NetworkForm.Bandpass;
    [ObservableProperty] private FilterFirstElement _first = FilterFirstElement.Parallel;
    [ObservableProperty] private FilterSeriesKind _series = FilterSeriesKind.Exact;
    [ObservableProperty] private string _unit = "GHz";
    [ObservableProperty] private string _orderText = "5";
    [ObservableProperty] private string _apText = "3";
    [ObservableProperty] private string _asText = "20";
    [ObservableProperty] private string _sourceText = "50";
    [ObservableProperty] private string _loadText = "50";
    [ObservableProperty] private string _dbPerDivText = "2";
    [ObservableProperty] private string _faText = "0.5";
    [ObservableProperty] private string _fbText = "1";
    [ObservableProperty] private string _fcText = "2";
    [ObservableProperty] private string _fdText = "2.5";
    [ObservableProperty] private bool _showC;
    [ObservableProperty] private bool _showD;
    [ObservableProperty] private string _labelA = "Fs1";
    [ObservableProperty] private string _labelB = "Fp1";
    [ObservableProperty] private string _labelC = "Fp2";
    [ObservableProperty] private string _labelD = "Fs2";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _designInfo = "";
    [ObservableProperty] private FilterLibrarySeries? _inductorChoice;
    [ObservableProperty] private FilterLibrarySeries? _capacitorChoice;
    [ObservableProperty] private bool _libraryAvailable;

    /// <summary>True when a placed Filter can take Apply.</summary>
    public bool CanApply => Target is { Symbol: SymbolKind.Filter } && Schematic is not null;

    public FilterDesignerViewModel()
    {
        MagnitudeContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, 0, 0, 640, 280);
        PhaseContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, 0, 280, 640, 220);
        MagnitudePlot.Axes.LockedPanning = true;
        PhasePlot.Axes.LockedPanning = true;
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
        ApplyLabels();
    }

    partial void OnFormChanged(NetworkForm value) => ApplyLabels();
    partial void OnResponseChanged(FilterResponse value) => Design();
    partial void OnFirstChanged(FilterFirstElement value) => Design();
    partial void OnSeriesChanged(FilterSeriesKind value) => Design();
    partial void OnUnitChanged(string value) => Design();
    partial void OnInductorChoiceChanged(FilterLibrarySeries? value) => Design();
    partial void OnCapacitorChoiceChanged(FilterLibrarySeries? value) => Design();

    /// <summary>The edge labels follow the form. Two edges for lowpass and highpass, four otherwise.</summary>
    public void ApplyLabels()
    {
        switch (Form)
        {
            case NetworkForm.Lowpass:
                LabelA = "Fp"; LabelB = "Fs"; ShowC = false; ShowD = false;
                break;
            case NetworkForm.Highpass:
                LabelA = "Fs"; LabelB = "Fp"; ShowC = false; ShowD = false;
                break;
            case NetworkForm.Bandstop:
                LabelA = "Fp1"; LabelB = "Fs1"; LabelC = "Fs2"; LabelD = "Fp2";
                ShowC = true; ShowD = true;
                break;
            default:
                LabelA = "Fs1"; LabelB = "Fp1"; LabelC = "Fp2"; LabelD = "Fs2";
                ShowC = true; ShowD = true;
                break;
        }
        Design();
    }

    /// <summary>Builds the realization and redraws both plots. Redraw and Design are this one step:
    /// the ladder is closed form, so there is nothing to defer.</summary>
    [RelayCommand]
    public void Design()
    {
        Elements.Clear();
        Cnl = "";
        if (!TrySpec(out var spec, out string? error))
        {
            Status = error ?? "";
            DesignInfo = "";
            ClearPlots();
            return;
        }

        FilterNetwork ideal;
        try
        {
            ideal = FilterNetwork.Create(spec.Response, spec.Form, spec.Order,
                                          spec.FcHz, spec.F1Hz, spec.F2Hz, spec.RippleDb, spec.AsDb, 0);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            Status = e.Message;
            ClearPlots();
            return;
        }

        var built = FilterLadder.Synthesize(spec.Response, spec.Form, spec.Order,
                                             spec.FcHz, spec.F1Hz, spec.F2Hz, spec.RippleDb,
                                             spec.SourceOhms, spec.First);
        var freqs = Frequencies(spec);
        var idealSnp = IdealSnp(ideal, freqs, spec.SourceOhms, built.Realization?.LoadOhms ?? spec.LoadOhms);

        SNP? ladderSnp = null;
        string note = "";
        if (built.Realization is { } real)
        {
            var used = ApplyValues(real, spec);
            Cnl = FilterLadder.ToCnl(used, real.SourceOhms, real.LoadOhms);
            try
            {
                ladderSnp = Run(Cnl, freqs, real.SourceOhms);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
            {
                note = e.Message;
            }
            if (Math.Abs(real.LoadOhms - spec.LoadOhms) / spec.LoadOhms > 0.01)
                note = $"The ladder's load is {real.LoadOhms:0.###} Ω. The requested load is " +
                       $"{spec.LoadOhms:0.###} Ω, which Place as Filter keeps; the ladder is built " +
                       "for the ratio the family produces. " + note;
            FillRows(real.Elements, used);
        }
        else
        {
            note = built.Refusal?.Message ?? "";
        }

        double minIl = MinimumInsertionLoss(ladderSnp ?? idealSnp, freqs, spec);
        DesignInfo = $"Order: {spec.Order}\nMinimum insertion loss: {minIl:0.0000} dB" +
                     (built.Realization is { } r ? $"\nLoad: {r.LoadOhms:0.###} Ω" : "");
        Status = note;
        BuildPlots(idealSnp, ladderSnp, spec.DbPerDiv);
    }

    /// <summary>Indexes the workspace's vendor libraries into inductor and capacitor series.</summary>
    [RelayCommand]
    public void LoadLibraries()
    {
        InductorSeries.Clear();
        CapacitorSeries.Clear();
        var notes = new List<string>();
        foreach (var s in FilterValues.LibrarySeries(WorkspaceRoot, notes))
        {
            if (s.Inductors) InductorSeries.Add(s);
            else CapacitorSeries.Add(s);
        }
        LibraryAvailable = InductorSeries.Count > 0 || CapacitorSeries.Count > 0;
        Status = LibraryAvailable
            ? $"{InductorSeries.Count} inductor series, {CapacitorSeries.Count} capacitor series."
            : notes.FirstOrDefault()
              ?? "This workspace has no vendor library with inductor or capacitor series.";
        if (LibraryAvailable) Design();
    }

    /// <summary>Reads a placed Filter's parameters into the boxes.</summary>
    public void SeedFrom(EditableComponent component)
    {
        string Expr(string name) =>
            component.Parameters.FirstOrDefault(p => p.Name == name)?.Expression ?? "";
        if (Enum.TryParse<FilterResponse>(Expr("Response"), out var response)) Response = response;
        if (Enum.TryParse<NetworkForm>(Expr("Form"), out var form)) Form = form;
        if (double.TryParse(Expr("Order"), NumberStyles.Float, CultureInfo.InvariantCulture, out double order))
            OrderText = ((int)order).ToString(CultureInfo.InvariantCulture);
        Unit = "GHz";
        double GhzOf(string name, double fallback)
        {
            var p = component.Parameters.FirstOrDefault(x => x.Name == name);
            if (p is null || !double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return fallback;
            double scale = p.Unit.ToLowerInvariant() switch
            {
                "hz" => 1e-9, "khz" => 1e-6, "mhz" => 1e-3, "ghz" => 1, _ => 1,
            };
            return v * scale;
        }
        double Ohms(string name, double fallback)
        {
            var p = component.Parameters.FirstOrDefault(x => x.Name == name);
            if (p is null || !double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return fallback;
            return v;
        }
        FaText = GhzOf(Form is NetworkForm.Lowpass or NetworkForm.Highpass ? "Fc" : "F1", 1).ToString("0.####", CultureInfo.InvariantCulture);
        FbText = GhzOf("F2", 2).ToString("0.####", CultureInfo.InvariantCulture);
        if (double.TryParse(Expr("Ripple"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ap))
            ApText = ap.ToString("0.####", CultureInfo.InvariantCulture);
        if (double.TryParse(Expr("Astop"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ast))
            AsText = ast.ToString("0.####", CultureInfo.InvariantCulture);
        SourceText = Ohms("Zin", 50).ToString("0.####", CultureInfo.InvariantCulture);
        LoadText = Ohms("Zout", 50).ToString("0.####", CultureInfo.InvariantCulture);
        ApplyLabels();
    }

    private void ApplyLabelsWithoutDesign() { }

    private sealed record Spec(
        FilterResponse Response, NetworkForm Form, int Order, FilterFirstElement First,
        double FcHz, double F1Hz, double F2Hz, double RippleDb, double AsDb,
        double SourceOhms, double LoadOhms, double DbPerDiv,
        double PassLoHz, double PassHiHz, bool PassIsOutside);

    private bool TrySpec(out Spec spec, out string? error)
    {
        spec = new Spec(FilterResponse.Butterworth, NetworkForm.Lowpass, 1, FilterFirstElement.Series,
                        1, 0, 0, 0.1, 20, 50, 50, 2, 0, 1, false);
        error = null;
        if (!int.TryParse(OrderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int order) || order < 1)
        { error = "Order is a whole number, 1 or more."; return false; }
        if (!double.TryParse(ApText, NumberStyles.Float, CultureInfo.InvariantCulture, out double ap) || ap < 0)
        { error = "Ap is a number of dB, zero or more."; return false; }
        if (!double.TryParse(AsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double astop) || !(astop > 0))
        { error = "As is a positive number of dB."; return false; }
        if (!double.TryParse(SourceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rs) || !(rs > 0))
        { error = "The source impedance is a positive number of ohms."; return false; }
        if (!double.TryParse(LoadText, NumberStyles.Float, CultureInfo.InvariantCulture, out double rl) || !(rl > 0))
        { error = "The load impedance is a positive number of ohms."; return false; }
        if (!double.TryParse(DbPerDivText, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) || !(db > 0))
            db = 2;
        double scale = UnitScale();
        if (!Hz(FaText, scale, out double a) || !Hz(FbText, scale, out double b))
        { error = "A frequency edge is a positive number."; return false; }
        Hz(FcText, scale, out double c);
        Hz(FdText, scale, out double d);

        double fc, f1, f2, passLo, passHi;
        bool outside;
        switch (Form)
        {
            case NetworkForm.Lowpass:
                fc = a; f1 = f2 = 0; passLo = 0; passHi = a; outside = false; // A is Fp
                break;
            case NetworkForm.Highpass:
                fc = b; f1 = f2 = 0; passLo = b; passHi = double.PositiveInfinity; outside = false; // B is Fp
                break;
            case NetworkForm.Bandstop:
                // A Fp1, B Fs1, C Fs2, D Fp2. The cutoff pair is the stopband.
                if (!(b > 0) || !(c > b)) { error = "A bandstop needs 0 < Fs1 < Fs2."; return false; }
                fc = 0; f1 = b; f2 = c; passLo = a; passHi = d; outside = true;
                break;
            default:
                if (!(b > 0) || !(c > b)) { error = "A bandpass needs 0 < Fp1 < Fp2."; return false; }
                fc = 0; f1 = b; f2 = c; passLo = b; passHi = c; outside = false;
                break;
        }
        // Chebyshev and elliptic read Ap as the ripple. The other families ignore it, and a 0 would
        // be refused by the prototype, so a maximally-flat Ap of 0 stays 0 and is not passed.
        double ripple = Response is FilterResponse.Chebyshev or FilterResponse.Elliptic
            ? (ap > 0 ? ap : 0.1) : 0.1;
        spec = new Spec(Response, Form, order, First, fc, f1, f2, ripple, astop, rs, rl, db,
                        passLo, passHi, outside);
        return true;
    }

    private static bool Hz(string text, double scale, out double hz)
    {
        hz = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0))
            return false;
        hz = v * scale;
        return true;
    }

    private double UnitScale() => Unit switch
    {
        "kHz" => 1e3,
        "MHz" => 1e6,
        "GHz" => 1e9,
        _ => 1,
    };

    private List<FilterElement> ApplyValues(FilterRealization real, Spec spec)
    {
        var used = new List<FilterElement>(real.Elements.Count);
        foreach (var e in real.Elements)
        {
            double value = FilterValues.Snap(e.Inductor, e.Value, Series);
            string part = "";
            var series = e.Inductor ? InductorChoice : CapacitorChoice;
            if (series is not null && FilterValues.Nearest(e.Value, series.Parts) is { } chosen)
            {
                value = chosen.Value;
                part = chosen.Name;
            }
            used.Add(e with { Value = value });
            Elements.Add(new FilterElementRow
            {
                Name = e.Name,
                Kind = (e.Shunt ? "shunt " : "series ") + (e.Inductor ? "L" : "C"),
                Exact = Eng(e.Inductor, e.Value),
                Used = Eng(e.Inductor, value),
                Part = part,
            });
        }
        return used;
    }

    private static string Eng(bool inductor, double v)
    {
        (double Scale, string Unit) pick = inductor
            ? v >= 1e-6 ? (1e6, "µH") : v >= 1e-9 ? (1e9, "nH") : (1e12, "pH")
            : v >= 1e-6 ? (1e6, "µF") : v >= 1e-9 ? (1e9, "nF") : (1e12, "pF");
        return (v * pick.Scale).ToString("0.####", CultureInfo.InvariantCulture) + " " + pick.Unit;
    }

    private void FillRows(IReadOnlyList<FilterElement> exact, List<FilterElement> used)
    {
        // ApplyValues already filled the rows as it snapped. Nothing further.
        _ = exact;
        _ = used;
    }

    private static double[] Frequencies(Spec spec)
    {
        double lo, hi;
        if (spec.Form == NetworkForm.Lowpass)
        {
            lo = Math.Max(spec.FcHz / 50, 1e3);
            hi = spec.FcHz * 20;
        }
        else if (spec.Form == NetworkForm.Highpass)
        {
            lo = Math.Max(spec.FcHz / 20, 1e3);
            hi = spec.FcHz * 50;
        }
        else
        {
            lo = Math.Max(spec.F1Hz / 5, 1e3);
            hi = spec.F2Hz * 5;
        }
        var f = new double[201];
        for (int i = 0; i < f.Length; i++)
            f[i] = lo * Math.Pow(hi / lo, i / (f.Length - 1.0));
        return f;
    }

    private static SNP IdealSnp(FilterNetwork ideal, double[] freqs, double rs, double rl)
    {
        var mats = new Mat<Complex>[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
        {
            var (s11, s21, s22) = ideal.At(2 * Math.PI * freqs[i]);
            mats[i] = new Mat<Complex>(2, 2);
            mats[i][0, 0] = s11;
            mats[i][0, 1] = s21;
            mats[i][1, 0] = s21;
            mats[i][1, 1] = s22;
        }
        var snp = new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI, new Complex(rs, 0));
        return snp;
    }

    private static SNP Run(string cnl, double[] freqs, double z0)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(netlist, freqs);
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
        return new SNP(cube.Axes[0].Values, mats, MatrixType.S, MatrixFormat.RI, new Complex(z0, 0));
    }

    private static double MinimumInsertionLoss(SNP snp, double[] freqs, Spec spec)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < freqs.Length && i < snp.Frequencies.Length; i++)
        {
            double f = freqs[i];
            bool pass = spec.PassIsOutside
                ? f <= spec.PassLoHz || f >= spec.PassHiHz
                : f >= spec.PassLoHz && f <= spec.PassHiHz;
            if (!pass) continue;
            double il = -20.0 * Math.Log10(Math.Max(snp.Matrices[i][1, 0].Magnitude, 1e-30));
            if (il < best) best = il;
        }
        return double.IsPositiveInfinity(best) ? 0 : Math.Max(0, best);
    }

    private void BuildPlots(SNP ideal, SNP? ladder, double dbPerDiv)
    {
        MagnitudePlot.Traces.Clear();
        PhasePlot.Traces.Clear();
        MagnitudePlot.FreqUnits = FreqUnitOf(Unit);
        PhasePlot.FreqUnits = MagnitudePlot.FreqUnits;

        // CubeName must stay null. A non-null name marks the trace cube-bound, and BuildPath
        // then ignores the SNP (Trace.IsCubeBound). These are matrix traces.
        var idealTrace = new Trace(ideal, MatrixType.S, 1, 0, DependentVarFormat.Db, false, Style(0));
        idealTrace.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
        MagnitudePlot.Traces.Add(idealTrace);

        if (ladder is not null)
        {
            var ladderTrace = new Trace(ladder, MatrixType.S, 1, 0, DependentVarFormat.Db, false, Style(1));
            ladderTrace.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
            MagnitudePlot.Traces.Add(ladderTrace);

            var s11 = new Trace(ladder, MatrixType.S, 0, 0, DependentVarFormat.Db, true, Style(2));
            s11.BuildPath(PlotType.Rect, MagnitudePlot.FreqUnits);
            MagnitudePlot.Traces.Add(s11);

            var phase = new Trace(ladder, MatrixType.S, 1, 0, DependentVarFormat.Phase, false, Style(0));
            phase.BuildPath(PlotType.Rect, PhasePlot.FreqUnits);
            PhasePlot.Traces.Add(phase);
            if (MatchDesignerViewModel.GroupDelayTrace(ladder, PhasePlot.FreqUnits) is { } delay)
                PhasePlot.Traces.Add(delay);
        }

        MagnitudePlot.CustomTitleOn = true;
        MagnitudePlot.CustomTitle = "Insertion loss";
        MagnitudePlot.Autoscale(force: true);
        // After autoscale, keep the grid at the requested dB spacing. SetTicks overwrites YTick
        // when the window is assigned, so this assignment has to come last.
        MagnitudePlot.Axes.YTick = dbPerDiv;
        PhasePlot.CustomTitleOn = true;
        PhasePlot.CustomTitle = "Phase and group delay";
        PhasePlot.Autoscale(force: true);
        Announce();
    }

    private void ClearPlots()
    {
        MagnitudePlot.Traces.Clear();
        PhasePlot.Traces.Clear();
        Announce();
    }

    private void Announce()
    {
        MagnitudeContainer.OnPlotChanged(this, EventArgs.Empty);
        PhaseContainer.OnPlotChanged(this, EventArgs.Empty);
        MagnitudeContainer.RequestPlotRedraw();
        PhaseContainer.RequestPlotRedraw();
    }

    private static FreqUnit FreqUnitOf(string unit) => unit switch
    {
        "Hz" => FreqUnit.Hz,
        "kHz" => FreqUnit.kHz,
        "MHz" => FreqUnit.MHz,
        _ => FreqUnit.GHz,
    };

    private static TraceProperties Style(int order)
    {
        int index = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        var props = new TraceProperties
        {
            LineColorIndex = index,
            MarkerColorIndex = index,
            FillColorIndex = index,
            Custom = false,
        };
        return props;
    }

    /// <summary>The netlist as text, for the clipboard and for Save.</summary>
    public string NetlistText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"* Filter Assistant  {Response} {Form} order {OrderText}");
        sb.Append(Cnl);
        return sb.ToString();
    }
}
