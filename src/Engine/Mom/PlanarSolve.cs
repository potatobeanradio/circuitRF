// L8d — the per-frequency driver, and the one caching decision that is worth stating.
//
// THE GREEN'S FUNCTION IS PER-FREQUENCY BUT NOT PER-MESH. Dcim.Fit costs ~0.2 s per frequency
// regardless of N (L8c's Tier 8 measured it), and a de-embedded solve touches THREE meshes at every
// frequency — the DUT and two calibration standards. Fitting once per frequency and sharing the
// model across all three is therefore worth 3× of a fixed cost that is 12% of a hero frequency
// point. The per-mesh part of the terms is only the ρ floor, which PlanarKernelTerms.With re-derives
// for free.
//
// This is the same shape as D6/R-fil-9's frequency-independent geometric core, one level up: the
// core is per-mesh and frequency-independent; the kernel is per-frequency and mesh-independent.
// Together they mean a sweep builds 3 cores (R-prt-11's counter) and fits 2 DCIM models per point,
// not 6 cores and 6 models.

using System.Diagnostics;
using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The two Green's-function kernels at one frequency — mesh-independent, so one fit serves the DUT
/// and every calibration standard at that frequency.
/// </summary>
public sealed record PlanarKernelPair(PlanarKernelTerms VectorPotential, PlanarKernelTerms Scalar)
{
    public static PlanarKernelPair Fit(GroundedSlab slab, double fHz,
                                       PlanarExtractionOrder order = PlanarExtractionOrder.Constant,
                                       DcimSettings? dcim = null)
    {
        var greens = new SpectralGreens(slab, fHz);
        return new PlanarKernelPair(
            PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.VectorPotential, dcim), order),
            PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.ScalarPotential,   dcim), order));
    }

    /// <summary>The same model, re-floored for one particular mesh's smallest cell.</summary>
    public PlanarKernelPair For(PlanarFillCores cores, PlanarExtractionOrder order) =>
        new(VectorPotential.With(order, cores.RhoFloorM), Scalar.With(order, cores.RhoFloorM));

    /// <summary>R-fil-8's ratio for this frequency against a given mesh.</summary>
    public double SmallestImageDepthOverCell(PlanarFillCores cores) =>
        cores.MinCellEdgeM > 0
            ? Math.Min(VectorPotential.SmallestImageDepth, Scalar.SmallestImageDepth) / cores.MinCellEdgeM
            : double.PositiveInfinity;
}

/// <summary>
/// <b>L9d/M1 — one frequency's kernel, whichever kind it is.</b>
///
/// <para>M1's obvious answer is to widen <see cref="PlanarKernelPair"/> in place, and it is wrong:
/// that type carries L8d's "fit once per frequency, share across the DUT and every standard"
/// decision, R-mlp-1 requires the one-level path to stay bit-identical, and the only way to promise
/// that is to leave the shipped path holding exactly the objects it already held. So this is a
/// discriminated wrapper, not a widening — a one-level problem carries L8d's pair and reaches
/// <c>PlanarFill.Fill</c>; anything else carries L9c's per-pairing set and reaches
/// <c>PlanarFill.FillMultiLevel</c>. There is exactly one place the choice is made
/// (<see cref="PlanarProblem.RequiresGeneralKernel"/>), and the driver hands the SAME instance to
/// the DUT and to every calibrator, so the shared fit cache does its job.</para>
/// </summary>
public sealed class PlanarFrequencyKernel
{
    /// <summary>L8's shipped pair, non-null exactly when this is the one-level path.</summary>
    public PlanarKernelPair? Pair { get; }

    /// <summary>L9's per-pairing set, non-null exactly when this is the general path.</summary>
    public PlanarKernelSet? Set { get; }

    public bool IsGeneral => Set is not null;

    private PlanarFrequencyKernel(PlanarKernelPair pair) => Pair = pair;
    private PlanarFrequencyKernel(PlanarKernelSet set)   => Set  = set;

    public static PlanarFrequencyKernel FromPair(PlanarKernelPair pair) => new(pair);
    public static PlanarFrequencyKernel FromSet(PlanarKernelSet set)    => new(set);

    /// <summary>
    /// The kernel one frequency of <paramref name="problem"/> needs. <b>The one-level branch is
    /// literally L8d's own call</b>, so R-mlp-1's bit-identity is a property of the code path rather
    /// than of a tolerance.
    /// </summary>
    public static PlanarFrequencyKernel Fit(
        PlanarProblem problem, double fHz,
        PlanarExtractionOrder order = PlanarExtractionOrder.Constant, DcimSettings? dcim = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.RequiresGeneralKernel
            ? new PlanarFrequencyKernel(
                  new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHz),
                                      order, 0.0, dcim))
            : new PlanarFrequencyKernel(PlanarKernelPair.Fit(problem.Slab, fHz, order, dcim));
    }
}

/// <summary>
/// One mesh, filled and factored once, with its geometric core kept so a sweep can reuse it (D6).
/// This is the object a DUT and each calibration standard each own exactly one of.
/// </summary>
public sealed class PlanarSolveContext
{
    public PlanarMesh                            Mesh  { get; }
    public PlanarFillCores                       Cores { get; }
    public IReadOnlyList<PlanarPortResolution>   Ports { get; }
    public PlanarFillSettings                    Settings { get; }

    /// <summary>
    /// <b>L9d — the z of every conductor level this mesh's cells sit on</b>, needed only on the
    /// general path. Null on the one-level path, where the kernel carries no height pairing at all.
    ///
    /// <para>A calibration STANDARD is always a single-level uniform line (D3), so its levels list
    /// has exactly one entry — the z of the level its port sits on — and its cells all carry
    /// <c>LayerIndex = 0</c>. That is what lets a standard share the DUT's own same-level fit.</para>
    /// </summary>
    public PlanarLevels? Levels { get; }

    public PlanarSolveContext(PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
                              PlanarFillSettings? settings = null, PlanarLevels? levels = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        Mesh     = mesh;
        Ports    = ports;
        Levels   = levels;
        Settings = settings ?? PlanarFillSettings.Default;
        PlanarSystem.GuardCeiling(mesh.Bases.Count);          // R-fil-10, before the core allocates
        Cores    = PlanarFill.BuildCores(mesh, Settings);
    }

    /// <summary>Fill, factor, excite — the raw admittance at one frequency.</summary>
    public PlanarPortSolution SolveAt(PlanarKernelPair kernel, double fHz)
    {
        var k = kernel.For(Cores, Settings.Order);
        var system = PlanarSystem.Build(Cores, k.VectorPotential, k.Scalar, 2.0 * Math.PI * fHz);
        return PlanarExcitation.Solve(system, Ports);
    }

    /// <summary>The same, for whichever kernel this frequency actually has (L9d/M1).</summary>
    public PlanarPortSolution SolveAt(PlanarFrequencyKernel kernel, double fHz)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (kernel.Pair is { } pair) return SolveAt(pair, fHz);

        if (Levels is null)
            throw new InvalidOperationException(
                "This mesh was built without a level list, so the general kernel has no height to " +
                "evaluate its Green's function at. Construct the PlanarSolveContext with the " +
                "PlanarLevels its cells sit on — for a calibration standard that is the single z of " +
                "the level its port is on (D3).");

