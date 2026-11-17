// brief-paa-9 R-paa9-1 — the Phased Array tool is a VIEW OF AN INSTANCE (D7): it holds no document of its own. Every edit it makes is a
// schematic command on the instance's parameters or on the VARs its steer() names, executed on the schematic's own undo stack, and it follows
// every edit made in the schematic (SchematicEditModel.Changed rebuilds the session from the schematic as Simulate would netlist it). So the
// schematic and the tool cannot disagree, and undo in either undoes both.
//
// A BEAM DRAG WRITES NOTHING UNTIL IT ENDS. Each step evaluates with the steering VARs as tunables (re-weighs, R-paa9-7), and the release
// writes the VARs once — one undo step (gate 2). Steps arriving while one evaluates are coalesced to the newest, so a slow evaluation drops
// steps rather than queueing them.
//
// The view model is framework-free: the window supplies IPhasedArrayHost (the UI thread and a worker), and the tests supply one that runs
// everything at once.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.PhasedArray;

/// <summary>Where the tool's work runs: <see cref="Post"/> on the UI thread, <see cref="Run"/> off it.</summary>
public interface IPhasedArrayHost
{
    void Post(Action action);
    void Run(Action work);
}

/// <summary>Runs everything where it is asked, at once: the tests' host, and UiSnapshot's.</summary>
public sealed class ImmediatePhasedArrayHost : IPhasedArrayHost
{
    public void Post(Action action) => action();
    public void Run(Action work) => work();
}

/// <summary>R-paa9-2 — what the aperture is coloured by.</summary>
public enum ApertureQuantity { Amplitude, Phase, GammaActive, DeliveredPower, PaPout, PaPae, ChannelNf }

/// <summary>The tool's pages, one toolbar button each. Channel is offered only when the array ends a Propagation Channel.</summary>
public enum ArrayPage { Beam, Channel, ScanMaps, TapersAndBits, Failures, Frequency }

/// <summary>One readout row.</summary>
public sealed record ReadoutRow(string Label, string Value);

/// <summary>A choice in a picker.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>brief-paa-9 — the Phased Array tool's view model.</summary>
public sealed partial class PhasedArrayToolViewModel : ObservableObject, IDisposable
{
    private readonly Func<SchematicViewModel, PreparedCircuit?> _prepare;
    private readonly IPhasedArrayHost _host;
    private readonly object _gate = new();
    private PhasedArraySession? _session;
    private bool _busy;
    private (IReadOnlyDictionary<string, string> Tunables, bool Interactive, (double, double)? Steer)? _pending;
    private CancellationTokenSource? _scan;
    private bool _dragging;
    private (double Theta, double Phi) _dragAt;
    private bool _rebuildQueued;
    private bool _disposed;

    public PhasedArrayToolViewModel(SchematicViewModel schematic, EditableComponent instance,
                                    Func<SchematicViewModel, PreparedCircuit?> prepare, IPhasedArrayHost host)
    {
        Schematic = schematic;
        Instance = instance;
        _prepare = prepare;
        _host = host;
        schematic.EditModel.Changed += OnSchematicChanged;
        foreach (var c in BaseQuantities) Quantities.Add(c);   // listed before any frame: the picker is bound before the first one arrives
        ReadTaper();
        Rebuild();
    }

    public SchematicViewModel Schematic { get; }
    public EditableComponent Instance { get; }

    public string Title => $"Phased Array — {Instance.InstanceName}";

    /// <summary>The current frame; null before the first evaluation or when there is none.</summary>
    [ObservableProperty] private PhasedArrayFrame? _frame;

    /// <summary>Why there is no frame, or the run's own status.</summary>
    [ObservableProperty] private string _status = "Evaluating…";

    /// <summary>The steering: draggable or not, and why not.</summary>
    [ObservableProperty] private PhasedArraySteering _steering = new("", "", null, null, PhasedArraySteering.NotSteered);

    /// <summary>The page shown.</summary>
    [ObservableProperty] private ArrayPage _page = ArrayPage.Beam;

