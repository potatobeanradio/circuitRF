// L8e — kernel B's own entry point, and D4's "no new result type" for the fifth phase running.
//
// D1 — THIS DOES NOT IMPLEMENT IEmKernel, AND THAT IS THE DECISION, NOT AN OMISSION. IEmKernel takes
// an EmProblem (a cross-section) and an EmMeshSettings (kernel A's six boundary-mesh controls);
// kernel B takes a PlanarProblem (a plan view), a PlanarMeshSettings (D3's three surface controls)
// and a PORT LIST, which kernel A does not have because for a uniform cross-section the two ports
// ARE the two ends of the line by construction (R-mom-15). Widening IEmKernel to cover both would
// mean either the base class L8b's D1 rejected or nullable fields threaded through every call site.
// What the two share is the OUTPUT, and EmKernelRegistry.EmKernelOutcome is where that is stated.
//
// R-res-6 — NO NEW RESULT TYPE. A planar run produces the same DataSet shape kernel A's does: an S
// cube plus a per-port Z0 cube, through DataSetBuilder.FromSnp, written by the existing EmRunService
// path to the existing predictable .snp location. What is ADDED is one group of diagnostics — γ, Z_c,
// ε_eff per port and L8d's own de-embedding residuals — under its own name so nothing collides with
// the "tline" group's meaning. The eight tline scalars are what make a wrong kernel-A answer
// diagnosable (R-em-18's own words); this group plays exactly the same role for kernel B, and must
// not be filtered out on the way to Data Display either.
//
// R-res-15 / R-em-21 — NO PHYSICS HERE. Every number this file puts in a cube came out of
// PlanarSolve, PlanarCalibration or PlanarDeembed. The one derived quantity is ε_eff, and it is
// GammaResult.EffectivePermittivity's own method, called rather than re-derived.

using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Mom;

/// <summary>What a planar solve produced: the house-convention <see cref="DataSet"/>, plus the
/// engine objects behind it so a caller (or a test) can look at the mesh and the calibration without
/// re-deriving anything from the cubes.</summary>
public sealed record PlanarKernelResult(
    DataSet                              Data,
    PlanarMeshReport                     MeshReport,
    PlanarSolveResult                    Solve,
    IReadOnlyList<PlanarPortResolution>  Ports,
    IReadOnlyList<string>                Notes,
    /// <summary>D5's heat map, for the one port and one frequency the solve was asked to keep
    /// currents for. Null when none was requested or the sweep produced none.</summary>
    PlanarCurrentDensityMap?             CurrentDensity = null);

/// <summary>
/// Kernel B — the full-wave planar (MoM) solve on one grounded slab (§10.3, phase L8).
///
/// <para>Everything numerical already exists: L8a's Green's function, L8b's mesher, L8c's fill and
/// L8d's ports and de-embedding. This is the seam that turns them into the house result convention,
/// and it is deliberately thin.</para>
/// </summary>
public sealed class PlanarKernel
{
    /// <summary>Worded once so the registry, the panel and the notes cannot drift.</summary>
    public const string KernelName = "Full-wave planar (kernel B)";

    /// <summary>The diagnostics group D4 adds. <b>Not "tline"</b> — kernel A's eight scalars are
    /// per-unit-length properties of a uniform line, and a planar structure has none; overloading the
    /// name would make a Data Display trace mean two different things depending on which kernel
    /// happened to run.</summary>
    public const string DiagnosticsGroup = "planar";

    public string Name => KernelName;

    /// <summary>
    /// <b>L9d/M5 — <see cref="EmCapabilities.LayeredWithVias"/> is finally declared, and only now.</b>
    ///
    /// <para>The flag has existed since L6 and was read by nothing. L9c deliberately still did not
    /// set it, and said why: the mesh, the basis and the kernel existed but nothing could SOLVE a
    /// two-level structure, so declaring it would have been the advance-refusal mistake in reverse.
    /// After L9d there is a solve, ports on a level, and de-embedding against single-level standards,
    /// so it is declared — and what it means is stated here rather than left to be inferred:
    /// <b>N conductor levels on an arbitrary stratified medium, with vias carrying z-directed current
    /// between adjacent levels.</b> Its own limits (the midpoint rule's kℓ ≤ 0.05, G_A^zz's
    /// ρ/λ ≤ 0.1, single-level calibration standards on the slab's own top surface) are refusals on
    /// this kernel, not qualifications on the flag.</para>
    /// </summary>
    public EmCapabilities Capabilities => EmCapabilities.Planar | EmCapabilities.LayeredWithVias;

