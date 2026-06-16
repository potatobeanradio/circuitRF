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

    // Names of analyses in the OLD chain that must be removed on edit (inner + old sweeps).
    // Empty for the Add flow.
    private readonly List<string> _editingChainNames = [];

    // ── Type selection ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDc), nameof(IsSp), nameof(IsHb), nameof(IsLp), nameof(IsLpp))]
    private AnalysisKind _type = AnalysisKind.DC;

    public bool IsDc  => Type == AnalysisKind.DC;
    public bool IsSp  => Type == AnalysisKind.SP;
    public bool IsHb  => Type == AnalysisKind.HB;
    public bool IsLp  => Type == AnalysisKind.LP;
    public bool IsLpp => Type == AnalysisKind.LPP;

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

    // ── Parametric sweep axes ─────────────────────────────────────────────────

    public ObservableCollection<SweepAxisRowViewModel> SweepAxes { get; } = [];

    [ObservableProperty] private bool _sweepsExpanded = false;

    [RelayCommand]
    private void AddSweepAxis()
    {
        SweepAxes.Add(new SweepAxisRowViewModel(_model));
        SweepsExpanded = true;
    }

    [RelayCommand]
    private void RemoveSweepAxis(SweepAxisRowViewModel row) => SweepAxes.Remove(row);

    // ── Constructor: Add new ──────────────────────────────────────────────────

    public AnalysisEditorViewModel(SchematicEditModel model, AnalysisKind initialType = AnalysisKind.DC)
    {
        _model         = model;
        _existingNames = model.Analyses.Select(a => a.Name).ToList();
        _editingName   = null;
        _type          = initialType;
        _name          = NextFreeName(initialType, _existingNames);
        SpBody         = new SpBodyViewModel(model);
        HbBody         = new HbBodyViewModel(model);
        ValidateName();
    }

    // ── Constructor: Edit existing ────────────────────────────────────────────

    public AnalysisEditorViewModel(SchematicEditModel model, Analysis existing)
    {
        _model         = model;
        _existingNames = model.Analyses.Select(a => a.Name).ToList();

        // Resolve the chain: navigate to innermost non-sweep analysis and collect sweeps.
        var (inner, sweepChain) = ResolveChain(model, existing);

        _editingName = inner.Name;
        _name        = inner.Name;
        // Enabled is the outermost analysis's flag (or the inner if no sweeps).
        _enabled = sweepChain.Count > 0 ? sweepChain[^1].Enabled : inner.Enabled;

        // Track all chain member names so the edit command can remove the old chain.
        _editingChainNames.Add(inner.Name);
        _editingChainNames.AddRange(sweepChain.Select(s => s.Name));

        switch (inner)
        {
            case SParameterAnalysis sp:
                _type  = AnalysisKind.SP;
                SpBody = SpBodyViewModel.FromAnalysis(sp, model);
                HbBody = new HbBodyViewModel(model);
                break;

            case HarmonicBalanceAnalysis hb:
                _type  = AnalysisKind.HB;
                SpBody = new SpBodyViewModel(model);
                HbBody = HbBodyViewModel.FromAnalysis(hb, model);

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
                break;
        }

        // Load existing sweep chain into the rows list (innermost first).
        foreach (var psa in sweepChain)
        {
            SweepAxes.Add(SweepAxisRowViewModel.FromPsa(psa, model));
            SweepsExpanded = true;
        }

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
    /// When no sweep axes are present the list contains a single enabled analysis.
    /// When N axes are present: [inner (disabled), sweep1 (disabled), …, sweepN (enabled)],
    /// naming each sweep as <c>&lt;innerName&gt;_sweep_&lt;varName&gt;</c>.
    /// </summary>
    public IReadOnlyList<Analysis>? BuildAnalyses()
    {
        if (!CanCommit) return null;
        string name = Name.Trim();

        bool hasSweeps = SweepAxes.Count > 0;

        // Build the inner analysis.
        Analysis? inner = Type switch
        {
            AnalysisKind.DC  => new DcAnalysis(name)           { Enabled = !hasSweeps && Enabled },
            AnalysisKind.SP  => BuildSp(name, !hasSweeps && Enabled),
            AnalysisKind.HB  => HbBody.BuildAnalysis(name,      !hasSweeps && Enabled),
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
            bool isLast  = i == SweepAxes.Count - 1;
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

            psa.Enabled = isLast && Enabled;
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
        // Navigate inward if the selected analysis is itself a sweep.
        var psaStack = new Stack<ParametricSweepAnalysis>();
        Analysis current = selected;
        while (current is ParametricSweepAnalysis psa)
        {
            psaStack.Push(psa);
            var next = model.Analyses.FirstOrDefault(
                a => string.Equals(a.Name, psa.InnerAnalysisName, StringComparison.OrdinalIgnoreCase));
            if (next is null) break;
            current = next;
        }
        var inner = current;

        // The psaStack is now outermost-first; we want innermost-first for the list.
        var chainFromOuter = psaStack.ToList(); // stack top = innermost sweep added last
        // psaStack.Push order is outer→inner as we traverse in→out;
        // but we traversed from outer→inner, so top is innermost PSA.
        // Actually: selected was outermost. First iteration pushed selected (outermost).
        // Next iteration pushed the next inner psa, etc.
        // So stack top = deepest PSA (= PSA immediately wrapping inner).
        // For our chain list we want [PSA wrapping inner, ..., outermost PSA].
        var sweepChain = chainFromOuter; // already innermost-to-outermost? Let's check:
        // If selected = outer sweep → stack = [outer], current = inner → stack top = outer
        // If selected = inner-to-outer chain [A→B→inner]:
        //   iter1: push A (outer), current = B
        //   iter2: push B, current = inner
        //   stack (bottom→top): A, B → top = B (immediately wrapping inner) ✓
        // So stack ToList() = [A, B] = outermost to innermost-sweep → we need reverse
        sweepChain.Reverse(); // now innermost-sweep to outermost

        // If selected was NOT a sweep, collect sweep analyses wrapping it.
        if (psaStack.Count == 0)
        {
            var wrapping = new List<ParametricSweepAnalysis>();
            var cursor   = inner;
            var sweepMap = model.Analyses
                .OfType<ParametricSweepAnalysis>()
                .ToDictionary(a => a.InnerAnalysisName, StringComparer.OrdinalIgnoreCase);
            while (sweepMap.TryGetValue(cursor.Name, out var nextSweep))
            {
                wrapping.Add(nextSweep);
                cursor = nextSweep;
            }
            return (inner, wrapping);
        }

        return (inner, sweepChain);
    }
}
