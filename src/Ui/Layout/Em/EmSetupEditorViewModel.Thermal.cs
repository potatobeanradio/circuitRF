// brief-em3d-75 R-em3d75-3 — the Setups dialog's THERMAL page: a 3D view's setup whose Problem is Thermal (brief 73 D1 — only a
// .c3d embeds one). Top to bottom: Sources (each heat source's default and this setup's override), Boundaries (the list the
// face menu writes, and All exposed faces), Currents (brief-em3d-77: a DC current per port, and the wires' convection and bond;
// brief-em3d-78: harmonics per port or per array, each Peak or Rms, and the DC-equivalent RMS as a readout, never an input),
// Sweep (up to two variables), Measures (parsed as typed, each error beside it), Mesh, Balance (k(T), and σ(T) from brief 77),
// the Rth matrix, Z_th and a pulse train (brief-em3d-80),
// and the size estimate `explain` gives (brief 74 §6).
//
// NOTHING LIVES ONLY HERE (R-em-11): every control writes the setup's Thermal section through the panel's one CommitEdit, so
// an edit is one undo entry of the .c3d and the dialog shows exactly what the file says. The document (its heat sources,
// probes and variables) is read through ThermalContext, which the 3D editor hands the panel; a .cem panel has none and is
// never thermal.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>What the thermal page reads of the document it is embedded in: the document, its resolution and elaboration; and
/// (brief-em3d-79) its path and the results folder, which a circuit link resolves against.</summary>
public sealed record EmThermalContext(Func<C3dDocument> Document, Func<C3dResolution?> Resolution, Func<C3dElaboration?> Elaboration,
                                      Func<string?>? Path = null, Func<string?>? ResultsRoot = null);

/// <summary>brief-em3d-79 R-em3d79-1 — one row of a circuit link's read-only mapping: port p is pin p, on this net.</summary>
public sealed record ThermalCircuitMapRow(int Port, string PortName, string Net)
{
    public string Text => $"port {Port} ({PortName}) ↔ pin {Port} ↔ net '{Net}'";
}

/// <summary>One heat source on the page: its document default, and this setup's override (empty: the default).</summary>
public sealed partial class ThermalSourceRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Default { get; init; }
    [ObservableProperty] private string _override = "";
}

/// <summary>One boundary row: a face, its kind, and its values as typed.</summary>
public sealed partial class ThermalBoundaryRow : ObservableObject
{
    [ObservableProperty] private string _face = "";
    [ObservableProperty] private ThermalBoundaryKind _kind;
    [ObservableProperty] private string _tempC = "";
    [ObservableProperty] private string _h = "";
    [ObservableProperty] private string _ambientC = "";

    public bool IsFixed => Kind == ThermalBoundaryKind.FixedT;
    public bool IsConvection => Kind == ThermalBoundaryKind.Convection;

    partial void OnKindChanged(ThermalBoundaryKind value)
    {
        OnPropertyChanged(nameof(IsFixed));
        OnPropertyChanged(nameof(IsConvection));
    }
}

/// <summary>brief-em3d-77 R-em3d77-1a — one port's current as typed: the port, the DC amperes (an expression), and the faces it
/// enters and leaves by when not inferred.</summary>
public sealed partial class ThermalCurrentRow : ObservableObject
{
    [ObservableProperty] private string _port = "1";
    [ObservableProperty] private string _dc = "";
    [ObservableProperty] private string _enterFace = "";
    [ObservableProperty] private string _leaveFace = "";

    /// <summary>brief-em3d-78 R-em3d78-2 — a wire array's name, in place of the port.</summary>
    [ObservableProperty] private string _array = "";

    /// <summary>R-em3d78-1 — the fundamental, Hz, and the harmonics as <c>1: I1 Peak; 2: 0.12 Rms</c>.</summary>
    [ObservableProperty] private string _f0 = "";
    [ObservableProperty] private string _harmonics = "";

    /// <summary>R-em3d78-1 — the DC-equivalent RMS, a readout (empty with no harmonics).</summary>
    [ObservableProperty] private string _dcEquivalent = "";
}

/// <summary>One sweep axis as typed.</summary>
public sealed partial class ThermalSweepRow : ObservableObject
{
    [ObservableProperty] private string _var = "";
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _stop = "";
    [ObservableProperty] private string _points = "1";
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>The 3D view's document changed (a heat source drawn, renamed or deleted): the page reads it again.</summary>
    public void RefreshThermal() { if (IsThermalSetup) SyncThermalFields(); }