    /// <summary>
    /// R-mom-17's shape, for the problem-level refusals kernel B can see from a
    /// <see cref="PlanarProblem"/> alone. The GEOMETRIC refusals — no ground plane at all, artwork on
    /// a layer the technology does not describe — are the Ui-side <c>PlanarExtractor</c>'s and are
    /// worded there; this is the split §10.3.4 already describes for kernel A, applied unchanged.
    /// </summary>
    public EmSuitability CanSolve(PlanarProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Layers.Count == 0)
            return EmSuitability.No("This problem has no conductor layers; there is nothing to solve.");

        if (problem.PolygonCount == 0)
            return EmSuitability.No(
                "The conductor layer encloses no filled region, so there is no metal to mesh. A " +
                "planar solver needs areas, not centrelines or markers.");

        // L9c's own three earned refusals on the problem TYPE: a level off every interface, levels
        // out of order, a via skipping a level. Asked here rather than restated.
        var own = problem.CanSolve();
        if (!own.Ok) return own;

        if (!problem.RequiresGeneralKernel)
        {
            // L8's shipped path, unchanged: one level on the slab's own top surface.
            var host = GroundedSlab.CanHost(1, problem.Slab.HeightM, problem.Slab.HeightM);
            return host.Ok
                ? EmSuitability.Yes
                : EmSuitability.No(host.Reason ?? "This stackup is not one kernel B supports.");
        }

        // ── The general path's own structural limits ──────────────────────────────────────────────
        var fit = Dcim.CanFit(problem.EffectiveStack);
        if (!fit.Ok) return fit;

        if (problem.MaxFrequencyHz > 0)
        {
            var midpoint = MidpointRuleVerdict(problem, problem.MaxFrequencyHz);
            if (!midpoint.Ok) return midpoint;
        }

        var oneRegion = EveryViaLiesInOneMediumRegion(problem);
        if (!oneRegion.Ok) return oneRegion;

