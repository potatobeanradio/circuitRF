namespace CircuitRF.WBond.Mom;

/// <summary>
/// The knobs of the distributed (MoM / quasi-static PEEC) wirebond kernel — kernel W1 of
/// <c>docs/design/mom-wirebond-kernel.md</c>.
///
/// <para><b>There is no frequency here, and there must never be one.</b> Every matrix this kernel
/// builds — <b>L</b>, <b>P</b>, <b>A</b>, <b>R</b>, <b>G</b>, <b>K̃</b>, <b>W</b>, <b>H</b> — is
/// frequency-independent, filled once per design and reused across an entire sweep. That property is
/// the whole speed argument for the kernel (§4.1 of the design note), and a setting that varied with
/// frequency would quietly destroy it. The one frequency-dependent quantity, the per-segment internal
/// impedance of <see cref="SegmentInternalZ"/>, is diagonal and closed-form and takes its frequency as
/// a call argument rather than as state.</para>
/// </summary>
public sealed record WireMomSettings
{
    /// <summary>
    /// How many segments each wire is meshed into, as a target on its developed path length.
    ///
    /// <para><b>24 is measured, not assumed</b> — see <c>src/WBond/Mom/RESOLVED.md</c> §9.7 for the
    /// four-point convergence table on a ball bond over ground, where the 24 → 48 change in the wire's
    /// total capacitance is a small fraction of the 12 → 24 change. The design note expects ~25–30
    /// over a ~100 mil arc, which is the same neighbourhood.</para>
    /// </summary>
    public int TargetSegmentsPerWire { get; init; } = 24;

    /// <summary>
    /// The hard cap on one wire's segment count, whatever <see cref="TargetSegmentsPerWire"/> and the
    /// geometry between them ask for.
    ///
    /// <para>When it bites, the mesh report says so by name. <b>Silently coarsening a wire is exactly
    /// how a confidently wrong number gets produced</b>, so the clamp is reported, never hidden.</para>
    /// </summary>
    public int MaxSegmentsPerWire { get; init; } = 200;

    /// <summary>
    /// The total segment count above which meshing <b>refuses</b>, rather than allocating.
    ///
    /// <para>8,000 segments is ~1 GB at the peak of §8's arithmetic (two real N² matrices plus WM-2's
    /// complex one). The refusal names three <b>binding</b> remedies with their real numbers
    /// substituted — a refusal that names knobs which do not change the outcome has already cost this
    /// repository a debugging session (<c>src/Engine/Mom/RESOLVED.md</c>, the AIM ceiling).</para>
    /// </summary>
    public int UnknownCeiling { get; init; } = 8_000;

    /// <summary>
    /// The <c>s/a</c> ratio (closest centreline approach over the geometric-mean radius) below which
    /// the mesh report <b>warns</b>. RW17.
    ///
    /// <para>It warns and does not refuse: the thin-wire reduced kernel is a few percent optimistic
    /// below this, which is a stated accuracy limit and not an error.</para>
    /// </summary>
    public double ProximityWarnRatio { get; init; } = 6.0;

