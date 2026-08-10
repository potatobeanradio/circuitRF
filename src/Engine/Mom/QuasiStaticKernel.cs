using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Mom;

/// <summary>A solve, with the intermediate quantities a test or a diagnostic wants to see.</summary>
/// <param name="SolveNotes">
/// Remarks produced by the s-parameter step itself, which the RLGC extraction could not have made
/// because it does not know the frequencies — chiefly R-gen-5's measured mode-coupling residual.
/// Additive and defaulted, so every pre-L7b-b construction site still compiles.
/// </param>
public sealed record EmSolveResult(
    DataSet                Data,
    RlgcModel              Rlgc,
    EmMeshReport           MeshReport,
    IReadOnlyList<string>? SolveNotes = null);

/// <summary>
/// Kernel A — the 2D quasi-static per-unit-length kernel (§10.3). Solves the <i>cross-section</i>
/// of a uniform transmission-line structure for [C], [C₀], [L], [G] and [R], then forms the
/// s-parameters of a length-ℓ uniform line.
///
/// <para>The whole model is frequency-independent (R-mom-11), so a 1001-point sweep costs the same
/// four matrix fills as a 3-point one — this is the property that makes v1 dramatically snappier
/// than the full-wave kernel that eventually replaces it.</para>
/// </summary>
public sealed class QuasiStaticKernel : IEmKernel
{
    private readonly bool _dispersionCorrection;

    /// <param name="dispersionCorrection">
    /// Opt in to the closed-form Kirschning–Jansen dispersion correction (§10.3.2). <b>Off by
    /// default and applicable to the single-microstrip case only</b> — it is a correction applied
    /// on top of a validated static result, never a substitute for one, so it must never run before
    /// the static answer has been validated on its own.
    /// </param>
    public QuasiStaticKernel(bool dispersionCorrection = false)
        => _dispersionCorrection = dispersionCorrection;

    /// <summary>Worded once so the registry, the panel and the notes cannot drift.
    /// <b>No "kernel A" (owner request, 2026-08-09)</b> — this string is shown in the EM Setup panel
    /// and stamped into every <c>.snp</c> header, and our own internal A/B shorthand means nothing to
    /// the person reading either.</summary>
    public const string KernelName = "Quasi-static cross-section";

    public string Name => KernelName;

    public EmCapabilities Capabilities => EmCapabilities.UniformCrossSection;

    // ── R-mom-17: the only place a refusal is worded ───────────────────────────────────────────
    //
    // Kernel A's own refusals are the ones it can see from an EmProblem. The GEOMETRIC refusals —
    // bends, tapers, non-parallel conductors — are detected by the Ui-side extractor before an
    // EmProblem is ever built (§10.3.3: "This geometry has a bend at (x, y); the quasi-static
    // solver handles uniform cross-sections only. Full-wave analysis of discontinuities arrives in
    // L8"), and its message must follow the same shape: name the specific feature, name where the
    // capability arrives.

    public EmSuitability CanSolve(EmProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        if (problem.Conductors.Count == 0)
            return EmSuitability.No("This problem has no conductors; there is nothing to solve.");

        if (!(problem.LengthMeters > 0))
            return EmSuitability.No(
                $"The propagation length is {problem.LengthMeters:G4} m. A per-unit-length kernel " +
                "needs a positive line length to form s-parameters from.");

        var regionCheck = CheckRegions(problem);
        if (regionCheck is not null) return regionCheck;

        foreach (var c in problem.Conductors)
        {
            if (c.Outline.Count < 3)
                return EmSuitability.No(
                    $"Conductor '{c.Name}' has {c.Outline.Count} vertices. A conductor is a closed " +
                    "polygon of finite thickness, not a line or a point.");

            if (Polygon2D.Area(c.Outline) <= 0)
                return EmSuitability.No(
                    $"Conductor '{c.Name}' encloses zero area — it is a zero-thickness sheet. " +
                    "Wheeler's incremental-inductance rule recedes a conductor surface INTO the " +
                    "metal, so a sheet has no interior to recede into and its conductor loss would " +
                    "be undefined rather than merely approximate. Give it a finite thickness.");

            var hit = Polygon2D.FindSelfIntersection(c.Outline);
            if (hit is not null)
                return EmSuitability.No(
                    $"Conductor '{c.Name}' has a self-intersecting outline: edge {hit.Value.I} " +
                    $"crosses edge {hit.Value.J}. The boundary-charge formulation needs a simple " +
                    "closed polygon.");

            if (c.SigmaSm <= 0)
                return EmSuitability.No(
                    $"Conductor '{c.Name}' has conductivity {c.SigmaSm:G4} S/m. Use a positive " +
                    "conductivity, or double.PositiveInfinity for a perfect conductor.");
        }

        var portCheck = CheckPorts(problem);
        if (portCheck is not null) return portCheck;

        return EmSuitability.Yes;
    }

