// Hands CircuitRF.Design the one thing 3D EM solver discovery needs from the application: which Palace,
// Gmsh and openEMS the user named in Settings ▸ 3D EM (brief-em3d-6 R-em3d6-1a).
//
// The preferences live above the firewall in AppPreferences and CircuitRF.Design may not reach them,
// so SolverDiscovery exposes the seam and this fills it — exactly as GitPathInstaller fills
// GitDiscovery's and VerilogACompilerInstaller fills VerilogACompilerDiscovery's.
//
// A MODULE INITIALIZER for the reason those two give: it runs before any type in this assembly is
// touched, so there is no startup ordering to get wrong and no second entry point to remember.
//
// Unset — which is what a headless `circuitrf` process gets — discovery falls back to CIRCUITRF_PALACE /
// CIRCUITRF_GMSH / CIRCUITRF_OPENEMS, then PATH, then the default directories. That is deliberate: the
// environment variable is the headless process's way of naming a program.

using System.Runtime.CompilerServices;
using CircuitRF.Design.Em3d;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

internal static class Em3dSolverPathInstaller
{
    [ModuleInitializer]
    internal static void Install()
    {
        // Read through the Func on every call rather than captured once: a user who names a program in
        // Settings and runs again must get the one they just named.
        SolverDiscovery.Palace.PreferredCommand  = () => AppPreferencesIo.Load().Em3dPalacePath;
        SolverDiscovery.Gmsh.PreferredCommand    = () => AppPreferencesIo.Load().Em3dGmshPath;
        SolverDiscovery.OpenEms.PreferredCommand = () => AppPreferencesIo.Load().Em3dOpenEmsPath;
    }
}
