namespace CircuitRF.Engine;

/// <summary>
/// Controls when DC bias-supply ramping (source-stepping) is applied during the Newton solve.
/// Mirrors the <see cref="RegularizationMode"/> tri-state pattern.
/// Named <c>DcBiasStepping</c> (ramps DC bias supplies) — distinct from the HB
/// <c>DriveStepping</c> (reserved for Phase 4, ramps RF drive power).
/// </summary>
public enum DcBiasSteppingMode
{
    /// <summary>
    /// Attempt a direct cold-start Newton solve first (sources at full bias).
    /// Only if it fails to converge within the max-iteration cap, fall back to
    /// ramping the DC supplies from zero to full bias (the <c>Always</c> path).
    /// Easy circuits pay nothing; hard circuits are rescued automatically.
    /// </summary>
    IfNecessary,

    /// <summary>
    /// Always ramp the DC supplies from zero to full bias before Newton.
    /// Use for known-difficult circuits (stiff nonlinearity, far-from-bias cold start)
    /// where a failed direct attempt isn't worth paying for.
    /// </summary>
    Always,

    /// <summary>
    /// Direct solve only — no ramp ever. If Newton fails to converge,
    /// throw <see cref="NonlinearDcNotConvergedException"/> with the residual and
    /// iteration count. Use for validation/debugging to confirm a circuit is well-posed.
    /// </summary>
    Never,
}

/// <summary>
/// Controls when a regularization pass is applied to the MNA matrix.
/// </summary>
public enum RegularizationMode
{
    /// <summary>
    /// Assemble without regularization first; if factorization fails (singular matrix),
    /// re-assemble with regularization and retry. Clean circuits pay nothing and get the
    /// unperturbed result; degenerate circuits are rescued on the retry with a warning.
    /// When both ConductanceRegularization and InductanceRegularization are IfNecessary
    /// and factorization fails, <b>both</b> are applied on the retry (cheapest path).
    /// </summary>
    IfNecessary,

    /// <summary>
    /// Always apply the regularization before the first factorization attempt.
    /// Useful for large circuits known to need it — avoids a second matrix assembly.
    /// Slightly perturbs all results by the regularization value.
    /// </summary>
    Always,

    /// <summary>
    /// Never apply the regularization. If the matrix is singular, throw
    /// <see cref="SingularMatrixException"/> with the full structural diagnostic.
    /// Use for validation/debugging to confirm a circuit is non-degenerate.
    /// </summary>
    Never,
}

/// <summary>
/// Advanced settings for <see cref="SParameterEngine"/> (and future analyses).
/// All defaults match the historical behaviour while being individually overridable.
/// </summary>
public sealed class AnalysisSettings
{
    /// <summary>Singleton default — all settings at their recommended defaults.</summary>
    public static readonly AnalysisSettings Default = new();

    // ── Conductance regularization (gmin) ─────────────────────────────────────

    /// <summary>
    /// When gmin is applied, this conductance (Siemens) is added from every non-ground
    /// node to ground. Cures floating nodes that would otherwise make the G-block singular.
    /// Default: 1e-12 S (1 pS).
    /// </summary>
    public double Gmin { get; init; } = 1e-12;

    /// <summary>
    /// Controls when the <see cref="Gmin"/> conductance pass is applied.
    /// Default: <see cref="RegularizationMode.IfNecessary"/>.
    /// </summary>
    public RegularizationMode ConductanceRegularization { get; init; } = RegularizationMode.IfNecessary;

    // ── Inductance regularization (small series R on every inductor branch) ───

    /// <summary>
    /// When inductance regularization is applied, this series resistance (Ohms) is added
    /// to every inductor branch diagonal. Cures:
    ///   (a) a rank-deficient coupled-inductor D-block (zero eigenvalue from k≥1 or EM extraction);
    ///   (b) a DC voltage-pinned interface (ideal choke + ideal voltage source → Z(0)=0):
    ///       the resulting Y(0)≈1/InductanceRegR ≈ 1e6 S gives the Newton a well-conditioned
    ///       DC block, equivalent to the analytical limit as R→0 (linear-engine §4.3.1).
    /// Default: 1e-6 Ω — negligible at any RF frequency (|jωL| >> 1e-6 Ω), but large enough to
    /// keep Y(0) near 1e6 S for good Jacobian conditioning in the HB DC block.
    /// </summary>
    public double InductanceRegR { get; init; } = 1e-6;

