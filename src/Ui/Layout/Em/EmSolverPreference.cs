// Owner request, 2026-11-08 — the solver a NEW setup starts with, chosen in Settings ▸ Solvers: one for a new .cem (Planar or a
// 3D solver) and one for a new .c3d setup (a 3D solver, or Thermal: a user who only solves temperature). Per user, in AppPreferences, like the other solver settings beside it,
// and handed to src/Design as an argument where a setup is made there (C3dHierarchy.NewFromLayout).

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

public static class EmSolverPreference
{
    /// <summary>Test seam, as <see cref="EmEmergeDevicePreference"/>'s: a test never writes the real preferences file.</summary>
    internal static bool TestOverrideActive;
    internal static string? TestLayoutStore, TestThreeDStore;

    /// <summary>The 3D solvers a setting can name, in the order the Settings combos list them after Planar.</summary>
    public static IReadOnlyList<Em3dSolverId> ThreeDChoices { get; } = [Em3dSolverId.Palace, Em3dSolverId.Emerge, Em3dSolverId.OpenEms];

    /// <summary>A combo row's words: <c>Palace (FEM)</c>, <c>EMerge (FEM)</c>, <c>openEMS (FDTD)</c>.</summary>
    public static string Label(Em3dSolverId id) => id switch
    {
        Em3dSolverId.Emerge  => "EMerge (FEM)",
        Em3dSolverId.OpenEms => "openEMS (FDTD)",
        _                    => "Palace (FEM)",
    };

    public const string PlanarLabel = "Planar (MoM)";
    public const string ThermalLabel = "Thermal (FEM)";

    /// <summary>What is stored for Thermal as the 3D preference.</summary>
    public const string ThermalStored = "thermal";

    /// <summary>What is stored for a solver, and what a stored value names (anything unknown names none).</summary>
    public static string ToStored(Em3dSolverId id) => id.ToString().ToLowerInvariant();
    public static Em3dSolverId? FromStored(string? stored)
        => ThreeDChoices.Cast<Em3dSolverId?>().FirstOrDefault(id => string.Equals(ToStored(id!.Value), stored, StringComparison.OrdinalIgnoreCase));

    /// <summary>A new <c>.cem</c>'s solver: <see cref="Em3dSolverChoice.None"/> is Planar, the default.</summary>
    public static Em3dSolverChoice Layout
    {
        get => FromStored(TestOverrideActive ? TestLayoutStore : AppPreferencesIo.Load().EmPreferredLayoutSolver) is { } id
            ? Em3dSolverChoice.One(id) : Em3dSolverChoice.None;
        set
        {
            string? stored = value.IsNone ? null : ToStored(value.Reference);
            if (TestOverrideActive) TestLayoutStore = stored;
            else AppPreferencesIo.Update(p => p.EmPreferredLayoutSolver = stored);
        }
    }

    private static string? ThreeDStored => TestOverrideActive ? TestThreeDStore : AppPreferencesIo.Load().EmPreferred3DSolver;

    private static void StoreThreeD(string? stored)
    {
        if (TestOverrideActive) TestThreeDStore = stored;
        else AppPreferencesIo.Update(p => p.EmPreferred3DSolver = stored);
    }

    /// <summary>A new <c>.c3d</c> EM setup's solver: Palace (<see cref="Em3dSolvers.Default"/>) unless another is stored. With
    /// Thermal preferred it is Palace: a setup that must solve the EM problem (one made from a Planar .cem's component ports)
    /// still needs an EM solver.</summary>
    public static Em3dSolverId ThreeD
    {
        get => FromStored(ThreeDStored) ?? Em3dSolvers.Default.Reference;
        set => StoreThreeD(value == Em3dSolvers.Default.Reference ? null : ToStored(value));
    }

    /// <summary>Thermal is the 3D preference: the editor's Add makes a thermal setup. Setting it false stores the default (Palace).</summary>
    public static bool ThreeDIsThermal
    {
        get => string.Equals(ThreeDStored, ThermalStored, StringComparison.OrdinalIgnoreCase);
        set => StoreThreeD(value ? ThermalStored : null);
    }
}
