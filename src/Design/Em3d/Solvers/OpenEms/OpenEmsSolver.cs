// brief-emi-1 R-emi1-1b — openEMS: a finite-difference time-domain (FDTD) solver on circuitRF's own grid. Everything the
// rest of circuitRF asks about openEMS as a solver is answered here; its run is Em3dRunService.OpenEms.cs beside this file.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class OpenEmsSolver
{
    public const Em3dSolverId Id = Em3dSolverId.OpenEms;

    public static Em3dSolverDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "openEMS",
        Spelling = "OpenEms",
        ResultToken = "openems",
        Method = Em3dSolverFit.Fdtd,
        Capabilities = new()
        {
            Fields = true,
            Problems = new HashSet<Em3dProblemType> { Em3dProblemType.Driven },
            RadiationPattern = true,
            Sar = true,
            // brief-hzn-12 — a plane wave on a total-field/scattered-field box, its far field on the pattern's surface
            PlaneWave = true,
        },
        // openEMS takes circuitRF's own grid and needs nothing else.
        Tools = [SolverTool.OpenEms],
        SetupDescription =
            "Generates a 3D model from the layout, its technology and any bond wires, places circuitRF's own FDTD " +
            "grid on it and solves it with openEMS, installed separately (Settings ▸ Solvers) — once per port.",
        SweepText = _ => "openEMS runs once in the time domain and takes every point from that run",
        SavedFieldsGHz = setup => setup.OpenEms?.SaveFieldsGHz,
        SetSavedFieldsGHz = (setup, ghz) =>
        {
            var section = setup.OpenEms?.Clone() ?? new CemOpenEms();
            section.SaveFieldsGHz = ghz;
            setup.OpenEms = section;
        },
        Refusal = Refusal,
        // brief-em3d-120 C5 — a cylindrical grid is a 3D view's own setup's.
        CemRefusal = setup => CemOpenEms.CemCylindricalRefusal(setup.OpenEms),
        Check = Check,
        Fidelity = Fidelity,
        NewLeg = () => new Em3dRunService.OpenEmsLeg(),
    };

    /// <summary>
    /// brief-em3d-22 R-em3d22-1b / brief-em3d-23 R-em3d23-2e — the problems a time-domain solver has no solver for: the
    /// static ones and an eigenmode solve, refused naming the solver that does them.
    /// </summary>
    private static string? Refusal(EmSetup setup)
    {
        string on = setup.Solver3D.Solvers.Count switch
        {
            2 => "both solvers",
            > 2 => Em3dSolvers.Names(Em3dSolvers.Of(setup.Solver3D)),
            _ => Descriptor.DisplayName,
        };
        if (setup.IsStatic3D)
        {
            var (only, many, remedy) = OnlyOn(setup.Problem3D);
            return $"This setup asks for a{(setup.Problem3D == Em3dProblemType.Electrostatic ? "n electrostatic" : " magnetostatic")} " +
                   $"solve on {on}, and only {only} {(many ? "solve" : "solves")} the static problems: openEMS is a time-domain solver and has no static " +
                   "one. " + remedy;
        }
        if (setup.Problem3D == Em3dProblemType.Eigenmode)
        {
            var (only, many, remedy) = OnlyOn(setup.Problem3D);
            return $"This setup asks for an eigenmode solve on {on}, and only {only} {(many ? "find" : "finds")} eigenmodes: openEMS is a " +
                   "time-domain (FDTD) solver and has no eigensolver. " + remedy;
        }
        return null;
    }

    /// <summary>The solvers that solve <paramref name="type"/>, by name, whether they are several (the verb's number), and the
    /// sentence that moves the setup to the first.</summary>
    private static (string Only, bool Many, string Remedy) OnlyOn(Em3dProblemType type)
    {
        var able = Em3dSolvers.With(c => c.Problems.Contains(type));
        var first = able[0];
        return (Em3dSolvers.Names(able), able.Count > 1,
                $"Set the setup's Solver3D to {first.Spelling}, or run it with `circuitrf em --solver {first.ResultToken}`.");
    }

    /// <summary>brief-em3d-31 / brief-em3d-116 — what openEMS's lowering would refuse of a built problem: a radiation
    /// pattern's surface in a box too tight for it, and a wave port it cannot build. Each from the run's own planner.</summary>
    private static Em3dSolverCheck Check(Em3dProblem problem, EmSetup setup, string? settingsPath)
    {
        var refusals = new List<string>();
        if (setup.RadiationPattern && problem.Type == Em3dProblemType.Driven)
        {
            var gridSettings = CemOpenEms.ResolveGrid(setup.OpenEms, settingsPath);
            if (gridSettings.Problems().Count == 0)
            {
                var grid = FdtdGrid.Build(problem, gridSettings);
                if (OpenEmsFarField.Place(problem, grid, [problem.Frequency.StartHz], gridSettings.PmlCells).Refusal is { } tight)
                    refusals.Add(tight);
            }
        }
        if (refusals.Count == 0 &&
            Em3dRunService.OpenEmsWavePortRefusal(problem, CemOpenEms.ResolveGrid(setup.OpenEms, settingsPath)) is { } noWave)
            refusals.Add(noWave);
        return new(refusals, []);
    }

    private static IEnumerable<Em3dFidelityFinding> Fidelity(Em3dProblem problem, EmSetup setup)
    {
        if (problem.IsStatic || problem.Type == Em3dProblemType.Eigenmode) return [];
        var grid = CemOpenEms.ResolveGrid(setup.OpenEms);
        if (grid.Problems().Count > 0) return [];
        try { return [.. Em3dFidelity.For(problem, Em3dFidelitySolver.OpenEms, FdtdGrid.Build(problem, grid, long.MaxValue))]; }
        catch (InvalidOperationException) { return []; /* the run reports a grid it cannot build; nothing to measure against */ }
    }
}
