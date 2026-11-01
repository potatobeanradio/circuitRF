// Will a planar (MoM) run fit in this machine's memory — asked once the run's mesh is final and before anything is
// filled, through PlanarSolveSettings.Admit.
//
// The answer goes out on the 3D solvers' own verdict type so it reaches the same question at Simulate (the panel's
// dialog) and the same --force on `circuitrf em`. The thresholds are NOT the 3D solvers': their estimate errs high by
// construction, so they confirm only past 150 %. The dense estimate here is the measured accounting of one frequency
// point (PlanarSystem.ResidentBytes, P1/P7) — the structure's plus, on a de-embedded run, its calibration standards',
// which are solved beside it (PlanarRunMemory, CAP-1 follow-up) — so past this machine's memory the run is asked about
// rather than allowed to swap. That question is also the only thing a machine-derived ceiling ever does: past it the
// run is asked, never refused.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>The planar solver's memory check: a warning past <see cref="WarnFraction"/> of physical memory, and a
/// question past <see cref="ConfirmFraction"/>.</summary>
public static class PlanarMemoryCheck
{
    /// <summary>What the memory limit is typed in: DECIMAL gigabytes, as every memory figure the EM
    /// panels show (<see cref="MachineMemory.Format"/>).</summary>
    public const double BytesPerGb = 1e9;