    /// <summary>
    /// Controls when the <see cref="InductanceRegR"/> pass is applied.
    /// Default: <see cref="RegularizationMode.IfNecessary"/>.
    /// </summary>
    public RegularizationMode InductanceRegularization { get; init; } = RegularizationMode.IfNecessary;

    // ── Conductance ceiling (Gmax) ─────────────────────────────────────────────

    /// <summary>
    /// Maximum conductance (Siemens) used to stamp R=0 resistors as a near-short.
    /// Dual of Gmin: Gmin is the conductance floor (floating node rescue); Gmax is
    /// the conductance ceiling (zero-resistance short approximation).
    /// Default: 1e12 S (1 TΩ⁻¹). A warning is always emitted when Gmax is used.
    /// </summary>
    public double Gmax { get; init; } = 1e12;

    // ── Nonlinear-DC Newton solver ─────────────────────────────────────────────

    /// <summary>
    /// Absolute residual tolerance for the Newton solver.
    /// Convergence declared when ‖F(V)‖₂ &lt; NonlinearAbsTol.
    /// Default: 1e-6.
    /// </summary>
    public double NonlinearAbsTol { get; init; } = 1e-6;

    /// <summary>
    /// Relative residual tolerance (optional). When &gt; 0, convergence also declared
    /// when ‖F(V)‖₂ / ‖F(V₀)‖₂ &lt; NonlinearRelTol (normalized to the first-step residual).
    /// Default: 0 (disabled).
    /// </summary>
    public double NonlinearRelTol { get; init; } = 0.0;

    /// <summary>
    /// Maximum Newton iterations per continuation step.
    /// Exceeding this triggers continuation step-halving (if continuation is enabled).
    /// Default: 150.
    /// </summary>
    public int NonlinearMaxIter { get; init; } = 150;

    /// <summary>
    /// Controls when the DC bias ramp (source-stepping) engages.
    /// Default: <see cref="DcBiasSteppingMode.IfNecessary"/> — try a direct cold-start solve first;
    /// ramp only if it fails.
    /// </summary>
    public DcBiasSteppingMode DcBiasStepping { get; init; } = DcBiasSteppingMode.IfNecessary;

    /// <summary>
    /// Number of equal ramp steps when DC bias-stepping runs (0 → full bias in this many increments).
    /// Only relevant when <see cref="DcBiasStepping"/> is <c>Always</c> or the fallback ramp fires
    /// under <c>IfNecessary</c>.
    /// Default: 20.
    /// </summary>
    public int DcBiasRampSteps { get; init; } = 20;

    /// <summary>
    /// Maximum source-stepping continuation steps (0→1 in this many equal increments).
    /// Default: 20. Kept for back-compat; prefer <see cref="DcBiasRampSteps"/>.
    /// </summary>
    public int NonlinearMaxContinuationSteps { get; init; } = 20;

    /// <summary>
    /// Maximum step-halvings per continuation step before declaring non-convergence.
    /// Default: 10.
    /// </summary>
    public int NonlinearMaxHalvings { get; init; } = 10;

    // ── HB-specific settings (Phase 4) ────────────────────────────────────────

    /// <summary>
    /// Controls when HB RF-drive continuation (power stepping) engages.
    /// Distinct from <see cref="DcBiasStepping"/> which ramps DC supply voltages.
    /// Default: <see cref="DcBiasSteppingMode.IfNecessary"/> — warm-start from previous
    /// sweep point (or DC seed at first point); fall back to power ramping only on failure.
    /// </summary>
    public DcBiasSteppingMode DriveStepping { get; init; } = DcBiasSteppingMode.IfNecessary;

    /// <summary>
    /// Number of drive-stepping increments (0 → full drive in this many equal steps).
    /// Only relevant when <see cref="DriveStepping"/> is Always or the fallback fires.
    /// Default: 10.
    /// </summary>
    public int DriveRampSteps { get; init; } = 10;

    /// <summary>
    /// Maximum Newton iterations per HB solve before declaring non-convergence / triggering backoff.
    /// Default: 100. Both type=hb and type=loadpull directives default to MaxIter=100 and
    /// build an AnalysisSettings with this value. The settings default here matches that directive
    /// default so engine-direct callers (tests, CLI) also get 100 iterations.
    /// </summary>
    public int HbMaxIter { get; init; } = 100;

