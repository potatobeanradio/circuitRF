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
                     CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes,
                     ProcessLimits? limits = null);

    /// <inheritdoc cref="PalaceRun.SecondModes(string, string, string, double, IReadOnlyList{int}, CancellationToken, out string?)"/>
    IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                               IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note);

    /// <inheritdoc cref="PalaceRun.FaceModes(string, string, string, int, int, double, CancellationToken, out string?)"/>
    IReadOnlyList<System.Numerics.Complex>? FaceModes(string runDir, string configJson, string palace, int attribute, int count,
                                                      double fHz, CancellationToken ct, out string? note);

    /// <summary>Whether <paramref name="runDir"/>'s saved fields are still where Palace wrote them, to be brought over by
    /// <see cref="BringFields"/> once the results are published. False where Palace wrote them into the run directory itself.</summary>
    bool FieldsPending(string runDir) => false;

    /// <summary>The saved fields, brought into <paramref name="runDir"/>'s <c>postpro/</c> AFTER the results are published: they
    /// are only for the 3D view's field plots, and on a large mesh they are gigabytes the Touchstone must not wait for.</summary>
    PalaceFieldCopy BringFields(string runDir, RunControl? control, CancellationToken ct) => PalaceFieldCopy.None;
}

/// <summary>What bringing a run's saved fields over said: a note when they came, a warning when they did not.</summary>
internal sealed record PalaceFieldCopy(string? Note, string? Warning)
{
    public static readonly PalaceFieldCopy None = new(null, null);
}

/// <summary>Palace on this machine — series 1's route, unchanged.</summary>
internal sealed class NativePalaceRunner : IPalaceRunner
{
    public static readonly NativePalaceRunner Instance = new();

    public Engine.Em3d.PhysicalCoreReading Cores => Engine.Em3d.PhysicalCores.Current;

    public PalaceStep Solve(string runDir, string configJson, string palace, int processes, RunControl? control,
                            CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes,
                            ProcessLimits? limits = null)
        => PalaceRun.Solve(runDir, configJson, palace, processes, control, ct, out note, tracker, physicalBytes, limits);

    public IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                                      IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note)
        => PalaceRun.SecondModes(runDir, configJson, palace, topHz, wavePorts, ct, out note);

    public IReadOnlyList<System.Numerics.Complex>? FaceModes(string runDir, string configJson, string palace, int attribute, int count,
                                                             double fHz, CancellationToken ct, out string? note)
        => PalaceRun.FaceModes(runDir, configJson, palace, attribute, count, fHz, ct, out note);

    public void Dispose() { }
}
