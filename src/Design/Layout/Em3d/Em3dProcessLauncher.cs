// brief-em3d-5 R-em3d5-3d — the ONE door every 3D solver and mesher process goes out through, and
// the counter that proves a verb did not use it.
//
// `explain` and `render` on a 3D setup must start no process: sizing a run and drawing it are
// answered from the resolved problem alone, and a verb that quietly ran Gmsh to count tetrahedra would
// take minutes and need an installation the question does not. That is held by a COUNT, not a timing
// (feedback-no-new-timing-benchmark-tests): gate 6 reads Started after each verb and expects zero.
//
// Nothing starts a process through here yet — briefs 6, 7 and 9 add capability probes, Gmsh and the
// two solvers, and they start them HERE, so the counter keeps meaning what it says. Brief 6 narrows
// brief 5's gate to "no SOLVE process" once probes exist (its own §3).

using System.Diagnostics;

namespace CircuitRF.Design.Layout.Em3d;

public static class Em3dProcessLauncher
{
    private static long _started;

    /// <summary>How many processes this process has started through <see cref="Start"/>.</summary>
    public static long Started => Interlocked.Read(ref _started);

    /// <summary>Starts <paramref name="info"/>, counting it. Null when the operating system started
    /// nothing, exactly as <see cref="Process.Start(ProcessStartInfo)"/> reports it.</summary>
    public static Process? Start(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Interlocked.Increment(ref _started);
        return Process.Start(info);
    }
}