    /// <summary>
    /// The near/far threshold of <see cref="PotentialCoefficients.Kernel"/>, <b>for this kernel's own
    /// half-segment cells</b>.
    ///
    /// <h3>It is 4.0 here and 3.5 there, and the difference is measured</h3>
    /// <para><see cref="PotentialCoefficients.FarThresholdFactor"/> = 3.5 was measured against
    /// <i>wire-length</i> cells, and its own doc comment says 3.5 is the smallest value inside a 0.1 %
    /// target. <b>This kernel's cells are half-segments — roughly 1/48 of a wire — and at that size the
    /// same threshold is outside the same target.</b> Swept on a 40-wire / 10-array ball-bond design at
    /// 24 segments per wire (N_s = 1,040), worst per-wire self-capacitance error against an all-near
    /// reference: <b>0.508 % at 2, 0.173 % at 3, 0.121 % at 3.5, 0.0675 % at 4, 0.0400 % at 5,
    /// 0.0218 % at 6, 0.0130 % at 7</b>. <b>4.0 is the smallest swept value inside 0.1 %</b>, which is
    /// the same rule that picked 3.5 there, applied to the cells this kernel actually has.</para>
    ///
    /// <para><b>The extra half costs nothing measurable</b>: the N_s = 1,040 fill is 3.1 ms at both 3.5
    /// and 4.0 (Release), because the near branch for the parallel pairs that dominate a bond array is
    /// <see cref="Grover.ParallelScalarKernel"/>, a closed form. Forcing the accurate kernel everywhere
    /// (<see cref="double.PositiveInfinity"/>) costs 26 ms, so the far branch is still earning its
    /// keep.</para>
    ///
    /// <para><b><see cref="PotentialCoefficients"/> itself is untouched</b> — the wire-basis model keeps
    /// 3.5, which is correct for its own cells. Pass <see cref="double.PositiveInfinity"/> to force the
    /// accurate kernel, which is what the §9.3 identity gates do on both sides so the near/far split
    /// cannot be what makes them differ.</para>
    /// </summary>
    public double FarThresholdFactor { get; init; } = 4.0;

    /// <summary>
    /// The lowest frequency <see cref="WireMomSolver"/> will solve at, in hertz.
    ///
    /// <h3>It is a conditioning floor, and the number is MEASURED</h3>
    /// <para><c>M~(w) = (jw)^2 L + jw D(w) + K~</c> tends to <c>K~</c> as w tends to zero, and
    /// <c>K~</c> is singular whenever terminal shorting created a loop — which is every array with two
    /// or more wires. The blow-up is projected out of <c>Z_port</c> analytically, but the <i>condition
    /// number</i> of the matrix being factorised still grows like 1/f, so below some frequency the
    /// answer is rounding noise. <c>src/WBond/Mom/RESOLVED.md</c> carries the decade-by-decade sweep
    /// this default came from.</para>
    ///
    /// <para><b>100 kHz is where it was measured to depart by 0.1 %</b>, on four designs from one loop
    /// to eighteen — the series inductance extracted from <c>Y_port</c> against
    /// <see cref="ImpedanceReduction.ArrayImpedance"/>, decade by decade from 1 kHz. The departure is
    /// remarkably insensitive to the loop count (0.043 % to 0.080 % at 100 kHz across all four) and
    /// falls by two further decades within one decade above it. Below 100 kHz it degrades fast: 0.4 % at
    /// 30 kHz, 4 % at 10 kHz, 300 % at 1 kHz.</para>
    ///
    /// <para><b>There is nothing to fall back to inside this kernel, and that is fine</b> — the lumped
    /// <see cref="ImpedanceReduction"/> path consumes <b>L</b> and <b>A</b> only, never forms
    /// <c>K~</c>, and therefore has no low-frequency limit at all. The refusal names it.</para>
    /// </summary>
    public double MinimumFrequencyHz { get; init; } = 1e5;

    /// <summary>Fill <b>L</b> and <b>P</b> concurrently. On by default — see <see cref="SegmentInductance"/>.</summary>
    public bool Parallel { get; init; } = true;

    /// <summary>
    /// Factorise <c>M̃(ω)</c> with the complex-<b>symmetric</b> <see cref="ComplexLdlt"/> rather than
    /// with <see cref="ComplexLu"/>'s pivoted general LU. On by default: it is the dominant
    /// per-frequency cost and the symmetric factorisation is half the flops.
    ///
    /// <para><b>Correctness does not depend on this being right</b> — a point whose pivots collapse
    /// falls back to the LU automatically and says so in the result's notes (see
    /// <see cref="MinimumPivotRatio"/>). Turning it off is a way to <i>measure</i> the two against each
    /// other, not a safety switch anyone needs to reach for.</para>
    /// </summary>
    public bool SymmetricFactorisation { get; init; } = true;