    /// <summary>Set by the 3D editor: what the thermal page reads of the document. Setting it fills the page.</summary>
    public EmThermalContext? ThermalContext
    {
        get => _thermalContext;
        set { _thermalContext = value; SyncThermalFields(); }
    }

    private EmThermalContext? _thermalContext;

    /// <summary>The thermal page is shown: an embedded setup whose problem is Thermal.</summary>
    public bool IsThermalSetup => IsEmbedded && Working.Problem3D == Em3dProblemType.Thermal;

    /// <summary>What an EM setup reads and a thermal one does not (the solver, the frequency, the conductors, the options).</summary>
    public bool IsNotThermalSetup => !IsThermalSetup;

    public ObservableCollection<ThermalSourceRow> ThermalSources { get; } = [];
    public ObservableCollection<ThermalBoundaryRow> ThermalBoundaries { get; } = [];
    public ObservableCollection<ThermalSweepRow> ThermalSweeps { get; } = [];
    public ObservableCollection<ThermalCurrentRow> ThermalCurrents { get; } = [];

    // ── brief-em3d-79 R-em3d79-4 — Currents ▸ From circuit… ─────────────────────────────────────────────

    /// <summary>The ports' currents come from a circuit's HB power sweep.</summary>
    [ObservableProperty] private bool _thermalFromCircuit;
    /// <summary>The circuit, relative to the .c3d.</summary>
    [ObservableProperty] private string _thermalCircuitSchematic = "";
    /// <summary>Its HB analysis (or the sweep wrapping one); empty: the circuit's one HB chain.</summary>
    [ObservableProperty] private string _thermalCircuitAnalysis = "";
    /// <summary>The instance whose model is this view's result; empty: the one there is.</summary>
    [ObservableProperty] private string _thermalCircuitInstance = "";
    /// <summary>The HB cubes carried into the result, comma separated; empty: every scalar measure.</summary>
    [ObservableProperty] private string _thermalCircuitCarry = "";
    /// <summary>What does not resolve yet, or empty.</summary>
    [ObservableProperty] private string _thermalCircuitProblem = "";

    /// <summary>Only HB analyses and the sweeps wrapping them: "" first (the circuit's one chain).</summary>
    [ObservableProperty] private IReadOnlyList<string> _thermalCircuitAnalyses = [""];
    /// <summary>Only instances whose model is this view's result: "" first (the one there is).</summary>
    [ObservableProperty] private IReadOnlyList<string> _thermalCircuitInstances = [""];
    public ObservableCollection<ThermalCircuitMapRow> ThermalCircuitMapping { get; } = [];

