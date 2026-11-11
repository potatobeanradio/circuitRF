// CAP-1 follow-up — what one frequency point of a WHOLE run holds: the structure's own dense system plus, when the
// run de-embeds, its calibration standards, which PlanarSolve solves CONCURRENTLY with it at every point. Asked by
// the run (PlanarKernel.Solve, before anything is filled), by the pre-solve check and by the EM panel, through this
// one function, so the three judge a de-embedded run against the same figure.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>The memory of a whole run's frequency point, standards included</b> — and the verdict on it against the
/// run's memory limit.
///
/// <para><b>Measured, and it is why the standards count.</b> On §10.7's hero cross-section at 80 cells/λ the DUT
/// held 0.40 GB and its four standards 0.55 GB, and the run's live peak sat on their sum (it completed under a
/// 1.07 GB heap cap and ran out at 0.94). A limit judged on the DUT alone let a de-embedded run start that needed
/// more than twice what it was judged on. <c>RESOLVED.md</c> §CAP-1 follow-up.</para>
///
/// <para><b>An estimate of the standards, from the same calls the run makes</b> — one standard set per distinct
/// port cross-section (<see cref="PlanarPortCalibrator.SameCrossSection"/>), each built by
/// <see cref="PlanarCalibration.BuildSet(PlanarPortResolution, GroundedSlab, double, double, PlanarCalibrationSettings?, string)"/>
/// over the run's band. Meshing only; nothing is filled. A CALIBRATION GROUP (PCAL4) or a widened standard (PCAL3)
/// builds larger standards than a port's own line, and this prices the port's own line: the figure is a floor
/// there, which the note says.</para>
/// </summary>
public static class PlanarRunMemory
{
    /// <summary>
    /// What the calibration standards of a de-embedded dense run hold at one frequency point, bytes — the sum of
    /// <see cref="PlanarSystem.ResidentBytes"/> over every standard, since all of them are solved alongside the
    /// structure. 0 when the run de-embeds nothing, runs accelerated (its standards hold no dense matrix), has no
    /// non-zero frequency, or has no de-embeddable port.
    /// </summary>
    /// <param name="structure">CAP-3 — the structure's own mesh. With it the figure also covers the one step
    /// of a de-embedded run that is not a solve: the calibrators' static capacitance (D7), a dense m×m P and
    /// NumFlat's L and U over the LONGEST standard's cells (48·m² bytes), run once after the first point's
    /// solves have finished — when the structure holds only its cores. Where that step is the larger, the
    /// standards' share is what makes the run's figure reach it.</param>
    /// <param name="control">PMS-1 — the stage (<c>pricing the calibration standards</c>, one unit per port) and the
    /// cancellation the count of each standard's cores checks once per batch of rows.</param>
    public static long StandardBytes(PlanarProblem problem, IReadOnlyList<PlanarPortResolution> ports,
                                     IReadOnlyList<double> freqsHz, PlanarSolveSettings? settings,
                                     PlanarMesh? structure = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(freqsHz);
        var st = settings ?? PlanarSolveSettings.Default;
        if (!st.Deembed || st.Fill?.Aim is not null) return 0;

        double fLo = double.PositiveInfinity, fHi = 0;
        foreach (double f in freqsHz)
            if (f > 0) { fLo = Math.Min(fLo, f); fHi = Math.Max(fHi, f); }
        if (!(fHi > 0)) return 0;

        var slab   = problem.Slab;
        var owners = new List<PlanarPortResolution>();
        long total = 0, heldCores = 0, capacitance = 0;
        var ct = control?.Token ?? default;
        int deembeddable = 0;
        foreach (var port in ports) if (port.IsDeembeddable) deembeddable++;
        if (deembeddable > 0) control?.BeginStage(PricingStage, deembeddable, "port(s)");
        foreach (var port in ports)
        {
            if (!port.IsDeembeddable) continue;
            ct.ThrowIfCancellationRequested();
            control?.SetStageLabel($"{PricingStage} (port {port.Number})");
            int k = PlanarCalibration.EndRunCellsFor(port, slab, st.Calibration);
            bool shared = false;
            foreach (var o in owners)
                if (PlanarPortCalibrator.SameCrossSection(o, port, k)) { shared = true; break; }
            if (shared) { control?.TickStage(); continue; }
            owners.Add(port);

            // A calibrator solves the SHORT line and the one long line a frequency selects
            // (PlanarPortCalibrator.PlannedSolvesAt), so at its worst point it holds the short one and the
            // largest of the rest.
            string layer = problem.Layers[Math.Clamp(port.LayerIndex, 0, problem.Layers.Count - 1)].Name;
            var substrate = PlanarSubstrateReturn.TryFor(problem, port, st.Fill?.PerfectConductor == true);
            var set = PlanarCalibration.BuildSet(port, slab, fLo, fHi, st.Calibration, layer,
                                                 substrate is null ? null : substrate.EstimatedBetaAt);
            long longest = 0, longestCores = 0;
            int longestAt = 0;
            for (int i = 0; i < set.Length; i++)
            {
                // CAP-3 — each standard's own counted cores and its fill's bounded transient.
                // PMS-1 R-pms1-3 — through For, so a standard is bounded exactly as the structure is: CountedUpTo, and
                // the class cap a count stops at. On a chip-scale grid a standard inherits the chip's lines, and its
                // exact count ran to tens of millions of classes (34 s and 5.9 GB for one).
                var cores = PlanarCoreFootprint.For(set[i].Mesh, set[i].Mesh.SolvedUnknownCount, set[i].Mesh.Cells.Count,
                                                    ct: ct);
                long b = PlanarSystem.ResidentBytes(set[i].Mesh.SolvedUnknownCount, cores);
                if (i == 0) { total += b; heldCores += cores.Bytes; }
                else if (b > longest) { longest = b; longestCores = cores.Bytes; longestAt = i; }
            }
            total += longest;
            heldCores += longestCores;

            // The static capacitance differences the short and the LONGEST standard, one after the other,
            // so the last standard's cores are held then too — counted once, not again when the largest
            // solve above already was the last standard.
            var last = set[^1].Mesh;
            long ml = last.Cells.Count;
            capacitance = Math.Max(capacitance, 48L * ml * ml);
            if (set.Length > 1 && longestAt != set.Length - 1 && !ReferenceEquals(set[^1], set[0]))
                heldCores += PlanarCoreFootprint.For(last, last.SolvedUnknownCount, last.Cells.Count, ct: ct).Bytes;
            control?.TickStage();
        }
        if (structure is null || capacitance == 0) return total;

        var dut = PlanarCoreFootprint.For(structure, structure.SolvedUnknownCount, structure.Cells.Count, ct: ct);
        long dutPoint = PlanarSystem.ResidentBytes(structure.SolvedUnknownCount, dut);
        long staticStep = dut.Bytes + heldCores + capacitance;
        return Math.Max(total, staticStep - dutPoint);
    }

