// brief TRN-7 R-trn7-1/7 — hands the transient engine the one thing its ngspice solver needs from this layer: which ngspice to
// run, as discovery finds it (SolverDiscovery.Ngspice: Settings, CIRCUITRF_NGSPICE, PATH, the default directories, conda,
// circuitRF's own install). A MODULE INITIALIZER, as the solver-path installers are, so every entry point that reaches this
// assembly — the GUI, the CLI, MCP — has it with nothing to remember. Read at the moment a run needs it, so an install that
// has just finished, or a path just set in Settings, is the one used, with no restart.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Transient;

namespace CircuitRF.Design.Circuit;

/// <summary>The ngspice a transient run with <c>Solver=ngspice</c> uses: the one discovery finds and would run.</summary>
public static class NgspiceLocator
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
                     Justification = "Fills a seam src/Engine declares; see the comment above.")]
    internal static void Install() => NgspiceRunner.Locator = Find;

    /// <summary>The ngspice discovery finds, or null when there is none usable (none found, its path gone, or one that does not
    /// answer <c>ngspice -v</c>).</summary>
    public static NgspiceProgram? Find()
    {
        var d = SolverDiscovery.Ngspice;
        return d.Find(out _) is { } found && d.Runs(found) ? new NgspiceProgram(found.Path, found.Version) : null;
    }

    /// <summary>circuitRF's version as a deck's header names it.</summary>
    internal static string ProgramVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(NgspiceLocator).Assembly;
        string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}
