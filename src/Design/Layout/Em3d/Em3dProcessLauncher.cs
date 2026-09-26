// brief-em3d-5 R-em3d5-3d — the ONE door every 3D solver and mesher process goes out through, and
// the counter that proves a verb did not use it.
//
// `explain` and `render` on a 3D setup must start no SOLVE: sizing a run and drawing it are answered
// from the resolved problem alone, and a verb that quietly ran Gmsh to count tetrahedra would take
// minutes and need an installation the question does not. That is held by a COUNT, not a timing
// (feedback-no-new-timing-benchmark-tests): gate 6 reads SolvesStarted after each verb and expects
// zero.
//
// brief-em3d-6 split the count by kind. Discovery now asks each program what version it is, and asks
// Palace to dry-run a configuration (SolverDiscovery), and `explain` reports that answer (R-em3d6-4c)
// — so `explain` does start PROBES now, deliberately. What it must still never start is a mesher or a
// solver, which is what SolvesStarted counts. Briefs 7 and 9 start Gmsh and the solvers HERE, with
// their own kind, so the counter keeps meaning what it says.

using System.Diagnostics;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>What a process started for 3D EM is for.</summary>
public enum Em3dProcessKind
{
    /// <summary>A version or capability question: seconds at most, and it writes no result.</summary>
    Probe,
    /// <summary>Gmsh, meshing a problem.</summary>
    Mesher,
    /// <summary>Palace or openEMS, solving one.</summary>
    Solver,

    /// <summary>brief-em3d-24 — one step of a solver's install recipe (git, Spack, an upstream build
    /// script). Counted apart from solves, and apart from probes, so "declining consent started nothing"
    /// is a counter rather than a hope.</summary>
    Installer,
}

public static class Em3dProcessLauncher
{
    private static long _started;
    private static long _solves;
    private static long _installSteps;

    /// <summary>How many processes this process has started through <see cref="Start"/>, of any kind.</summary>
    public static long Started => Interlocked.Read(ref _started);

    /// <summary>How many of those were a mesher or a solver — anything but a probe.</summary>
    public static long SolvesStarted => Interlocked.Read(ref _solves);

    /// <summary>How many install-recipe steps this process has started (brief-em3d-24).</summary>
    public static long InstallStepsStarted => Interlocked.Read(ref _installSteps);

    /// <summary>Starts <paramref name="info"/>, counting it under <paramref name="kind"/>. Null when the
    /// operating system started nothing, exactly as <see cref="Process.Start(ProcessStartInfo)"/>
    /// reports it.</summary>
    public static Process? Start(ProcessStartInfo info, Em3dProcessKind kind)
    {
        ArgumentNullException.ThrowIfNull(info);
        Interlocked.Increment(ref _started);
        if (kind is Em3dProcessKind.Mesher or Em3dProcessKind.Solver) Interlocked.Increment(ref _solves);
        if (kind == Em3dProcessKind.Installer) Interlocked.Increment(ref _installSteps);
        return Process.Start(info);
    }
}