        var system = PlanarSystem.BuildMultiLevel(Cores, kernel.Set!.For(Cores), Levels,
                                                  2.0 * Math.PI * fHz);
        return PlanarExcitation.Solve(system, Ports);
    }

    /// <summary>The raw s-parameters at the ports' own declared reference impedances.</summary>
    public Mat<Complex> RawScatteringAt(PlanarKernelPair kernel, double fHz) =>
        PlanarExcitation.RawScattering(SolveAt(kernel, fHz).Y,
                                       PlanarExcitation.ReferenceImpedances(Ports));

    /// <inheritdoc cref="RawScatteringAt(PlanarKernelPair, double)"/>
    public Mat<Complex> RawScatteringAt(PlanarFrequencyKernel kernel, double fHz) =>
        PlanarExcitation.RawScattering(SolveAt(kernel, fHz).Y,
                                       PlanarExcitation.ReferenceImpedances(Ports));
}

/// <summary>
/// One port's calibration, built once and stepped across a sweep.
///
/// <para><b>It is STATEFUL and must be stepped in increasing frequency order</b>, because both branch
/// resolutions are continuations: γ's 2π ambiguity is unwrapped from the previous point, and a₂₁'s
/// sign is carried from it. That is not an implementation convenience — a per-point independent
/// choice has no information with which to make either decision. Calling <see cref="At"/> out of
/// order is a programming error and produces a plausible, wrong phase.</para>
/// </summary>
public sealed class PlanarPortCalibrator
{
    private readonly GroundedSlab       _slab;
    private readonly PlanarSolveContext[] _standards;
    private readonly double[]           _deltas;
    private readonly double             _shortLength;

    private double   _cPerMetre = double.NaN;   // quasi-static: computed once, reused (D7)
    private double   _prevBeta  = double.NaN;
    private Complex? _prevA21;

    public IReadOnlyList<PlanarStandard> Standards { get; }

    /// <summary>How many meshes this calibrator owns — R-prt-11's counter counts these.</summary>
    public int MeshCount => _standards.Length;

    /// <summary>
    /// <b>L9d/D3 — the z of the level this port's standards live on</b>, or null on the one-level
    /// path. A standard is ALWAYS a single-level uniform line: a standard with a via in it is not a
    /// standard, because the calibration's whole model is "box + matched UNIFORM line + box" and a
    /// via is a discontinuity in the middle of the very thing that is assumed uniform.
    /// </summary>
    private readonly PlanarLevels? _standardLevels;

    public PlanarPortCalibrator(PlanarPortResolution port, GroundedSlab slab,
                                double fLoHz, double fHiHz,
                                PlanarCalibrationSettings? calibration = null,
                                PlanarFillSettings? fill = null,
                                double standardLevelZ = double.NaN)
    {
        _slab = slab;
        _standardLevels = double.IsNaN(standardLevelZ) ? null : new PlanarLevels([standardLevelZ]);

        var set = PlanarCalibration.BuildSet(port, slab, fLoHz, fHiHz, calibration);
        Standards = set;

        _standards   = new PlanarSolveContext[set.Length];
        for (int i = 0; i < set.Length; i++)
            _standards[i] = new PlanarSolveContext(set[i].Mesh, set[i].Ports, fill, _standardLevels);

        _shortLength = set[0].LengthM;
        _deltas      = new double[set.Length - 1];
        for (int i = 1; i < set.Length; i++) _deltas[i - 1] = set[i].LengthM - set[0].LengthM;
    }

    /// <summary>
    /// γ, the error box and Z_c at one frequency. Steps the branch state; call in increasing
    /// frequency order.
    /// </summary>
    public PlanarPortCalibration At(PlanarKernelPair kernel, double fHz, int portNumber = 1)
        => At(PlanarFrequencyKernel.FromPair(kernel), fHz, portNumber);

    /// <inheritdoc cref="At(PlanarKernelPair, double, int)"/>
    public PlanarPortCalibration At(PlanarFrequencyKernel kernel, double fHz, int portNumber = 1)
        => At(() => kernel, fHz, portNumber);

    /// <summary>
    /// <b>L9e/M1 — the same step, with the standards' RAW scattering cached and the kernel supplied
    /// lazily. This is what resolves the adaptive-sampling collision (§0.2 item 2).</b>
    ///
    /// <para>This object is stateful and must be stepped in increasing frequency order, because both
    /// branch resolutions are continuations. Every adaptive scheme picks its next point in the
    /// MIDDLE of the interval that disagreed most. The two facts collide, and the resolution is to
    /// separate what is expensive from what is order-dependent: the <b>solve</b> (fill + factor +
    /// back-substitution on every standard mesh, which is 64% of a de-embedded point) depends only
    /// on the frequency, while the <b>branch continuation</b> is a few lines of algebra that depend
    /// on the order. So the raw matrices are cached per frequency, and a caller that has just
    /// inserted a point mid-band calls <see cref="RestartBranchContinuation"/> and replays every
    /// solved frequency in sorted order — <b>at zero extra solves</b>, reproducing exactly what an
    /// in-order sweep would have produced.</para>
    ///
    /// <para><b>The alternative was measured against, not merely rejected on taste.</b> Making the
    /// branch resolution non-incremental — predicting βΔℓ from the pre-solve ε_eff instead of from
    /// the previous point — needs no cache and no replay, but L8d already measured that estimate
    /// running <b>15-20% low</b>, which is why its own calibration standards are designed to 60°
    /// rather than 90°. A 20% error in the expected phase is a coin flip on the 2π branch the moment
    /// a section passes half a wavelength, and a wrong branch is a smooth, plausible, wrong phase.
    /// The cache costs <c>O(standards × solved frequencies)</c> matrices of order P×P — kilobytes —
    /// and gives the identical answer to the sequential sweep by construction.</para>
    ///
    /// <param name="kernelFor">Called ONLY on a cache miss, so a replayed frequency costs no fit.</param>
    /// </summary>
    public PlanarPortCalibration At(Func<PlanarFrequencyKernel> kernelFor, double fHz, int portNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(kernelFor);

        if (!_rawCache.TryGetValue(fHz, out var raw))
        {
            var kernel = kernelFor();
            var s0 = _standards[0].RawScatteringAt(kernel, fHz);
            var sl = new Mat<Complex>[_deltas.Length];
            for (int i = 0; i < _deltas.Length; i++)
                sl[i] = _standards[i + 1].RawScatteringAt(kernel, fHz);
            raw = (s0, sl);
            _rawCache[fHz] = raw;
            SolveCount++;
        }

        var (sShort, sLong) = raw;

        double expect = double.IsNaN(_prevBeta)
            ? PlanarCalibration.EstimateBeta(_slab, fHz)
            : _prevBeta * (fHz / _prevF);

        var g = PlanarCalibration.GammaBest(sShort, sLong, _deltas, expect, out int pick);
        _prevBeta = g.Beta;
        _prevF    = fHz;

        var box = PlanarDeembed.SolveErrorBox(sShort, sLong[pick], _shortLength,
                                              _shortLength + _deltas[pick], g.Gamma, _prevA21);
        _prevA21 = box.A21;

        if (double.IsNaN(_cPerMetre))
            _cPerMetre = PlanarDeembed.CapacitancePerMetre(Standards[0], Standards[^1], _slab,
                                                           _standards[0].Settings);

        return new PlanarPortCalibration(
            portNumber, g, box,
            PlanarDeembed.CharacteristicImpedance(g.Gamma, _cPerMetre, fHz), _cPerMetre);
    }

