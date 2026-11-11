// brief-emi-1 R-emi1-1b — Palace: a finite-element (FEM) frequency-domain solver, meshed by Gmsh. Everything the rest of
// circuitRF asks about Palace as a solver is answered here; its run is Em3dRunService.Palace.cs beside this file.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class PalaceSolver
{
    public const Em3dSolverId Id = Em3dSolverId.Palace;

    public static Em3dSolverDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "Palace",
        Spelling = "Palace",
        ResultToken = "palace",
        Method = Em3dSolverFit.Fem,
        Quality = s => s.Palace?.Quality ?? PalaceQuality.Standard,
        Capabilities = new()
        {
            Fields = true,
            FieldArraysKnownAhead = true,
            Problems = new HashSet<Em3dProblemType>
            {
                Em3dProblemType.Driven, Em3dProblemType.Electrostatic, Em3dProblemType.Magnetostatic, Em3dProblemType.Eigenmode,
            },
            RadiationPattern = true,
            Gpu = true,
            GmshMesh = true,
            MeshRegions = true,
        },
        // Palace meshes through Gmsh (em-3d.md §6.1).
        Tools = [SolverTool.Palace, SolverTool.Gmsh],
        SetupDescription =
            "Generates a 3D model from the layout, its technology and any bond wires, meshes it with Gmsh and " +
            "solves it with Palace, both installed separately (Settings ▸ Solvers).",
        SweepText = SweepText,
        SavedFieldsGHz = setup => setup.Palace?.SaveFieldsGHz,
        SetSavedFieldsGHz = (setup, ghz) =>
        {
            var section = setup.Palace?.Clone() ?? new CemPalace();
            section.SaveFieldsGHz = ghz;
            setup.Palace = section;
        },
        PortRefusal = Em3dRunService.PalacePortRefusal,
        Check = Check,
        Fidelity = Fidelity,
        MemoryVerdict = Em3dRunService.PalaceMemoryVerdict,
        NewLeg = () => new Em3dRunService.PalaceLeg(),
    };

    /// <summary>
    /// Designer feedback round 11 — Palace with a sweep tolerance builds the sweep ADAPTIVELY: it solves the full problem at
    /// as few frequencies as reach the tolerance and evaluates every requested point from the model those solves make —
    /// which is why its closing line counts "full solves" that are not the requested points. The planar setup's own
    /// Adaptive sampling box plays no part in it.
    /// </summary>
    private static string SweepText(EmSetup setup)
    {
        var settings = PalaceSettings.Resolve(setup.Palace);
        string quality = (setup.Palace?.Quality ?? PalaceQuality.Standard).ToString();
        return settings.SweepAdaptiveTol > 0
            ? $"Palace builds the sweep adaptively (tolerance {settings.SweepAdaptiveTol.ToString("G3", CultureInfo.InvariantCulture)}, " +
              $"the {quality} preset's): it solves the full problem at as few frequencies as reach that tolerance and " +
              "evaluates every requested point from them"
            : "Palace solves the full problem at every point";
    }

    /// <summary>brief-em3d-31 — Palace goes on without a radiation pattern it cannot place, and says why.</summary>
    private static Em3dSolverCheck Check(Em3dProblem problem, EmSetup setup, string? settingsPath)
        => setup.RadiationPattern && problem.Type == Em3dProblemType.Driven && PalaceConfigWriter.FarFieldRefusal(problem) is { } noPattern
            ? new([], [noPattern])
            : Em3dSolverCheck.Clean;

    private static IEnumerable<Em3dFidelityFinding> Fidelity(Em3dProblem problem, EmSetup setup)
    {
        var settings = PalaceSettings.Resolve(setup.Palace);
        double? smallest = settings.Problems().Count == 0 ? GmshGeoWriter.SmallestRequestedSizeM(problem, settings) : null;
        return Em3dFidelity.For(problem, Em3dFidelitySolver.Palace, null, smallest);
    }
}