    /// <summary>The link as the page reads it now, for the survey and the commit.</summary>
    private CemThermalFromCircuit CircuitLink() => new()
    {
        Schematic = ThermalCircuitSchematic.Trim(),
        Analysis = Blank(ThermalCircuitAnalysis),
        Instance = Blank(ThermalCircuitInstance),
        Carry = ThermalCircuitCarry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } c ? [.. c] : null,
    };

    /// <summary>Resolves the link without running anything (ThermalCircuitLink.Describe): the pickers' choices and the mapping.</summary>
    public void RefreshCircuitSurvey()
    {
        ThermalCircuitMapping.Clear();
        if (!ThermalFromCircuit || ThermalContext is not { } ctx || ctx.Path?.Invoke() is not { } path || ctx.ResultsRoot?.Invoke() is not { } root)
        {
            ThermalCircuitProblem = ThermalFromCircuit ? "Save the 3D view in a workspace first: the link resolves against its folder and results." : "";
            return;
        }
        var survey = ThermalCircuitLink.Describe(CircuitLink(), ctx.Document(), path, root);
        ThermalCircuitAnalyses = ["", .. survey.Analyses];
        ThermalCircuitInstances = ["", .. survey.Instances];
        foreach (var (port, name, net) in survey.Mapping) ThermalCircuitMapping.Add(new ThermalCircuitMapRow(port, name, net));
        ThermalCircuitProblem = survey.Problem ?? "";
    }

    /// <summary>R-em3d79-4 — a picked file, as the link stores it: relative to the .c3d's folder.</summary>
    public void SetCircuitSchematic(string absolutePath)
    {
        string? dir = ThermalContext?.Path?.Invoke() is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        ThermalCircuitSchematic = dir is null ? absolutePath : Path.GetRelativePath(dir, absolutePath).Replace('\\', '/');
        CommitThermal();
    }

    /// <summary>brief-em3d-77 R-em3d77-3 — a bond wire's heat loss in air (h and its ambient), and a bond's interface resistances.</summary>
    [ObservableProperty] private string _thermalWireConvectionH = "";
    [ObservableProperty] private string _thermalWireAmbient = "";
    [ObservableProperty] private string _thermalBondThermal = "";
    [ObservableProperty] private string _thermalBondElectrical = "";

    public static IReadOnlyList<ThermalBoundaryKind> ThermalBoundaryKinds { get; } = [ThermalBoundaryKind.FixedT, ThermalBoundaryKind.Convection];
    public static IReadOnlyList<int> ThermalOrders { get; } = [1, 2];
    public static IReadOnlyList<string> ThermalSolverChoices { get; } = ["Auto", "Direct", "Iterative"];

    /// <summary>All exposed faces: convection at h to T_amb from every face nothing else names.</summary>
    [ObservableProperty] private bool _thermalExposed;
    [ObservableProperty] private string _thermalExposedH = "";
    [ObservableProperty] private string _thermalExposedAmbient = "";

    /// <summary>The measures, one per line (<c>Rth = (Tmax(die) - Tavg(flange)) / Pdiss</c>).</summary>
    [ObservableProperty] private string _thermalMeasuresText = "";
    /// <summary>Each measure's error, as typed — parsed live, never written until the box loses focus.</summary>
    [ObservableProperty] private string _thermalMeasureErrors = "";

    [ObservableProperty] private int _thermalOrder = ThermalLowerings.DefaultOrder;
    [ObservableProperty] private string _thermalSizeFromSources = "";
    [ObservableProperty] private string _thermalMinThroughThickness = "";
    [ObservableProperty] private string _thermalGrading = "";
    [ObservableProperty] private string _thermalSolver = "Auto";
    [ObservableProperty] private bool _thermalCheck;
    [ObservableProperty] private bool _thermalKOfT = true;
    /// <summary>brief-em3d-77 R-em3d77-4b — electrical conductivity follows temperature in conductive balance.</summary>
    [ObservableProperty] private bool _thermalSigmaOfT = true;
    [ObservableProperty] private string _thermalTolerance = "";
    [ObservableProperty] private string _thermalMaxIterations = "";

    /// <summary>brief-em3d-76 R-em3d76-3a — a submodel: the whole-model thermal setup it is cut from ("" for none: this setup
    /// solves the whole model) and the mesh region it is cut to.</summary>
    [ObservableProperty] private string _thermalSubmodelFrom = "";
    [ObservableProperty] private string _thermalSubmodelRegion = "";

    /// <summary>The From picker's choices: "" (no submodel), then every OTHER thermal setup of the document that is not itself a
    /// submodel.</summary>
    public IReadOnlyList<string> ThermalSubmodelFromChoices
        => ["", .. (ThermalContext?.Document() is { } d ? C3dSetups.Read(d) : [])
                   .Where(s => s.Setup is { IsThermal: true } x && x.Name != Working.Name && x.Thermal?.Submodel is null).Select(s => s.Name)];

    /// <summary>The Region picker's choices: the document's mesh regions.</summary>
    public IReadOnlyList<string> ThermalSubmodelRegionChoices
        => [.. ThermalContext?.Document().MeshRegions.Select(r => r.Name) ?? []];

    public bool IsThermalSubmodel => ThermalSubmodelFrom is { Length: > 0 };

    // brief-em3d-80 — the Rth matrix, Z_th and the pulse train. A name list reads as typed, comma-separated; empty is "*".
    [ObservableProperty] private bool _thermalRthOn;
    [ObservableProperty] private string _thermalRthSources = "";
    [ObservableProperty] private bool _thermalRthMax;
    [ObservableProperty] private bool _thermalZthOn;
    [ObservableProperty] private string _thermalZthSources = "";
    [ObservableProperty] private string _thermalZthProbes = "";
    [ObservableProperty] private string _thermalZthStart = "";
    [ObservableProperty] private string _thermalZthStop = "";
    [ObservableProperty] private string _thermalZthPerDecade = "";
    [ObservableProperty] private bool _thermalPulseOn;
    [ObservableProperty] private string _thermalPulsePeriod = "";
    [ObservableProperty] private string _thermalPulseDuty = "";
    [ObservableProperty] private string _thermalPulsePeak = "";

    /// <summary>brief-em3d-80 — a name list as the page shows it: comma-separated, and empty for "*".</summary>
    private static string NamesText(List<string>? names) => names is null or [ThermalNamesConverter.All] ? "" : string.Join(", ", names);

    /// <summary>brief-em3d-80 — the typed list back: null (every source) when empty or "*".</summary>
    private static List<string>? Names(string text)
    {
        var list = text.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        return list.Count == 0 || list is [ThermalNamesConverter.All] ? null : list;
    }

    /// <summary>The page's refusal of what it was just given (a bad number), or null.</summary>
    [ObservableProperty] private string? _thermalError;

    /// <summary>Everything check says of this setup (C3dThermal's one validator), or empty.</summary>
    [ObservableProperty] private string _thermalFindings = "";

    /// <summary>R-em3d75-3 — the size estimate `explain` gives, updated as sizes change.</summary>
    [ObservableProperty] private string _thermalEstimate = "";

    public bool CanAddThermalSweep => ThermalSweeps.Count < C3dThermal.MaxSweepAxes;

    public bool HasThermalSources => ThermalSources.Count > 0;

    private bool _syncingThermal;

    /// <summary>Reads the Thermal section into the page's rows (on load, an undo, a redo).</summary>
    private void SyncThermalFields()
    {
        _syncingThermal = true;
        try
        {
            var t = Working.Thermal ?? new CemThermal();
            ThermalSources.Clear();
            var doc = ThermalContext?.Document();
            foreach (var h in doc?.HeatSources ?? [])
                ThermalSources.Add(new ThermalSourceRow
                {
                    Name = h.Name, Default = h.Power ?? "(none)",
                    Override = t.Sources?.FirstOrDefault(s => s.Name == h.Name)?.Power ?? "",
                });
            ThermalBoundaries.Clear();
            ThermalExposed = false;
            ThermalExposedH = ThermalExposedAmbient = "";
            foreach (var b in t.Boundaries ?? [])
            {
                if (b.Face == C3dThermal.ExposedFaces)
                {
                    ThermalExposed = true;
                    ThermalExposedH = b.H ?? "";
                    ThermalExposedAmbient = b.AmbientC ?? "";
                    continue;
                }
                ThermalBoundaries.Add(new ThermalBoundaryRow { Face = b.Face, Kind = b.Kind, TempC = b.TempC ?? "", H = b.H ?? "", AmbientC = b.AmbientC ?? "" });
            }
            ThermalSweeps.Clear();
            foreach (var s in t.Sweep ?? [])
                ThermalSweeps.Add(new ThermalSweepRow { Var = s.Var, Start = s.Start, Stop = s.Stop, Points = s.Points.ToString(CultureInfo.InvariantCulture) });
            ThermalCurrents.Clear();
            var link = (t.Currents ?? []).FirstOrDefault(c => c.FromCircuit is not null)?.FromCircuit;
            ThermalFromCircuit = link is not null;
            ThermalCircuitSchematic = link?.Schematic ?? "";
            ThermalCircuitAnalysis = link?.Analysis ?? "";
            ThermalCircuitInstance = link?.Instance ?? "";
            ThermalCircuitCarry = link?.Carry is { } carry ? string.Join(", ", carry) : "";
            foreach (var c in (t.Currents ?? []).Where(c => c.FromCircuit is null))
                ThermalCurrents.Add(new ThermalCurrentRow
                {
                    Port = c.Port?.ToString(CultureInfo.InvariantCulture) ?? "", Dc = c.Dc ?? "", EnterFace = c.EnterFace ?? "", LeaveFace = c.LeaveFace ?? "",
                    Array = c.Array ?? "", F0 = c.F0 ?? "", Harmonics = ThermalRfPlan.HarmonicsText(c.Harmonics),
                    DcEquivalent = DcEquivalentText(c),
                });
            ThermalWireConvectionH = t.WireConvectionH ?? "";
            ThermalWireAmbient = t.WireAmbientC ?? "";
            ThermalBondThermal = t.BondThermalResistance ?? "";
            ThermalBondElectrical = t.BondElectricalResistance ?? "";
            ThermalMeasuresText = string.Join("\n", t.Measures ?? []);
            var m = t.Mesh;
            ThermalOrder = m?.Order ?? ThermalLowerings.DefaultOrder;
            ThermalSizeFromSources = G(m?.SizeFromSources);
            ThermalMinThroughThickness = m?.MinThroughThickness?.ToString(CultureInfo.InvariantCulture) ?? "";
            ThermalGrading = G(m?.Grading);
            ThermalSolver = m?.Solver?.ToString() ?? "Auto";
            ThermalCheck = m?.Check ?? false;
            ThermalKOfT = t.Balance?.KOfT ?? true;
            ThermalSigmaOfT = t.Balance?.SigmaOfT ?? true;
            ThermalTolerance = G(t.Balance?.Tolerance);
            ThermalMaxIterations = t.Balance?.MaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
            ThermalSubmodelFrom = t.Submodel?.From ?? "";
            ThermalSubmodelRegion = t.Submodel?.Region ?? "";
            ThermalRthOn = t.Rth is not null;
            ThermalRthSources = NamesText(t.Rth?.Sources);
            ThermalRthMax = t.Rth?.Stat == ThermalRthStat.Max;
            ThermalZthOn = t.Zth is not null;
            ThermalZthSources = NamesText(t.Zth?.Sources);
            ThermalZthProbes = string.Join(", ", t.Zth?.Probes ?? []);
            ThermalZthStart = t.Zth?.StartHz ?? "";
            ThermalZthStop = t.Zth?.StopHz ?? "";
            ThermalZthPerDecade = t.Zth?.PerDecade?.ToString(CultureInfo.InvariantCulture) ?? "";
            ThermalPulseOn = t.Pulse is not null;
            ThermalPulsePeriod = t.Pulse?.Period ?? "";
            ThermalPulseDuty = t.Pulse?.Duty ?? "";
            ThermalPulsePeak = t.Pulse?.PeakPower ?? "";
            ThermalError = null;
        }
        finally { _syncingThermal = false; }
        OnPropertyChanged(nameof(CanAddThermalSweep));
        OnPropertyChanged(nameof(HasThermalSources));
        OnPropertyChanged(nameof(ThermalSubmodelFromChoices));
        OnPropertyChanged(nameof(ThermalSubmodelRegionChoices));
        OnPropertyChanged(nameof(IsThermalSubmodel));
        RefreshCircuitSurvey();
        RefreshThermalChecks();
    }

    private void RaiseThermalVisibility()
    {
        OnPropertyChanged(nameof(IsThermalSetup));
        OnPropertyChanged(nameof(ShowRobustMeshing));
        OnPropertyChanged(nameof(IsNotThermalSetup));
        OnPropertyChanged(nameof(Is3DSetup));
        OnPropertyChanged(nameof(ShowProblem3D));
        RaiseSolverKindVisibility();
    }

    /// <summary>
    /// Writes the page as it now reads into the Thermal section: one undoable edit, or none when nothing changed. A number
    /// that does not parse stays in its box beside the message, and nothing is written.
    /// </summary>
    public void CommitThermal()
    {
        if (_syncingThermal || !IsThermalSetup) return;
        ThermalError = null;
        var before = SnapshotJson();
        // a COPY: an input that does not parse returns below, and must leave the live setup exactly as it was — writing into
        // Working.Thermal first put the edits made so far into the document with no undo entry of their own
        var t = Working.Thermal?.Clone() ?? new CemThermal();

        // an override naming a source the page does not list (deleted, or renamed outside the editor) is kept as written,
        // not dropped by an unrelated edit here
        var listed = ThermalSources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        t.Sources = [.. ThermalSources.Where(r => r.Override.Trim().Length > 0).Select(r => new CemThermalSource { Name = r.Name, Power = r.Override.Trim() }),
                     .. (t.Sources ?? []).Where(x => !listed.Contains(x.Name))];
        if (t.Sources.Count == 0) t.Sources = null;

        var boundaries = ThermalBoundaries.Where(r => r.Face.Trim().Length > 0).Select(r => new CemThermalBoundary
        {
            Face = r.Face.Trim(), Kind = r.Kind,
            TempC = r.Kind == ThermalBoundaryKind.FixedT ? Blank(r.TempC) : null,
            H = r.Kind == ThermalBoundaryKind.Convection ? Blank(r.H) : null,
            AmbientC = r.Kind == ThermalBoundaryKind.Convection ? Blank(r.AmbientC) : null,
        }).ToList();
        if (ThermalExposed)
            boundaries.Add(new CemThermalBoundary
            {
                Face = C3dThermal.ExposedFaces, Kind = ThermalBoundaryKind.Convection, H = Blank(ThermalExposedH), AmbientC = Blank(ThermalExposedAmbient),
            });
        t.Boundaries = boundaries.Count > 0 ? boundaries : null;

        var sweeps = new List<CemThermalSweep>();
        foreach (var r in ThermalSweeps)
        {
            if (r.Var.Trim().Length == 0) continue;
            if (!int.TryParse(r.Points.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                ThermalError = $"The sweep of '{r.Var}' needs a whole number of points, at least 1.";
                return;
            }
            sweeps.Add(new CemThermalSweep { Var = r.Var.Trim(), Start = r.Start.Trim(), Stop = r.Stop.Trim(), Points = n });
        }
        t.Sweep = sweeps.Count > 0 ? sweeps : null;

        // brief-em3d-77 — a row keeps what a later version wrote beside its port; brief-em3d-78 — or names an array, and states
        // harmonics, each Peak or Rms
        var currents = new List<CemThermalCurrent>();
        // brief-em3d-79 — the circuit link is one entry, first, keeping what a later version wrote beside it
        if (ThermalFromCircuit)
            currents.Add(new CemThermalCurrent { FromCircuit = CircuitLink(), More = t.Currents?.FirstOrDefault(c => c.FromCircuit is not null)?.More });
        foreach (var r in ThermalCurrents)
        {
            string? array = Blank(r.Array);
            if (r.Port.Trim().Length == 0 && r.Dc.Trim().Length == 0 && array is null && r.Harmonics.Trim().Length == 0) continue;
            int? port = null;
            if (array is null || r.Port.Trim().Length > 0)
            {
                if (!int.TryParse(r.Port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) || p < 1)
                {
                    ThermalError = $"A current's port is a port number, at least 1; '{r.Port}' is not. (An array's harmonics leave the port empty.)";
                    return;
                }
                port = p;
            }
            var harmonics = ThermalRfPlan.ParseHarmonics(r.Harmonics, out string? harmonicError);
            if (harmonics is null)
            {
                ThermalError = $"{(array is null ? $"Port {port}" : $"Array '{array}'")}'s harmonics: {harmonicError}";
                return;
            }
            var current = new CemThermalCurrent
            {
                Port = port, Array = array, Dc = Blank(r.Dc), EnterFace = Blank(r.EnterFace), LeaveFace = Blank(r.LeaveFace),
                F0 = Blank(r.F0), Harmonics = harmonics.Count > 0 ? harmonics : null,
                More = t.Currents?.FirstOrDefault(c => c.FromCircuit is null && c.Port == port && c.Array == array)?.More,
            };
            currents.Add(current);
            r.DcEquivalent = DcEquivalentText(current);
        }
        t.Currents = currents.Count > 0 ? currents : null;
        t.WireConvectionH = Blank(ThermalWireConvectionH);
        t.WireAmbientC = Blank(ThermalWireAmbient);
        t.BondThermalResistance = Blank(ThermalBondThermal);
        t.BondElectricalResistance = Blank(ThermalBondElectrical);

        var measures = ThermalMeasuresText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        t.Measures = measures.Count > 0 ? measures : null;

        double? D(string text, string what, bool positive)
        {
            text = text.Trim();
            if (text.Length == 0 || ThermalError is not null) return null;
            if (NumericText.TryParseDouble(text, out double v) && double.IsFinite(v) && (!positive || v > 0)) return v;
            ThermalError = $"{what} is {(positive ? "a positive number" : "a number")}; '{text}' is not.";
            return null;
        }
        int? I(string text, string what)
        {
            text = text.Trim();
            if (text.Length == 0 || ThermalError is not null) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 1) return v;
            ThermalError = $"{what} is a whole number, at least 1; '{text}' is not.";
            return null;
        }
        var mesh = new CemThermalMesh
        {
            Order = ThermalOrder == ThermalLowerings.DefaultOrder ? null : ThermalOrder,
            SizeFromSources = D(ThermalSizeFromSources, "Elements across a source", true),
            MinThroughThickness = I(ThermalMinThroughThickness, "Elements through the thinnest solid"),
            Grading = D(ThermalGrading, "The grading", true),
            Solver = ThermalSolver switch { "Direct" => ThermalMeshSolver.Direct, "Iterative" => ThermalMeshSolver.Iterative, _ => null },
            Check = ThermalCheck ? true : null,
        };
        var balance = new CemThermalBalance
        {
            KOfT = ThermalKOfT ? null : false,
            SigmaOfT = ThermalSigmaOfT ? null : false,
            Tolerance = D(ThermalTolerance, "The balance's tolerance", true),
            MaxIterations = I(ThermalMaxIterations, "The most Newton steps"),
        };
        if (ThermalError is not null) return;
        t.Mesh = mesh is { Order: null, SizeFromSources: null, MinThroughThickness: null, Grading: null, Solver: null, Check: null } ? null : mesh;
        t.Balance = balance is { KOfT: null, SigmaOfT: null, Tolerance: null, MaxIterations: null } ? null : balance;
        // a combo whose choice left its list pushes null back: an empty box, not a crash
        t.Submodel = (ThermalSubmodelFrom ?? "").Trim().Length > 0
            ? new CemThermalSubmodel { From = ThermalSubmodelFrom!.Trim(), Region = (ThermalSubmodelRegion ?? "").Trim() }
            : null;
        // brief-em3d-80 — the switches make or remove each section; what the page does not show is carried as written
        t.Rth = ThermalRthOn ? new CemThermalRth { Sources = Names(ThermalRthSources), Stat = ThermalRthMax ? ThermalRthStat.Max : null } : null;
        int? perDecade = I(ThermalZthPerDecade, "Z_th's frequencies per decade");
        if (ThermalError is not null) return;
        var probes = ThermalZthProbes.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        t.Zth = ThermalZthOn
            ? new CemThermalZth
            {
                Sources = Names(ThermalZthSources), Probes = probes.Count > 0 ? probes : null, StartHz = Blank(ThermalZthStart),
                StopHz = Blank(ThermalZthStop), PerDecade = perDecade,
            }
            : null;
        t.Pulse = ThermalPulseOn
            ? new CemThermalPulse { Period = ThermalPulsePeriod.Trim(), Duty = ThermalPulseDuty.Trim(), PeakPower = Blank(ThermalPulsePeak) }
            : null;
        Working.Thermal = t;
        CommitEdit(before, "Change the thermal setup");
        RefreshCircuitSurvey();
        RefreshThermalChecks();
    }

    private static string? Blank(string s) => s.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>R-em3d78-1 — the DC-equivalent RMS readout of a current with harmonics; empty without, "—" when it does not evaluate.</summary>
    private string DcEquivalentText(CemThermalCurrent c)
    {
        if (c.Harmonics is not { Count: > 0 }) return "";
        var res = ThermalContext?.Resolution();
        string value = res is not null && ThermalRfPlan.DcEquivalentRms(res, c) is { } a ? $"{a.ToString("G4", CultureInfo.InvariantCulture)} A" : "—";
        return $"DC-equivalent RMS {value} — for comparison only: RF heats more than this DC current would, because of skin effect.";
    }

    [RelayCommand]
    private void AddThermalBoundary()
    {
        ThermalBoundaries.Add(new ThermalBoundaryRow { Kind = ThermalBoundaryKind.FixedT, TempC = "25" });
    }

    [RelayCommand]
    private void RemoveThermalBoundary(ThermalBoundaryRow? row)
    {
        if (row is null || !ThermalBoundaries.Remove(row)) return;
        CommitThermal();
    }

    [RelayCommand]
    private void AddThermalCurrent()
    {
        var doc = ThermalContext?.Document();
        int port = doc?.Ports.Select(p => p.Number).Where(n => ThermalCurrents.All(r => r.Port.Trim() != n.ToString(CultureInfo.InvariantCulture)))
                               .DefaultIfEmpty(1).Min() ?? 1;
        ThermalCurrents.Add(new ThermalCurrentRow { Port = port.ToString(CultureInfo.InvariantCulture) });
    }

    [RelayCommand]
    private void RemoveThermalCurrent(ThermalCurrentRow? row)
    {
        if (row is null || !ThermalCurrents.Remove(row)) return;
        CommitThermal();
    }

    [RelayCommand]
    private void AddThermalSweep()
    {
        if (!CanAddThermalSweep) return;
        ThermalSweeps.Add(new ThermalSweepRow());
        OnPropertyChanged(nameof(CanAddThermalSweep));
    }

    [RelayCommand]
    private void RemoveThermalSweep(ThermalSweepRow? row)
    {
        if (row is null || !ThermalSweeps.Remove(row)) return;
        OnPropertyChanged(nameof(CanAddThermalSweep));
        CommitThermal();
    }

    // Switches and pickers commit at once; text commits on Enter or lost focus (the view calls CommitThermal).
    partial void OnThermalExposedChanged(bool value) => CommitThermal();
    partial void OnThermalOrderChanged(int value) => CommitThermal();
    partial void OnThermalSolverChanged(string value) => CommitThermal();
    partial void OnThermalCheckChanged(bool value) => CommitThermal();
    partial void OnThermalKOfTChanged(bool value) => CommitThermal();
    partial void OnThermalSigmaOfTChanged(bool value) => CommitThermal();
    partial void OnThermalSubmodelFromChanged(string value) { OnPropertyChanged(nameof(IsThermalSubmodel)); CommitThermal(); }
    partial void OnThermalSubmodelRegionChanged(string value) => CommitThermal();
    partial void OnThermalRthOnChanged(bool value) => CommitThermal();
    partial void OnThermalRthMaxChanged(bool value) => CommitThermal();
    partial void OnThermalZthOnChanged(bool value) => CommitThermal();
    partial void OnThermalPulseOnChanged(bool value) => CommitThermal();
    partial void OnThermalFromCircuitChanged(bool value) => CommitThermal();
    partial void OnThermalCircuitAnalysisChanged(string value) => CommitThermal();
    partial void OnThermalCircuitInstanceChanged(string value) => CommitThermal();

    /// <summary>R-em3d75-3 — the measures, parsed as they are typed: each error beside the list, nothing written.</summary>
    partial void OnThermalMeasuresTextChanged(string value)
    {
        if (ThermalContext is not { } ctx || ctx.Resolution() is not { } res) { ThermalMeasureErrors = ""; return; }
        var doc = ctx.Document();
        var errors = new List<string>();
        foreach (string line in value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            if (C3dThermal.MeasureProblem(line, doc, res) is { } why) errors.Add($"{line}: {why}");
        ThermalMeasureErrors = string.Join("\n", errors);
    }

    /// <summary>What check says of this setup, and the size estimate `explain` gives — recomputed after each edit.</summary>
    private void RefreshThermalChecks()
    {
        if (!IsThermalSetup || ThermalContext is not { } ctx) { ThermalFindings = ""; ThermalEstimate = ""; return; }
        var doc = ctx.Document();
        var e = ctx.Elaboration();
        var res = ctx.Resolution();
        if (res is not null)
            ThermalFindings = string.Join("\n", C3dThermal.Setup(Working.Name, Working, doc, e, res).Select(d => d.Render()));
        ThermalEstimate = Estimate(doc, e, Working);
        OnThermalMeasuresTextChanged(ThermalMeasuresText);
    }

    /// <summary>R-em3d75-3 — brief 74 §6's estimate, as `explain` states it; or why there is none yet.</summary>
    public static string Estimate(C3dDocument doc, C3dElaboration? e, EmSetup setup)
    {
        if (e is not { Ok: true }) return "No estimate: the 3D view does not elaborate yet.";
        var t = setup.Thermal ?? new CemThermal();
        try
        {
            // brief-em3d-93 — estimated over what the run solves: the objects that are modelled
            if (ThermalLowerings.Build(doc, C3dModelled.Filter(doc, e), t, 1, out string? why) is not { } lowering) return "No estimate: " + why;
            var solver = t.Mesh?.Solver switch { ThermalMeshSolver.Direct => ThermalSolverKind.Direct, ThermalMeshSolver.Iterative => ThermalSolverKind.Iterative, _ => ThermalSolverKind.Auto };
            var est = ThermalSizeEstimate.Of(lowering.Input, new ThermalSolveOptions { Solver = solver });
            return $"Estimate: ≈ {est.Elements:N0} elements, ≈ {est.Unknowns:N0} unknowns, the {est.Solver.ToString().ToLowerInvariant()} solver, " +
                   $"≈ {est.MemoryBytes / 1e6:N0} MB. An estimate before meshing: each solid at its own element size.";
        }
        catch (Exception x) when (x is InvalidOperationException or ArgumentException) { return "No estimate: " + x.Message; }
    }
}
