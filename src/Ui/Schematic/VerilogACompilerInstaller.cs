// Hands CircuitRF.Core the two things the Verilog-A compile step needs from the application: which
// compiler the user named, and where circuitRF keeps per-user state.
//
// Both live on the UI side of the firewall — the preference is in AppPreferences and the state
// directory is AppDataRoot, which tools redirect — and CircuitRF.Core may reference neither. So Core
// exposes the seam and this fills it, exactly as UiTypefaceInstaller fills LayoutTextOutline's.
//
// A MODULE INITIALIZER for the same reason as that one: it runs before any type in this assembly is
// touched, so there is no startup ordering to get wrong and no second entry point to remember (the
// standalone harmonicaRF and wBond binaries are this same assembly with a different Main, and a
// .va placed in harmonicaRF's Set DUT has to compile there too). Unset, Core falls back to the
// environment variable, PATH, and the platform's own LocalApplicationData — which is what a headless
// process gets and what `circuitrf hb` runs on.

using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Schematic;

internal static class VerilogACompilerInstaller
{
    [ModuleInitializer]
    internal static void Install()
    {
        // Read through the Func on every call rather than captured once: a user who names a compiler
        // in Settings and compiles again must get the one they just named.
        VerilogACompilerDiscovery.PreferredCommand = () => AppPreferencesIo.Load().VerilogACompiler;

        // The cache belongs with the rest of circuitRF's per-user state, so a tool that redirects
        // AppDataRoot — the docs factory, a test — moves the compiled models with it rather than
        // writing into the real one.
        RefreshCacheDirectory();
    }

    /// <summary>
    /// Points the compiled-model cache at the current per-user state directory. Called again by
    /// <see cref="AppDataRoot.RedirectTo"/>, because the directory can move mid-process.
    /// </summary>
    internal static void RefreshCacheDirectory()
        => VerilogASourceCompiler.CacheDirectory = AppDataRoot.SubDir("compiled-models");
}
