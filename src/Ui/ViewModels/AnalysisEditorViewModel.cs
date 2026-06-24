using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Staging VM for the Add/Edit Analysis dialog (analysis-authoring.md §4.2).
///
/// Holds the type selector (DC / S-Parameter / Harmonic Balance), name, enabled flag,
/// per-type body VMs, and a list of parametric sweep axes.
///
/// Call <see cref="BuildAnalyses"/> on OK to commit; returns null when validation fails.
/// When sweep axes are present the list contains [inner (disabled), …sweeps…, outer (enabled)].
/// The static constructors handle both "Add new" and "Edit existing" (including chains).
/// </summary>
public sealed partial class AnalysisEditorViewModel : ObservableObject
{
    public enum AnalysisKind { DC, SP, HB, LP, LPP }

    private readonly SchematicEditModel _model;
    private readonly List<string>       _existingNames;
    private readonly string?            _editingName;
    // Workspace root — the base for storing picked .gam paths relative (the engine's resolution base).
    // Threaded into the LP/LPP bodies so their file pickers mirror the SnP File picker's behavior.
    private readonly string?            _workspaceRoot;

    // Names of analyses in the OLD chain that must be removed on edit (inner + old sweeps).
    // Empty for the Add flow.
    private readonly List<string> _editingChainNames = [];