    [ObservableProperty] private ApertureQuantity _quantity = ApertureQuantity.Amplitude;
    [ObservableProperty] private string _hover = "";
    [ObservableProperty] private bool _cutsPolar;
    [ObservableProperty] private double _scanLimitDeg = 60;
    [ObservableProperty] private IReadOnlyList<ScanPoint>? _scanPoints;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private FailureTrials? _failures;
    [ObservableProperty] private string _failureSummary = "";
    [ObservableProperty] private double _failureFraction = 0.05;
    [ObservableProperty] private int _failureTrialCount = 20;
    [ObservableProperty] private FrequencyPointing? _frequency;
    [ObservableProperty] private string _taperFamily = "Uniform";
    [ObservableProperty] private double _taperLevel = -30;
    [ObservableProperty] private int _taperNbar = 4;
    [ObservableProperty] private string _phaseBits = "";
    [ObservableProperty] private string _attenStep = "";

    /// <summary>Elements selected by a drag on the aperture, 1-based.</summary>
    public ObservableCollection<int> Selected { get; } = [];

    public ObservableCollection<ReadoutRow> Readouts { get; } = [];

    public ObservableCollection<Choice<ApertureQuantity>> Quantities { get; } = [];

    public static IReadOnlyList<string> TaperFamilies { get; } = ["Uniform", "Taylor", "Chebyshev", "Cosine^q"];

    /// <summary>The phase-shifter resolutions offered, and the attenuator steps, "Continuous" for none.</summary>
    public static IReadOnlyList<string> PhaseBitChoices { get; } = ["Continuous", "1", "2", "3", "4", "5", "6", "7", "8"];
    public static IReadOnlyList<string> AttenStepChoices { get; } = ["Continuous", "0.25", "0.5", "1", "2"];

    /// <summary><see cref="PhaseBits"/> as a choice.</summary>
    public string PhaseBitsChoice
    {
        get => PhaseBits.Trim() is "" or "0" ? "Continuous" : PhaseBits.Trim();
        set { PhaseBits = value == "Continuous" ? "" : value; OnPropertyChanged(); }
    }

    /// <summary><see cref="AttenStep"/> as a choice.</summary>
    public string AttenStepChoice
    {
        get => AttenStep.Trim() is "" or "0" ? "Continuous" : AttenStep.Trim();
        set { AttenStep = value == "Continuous" ? "" : value; OnPropertyChanged(); }
    }

    /// <summary>True while a scan map runs.</summary>
    public bool ScanRunning => _scan is not null;

    /// <summary>True while the beam is being dragged.</summary>
    public bool Dragging => _dragging;

    // ── work in progress, for the window's progress bar ─────────────────────────────────────────────────────

    private int _jobs;

    /// <summary>True while any of the tool's work runs: an evaluation, the scan maps, the failure trials, the frequency study. The window
    /// shows its bar only once this has stayed true for a while, so quick work never flashes one.</summary>
    [ObservableProperty] private bool _working;

    /// <summary>How far the work is, 0-1, or NaN when it cannot say (the bar is then indeterminate).</summary>
    [ObservableProperty] private double _workProgress = double.NaN;

    /// <summary>What is running, beside the bar.</summary>
    [ObservableProperty] private string _workLabel = "";

    /// <summary>True while the scan maps compute. Their bar is beside the Scan maps page's status, not the window's: the window's bar is
    /// for the rest of the work.</summary>
    [ObservableProperty] private bool _scanWorking;

    /// <summary>How far the scan maps are, 0-1.</summary>
    [ObservableProperty] private double _scanProgress;

    /// <summary>On the UI thread: a job starts.</summary>
    private void BeginJob(string label)
    {
        _jobs++;
        WorkLabel = label;
        WorkProgress = double.NaN;
        Working = true;
    }

    /// <summary>On the UI thread: a job ended, finished or cancelled.</summary>
    private void EndJob()
    {
        if (--_jobs > 0) return;
        _jobs = 0;
        Working = false;
        WorkProgress = double.NaN;
    }

    /// <summary>Raised whenever anything the canvases draw has changed.</summary>
    public event EventHandler? Redraw;

    /// <summary>The (θ₀, φ₀) the weights steer to, degrees, as the schematic resolves them; null when not steered.</summary>
    public (double ThetaDeg, double PhiDeg)? SteerAngles { get; private set; }

