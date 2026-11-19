// brief-emi-6 follow-up — the EMerge section's fields, shown whenever the setup runs EMerge, alone or in a comparison.
//
// The Palace section's rules exactly (R-em-11): every control writes a .cem field (EMI-4's CemEmerge) through the same
// undoable CommitEdit; a blank box is the chosen preset's value — EmergeSettings.Preset's, the one place the defaults live and
// the one the lowering reads — and a section with every field blank is written as no section. A value the run would refuse
// (EmergeSettings.Problems) is refused here with the same limits, and writes nothing.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmSetupEditorViewModel
{
    [ObservableProperty] private PalaceQualityChoice _emergeQualityChoice = PalaceQualityChoices[1];
    [ObservableProperty] private string _emergeMaxElementWavelengthsText = "";
    [ObservableProperty] private string _emergeEdgeRefinementText        = "";
    [ObservableProperty] private string _emergeElementOrderText          = "";
    [ObservableProperty] private bool   _emergeAdaptiveSweep             = true;
    [ObservableProperty] private string _emergeToleranceText             = "";
    [ObservableProperty] private string _emergeMaxSamplesText            = "";
    [ObservableProperty] private string _emergeAdaptiveMeshText          = "";
    [ObservableProperty] private string _emergeSaveFieldsText            = "";   // brief-emi-7
    [ObservableProperty] private string? _emergeFieldError;

    /// <summary>True when EMerge's section is shown: EMerge is one of the solvers the setup runs.</summary>
    public bool IsEmergeSetup => !IsThermalSetup && Working.Solver3D.Runs(CircuitRF.Design.Em3d.EmergeSolver.Id);

    /// <summary>A comparison shows several solvers' sections at once, so each is titled with its solver's name then.</summary>
    public bool ShowPalaceTitle  => IsCompareSetup && IsPalaceSetup;
    public bool ShowOpenEmsTitle => IsCompareSetup && IsOpenEmsSetup;
    public bool ShowEmergeTitle  => IsCompareSetup && IsEmergeSetup;

    /// <summary>What each preset sets, from the one table the lowering reads.</summary>
    public static string EmergeQualityTip =>
        "Draft, Standard or Accurate: the mesh, the sweep's tolerance and EMerge's own mesh refinement together. A box below that " +
        "is filled in overrides the preset for that setting only. " + string.Join(" ", PalaceQualityChoices.Select(c =>
        {
            var p = EmergeSettings.Preset(c.Value);
            return $"{c.Label}: elements up to {G(p.MaxElementWavelengths)} λ, {G(p.EdgeRefinement)} of that at metal, sweep " +
                   $"tolerance {G(p.Tolerance)}, {p.AdaptiveMesh} refinement pass{(p.AdaptiveMesh == 1 ? "" : "es")}.";
        }));

    private EmergeSettings EmergePresetShown => EmergeSettings.Preset(EmergeQualityChoice.Value);

    /// <summary>What a blank box stands for — the chosen preset's value, the one the lowering reads.</summary>
    public string EmergeDefaultMaxElementWavelengths => G(EmergePresetShown.MaxElementWavelengths);
    public string EmergeDefaultEdgeRefinement        => G(EmergePresetShown.EdgeRefinement);
    public string EmergeDefaultElementOrder          => EmergePresetShown.ElementOrder.ToString(CultureInfo.InvariantCulture);
    public string EmergeDefaultTolerance             => G(EmergePresetShown.Tolerance);
    public string EmergeDefaultMaxSamples            => EmergePresetShown.MaxSamples.ToString(CultureInfo.InvariantCulture);
    public string EmergeDefaultAdaptiveMesh          => EmergePresetShown.AdaptiveMesh.ToString(CultureInfo.InvariantCulture);

    partial void OnEmergeQualityChoiceChanged(PalaceQualityChoice value)
    {
        OnPropertyChanged(nameof(EmergeDefaultMaxElementWavelengths));
        OnPropertyChanged(nameof(EmergeDefaultEdgeRefinement));
        OnPropertyChanged(nameof(EmergeDefaultElementOrder));
        OnPropertyChanged(nameof(EmergeDefaultTolerance));
        OnPropertyChanged(nameof(EmergeDefaultMaxSamples));
        OnPropertyChanged(nameof(EmergeDefaultAdaptiveMesh));
        if (_suppressCommit) return;
        var section = Working.Emerge?.Clone() ?? new CemEmerge();
        // Standard is written as no field at all, as Palace's is.
        section.Quality = value.Value == PalaceQuality.Standard ? null : value.Value;
        CommitEmergeSection(section, "Change EMerge preset");
    }

    // ── brief-emi-12: the linear solver ──────────────────────────────────────────────────────

    /// <summary>Direct first: it is EMerge's default, where Palace's is Iterative.</summary>
    public static IReadOnlyList<PalaceLinearSolverChoice> EmergeLinearSolverChoices { get; } =
    [
        new(PalaceLinearSolver.Direct,    "Direct"),
        new(PalaceLinearSolver.Iterative, "Iterative"),
    ];

    public const string EmergeLinearSolverTip =
        "How EMerge solves each linear system of a driven run. Direct (the default) factorises it with SPARTA, EMerge's own " +
        "pick. Iterative uses CORVID, which factorises only the first-order part and iterates on the rest: about a third less " +
        "memory, S within about 1e-7 of Direct, and usually a little slower where Direct fits (the Coupled Microstrip, " +
        "337,980 unknowns: 14 s and 3.9 GB against 11 s and 5.0 GB). An eigenmode run always solves directly.";

    [ObservableProperty] private PalaceLinearSolverChoice _emergeLinearSolverChoice = EmergeLinearSolverChoices[0];

    partial void OnEmergeLinearSolverChoiceChanged(PalaceLinearSolverChoice value)
    {
        if (_suppressCommit) return;
        var section = Working.Emerge?.Clone() ?? new CemEmerge();
        // Direct is written as no field at all.
        section.LinearSolver = value.Value == PalaceLinearSolver.Direct ? null : value.Value;
        CommitEmergeSection(section, "Change EMerge linear solver");
    }

    partial void OnEmergeAdaptiveSweepChanged(bool value)
    {
        if (_suppressCommit) return;
        var section = Working.Emerge?.Clone() ?? new CemEmerge();
        // On is the default and written as no field.
        section.AdaptiveSweep = value ? null : false;
        CommitEmergeSection(section, "Change EMerge adaptive sweep");
    }

    /// <summary>
    /// Commits one EMerge field — <c>Emerge.&lt;Field&gt;</c> is its tag. Blank writes the field as omitted (the preset's
    /// value); a value the run would refuse stays in the box beside the message and writes nothing.
    /// </summary>
    public void CommitEmergeField(string tag)
    {
        var section = Working.Emerge?.Clone() ?? new CemEmerge();
        string? error = null;

        void Real(string text, Func<double, bool> ok, string rule, Action<double?> set)
        {
            if (text.Trim().Length == 0) set(null);
            else if (TryDouble(text, out double v) && ok(v)) set(v);
            else error = rule;
        }
        void Whole(string text, Func<int, bool> ok, string rule, Action<int?> set)
        {
            if (text.Trim().Length == 0) set(null);
            else if (TryInt(text, int.MinValue, out int v) && ok(v)) set(v);
            else error = rule;
        }

        switch (tag)
        {
            case "Emerge.MaxElementWavelengths":
                Real(EmergeMaxElementWavelengthsText, v => v > 0 && v <= 0.5,
                     "Enter a fraction of a wavelength above 0 and at most 0.5, e.g. 0.15.", v => section.MaxElementWavelengths = v);
                break;
            case "Emerge.EdgeRefinement":
                Real(EmergeEdgeRefinementText, v => v > 0 && v <= 1, "Enter a number above 0 and at most 1.", v => section.EdgeRefinement = v);
                break;
            case "Emerge.ElementOrder":
                Whole(EmergeElementOrderText, v => v is 1 or 2, "Enter 1 or 2: EMerge solves with first- or second-order elements.",
                      v => section.ElementOrder = v);
                break;
            case "Emerge.Tolerance":
                Real(EmergeToleranceText, v => v > 0 && double.IsFinite(v), "Enter a positive tolerance, e.g. 0.001.", v => section.Tolerance = v);
                break;
            case "Emerge.MaxSamples":
                Whole(EmergeMaxSamplesText, v => v >= EmergeSettings.EmergeInitialSamples,
                      $"Enter a whole number of at least {EmergeSettings.EmergeInitialSamples}: the adaptive sweep starts from that many.",
                      v => section.MaxSamples = v);
                break;
            case "Emerge.AdaptiveMesh":
                Whole(EmergeAdaptiveMeshText, v => v >= 0, "Enter a whole number of 0 or more (0 solves the first mesh).",
                      v => section.AdaptiveMesh = v);
                break;
            case "Emerge.SaveFieldsGHz":                // brief-emi-7 — Palace's box and rules (EmSetupEditorViewModel.SaveFields.cs)
                if (TryParseSaveFields(EmergeSaveFieldsText, out var saves, out error))
                {
                    section.SaveFieldsGHz = saves;
                    ShowSaving(() => EmergeSaveFields = SavesAny(saves));
                }
                break;
            default:
                return;
        }

        EmergeFieldError = error;
        if (error is not null) return;
        CommitEmergeSection(section, $"Change EMerge setting {tag["Emerge.".Length..]}");
    }

    private void CommitEmergeSection(CemEmerge section, string description)
    {
        var before = SnapshotJson();
        Working.Emerge = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;      // the same value, retyped: no undo entry
        CommitEdit(before, description);
    }

    /// <summary>The section's boxes, from the file. Called inside <see cref="SyncSolver3DFields"/>'s suppression.</summary>
    private void SyncEmergeFields()
    {
        var e = Working.Emerge;
        EmergeQualityChoice = PalaceQualityChoices.First(c => c.Value == (e?.Quality ?? PalaceQuality.Standard));
        EmergeLinearSolverChoice = EmergeLinearSolverChoices.First(c => c.Value == (e?.LinearSolver ?? PalaceLinearSolver.Direct));
        EmergeMaxElementWavelengthsText = G(e?.MaxElementWavelengths);
        EmergeEdgeRefinementText        = G(e?.EdgeRefinement);
        EmergeElementOrderText          = e?.ElementOrder?.ToString(CultureInfo.InvariantCulture) ?? "";
        EmergeAdaptiveSweep             = e?.AdaptiveSweep ?? true;
        EmergeToleranceText             = G(e?.Tolerance);
        EmergeMaxSamplesText            = e?.MaxSamples?.ToString(CultureInfo.InvariantCulture) ?? "";
        EmergeAdaptiveMeshText          = e?.AdaptiveMesh?.ToString(CultureInfo.InvariantCulture) ?? "";
        EmergeSaveFieldsText            = SaveFieldsText(e?.SaveFieldsGHz);
        EmergeSaveFields                = SavesAny(e?.SaveFieldsGHz);
        EmergeFieldError = null;
    }
}