    private double _prevF;

    /// <summary>
    /// L9e/M1 — the standards' RAW scattering per frequency. Keyed by the exact <c>double</c> the
    /// caller passed, which is safe because every frequency here came from the same array: a
    /// tolerance would silently merge two genuinely distinct closely-spaced sweep points.
    /// </summary>
    private readonly Dictionary<double, (Mat<Complex> Short, Mat<Complex>[] Long)> _rawCache = new();

    /// <summary>
    /// How many frequencies this calibrator has actually SOLVED, as against replayed — the counter
    /// that says the cache is doing its job. R-mom-11's pattern: assert the number, not a comment.
    /// </summary>
    public int SolveCount { get; private set; }

    /// <summary>
    /// <b>Drop the branch state so the next <see cref="At"/> starts a fresh continuation.</b> The
    /// per-metre capacitance is deliberately KEPT — it is a static, frequency-independent property
    /// of the two standards' geometry (D7), computed once and reused, and re-deriving it per replay
    /// would pay for two electrostatic solves per round to get the same number back.
    /// </summary>
    public void RestartBranchContinuation()
    {
        _prevBeta = double.NaN;
        _prevF    = 0;
        _prevA21  = null;
    }

    /// <summary>
    /// Whether two ports can share one calibration: same width, same transverse partition, same end
    /// run. Compared on cell SIZES rather than on positions, so two ends of one line match.
    ///
    /// <para><b>The tolerance is 1e-12 relative and not an equality, and the reason is arithmetic
    /// rather than physics.</b> The two ends of a uniform line have bit-identical CELLS, but their
    /// run lengths are computed by different subtractions — <c>g[1]−g[0]</c> at one end,
    /// <c>g[n]−g[n−1]</c> at the other — and those differ in the last bit. Demanding exact equality
    /// silently stopped the two ports of a plain microstrip from sharing a calibration, doubling the
    /// standards built for no reason at all.</para>
    /// </summary>
    public static bool SameCrossSection(PlanarPortResolution a, PlanarPortResolution b, int endRunCells)
    {
        const double Tol = 1e-12;

        if (a.BasisCount != b.BasisCount) return false;
        if (a.TransverseLines.Count != b.TransverseLines.Count) return false;

        for (int i = 1; i < a.TransverseLines.Count; i++)
        {
            double da = a.TransverseLines[i] - a.TransverseLines[i - 1];
            double db = b.TransverseLines[i] - b.TransverseLines[i - 1];
            if (Math.Abs(da - db) > Tol * Math.Max(da, db)) return false;
        }

        int k = Math.Min(endRunCells, Math.Min(a.LongitudinalRunM.Count, b.LongitudinalRunM.Count));
        if (k < endRunCells) return false;                 // one feed is too short to reproduce
        for (int i = 0; i < k; i++)
            if (Math.Abs(a.LongitudinalRunM[i] - b.LongitudinalRunM[i])
                > Tol * Math.Max(a.LongitudinalRunM[i], b.LongitudinalRunM[i])) return false;

        return true;
    }
}

/// <summary>What one de-embedded frequency point cost and produced.</summary>
public sealed record PlanarFrequencyPoint(
    double                               FrequencyHz,
    Mat<Complex>                         S,
    Mat<Complex>                         RawS,
    IReadOnlyList<PlanarPortCalibration> Calibrations,
    double                               KernelFitMs,
    double                               DutMs,
    double                               CalibrationMs);

/// <summary>
/// A de-embedded sweep. <b><see cref="CoreFillCount"/> is R-prt-11's counter</b> and generalises
/// R-fil-9's: it counts the frequency-independent geometric cores built for the whole run, which is
/// one per MESH — the DUT plus every calibration standard — and must not grow with the sweep length.
/// </summary>
public sealed class PlanarSolveResult
{
    public required IReadOnlyList<PlanarFrequencyPoint> Points        { get; init; }
    public required int                                 CoreFillCount { get; init; }
    public required int                                 UnknownCount  { get; init; }
    public required int                                 StandardCount { get; init; }
    public required double                              CoreBuildMs   { get; init; }
    public required IReadOnlyList<string>               Notes         { get; init; }

    /// <summary>
    /// L8e/D5 — the DUT's own basis currents for ONE driven port at ONE frequency, kept so the
    /// current-density heat map costs nothing extra: the sweep already fills, factors and excites
    /// this matrix at every point, and keeping one solution column is ~16·N bytes.
    ///
    /// <para>Null when nothing was requested. <b>Deliberately ONE column, not the whole set</b> —
    /// keeping every port at every frequency would be N × P × F complex, and a map that superposed
    /// them would be a map of nothing (D5).</para>
    /// </summary>
    public Vec<Complex>? CapturedCurrents   { get; init; }

    public double        CapturedFrequencyHz { get; init; }
    public int           CapturedPortNumber  { get; init; }

    /// <summary>
    /// <b>L9e/R-adf-2 — how many of the published points were actually SOLVED.</b> Equal to
    /// <c>Points.Count</c> when adaptive sampling is off. This is half of what makes an adaptively
    /// sampled sweep honest: a user who cannot tell whether a value was solved or modelled cannot
    /// tell whether it is credible.
    /// </summary>
    public int SolvedPointCount { get; init; }

    /// <summary>
    /// <b>The worst disagreement the refinement STOPPED at</b> — the largest |ΔS| between a freshly
    /// solved point and what the interpolant predicted there, over the probes that ended refinement
    /// (either by converging inside the tolerance or by running out of grid or budget). NaN when
    /// adaptive sampling is off. It is an ERROR, not a fit residual (D2).
    /// </summary>
    public double WorstAdaptiveDisagreement { get; init; } = double.NaN;

    /// <summary>Which frequencies were solved, ascending. Empty when adaptive sampling is off.</summary>
    public IReadOnlyList<double> SolvedFrequencies { get; init; } = [];

    public double TotalKernelMs      { get { double s = 0; foreach (var p in Points) s += p.KernelFitMs;    return s; } }
    public double TotalDutMs         { get { double s = 0; foreach (var p in Points) s += p.DutMs;          return s; } }
    public double TotalCalibrationMs { get { double s = 0; foreach (var p in Points) s += p.CalibrationMs;  return s; } }
    public double TotalMs => CoreBuildMs + TotalKernelMs + TotalDutMs + TotalCalibrationMs;
}

