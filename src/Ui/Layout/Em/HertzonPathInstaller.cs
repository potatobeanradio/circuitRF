// HZN-8 R-hzn8-1 — hands CircuitRF.Design the one thing Hertzon discovery needs from the application: the hertzon-cli the user
// named in Settings ▸ Solvers. A module initializer for the reason Em3dSolverPathInstaller gives; unset (a headless process
// before this assembly loads), discovery falls back to CIRCUITRF_HERTZON, CIRCUITRF_HERTZON_HOME, PATH and the install folders.

using System.Runtime.CompilerServices;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

internal static class HertzonPathInstaller
{
    [ModuleInitializer]
    internal static void Install()
        => HertzonDiscovery.Shared.PreferredCommand = () => AppPreferencesIo.Load().HertzonCliPath;
}