    /// <summary>
    /// When true, the HB engine prints per-solve diagnostics to stderr: the auto-regularization notice
    /// (<c>[HB] ExtractDC: …</c>), the DC operating point (<c>[HB-DC]:</c> / <c>[HB2D-DC]:</c>), and the
    /// per-point convergence trace (<c>[HB trace]</c>).
    ///
    /// Default <c>false</c>. These fire once per HB Newton solve, so in a parametric sweep they would
    /// otherwise repeat at every point (e.g. a Pin sweep prints the inductance-regularization notice for
    /// every power step). The auto-regularization is benign — it converges to the exact answer as the
    /// series R→0 — so it does not need to nag per point; enable this only when debugging convergence.
    /// (Non-convergence warnings are always surfaced via the diagnostics channel, independent of this.)
    /// </summary>
    public bool HbConsoleDiagnostics { get; init; } = false;

    /// <summary>
    /// When true (default), a parametric sweep whose innermost analysis is an HB analysis — at ANY
    /// tone count — <strong>warm-starts</strong> each point from the previous point's converged
    /// spectrum, the continuation method (harmonic-balance.md §11). This skips the per-point
    /// nonlinear-DC seed solve and follows the solution branch, cutting Newton iterations
    /// (benchmarked ~45% fewer iters and N→1 DC solves on a GaN-PA Pin sweep, with bit-identical
    /// results). The seed is the run's own interface spectrum — <c>[N, K+1]</c> for a single tone,
    /// <c>[N, M]</c> over the mixing lattice for two or more — and each engine path checks the shape
    /// it was handed, so a sweep that changes dimension falls back to a cold seed rather than
    /// mis-reading one.
    ///
    /// The seed chains <em>only along the innermost sweep axis</em> and resets at each outer-sweep step
    /// (each outer step runs a fresh inner sweep, whose first point is DC-seeded). A non-converged point
    /// also resets the chain. Set false to force a cold DC seed at every point (e.g. to study
    /// branch-dependence near a bifurcation).
    /// </summary>
    public bool HbSweepWarmStart { get; init; } = true;

    // ── Multi-tone (T ≥ 3) HB ceiling and transform (harmonic-balance.md §6.5) ──────────────

    /// <summary>
    /// Maximum number of independent excitation tones an HB analysis may declare. Default 6.
    ///
    /// <para>A cap exists because the T ≥ 3 path solves a DENSE Jacobian, and the retained
    /// mixing-product count grows steeply with tone count — see <see cref="HbMaxMixProducts"/>,
    /// which is the constraint that actually binds in practice. This one exists so a typo
    /// (<c>NumFreqs=60</c>) refuses immediately with a clear message instead of attempting a
    /// lattice nothing can hold.</para>
    /// </summary>
    public int HbMaxTones { get; init; } = 6;

    /// <summary>
    /// Maximum retained mixing products M for a multi-tone HB analysis. Default 600, i.e. ~2,400
    /// dense unknowns on a two-node nonlinear interface.
    ///
    /// <para>This is the knob that actually binds, and the engine refuses <em>at setup time</em>
    /// — before any extraction or Newton solve — naming the largest MaxMixOrder that fits, so a
    /// too-ambitious analysis never becomes a long run that then throws. The default admits the
    /// configurations that are practical on a dense solve (6 tones at order 3 = 189 products,
    /// 4 tones at order 4 = 161, 3 tones at order 9 = 580) and excludes the ones that are not
    /// (6 tones at order 4 = 645 products would cost roughly 40 s <em>per Newton iteration</em>).
    /// Raise it deliberately, accepting the runtime.</para>
    /// </summary>
    public int HbMaxMixProducts { get; init; } = 600;

    /// <summary>
    /// Ratio of APFT torus samples to real unknowns for the multi-tone transform (default 2.0).
    ///
    /// <para>The transform is a least-squares projection onto the retained lattice, so
    /// oversampling controls how out-of-band content is handled and how well conditioned ΓᵀΓ is.
    /// Cost is linear in this: it multiplies both the per-iteration device evaluations and the
    /// Jacobian's triple product. <see cref="HarmonicBalance.HbApft"/> gates on the measured
    /// conditioning and refuses rather than returning a rank-deficient transform, so lowering
    /// this trades margin, not silence.</para>
    /// </summary>
    public double HbApftOversample { get; init; } = 2.0;