    // ── Type selection ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDc), nameof(IsSp), nameof(IsHb), nameof(IsLp), nameof(IsLpp),
                              nameof(ShowSweeps))]
    private AnalysisKind _type = AnalysisKind.DC;

    public bool IsDc  => Type == AnalysisKind.DC;
    public bool IsSp  => Type == AnalysisKind.SP;
    public bool IsHb  => Type == AnalysisKind.HB;
    public bool IsLp  => Type == AnalysisKind.LP;
    public bool IsLpp => Type == AnalysisKind.LPP;

    /// <summary>Parametric-sweep chains are supported on every analysis type — including freq-swept
    /// Loadpull and Loadpull-Pursuit over a tone VAR (FreqSweptLoadpull brief, Layers A–E).</summary>
    public bool ShowSweeps => true;

    // ── Name + Enabled ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(CanCommit))]
    private string _name = "";

    [ObservableProperty] private bool _enabled = true;

    /// <summary>Non-null when the current Name is invalid or conflicts with an existing analysis.</summary>
    public string? NameError { get; private set; }

    /// <summary>True when Name is valid and the dialog OK button may be enabled.</summary>
    public bool CanCommit => NameError is null && Name.Trim().Length > 0;

    // ── Per-type body VMs ─────────────────────────────────────────────────────

    /// <summary>S-Parameter body (Layer 2 enriches this stub with segment editing).</summary>
    public SpBodyViewModel SpBody { get; }

    /// <summary>HB body (Layer 3 enriches this stub with all field editing).</summary>
    public HbBodyViewModel HbBody { get; }

    /// <summary>Loadpull body (brief 05).</summary>
    public LpBodyViewModel LpBody { get; }

    /// <summary>Loadpull-Pursuit body (brief 06).</summary>
    public LppBodyViewModel LppBody { get; }

    // ── Parametric sweep axes ─────────────────────────────────────────────────

    public ObservableCollection<SweepAxisRowViewModel> SweepAxes { get; } = [];

    [ObservableProperty] private bool _sweepsExpanded = false;

    [RelayCommand]
    private void AddSweepAxis()
    {
        SweepAxes.Add(new SweepAxisRowViewModel(_model));
        ApplySweepVariableHint();
        SweepsExpanded = true;
    }

    [RelayCommand]
    private void RemoveSweepAxis(SweepAxisRowViewModel row) => SweepAxes.Remove(row);

    [RelayCommand]
    private void MoveSweepAxisUp(SweepAxisRowViewModel row)
    {
        int i = SweepAxes.IndexOf(row);
        if (i > 0) SweepAxes.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveSweepAxisDown(SweepAxisRowViewModel row)
    {
        int i = SweepAxes.IndexOf(row);
        if (i >= 0 && i < SweepAxes.Count - 1) SweepAxes.Move(i, i + 1);
    }

    // ── Constructor: Add new ──────────────────────────────────────────────────

    public AnalysisEditorViewModel(SchematicEditModel model, AnalysisKind initialType = AnalysisKind.DC,
        string? workspaceRoot = null)
    {
        _model         = model;
        _workspaceRoot = workspaceRoot;
        _existingNames = model.Analyses.Select(a => a.Name).ToList();
        _editingName   = null;
        _type          = initialType;
        _name          = NextFreeName(initialType, _existingNames);
        SpBody         = new SpBodyViewModel(model);
        HbBody         = new HbBodyViewModel(model);
        LpBody         = new LpBodyViewModel(model, workspaceRoot);
        LppBody        = new LppBodyViewModel(model, workspaceRoot);
        ValidateName();
    }

    // ── Constructor: Edit existing ────────────────────────────────────────────

    public AnalysisEditorViewModel(SchematicEditModel model, Analysis existing,
        string? workspaceRoot = null)
    {
        _model         = model;
        _workspaceRoot = workspaceRoot;
        _existingNames = model.Analyses.Select(a => a.Name).ToList();

        // Resolve the chain: navigate to innermost non-sweep analysis and collect sweeps.
        var (inner, sweepChain) = ResolveChain(model, existing);

        _editingName = inner.Name;
        _name        = inner.Name;
        // Enabled now lives on the base analysis (each sweep axis has its own row Enabled).
        _enabled = inner.Enabled;

        // Track all chain member names so the edit command can remove the old chain.
        _editingChainNames.Add(inner.Name);
        _editingChainNames.AddRange(sweepChain.Select(s => s.Name));

        switch (inner)
        {
            case SParameterAnalysis sp:
                _type  = AnalysisKind.SP;
                SpBody = SpBodyViewModel.FromAnalysis(sp, model);
                HbBody = new HbBodyViewModel(model);
                LpBody = new LpBodyViewModel(model, workspaceRoot);
                LppBody = new LppBodyViewModel(model, workspaceRoot);
                break;

            case LoadpullPursuitAnalysis lpp:
                _type   = AnalysisKind.LPP;
                SpBody  = new SpBodyViewModel(model);
                HbBody  = new HbBodyViewModel(model);
                LpBody  = new LpBodyViewModel(model, workspaceRoot);
                LppBody = LppBodyViewModel.FromAnalysis(lpp, model, workspaceRoot);
                break;

            case LoadpullAnalysis lp:
                _type  = AnalysisKind.LP;
                SpBody = new SpBodyViewModel(model);
                HbBody = new HbBodyViewModel(model);
                LpBody = LpBodyViewModel.FromAnalysis(lp, model, workspaceRoot);
                LppBody = new LppBodyViewModel(model, workspaceRoot);
                break;

            case HarmonicBalanceAnalysis hb:
                _type  = AnalysisKind.HB;
                SpBody = new SpBodyViewModel(model);
                HbBody = HbBodyViewModel.FromAnalysis(hb, model);
                LpBody = new LpBodyViewModel(model, workspaceRoot);
                LppBody = new LppBodyViewModel(model, workspaceRoot);

                // Migrate legacy HB sweep fields into a sweep axis row.
#pragma warning disable CS0618
                if (hb.SweepVarName is { Length: > 0 } && sweepChain.Count == 0)
                {
                    SweepAxes.Add(SweepAxisRowViewModel.FromLegacyHbSweep(
                        hb.SweepVarName,
                        hb.SweepStartExpr ?? "0",
                        hb.SweepStopExpr  ?? "1",
                        hb.SweepStepExpr  ?? "0.1",
                        model));
                    SweepsExpanded = true;
                }
#pragma warning restore CS0618
                break;

            default: // DcAnalysis or unknown
                _type  = AnalysisKind.DC;
                SpBody = new SpBodyViewModel(model);
                HbBody = new HbBodyViewModel(model);
                LpBody = new LpBodyViewModel(model, workspaceRoot);
                LppBody = new LppBodyViewModel(model, workspaceRoot);
                break;
        }

        // Load existing sweep chain into the rows list (innermost first).
        foreach (var psa in sweepChain)
        {
            SweepAxes.Add(SweepAxisRowViewModel.FromPsa(psa, model));
            SweepsExpanded = true;
        }

        ApplySweepVariableHint();   // hint reflects the (now-resolved) analysis type
        ValidateName();
    }

    // ── Validation ────────────────────────────────────────────────────────────

    partial void OnNameChanged(string value) => ValidateName();

    // When the type changes, refresh the Name to the next free name for the new type —
    // but only if the current name still looks auto-generated (letters-only prefix + digits suffix).
    // A custom name the user typed (e.g. "MyAmplifierSP") is left alone.
    partial void OnTypeChanged(AnalysisKind value)
    {
        if (IsAutoGeneratedName(Name))
            Name = NextFreeName(value, _existingNames, _editingName);
        ApplySweepVariableHint();
    }

    /// <summary>Sets each sweep row's Variable placeholder to hint at the typical sweep variable for the
    /// current type: "e.g. RFfreq" for Loadpull/LP-Pursuit (which are primarily frequency sweeps — the
    /// LP engine already does the Pin/Pavl sweep), "e.g. Pavl" for HB and everything else.</summary>
    private void ApplySweepVariableHint()
    {
        string hint = (IsLp || IsLpp) ? "e.g. RFfreq" : "e.g. Pavl";
        foreach (var row in SweepAxes) row.VariablePlaceholder = hint;
    }

    private static bool IsAutoGeneratedName(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return true;
        int i = 0;
        while (i < n.Length && char.IsLetter(n[i])) i++;
        // Must have ≥1 letter prefix AND ≥1 digit suffix (all digits after the prefix).
        return i > 0 && i < n.Length && n[i..].All(char.IsDigit);
    }

    private void ValidateName()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            NameError = "Name is required.";
        }
        else if (!IsValidName(name))
        {
            NameError = "Name must start with a letter and contain only letters, digits, and underscores.";
        }
        else if (_existingNames.Contains(name, StringComparer.OrdinalIgnoreCase)
              && !string.Equals(name, _editingName, StringComparison.OrdinalIgnoreCase))
        {
            NameError = $"An analysis named '{name}' already exists.";
        }
        else
        {
            NameError = null;
        }
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(CanCommit));
    }

    private static bool IsValidName(string s)
        => s.Length > 0 && char.IsLetter(s[0])
           && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    // ── Name helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the lowest free typed name ("DC1"/"SP1"/"HB1"/"LP1"/"LPP1") not already in
    /// <paramref name="existing"/>, optionally excluding <paramref name="excluding"/> (the name being edited).
    /// </summary>
    public static string NextFreeName(AnalysisKind kind, IList<string> existing, string? excluding = null)
    {
        string prefix = kind switch
        {
            AnalysisKind.DC  => "DC",
            AnalysisKind.SP  => "SP",
            AnalysisKind.HB  => "HB",
            AnalysisKind.LP  => "LP",
            AnalysisKind.LPP => "LPP",
            _                => "DC",
        };
        for (int n = 1; n <= 999; n++)
        {
            string candidate = $"{prefix}{n}";
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase)
                || string.Equals(candidate, excluding, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return prefix + "1";
    }

    // ── Expression preview ────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates <paramref name="expression"/> against the schematic's current VARs and returns
    /// "≈ value", or "" for blank / bare-number / unresolvable. Same evaluator as
    /// <see cref="ParameterRowViewModel"/> — no fork (§4.3).
    /// </summary>
    public string ComputePreview(string expression)
        => AnalysisPreviewHelper.ComputePreview(expression, _model);

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the staged analyses when validation passes, or null.
    /// When no sweep axes are present the list contains a single analysis (Enabled from the dialog).
    /// When N axes are present: [inner, sweep0, …, sweepN-1]; the base carries the dialog's
    /// Enabled flag; each sweep axis carries its own row's Enabled (Stage 3 — no more isLast hack).
    /// </summary>
    public IReadOnlyList<Analysis>? BuildAnalyses()
    {
        if (!CanCommit) return null;
        string name = Name.Trim();

        bool hasSweeps = SweepAxes.Count > 0;

        // Build the inner analysis. The base carries the dialog's Enabled flag; a disabled base makes
        // the whole chain inert (Stage 2). Each sweep axis carries its own row's Enabled below.
        Analysis? inner = Type switch
        {
            AnalysisKind.DC  => new DcAnalysis(name)            { Enabled = Enabled },
            AnalysisKind.SP  => BuildSp(name, Enabled),
            AnalysisKind.HB  => HbBody.BuildAnalysis(name,      Enabled),
            AnalysisKind.LP  => LpBody.IsValid  ? LpBody.BuildAnalysis(name, Enabled)  : null,
            AnalysisKind.LPP => LppBody.IsValid ? LppBody.BuildAnalysis(name, Enabled) : null,
            _                => null,
        };
        if (inner is null) return null;

        if (!hasSweeps)
            return [inner];

        // Build sweep chain (innermost first).
        var chain = new List<Analysis> { inner };
        string innerName = name;

        for (int i = 0; i < SweepAxes.Count; i++)
        {
            var row      = SweepAxes[i];
            string varName = row.VarName.Trim();
            if (varName.Length == 0) return null;

            string sweepName = $"{name}_sweep_{varName}";
            ParametricSweepAnalysis psa;

            if (row.Mode == SweepAxisMode.List)
            {
                double[]? values = row.BuildValues();
                if (values is null || values.Length == 0) return null;
                psa = new ParametricSweepAnalysis(sweepName, varName, values, innerName);
            }
            else
            {
                // StepSize or PointCount — store the compact spec for .cnl round-trip fidelity.
                var spec = row.BuildSpec();
                if (spec is null) return null;
                psa = new ParametricSweepAnalysis(sweepName, varName, spec, innerName);
                if (psa.SweepValues.Length == 0) return null;
            }

            psa.Enabled = row.Enabled;        // was: isLast && Enabled
            chain.Add(psa);
            innerName = sweepName;
        }

        return chain;
    }

    // ── Chain names for the edit command ─────────────────────────────────────

    /// <summary>
    /// Names of the analyses in the chain being edited (inner + all wrapping sweeps).
    /// Empty for the Add flow.  The edit command removes ALL of these and replaces them
    /// with the result of <see cref="BuildAnalyses"/>.
    /// </summary>
    public IReadOnlyList<string> EditingChainNames => _editingChainNames;

    // ── Private helpers ───────────────────────────────────────────────────────

    private Analysis? BuildSp(string name, bool enabled)
    {
        var sweeps = SpBody.BuildSweeps();
        var sp = new SParameterAnalysis(name, sweeps);
        sp.Enabled = enabled;
        return sp;
    }

    /// <summary>
    /// Given the analysis the user selected, finds the innermost non-sweep analysis and
    /// collects the sweep chain from innermost outward.
    /// </summary>
    private static (Analysis Inner, List<ParametricSweepAnalysis> SweepChain)
        ResolveChain(SchematicEditModel model, Analysis selected)
    {
        // Navigate inward to the base (innermost non-sweep) regardless of which chain member was
        // selected, so editing ANY member — base or any sweep, inner or outer — opens the root view.
        Analysis current = selected;
        while (current is ParametricSweepAnalysis psa)
        {
            var next = model.Analyses.FirstOrDefault(
                a => string.Equals(a.Name, psa.InnerAnalysisName, StringComparison.OrdinalIgnoreCase));
            if (next is null) break;   // dangling inner ref (broken chain) — stop here
            current = next;
        }
        var inner = current;

        // Collect the FULL sweep chain wrapping the base, innermost→outermost.
        var wrapping = new List<ParametricSweepAnalysis>();
        var sweepMap = model.Analyses
            .OfType<ParametricSweepAnalysis>()
            .ToDictionary(a => a.InnerAnalysisName, StringComparer.OrdinalIgnoreCase);
        var cursor = inner;
        while (sweepMap.TryGetValue(cursor.Name, out var nextSweep))
        {
            wrapping.Add(nextSweep);
            cursor = nextSweep;
        }
        return (inner, wrapping);
    }
}
