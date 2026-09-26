using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

/// <summary>
/// brief-em3d-26 — WHERE a Palace run's two Palace processes run: the solve and the wave-port second-mode
/// check. The run service writes the mesh, the configuration and every result into the one Windows (or
/// native) run directory either way; a runner only decides where Palace itself executes.
/// </summary>
internal interface IPalaceRunner : IDisposable
{
    /// <summary>The processor count ranks default to (brief 21: physical cores, of where Palace runs).</summary>
    Engine.Em3d.PhysicalCoreReading Cores { get; }

    /// <summary>Runs Palace on <paramref name="runDir"/>'s mesh and configuration. On return the run
    /// directory's <c>postpro/</c> holds what Palace wrote, wherever it ran.</summary>
    PalaceStep Solve(string runDir, string configJson, string palace, int processes, RunControl? control,
                     CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes);

    /// <inheritdoc cref="PalaceRun.SecondModes(string, string, string, double, IReadOnlyList{int}, CancellationToken, out string?)"/>
    IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                               IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note);
}

/// <summary>Palace on this machine — series 1's route, unchanged.</summary>
internal sealed class NativePalaceRunner : IPalaceRunner
{
    public static readonly NativePalaceRunner Instance = new();

    public Engine.Em3d.PhysicalCoreReading Cores => Engine.Em3d.PhysicalCores.Current;

    public PalaceStep Solve(string runDir, string configJson, string palace, int processes, RunControl? control,
                            CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes)
        => PalaceRun.Solve(runDir, configJson, palace, processes, control, ct, out note, tracker, physicalBytes);

    public IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                                      IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note)
        => PalaceRun.SecondModes(runDir, configJson, palace, topHz, wavePorts, ct, out note);

    public void Dispose() { }
}