/// <param name="CurrentDensityPortNumber">
/// L8e/D5 — which port's excitation to keep basis currents for, so a heat map can be built without a
/// second solve. 0 keeps none. <b>One port, because a map that superposes every port is a map of
/// nothing</b>; the port NUMBER rather than an index, so it means the same thing the s-parameter
/// matrix does.
/// </param>
/// <param name="CurrentDensityFrequencyHz">Which swept point to keep them at — the nearest actual
/// point is used. 0 means the lowest swept frequency.</param>
/// <param name="Adaptive">
/// <b>L9e/D1 — adaptive frequency sampling, and it is a SETTING.</b> Null (the default) runs L8d's
/// own loop over exactly the frequencies it was given, so every measured number in §L8c, §L8d and
/// §L9d is reproducible at full precision by leaving it null — L9a's D5 precedent and R-mlp-1's:
/// the general capability is built alongside the shipped one and gated against it, never on top
/// of it.
/// </param>
public sealed record PlanarSolveSettings(
    PlanarFillSettings?        Fill        = null,
    PlanarCalibrationSettings? Calibration = null,
    DcimSettings?              Dcim        = null,
    bool                       Deembed     = true,
    int                        CurrentDensityPortNumber  = 0,
    double                     CurrentDensityFrequencyHz = 0,
    PlanarAdaptiveSettings?    Adaptive    = null)
{
    public static readonly PlanarSolveSettings Default = new();
}

/// <summary>
/// The whole thing: mesh + ports + slab + frequencies → de-embedded, renormalised s-parameters.
///
/// <para><b>No <c>DataSet</c>, no <c>.snp</c>, no kernel registry (D9).</b> This returns matrices and
/// diagnostics; wrapping them in the house result convention is L8e's, and inventing a result type
/// here would be inventing the one that ships.</para>
/// </summary>
public static class PlanarSolve
{
    /// <summary>
    /// L8d's own entry point, unchanged: a single conductor level on one grounded slab. Delegates to
    /// the problem-taking overload with the one-level problem this describes, so both paths share
    /// one implementation and the one-level one still fits through <see cref="PlanarKernelPair"/>.
    /// </summary>
    public static PlanarSolveResult Run(
        PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports, GroundedSlab slab,
        IReadOnlyList<double> freqsHz, PlanarSolveSettings? settings = null)
        => Run(new PlanarProblem([new PlanarConductorLayer("Metal", [], 0, 0)], slab, 0),
               mesh, ports, freqsHz, settings);