    /// <summary>
    /// The <c>min|d_k| / max|d_k|</c> below which a <see cref="ComplexLdlt"/> factorisation is
    /// discarded and the point is refactorised with <see cref="ComplexLu"/>.
    ///
    /// <para>An unpivoted LDLᵀ on a complex symmetric matrix has no diagonal-dominance guarantee: a
    /// pivot can be annihilated by cancellation between the real and imaginary parts on a matrix that
    /// is nowhere near singular, and the factorisation then completes and returns finite garbage.
    /// <b>1e-12 is a declared threshold, not a measured one</b> — what is measured is how often it
    /// bites, which on real bond geometry is <i>never</i> (see <c>RESOLVED.md</c>). It exists so that
    /// "never" is enforced rather than assumed.</para>
    /// </summary>
    public double MinimumPivotRatio { get; init; } = 1e-12;

    /// <summary>
    /// How much memory a frequency-parallel sweep may spend on its per-thread <c>M̃</c> buffers, in
    /// bytes. <c>null</c> derives it as a quarter of
    /// <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/>.
    ///
    /// <para><b>The degree of parallelism of a sweep is set by memory, not by cores.</b> Each thread
    /// needs its own <c>M̃</c> — <c>16·N_s²</c> bytes — which is 14.7 MB at N_s = 960 and <b>369 MB</b>
    /// at N_s = 4,800. Ten threads of the latter is 3.6 GB and would page rather than compute, so the
    /// thread count is <i>computed</i> (<see cref="WireMomCost.SolveThreadCount"/>) and the number
    /// chosen is reported in the result's notes. A user whose 200-wire sweep ran single-threaded can
    /// see why in one line.</para>
    ///
    /// <para>A quarter of available memory is the honest default: the process is a GUI with a document
    /// model, a layout view and possibly a second analysis in it, and a sweep that takes everything
    /// there is to take makes the rest of the application the thing that fails.</para>
    /// </summary>
    public long? SolveMemoryBudgetBytes { get; init; }

    /// <summary>
    /// A hard cap on the sweep's thread count, whatever the memory budget allows. <c>null</c> means
    /// <see cref="Environment.ProcessorCount"/>. Set it to 1 to make a sweep serial — which is what the
    /// speedup measurement does, and the only reason it exists.
    /// </summary>
    public int? MaxSolveThreads { get; init; }

    /// <summary>The shipped defaults.</summary>
    public static WireMomSettings Default { get; } = new();

    /// <summary>
    /// The <b>Fast</b> rung of §5's segmentation ladder: 8 segments per wire.
    ///
    /// <para>What a 200-wire array is solved at. It is not a "draft" setting in the sense of being
    /// wrong — the current path is mesh-invariant (<c>SeriesArmImpedance</c> reproduces the analytic
    /// array impedance to 4e-10 at every rung); what coarsening costs is charge-path accuracy, and
    /// <c>RESOLVED.md</c> carries the measured |ΔS| for each rung.</para>
    /// </summary>
    public static WireMomSettings Fast { get; } = new() { TargetSegmentsPerWire = 8 };

    /// <summary>
    /// The <b>Accurate</b> rung: 2 × <see cref="Balanced"/>. Roughly <b>6× the per-point cost</b> and
    /// 3× the setup of Balanced, for a measured ~4e-3 of |ΔS| at 40 GHz — which is worth having as a
    /// confirmation run and is not worth having as a default.
    /// </summary>
    public static WireMomSettings Accurate { get; } = new() { TargetSegmentsPerWire = 48 };

    /// <summary>
    /// The shipped rung, 24 segments per wire — the same object as <see cref="Default"/>, named so the
    /// three rungs read as one ladder at the call site.
    /// </summary>
    public static WireMomSettings Balanced => Default;

    /// <summary>
    /// The ladder as data: <c>(name, segments per wire)</c>, coarsest first. A UI that offers the three
    /// rungs and an explicit override reads this rather than repeating the numbers.
    /// </summary>
    public static IReadOnlyList<(string Name, int SegmentsPerWire)> Ladder { get; } =
        [("Fast", 8), ("Balanced", 24), ("Accurate", 48)];
}
