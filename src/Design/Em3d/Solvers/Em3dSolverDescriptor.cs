// brief-emi-1 R-emi1-1b — what a 3D solver is, in one object per solver.
//
// Every question the code used to answer by listing Em3dSolver values has one owner here: the solver's name and
// result token, its method, what it can solve and report, the programs it needs found, what it refuses and in which
// words, and how a run prepares and executes it. A descriptor is a plain object built once, in its solver's own
// folder (Solvers/<Name>/<Name>Solver.cs); Em3dSolvers lists them. See src/Design/RESOLVED.md, "Adding a 3D solver".

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>What a solver can solve and report — the facts a refusal, a picker or a viewer asks about it.</summary>
public sealed record Em3dSolverCapabilities
{
    /// <summary>The problem types it solves. A setup asking for another is refused before anything is looked for.</summary>
    public required IReadOnlySet<Em3dProblemType> Problems { get; init; }

    /// <summary>brief-emi-5 — its run saves fields a field plot can draw, at frequencies a setup lists.</summary>
    public bool Fields { get; init; }

    /// <summary>brief-dfr31-3 — the arrays its run writes for a problem are known before it runs (the FEM writer's fixed
    /// set), so a field plot's Quantity picker can list them ahead of the first run.</summary>
    public bool FieldArraysKnownAhead { get; init; }

    /// <summary>It produces a driven run's far field (the radiation pattern group).</summary>
    public bool RadiationPattern { get; init; }

    /// <summary>It writes the raw dump SAR and B1 are computed on (a setup's Sar group, a band sweep).</summary>
    public bool Sar { get; init; }

    /// <summary>It can solve on a GPU (<c>--device gpu</c>).</summary>
    public bool Gpu { get; init; }

    /// <summary>Its run directory keeps the Gmsh mesh it solved on, which the 3D view can show.</summary>
    public bool GmshMesh { get; init; }

    /// <summary>It reads a problem's mesh regions (they size a Gmsh mesh; a solver that grids on its own ignores them).</summary>
    public bool MeshRegions { get; init; }
}

/// <summary>What a solver refuses of a built problem: the sentence when it runs alone, and the note when it is one of
/// a comparison, which then goes on without it.</summary>
public sealed record Em3dLegRefusal(string Alone, string InCompare);

/// <summary>A problem's multi-terminal wave ports, as a solver's port refusal reads them: one entry per face group, whether
/// a lumped port sits beside them, and a terminal port whose reference plane is moved (and how far).</summary>
public sealed record Em3dTerminalFacts(IReadOnlyList<(string Label, int Terminals)> Ports, bool LumpedPorts,
                                       (string Label, double ShiftM)? Shifted)
{
    /// <summary>brief-emw-3 — the labels of the terminal ports whose face meets one material, so whose modes travel at one
    /// speed; null when not known (facts read from a document, before its problem is built).</summary>
    public IReadOnlyCollection<string>? OneSpeed { get; init; }
}

/// <summary>What <c>check</c> reports of a built problem for one solver: refusals (the run would refuse it) and notes.</summary>
public sealed record Em3dSolverCheck(IReadOnlyList<string> Refusals, IReadOnlyList<string> Notes)
{
    public static Em3dSolverCheck Clean { get; } = new([], []);
}

/// <summary>
/// brief-emi-1 R-emi1-1b — one 3D solver. Built once, in <c>Solvers/&lt;Name&gt;/</c>; <see cref="Em3dSolvers"/> is the
/// only list of them.
/// </summary>
public sealed class Em3dSolverDescriptor
{
    public required Em3dSolverId Id { get; init; }

    /// <summary>Its name in a sentence: <c>Palace</c>, <c>openEMS</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The <c>.cem</c>/<c>.c3d</c> key <c>Solver3D</c>'s spelling for it: <c>Palace</c>, <c>OpenEms</c>.</summary>
    public required string Spelling { get; init; }

    /// <summary>The token a solver adds to a result's name, and <c>em --solver</c>'s word for it: <c>palace</c>,
    /// <c>openems</c>. Unique among the solvers.</summary>
    public required string ResultToken { get; init; }

