// brief-emi-5 R-emi5-1/2 — the EMerge process: `<env python> -I circuitrf_emerge.py problem.json solve`, in the run directory,
// started with an argument list (never a shell) through the process helper Palace's run uses, which logs both streams to
// emerge.log, holds the whole tree to the memory ceiling and the disk floor (brief-emi-10) and, on cancellation, kills the whole TREE: EMerge can start worker processes, and
// killing only the interpreter would leave them solving.

using System.Diagnostics;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

public static class EmergeRun
{
    /// <summary>The driver's mode that solves and writes the results.</summary>
    public const string SolveMode = "solve";

    /// <summary>brief-icf-1 R-icf1-8 — the driver's mode that builds the geometry and the mesh, reports their counts and stops.</summary>
    public const string MeshMode = "mesh";

    /// <summary>brief-emi-9 — the driver's argument that names the unknowns from which a driven solve uses the GPU.</summary>
    public const string GpuFromArgument = "gpu-from=";

    /// <summary>
    /// brief-emi-12 R-emi12-2 — the driver's argument that makes it wait, once its mesh is made and reported, for one line on its
    /// standard input: <see cref="SolveAnswer"/>, <see cref="IterativeAnswer"/> or <see cref="StopAnswer"/>. The memory check
    /// runs on the mesh's counts in the driver's own process, after meshing, so its answer has to reach the RUNNING driver; the
    /// alternative, starting the driver again with the answer as an argument, would mesh twice (seconds to minutes on a large
    /// model, and the mesh the check priced would not be the one solved unless Gmsh is deterministic). Waiting also means a run
    /// the panel is asking about solves nothing while the question is open, where until EMI-12 it went on solving under it.
    /// </summary>
    public const string AskArgument = "ask";

    /// <summary>brief-emi-12 — the answers <see cref="AskArgument"/> waits for.</summary>
    public const string SolveAnswer = "solve", StopAnswer = "stop";

    /// <summary>brief-emi-12 — the answer that solves this run with EMerge's iterative solver, to <paramref name="tolerance"/>.</summary>
    public static string IterativeAnswer(double tolerance)
        => "iterative " + tolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>brief-emi-12 — the driver's exit code when it was answered <see cref="StopAnswer"/>.</summary>
    public const int StoppedExitCode = 3;

    /// <summary>
    /// The process to start. Its environment is the parent's less every <c>PYTHON*</c> variable and <c>VIRTUAL_ENV</c> (a
    /// user's Python settings must not reach circuitRF's interpreter, which <c>-I</c> also isolates), plus the thread count
    /// for every sparse solver (EMI-3 R-emi3-5), a plotting backend that draws to no window and an off-screen 3D view.
    /// Nothing in a run reaches a network: EMerge's update check is off unless asked for, and the driver never asks.
    /// </summary>
    /// <param name="gpuFromUnknowns">brief-emi-9 — the unknowns from which the driver hands a driven problem to EMerge's GPU sparse
    /// solver (<see cref="EmergeDevicePolicy.GpuFromUnknowns"/>); null, never.</param>
    public static ProcessStartInfo StartInfo(string python, string runDirectory, IReadOnlyDictionary<string, string> threads,
                                             long? gpuFromUnknowns = null, bool meshOnly = false, bool ask = false)
    {
        ask = ask && !meshOnly;
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput  = ask,
            WorkingDirectory       = runDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding  = System.Text.Encoding.UTF8,
        };
        // No byte-order mark ahead of the answer: the driver compares the word it reads.
        if (ask) psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        // -X utf8: on Windows Python writes its streams in the ANSI code page, where EMerge's own log lines (a mesh count
        // carries ◮ and ▲) cannot be encoded and each costs a logging-error traceback in emerge.log. -I ignores PYTHONUTF8,
        // so the mode is an argument.
        foreach (string a in new[] { "-I", "-X", "utf8", EmergeScriptWriter.DriverFile, EmergeScriptWriter.ProblemFile, meshOnly ? MeshMode : SolveMode })
            psi.ArgumentList.Add(a);
        if (gpuFromUnknowns is { } from && !meshOnly) psi.ArgumentList.Add(GpuFromArgument + from.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (ask) psi.ArgumentList.Add(AskArgument);
        foreach (string key in psi.Environment.Keys.ToList())
            if (key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) || key.Equals("VIRTUAL_ENV", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(key);
        foreach (var (k, v) in threads) psi.Environment[k] = v;
        psi.Environment["MPLBACKEND"] = "Agg";
        psi.Environment["PYVISTA_OFF_SCREEN"] = "true";
        psi.Environment["EMERGE_STD_LOGLEVEL"] = "INFO";
        return psi;
    }

    /// <summary>Runs <paramref name="psi"/> to its end, every line through <paramref name="tracker"/>, the log in
    /// <c>emerge.log</c> beside the input. Started to ask (<see cref="AskArgument"/>), its standard input is the tracker's to answer
    /// on. Cancelled (or stopped by the tracker's memory check) kills the whole tree.
    /// brief-emi-10 — held to <paramref name="limits"/>' ceiling and the disk floor; until the driver reports its mesh the
    /// process is meshing, which is what a stop's sentence says.</summary>
    internal static PalaceRun.ProcessRun Solve(ProcessStartInfo psi, RunControl? control, CancellationToken ct,
                                               EmergeStageTracker tracker, long physicalBytes, ProcessLimits? limits = null)
    {
        tracker.Begin();
        bool ask = psi.RedirectStandardInput;
        return PalaceRun.RunProcess(psi, Path.Combine(psi.WorkingDirectory, EmergeResults.LogFile), Em3dProcessKind.Solver, control, ct,
                                    PalaceRun.Watch(limits, tracker.Line, physicalBytes, psi.WorkingDirectory)
                                        with
                                        {
                                            Meshing = () => tracker.Tetrahedra is null,
                                            KeepInput = ask,
                                            OnStarted = ask ? p => tracker.Input = p.StandardInput : null,
                                        });
    }
}
