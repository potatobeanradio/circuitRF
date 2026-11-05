// brief-em3d-7 R-em3d7-6b — the panel's Solver picker and, when Palace is picked, its section's fields.
//
// NOTHING THAT AFFECTS THE ANSWER LIVES ONLY HERE (R-em-11): every control writes a .cem field
// (Solver3D, and the Palace section's seven), through the same undoable CommitEdit every other control
// uses, and the defaults a blank box stands for are PalaceSettings.Default's — the ones the writers
// read. A blank box is the default, and a section with every box blank is written as no section.

using System.Globalization;
using CircuitRF.Engine.Em3d;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>One row of the Palace preset picker (brief-em3d-21 R-em3d21-4).</summary>
public sealed record PalaceLinearSolverChoice(PalaceLinearSolver Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PalaceQualityChoice(PalaceQuality Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the Solver picker. <paramref name="IsThermal"/>: circuitRF's own thermal solver — a setup whose problem is
/// Thermal and which names no EM solver (brief 73), so <paramref name="Value"/> is None and is not what tells it apart.
/// <paramref name="IsCompare"/> (brief-emi-6): the Compare… row, whose solvers are the checkboxes under it, not its
/// <paramref name="Value"/>. A row a panel offers says whether its solver can run here (<see cref="IsEnabled"/>), and why not.</summary>
public sealed partial class Em3dSolverPickerRow(Em3dSolverChoice Value, string Label, bool IsThermal = false, bool IsCompare = false)
    : ObservableObject
{
    public Em3dSolverChoice Value { get; } = Value;
    public string Label { get; } = Label;
    public bool IsThermal { get; } = IsThermal;
    public bool IsCompare { get; } = IsCompare;

    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>The disabled row's tooltip: not installed, or the solver's own refusal of this setup.</summary>
    [ObservableProperty] private string? _disabledReason;

    /// <summary>A fresh row with this one's value and label, for a panel's own list.</summary>
    public Em3dSolverPickerRow Copy() => new(this.Value, this.Label, this.IsThermal, this.IsCompare);

    public override string ToString() => Label;
}

public sealed partial class EmSetupEditorViewModel
{
    /// <summary>Planar, each 3D solver (brief-emi-1: the registry's, in its order), and Compare… — any set of them on one problem
    /// (brief 10; brief-emi-6). The template every panel copies its own rows from (<see cref="Solver3DChoiceList"/>): a
    /// panel's rows carry whether each solver can run there, and that is the panel's, not every panel's.</summary>
    public static IReadOnlyList<Em3dSolverPickerRow> Solver3DChoices { get; } =
    [
        new(Em3dSolverChoice.None, "Planar (circuitRF)"),
        .. CircuitRF.Design.Em3d.Em3dSolvers.All.Select(d => new Em3dSolverPickerRow(d.Id, d.PickerLabel)),
        // brief-emi-6 R-emi6-2 — the set and reference are the checkboxes and combo under the picker. Its value is the format's
        // Both, the set a setup starts on when Compare is chosen.
        new(CircuitRF.Design.Em3d.Em3dSolvers.Both, "Compare…", IsCompare: true),
    ];

    /// <summary>The Solver picker's Thermal row, offered by a 3D view's panel: who solves a thermal setup is a solver choice,
    /// beside Palace and openEMS, not a kind of EM problem (owner request, 2026-09-28). The file is unchanged: Problem3D
    /// Thermal, Solver3D None.</summary>
    public static Em3dSolverPickerRow ThermalSolverChoice { get; } = new(Em3dSolverChoice.None, "Thermal", IsThermal: true);

    [ObservableProperty] private Em3dSolverPickerRow _solver3DChoice = Solver3DChoices[0];

    /// <summary>True when this setup is a 3D one: the planar controls below are kept but not read.</summary>
    public bool Is3DSetup => !Solver3DChoice.Value.IsNone || IsThermalSetup;

    // What a 3D run does NOT read is hidden while one is chosen, rather than shown and ignored. What
    // it DOES read stays: the frequency sweep, each port's Z0 (EmSetup.ResolvePortZ0), the solve region
    // and the core cap. The hidden fields stay in the .cem untouched, so switching back to Planar finds
    // them as they were. brief-dfr28-4: the return plane is not among them — each 3D port walks the
    // artwork to its own return, and the 3D port table's Return column states one where it must.

    /// <summary>The planar kernel's own controls — surface mesh, analysis levels, adaptive sampling,
    /// the port-type combo — shown only when the planar kernel is what runs.</summary>
    public bool ShowPlanarControls => IsPlanarAnalysis && !Is3DSetup;

    /// <summary>The cross-section kernel's mesh group, likewise.</summary>
    public bool ShowCrossSectionControls => !IsPlanarAnalysis && !Is3DSetup;

    /// <summary>The return plane: the planar kernel's alone. brief-dfr28-4 R-dfr28-4-4 — no 3D setup shows it: a 3D port
    /// finds its return in the artwork, and a port whose return is not the walk's choice states its own in the 3D port
    /// table. A <c>.cem</c> that already names one keeps honouring it (D2), unseen.</summary>
    public bool ShowReturnPlane => IsPlanarAnalysis && !Is3DSetup;

    /// <summary>The Conductors group: shown while anything in it is (the signal conductor and analysis levels for the planar
    /// kernels, the planar return plane) — so never for a 3D setup.</summary>
    public bool ShowConductorsGroup => ShowCircuitRfSolverControls || ShowReturnPlane;

    /// <summary>Controls every circuitRF-kernel run reads and no 3D run does (the analysis kind and
    /// kernel beside the solver, the Notes group, the signal conductor, the planar/cross-section
    /// solver options, the radiation pattern).</summary>
    public bool ShowCircuitRfSolverControls => !Is3DSetup;

    /// <summary>The Mesh button. It meshes for circuitRF's own kernels (the planar MoM mesh or the cross-section
    /// grid) and no 3D solver reads either: Gmsh meshes a 3D run as part of Simulate. Shown on a 3D setup it
    /// answered with a mesh the run never uses, or with "no cross-section" in a .c3d (owner report, 2026-09-28). Off while
    /// a mesh is in flight, when its Cancel takes the cell.</summary>
    public bool ShowMeshButton => !Is3DSetup && !IsMeshing;

    /// <summary>The Stackup group. A .c3d setup has no layout and no technology — its geometry is the 3D model — so
    /// the group could only ever show an empty header there. A .cem 3D setup keeps it: the generator builds its
    /// model from that stackup.</summary>
    public bool ShowStackup => !IsEmbedded;

    /// <summary>
    /// brief-em3d-31 R-em3d31-4 — the Radiation pattern group: the planar kernel, and every 3D solver, since each
    /// now produces the farfield group (openEMS by its own surface transform, Palace by its far-field integral).
    /// Its own property rather than a widening of <see cref="ShowCircuitRfSolverControls"/>, which still hides
    /// what no 3D run reads.
    /// </summary>
    public bool ShowRadiationPattern =>
        !Is3DSetup || CircuitRF.Design.Em3d.Em3dSolvers.Any(Working.Solver3D, c => c.RadiationPattern);

    /// <summary>brief-dfr28-3 — Robust meshing is every 3D EM solver's (they mesh the same outlines); a planar or thermal
    /// setup does not read it.</summary>
    public bool ShowRobustMeshing => Is3DSetup && !IsThermalSetup;

    /// <summary>The tooltip, with the tolerance written from the constants the cleanup reads.</summary>
    public static string RobustMeshingTip { get; } =
        "Removes outline points that sit within a fraction of a micrometre of the line through their neighbours (at most " +
        $"{CircuitRF.Design.Layout.Em3d.Em3dOutlineCleanup.MaxToleranceM * 1e6:0.#} µm, or 1/" +
        $"{1 / CircuitRF.Design.Layout.Em3d.Em3dOutlineCleanup.ThicknessFraction:0} of the layer's thickness if that is less). " +
        "Imported artwork often has them — arcs broken into short segments, tiny edges left where shapes were merged — and " +
        "they can make the mesher build a degenerate element that stops the run. No outline moves by more than that " +
        "tolerance, and the run's notes say how many points were removed and how far. Off meshes the artwork exactly as drawn.";

    private void RaiseSolverKindVisibility()
    {
        OnPropertyChanged(nameof(ShowRobustMeshing));
        OnPropertyChanged(nameof(ShowMeshFrequency3D));
        OnPropertyChanged(nameof(ShowComponentPorts));
        OnPropertyChanged(nameof(ShowPlanarControls));
        OnPropertyChanged(nameof(ShowCrossSectionControls));
        OnPropertyChanged(nameof(ShowReturnPlane));
        OnPropertyChanged(nameof(ShowConductorsGroup));
        OnPropertyChanged(nameof(ShowCircuitRfSolverControls));
        OnPropertyChanged(nameof(ShowMeshButton));
        OnPropertyChanged(nameof(ShowRadiationPattern));
        OnPropertyChanged(nameof(RadiationPatternDisabledReason));
        OnPropertyChanged(nameof(ReferenceInputPowerEnabled));
        OnPropertyChanged(nameof(ShowNotesGroup));
    }

    /// <summary>True when Palace's section is shown: Palace is one of the solvers the setup runs (brief-emi-6 R-emi6-2).</summary>
    public bool IsPalaceSetup => !IsThermalSetup && Working.Solver3D.Runs(CircuitRF.Design.Em3d.PalaceSolver.Id);

    /// <summary>True when openEMS's section is shown (brief-em3d-9 R-em3d9-6).</summary>
    public bool IsOpenEmsSetup => !IsThermalSetup && Working.Solver3D.Runs(CircuitRF.Design.Em3d.OpenEmsSolver.Id);

    private const string PlanarHiddenNote =
        "The planar-only settings are hidden; they are kept, and come back if you switch to Planar.";

    public string Solver3DDescription => Solver3DChoice.IsThermal
        ? "Temperature, solved by circuitRF's own thermal solver on a mesh Gmsh makes (Settings ▸ Solvers): the heat sources, " +
          "boundaries and probes drawn in this view, their values below."
        : Solver3DChoice.Value is { IsNone: true } ? "circuitRF's own planar and cross-section solvers, chosen under Analysis."
        : Working.Solver3D.IsCompare
            ? "Generates one 3D model from the layout, its technology and any bond wires and solves it with " +
              string.Join(", then with ", CircuitRF.Design.Em3d.Em3dSolvers.Of(Working.Solver3D).Select(d => d.DisplayName)) +
              " (Settings ▸ Solvers), writing each solver's result and a comparison of each against the reference, " +
              CircuitRF.Design.Em3d.Em3dSolvers.Get(Working.Solver3D.Reference).DisplayName + ". " + PlanarHiddenNote
            : CircuitRF.Design.Em3d.Em3dSolvers.Get(Working.Solver3D.Reference).SetupDescription + " " + PlanarHiddenNote;

    partial void OnSolver3DChoiceChanged(Em3dSolverPickerRow value)
    {
        RaiseSolverDependents();
        if (_suppressCommit) return;
        bool wasThermal = Working.Problem3D == Em3dProblemType.Thermal;
        // brief-emi-6 — Compare… keeps a comparison the setup already has, and otherwise starts on CompareStart's set.
        var target = value.IsCompare ? Working.Solver3D.IsCompare ? Working.Solver3D : CompareStart() : value.Value;
        if (value.IsThermal == wasThermal && target == Working.Solver3D) return;
        var before = SnapshotJson();
        Working.Solver3D = target;
        // Thermal is a solver here and a problem in the file: choosing it or leaving it changes both. A thermal setup names
        // no EM solver (brief 73); leaving it solves the EM problem an EM setup starts with.
        if (value.IsThermal) { Working.Problem3D = Em3dProblemType.Thermal; Working.Thermal ??= new CircuitRF.Design.Layout.Em.CemThermal(); }
        else if (wasThermal) Working.Problem3D = Em3dProblemType.Driven;
        CommitEdit(before, "Change EM solver");
        if (value.IsThermal != wasThermal)
        {
            _suppressCommit = true;
            try { Problem3DChoice = Problem3DChoices.FirstOrDefault(c => c.Value == Working.Problem3D) ?? Problem3DChoices[0]; }
            finally { _suppressCommit = false; }
            SyncThermalFields();
            RaiseThermalVisibility();
            RaiseStaticVisibility();
        }
        SyncCompareFields();
        RaiseSolverDependents();
        Refresh();
    }

    /// <summary>What the solver choice decides on screen: the sections shown, the description, and the Run gate.</summary>
    private void RaiseSolverDependents()
    {
        OnPropertyChanged(nameof(Is3DSetup));
        OnPropertyChanged(nameof(IsPalaceSetup));
        OnPropertyChanged(nameof(IsOpenEmsSetup));
        OnPropertyChanged(nameof(IsEmergeSetup));
        OnPropertyChanged(nameof(ShowPalaceTitle));
        OnPropertyChanged(nameof(ShowOpenEmsTitle));
        OnPropertyChanged(nameof(ShowEmergeTitle));
        OnPropertyChanged(nameof(IsSarSetup));
        OnPropertyChanged(nameof(Solver3DDescription));
        OnPropertyChanged(nameof(IsCompareSetup));
        OnPropertyChanged(nameof(CompareBlockingReason));
        OnPropertyChanged(nameof(BlockingReason));
        OnPropertyChanged(nameof(SimulateTip));
        OnPropertyChanged(nameof(CanRun));
        SimulateCommand.NotifyCanExecuteChanged();
        RaiseSolverKindVisibility();
        RaiseStaticVisibility();
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

    // ── brief-em3d-21 R-em3d21-4: the preset ─────────────────────────────────────────────────

    /// <summary>The three presets. Standard is today's defaults; the tooltip carries what each one
    /// costs and how far it lands from Accurate, as measured (src/Design/RESOLVED.md §brief-em3d-21).</summary>
    public static IReadOnlyList<PalaceQualityChoice> PalaceQualityChoices { get; } =
    [
        new(PalaceQuality.Draft,    "Draft"),
        new(PalaceQuality.Standard, "Standard"),
        new(PalaceQuality.Accurate, "Accurate"),
    ];

    /// <summary>What each preset trades, as measured on F0's cases A and B. The figures are the
    /// measurement's, not estimates; a preset whose deviation grew would change them.</summary>
    public static string PalaceQualityTip => CircuitRF.Design.Em3d.PalacePresetTable.Tooltip;

    [ObservableProperty] private PalaceQualityChoice _palaceQualityChoice = PalaceQualityChoices[1];

    partial void OnPalaceQualityChoiceChanged(PalaceQualityChoice value)
    {
        RaisePalacePlaceholders();
        if (_suppressCommit) return;
        var section = Working.Palace?.Clone() ?? new CemPalace();
        // Standard is written as no field at all, so choosing it on a setup that never named one is no edit.
        section.Quality = value.Value == PalaceQuality.Standard ? null : value.Value;
        var before = SnapshotJson();
        Working.Palace = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change Palace preset");
    }

    // ── brief-em3d-52: the linear solver ─────────────────────────────────────────────────────

    public static IReadOnlyList<PalaceLinearSolverChoice> PalaceLinearSolverChoices { get; } =
    [
        new(PalaceLinearSolver.Iterative, "Iterative"),
        new(PalaceLinearSolver.Direct,    "Direct"),
    ];

    public const string PalaceLinearSolverTip =
        "How Palace solves each linear system. Iterative (the default) needs the least memory. Direct factors it " +
        "with SuperLU_DIST: more memory, and far faster for an eigenmode solve, whose shifted operator an iterative " +
        "solver converges on slowly — the 3D Package example's lid mode took 35 s Direct, and had not finished " +
        "after ten minutes Iterative.";

    [ObservableProperty] private PalaceLinearSolverChoice _palaceLinearSolverChoice = PalaceLinearSolverChoices[0];

    partial void OnPalaceLinearSolverChoiceChanged(PalaceLinearSolverChoice value)
    {
        if (_suppressCommit) return;
        var section = Working.Palace?.Clone() ?? new CemPalace();
        // Iterative is written as no field at all, as Standard is.
        section.LinearSolver = value.Value == PalaceLinearSolver.Iterative ? null : value.Value;
        var before = SnapshotJson();
        Working.Palace = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;
        CommitEdit(before, "Change Palace linear solver");
    }

    private PalaceSettings PresetShown => PalaceSettings.Preset(PalaceQualityChoice.Value);

    /// <summary>What a blank box stands for — the chosen preset's value, the one the writers read.</summary>
    public string PalaceDefaultMaxElementWavelengths => G(PresetShown.MaxElementWavelengths);
    public string PalaceDefaultEdgeRefinement        => G(PresetShown.EdgeRefinement);
    public string PalaceDefaultGrading               => G(PresetShown.Grading);
    public string PalaceDefaultElementOrder          => PresetShown.ElementOrder.ToString(CultureInfo.InvariantCulture);
    public string PalaceDefaultAdaptiveTol           => G(PresetShown.AdaptiveTol);
    public string PalaceDefaultAdaptiveMaxIterations => PresetShown.AdaptiveMaxIterations.ToString(CultureInfo.InvariantCulture);
    public string PalaceDefaultSweepAdaptiveTol      => G(PresetShown.SweepAdaptiveTol);

    private void RaisePalacePlaceholders()
    {
        OnPropertyChanged(nameof(PalaceDefaultMaxElementWavelengths));
        OnPropertyChanged(nameof(PalaceDefaultEdgeRefinement));
        OnPropertyChanged(nameof(PalaceDefaultGrading));
        OnPropertyChanged(nameof(PalaceDefaultElementOrder));
        OnPropertyChanged(nameof(PalaceDefaultAdaptiveTol));
        OnPropertyChanged(nameof(PalaceDefaultAdaptiveMaxIterations));
        OnPropertyChanged(nameof(PalaceDefaultSweepAdaptiveTol));
    }

    private void SyncSolver3DFields()
    {
        _suppressCommit = true;
        Solver3DChoice = IsThermalSetup ? ThermalSolverChoice
                       : Working.Solver3D.IsCompare ? _solverRows.Single(c => c.IsCompare)
                       : _solverRows.FirstOrDefault(c => !c.IsCompare && c.Value == Working.Solver3D)
                         ?? new Em3dSolverPickerRow(Working.Solver3D, Working.Solver3D.ToString());
        SyncCompareFields();
        _suppressCommit = true;
        var p = Working.Palace;
        PalaceQualityChoice = PalaceQualityChoices.First(c => c.Value == (p?.Quality ?? PalaceQuality.Standard));
        PalaceLinearSolverChoice = PalaceLinearSolverChoices.First(c => c.Value == (p?.LinearSolver ?? PalaceLinearSolver.Iterative));
        PalaceMaxElementWavelengthsText = G(p?.MaxElementWavelengths);
        PalaceEdgeRefinementText        = G(p?.EdgeRefinement);
        PalaceGradingText               = G(p?.Grading);
        PalaceElementOrderText          = p?.ElementOrder?.ToString(CultureInfo.InvariantCulture) ?? "";
        PalaceAdaptiveTolText           = G(p?.AdaptiveTol);
        PalaceAdaptiveMaxIterationsText = p?.AdaptiveMaxIterations?.ToString(CultureInfo.InvariantCulture) ?? "";
        PalaceSweepAdaptiveTolText      = G(p?.SweepAdaptiveTol);
        PalaceSaveFieldsText            = SaveFieldsText(p?.SaveFieldsGHz);       // brief-em3d-83
        PalaceSaveFields                = SavesAny(p?.SaveFieldsGHz);
        OnPropertyChanged(nameof(SaveFieldsPlaceholder));
        PalaceFieldError = null;
        SyncOpenEmsFields();
        SyncEmergeFields();
        SyncStaticFields();
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
            case "Palace.SaveFieldsGHz":                // brief-em3d-83 R-em3d83-6
                if (TryParseSaveFields(PalaceSaveFieldsText, out var saves, out error))
                {
                    section.SaveFieldsGHz = saves;
                    ShowSaving(() => PalaceSaveFields = SavesAny(saves));
                }
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
