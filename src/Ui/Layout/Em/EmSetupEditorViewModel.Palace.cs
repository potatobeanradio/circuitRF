// brief-em3d-7 R-em3d7-6b — the panel's Solver picker and, when Palace is picked, its section's fields.
//
// NOTHING THAT AFFECTS THE ANSWER LIVES ONLY HERE (R-em-11): every control writes a .cem field
// (Solver3D, and the Palace section's seven), through the same undoable CommitEdit every other control
// uses, and the defaults a blank box stands for are PalaceSettings.Default's — the ones the writers
// read. A blank box is the default, and a section with every box blank is written as no section.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One row of the Solver picker.</summary>
public sealed record Em3dSolverChoice(Em3dSolver Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>Planar, then the two 3D solvers. "Both" is brief 10's and is not offered yet.</summary>
    public static IReadOnlyList<Em3dSolverChoice> Solver3DChoices { get; } =
    [
        new(Em3dSolver.None,    "Planar (circuitRF)"),
        new(Em3dSolver.Palace,  "3D — Palace (FEM)"),
        new(Em3dSolver.OpenEms, "3D — openEMS (FDTD)"),
    ];

    [ObservableProperty] private Em3dSolverChoice _solver3DChoice = Solver3DChoices[0];

    /// <summary>True when this setup is a 3D one: the planar controls below are kept but not read.</summary>
    public bool Is3DSetup => Solver3DChoice.Value != Em3dSolver.None;

    /// <summary>True when Palace's section is shown.</summary>
    public bool IsPalaceSetup => Solver3DChoice.Value == Em3dSolver.Palace;

    /// <summary>True when openEMS's section is shown (brief-em3d-9 R-em3d9-6).</summary>
    public bool IsOpenEmsSetup => Solver3DChoice.Value == Em3dSolver.OpenEms;

    public string Solver3DDescription => Solver3DChoice.Value switch
    {
        Em3dSolver.Palace =>
            "Generates a 3D model from the layout, its technology and any bond wires, meshes it with Gmsh and " +
            "solves it with Palace, both installed separately (Settings ▸ 3D EM). The planar settings below are " +
            "kept but not used.",
        Em3dSolver.OpenEms =>
            "Generates a 3D model from the layout, its technology and any bond wires, places circuitRF's own FDTD " +
            "grid on it and solves it with openEMS, installed separately (Settings ▸ 3D EM) — once per port. The " +
            "planar settings below are kept but not used.",
        _ => "circuitRF's own planar and cross-section solvers, chosen under Analysis.",
    };

    partial void OnSolver3DChoiceChanged(Em3dSolverChoice value)
    {
        OnPropertyChanged(nameof(Is3DSetup));
        OnPropertyChanged(nameof(IsPalaceSetup));
        OnPropertyChanged(nameof(IsOpenEmsSetup));
        OnPropertyChanged(nameof(Solver3DDescription));
        if (_suppressCommit) return;
        if (value.Value == Working.Solver3D) return;
        var before = SnapshotJson();
        Working.Solver3D = value.Value;
        CommitEdit(before, "Change EM solver");
        Refresh();
    }

    // ── Palace's section, as staged text ─────────────────────────────────────────────────────

    [ObservableProperty] private string _palaceMaxElementWavelengthsText = "";
    [ObservableProperty] private string _palaceEdgeRefinementText        = "";
    [ObservableProperty] private string _palaceGradingText               = "";
    [ObservableProperty] private string _palaceElementOrderText          = "";
    [ObservableProperty] private string _palaceAdaptiveTolText           = "";
    [ObservableProperty] private string _palaceAdaptiveMaxIterationsText = "";
    [ObservableProperty] private string _palaceSweepAdaptiveTolText      = "";
    [ObservableProperty] private string? _palaceFieldError;

    /// <summary>What a blank box stands for — the one set of defaults the writers read.</summary>
    public static string PalaceDefaultMaxElementWavelengths => G(PalaceSettings.Default.MaxElementWavelengths);
    public static string PalaceDefaultEdgeRefinement        => G(PalaceSettings.Default.EdgeRefinement);
    public static string PalaceDefaultGrading               => G(PalaceSettings.Default.Grading);
    public static string PalaceDefaultElementOrder          => PalaceSettings.Default.ElementOrder.ToString(CultureInfo.InvariantCulture);
    public static string PalaceDefaultAdaptiveTol           => G(PalaceSettings.Default.AdaptiveTol);
    public static string PalaceDefaultAdaptiveMaxIterations => PalaceSettings.Default.AdaptiveMaxIterations.ToString(CultureInfo.InvariantCulture);
    public static string PalaceDefaultSweepAdaptiveTol      => G(PalaceSettings.Default.SweepAdaptiveTol);

    private void SyncSolver3DFields()
    {
        _suppressCommit = true;
        Solver3DChoice = Solver3DChoices.FirstOrDefault(c => c.Value == Working.Solver3D)
                         ?? new Em3dSolverChoice(Working.Solver3D, Working.Solver3D.ToString());
        var p = Working.Palace;
        PalaceMaxElementWavelengthsText = G(p?.MaxElementWavelengths);
        PalaceEdgeRefinementText        = G(p?.EdgeRefinement);
        PalaceGradingText               = G(p?.Grading);
        PalaceElementOrderText          = p?.ElementOrder?.ToString(CultureInfo.InvariantCulture) ?? "";
        PalaceAdaptiveTolText           = G(p?.AdaptiveTol);
        PalaceAdaptiveMaxIterationsText = p?.AdaptiveMaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
        PalaceSweepAdaptiveTolText      = G(p?.SweepAdaptiveTol);
        PalaceFieldError = null;
        SyncOpenEmsFields();
        _suppressCommit = false;
    }

    /// <summary>
    /// Commits one Palace field — <c>Palace.&lt;Field&gt;</c> is its tag. Blank writes the field as
    /// omitted (the default); an invalid value stays in the box beside the message and writes nothing.
    /// </summary>
    public void CommitPalaceField(string tag)
    {
        var section = Working.Palace?.Clone() ?? new CemPalace();
        string? error = null;

        bool Real(string text, Func<double, bool> ok, string rule, Action<double?> set)
        {
            if (text.Trim().Length == 0) { set(null); return true; }
            if (TryDouble(text, out double v) && ok(v)) { set(v); return true; }
            error = rule;
            return false;
        }
        bool Whole(string text, int min, string rule, Action<int?> set)
        {
            if (text.Trim().Length == 0) { set(null); return true; }
            if (TryInt(text, min, out int v)) { set(v); return true; }
            error = rule;
            return false;
        }

        switch (tag)
        {
            case "Palace.MaxElementWavelengths":
                Real(PalaceMaxElementWavelengthsText, v => v > 0, "Enter a positive fraction of a wavelength, e.g. 0.1.",
                     v => section.MaxElementWavelengths = v);
                break;
            case "Palace.EdgeRefinement":
                Real(PalaceEdgeRefinementText, v => v > 0 && v <= 1, "Enter a number above 0 and at most 1.",
                     v => section.EdgeRefinement = v);
                break;
            case "Palace.Grading":
                Real(PalaceGradingText, v => v > 1, "Enter a number greater than 1.", v => section.Grading = v);
                break;
            case "Palace.ElementOrder":
                Whole(PalaceElementOrderText, 1, "Enter a whole number from 1 to 6.", v => section.ElementOrder = v);
                if (section.ElementOrder > 6) error = "Enter a whole number from 1 to 6.";
                break;
            case "Palace.AdaptiveTol":
                Real(PalaceAdaptiveTolText, v => v > 0, "Enter a positive tolerance, e.g. 0.01.", v => section.AdaptiveTol = v);
                break;
            case "Palace.AdaptiveMaxIterations":
                Whole(PalaceAdaptiveMaxIterationsText, 0, "Enter a whole number of 0 or more (0 solves the first mesh).",
                      v => section.AdaptiveMaxIterations = v);
                break;
            case "Palace.SweepAdaptiveTol":
                Real(PalaceSweepAdaptiveTolText, v => v >= 0, "Enter a tolerance of 0 or more, e.g. 0.0001.",
                     v => section.SweepAdaptiveTol = v);
                break;
            default:
                return;
        }

        PalaceFieldError = error;
        if (error is not null) return;

        var before = SnapshotJson();
        Working.Palace = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;      // the same value, retyped: no undo entry
        CommitEdit(before, $"Change Palace setting {tag["Palace.".Length..]}");
    }

    private static string G(double? v) => v?.ToString("G6", CultureInfo.InvariantCulture) ?? "";
}