    /// <summary>
    /// <b>L9d/M1 — the same sweep for a problem of any level count.</b> Which kernel each frequency
    /// gets is <see cref="PlanarFrequencyKernel.Fit"/>'s single decision; the DUT and every
    /// calibration standard are handed the SAME kernel instance at each frequency, so L8d's "fit once
    /// per frequency, share across the DUT and every standard" survives unchanged (D7).
    /// </summary>
    public static PlanarSolveResult Run(
        PlanarProblem problem,
        PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
        IReadOnlyList<double> freqsHz, PlanarSolveSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        var slab = problem.Slab;
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(freqsHz);
        if (freqsHz.Count == 0) throw new ArgumentException("A sweep needs at least one frequency.", nameof(freqsHz));

        var st    = settings ?? PlanarSolveSettings.Default;
        var notes = new List<string>();

        // Ascending, because both branch resolutions are continuations (PlanarPortCalibrator).
        var freqs = freqsHz.ToArray();
        Array.Sort(freqs);
        double fLo = freqs[0], fHi = freqs[^1];

        bool general = problem.RequiresGeneralKernel;
        var  levels  = general ? PlanarLevels.From(problem) : null;

        // ── L9e/D8 — the low-frequency guard, asked of the LOWEST requested point ────────────────
        //
        // L8e recorded a 6 Hz point spending 50 s and ending in a raw framework exception with no
        // refusal attached, and left it because nothing could reach it from the EM panel. Adaptive
        // frequency sampling (M1) chooses its own points, so it can — and a scheme that picks a
        // frequency there must be stopped by a refusal rather than by an out-of-range array.
        double stackH = general ? problem.EffectiveStack.TopZ : slab.HeightM;
        var lowFreq = Dcim.CanFitAtFrequency(
            2.0 * Math.PI * fLo / EmConstants.C0, stackH, st.Dcim);
        if (!lowFreq.Ok)
            throw new InvalidOperationException(
                $"The sweep's lowest frequency is {SurfaceMesher.Eng(fLo)}Hz. " + lowFreq.Reason);

        // ── L9d/§0.2 item 4 — G_A^zz's OWN range is a REFUSAL, not a note, and this is the caller
        //    that acts on the answer the fill has been asking for since L9c.
        //
        // R-prt-13's note above is deliberately a note: it is worded on L8a's STRICT relative measure
        // and L8c measured the SCALED error a fill actually experiences at ≤ 5.4e-3 out to ρ/λ = 2.8.
        // This one is different in kind. ValidatedRhoOverLambdaAtHeights = 0.1 is an order of
        // magnitude tighter, was measured on the SCALED error (the one a fill does experience), and
        // G_A^zz reaches 14× the free-space kernel beyond it — so past that separation a two-level
        // solve produces a complete, smooth, plausible s-parameter set that is simply wrong. It binds
        // ONLY the ẑẑ block, so it is asked only when the mesh actually carries vertical bases: a
        // multi-level structure with no via is governed by the horizontal components, which L9c
        // measured at ≤ 1.9e-2 out to ρ/λ = 1 on every grounded stack.
        //
        // ── R-zz-1 — AND IT IS ASKED OF THE VERTICAL BASES, NOT OF THE MESH DIAGONAL ──────────
        //
        // The comment above already said the limit "binds ONLY the ẑẑ block", and it was still asked
        // of Diagonal(mesh). Those are not the same quantity: G_A^zz is consumed in exactly two
        // places (PlanarFill's `zi && zj` arm and the SingularPrismPart it calls), both between two
        // VERTICAL bases, so the largest ρ it is ever asked about is the extent of the via
        // FOOTPRINTS — not of the board. On §10.7's own 2.9 × 20 mm FR-4 hero at 10 GHz the mesh
        // diagonal is 0.67 λ and a single via's own footprint is ~0.02 λ: the old question refused a
        // whole class of board-scale structures on a separation the kernel is never asked about.
        //
        // Two vias genuinely far apart still refuse, and that is correct rather than a leftover —
        // there the fit really is asked about that ρ. Which is why the message has to name what the
        // separation is BETWEEN: "move the vias closer together" and "make the board smaller" are
        // different instructions and only one of them is the right one.
        if (general)
        {
            var (verdict, scoped) = VerticalRangeVerdict(problem, mesh, fHi, st.Fill);
            if (!verdict.Ok) throw new InvalidOperationException(verdict.Reason);
            notes.AddRange(scoped);
        }

        var sw    = Stopwatch.StartNew();
        var dut   = new PlanarSolveContext(mesh, ports, st.Fill, levels);
        double coreMs = sw.Elapsed.TotalMilliseconds;
        int    cores  = 1;

        // ── R-prt-2/3: what the ports resolved to, and whether their feeds are clear ─────────────
        foreach (var p in ports)
        {
            notes.Add(p.Describe());
            string? warn = PlanarPorts.CheckFeedClearance(
                mesh, p, (st.Calibration ?? PlanarCalibrationSettings.Default).EndRunHeights * slab.HeightM);
            if (warn is not null) notes.Add(warn);
        }

        // ── One calibrator per distinct port cross-section, shared where they match (D4) ─────────
        var calibrators = new List<PlanarPortCalibrator>();
        var byPort      = new int[ports.Count];
        int standards   = 0;

        if (st.Deembed)
        {
            var owners = new List<PlanarPortResolution>();
            for (int i = 0; i < ports.Count; i++)
            {
                int k = PlanarCalibration.EndRunCellsFor(ports[i], slab, st.Calibration);
                int found = -1;
                for (int j = 0; j < owners.Count; j++)
                    if (PlanarPortCalibrator.SameCrossSection(owners[j], ports[i], k)) { found = j; break; }

                if (found < 0)
                {
                    // ── L9d/D3 — a standard is a SINGLE-LEVEL uniform line on the port's own level,
                    //    and Z_c's quasi-static C_pul is what bounds where that is legitimate.
                    //
                    // PlanarDeembed differences the two standards' STATIC capacitances, and the only
                    // static Green's function this repository has is an electrostatic image series
                    // over a GROUNDED SLAB (PlanarKernelTerms.StaticScalar). That is the right
                    // electrostatic problem for a line ON the slab's own top surface and the wrong
                    // one for a line buried inside the stack — where the return path, the image
                    // depths and the whole series change. The de-embedded S is REFERENCED to Z_c, so
                    // a wrong C_pul is not a diagnostic inaccuracy: it renormalises every published
                    // s-parameter. Refused by name rather than reported.
                    if (general && !problem.LevelIsOnSlabTop(ports[i].LayerIndex))
                        throw new InvalidOperationException(
                            $"Port {ports[i].Number} sits on conductor level {ports[i].LayerIndex} at " +
                            $"z = {SurfaceMesher.Eng(problem.LevelZ(ports[i].LayerIndex))}m, which is not " +
                            $"the top surface of the grounded slab ({SurfaceMesher.Eng(slab.HeightM)}m). " +
                            "De-embedding references the answer to the line's own Z_c = γ/(jωC_pul), " +
                            "and C_pul comes from differencing two electrostatic solves that use an " +
                            "IMAGE SERIES over the slab — correct for a line on the slab's top surface, " +
                            "and not the right electrostatic problem for a level buried inside the " +
                            "stack. Feeding it anyway would renormalise every published s-parameter by " +
                            "the wrong reference. A buried-level port needs a static Green's function " +
                            "at INTERIOR heights — LayeredStaticGreens is referenced to the top " +
                            "half-space and refuses one by name, and nothing else in this repository " +
                            "provides it (see src/Engine/Mom/CLAUDE.md §L9c for what building it " +
                            "would take). Bring the feed out on the level that sits " +
                            "on the slab, or turn de-embedding off and read the raw solve.");

                    sw.Restart();
                    var cal = new PlanarPortCalibrator(
                        ports[i], slab, fLo, fHi, st.Calibration, st.Fill,
                        standardLevelZ: general ? problem.LevelZ(ports[i].LayerIndex) : double.NaN);
                    coreMs += sw.Elapsed.TotalMilliseconds;
                    cores  += cal.MeshCount;
                    standards += cal.MeshCount;
                    owners.Add(ports[i]);
                    calibrators.Add(cal);
                    found = calibrators.Count - 1;
                }
                byPort[i] = found;
            }

            // The standards are NOT free and their size is not obvious from the DUT's, so it is
            // reported rather than left to be discovered from a stopwatch.
            var sizes = new List<int>();
            foreach (var cal in calibrators)
                foreach (var s in cal.Standards) sizes.Add(s.Mesh.Bases.Count);
            int totalN = 0;
            foreach (int n in sizes) totalN += n;

            notes.Add($"De-embedding costs {calibrators.Count} calibration(s) over {ports.Count} port(s), " +
                      $"{standards} standard mesh(es) of N = {string.Join(" / ", sizes)} against the DUT's " +
                      $"N = {mesh.Bases.Count} — {(double)totalN / Math.Max(mesh.Bases.Count, 1):F2}× the " +
                      "DUT's unknowns, solved at every frequency alongside it.");

            if (calibrators.Count < ports.Count)
                notes.Add($"{ports.Count} port(s) share {calibrators.Count} calibration(s), because their " +
                          "cross-sections and port cells are identical — the standards are solved once each.");

            if (general) notes.Add(GeneralStackCalibrationNote(problem, ports));
        }
        else notes.Add("De-embedding is OFF: these s-parameters include the port discontinuity and are " +
                       "NOT the structure's response. This path exists for diagnostics only.");

        // ── R-prt-13: the DCIM validated range, decided rather than left unwired ─────────────────
        notes.Add(ValidatedRangeNote(mesh, slab, fHi));

        var z0 = PlanarExcitation.ReferenceImpedances(ports);
        var points = new List<PlanarFrequencyPoint>(freqs.Length);
        var flaggedBand = new List<double>();

        // D5's capture: the ONE frequency and ONE port whose basis currents the heat map needs.
        int capturePort = -1;
        if (st.CurrentDensityPortNumber > 0)
            for (int i = 0; i < ports.Count; i++)
                if (ports[i].Number == st.CurrentDensityPortNumber) { capturePort = i; break; }

        double captureF = st.CurrentDensityFrequencyHz > 0 ? st.CurrentDensityFrequencyHz : freqs[0];
        int    captureAt = -1;
        if (capturePort >= 0)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < freqs.Length; i++)
            {
                double d = Math.Abs(freqs[i] - captureF);
                if (d < best) { best = d; captureAt = i; }
            }
        }

        Vec<Complex>? captured = null;
        double capturedF = 0;

        // ── One frequency's raw DUT solve, lifted out of the loop so the adaptive driver below
        //    reaches EXACTLY the same arithmetic. R-adf-1's bit-identity when adaptive is off is a
        //    property of this being one implementation, not of two that agree.
        (PlanarFrequencyKernel Kernel, Mat<Complex> Raw, Vec<Complex>[] Currents, double KernelMs, double DutMs)
        SolveRawAt(double f)
        {
            sw.Restart();
            var k = PlanarFrequencyKernel.Fit(
                problem, f, (st.Fill ?? PlanarFillSettings.Default).Order, st.Dcim);
            double kMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            var sol = dut.SolveAt(k, f);
            var r = PlanarExcitation.RawScattering(sol.Y, z0);
            double dMs = sw.Elapsed.TotalMilliseconds;

            return (k, r, sol.Currents.ToArray(), kMs, dMs);
        }

        // ── The de-embedding half, likewise shared. `kernelFor` is lazy because a REPLAY (adaptive
        //    only) hits the calibrator's raw cache and needs no kernel at all.
        (Mat<Complex> S, List<PlanarPortCalibration> Cals, double CalMs)
        DeembedAt(double f, Mat<Complex> raw, Func<PlanarFrequencyKernel> kernelFor)
        {
            sw.Restart();
            Mat<Complex> s = raw;
            var cals = new List<PlanarPortCalibration>(ports.Count);

            if (st.Deembed)
            {
                var perCal = new PlanarPortCalibration[calibrators.Count];
                for (int j = 0; j < calibrators.Count; j++) perCal[j] = calibrators[j].At(kernelFor, f);

                var boxes = new PlanarErrorBox[ports.Count];
                var zc    = new Complex[ports.Count];
                for (int i = 0; i < ports.Count; i++)
                {
                    var c = perCal[byPort[i]];
                    cals.Add(c with { PortNumber = ports[i].Number });
                    boxes[i] = c.Box;
                    zc[i]    = c.Zc;
                    if (!c.Gamma.Usable) flaggedBand.Add(f);
                }

                s = PlanarDeembed.Renormalise(PlanarDeembed.Apply(raw, boxes), zc, z0);
            }
            return (s, cals, sw.Elapsed.TotalMilliseconds);
        }

        int    solvedCount = freqs.Length;
        double worstAdaptive = double.NaN;
        var    solvedList = Array.Empty<double>();

        if (st.Adaptive is null)
        {
            // L8d's own loop, untouched.
            foreach (double f in freqs)
            {
                var (kernel, raw, currents, kernelMs, dutMs) = SolveRawAt(f);

                if (capturePort >= 0 && points.Count == captureAt)
                {
                    captured  = currents[capturePort];
                    capturedF = f;
                }

                var (s, cals, calMs) = DeembedAt(f, raw, () => kernel);
                points.Add(new PlanarFrequencyPoint(f, s, raw, cals, kernelMs, dutMs, calMs));
            }
        }
        else
        {
            var ad = st.Adaptive;
            int budget = Math.Min(Math.Max(ad.MaxSolves, 2), freqs.Length);

            var rawByIndex   = new Dictionary<int, Mat<Complex>>();
            var kernelByIndex = new Dictionary<int, PlanarFrequencyKernel>();
            var timeByIndex  = new Dictionary<int, (double K, double D)>();
            var currentsByIndex = new Dictionary<int, Vec<Complex>[]>();
            var solved = new SortedSet<int>();

            void Solve(int i)
            {
                if (!solved.Add(i)) return;
                var r = SolveRawAt(freqs[i]);
                rawByIndex[i]      = r.Raw;
                kernelByIndex[i]   = r.Kernel;
                timeByIndex[i]     = (r.KernelMs, r.DutMs);
                currentsByIndex[i] = r.Currents;
            }

            // R-adf-3 — the calibration is REPLAYED in ascending frequency order from a fresh branch
            // state after every insertion, so the answer never depends on the order the adaptive
            // scheme happened to discover the points in. No solve is repeated: the calibrator's own
            // cache serves every replayed frequency.
            Dictionary<int, (Mat<Complex> S, List<PlanarPortCalibration> Cals, double CalMs)> Replay()
            {
                foreach (var c in calibrators) c.RestartBranchContinuation();
                flaggedBand.Clear();
                var outp = new Dictionary<int, (Mat<Complex>, List<PlanarPortCalibration>, double)>();
                foreach (int i in solved)
                    outp[i] = DeembedAt(freqs[i], rawByIndex[i], () => kernelByIndex[i]);
                return outp;
            }

            foreach (int i in PlanarAdaptiveSweep.SeedIndices(freqs.Length, ad.InitialPoints)) Solve(i);
            var byIndex = Replay();

            var work = new List<(int Lo, int Hi)>();
            {
                var seedList = solved.ToList();
                for (int i = 0; i + 1 < seedList.Count; i++) work.Add((seedList[i], seedList[i + 1]));
            }

            double worstStopped = 0;
            while (work.Count > 0 && solved.Count < budget)
            {
                var nodes  = solved.Select(i => freqs[i]).ToList();
                var values = solved.Select(i => byIndex[i].S).ToList();

                // Ascending interval order is the deterministic tie-break R-adf-3 asks for.
                var probes = new List<(int Lo, int Hi, int Mid, Mat<Complex> Predicted)>();
                foreach (var (lo, hi) in work)
                {
                    if (hi - lo < 2) continue;
                    int mid = (lo + hi) / 2;
                    if (solved.Contains(mid)) continue;
                    probes.Add((lo, hi, mid,
                                PlanarAdaptiveSweep.PredictAt(nodes, values, freqs[mid], ad.Interpolant)));
                }
                if (probes.Count == 0) break;

                var taken = new List<(int Lo, int Hi, int Mid, Mat<Complex> Predicted)>();
                foreach (var p in probes)
                {
                    if (solved.Count >= budget) break;
                    Solve(p.Mid);
                    taken.Add(p);
                }
                byIndex = Replay();

                var next = new List<(int, int)>();
                foreach (var p in taken)
                {
                    double err = PlanarAdaptiveSweep.WorstAbsDiff(byIndex[p.Mid].S, p.Predicted);
                    bool canSplit = (p.Mid - p.Lo >= 2 || p.Hi - p.Mid >= 2) && solved.Count < budget;

                    if (err > ad.Tolerance && canSplit)
                    {
                        if (p.Mid - p.Lo >= 2) next.Add((p.Lo, p.Mid));
                        if (p.Hi - p.Mid >= 2) next.Add((p.Mid, p.Hi));
                    }
                    else
                    {
                        // This interval stopped here — either converged, or out of grid or budget.
                        worstStopped = Math.Max(worstStopped, err);
                    }
                }
                work = next;
            }

            solvedCount   = solved.Count;
            worstAdaptive = worstStopped;
            solvedList    = solved.Select(i => freqs[i]).ToArray();

            // ── Publish on the USER'S grid (R-adf-2). A solved point carries its own solved matrix
            //    byte for byte; everything else is the interpolant's value.
            var nodeF   = solvedList;
            var nodeS   = solved.Select(i => byIndex[i].S).ToArray();
            var nodeRaw = solved.Select(i => rawByIndex[i]).ToArray();
            var modelS   = PlanarAdaptiveSweep.Model(nodeF, nodeS,   freqs, ad.Interpolant);
            var modelRaw = PlanarAdaptiveSweep.Model(nodeF, nodeRaw, freqs, ad.Interpolant);

            var solvedIdx = solved.ToList();
            for (int i = 0; i < freqs.Length; i++)
            {
                bool isSolved = solved.Contains(i);

                // The per-port calibration is a DIAGNOSTIC of a solved point; it is carried from the
                // nearest solved frequency rather than interpolated, because interpolating γ across
                // its own 2π branch is a second modelling claim this does not need to make.
                int near = solvedIdx[0];
                foreach (int j in solvedIdx)
                    if (Math.Abs(freqs[j] - freqs[i]) < Math.Abs(freqs[near] - freqs[i])) near = j;

                var (kMs, dMs) = isSolved ? timeByIndex[i] : (0.0, 0.0);
                points.Add(new PlanarFrequencyPoint(
                    freqs[i], modelS[i], modelRaw[i], byIndex[near].Cals,
                    kMs, dMs, isSolved ? byIndex[i].CalMs : 0.0));
            }

            if (capturePort >= 0 && captureAt >= 0)
            {
                int near = solvedIdx[0];
                foreach (int j in solvedIdx)
                    if (Math.Abs(freqs[j] - freqs[captureAt]) < Math.Abs(freqs[near] - freqs[captureAt]))
                        near = j;
                captured  = currentsByIndex[near][capturePort];
                capturedF = freqs[near];
            }

            notes.Add($"Adaptive frequency sampling: {solved.Count} of {freqs.Length} point(s) were " +
                      $"SOLVED; the rest are modelled by a " +
                      $"{(ad.Interpolant == PlanarInterpolant.Rational ? "barycentric rational" : "complex cubic spline")} " +
                      $"interpolant through them. The worst disagreement refinement stopped at is " +
                      $"|ΔS| = {worstStopped:G3} against a tolerance of {ad.Tolerance:G3}" +
                      (solved.Count >= budget ? ", and the solve budget was reached before every " +
                       "interval converged — treat the modelled points with that in mind." : ".") +
                      " Every published frequency is the one you asked for; a solved point carries " +
                      "the solver's own matrix exactly, and its per-port calibration diagnostics are " +
                      "carried from the nearest solved frequency rather than interpolated.");
        }

        if (flaggedBand.Count > 0)
            notes.Add($"{flaggedBand.Count} of {freqs.Length} frequency point(s) fall outside the " +
                      $"[{PlanarCalibrationSettings.UsableLoDegrees:F0}°, " +
                      $"{PlanarCalibrationSettings.UsableHiDegrees:F0}°] electrical length the two-line " +
                      $"calibration is well conditioned over — first at {SurfaceMesher.Eng(flaggedBand[0])}Hz. " +
                      "Narrow the sweep, or accept that those points carry a larger de-embedding error.");

        return new PlanarSolveResult
        {
            Points        = points,
            CoreFillCount = cores,
            UnknownCount  = mesh.Bases.Count,
            StandardCount = standards,
            CoreBuildMs   = coreMs,
            Notes         = notes,
            CapturedCurrents    = captured,
            CapturedFrequencyHz = capturedF,
            CapturedPortNumber  = captured is null ? 0 : st.CurrentDensityPortNumber,
            SolvedPointCount          = solvedCount,
            WorstAdaptiveDisagreement = worstAdaptive,
            SolvedFrequencies         = solvedList,
        };
    }

    /// <summary>
    /// R-prt-13 — <c>Dcim.WithinValidatedRange</c> exists, is tested, and was called from nowhere.
    /// This is where it becomes a decision instead of an oversight.
    ///
    /// <para><b>It is surfaced as a NOTE, not as a refusal, and the reason is measured rather than
    /// convenient.</b> The function is worded on L8a's STRICT relative measure, which is the right
    /// instrument for a pointwise kernel query and the wrong one for a matrix fill: L8c's Tier 2
    /// measured the entries a real mesh actually produces and found the SCALED error — the one a fill
    /// experiences — at ≤ 5.4e-3 on both starters at 2/10/20 GHz, inside L8a's own kernel budget. A
    /// per-entry refusal on the strict measure would refuse §10.7's own hero, whose far ends sit at
    /// ρ/λ ≈ 2.4 at 20 GHz, for an error the fill does not experience. So the extent is reported and
    /// the user is told what it means.</para>
    /// </summary>
    private static string ValidatedRangeNote(PlanarMesh mesh, GroundedSlab slab, double fHiHz)
    {
        var (x0, y0, x1, y1) = Extent(mesh);
        double diag   = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        double lambda = EmConstants.C0 / (fHiHz * Math.Sqrt(Math.Max(1.0, slab.Material.EpsR)));
        double ratio  = lambda > 0 ? diag / lambda : 0;

        var verdict = Dcim.WithinValidatedRange(GreensKernel.ScalarPotential, ratio);
        if (verdict.Ok)
            return $"Widest separation in this mesh is ρ/λ = {ratio:G3} at {SurfaceMesher.Eng(fHiHz)}Hz, " +
                   "inside the range the layered Green's function was validated over on the strict " +
                   "relative measure.";

        return $"Widest separation in this mesh is ρ/λ = {ratio:G3} at {SurfaceMesher.Eng(fHiHz)}Hz, " +
               "past where the layered Green's function's STRICT relative error was validated. That " +
               "measure is not the one a matrix fill experiences: the far entries sit in G_q's own " +
               "cancellation zone, where a relative error says more about the zero than about the " +
               "method, and the scaled error a fill actually sees was measured at ≤ 5.4e-3 there. " +
               "This is a note, not a refusal, and it is the reason it is a note.";
    }

    /// <summary>
    /// D3's two standing caveats for a general stack, stated once and rather than discovered: the
    /// standards are single-level uniform lines (a standard with a via in it is not a standard), and
    /// C_pul neglects everything above the port's own level.
    /// </summary>
    private static string GeneralStackCalibrationNote(
        PlanarProblem problem, IReadOnlyList<PlanarPortResolution> ports)
    {
        var stack = problem.EffectiveStack;
        double zPort = problem.LevelZ(ports[0].LayerIndex);

        var above = new List<string>();
        for (int i = 0; i < stack.Layers.Count; i++)
            if (stack.InterfaceZ[i] >= zPort - 1e-15)
                above.Add($"{SurfaceMesher.Eng(stack.Layers[i].ThicknessM)}m of " +
                          $"εᵣ = {stack.Layers[i].Material.EpsR:G4}");

        return "The calibration standards are SINGLE-LEVEL uniform lines on the port's own level — a " +
               "standard with a via in it is not a standard, because the two-line algebra models the " +
               "section between the reference planes as a uniform matched line and a via is a " +
               "discontinuity in the middle of exactly that. And Z_c's C_pul is an electrostatic " +
               "image series over the grounded slab alone, so it neglects " +
               (above.Count == 0
                   ? "nothing — there is no dielectric above the port's level."
                   : string.Join(" plus ", above) + " above the port's level, treating it as free " +
                     "space. That is a limitation of the γ-and-C route used to REPORT Z_c, and it " +
                     "renormalises the published S; the de-embedding's own accuracy is separate and " +
                     "is what the residual measures.");
    }

    /// <summary>Whether any basis is a via basis — R-via-5 makes them the tail of the vector, so
    /// this is a property of the mesh and not a per-entry question.</summary>
    private static bool HasVerticalBasis(PlanarMesh mesh)
    {
        foreach (var b in mesh.Bases)
            if (b.Direction == PlanarBasisDirection.Z) return true;
        return false;
    }

    /// <summary>The widest separation this mesh can ask the kernel about — its own bounding-box
    /// diagonal. Public because it is what a caller compares the ẑẑ-scoped extent against.</summary>
    public static double Diagonal(PlanarMesh mesh)
    {
        var (x0, y0, x1, y1) = Extent(mesh);
        return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
    }

    /// <summary>
    /// <b>R-zz-1 — G_A^zz's range verdict, asked of the VIA FOOTPRINTS rather than of the mesh.</b>
    ///
    /// <para>Extracted from <see cref="Run"/> so it is reachable without paying for a solve: a
    /// board-scale two-level structure is ~1,140 unknowns and a de-embedded point on one is minutes,
    /// so a test that had to run it could not gate the ACCEPTED case at all — only the refused one.
    /// This is the decision, composed exactly as <see cref="Run"/> composes it, and <see cref="Run"/>
    /// calls it rather than repeating it. Public for the same reason <c>PlanarKernel.CanSolve</c> is:
    /// a pre-flight verdict is worth having before committing to a sweep.</para>
    /// </summary>
    public static (EmSuitability Verdict, List<string> Notes) VerticalRangeVerdict(
        PlanarProblem problem, PlanarMesh mesh, double fHiHz, PlanarFillSettings? fill = null)
    {
        var notes  = new List<string>();
        double lam = EmConstants.C0 / fHiHz;

        // ── The ẑẑ block, and ONLY it ─────────────────────────────────────────────────────────
        //
        // The comment at the call site already said the limit "binds ONLY the ẑẑ block", and it was
        // still asked of Diagonal(mesh). Those are not the same quantity: G_A^zz has exactly two
        // consumers (PlanarFill's `zi && zj` arm and the SingularPrismPart it calls), both between
        // two VERTICAL bases, so the largest ρ it is ever asked about is the extent of the via
        // FOOTPRINTS — not of the board. On §10.7's own 2.9 × 20 mm FR-4 hero at 10 GHz the mesh
        // diagonal is 0.67 λ while a single via's own footprint is ~0.02 λ: the old question refused
        // a whole class of board-scale structures on a separation the kernel is never asked about.
        //
        // Two vias genuinely far apart still refuse, and that is correct rather than a leftover —
        // there the fit really is asked about that ρ. Which is why the message names what the
        // separation is BETWEEN: "move the vias closer together" and "make the board smaller" are
        // different instructions and only one of them acts on this.
        if (HasVerticalBasis(mesh))
        {
            double extent = VerticalExtent(mesh);
            var range = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, fHiHz))
                            .WithinValidatedRange(extent);

            // ── M4 (R-zz-4/5) — the constant did NOT move, and the way past it is a DIFFERENT
            //    KERNEL rather than a wider claim about the same one.
            //
            // M1 measured every reachable DcimSettings knob and none of them closes this: three of
            // the five the brief names are structurally inert on the interior path (FitAtHeights
            // reads no branch-point setting — the interior sum rule is a theorem by inspection), and
            // the best reachable configuration is still 71× outside the envelope while being 23×
            // WORSE inside ρ/λ ≤ 0.1, where the kernel is used today. So ValidatedRhoOverLambdaAtHeights
            // stays exactly where L9c measured it, and DirectVerticalKernel replaces the FIT for this
            // one block with direct Sommerfeld integration — which is the oracle the limit was
            // measured against, and therefore has no such limit of its own.
            if (fill?.DirectVerticalKernel == true)
            {
                // NOT an early return: D2's note below is unconditional, and dropping it would
                // re-open exactly the "narrowing left something ungoverned" hole M0 closed.
                notes.Add(
                    $"G_A^zz spans ρ/λ = {extent / lam:G3} between the via footprints" +
                    (range.Ok ? ", inside the fit's own validated range" :
                                $", PAST the {Dcim.ValidatedRhoOverLambdaAtHeights} the FIT is " +
                                $"validated over") +
                    " — and this run has DirectVerticalKernel on, so the ẑẑ block takes its kernel " +
                    "from direct Sommerfeld integration rather than from the fit. That limit is a " +
                    "property of the fit and does not apply to the integrator it was measured " +
                    "against. This is the expensive path by construction.");
            }
            else if (!range.Ok)
                return (EmSuitability.No(
                    $"This structure's vertical (via) current spans {SurfaceMesher.Eng(extent)}m " +
                    $"between its most distant VIA FOOTPRINT cells, which at " +
                    $"{SurfaceMesher.Eng(fHiHz)}Hz is ρ/λ = {extent / lam:G3}. This is a separation " +
                    $"between VIAS, not the size of the board: the mesh itself is " +
                    $"{SurfaceMesher.Eng(Diagonal(mesh))}m across and that is NOT what is refused " +
                    $"here. Bringing the vias closer together, or lowering the sweep's top, acts on " +
                    $"this; shrinking the surrounding metal does not. Alternatively set " +
                    $"PlanarFillSettings.DirectVerticalKernel, which replaces the FIT with direct " +
                    $"Sommerfeld integration for this one block — accurate at any separation, and " +
                    $"far slower (see M2's own cost measurement). " + range.Reason), notes);

            else notes.Add(
                $"G_A^zz's range was checked over the via footprints ({SurfaceMesher.Eng(extent)}m, " +
                $"ρ/λ = {extent / lam:G3}) rather than over the whole mesh " +
                $"({SurfaceMesher.Eng(Diagonal(mesh))}m, ρ/λ = {Diagonal(mesh) / lam:G3}) — that " +
                $"kernel is only ever asked about pairs of vertical bases.");
        }

        // ── D2 — narrowing the question must not leave anything UNGOVERNED, and it exposed that it
        //    would have. Scoping G_A^zz to the via footprints leaves the interior pairings of
        //    G_A^xx, G_q and the MIXED component — whose ρ genuinely spans the mesh, since the mixed
        //    block couples a via to EVERY horizontal basis — checked by nothing at all.
        //
        //    They do not need a refusal and the NUMBER is what says so: L9c's Tier 5 measured them
        //    at ≤ 1.9e-2 of the free-space kernel out to ρ/λ = 1 on every grounded stack, which is
        //    L9b's own envelope for the top-half-space pairing. Past ρ/λ = 1 there is simply no
        //    measurement, and this is a NOTE rather than a refusal for exactly R-prt-13's reason:
        //    reporting "unmeasured" is honest, and refusing on it would be inventing a limit.
        double meshRho = Diagonal(mesh) / lam;
        notes.Add(meshRho <= Dcim.ValidatedRhoOverLambdaInteriorHorizontal
            ? $"The interior G_A^xx / G_q / mixed pairings span ρ/λ = {meshRho:G3}, inside the " +
              $"{Dcim.ValidatedRhoOverLambdaInteriorHorizontal} L9c's Tier 5 measured them over " +
              $"(≤ 1.9e-2 of the free-space kernel on every grounded stack)."
            : $"The interior G_A^xx / G_q / mixed pairings span ρ/λ = {meshRho:G3}, PAST the " +
              $"{Dcim.ValidatedRhoOverLambdaInteriorHorizontal} L9c's Tier 5 measured them over " +
              $"(≤ 1.9e-2 there). Nothing above that separation has been measured for these three " +
              $"components — this is a note rather than a refusal because 'unmeasured' is what it " +
              $"is, and refusing on it would be inventing a limit rather than reporting one.");

        return (EmSuitability.Yes, notes);
    }

    /// <summary>
    /// <b>R-zz-1 — the widest separation the ẑẑ block can ask about</b>: the bounding-box diagonal of
    /// the cells that carry a VERTICAL basis. <c>G_A^zz</c> has exactly two consumers, both in
    /// <c>PlanarFill</c>'s <c>zi &amp;&amp; zj</c> arm, so this is an upper bound on every ρ that
    /// kernel is ever evaluated at — and an EXACT one whenever the two extreme via cells are
    /// themselves a pair, which they always are (the arm computes every pair).
    /// </summary>
    public static double VerticalExtent(PlanarMesh mesh)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        bool any = false;

        foreach (var b in mesh.Bases)
        {
            if (b.Direction != PlanarBasisDirection.Z) continue;
            foreach (int ci in new[] { b.CellA, b.CellB })
            {
                var c = mesh.Cells[ci];
                if (c.XMin < x0) x0 = c.XMin;
                if (c.YMin < y0) y0 = c.YMin;
                if (c.XMax > x1) x1 = c.XMax;
                if (c.YMax > y1) y1 = c.YMax;
                any = true;
            }
        }

        return any ? Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) : 0.0;
    }

    private static (double, double, double, double) Extent(PlanarMesh mesh)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var c in mesh.Cells)
        {
            if (c.XMin < x0) x0 = c.XMin;
            if (c.YMin < y0) y0 = c.YMin;
            if (c.XMax > x1) x1 = c.XMax;
            if (c.YMax > y1) y1 = c.YMax;
        }
        return mesh.Cells.Count == 0 ? (0, 0, 0, 0) : (x0, y0, x1, y1);
    }
}