    /// <summary>
    /// <b>CAP-1 — the memory a planar run is sized on, in one place.</b> The machine-wide limit
    /// (Settings ▸ Solvers; <c>em --memory-limit</c> / MCP <c>memoryLimitGB</c> headless) when one is set,
    /// otherwise <paramref name="physicalBytes"/>. <b>Settings ▸ Solvers is THE memory limit</b>: an EM
    /// setup carries none, so whatever <paramref name="mesh"/> holds is replaced. The answer is handed down
    /// in <see cref="PlanarMeshSettings.MemoryLimitBytes"/> with <see cref="PlanarMeshSettings.MemoryLimitSource"/>
    /// saying whose it is, so the engine never reads the machine and the preflight, the panel and the run
    /// all judge against the same number — advisorily: past it the run asks. Physical memory of 0
    /// (unreadable) leaves the shipped ceiling.
    /// </summary>
    public static PlanarMeshSettings WithMachineCeiling(PlanarMeshSettings mesh, long? machineLimitBytes,
                                                        long physicalBytes)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (machineLimitBytes is > 0 and { } machine)
            return mesh with { MemoryLimitBytes = machine, MemoryLimitSource = PlanarMemoryLimitSource.MachineLimit };
        if (physicalBytes > 0)
            return mesh with { MemoryLimitBytes = physicalBytes, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory };
        return mesh with { MemoryLimitBytes = null };
    }

    /// <summary>
    /// <b>CAP-1 follow-up — the mesh report priced as the run will price it</b>: the ports resolved on its mesh,
    /// the calibration standards of a de-embedded dense run added (<see cref="PlanarRunMemory.StandardBytes"/>,
    /// under the product's own default solve settings — the run's de-embedding is always on), and the result judged
    /// against <paramref name="mesh"/>'s limit (<see cref="PlanarRunMemory.WithStandards"/>). The pre-solve check
    /// and the EM panel call this so they agree with the run. Ports that do not resolve leave the report as it was
    /// — the run refuses them on their own account.
    /// </summary>
    public static PlanarMeshReport PriceStandards(PlanarMeshReport report, PlanarProblem meshed,
                                                  IReadOnlyList<PlanarPort> ports, IReadOnlyList<double> freqsHz,
                                                  PlanarMeshSettings mesh, bool accelerated)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (accelerated || ports.Count == 0) return report;
        long std;
        try
        {
            var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
            std = PlanarRunMemory.StandardBytes(meshed, resolved, freqsHz, PlanarSolveSettings.Default, report.Mesh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return report; }
        return PlanarRunMemory.WithStandards(report, std, mesh, accelerated);
    }

    /// <summary>Above this share of physical memory a run is warned about.</summary>
    public const double WarnFraction = 0.75;

    /// <summary>Above this share a run must be confirmed (the panel asks; the CLI needs --force).</summary>
    public const double ConfirmFraction = 1.0;

    /// <summary>The verdict for <paramref name="size"/> on a machine with <paramref name="physicalBytes"/> — or, when
    /// the machine's own memory limit is set (Settings ▸ Solvers), that limit, which every solver is held to. An
    /// accelerated run has no closed-form estimate and always fits.</summary>
    public static Em3dMemoryVerdict Evaluate(PlanarRunSize size, long physicalBytes, bool machineLimit = false)
    {
        string owner = machineLimit ? "this machine's memory limit of" : "this machine's";

        // CAP-1 follow-up — an accelerated run has no dense figure; the one thing it is asked about is a mesh past
        // the unknown count the machine's memory sizes.
        if (size.DenseResidentBytes is not { } e || physicalBytes <= 0)
            return size.PastMachineCeiling && physicalBytes > 0
                ? new(Em3dMemoryLevel.Severe, null, physicalBytes, AcceleratedPastCeiling(size, owner, physicalBytes))
                : new(Em3dMemoryLevel.Fits, size.DenseResidentBytes, physicalBytes, null);
        double share = (double)e / physicalBytes;
        if (share <= WarnFraction && !size.PastMachineCeiling) return new(Em3dMemoryLevel.Fits, e, physicalBytes, null);

        bool severe = share > ConfirmFraction || size.PastMachineCeiling;
        string pct = (share * 100).ToString("0", CultureInfo.InvariantCulture);
        string text =
            $"This run's {size.Unknowns:N0} unknowns need about {MachineMemory.Format(e)} at each frequency point" +
            (size.StandardBytes > 0
                ? $" — {MachineMemory.Format(size.StandardBytes)} of it for the calibration standards solved beside " +
                  "the structure — "
                : ", ") +
            $"{pct} % of {owner} {MachineMemory.Format(physicalBytes)}. " +
            (severe ? "At that size it will very likely swap heavily or be stopped by the operating system. "
                    : "A run this size may slow the machine or swap. ") +
            "The accelerated solve (Solver options) needs far less.";
        return new(severe ? Em3dMemoryLevel.Severe : Em3dMemoryLevel.Warning, e, physicalBytes, text);
    }

    /// <summary>
    /// The sentence for an accelerated run past the count the machine's memory sizes. EMB-4: a multi-level or via mesh is
    /// judged on the DENSE ceiling (<see cref="PlanarRunSize.DenseCeiling"/>), so the sentence says that rather than
    /// calling it the accelerated solve's, and says what the accelerator holds (R-emb4-2); a ceiling of 0 — the mesh's
    /// cores alone past the limit — is said in words (R-emb4-3), never as "past the 0".
    /// </summary>
    internal static string AcceleratedPastCeiling(PlanarRunSize size, string owner, long physicalBytes)
    {
        string machine = $"{owner} {MachineMemory.Format(physicalBytes)}";
        string past = size.CeilingUnknowns > 0
            ? $"past the {size.CeilingUnknowns:N0} {(size.DenseCeiling ? "the dense ceiling allows" : "the accelerated solve is judged against")} in {machine}"
            : $"judged against {machine}: {SurfaceMesher.CoresAlone(size.CoreBytes, physicalBytes)}";
        string dense = size.DenseCeiling
            ? " A mesh with more than one metal level or a via is judged on the dense ceiling even when the accelerated " +
              "solve is on" +
              (size.AcceleratedResidentBytes is { } a
                  ? $"; the accelerated solve this run uses holds about {MachineMemory.Format(a)}, estimated."
                  : ".")
            : "";
        return $"This run's {size.Unknowns:N0} unknowns are {past}.{dense} It may run out of memory or take far longer " +
               "than a mesh inside it.";
    }

    /// <summary>
    /// The <see cref="PlanarSolveSettings.Admit"/> a run is given: a warning is added to <paramref name="warnings"/>,
    /// and a run past <see cref="ConfirmFraction"/> is asked about through <paramref name="confirm"/> — declined, or
    /// with no one to ask, it is not started.
    /// </summary>
    public static Func<PlanarRunSize, string?> Admitter(Func<Em3dMemoryVerdict, bool>? confirm, List<string> warnings,
                                                        long physicalBytes, bool machineLimit = false)
        => size =>
        {
            var verdict = Evaluate(size, physicalBytes, machineLimit);
            if (verdict.Text is not { } text) return null;
            if (verdict.Level != Em3dMemoryLevel.Severe) { warnings.Add(text); return null; }
            if (confirm?.Invoke(verdict) != true)
                return text + " It was not started. To start it anyway, confirm it when Simulate asks, or pass " +
                       "--force to `circuitrf em`.";
            warnings.Add(text + " It was started anyway, as confirmed.");
            return null;
        };
}