    private static EmSuitability? CheckRegions(EmProblem problem)
    {
        if (problem.Regions.Count == 0)
            return EmSuitability.No(
                "This problem has no dielectric regions. The uniform-line analysis needs at least one — a single " +
                "air region spanning ±infinity is the free-space case.");

        for (int i = 0; i < problem.Regions.Count; i++)
        {
            var r = problem.Regions[i];
            if (!(r.YTop > r.YBottom))
                return EmSuitability.No(
                    $"Dielectric region {i} runs from y = {r.YBottom:G4} to y = {r.YTop:G4} m, which " +
                    "is empty or inverted. Regions are ordered bottom-to-top.");
            if (r.Material.EpsR < 1)
                return EmSuitability.No(
                    $"Dielectric region {i} has εᵣ = {r.Material.EpsR:G4}. Relative permittivity is ≥ 1.");
        }

        for (int i = 0; i + 1 < problem.Regions.Count; i++)
        {
            double top = problem.Regions[i].YTop;
            double bot = problem.Regions[i + 1].YBottom;
            if (top == bot) continue;
            string what = bot > top ? "a gap" : "an overlap";
            return EmSuitability.No(
                $"Dielectric regions {i} and {i + 1} leave {what} between y = {top:G4} and " +
                $"y = {bot:G4} m; regions must tile the y axis without gaps or overlap. This analysis's " +
                "2.5D premise is horizontal, laterally infinite interfaces — a vertical or sloped " +
                "dielectric boundary is out of scope. THE FULL-WAVE PLANAR ANALYSIS DOES NOT HELP " +
                "HERE EITHER, AND NOT BECAUSE IT IS UNFINISHED: it now takes an arbitrary stratified " +
                "medium (LayerStack) with metal on many levels and vias between them, but \"a general " +
                "layered stack\" means N HORIZONTAL layers. A sloped or vertical dielectric boundary " +
                "is outside the 2.5D premise both kernels share, so no amount of layering reaches it " +
                "— it needs a genuinely 3-D formulation, which nothing in circuitRF has. Model the " +
                "boundary as a staircase of horizontal layers, or accept the nearest uniform stack.");
        }

        if (!double.IsNegativeInfinity(problem.Regions[0].YBottom) && problem.Ground is null)
            return EmSuitability.No(
                $"The bottom dielectric region starts at y = {problem.Regions[0].YBottom:G4} m with no " +
                "ground plane below it, so the stack is not closed. Either extend it to " +
                "double.NegativeInfinity or add a ground plane.");

        return null;
    }