    /// <summary>PMS-1 — the stage <see cref="StandardBytes"/> opens, so a progress line names the step it is on rather than
    /// the mesher's last.</summary>
    public const string PricingStage = "pricing the calibration standards";

    /// <summary>PMS-1 — the sentence a report carries when its standards were not priced because its dense matrix alone
    /// is past the limit (<see cref="PlanarMeshReport.MatrixAlonePastLimit"/>).</summary>
    public const string NotPricedNote =
        "The calibration standards were not priced: the structure's own matrix is past the memory limit without them.";

    /// <summary>
    /// <b>PMS-1 R-pms1-2 — the report, with the pricing skipped and said.</b> For a report whose dense matrix alone is
    /// past the limit (<see cref="PlanarMeshReport.MatrixAlonePastLimit"/>): pricing its standards can only add to a
    /// figure already past the limit, and on a chip-scale grid it was the step that never finished. Every caller that
    /// prices asks this first, so the pre-solve check, the panel and the run agree.
    /// </summary>
    public static PlanarMeshReport NotPriced(PlanarMeshReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var findings = new List<EmFinding>(report.AllFindings) { EmFinding.Note(NotPricedNote) };
        return report with { Findings = findings, Notes = EmFindings.Texts(findings) };
    }

    /// <summary>A heap this size or larger is collected after each finished solve (<see cref="ReleaseFinishedSolve"/>).</summary>
    public const long ReleaseThresholdBytes = 256L << 20;

    /// <summary>
    /// <b>Hand a finished solve's matrix back before the next one allocates.</b> Called at the end of each of a
    /// point's independent solves (the structure and every standard). Without it the runtime keeps a finished
    /// solve's triangle and transients as garbage until its own budget says otherwise, and a de-embedded point's
    /// process peak ran at twice what the point actually holds. Measured on §10.7's hero at 80 cells/λ (DUT
    /// N = 4,236 and four standards, live sum 0.61 GB): peak footprint 0.98 → 0.48 GB with the solves in series
    /// and 1.21 → 0.91 GB concurrently, the same wall clock either way. Only when the heap is at least
    /// <see cref="ReleaseThresholdBytes"/>, so a small run pays nothing. Changes no answer.
    /// </summary>
    public static void ReleaseFinishedSolve()
    {
        if (GC.GetTotalMemory(forceFullCollection: false) >= ReleaseThresholdBytes)
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    /// <summary>
    /// <b>The mesh report, priced with its standards and judged again.</b> Carries
    /// <paramref name="standardBytes"/> on <see cref="PlanarMeshReport.StandardResidentBytes"/>; past the run's
    /// memory limit with them it warns and sets <see cref="PlanarMeshReport.PastMachineLimit"/>, so the run asks
    /// before it starts — a memory limit is advisory, whoever stated it. A report that already refuses, an accelerated run, or no limit at all, is returned priced but
    /// otherwise as it was.
    /// </summary>
    public static PlanarMeshReport WithStandards(PlanarMeshReport report, long standardBytes,
                                                 PlanarMeshSettings settings, bool accelerated)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(settings);
        var priced = report with { StandardResidentBytes = Math.Max(0, standardBytes) };
        if (standardBytes <= 0 || accelerated || !report.CanSolve || settings.MemoryLimitBytes is not { } limit)
            return priced;
        if (priced.RunResidentBytes <= limit) return priced;

        var source   = settings.MemoryLimitSource;
        var findings = new List<EmFinding>(report.AllFindings);
        int n = report.UnknownCount, cells = report.CellCount;

        // A memory limit is advisory: a warning, and the run asks. The mesh's own warning (if the structure
        // alone was past) is replaced, not repeated — keeping the diagnosis it carries after its first part.
        var cores = PlanarCoreFootprint.For(report.Mesh, n, cells);
        string alone = SurfaceMesher.MachineLimitWarning(n, cells, 0, limit, source, false, cores);
        string diagnosis = "";
        int at = findings.FindIndex(f => f.Text.StartsWith(alone, StringComparison.Ordinal));
        if (at >= 0) { diagnosis = findings[at].Text[alone.Length..]; findings.RemoveAt(at); }
        findings.Add(EmFinding.Warn(
            SurfaceMesher.MachineLimitWarning(n, cells, standardBytes, limit, source, false, cores) + diagnosis));
        return priced with
        {
            Verdict          = PlanarBudgetVerdict.Warn,
            Findings         = findings,
            Notes            = EmFindings.Texts(findings),
            PastMachineLimit = true,
        };
    }
}