    public required Em3dSolverFit Method { get; init; }

    public required Em3dSolverCapabilities Capabilities { get; init; }

    /// <summary>The programs it needs found, in the order they are reported. <see cref="SolverDiscovery"/> checks each.</summary>
    public required IReadOnlyList<SolverTool> Tools { get; init; }

    /// <summary>The 3D panel's sentence under the Solver picker when this solver is chosen alone.</summary>
    public required string SetupDescription { get; init; }

    /// <summary>What it does with a frequency sweep, as the Messages panel's sweep row says it.</summary>
    public required Func<EmSetup, string> SweepText { get; init; }

    /// <summary>The frequencies, GHz, its run is told to save fields at — the setup's own list, or null for the default.</summary>
    public required Func<EmSetup, IReadOnlyList<double>?> SavedFieldsGHz { get; init; }

    /// <summary>Sets that list on the setup (a new copy of the solver's settings section).</summary>
    public required Action<EmSetup, List<double>> SetSavedFieldsGHz { get; init; }

    /// <summary>
    /// The setup-level refusal, before anything is looked for: a problem type it does not solve, in its own words. Null
    /// when it runs the setup. <see cref="Em3dRunService.StaticSolverRefusal"/> and <see cref="Em3dRunService.SolverRefusal"/>
    /// ask every chosen solver.
    /// </summary>
    public Func<EmSetup, string?> Refusal { get; init; } = _ => null;

    /// <summary>What a <c>.cem</c> (not a <c>.c3d</c>) may not ask of it, refused before anything is looked for.</summary>
    public Func<EmSetup, string?> CemRefusal { get; init; } = _ => null;

    /// <summary>What it refuses of a problem's multi-terminal wave ports; null when it runs them.</summary>
    public Func<Em3dTerminalFacts, Em3dLegRefusal?> PortRefusal { get; init; } = _ => null;

    /// <summary>What <c>check</c> says of a built problem for it, from its own lowering's rules.
    /// The third argument is the settings file a relative path in its section resolves against.</summary>
    public Func<Em3dProblem, EmSetup, string?, Em3dSolverCheck> Check { get; init; } = (_, _, _) => Em3dSolverCheck.Clean;

    /// <summary>brief-em3d-65 — what it will not respect of a kernel solid, with the settings its run would use.</summary>
    public Func<Em3dProblem, EmSetup, IEnumerable<Em3dFidelityFinding>> Fidelity { get; init; } = (_, _) => [];

    /// <summary>The quality preset a setup runs it at — the setup's own, or the preset's default — or null for a solver
    /// with no preset (openEMS).</summary>
    public Func<EmSetup, PalaceQuality?> Quality { get; init; } = _ => null;

    /// <summary>Its memory estimate for a setup, when it has one.</summary>
    public Func<EmSetup, EmLayoutSource, Em3dMemoryVerdict>? MemoryVerdict { get; init; }

    /// <summary>A fresh leg of one run: its settings, its lowering and its execution (Em3dRunService.Run).</summary>
    internal Func<Em3dRunService.SolverLeg> NewLeg { get; init; } = null!;

    /// <summary>brief-emi-5 — why a field plot cannot ask this solver's run for fields; null when it can.</summary>
    public string? FieldsRefusal
        => Capabilities.Fields ? null
         : $"{DisplayName} saves no fields in this version of circuitRF; a field plot is drawn from a " +
           $"{string.Join(" or ", Em3dSolvers.With(c => c.Fields).Select(d => d.DisplayName))} run.";

    /// <summary><c>FEM</c> or <c>FDTD</c>.</summary>
    public string MethodName => MethodNameOf(Method);

    /// <summary>The Solver picker's row: <c>FEM 3D (Palace)</c>.</summary>
    public string PickerLabel => $"{MethodName} 3D ({DisplayName})";

    public static string MethodNameOf(Em3dSolverFit method) => method == Em3dSolverFit.Fem ? "FEM" : "FDTD";

    public override string ToString() => DisplayName;
}