    // ── following the schematic ────────────────────────────────────────────────────────────────────────────

    private void OnSchematicChanged(object? sender, EventArgs e)
    {
        CancelScan();
        // the map was the bench before this edit: it is formed again for the bench after it
        if (ScanPoints is not null) { ScanPoints = null; ScanStatus = "Cancelled by an edit."; }
        if (_rebuildQueued || _dragging) return;
        _rebuildQueued = true;
        _host.Post(() => { _rebuildQueued = false; Rebuild(); });
    }

    /// <summary>The instance is still in its schematic.</summary>
    public bool InstancePresent => Schematic.EditModel.Components.Contains(Instance);

    /// <summary>Reads the schematic again: the steering, the taper fields, a new session, and a settled frame.</summary>
    public void Rebuild()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(Title));
        if (!InstancePresent)
        {
            Status = $"'{Instance.InstanceName}' is no longer in the schematic.";
            Frame = null;
            Redraw?.Invoke(this, EventArgs.Empty);
            return;
        }
        Steering = PhasedArraySteering.Of(Instance, Schematic.EditModel.Components);
        SteerAngles = ResolvedSteer();
        ReadTaper();
        var circuit = _prepare(Schematic);
        _session = circuit is null ? null : new PhasedArraySession(circuit, Instance.InstanceName);
        if (_session?.Refusal is { } why || circuit is null)
        {
            Status = _session?.Refusal ?? "The schematic could not be netlisted.";
            Frame = null;
            Redraw?.Invoke(this, EventArgs.Empty);
            return;
        }
        Request(new Dictionary<string, string>(), interactive: false, SteerAngles);
    }

    /// <summary>θ₀ and φ₀ in degrees from the steer()'s two arguments, resolved against the schematic's VARs; null when either does not.</summary>
    private (double, double)? ResolvedSteer()
    {
        if (Steering.Theta is null || Steering.Phi is null) return null;
        var nominal = SchematicNominal.Of(Schematic.EditModel.Components);
        double? t = nominal.Real(Steering.Theta.Text, null), p = nominal.Real(Steering.Phi.Text, null);
        return t is { } tt && p is { } pp ? (tt * 180 / Math.PI, pp * 180 / Math.PI) : null;
    }

    // ── evaluation, coalesced ───────────────────────────────────────────────────────────────────────────────

    private void Request(IReadOnlyDictionary<string, string> tunables, bool interactive, (double, double)? steer)
    {
        lock (_gate)
        {
            if (_busy) { _pending = (tunables, interactive, steer); return; }
            _busy = true;
        }
        BeginJob("Evaluating");
        var session = _session;
        _host.Run(() => Work(session, tunables, interactive, steer));
    }

    private void Work(PhasedArraySession? session, IReadOnlyDictionary<string, string> tunables, bool interactive, (double, double)? steer)
    {
        while (true)
        {
            // brief-pch-9 — a settled frame's stages (a channel's beam selection) on the status line while it runs
            var result = session?.Evaluate(tunables, interactive, steer, progress: interactive ? null : new PostedProgress(_host, p =>
            {
                if (!_disposed && p.Stage.Length > 0) Status = "Evaluating · " + PhasedArrayToolViewModel.Describe(p);
            }));
            _host.Post(() => Show(result));
            lock (_gate)
            {
                if (_pending is not { } next) { _busy = false; _host.Post(EndJob); return; }
                _pending = null;
                (tunables, interactive, steer) = next;
                session = _session;
            }
        }
    }

    private void Show(PhasedArrayEvaluation? result)
    {
        if (_disposed || result is null) return;
        if (result.Frame is null)
        {
            if (result.Error != "Cancelled.") Status = result.Error ?? "";
            return;
        }
        Frame = result.Frame;
        var st = result.Frame.State;
        // The frame time measures the machine, so a documentation capture leaves it out (src/Ui/Diagnostics/CLAUDE.md).
        string time = CircuitRF.Ui.Diagnostics.UiArtworkGenerator.HeadlessCapture ? "" : $" · {result.Frame.EvaluationMs + result.Frame.FormingMs:0} ms";
        Status = $"{st.Lattice.Rows} × {st.Lattice.Cols} at {st.Hz / 1e9:0.###} GHz{time}" + (st.Estimated ? " · estimated" : "");
        RefreshQuantities();
        RefreshReadouts();
        if (!_dragging) RefreshChannel();   // brief-pch-9 — the channel this array ends, scored again for the bench as it now stands
        Redraw?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A settled frame of the schematic as it stands.</summary>
    public void Refresh() => Request(new Dictionary<string, string>(), interactive: false, SteerAngles);

    // ── R-paa9-3: the beam drag ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts a drag of the beam marker; false (and nothing happens) when the steering cannot be written.</summary>
    public bool BeginDrag()
    {
        if (!Steering.Draggable || _session is null) return false;
        CancelScan();
        _dragging = true;
        _dragAt = SteerAngles ?? (0, 0);
        return true;
    }

    /// <summary>The marker at direction cosines (u, v): clamped inside the visible circle, evaluated with the steering at that direction.</summary>
    public void DragTo(double u, double v)
    {
        if (!_dragging) return;
        double r = Math.Sqrt(u * u + v * v);
        if (r > 0.999) { u *= 0.999 / r; v *= 0.999 / r; r = 0.999; }
        double theta = Math.Asin(r) * 180 / Math.PI, phi = r < 1e-12 ? _dragAt.Phi : Math.Atan2(v, u) * 180 / Math.PI;
        DragToAngles(theta, phi);
    }

    /// <summary>The drag at (θ₀, φ₀) in degrees.</summary>
    public void DragToAngles(double thetaDeg, double phiDeg)
    {
        if (!_dragging) return;
        _dragAt = (Math.Round(thetaDeg, 2), Math.Round(phiDeg, 2));
        SteerAngles = _dragAt;
        Request(Steering.Tunables(Instance.InstanceName, Instance.Parameters, _dragAt.Theta, _dragAt.Phi), interactive: true, _dragAt);
    }

    /// <summary>Ends the drag: writes the steering's VARs (or literals) as ONE undo step, and the schematic's change re-reads the tool.</summary>
    public void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        var model = Schematic.EditModel;
        IUiCommand? cmd = null;
        foreach (var (row, expr, unit) in Steering.VarWrites(model.Components, _dragAt.Theta, _dragAt.Phi))
            cmd = Then(cmd, new EditParameterCommand(model, row, expr, unit));
        if (Steering.Theta is { IsVar: false } || Steering.Phi is { IsVar: false })
            cmd = Then(cmd, Write(Steering.Field, Steering.Rewritten(Instance.Parameters, _dragAt.Theta, _dragAt.Phi)));
        if (cmd is not null) Schematic.Execute(new NamedCommand($"Steer {Instance.InstanceName} to θ0 {_dragAt.Theta:0.##}°, φ0 {_dragAt.Phi:0.##}°", cmd));
    }

    // ── R-paa9-2: the aperture ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The elements switched off, 1-based, as the instance's Off states them.</summary>
    public SortedSet<int> OffSet()
    {
        var set = new SortedSet<int>();
        string text = Instance.Parameters.FirstOrDefault(p => p.Name == "Off")?.Expression.Trim().Trim('"') ?? "";
        foreach (string t in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && k >= 1) set.Add(k);
        return set;
    }

    /// <summary>Click: element <paramref name="n"/> (1-based) switched off, or on again — one undo step writing Off.</summary>
    public void ToggleElement(int n)
    {
        var off = OffSet();
        if (!off.Remove(n)) off.Add(n);
        WriteOff(off, $"Switch element {n} {(off.Contains(n) ? "off" : "on")}");
    }

    /// <summary>The selected elements switched off (or on), one undo step.</summary>
    [RelayCommand]
    private void SwitchSelectedOff() => SetSelected(true);

    [RelayCommand]
    private void SwitchSelectedOn() => SetSelected(false);

    private void SetSelected(bool off)
    {
        if (Selected.Count == 0) return;
        var set = OffSet();
        foreach (int n in Selected) { if (off) set.Add(n); else set.Remove(n); }
        WriteOff(set, $"Switch {Selected.Count} elements {(off ? "off" : "on")}");
    }

    /// <summary>
    /// Q, as Q disables a component in the schematic: the selected elements switched off, or on again when every one of them is
    /// already off; with nothing selected, the element under the pointer. One undo step. False when there was nothing to switch.
    /// </summary>
    public bool ToggleSelectedOrHovered()
    {
        if (Selected.Count > 0)
        {
            var off = OffSet();
            SetSelected(!Selected.All(off.Contains));
            return true;
        }
        if (_hoverElement < 1) return false;
        ToggleElement(_hoverElement);
        return true;
    }

    private int _hoverElement;

    /// <summary>Esc: no element selected. False when none was.</summary>
    public bool ClearSelection()
    {
        if (Selected.Count == 0) return false;
        Selected.Clear();
        Redraw?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>An export's outcome, on the status line until the next frame replaces it.</summary>
    public void ReportExport(string text) => Status = text;

    /// <summary>The readouts as tab-separated lines, for the clipboard.</summary>
    public string ReadoutsText() => string.Join(Environment.NewLine, Readouts.Select(r => $"{r.Label}\t{r.Value}"));

    private void WriteOff(SortedSet<int> off, string description)
        => Schematic.Execute(new NamedCommand(description, Write("Off", string.Join(",", off))));

    /// <summary>The aperture's value per element for the chosen quantity (NaN where it has none), and its unit.</summary>
    public (double[] Values, string Unit) ApertureValues()
    {
        if (Frame is not { } f) return ([], "");
        var st = f.State;
        int n = st.Lattice.Count;
        var values = new double[n];
        Array.Fill(values, double.NaN);
        var w = st.Quantised ?? st.A;
        double maxAmp = w.Where((_, e) => st.Lattice.IsOn(e)).Select(c => c.Magnitude).DefaultIfEmpty(0).Max();
        string unit = "";
        for (int e = 0; e < n; e++)
        {
            if (!st.Lattice.IsOn(e)) continue;
            switch (Quantity)
            {
                case ApertureQuantity.Amplitude: values[e] = maxAmp > 0 ? w[e].Magnitude / maxAmp : 0; unit = "linear"; break;
                case ApertureQuantity.Phase: values[e] = w[e] == Complex.Zero ? double.NaN : w[e].Phase * 180 / Math.PI; unit = "°"; break;
                case ApertureQuantity.GammaActive: values[e] = st.ActiveGamma(e)?.Magnitude ?? double.NaN; unit = "|Γact|"; break;
                case ApertureQuantity.DeliveredPower:
                    double p = st.AcceptedBy(e);
                    values[e] = p > 0 ? 10 * Math.Log10(p * 1000) : double.NaN; unit = "dBm"; break;
                default:
                    var (pattern, u) = MeasurementPattern(Quantity);
                    values[e] = PerElement(f.Measurements, pattern, e + 1) ?? double.NaN; unit = u; break;
            }
        }
        return (values, unit);
    }

    private static (Regex Pattern, string Unit) MeasurementPattern(ApertureQuantity q) => q switch
    {
        ApertureQuantity.PaPout => (new Regex(@"^Pout_?(\d+)(_dBm)?$", RegexOptions.IgnoreCase), "dBm"),
        ApertureQuantity.PaPae => (new Regex(@"^PAE_?(\d+)$", RegexOptions.IgnoreCase), "%"),
        _ => (new Regex(@"^NF_?(\d+)(_dB)?$", RegexOptions.IgnoreCase), "dB"),
    };

    /// <summary>A measurement named for element <paramref name="n"/> (Pout3_dBm, PAE3, NF3), when the bench has one.</summary>
    private static double? PerElement(IReadOnlyDictionary<string, double> m, Regex pattern, int n)
    {
        foreach (var (name, v) in m)
            if (pattern.Match(name) is { Success: true } hit && int.Parse(hit.Groups[1].Value, CultureInfo.InvariantCulture) == n) return v;
        return null;
    }

    /// <summary>What every array can be coloured by, whatever its bench measures.</summary>
    private static readonly Choice<ApertureQuantity>[] BaseQuantities =
    [
        new(ApertureQuantity.Amplitude, "Amplitude"), new(ApertureQuantity.Phase, "Phase (quantised)"),
        new(ApertureQuantity.GammaActive, "|Γ active|"), new(ApertureQuantity.DeliveredPower, "Delivered power"),
    ];

    /// <summary>
    /// The picker's list for this frame: the base four, then a per-element measurement the bench makes. Changed IN PLACE and never
    /// cleared — the four are listed from the constructor and stay — and the selection is raised again after any change, because the
    /// picker resolves its selection only against the items it holds when the selection arrives (src/Ui/CLAUDE.md): a selection pushed
    /// before its item, or an item list rebuilt under it, leaves the picker blank with no error anywhere.
    /// </summary>
    private void RefreshQuantities()
    {
        var want = BaseQuantities.ToList();
        foreach (var (q, label) in new[] { (ApertureQuantity.PaPout, "PA Pout"), (ApertureQuantity.PaPae, "PA PAE"), (ApertureQuantity.ChannelNf, "Channel NF") })
            if (Frame is { } f && f.Measurements.Keys.Any(k => MeasurementPattern(q).Pattern.IsMatch(k))) want.Add(new(q, label));
        if (want.Select(c => c.Value).SequenceEqual(Quantities.Select(c => c.Value))) return;
        for (int i = Quantities.Count - 1; i >= 0; i--)
            if (!want.Any(c => c.Value == Quantities[i].Value)) Quantities.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
            if (i >= Quantities.Count || Quantities[i].Value != want[i].Value) Quantities.Insert(i, want[i]);
        if (!want.Any(c => c.Value == Quantity)) Quantity = ApertureQuantity.Amplitude;
        else OnPropertyChanged(nameof(Quantity));
    }

    /// <summary>Hover over element <paramref name="n"/> (1-based; 0 clears): its number, row, column, position and the coloured value.</summary>
    public void HoverElement(int n)
    {
        _hoverElement = n;
        if (Frame is not { } f || n < 1 || n > f.State.Lattice.Count) { Hover = ""; return; }
        var lattice = f.State.Lattice;
        var (row, col) = lattice.RowCol(n);
        var (x, y) = lattice.Position(n - 1);
        var (values, unit) = ApertureValues();
        string v = !lattice.IsOn(n - 1) ? "off" : double.IsNaN(values[n - 1]) ? "—" : $"{values[n - 1]:0.###} {unit}";
        Hover = $"n {n} · row {row}, col {col} · x {x * 1e3:0.##} mm, y {y * 1e3:0.##} mm · {v}";
    }

    /// <summary>The shown page's name, beside the page buttons.</summary>
    public string PageTitle => Page switch
    {
        ArrayPage.Channel => "Channel",
        ArrayPage.ScanMaps => "Scan maps",
        ArrayPage.TapersAndBits => "Tapers and bits",
        ArrayPage.Failures => "Failures",
        ArrayPage.Frequency => "Frequency",
        _ => "Beam",
    };

    partial void OnPageChanged(ArrayPage value) => OnPropertyChanged(nameof(PageTitle));

    partial void OnQuantityChanged(ApertureQuantity value) => Redraw?.Invoke(this, EventArgs.Empty);
    partial void OnCutsPolarChanged(bool value) => Redraw?.Invoke(this, EventArgs.Empty);
    partial void OnScanLimitDegChanged(double value) => Redraw?.Invoke(this, EventArgs.Empty);

    // ── R-paa9-4: readouts ──────────────────────────────────────────────────────────────────────────────────

    private void RefreshReadouts()
    {
        Readouts.Clear();
        if (Frame is not { } f) return;
        var r = f.Readouts;
        string Db(double v, string unit) => double.IsFinite(v) ? $"{v:0.00} {unit}" : "—";
        void Row(string label, string value) => Readouts.Add(new ReadoutRow(label, value));
        Row("Directivity", Db(r.DirectivityDbi, "dBi"));
        Row("Gain", Db(r.GainDbi, "dBi"));
        Row("Realized gain", Db(r.RealizedGainDbi, "dBi"));
        if (r.GoverTDbK is { } gt) Row("G/T", Db(gt, "dB/K"));
        else if (r.EirpDbm is { } eirp) Row("EIRP", Db(eirp, "dBm"));
        Row("Peak", $"θ {r.PeakThetaDeg:0.00}°, φ {r.PeakPhiDeg:0.00}°");
        Row("Beamwidth (elevation)", $"{r.ElevationBeamwidthDeg:0.00}°");
        Row("Beamwidth (azimuth)", $"{r.AzimuthBeamwidthDeg:0.00}°");
        Row("Sidelobe level", r.SidelobeDb is { } sl ? Db(sl, "dB") : "—");
        Row("Pointing error", r.PointingErrorDeg is { } pe ? $"{pe:0.000}°" : "—");
        Row("Scan loss", Db(r.ScanLossDb, "dB"));
        Row("Grating-lobe margin", double.IsPositiveInfinity(r.GratingMarginDeg) ? "none" : $"{r.GratingMarginDeg:0.0}°");
        Row("Worst |Γ active|", $"{r.WorstGammaActive:0.000}");
        Row("Power balance", $"{r.PowerBalance:0.0000}");
        Row("Level", r.Level);
    }

    // ── R-paa9-5: scan maps ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the scan map in the background (cancelling one already running); the tab says why when it cannot run.</summary>
    [RelayCommand]
    public void StartScan()
    {
        CancelScan();
        if (Frame is not { } frame || _session is not { } session) { ScanStatus = "Nothing to scan yet."; return; }
        if (!Steering.Draggable) { ScanStatus = $"No scan map: {Steering.Refusal}"; return; }
        var cts = new CancellationTokenSource();
        _scan = cts;
        ScanPoints = null;
        ScanStatus = "Scanning…";
        var partial = new List<ScanPoint>();
        double limit = ScanLimitDeg;
        // the directions the map steps through, as ScanMap counts them: one at boresight, then a ring of 360/15 per θ0
        int total = 1 + (int)Math.Floor(limit / 5 + 1e-9) * 24;
        ScanProgress = 0;
        ScanWorking = true;
        var steering = Steering;
        var parameters = Instance.Parameters.Select(p => p.Clone()).ToList();
        string name = Instance.InstanceName;
        _host.Run(() =>
        {
            var (points, refusal) = session.ScanMap(frame, (t, p) => steering.Tunables(name, parameters, t, p), limit, 5, 15,
                                                    p =>
                                                    {
                                                        int done;
                                                        lock (partial) { partial.Add(p); done = partial.Count; }
                                                        _host.Post(() => { if (!cts.IsCancellationRequested) ScanProgress = Math.Min(1, done / (double)total); });
                                                    }, cts.Token);
            _host.Post(() =>
            {
                if (ReferenceEquals(_scan, cts) || _scan is null) ScanWorking = false;   // a newer scan keeps its own bar
                if (cts.IsCancellationRequested) return;
                if (ReferenceEquals(_scan, cts)) _scan = null;
                ScanPoints = points;
                ScanStatus = refusal ?? $"{points!.Count} directions to θ0 {limit:0}°";
                Redraw?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    /// <summary>Cancels a running scan map; any edit does.</summary>
    public void CancelScan()
    {
        if (_scan is { } s) { s.Cancel(); _scan = null; ScanStatus = "Cancelled by an edit."; ScanWorking = false; }
    }

    // ── R-paa9-6: tapers, bits, failures, frequency ───────────────────────────────────────────────────────────

    private void ReadTaper()
    {
        string amp = Instance.Parameters.FirstOrDefault(p => p.Name == "Amp")?.Expression.Trim() ?? "";
        var m = Regex.Match(amp, @"^(taylor|chebyshev|cosq)\(\s*(-?[\d.]+)\s*(?:,\s*(\d+)\s*)?\)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            TaperFamily = m.Groups[1].Value.ToLowerInvariant() switch { "taylor" => "Taylor", "chebyshev" => "Chebyshev", _ => "Cosine^q" };
            TaperLevel = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[3].Success) TaperNbar = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        else if (amp is "" or "1") TaperFamily = "Uniform";
        PhaseBits = Instance.Parameters.FirstOrDefault(p => p.Name == "PhaseBits")?.Expression ?? "";
        AttenStep = Instance.Parameters.FirstOrDefault(p => p.Name == "AttenStep")?.Expression ?? "";
        OnPropertyChanged(nameof(PhaseBitsChoice));
        OnPropertyChanged(nameof(AttenStepChoice));
    }

    /// <summary>The Amp expression the taper fields describe.</summary>
    public string TaperExpression() => TaperFamily switch
    {
        "Taylor" => $"taylor({Num(TaperLevel)},{TaperNbar})",
        "Chebyshev" => $"chebyshev({Num(TaperLevel)})",
        "Cosine^q" => $"cosq({Num(TaperLevel)})",
        _ => "1",
    };

    private static string Num(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Writes the taper to Amp, and the phase bits and attenuator step, as one undo step.</summary>
    [RelayCommand]
    public void ApplyTaperAndBits()
    {
        IUiCommand? cmd = null;
        string amp = TaperExpression();
        string current = Instance.Parameters.FirstOrDefault(p => p.Name == "Amp")?.Expression.Trim() ?? "";
        if (amp != current && !(amp == "1" && current == "")) cmd = Then(cmd, Write("Amp", amp));
        if ((Instance.Parameters.FirstOrDefault(p => p.Name == "PhaseBits")?.Expression ?? "") != PhaseBits.Trim()) cmd = Then(cmd, Write("PhaseBits", PhaseBits.Trim()));
        if ((Instance.Parameters.FirstOrDefault(p => p.Name == "AttenStep")?.Expression ?? "") != AttenStep.Trim()) cmd = Then(cmd, Write("AttenStep", AttenStep.Trim()));
        if (cmd is not null) Schematic.Execute(new NamedCommand($"Taper and bits of {Instance.InstanceName}", cmd));
    }

    /// <summary>R-paa9-6 — the failure study on the current frame, in memory.</summary>
    [RelayCommand]
    public void RunFailures()
    {
        if (Frame is not { } frame) return;
        double fraction = FailureFraction;
        int trials = Math.Clamp(FailureTrialCount, 1, 500);
        FailureSummary = "Running…";
        BeginJob("Failure trials");
        _host.Run(() =>
        {
            var result = PhasedArraySession.Failures(frame, fraction, trials, seed: 1);
            _host.Post(() =>
            {
                EndJob();
                Failures = result;
                var g = result.GainChangeDb;
                var s = result.SidelobeDb.Where(double.IsFinite).ToArray();
                FailureSummary = $"{result.Off} of {frame.State.Lattice.Count - frame.State.Lattice.Off.Count} off, {g.Length} trials · gain {g.Average():0.00} dB " +
                                 $"(worst {g.Min():0.00}) · sidelobe {(s.Length > 0 ? $"{s.Average():0.0} dB (worst {s.Max():0.0})" : "—")}";
                Redraw?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    /// <summary>R-paa9-6 — pointing against frequency for the current frame.</summary>
    [RelayCommand]
    public void RunFrequency()
    {
        if (Frame is not { } frame) return;
        BeginJob("Frequency study");
        _host.Run(() =>
        {
            var result = PhasedArraySession.Frequency(frame);
            _host.Post(() => { EndJob(); Frequency = result; Redraw?.Invoke(this, EventArgs.Empty); });
        });
    }

    // ── writes ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The command setting the instance's parameter <paramref name="name"/> to <paramref name="expression"/>, adding the row when the
    /// instance has none.</summary>
    private IUiCommand Write(string name, string expression)
    {
        var model = Schematic.EditModel;
        if (Instance.Parameters.FirstOrDefault(p => p.Name == name) is { } row)
            return new EditParameterCommand(model, row, expression, row.Unit);
        return new AddParameterCommand(model, Instance, new EditableParameter { Name = name, Expression = expression, ShowOnSchematic = false });
    }

    private static IUiCommand Then(IUiCommand? first, IUiCommand next) => first is null ? next : new CompositeCommand(first, next);

    /// <summary>A command under the description the Edit menu shows.</summary>
    private sealed class NamedCommand(string description, IUiCommand inner) : IUiCommand
    {
        public string Description => description;
        public void Execute() => inner.Execute();
        public void Undo() => inner.Undo();
    }

    public void Dispose()
    {
        _disposed = true;
        CancelScan();
        _channelCancel?.Cancel();   // brief-pch-9 — a scoring of the channel still running
        Schematic.EditModel.Changed -= OnSchematicChanged;
    }
}