    private static EmSuitability? CheckPorts(EmProblem problem)
    {
        if (problem.Ports.Count == 0)
            return EmSuitability.No("This problem has no ports; there is no excitation to solve for.");

        foreach (var p in problem.Ports)
        {
            if (problem.FindConductor(p.Conductor) is null)
                return EmSuitability.No(
                    $"Port {p.Number} names conductor '{p.Conductor}', which is not in this problem. " +
                    $"Known conductors: {string.Join(", ", Names(problem))}.");

            if (p.ReferenceConductor is not null && problem.FindConductor(p.ReferenceConductor) is null)
                return EmSuitability.No(
                    $"Port {p.Number} references conductor '{p.ReferenceConductor}', which is not in " +
                    $"this problem. Known conductors: {string.Join(", ", Names(problem))}.");

            if (p.ReferenceConductor is null && problem.Ground is null)
                return EmSuitability.No(
                    $"Port {p.Number} has no reference conductor and this problem has no ground plane, " +
                    "so its return path is undefined. Name a reference conductor on the port, or add " +
                    "a ground plane to the stackup.");
        }

        // ── R-gen-9: NARROW the refusals again, never delete them. L7b refused N > 2 and refused
        // an asymmetric pair; L7b-b's general modal decomposition supersedes both, so what replaces
        // them is not "nothing" — it is a conductor-count ceiling with a stated reason, below. The
        // geometric-symmetry check itself SURVIVES (ModalDecomposition.CheckGeometricSymmetry) and
        // is still what makes L7b's exact even/odd construction applicable as a test oracle; it has
        // simply stopped being a refusal. Every L8/L9/LW refusal elsewhere in this file is untouched.

        int signalCount = 0;
        foreach (var c in problem.Conductors)
            if (!IsReference(problem, c.Name)) signalCount++;

        if (signalCount > MaxSignalConductors)
            return EmSuitability.No(
                $"This cross-section has {signalCount} signal conductors; the uniform-line analysis solves up to " +
                $"{MaxSignalConductors}. {ConductorCeilingReason}");

        // Every signal conductor must own exactly two ports: its own two ends. This is the general
        // statement of kernel A's "ports 1 and 2 are on different conductors" — that refusal was
        // right when there was one line, and what it was really protecting is that a port PAIR
        // belongs to ONE conductor. Checked before the total count, because naming the offending
        // conductor is more use than reporting an arithmetic mismatch.
        foreach (var c in problem.Conductors)
        {
            if (IsReference(problem, c.Name)) continue;
            int owned = 0;
            foreach (var p in problem.Ports)
                if (string.Equals(p.Conductor, c.Name, StringComparison.Ordinal)) owned++;
            if (owned != 2)
                return EmSuitability.No(
                    $"Conductor '{c.Name}' has {owned} port" + (owned == 1 ? "" : "s") +
                    ". Each conductor of a uniform line carries exactly two — its near end and its " +
                    "far end (D3: port 2k−1 is conductor k's near end, 2k its far end). Coupling " +
                    "between conductors is carried by the modal decomposition, not by moving a port " +
                    "onto a different conductor.");
        }

        // Backstop for what the per-conductor loop cannot see: a port sitting on a REFERENCE
        // conductor, which owns none of the line's ends.
        int wantPorts = 2 * signalCount;
        if (problem.Ports.Count != wantPorts)
            return EmSuitability.No(
                $"This problem has {problem.Ports.Count} ports for {signalCount} signal conductor" +
                (signalCount == 1 ? "" : "s") + $", but a uniform line needs exactly {wantPorts} — " +
                "one at each end of each conductor.");

        return null;
    }

    /// <summary>
    /// <b>R-gen-9's ceiling, and what actually bounds it.</b> Two costs grow with N: the boundary
    /// mesh's dense complex LU, which is O(N_seg³) in the number of boundary unknowns and is
    /// factored once per matrix fill, and <see cref="RlgcExtractor"/>'s fill count, which is
    /// <c>2 + N + (ground ? 1 : 0)</c> — so total work grows roughly as N·N_seg³ and N_seg itself
    /// grows with N. The modal step is only O(N³) in conductors and is never the binding constraint
    /// at this scale.
    ///
    /// <para><b>Measured</b> on the FR-4 starter stackup (1 mm strips, 0.3 mm gaps), RLGC extraction
    /// only — the s-parameter step is milliseconds at any N:</para>
    /// <code>
    ///        EmMeshSettings.Default          Refined(2)
    ///  N=2     206 unknowns,   0.04 s      292 unknowns,   0.07 s
    ///  N=8     680 unknowns,   1.0 s       886 unknowns,   2.1 s
    ///  N=16  1,312 unknowns,   9.1 s     1,678 unknowns,  19.1 s
    /// </code>
    /// <para>Sixteen is chosen because a 16-way bus is a realistic thing to want and still costs a
    /// wait a user can explain to themselves; the cubic growth means twenty would be minutes at a
    /// refined mesh, with no progress reporting — which is how a user discovers a limit by waiting.
    /// Note the cost is dominated by the <i>mesh</i>, so a refined mesh raises it steeply: these
    /// figures are the floor, not the ceiling.</para>
    /// </summary>
    public const int MaxSignalConductors = 16;