        return EmSuitability.Yes;
    }

    /// <summary>
    /// <b>A via must lie inside ONE region of the medium, and this refusal is what the z-integral's
    /// closed form costs.</b>
    ///
    /// <para><c>ViaZIntegral</c> integrates the two extracted asymptotes over the via's length in
    /// closed form, and it can do that because their coefficients are the source REGION's own Fresnel
    /// coefficients and therefore do not move with the heights. A via that crosses a dielectric
    /// interface has two different sets of them over its own length, and the pairs that straddle the
    /// interface have none at all (cross-region needs no extraction) — so a single closed form over
    /// the whole span would be putting back a different function from the one that was removed.</para>
    ///
    /// <para>It is refused rather than approximated because the failure would be silent: the answer
    /// would still be a plausible inductance. Handling it properly means splitting each via at the
    /// interfaces it crosses and treating the sub-span pairs separately, which is real work and is
    /// named as not built. <b>L9c's midpoint rule did not have this limit</b> — it evaluated one
    /// height pair and never asked which region the rest of the via was in — so this is a narrowing,
    /// stated rather than buried, of a case nothing ever validated.</para>
    /// </summary>
    private static EmSuitability EveryViaLiesInOneMediumRegion(PlanarProblem problem)
    {
        if (problem.ViaList.Count == 0) return EmSuitability.Yes;
        var stack = problem.EffectiveStack;

        foreach (var via in problem.ViaList)
        {
            double lo = problem.LevelZ(via.LowerLayerIndex);
            double hi = problem.LevelZ(via.UpperLayerIndex);
            if (!(hi > lo)) continue;

            // Probed just inside each end, because a conductor level sits exactly ON an interface and
            // RegionOf's own convention at a boundary is not the question being asked.
            double eps = 1e-6 * (hi - lo);
            int rLo = stack.RegionOf(lo + eps), rHi = stack.RegionOf(hi - eps);
            if (rLo == rHi) continue;

            return EmSuitability.No(
                $"The via between levels {via.LowerLayerIndex} and {via.UpperLayerIndex} spans " +
                $"z = {SurfaceMesher.Eng(lo)}m to {SurfaceMesher.Eng(hi)}m, which crosses a " +
                $"dielectric interface of the medium (region {rLo} to region {rHi}). This kernel " +
                $"integrates a via's Green's function over its length in CLOSED FORM, and that form " +
                $"is written in the source region's own asymptotic coefficients — a via with two " +
                $"regions under it has two different sets of them, and the height pairs that straddle " +
                $"the interface have none at all. Approximating it would give a plausible wrong " +
                $"inductance rather than an obvious failure, which is why it is refused. Put a " +
                $"conductor level on the intervening interface so the via is two stacked vias, or " +
                $"remove the interface if it carries no physics.");
        }
        return EmSuitability.Yes;
    }

    /// <summary>
    /// R-via-6's refusal, asked at whatever the top of the sweep actually is. The wavenumber is taken
    /// in the fastest-slowing medium anywhere in the stack — the same rule R-msh-3 uses for the mesh —
    /// because that is the shortest wavelength any part of the via can see.
    /// </summary>
    private static EmSuitability MidpointRuleVerdict(PlanarProblem problem, double fHiHz)
    {
        if (problem.ViaList.Count == 0 || problem.Layers.Count < 2) return EmSuitability.Yes;
        double lambdaG = problem.MaxFrequencyHz > 0 && fHiHz == problem.MaxFrequencyHz
            ? problem.GuidedWavelengthM
            : (problem with { MaxFrequencyHz = fHiHz }).GuidedWavelengthM;
        if (double.IsInfinity(lambdaG) || !(lambdaG > 0)) return EmSuitability.Yes;
        // L9e's GEOMETRIC arm (and with it NarrowestViaFootprint) is gone: the z-integral it bounded
        // is resolved, and the ℓ/w curve it was measured on is flat. What is left is electrical and
        // is about the BASIS — see PlanarLevels.CanRepresentVias.
        return PlanarLevels.From(problem).CanRepresentVias(2.0 * Math.PI / lambdaG);
    }

    /// <summary>The pre-solve mesh and R17's verdict — §10.5's "report the unknown count before
    /// solving", which is L8b's own product and is simply forwarded.</summary>
    public PlanarMeshReport Mesh(PlanarProblem problem, PlanarMeshSettings settings)
        => SurfaceMesher.Mesh(problem, settings);

    /// <summary>
    /// Mesh → resolve ports → sweep → <see cref="DataSet"/>.
    ///
    /// <para>Throws with the engine's own wording on a refused port or an over-budget mesh; the Ui
    /// run path catches and reports, exactly as it already does for kernel A.</para>
    /// </summary>
    /// <para><b>Calibrations are shared only where <see cref="PlanarPortCalibrator.SameCrossSection"/>
    /// says they may be</b>, which <see cref="PlanarSolve"/> already decides port by port. Sharing one
    /// across two DIFFERENT feed cross-sections is the trap L8d's own D4 exists for — it moved a
    /// supposedly invariant answer by 1.8e-1 — so nothing here shares one on its own initiative.</para>
    public PlanarKernelResult Solve(
        PlanarProblem              problem,
        PlanarMeshSettings         meshSettings,
        IReadOnlyList<PlanarPort>  ports,
        IReadOnlyList<double>      freqsHz,
        PlanarSolveSettings?       settings = null,
        CancellationToken          ct       = default)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(freqsHz);

        if (ports.Count == 0)
            throw new InvalidOperationException(
                "A planar solve needs at least one port. Place port labels on the conductor ends in " +
                "the layout editor's Port tool, or check that they resolved onto the metal.");

        var report = Mesh(problem, meshSettings);
        if (!report.CanSolve)
            throw new InvalidOperationException(report.Refusal ?? "The mesh is over the R17 budget.");

        ct.ThrowIfCancellationRequested();

        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);

        // D5 — the heat map rides along with the sweep the panel already pays for: default to port 1
        // at the lowest swept frequency, which is where a user starts looking. Both are selectable
        // (PlanarSolveSettings), and a caller that wants no map passes 0.
        var st = settings ?? PlanarSolveSettings.Default;
        if (st.CurrentDensityPortNumber == 0)
            st = st with { CurrentDensityPortNumber = resolved[0].Number };

        // R-via-6 at the sweep's ACTUAL top, which CanSolve can only guess at from MaxFrequencyHz.
        double fHi = 0;
        foreach (double f in freqsHz) fHi = Math.Max(fHi, f);
        var midpoint = MidpointRuleVerdict(problem, fHi);
        if (!midpoint.Ok) throw new InvalidOperationException(midpoint.Reason);

        var sweep = PlanarSolve.Run(problem, report.Mesh, resolved, freqsHz, st);

        var notes = new List<string>(report.Notes);
        notes.AddRange(sweep.Notes);
        notes.Add(QuasiStaticNote);

        PlanarCurrentDensityMap? density = null;
        if (sweep.CapturedCurrents is { } currents)
        {
            density = PlanarCurrentDensity.Compute(
                report.Mesh, currents, sweep.CapturedPortNumber, sweep.CapturedFrequencyHz);
            notes.Add(density.ScaleCaption);
        }

        return new PlanarKernelResult(
            BuildDataSet(sweep, resolved), report, sweep, resolved, notes, density);
    }

    /// <summary>
    /// D5's "selectable" half: recompute the heat map for a DIFFERENT port or frequency. This is a
    /// fresh fill + factorisation at that one point, which is what changing either genuinely costs —
    /// there is no cached matrix to re-excite, because a sweep keeps none (L8c's Tier 8 measured the
    /// matrix at 4.6 MB on §10.7's hero and 371 MB at R17's ceiling; keeping one per frequency is not
    /// a trade this kernel makes).
    /// </summary>
    public PlanarCurrentDensityMap CurrentDensityAt(
        PlanarProblem              problem,
        PlanarMeshSettings         meshSettings,
        IReadOnlyList<PlanarPort>  ports,
        double                     fHz,
        int                        drivenPortNumber,
        PlanarSolveSettings?       settings = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);

        var report   = Mesh(problem, meshSettings);
        if (!report.CanSolve)
            throw new InvalidOperationException(report.Refusal ?? "The mesh is over the R17 budget.");

        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
        int j = -1;
        for (int i = 0; i < resolved.Count; i++)
            if (resolved[i].Number == drivenPortNumber) { j = i; break; }
        if (j < 0)
            throw new InvalidOperationException(
                $"This layout has no port {drivenPortNumber} to drive; it has " +
                string.Join(", ", resolved.Select(p => p.Number)) + ".");

        var st      = settings ?? PlanarSolveSettings.Default;
        var context = new PlanarSolveContext(
            report.Mesh, resolved, st.Fill,
            problem.RequiresGeneralKernel ? PlanarLevels.From(problem) : null);
        var kernel  = PlanarFrequencyKernel.Fit(
            problem, fHz, (st.Fill ?? PlanarFillSettings.Default).Order, st.Dcim);

        var solution = context.SolveAt(kernel, fHz);
        return PlanarCurrentDensity.Compute(report.Mesh, solution.Currents[j], drivenPortNumber, fHz);
    }

    /// <summary>
    /// §0's third finding, surfaced to the user as a NOTE rather than published as if it were
    /// dispersion. Z_c here is <c>γ/(jωC_pul)</c> with C_pul differenced between the two calibration
    /// standards, so C is held at its quasi-static value; Z_c therefore rises with frequency as
    /// √ε_eff(f). Measured against kernel A's own static value on §10.7's hero: +0.40% at 1 GHz,
    /// +2.33% at 5 GHz, +6.34% at 20 GHz (L8d Tier 5). That is the γ-and-C route's honest cost, and a
    /// dispersive C needs a field integral this kernel does not have.
    /// </summary>
    internal const string QuasiStaticNote =
        "The reported Z_c is γ/(jωC_pul) with C_pul held at its QUASI-STATIC value, so it rises with " +
        "frequency as √ε_eff(f) rather than dispersing properly. Measured against kernel A's static " +
        "answer on a 50 Ω FR-4 line: +0.4% at 1 GHz, +2.3% at 5 GHz, +6.3% at 20 GHz. The " +
        "s-parameters themselves are not affected — this is a limitation of the γ-and-C route used to " +
        "REPORT Z_c, and a dispersive C needs a field integral kernel B does not have.";

    // ── D4: the DataSet, in the house convention plus one diagnostics group ────────────────────

    private static DataSet BuildDataSet(
        PlanarSolveResult sweep, IReadOnlyList<PlanarPortResolution> ports)
    {
        int nf = sweep.Points.Count, np = ports.Count;

        var freqs = new double[nf];
        var sMats = new Mat<Complex>[nf];
        for (int i = 0; i < nf; i++)
        {
            freqs[i] = sweep.Points[i].FrequencyHz;
            sMats[i] = sweep.Points[i].S;
        }

        var z0 = new Complex[np];
        for (int p = 0; p < np; p++) z0[p] = ports[p].Z0;

        // The house convention, exactly as RlgcToSparams and SParameterEngine end (R-mom-14).
        var snp = new SNP(freqs, sMats, MatrixType.S, MatrixFormat.RI, z0[0]);
        var ds  = DataSetBuilder.FromSnp(snp);
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0));

        // ── The per-port diagnostics: [freq, port] ────────────────────────────────────────────
        //
        // A fresh Axis[] per cube, for the same reason RlgcToSparams gives: a DataCube keeps its
        // axes, and sharing one mutable instance across several cubes is aliasing that only bites
        // much later.
        Axis[] Ax2() =>
        [
            new Axis("freq", freqs, "Hz"),
            new Axis("port", PortNumbers(ports), ""),
        ];
        Axis[] Ax1() => [new Axis("freq", freqs, "Hz")];

        // Row-major [freq, port], flat — the shape DataCube takes, and the same indexing
        // RlgcToSparams' own [freq, mode] cubes use.
        var gamma  = new Complex[nf * np];
        var zc     = new Complex[nf * np];
        var eeff   = new double[nf * np];
        var atten  = new double[nf * np];
        var cpul   = new double[nf * np];
        var elDeg  = new double[nf * np];
        var resid  = new double[nf * np];
        var rejct  = new double[nf * np];
        var usable = new double[nf];

        for (int i = 0; i < nf; i++)
        {
            var pt = sweep.Points[i];
            int flagged = 0;
            for (int p = 0; p < np && p < pt.Calibrations.Count; p++)
            {
                var c = pt.Calibrations[p];
                int o = i * np + p;
                gamma[o] = c.Gamma.Gamma;
                zc[o]    = c.Zc;
                eeff[o]  = c.Gamma.EffectivePermittivity(pt.FrequencyHz);
                atten[o] = c.Gamma.Alpha * NeperToDb;
                cpul[o]  = c.CPerMetre;
                elDeg[o] = c.Gamma.ElectricalDegrees;
                resid[o] = c.Box.ConsistencyResidual;
                rejct[o] = c.Box.RejectedResidual;
                if (!c.Gamma.Usable) flagged++;
            }
            usable[i] = flagged == 0 ? 1 : 0;
        }

        ds.AddToGroup(DiagnosticsGroup, "Gamma",              new DataCube(Ax2(), gamma));
        ds.AddToGroup(DiagnosticsGroup, "Zc",                 new DataCube(Ax2(), zc));
        ds.AddToGroup(DiagnosticsGroup, "Eeff",               new DataCube(Ax2(), eeff));
        ds.AddToGroup(DiagnosticsGroup, "AttenDbPerM",        new DataCube(Ax2(), atten));
        ds.AddToGroup(DiagnosticsGroup, "Cpul",               new DataCube(Ax2(), cpul));
        ds.AddToGroup(DiagnosticsGroup, "CalElectricalDeg",   new DataCube(Ax2(), elDeg));
        ds.AddToGroup(DiagnosticsGroup, "DeembedResidual",    new DataCube(Ax2(), resid));
        ds.AddToGroup(DiagnosticsGroup, "DeembedRejected",    new DataCube(Ax2(), rejct));
        ds.AddToGroup(DiagnosticsGroup, "CalibrationUsable",  new DataCube(Ax1(), usable));

        return ds;
    }

    private static double[] PortNumbers(IReadOnlyList<PlanarPortResolution> ports)
    {
        var n = new double[ports.Count];
        for (int i = 0; i < ports.Count; i++) n[i] = ports[i].Number;
        return n;
    }

    /// <summary>Nepers → dB, the same constant <c>RlgcToSparams</c> uses, restated rather than made
    /// public there — it is one number and two files that both mean 20/ln 10.</summary>
    private const double NeperToDb = 8.685889638065035;
}