    /// <summary>
    /// Route a TWO-tone analysis through the T-tone lattice path (<see cref="HarmonicBalance.HbApft"/>
    /// + <see cref="HarmonicBalance.HbNewtonNd"/>) — the path three or more tones already take —
    /// rather than the rectangular-FFT path (<see cref="HarmonicBalance.HbFft2D"/> +
    /// <see cref="HarmonicBalance.HbNewton2D"/>). <b>Default TRUE since 2026-08-30</b> (owner
    /// decision, on the measurements in <c>src/Engine/RESOLVED.md</c> §HB-P1). Set it FALSE to get
    /// the FFT path back; nothing else in the engine reads it.
    ///
    /// <para><b>Why it is on.</b> Both paths solve the same retained diamond, but the FFT path
    /// evaluates the device on a full rectangular grid in order to keep a diamond-shaped subset of
    /// it: 1,024 time samples for the 62 complex unknowns of the shipping order-5 two-tone
    /// analysis, against roughly 250 on the lattice. Device evaluation is the two-tone path's
    /// dominant per-iteration cost, so that is a measured <b>3.5×</b> on <c>hero5.cnl</c> —
    /// 21.3 ms/point to 6.1 ms/point — and it is the only thing that moves it: HB-P1's M1–M3 took
    /// the same point from 21.6 ms to 21.2 ms and no further.</para>
    ///
    /// <para><b>What it changes, and what it does not.</b> The two paths discard everything outside
    /// the retained diamond DIFFERENTLY — the FFT grid aliases it back by periodic wrap, the
    /// lattice least-squares projects it — so they do not agree to the last bit. The disagreement
    /// grows monotonically with mixing order and is largest at the products on the diamond's outer
    /// rim, the ones most exposed to what was thrown away: measured 1.5e-16 at DC, 1.8e-8 at the
    /// carriers, 4.8e-6 at IM3, <b>8.9e-2 at the order-5 rim</b>. That is truncation, not error
    /// (harmonic-balance.md §6.5), and <c>HbNewtonNdVs2DTests</c> pins that it shrinks as the
    /// diamond grows — but it does mean <b>the outermost retained order should not be read at face
    /// value on either path</b>. Raise <c>MaxMixOrder</c> past the product you actually want.</para>
    ///
    /// <para>The frozen two-tone goldens do NOT move: <c>Hero5GateTests</c> ignores bins below 1e-5
    /// and allows 1e-4 relative, and orders 0–3 — everything it checks — agree to 4.8e-6 or better.
    /// The committed goldens were produced on the FFT path and are deliberately left that way, so
    /// they are now a CROSS-PATH check rather than a self-check. See RESOLVED §HB-P1.</para>
    ///
    /// <para>Index order, cube shapes, <c>mixIndex</c> labels, <c>TwoToneMeasurements</c> and the
    /// data display's two-tone spectrum are unaffected either way — <c>MixingLattice</c> at T = 2
    /// reproduces <c>MixingGrid</c>'s locked enumeration element for element
    /// (<c>MixingLatticeTests</c>). Only the numbers move.</para>
    /// </summary>
    public bool HbTwoToneOnLattice { get; init; } = true;

    // ── Frequency-sweep parallelism (SP-P3) ─────────────────────────────────────

    /// <summary>
    /// How many frequency points of an S-parameter sweep may be solved at once. 0 = automatic
    /// (the default): the engine picks from <see cref="Environment.ProcessorCount"/> and the
    /// length of the grid. 1 pins the run to the serial path.
    ///
    /// <para>Every frequency point is an independent solve, so the sweep splits into contiguous
    /// chunks running on separately elaborated copies of the netlist — the copies are the whole
    /// thread-safety story, since a model writes state during <c>Stamp</c>. The knob exists so a
    /// laptop can be pinned and a test can force a chosen degree; it does not change any result,
    /// which stays bit-identical to the serial path at every degree.</para>
    /// </summary>
    public int MaxParallelism { get; init; } = 0;
}