    /// <summary>The reason the ceiling exists, worded once so the refusal and the docs cannot drift.</summary>
    internal const string ConductorCeilingReason =
        "The limit is the dense boundary-element solve, which is cubic in the number of mesh " +
        "unknowns and is repeated once per conductor for Wheeler's loss derivative — not the modal " +
        "decomposition, which is only cubic in the conductor count. A wider bus needs a compressed " +
        "or iterative solve, which nothing in circuitRF has: the full-wave planar kernel (B) is " +
        "dense too, with its own hard unknown ceiling (SurfaceMesher.UnknownCeiling), and matrix " +
        "compression is measured but not built there either — see src/Engine/Mom/CLAUDE.md §L9e for " +
        "the numbers and why.";

    private static bool IsReference(EmProblem problem, string conductor)
    {
        foreach (var p in problem.Ports)
            if (string.Equals(p.ReferenceConductor, conductor, StringComparison.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<string> Names(EmProblem problem)
    {
        foreach (var c in problem.Conductors) yield return $"'{c.Name}'";
    }

    // ── mesh & solve ──────────────────────────────────────────────────────────────────────────

    public EmMeshReport Mesh(EmProblem problem, EmMeshSettings settings)
        => BoundaryMesher.Mesh(problem, settings);

    public DataSet Solve(EmProblem problem, EmMeshSettings settings, double[] freqsHz, CancellationToken ct)
        => SolveDetailed(problem, settings, freqsHz, ct).Data;

    public EmSolveResult SolveDetailed(EmProblem problem, EmMeshSettings settings,
                                       double[] freqsHz, CancellationToken ct = default)
    {
        var ok = CanSolve(problem);
        if (!ok.Ok) throw new InvalidOperationException($"{Name} cannot solve this problem. {ok.Reason}");

        var report = BoundaryMesher.Mesh(problem, settings);
        var rlgc   = RlgcExtractor.Extract(problem, report);

        var ports = new List<EmPort>(problem.Ports);
        ports.Sort((a, b) => a.Number.CompareTo(b.Number));
        var z0 = new Complex[ports.Count];
        for (int i = 0; i < ports.Count; i++) z0[i] = ports[i].Z0;

        var dispersion = _dispersionCorrection ? TryMicrostripDispersion(problem) : null;
        var notes = new List<string>();
        var ds = RlgcToSparams.Build(rlgc, problem.LengthMeters, freqsHz, z0, dispersion, ct, notes);
        return new EmSolveResult(ds, rlgc, report, notes);
    }

    /// <summary>
    /// The Kirschning–Jansen correction applies to a single microstrip — one conductor over a
    /// ground plane on one substrate. Anything else returns null rather than being corrected with a
    /// formula that was never derived for it.
    /// </summary>
    public static MicrostripDispersion? TryMicrostripDispersion(EmProblem problem)
    {
        if (problem.Conductors.Count != 1 || problem.Ground is null) return null;

        var (x0, y0, x1, _) = Polygon2D.Bounds(problem.Conductors[0].Outline);
        double w = x1 - x0;
        double h = y0 - problem.Ground.Y;
        if (!(w > 0) || !(h > 0)) return null;

        // The substrate is whatever region the metal sits directly on.
        double epsR = 0;
        foreach (var r in problem.Regions)
            if (r.YBottom <= problem.Ground.Y + 0.5 * h && r.YTop >= y0 - 1e-15) epsR = r.Material.EpsR;
        return epsR >= 1 ? new MicrostripDispersion(w / h, epsR, h) : null;
    }
}
