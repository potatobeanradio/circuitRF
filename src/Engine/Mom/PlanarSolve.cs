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
using RfCore;

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
    public IReadOnlyList<PlanarPortResolution>   Ports { get; }
    public PlanarFillSettings                    Settings { get; }

    private readonly Lazy<PlanarFillCores> _cores;
    private readonly Lazy<PlanarAimGeometry>? _aimGeometry;

    /// <summary>
    /// D6's geometric core for this mesh — <b>P2/M4: built on first use, not in the constructor.</b>
    ///
    /// <para>A <see cref="PlanarPortCalibrator"/> owns one context per calibration standard of the
    /// band, and <c>NeededAt</c> fills exactly TWO of them at any frequency: the short line and the
    /// one long line the β prediction selects. Coring all of them up front built an O(m²) pair
    /// triangle for every separation the sweep would never touch — the largest standards are several
    /// times the DUT's own size, so on a wide band that was the single largest piece of memory in the
    /// run held for nothing. The build itself is unchanged and so is its answer; only WHEN it happens
    /// moved.</para>
    ///
    /// <para>The R17 ceiling is still checked in the constructor, eagerly: a refusal has to happen at
    /// setup (R-dcl-1), which is a decision about N and needs no cores to make.</para>
    /// </summary>
    public PlanarFillCores Cores => _cores.Value;

    /// <summary>Whether <see cref="Cores"/> has actually been built yet — M4's own counter, per
    /// context, beside the per-mesh one on <see cref="PlanarCoreBuildCounter"/>.</summary>
    public bool CoresBuilt => _cores.IsValueCreated;

    /// <summary>How long this mesh's core build took, or 0 while it has not happened. Summed into a
    /// run's reported <c>CoreBuildMs</c>, which before M4 was measured around the constructor.</summary>
    public double CoreBuildMs { get; private set; }

    /// <summary>
    /// <b>L9d — the z of every conductor level this mesh's cells sit on</b>, needed only on the
    /// general path. Null on the one-level path, where the kernel carries no height pairing at all.
    ///
    /// <para>A calibration STANDARD is always a single-level uniform line (D3), so its levels list
    /// has exactly one entry — the z of the level its port sits on — and its cells all carry
    /// <c>LayerIndex = 0</c>. That is what lets a standard share the DUT's own same-level fit.</para>
    /// </summary>
    public PlanarLevels? Levels { get; }

    /// <param name="slabHeightM">
    /// <b>P8 — required whenever <see cref="PlanarFillSettings.Aim"/> is set</b>, and ignored on the
    /// dense path (which is why it is optional rather than positional): the accelerator's near radius
    /// has a floor of 2h under it, and h is not derivable from a mesh. Omitting it on an accelerated
    /// context throws rather than quietly building the pre-P8 near field, whose bad case is a slow or
    /// non-converging solve rather than an error.
    /// </param>
    public PlanarSolveContext(PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
                              PlanarFillSettings? settings = null, PlanarLevels? levels = null,
                              double slabHeightM = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        Mesh     = mesh;
        Ports    = ports;
        Levels   = levels;
        Settings = settings ?? PlanarFillSettings.Default;

        // R-fil-10, before the core allocates. The accelerator holds no N×N anything, so the DENSE
        // ceiling is the wrong question to ask of it — brief-em-aim-ceiling.md answered what the
        // right one is (AcceleratedUnknownCeiling). P12: the multi-level exclusion is no longer
        // "the accelerator refuses this class anyway" — it does not, since P12 — it is an OPEN owner
        // decision, and SurfaceMesher.UsesAcceleratedCeiling is the one place it is stated so this
        // and the pre-solve mesh verdict cannot answer it differently (they did).
        bool accelerated = SurfaceMesher.UsesAcceleratedCeiling(
            Settings.Aim is not null, levels is not null);
        SurfaceMesher.GuardCeiling(mesh.Bases.Count, accelerated, mesh.Cells.Count);

        // P2/M4 — LazyThreadSafetyMode.ExecutionAndPublication (the default): a de-embedded run fans
        // the standards out across workers, so two of them can reach the same context at once and the
        // core must be built exactly once whichever wins.
        _cores = new Lazy<PlanarFillCores>(() =>
        {
            var sw = Stopwatch.StartNew();
            var built = Settings.Aim is null
                ? PlanarFill.BuildCores(mesh, Settings)
                : PlanarFill.BuildGeometryOnlyCores(mesh, Settings);
            CoreBuildMs = sw.Elapsed.TotalMilliseconds;
            return built;
        });

        // P6 — the accelerator's frequency-independent state, once per mesh, on the same lazy
        // footing as the cores it is built from (and for the same M4 reason: a standard the sweep
        // never selects must not pay for a projection and a near-field core pass either).
        if (Settings.Aim is { } aim)
        {
            if (!(slabHeightM > 0))
                throw new ArgumentOutOfRangeException(nameof(slabHeightM), slabHeightM,
                    "An ACCELERATED context needs the slab height: P8 floors the near radius at 2h " +
                    "and h cannot be read off a mesh. Pass the problem's own Slab.HeightM.");
            _aimGeometry = new Lazy<PlanarAimGeometry>(() =>
            {
                var sw = Stopwatch.StartNew();
                var built = PlanarAimGeometry.Build(Cores, slabHeightM, aim);
                AimGeometryBuildMs = sw.Elapsed.TotalMilliseconds;
                return built;
            });
        }
    }

    /// <summary>
    /// <b>P6 — the accelerator's per-mesh geometry</b>: stencils, near set, mirror index and the near
    /// pairs' singular cores, built on first use and shared by every frequency's
    /// <see cref="PlanarAimOperator"/>. Null on the dense path. Until P6 all of it was rebuilt inside
    /// every <see cref="SolveAt(PlanarKernelPair, double)"/>.
    /// </summary>
    public PlanarAimGeometry? AimGeometry => _aimGeometry?.Value;

    /// <summary>Whether <see cref="AimGeometry"/> has been built yet — the sweep-level counter's
    /// per-context form, beside <see cref="PlanarCoreBuildCounter.AimGeometryTotal"/>.</summary>
    public bool AimGeometryBuilt => _aimGeometry?.IsValueCreated ?? false;

    /// <summary>How long this mesh's AIM geometry took, or 0 while it has not been built.</summary>
    public double AimGeometryBuildMs { get; private set; }

    /// <summary>Fill, factor, excite — the raw admittance at one frequency.</summary>
    public PlanarPortSolution SolveAt(PlanarKernelPair kernel, double fHz)
    {
        var k = kernel.For(Cores, Settings.Order);
        double omega = 2.0 * Math.PI * fHz;

        if (_aimGeometry is not null)
        {
            LastAccelerator = PlanarAimOperator.Build(_aimGeometry.Value, k.VectorPotential, k.Scalar, omega);
            return PlanarExcitation.Solve(LastAccelerator, Ports);
        }

        var system = PlanarSystem.Build(Cores, k.VectorPotential, k.Scalar, omega);
        return PlanarExcitation.Solve(system, Ports);
    }

    /// <summary>
    /// <b>M5 — the accelerator the last <see cref="SolveAt(PlanarKernelPair, double)"/> built</b>, or
    /// null on the dense path. It carries <see cref="PlanarAimReport"/> and the iteration count, which
    /// are what the cost gates read; keeping it is how a measurement gets at them without the driver
    /// having to thread a diagnostics object through every call.
    /// </summary>
    public PlanarAimOperator? LastAccelerator { get; private set; }

    /// <summary>
    /// <b>P12 — the bordered accelerator the last general-kernel <see cref="SolveAt(PlanarFrequencyKernel,
    /// double)"/> built</b>, or null on the dense path. Carries <see cref="PlanarBorderedAimReport"/>
    /// and the iteration count, on <see cref="LastAccelerator"/>'s own terms.
    /// </summary>
    public PlanarBorderedAimOperator? LastBorderedAccelerator { get; private set; }

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

        // P12 — the multi-level/via refusal that stood here is retired. The accelerator projects the
        // HORIZONTAL PREFIX per (level, level) pairing over one shared auxiliary grid and carries the
        // ẑ unknowns as a dense border; nothing about a via basis is projected, which is what the old
        // refusal was actually about. See PlanarAimBordered.cs's header.
        if (_aimGeometry is not null)
        {
            LastBorderedAccelerator = PlanarBorderedAimOperator.Build(
                _aimGeometry.Value, kernel.Set!.For(Cores), Levels, 2.0 * Math.PI * fHz);
            return PlanarExcitation.Solve(LastBorderedAccelerator, Ports);
        }

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

    private readonly InteriorStaticModel? _interiorModel;
    private double   _cPerMetre = double.NaN;   // quasi-static: computed once, reused (D7)
    private double   _prevBeta  = double.NaN;
    private Complex? _prevA21;

    public IReadOnlyList<PlanarStandard> Standards { get; }

    /// <summary>How many meshes this calibrator owns — R-prt-11's counter counts these.</summary>
    public int MeshCount => _standards.Length;

    /// <summary><b>P2/M4 — how many of those meshes have actually had their cores built.</b> Every one
    /// of them, before M4; since M4, only the ones some frequency selected, plus the shortest and the
    /// longest, which D7's static differencing needs whether or not they were ever solved.</summary>
    public int CoredMeshCount
    {
        get { int k = 0; foreach (var c in _standards) if (c.CoresBuilt) k++; return k; }
    }

    /// <summary>The core-build time of every standard that has been cored. Zero at construction — M4
    /// moved the build out of the constructor, so a run's reported core time has to be summed after
    /// the sweep rather than measured around the constructor call.</summary>
    public double CoreBuildMs
    {
        get { double ms = 0; foreach (var c in _standards) ms += c.CoreBuildMs; return ms; }
    }

    /// <summary>
    /// <b>L9d/D3 — the z of the level this port's standards live on</b>, or null on the one-level
    /// path. A standard is ALWAYS a single-level uniform line: a standard with a via in it is not a
    /// standard, because the calibration's whole model is "box + matched UNIFORM line + box" and a
    /// via is a discontinuity in the middle of the very thing that is assumed uniform.
    /// </summary>
    private readonly PlanarLevels? _standardLevels;

    /// <summary>
    /// <b>MIM-4 — the medium C_pul's electrostatics is taken from, when that medium is not the
    /// slab.</b> Null keeps this calibrator on the shipped one-slab image series, bit for bit, which
    /// is what every port on a genuine one-slab problem still gets (R-mlp-1). Non-null routes D7's
    /// static differencing through <see cref="InteriorStaticImages"/> at <c>_standardLevelZ</c>.
    /// <see cref="DescribedByTheSlab"/> makes the choice.
    /// </summary>
    private readonly LayerStack? _interiorStack;
    private readonly double      _standardLevelZ;

    /// <summary><b>The fitted interior model's own spectral residual</b>, or NaN on the one-slab
    /// path. Carried so a run can report the quality of the electrostatics its reference impedance
    /// rests on rather than leaving it to be assumed.</summary>
    public double InteriorFitResidual { get; } = double.NaN;

    /// <summary>
    /// <b>Whether the shipped one-slab image series IS this level's electrostatic problem.</b>
    ///
    /// <para>Being at <c>slab.HeightM</c> is NOT enough on its own and that is the whole point: a
    /// single level over a STRATIFIED sub-feed region sits at the top of its medium and at the
    /// slab's height, and the slab it is compared against is a series-capacitance average the
    /// extractor built to size a mesh with. Answering "yes" there would put a two-dielectric board's
    /// reference impedance on a one-dielectric series — plausibly, and wrongly. So the medium is
    /// compared structurally: one layer, of the slab's own material and height, PEC below, half-space
    /// above, and the level on its top surface.</para>
    /// </summary>
    private static bool DescribedByTheSlab(LayerStack stack, GroundedSlab slab, double levelZ)
    {
        if (stack.LayerCount != 1) return false;
        if (stack.Bottom.Kind != TerminationKind.Pec) return false;
        if (stack.Top.Kind != TerminationKind.HalfSpace) return false;

        double tol = 1e-12 * Math.Max(1.0, slab.HeightM);
        var layer = stack.Layers[0];
        if (Math.Abs(layer.ThicknessM - slab.HeightM) > tol) return false;
        if (!double.IsNaN(levelZ) && Math.Abs(levelZ - slab.HeightM) > tol) return false;

        var m = layer.Material;
        return Math.Abs(m.EpsR - slab.Material.EpsR) <= 1e-12 * Math.Max(1.0, slab.Material.EpsR)
            && Math.Abs(m.TanD - slab.Material.TanD) <= 1e-12
            && Math.Abs(m.MuR  - slab.Material.MuR)  <= 1e-12
            && Math.Abs(stack.Top.Material.EpsR - 1.0) <= 1e-12;
    }

    public PlanarPortCalibrator(PlanarPortResolution port, GroundedSlab slab,
                                double fLoHz, double fHiHz,
                                PlanarCalibrationSettings? calibration = null,
                                PlanarFillSettings? fill = null,
                                double standardLevelZ = double.NaN,
                                IReadOnlyList<PlanarStandard>? standards = null,
                                LayerStack? mediumStack = null)
    {
        _slab = slab;
        _standardLevels = double.IsNaN(standardLevelZ) ? null : new PlanarLevels([standardLevelZ]);
        _standardLevelZ = standardLevelZ;

        // The decision is made ONCE, here, and it is the only thing that separates the two C_pul
        // routes.
        _interiorStack = mediumStack is not null && !DescribedByTheSlab(mediumStack, slab, standardLevelZ)
                       ? mediumStack : null;
        if (_interiorStack is not null)
        {
            _interiorModel = InteriorStaticImages.FitScalar(_interiorStack, standardLevelZ, standardLevelZ);
            InteriorFitResidual = _interiorModel.Residual;
        }

        var set = standards is null
                ? PlanarCalibration.BuildSet(port, slab, fLoHz, fHiHz, calibration)
                : [.. standards];
        Standards = set;

        _standards   = new PlanarSolveContext[set.Length];
        for (int i = 0; i < set.Length; i++)
            _standards[i] = new PlanarSolveContext(set[i].Mesh, set[i].Ports, fill, _standardLevels,
                                                   slab.HeightM);

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

        if (PrepareAt(kernelFor, fHz) is { } work)
        {
            foreach (var solve in work.Solves) solve();
            work.Commit();
        }

        double expect = ExpectedBeta(fHz);
        int    pick   = PlanarCalibration.SelectSeparation(_deltas, expect);

        var slots  = _rawCache[fHz];
        var sShort = slots[0]!.Value;              // Mat<T> is a struct, so these are Nullable<Mat<T>>
        var sLong  = slots[pick + 1]!.Value;

        // Selected here rather than inside GammaBest, because PrepareAt has already solved exactly
        // these two meshes and no others — asking GammaBest to re-select would mean handing it an
        // array that is null everywhere except at `pick`. Same rule, same arithmetic, asked once.
        var g = PlanarCalibration.Gamma(sShort, sLong, _deltas[pick], expect * _deltas[pick]);
        _prevBeta = g.Beta;
        _prevF    = fHz;

        var box = PlanarDeembed.SolveErrorBox(sShort, sLong, _shortLength,
                                              _shortLength + _deltas[pick], g.Gamma, _prevA21);
        _prevA21 = box.A21;

        // ── P2/M3 — the two standards' cores are already built; do not build them a second time ──
        //
        // StaticCapacitance's own O(m²) core build was a duplicate of one this calibrator's contexts
        // already hold for the SAME mesh and the SAME fill settings. It is handed them instead.
        //
        // P2/M4's own note: the cores are lazy now, so this call is what BUILDS the longest
        // standard's if no frequency in the band ever selected it. That is correct rather than a
        // leak — the static differencing needs the two EXTREME lengths, not the one this frequency
        // solved — but it does mean the longest standard is cored on every de-embedded run.
        if (double.IsNaN(_cPerMetre))
            _cPerMetre = _interiorStack is { } medium
                ? PlanarDeembed.CapacitancePerMetre(Standards[0], Standards[^1], medium,
                                                    _standardLevelZ,
                                                    _standardLevelZ - medium.InterfaceZ[0],
                                                    _standards[0].Settings,
                                                    _standards[0].Cores,
                                                    _standards[^1].Cores,
                                                    _interiorModel)
                : PlanarDeembed.CapacitancePerMetre(Standards[0], Standards[^1], _slab,
                                                    _standards[0].Settings,
                                                    _standards[0].Cores,
                                                    _standards[^1].Cores);

        return new PlanarPortCalibration(
            portNumber, g, box,
            PlanarDeembed.CharacteristicImpedance(g.Gamma, _cPerMetre, fHz), _cPerMetre);
    }

    private double _prevF;

    /// <summary>
    /// <b>M2/R-emp-9 — the independent RAW solves this calibrator owes at one frequency, handed back
    /// as work items so the driver can schedule them ALONGSIDE the DUT's rather than after it.</b>
    ///
    /// <para>Null when the frequency is already cached, which is what a replay hits. Otherwise every
    /// <see cref="Solves"/> entry is a fill + factorisation + back-substitution on one standard mesh
    /// and they share nothing but the read-only kernel and their own read-only geometric cores;
    /// <see cref="Commit"/> installs the cache and steps the solve counter, and is the part that must
    /// run on ONE thread after they have all joined. Nothing order-dependent is in here — the branch
    /// continuation stays in <see cref="PlanarPortCalibrator.At"/>, which is the separation L9e's own
    /// M1 made so that a frequency could be solved out of order at all.</para>
    /// </summary>
    public sealed record PlanarCalibratorWork(IReadOnlyList<Action> Solves, Action Commit);

    /// <inheritdoc cref="PlanarCalibratorWork"/>
    /// <param name="kernelFor">Called ONLY on a cache miss, exactly as <see cref="At"/> calls it.</param>
    public PlanarCalibratorWork? PrepareAt(Func<PlanarFrequencyKernel> kernelFor, double fHz)
    {
        ArgumentNullException.ThrowIfNull(kernelFor);

        var want = NeededAt(fHz);
        if (_rawCache.TryGetValue(fHz, out var have)
            && want.All(i => have[i] is not null)) return null;

        var slots  = have ?? new Mat<Complex>?[_standards.Length];
        var todo   = want.Where(i => slots[i] is null).ToArray();
        var kernel = kernelFor();

        var solves = new Action[todo.Length];
        var built  = new Mat<Complex>[todo.Length];
        for (int j = 0; j < todo.Length; j++)
        {
            int at = todo[j], slot = j;
            solves[slot] = () => built[slot] = _standards[at].RawScatteringAt(kernel, fHz);
        }

        return new PlanarCalibratorWork(solves, () =>
        {
            for (int j = 0; j < todo.Length; j++) slots[todo[j]] = built[j];
            _rawCache[fHz] = slots;
            if (_solvedFrequencies.Add(fHz)) SolveCount++;
            StandardSolveCount += todo.Length;
        });
    }

    /// <summary>
    /// <b>The standards this frequency actually NEEDS: the short line, and the ONE long line the
    /// prediction selects.</b> Never all of them.
    ///
    /// <para><b>This is the whole of the calibration saving, and it is a pure bookkeeping change: the
    /// matrices no longer solved were never read.</b> <see cref="PlanarCalibration.GammaBest"/> reads
    /// <c>sShort</c> and <c>sLong[pick]</c> and nothing else, and <c>pick</c> is a function of the Δℓ
    /// set and the PREDICTED β alone — both known before any fill. Solving the rest and discarding
    /// them was the single largest avoidable cost in a de-embedded sweep: the separations are sized
    /// geometrically across the band, so at the top of a 1-20 GHz sweep the longest standard is
    /// several times the DUT's own unknown count and is thrown away, and at the bottom the short ones
    /// are.</para>
    ///
    /// <para>Deliberately NOT a narrowing of the standard SET — every separation is still built, so
    /// <see cref="MeshCount"/>, the engine's own "N standard mesh(es)" note and R-prt-11's counter are
    /// unchanged, and the per-frequency choice still ranges over all of them. What changed is only
    /// which of them get filled at each frequency.</para>
    /// </summary>
    private int[] NeededAt(double fHz) =>
        [0, 1 + PlanarCalibration.SelectSeparation(_deltas, ExpectedBeta(fHz))];

    /// <summary>
    /// How many standard meshes <see cref="PrepareAt"/> would fill at this frequency — 0 when it is
    /// fully cached. The progress stage's own denominator: counting <see cref="MeshCount"/> there
    /// would promise ticks that no longer happen and leave the bar permanently short.
    /// </summary>
    public int PlannedSolvesAt(double fHz)
    {
        var want = NeededAt(fHz);
        if (!_rawCache.TryGetValue(fHz, out var have)) return want.Length;
        return want.Count(i => have[i] is null);
    }

    /// <summary>The β this frequency's selection and branch continuation are predicted from — the
    /// previous point's measured β scaled by frequency, or the pre-solve estimate at the first.</summary>
    private double ExpectedBeta(double fHz) =>
        double.IsNaN(_prevBeta) ? PlanarCalibration.EstimateBeta(_slab, fHz)
                                : _prevBeta * (fHz / _prevF);

    /// <summary>
    /// L9e/M1 — the standards' RAW scattering per frequency, <b>one slot per standard, null where it
    /// was never needed</b>. Keyed by the exact <c>double</c> the caller passed, which is safe because
    /// every frequency here came from the same array: a tolerance would silently merge two genuinely
    /// distinct closely-spaced sweep points.
    /// </summary>
    private readonly Dictionary<double, Mat<Complex>?[]> _rawCache = new();

    private readonly HashSet<double> _solvedFrequencies = [];

    /// <summary>
    /// How many frequencies this calibrator has actually SOLVED, as against replayed — the counter
    /// that says the cache is doing its job. R-mom-11's pattern: assert the number, not a comment.
    ///
    /// <para>Counted per DISTINCT frequency, so a replay that re-predicts across a separation boundary
    /// and needs one further mesh at an already-visited point does not read as a second solve of that
    /// point. <see cref="StandardSolveCount"/> is where that extra mesh shows up.</para>
    /// </summary>
    public int SolveCount { get; private set; }

    /// <summary>
    /// How many standard MESHES have been filled, across every frequency — the honest work counter,
    /// and the one that says the selection is doing its job. Two per frequency on a fresh sweep
    /// however many separations the band asked for.
    /// </summary>
    public int StandardSolveCount { get; private set; }

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

        // ── RP-2c — TWO PORTS SHARE A STANDARD ONLY IF THEY SHARE THE WHOLE NEIGHBOURHOOD ────────
        //
        // A coplanar port's standard is built from its cross-section, so two ports whose signal
        // conductors match cell for cell can still need DIFFERENT standards: a different slot, a
        // different return width, or a return on the other side. Comparing only the driven run would
        // hand port 2 a standard built for port 1's slot — a complete, plausible calibration
        // referenced to the wrong line.
        if ((a.CrossSection is null) != (b.CrossSection is null)) return false;
        if (a.CrossSection is { } xa && b.CrossSection is { } xb)
        {
            if (xa.IsMetal.Count != xb.IsMetal.Count) return false;
            if (xa.PositiveLo != xb.PositiveLo || xa.PositiveHi != xb.PositiveHi) return false;
            if (xa.NegativeLo != xb.NegativeLo || xa.NegativeHi != xb.NegativeHi) return false;
            for (int i = 0; i < xa.IsMetal.Count; i++)
            {
                if (xa.IsMetal[i] != xb.IsMetal[i]) return false;
                double da = xa.Lines[i + 1] - xa.Lines[i];
                double db = xb.Lines[i + 1] - xb.Lines[i];
                if (Math.Abs(da - db) > Tol * Math.Max(da, db)) return false;
            }
        }

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

/// <summary>
/// What one de-embedded frequency point cost and produced.
///
/// <para><b>M2 — these are PER-SOLVE times and they no longer sum to wall clock.</b> The DUT and the
/// calibration standards are solved concurrently now (see <see cref="PlanarFanOut"/>), so
/// <see cref="DutMs"/> is the DUT solve's own elapsed time, <see cref="CalibrationMs"/> is the
/// standards' SUMMED elapsed time plus the de-embedding algebra, and the two overlap in real time by
/// however much the parallel budget allowed. Keeping them separate is what makes the split still
/// informative — L8d's own "the standards are 78% of it" is a statement about work, not about wall
/// clock — but a caller adding them up and calling it a duration will overstate it.</para>
/// </summary>
public sealed record PlanarFrequencyPoint(
    double                               FrequencyHz,
    Mat<Complex>                         S,
    Mat<Complex>                         RawS,
    IReadOnlyList<PlanarPortCalibration> Calibrations,
    double                               KernelFitMs,
    double                               DutMs,
    double                               CalibrationMs)
{
    /// <summary>
    /// <b>ANT-9 — this frequency was FOUND, not requested.</b> True only under the opt-in resonance
    /// search (<c>PlanarAdaptiveSettings.Search</c>); false for every point of the user's own grid
    /// and, with the search off, for every point there is.
    ///
    /// <para>An init-only member rather than a positional parameter on purpose: every existing
    /// construction site keeps compiling and keeps meaning exactly what it meant, which is half of
    /// why the search-off path is bit-identical by construction rather than by assertion.</para>
    /// </summary>
    public bool AddedBySearch { get; init; }
}

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
    /// <b>ANT-4 — every far-field pattern this sweep produced</b>, or null when none was asked for
    /// or the structure was refused one by name. One pattern per (requested frequency, port); the
    /// frequency axis carries what was ASKED for and solved, which is why it is a list rather than
    /// the sweep's own axis.
    /// </summary>
    public PlanarFarFieldSet? FarField { get; init; }

    /// <summary>
    /// <b>ANT-5 — every metric ANT-4's patterns imply</b>, or null when no pattern was produced. One
    /// report per (requested frequency, port), each carrying both the numbers and the REFUSALS, so a
    /// metric that cannot be computed is present with its reason rather than missing.
    /// </summary>
    public PlanarMetricSet? Metrics { get; init; }

    /// <summary>
    /// <b>ANT-6 — the polarization of every pattern this sweep produced</b>, or null when no pattern
    /// was produced. The axial ratio and the IEEE sense are always there; the Ludwig-3 co/cross pair
    /// is there when a reference angle was named or could be derived, and carries its own refusal
    /// when it could not (R-ant-9).
    /// </summary>
    public PlanarPolarizationSet? Polarization { get; init; }

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

    /// <summary>
    /// <b>ANT-9 — did refinement actually converge?</b> True when every interval that stopped did so
    /// INSIDE the tolerance; false when at least one ran out of grid or budget while still
    /// disagreeing. Null when adaptive sampling is off and the question does not arise.
    ///
    /// <para>This exists because a solved-point count is not a verdict. The measurement that opened
    /// ANT-9 solved 44 of 51 — 86 %, which reads like success — and missed its tolerance by a factor
    /// of twenty. The two facts have to be separable by a caller, not just by a reader of the
    /// prose.</para>
    /// </summary>
    public bool? AdaptiveConverged { get; init; }

    /// <summary>
    /// <b>ANT-9 — the frequencies the resonance search ADDED</b>, ascending, none of which the user
    /// asked for. Always empty unless <c>PlanarAdaptiveSettings.Search</c> was set. Each is also
    /// flagged on its own point (<see cref="PlanarFrequencyPoint.AddedBySearch"/>); this list is the
    /// same fact in the form a caller wants when it is drawing the sweep rather than walking it.
    /// </summary>
    public IReadOnlyList<double> AddedFrequencies { get; init; } = [];

    /// <summary>
    /// <b>ANT-9 — every resonance the search located</b>, ascending in frequency. Empty when the
    /// search was off, and ALSO empty when it ran and found none, which is an answer rather than an
    /// absence — <see cref="Notes"/> carries the sentence that says which of the two it was.
    /// </summary>
    public IReadOnlyList<PlanarResonance> Resonances { get; init; } = [];

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
/// <param name="MaxDegreeOfParallelism">
/// <b>M1 — the user's core cap, and ONE number drives both levels of parallelism.</b> Null means
/// automatic; a user setting 4 means four cores TOTAL, not four per level of nesting. The driver
/// materialises it as a single <see cref="PlanarParallelBudget"/> shared by every solve in flight
/// and by every fill row inside them (R-emp-10) — see <see cref="PlanarFillSettings.Budget"/>.
///
/// <para><b>1 means strictly sequential, in the order the work was created</b>, which is what makes
/// R-emp-13's "cap 1 and cap 8 produce bit-identical results" a statement about one implementation
/// rather than about two that agree.</para>
/// </param>
/// <param name="FarField">
/// <b>ANT-4 — the radiated pattern, and it is a SETTING that defaults to off.</b> Null computes
/// none, so every measured number in §L8c, §L8d and §L9d is reproducible by leaving it null and the
/// sweep's arithmetic is untouched. When it is set the pattern rides along with the solve the sweep
/// already pays for: the basis currents exist at every solved point already, and a pattern is an
/// exact O(N) sum over them with no second fill and no second factorisation.
/// </param>
public sealed record PlanarSolveSettings(
    PlanarFillSettings?        Fill        = null,
    PlanarCalibrationSettings? Calibration = null,
    DcimSettings?              Dcim        = null,
    bool                       Deembed     = true,
    int                        CurrentDensityPortNumber  = 0,
    double                     CurrentDensityFrequencyHz = 0,
    PlanarAdaptiveSettings?    Adaptive    = null,
    int?                       MaxDegreeOfParallelism = null,
    PlanarFarFieldSettings?    FarField    = null)
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
    /// <b>The error box of a port that has no error box</b> — a₁₁ = 0, a₂₂ = 0, a₂₁ = 1, i.e. a
    /// through. An internal delta gap takes this, which makes <see cref="PlanarDeembed.Apply"/>'s
    /// algebra the identity on that port's row and column while every de-embedded port beside it is
    /// peeled exactly as before. The residuals are zero because nothing was fitted: no standard was
    /// solved, no sign was chosen, and there is nothing for a consistency check to be about.
    /// </summary>
    private static readonly PlanarErrorBox IdentityBox =
        new(Complex.Zero, Complex.Zero, Complex.One, 0, 0);

    /// <summary>
    /// <b>MIM-4 — how bad the interior electrostatic fit may be before a de-embedded buried-level
    /// port is refused.</b> A relative spectral residual, so it is comparable across stacks.
    ///
    /// <para>Sized from what the fit actually achieves rather than from taste: 3e-12 on a one-slab
    /// stack, 2e-10 on a three-layer board, 2e-10 on the shipped thin-film MIM stack whose layers
    /// span three orders of magnitude — and the spatial function agrees with direct Hankel
    /// integration to 1e-8 at those residuals. 1e-6 is four decades of headroom above every stack
    /// measured, which makes this a guard against a fit that has genuinely failed rather than a
    /// tolerance anyone has to tune.</para>
    /// </summary>
    public const double InteriorCPulResidualCeiling = 1e-6;

    /// <summary>
    /// L8d's own entry point, unchanged: a single conductor level on one grounded slab. Delegates to
    /// the problem-taking overload with the one-level problem this describes, so both paths share
    /// one implementation and the one-level one still fits through <see cref="PlanarKernelPair"/>.
    /// </summary>
    public static PlanarSolveResult Run(
        PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports, GroundedSlab slab,
        IReadOnlyList<double> freqsHz, PlanarSolveSettings? settings = null,
        RunControl? control = null, SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
        => Run(new PlanarProblem([new PlanarConductorLayer("Metal", [], 0, 0)], slab, 0),
               mesh, ports, freqsHz, settings, control, lengthFormat: lengthFormat);

    /// <summary>
    /// <b>L9d/M1 — the same sweep for a problem of any level count.</b> Which kernel each frequency
    /// gets is <see cref="PlanarFrequencyKernel.Fit"/>'s single decision; the DUT and every
    /// calibration standard are handed the SAME kernel instance at each frequency, so L8d's "fit once
    /// per frequency, share across the DUT and every standard" survives unchanged (D7).
    /// </summary>
    /// <param name="control">Progress and cancellation, or null for neither. A full-wave point costs
    /// tens of seconds (L8d/L9d: 48 s and 71.9 s de-embedded at the shipping mesh), so this reports
    /// BOTH the point count and the sub-steps within the current point — a bar that moved once a
    /// minute would be indistinguishable from a hung run. Cancellation is checked at the same
    /// boundaries, which is the granularity <see cref="RunControl"/>'s own contract describes.</param>
    /// <param name="leads">R-fed-1's automatically-grown uniform feeds, or null when the artwork
    /// needed none. Each one is peeled back off the de-embedded matrix
    /// (<see cref="PlanarFeedExtension.Peel"/>) so the published reference planes are the user's own
    /// drawn metal edges.</param>
    /// <param name="lengthFormat">Owner request, 2026-08-15 — every distance this sweep's own notes
    /// quote (feed leads, port peels, via z-extents, layer thicknesses) goes through this. See
    /// <see cref="SurfaceMesher.Mesh"/>'s own parameter of the same name.</param>
    public static PlanarSolveResult Run(
        PlanarProblem problem,
        PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
        IReadOnlyList<double> freqsHz, PlanarSolveSettings? settings = null,
        RunControl? control = null,
        IReadOnlyList<PlanarFeedLead>? leads = null,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        var slab = problem.Slab;
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(freqsHz);
        if (freqsHz.Count == 0) throw new ArgumentException("A sweep needs at least one frequency.", nameof(freqsHz));

        var st    = settings ?? PlanarSolveSettings.Default;
        var fmt   = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        var notes = new List<string>();

        // Ascending, because both branch resolutions are continuations (PlanarPortCalibrator).
        var freqs = freqsHz.ToArray();
        Array.Sort(freqs);
        double fLo = freqs[0], fHi = freqs[^1];

        bool general = problem.RequiresGeneralKernel;
        var  levels  = general ? PlanarLevels.From(problem) : null;

        // ── ANT-11 §2 — HOW BIG THE REAL GROUND PLANE IS, ON EVERY RUN THAT HAS ONE ──────────
        //
        // Not gated on a far field, a metric or an estimate. The analysis terminates on a laterally
        // INFINITE plane whatever the board looks like, so this sentence is about the same three
        // published numbers (Zin, S, directivity) whether or not anyone asked for a pattern — and a
        // user reading an S-parameter off a 0.4 λ₀ plane is entitled to it as much as one reading a
        // directivity. `SweepNote` returns null when no outline was read, which is the only case
        // where a run says nothing: an absent pour is not a measurement of a small one.
        if (problem.GroundOutline is { } pour &&
            PlanarGroundExtent.SweepNote(pour, problem.MetalBounds(), freqs[0], freqs[^1])
                is { } groundNote)
            notes.Add(groundNote);

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
            var (verdict, scoped) = VerticalRangeVerdict(problem, mesh, fHi, st.Fill, fmt);
            if (!verdict.Ok) throw new InvalidOperationException(verdict.Reason);
            notes.AddRange(scoped);
        }

        // ── M1/M2 — ONE budget, decided here because every context is built against it ───────────
        //
        // A fan-out only exists when there is more than one mesh to solve at a frequency, i.e. when
        // de-embedding is on. At cap 1 there is nothing to spend either, and the plain sequential
        // path is taken so that "cap 1" means exactly what it says. Otherwise the budget carries the
        // user's number, or ProcessorCount when they asked for automatic — which is what an
        // unbounded Parallel.For would have used anyway, and which is what stops five concurrent
        // fills each asking for a full machine's worth of workers.
        int? cap = st.MaxDegreeOfParallelism;
        var  parallelBudget = st.Deembed && cap != 1
            ? new PlanarParallelBudget(cap ?? Environment.ProcessorCount)
            : null;
        var fillSt = (st.Fill ?? PlanarFillSettings.Default) with
        {
            MaxDegreeOfParallelism = cap,
            Budget                 = parallelBudget,
        };

        var sw    = Stopwatch.StartNew();
        var dut   = new PlanarSolveContext(mesh, ports, fillSt, levels, slab.HeightM);
        double setupMs = sw.Elapsed.TotalMilliseconds;
        int    cores   = 1;

        // ── R-prt-2/3: what the ports resolved to, and whether their feeds are clear ─────────────
        foreach (var p in ports)
        {
            notes.Add(p.Describe());
            string? warn = PlanarPorts.CheckFeedClearance(
                mesh, p, (st.Calibration ?? PlanarCalibrationSettings.Default).EndRunHeights * slab.HeightM);
            if (warn is not null) notes.Add(warn);
        }

        // ── R-fed-2: how much of each auto-grown lead sits between the plane and the drawn edge ──
        //
        // The reference plane is one CELL in from the metal (D2), and the metal is now the lead's
        // outer end, so what has to come back off is the lead MINUS that first cell — the half the
        // error box already accounts for. Measured from the resolutions rather than assumed, because
        // the outermost cell's size is the mesher's decision and edge grading makes it small.
        var peelM = new double[ports.Count];
        if (leads is { Count: > 0 })
        {
            var byNumber = leads.ToDictionary(l => l.PortNumber);
            for (int i = 0; i < ports.Count; i++)
            {
                if (!byNumber.TryGetValue(ports[i].Number, out var lead)) continue;
                bool fromLow = ports[i].Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
                double d = fromLow
                    ? lead.DrawnEdgeM - ports[i].ReferencePlaneM
                    : ports[i].ReferencePlaneM - lead.DrawnEdgeM;

                // A negative value means the outermost cell is longer than the whole lead, so the
                // plane landed INSIDE the user's own metal. Peeling a negative length would add line
                // that is not uniform there; the honest answer is to peel nothing and say so.
                if (d < 0)
                {
                    notes.Add(
                        $"Port {ports[i].Number}'s automatic feed lead ({fmt(lead.LengthM)}) " +
                        $"is shorter than the outermost mesh cell, so its reference plane sits " +
                        $"{fmt(-d)} INSIDE your drawn metal rather than on its edge. Raise " +
                        "Cells per wavelength — a finer mesh at the port puts the plane back on the edge.");
                    continue;
                }
                peelM[i] = d;
            }

            var moved = new List<string>();
            for (int i = 0; i < ports.Count; i++)
                if (peelM[i] > 0)
                    moved.Add($"port {ports[i].Number} by {fmt(peelM[i])}");
            if (moved.Count > 0)
                notes.Add("The automatic feed lead is peeled back off the de-embedded matrix as a " +
                          "matched section in the line's own Z_c, using the γ the calibration measured " +
                          "for that same cross-section: " + string.Join(", ", moved) +
                          ". The published reference planes are your drawn metal edges.");
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
                // ── AN INTERNAL DELTA GAP OWNS NO CALIBRATION, AND THAT IS WHAT IT IS ────────────
                //
                // The two-line calibration measures a FEED and removes it. An interior cut has metal
                // on both sides and no feed, so there is no error box to solve for, no uniform line
                // that could serve as its standard, and no Z_c to reference the answer to. It takes
                // the identity box below and keeps its own declared Z0 — see IdentityBox.
                //
                // This is not "de-embedding skipped for now": building a standard here would mean
                // inventing a feed the structure does not have and then removing it, which changes
                // the answer by whatever was invented.
                if (!ports[i].IsDeembeddable) { byPort[i] = -1; continue; }

                int k = PlanarCalibration.EndRunCellsFor(ports[i], slab, st.Calibration);
                int found = -1;
                for (int j = 0; j < owners.Count; j++)
                    if (PlanarPortCalibrator.SameCrossSection(owners[j], ports[i], k)) { found = j; break; }

                if (found < 0)
                {
                    // ── L9d/D3 — a standard is a SINGLE-LEVEL uniform line on the port's own level,
                    //    and Z_c's quasi-static C_pul is what bounds where that is legitimate.
                    //
                    // **MIM-4 retired the refusal that stood here.** It said a buried level "needs a
                    // static Green's function at INTERIOR heights ... and nothing else in this
                    // repository provides it". InteriorStaticImages now does, so the port is
                    // CALIBRATED rather than refused: PlanarPortCalibrator takes the problem's own
                    // medium and D7's static differencing runs at the level's own z. The on-slab-top
                    // path is untouched and stays on the shipped image series bit for bit — the
                    // calibrator makes that choice in one place, from the level's height.
                    //
                    // What is checked instead is the thing that can actually go wrong now: the
                    // interior electrostatics is a FIT, and a fit that did not converge would
                    // renormalise every published s-parameter by a wrong reference just as surely as
                    // the wrong series would have. Its own measured residual is the gate, below.
                    // (An internal delta gap never reaches here — it took the IsDeembeddable
                    // continue above — and that is right: this is about C_pul deciding the Z_c the
                    // answer is REFERENCED to, and an internal port is referenced to its own declared
                    // Z0 instead.)

                    sw.Restart();

                    // ── RP-2c — A THIRD CONDUCTOR AT THE PLANE IS A REFUSAL, NOT A GUESS ─────────
                    //
                    // The coplanar standard reproduces the port's own neighbourhood, and D7 drives
                    // it as a PAIR: signal at +½ V, return at −½ V. A third piece of metal crossing
                    // the same plane — the far ground strip of a CPW whose port named only one of
                    // them, or a neighbouring line — has no stated potential, and the two readings
                    // available differ by more than a rounding: bond it to the return (the CPW
                    // reading) or leave it floating at whatever potential carries zero net charge
                    // (the coupled-line reading). Choosing silently publishes a reference impedance
                    // for a mode nobody asked for, which is R-rp2-4's own failure one level down.
                    if (ports[i].CrossSection is { ConductorCount: > 2 } xs3)
                        throw new InvalidOperationException(
                            $"Port {ports[i].Number} returns through drawn metal, and " +
                            $"{xs3.ConductorCount} separate conductors cross its reference plane — " +
                            "the two this port drives, and " +
                            $"{xs3.ConductorCount - 2} more. De-embedding it needs a calibration " +
                            "standard that is the port's own neighbourhood, and this kernel builds " +
                            "the coplanar PAIR: the signal conductor at +½ V and the named return " +
                            "at −½ V. The extra metal has no stated potential, and the two " +
                            "reasonable answers are far apart — tied to the return (a CPW with two " +
                            "ground strips) or floating at zero net charge (a neighbouring line) — " +
                            "so it is not chosen here. Join the ground strips before the reference " +
                            "plane so the pair is genuinely two conductors, move the port to a " +
                            "station where only the pair crosses it, cut this port as an internal " +
                            "delta gap instead (an interior cut has no feed, no error box and needs " +
                            "no standard), or turn de-embedding off and read the raw solve — those " +
                            "s-parameters include the port discontinuity and are for diagnostics only.");

                    // ── R-dcl-1..4 (brief-em-deembed-ceiling-closeout.md), RE-POINTED AT P11 —
                    // refuse a de-embedded run AT SETUP, honestly, rather than let it succeed here
                    // and throw real minutes later out of PlanarDeembed.CapacitancePerMetre.
                    //
                    // **It has to happen HERE, before the calibrator is constructed, and P11 found
                    // out the hard way that it did not.** The calibrator builds one
                    // PlanarSolveContext per standard, and that constructor's own eager
                    // SurfaceMesher.GuardCeiling throws first — with a correct sentence about a
                    // mesh, which says nothing about de-embedding, about which port, or about the
                    // remedy. The check that used to sit after this loop was therefore unreachable
                    // on the dense path and no test had ever seen its message.
                    //
                    // Until P11 the static capacitance solve was ALWAYS dense whatever the run's
                    // settings said, so this was judged against the DENSE ceiling even on an
                    // accelerated run and the sentence had to say the accelerator would not help.
                    // **It now does**: P is exactly the scalar block M5 projects, PlanarStaticAim
                    // solves it accelerated, and a standard is judged against the same ceiling as
                    // the DUT. What is left is the DENSE run's refusal, whose first remedy is now
                    // turning the accelerator on rather than turning de-embedding off.
                    //
                    // A standard reproduces the DUT's own transverse gridlines VERBATIM (D4), so a
                    // wide-port DUT's standard can be larger than the DUT itself — which is why the
                    // mesh remedies §0 of the parent brief measured inert on this class of geometry
                    // are not offered here either.
                    //
                    // P12 — asked through SurfaceMesher.UsesAcceleratedCeiling like every other
                    // site rather than spelled out again here. The answer is the same one today;
                    // the point is that it stays the same one if the multi-level half of that
                    // decision is ever settled differently, since a standard judged against a
                    // ceiling the DUT is not judged against is the same defect P12 fixed.
                    bool accStd = SurfaceMesher.UsesAcceleratedCeiling(fillSt.Aim is not null, general);
                    int  stdCeiling = accStd ? SurfaceMesher.AcceleratedUnknownCeiling
                                             : SurfaceMesher.UnknownCeiling;
                    var stdSet = PlanarCalibration.BuildSet(ports[i], slab, fLo, fHi, st.Calibration);
                    foreach (var std in stdSet)
                    {
                        int nStd = std.Mesh.Bases.Count;
                        if (nStd <= stdCeiling) continue;

                        var stdSizes = stdSet.Select(z => z.Mesh.Bases.Count.ToString("N0"));
                        throw new InvalidOperationException(
                            $"Port {ports[i].Number}'s calibration standard needs {nStd:N0} unknowns " +
                            $"to solve for its reference impedance, past the {stdCeiling:N0}-unknown " +
                            (accStd ? "ACCELERATED " : "") + "ceiling. This is de-embedding's OWN " +
                            "standard, not the DUT's mesh — a standard reproduces the DUT's " +
                            "transverse gridlines verbatim, so a wide port's standard can be larger " +
                            "than the DUT itself. " +
                            (accStd
                                ? "Both the standards' frequency-domain solves and their static " +
                                  "capacitance solve (Z_c = γ/(jωC_pul)) are accelerated, so this is " +
                                  "the same ceiling the DUT is judged against and there is no further " +
                                  "switch to turn on. Coarsen the mesh, or turn de-embedding off and " +
                                  "read the raw solve — those s-parameters include the port " +
                                  "discontinuity rather than being the structure's own response, and " +
                                  "are for diagnostics only."
                                : "Turn ON the accelerated solve (the EM setup's Accelerated solve, " +
                                  "PlanarFillSettings.Aim): since P11 it covers the standards' static " +
                                  "capacitance solve (Z_c = γ/(jωC_pul)) as well as every " +
                                  "frequency-domain system, and its ceiling is " +
                                  $"{SurfaceMesher.AcceleratedUnknownCeiling:N0} unknowns. Failing " +
                                  "that, turn de-embedding off and read the raw solve instead: those " +
                                  "s-parameters include the port discontinuity rather than being the " +
                                  "structure's own response, and are for diagnostics only.") +
                            $" The DUT's own mesh is N = {mesh.Bases.Count:N0}; this port's " +
                            $"standard(s) are N = {string.Join(" / ", stdSizes)}.");
                    }

                    var cal = new PlanarPortCalibrator(
                        ports[i], slab, fLo, fHi, st.Calibration, fillSt,
                        standardLevelZ: general ? problem.LevelZ(ports[i].LayerIndex) : double.NaN,
                        standards: stdSet,
                        mediumStack: general ? problem.EffectiveStack : null);

                    // MIM-4 — the interior electrostatics is fitted, so its quality is asked about
                    // rather than assumed. R-mom-17: a fit this poor is refused BY NAME at setup,
                    // not carried into a published reference impedance.
                    if (cal.InteriorFitResidual > InteriorCPulResidualCeiling)
                        throw new InvalidOperationException(
                            $"Port {ports[i].Number} sits on conductor level {ports[i].LayerIndex} at " +
                            $"z = {fmt(problem.LevelZ(ports[i].LayerIndex))}, an interior height of " +
                            $"this stack, and the static Green's function there did not fit: its " +
                            $"spectral residual is {cal.InteriorFitResidual:E2} against a ceiling of " +
                            $"{InteriorCPulResidualCeiling:E0}. De-embedding references the answer to " +
                            "the line's own Z_c = γ/(jωC_pul), so a bad electrostatic fit renormalises " +
                            "every published s-parameter rather than degrading one number. Simplify " +
                            "the medium under this level (merging two dielectrics of nearly equal εᵣ " +
                            "is exact, not an approximation), bring the feed out on a level with a " +
                            "simpler stack beneath it, or turn de-embedding off and read the raw " +
                            "solve — those s-parameters include the port discontinuity and are for " +
                            "diagnostics only.");
                    setupMs += sw.Elapsed.TotalMilliseconds;
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

            int deembedded = 0;
            var uncalibrated = new List<int>();
            var gapPorts     = new List<int>();
            var groundPorts  = new List<int>();
            for (int i = 0; i < ports.Count; i++)
            {
                if (ports[i].IsDeembeddable) { deembedded++; continue; }
                uncalibrated.Add(ports[i].Number);
                (ports[i].Kind == PlanarPortKind.Internal ? groundPorts : gapPorts)
                    .Add(ports[i].Number);
            }

            // The two internal kinds share the sentence that matters — nothing outside the cut, so
            // nothing to remove — and differ in what the cut IS, which is the half a user has to
            // read differently: a delta gap is one mesh cell of trace, an internal port is the path
            // down to the plane. Listed apart rather than under one name, because "internal delta
            // gaps" said of a ground-referenced port is a statement about the wrong geometry.
            if (uncalibrated.Count > 0)
                notes.Add(
                    (gapPorts.Count > 0
                        ? $"Port(s) {string.Join(", ", gapPorts)} are internal delta gaps — interior " +
                          "cuts with metal on both sides. "
                        : "") +
                    (groundPorts.Count > 0
                        ? $"Port(s) {string.Join(", ", groundPorts)} are internal ports — between the " +
                          "metal and the ground plane, at the foot of the via that gets there. "
                        : "") +
                    $"They are NOT de-embedded: there is no port " +
                    "discontinuity outside such a cut to remove and no line impedance to reference to. Their " +
                    "s-parameters are reported at the cut itself, in the reference impedance declared " +
                    "for each — which is exactly what an internal port means, not a step that was " +
                    "skipped." +
                    (gapPorts.Count > 0
                        ? " A gap is one mesh cell wide, so refining the mesh there is what makes " +
                          "it a better approximation to a point discontinuity."
                        : "") +
                    (deembedded > 0
                        ? " The remaining port(s) are de-embedded normally; the two kinds share one " +
                          "s-matrix and each keeps its own reference."
                        : " No port in this run is de-embedded, so nothing here is calibrated against " +
                          "a uniform line at all."));

            if (calibrators.Count > 0)
            notes.Add($"De-embedding costs {calibrators.Count} calibration(s) over {deembedded} de-embedded port(s), " +
                      $"{standards} standard mesh(es) of N = {string.Join(" / ", sizes)} against the DUT's " +
                      $"N = {mesh.Bases.Count} — {(double)totalN / Math.Max(mesh.Bases.Count, 1):F2}× the " +
                      "DUT's unknowns, solved at every frequency alongside it.");

            if (calibrators.Count < deembedded)
                notes.Add($"{deembedded} port(s) share {calibrators.Count} calibration(s), because their " +
                          "cross-sections and port cells are identical — the standards are solved once each.");

            // M2 — the user set a core count in the panel and it is a machine setting, not part of the
            // design, so the run says what it actually did with it rather than leaving the user to
            // infer it from a stopwatch. It names the SOLVES because that is the number the cap acts
            // on; it deliberately does not promise a speed-up, which depends on how unbalanced the
            // standards are (on the brief's own §0 design two of five solves are 96% of the work).
            if (parallelBudget is not null && standards > 0)
                notes.Add($"The DUT and its {standards} calibration standard(s) are solved concurrently at " +
                          $"each frequency — {1 + standards} independent solves, across at most " +
                          $"{parallelBudget.Cap} core(s)" +
                          (cap is null ? $" (automatic, from this machine's {Environment.ProcessorCount})" : "") +
                          ". The core count is a machine setting and changes no answer: the same sweep at " +
                          "any cap produces bit-identical s-parameters.");

            if (general && calibrators.Count > 0)
                notes.Add(GeneralStackCalibrationNote(problem, owners, fmt));
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

        // ── ANT-4 — which solved points get a pattern ───────────────────────────────────────────
        //
        // The far field asks for FREQUENCIES; the sweep owns INDICES. Each requested frequency is
        // mapped to the nearest point of the grid here, once, so the adaptive and non-adaptive
        // drivers below cannot disagree about which point a pattern belongs to. A pattern is only
        // ever produced from a point that was actually SOLVED — an interpolated s-parameter has no
        // basis currents behind it, and inventing some would be a pattern of nothing.
        var farSettings = st.FarField;
        EmSuitability farVerdict = EmSuitability.Yes;
        var farWanted = new SortedSet<int>();
        if (farSettings is not null)
        {
            var asked = farSettings.FrequenciesHz is { Count: > 0 } list
                ? list
                : [st.CurrentDensityFrequencyHz > 0 ? st.CurrentDensityFrequencyHz : freqs[0]];
            foreach (double want in asked)
            {
                int at = 0; double best = double.PositiveInfinity;
                for (int i = 0; i < freqs.Length; i++)
                {
                    double d = Math.Abs(freqs[i] - want);
                    if (d < best) { best = d; at = i; }
                }
                farWanted.Add(at);
            }

            // Asked at the LOWEST requested point, not at the top of the sweep: the spectral
            // kernel's only frequency-dependent refusal is its electrical-thickness FLOOR, so the
            // binding point is the lowest one a pattern was actually asked for. Checking fHi would
            // pass a run that then threw at its first pattern.
            double fCheck = double.PositiveInfinity;
            foreach (int i in farWanted) fCheck = Math.Min(fCheck, freqs[i]);
            farVerdict = PlanarFarField.CanCompute(problem, mesh, fCheck, farSettings.EffectiveGrid);
            if (!farVerdict.Ok) farWanted.Clear();
        }
        var farPatterns = new Dictionary<int, PlanarFarFieldPattern[]>();
        var farMetrics  = new Dictionary<int, PlanarMetricReport[]>();
        var farPol      = new Dictionary<int, PlanarPolarizationPattern[]>();

        // ── One frequency's raw DUT solve, lifted out of the loop so the adaptive driver below
        //    reaches EXACTLY the same arithmetic. R-adf-1's bit-identity when adaptive is off is a
        //    property of this being one implementation, not of two that agree.
        // Each PHASE owns its own stage bar rather than one bar spanning the whole point, and that
        // is a bug fix rather than a refactor (owner report, 2026-08-09: the row read "11 / 4").
        // A single per-point total cannot work, because the ADAPTIVE path replays de-embedding over
        // every already-solved point after each insertion — those ticks landed on the stage the last
        // raw solve had begun, so the numerator ran away from a denominator that only ever counted
        // ONE point's worth of work. Two stages, each with a total it can actually reach.

        // Engineering notation, because a stage label is read at a glance: "10 GHz", not "1E+10".
        static string FormatHz(double f) =>
            f >= 1e9  ? $"{f / 1e9:0.###} GHz" :
            f >= 1e6  ? $"{f / 1e6:0.###} MHz" :
            f >= 1e3  ? $"{f / 1e3:0.###} kHz" :
                        $"{f:0.###} Hz";

        // M2 — the return gained StandardsMs, because the standards are now solved HERE rather than
        // inside DeembedAt. Their wall clock and the DUT's overlap once the cap allows it, so each is
        // measured on its own Stopwatch and the two no longer sum to the point's elapsed time. Note
        // that `sw` is the driver's SHARED stopwatch and must never be restarted from a work item.
        (PlanarFrequencyKernel Kernel, Mat<Complex> Raw, Vec<Complex>[] Currents, Mat<Complex> Y,
         double KernelMs, double DutMs, double StandardsMs)
        SolveRawAt(double f)
        {
            // The kernel fit is sequential and cheap (~0.2 s, L8c's Tier 8) and everything below
            // needs it, so it is not part of the fan-out.
            // What will ACTUALLY be filled here, not how many standards exist: a calibrator solves the
            // short line plus the one long line this frequency selects (PlanarPortCalibrator.NeededAt),
            // so MeshCount would promise ticks that never arrive.
            int standardSolves = 0;
            foreach (var cal in calibrators) standardSolves += cal.PlannedSolvesAt(f);

            control?.BeginStage($"{FormatHz(f)} — Green's function", 2 + standardSolves);
            sw.Restart();
            var k = PlanarFrequencyKernel.Fit(
                problem, f, fillSt.Order, st.Dcim);
            double kMs = sw.Elapsed.TotalMilliseconds;
            control?.TickStage(nextLabel: $"{FormatHz(f)} — solving the structure");

            // ── M2/R-emp-9 — the DUT and every calibration STANDARD are independent solves at this
            //    frequency, sharing only the read-only kernel and their own read-only cores. On the
            //    brief's own §0 design that is five solves of which two are 96% of the work, so what
            //    the fan-out buys is the OVERLAP (one solve's single-threaded LU running while
            //    another fills) rather than more parallelism in any one fill — see
            //    PlanarParallelBudget's own header.
            PlanarPortSolution? sol = null;
            double dMs = 0, sMs = 0;
            var standardsClock = new object();
            var solvesAtF = new List<Action>(1 + standardSolves);
            var pending   = new List<PlanarPortCalibrator.PlanarCalibratorWork>(calibrators.Count);

            solvesAtF.Add(() =>
            {
                var own = Stopwatch.StartNew();
                sol = dut.SolveAt(k, f);
                dMs = own.Elapsed.TotalMilliseconds;
                control?.TickStage();
            });

            foreach (var cal in calibrators)
                if (cal.PrepareAt(() => k, f) is { } w)
                {
                    pending.Add(w);
                    foreach (var solve in w.Solves)
                        solvesAtF.Add(() =>
                        {
                            var own = Stopwatch.StartNew();
                            solve();
                            lock (standardsClock) sMs += own.Elapsed.TotalMilliseconds;
                            control?.TickStage();
                        });
                }

            PlanarFanOut.Run(cap, solvesAtF);

            // The cache writes and the branch continuation stay on ONE thread, exactly as before —
            // R-emp-9's whole point is that only the SOLVES are order-independent.
            foreach (var w in pending) w.Commit();

            var r = PlanarExcitation.RawScattering(sol!.Y, z0);
            return (k, r, sol.Currents.ToArray(), sol.Y, kMs, dMs, sMs);
        }

        // ── The de-embedding half, likewise shared. `kernelFor` is lazy because a REPLAY (adaptive
        //    only) hits the calibrator's raw cache and needs no kernel at all.
        // <param name="ownStage">True when this call is the whole of what is happening — it then
        // begins its own stage. False during an adaptive REPLAY, where the caller owns one stage
        // spanning every replayed point; ticking a per-point stage from inside that loop is exactly
        // what produced the runaway numerator.</param>
        (Mat<Complex> S, List<PlanarPortCalibration> Cals, double CalMs)
        DeembedAt(double f, Mat<Complex> raw, Func<PlanarFrequencyKernel> kernelFor, bool ownStage = true)
        {
            sw.Restart();
            Mat<Complex> s = raw;
            var cals = new List<PlanarPortCalibration>(ports.Count);

            if (st.Deembed)
            {
                if (ownStage)
                    control?.BeginStage($"{FormatHz(f)} — de-embedding", calibrators.Count);

                var perCal = new PlanarPortCalibration[calibrators.Count];
                for (int j = 0; j < calibrators.Count; j++)
                {
                    if (ownStage)
                        control?.SetStageLabel(
                            $"{FormatHz(f)} — calibration standard {j + 1} of {calibrators.Count}");
                    perCal[j] = calibrators[j].At(kernelFor, f);
                    if (ownStage) control?.TickStage();
                }

                var boxes = new PlanarErrorBox[ports.Count];
                var zc    = new Complex[ports.Count];
                var gam   = new Complex[ports.Count];
                for (int i = 0; i < ports.Count; i++)
                {
                    // ── AN INTERNAL DELTA GAP PASSES THROUGH, EXACTLY ───────────────────────────
                    //
                    // The identity error box (a₁₁ = 0, a₂₂ = 0, a₂₁ = 1) makes PlanarDeembed.Apply's
                    // algebra the identity on this port's row and column — y[i,j] divides by
                    // a₂₁(i)·a₂₁(j), so a unit a₂₁ leaves the mixed terms of a de-embedded neighbour
                    // untouched too, which is what lets the two kinds share one s-matrix. Zc = the
                    // port's own Z0 makes Renormalise the identity for it as well.
                    //
                    // This is arithmetic that provably changes nothing, NOT a de-embedding of an
                    // internal port. Writing it this way rather than partitioning the matrix keeps
                    // one code path for both kinds; the partitioned alternative would need its own
                    // proof that the off-diagonal terms come out the same.
                    if (!ports[i].IsDeembeddable)
                    {
                        boxes[i] = IdentityBox;
                        zc[i]    = z0[i];
                        gam[i]   = Complex.Zero;
                        continue;
                    }

                    var c = perCal[byPort[i]];
                    cals.Add(c with { PortNumber = ports[i].Number });
                    boxes[i] = c.Box;
                    zc[i]    = c.Zc;
                    gam[i]   = c.Gamma.Gamma;
                    if (!c.Gamma.Usable) flaggedBand.Add(f);
                }

                // R-fed-2 — the lead comes off BEFORE renormalisation: "matched" means matched in
                // Z_c, which is the reference Apply hands back and the one the section is uniform in.
                var atZc = PlanarFeedExtension.Peel(PlanarDeembed.Apply(raw, boxes), peelM, gam);
                s = PlanarDeembed.Renormalise(atZc, zc, z0);
            }
            return (s, cals, sw.Elapsed.TotalMilliseconds);
        }

        int    solvedCount = freqs.Length;
        double worstAdaptive = double.NaN;
        var    solvedList = Array.Empty<double>();
        bool?  converged  = null;
        var    addedList  = Array.Empty<double>();
        IReadOnlyList<PlanarResonance> resonances = [];

        if (st.Adaptive is null)
        {
            // L8d's own loop, untouched.
            foreach (double f in freqs)
            {
                var (kernel, raw, currents, y, kernelMs, dutMs, standardsMs) = SolveRawAt(f);

                if (capturePort >= 0 && points.Count == captureAt)
                {
                    captured  = currents[capturePort];
                    capturedF = f;
                }

                // ── THE DE-EMBEDDING RUNS FIRST, AND THAT ORDER IS LOAD-BEARING ──────────
                //
                // ANT-12 measured that the mismatch factor may not be read off the RAW
                // self-admittance — at a de-embedded edge port that is the delta gap's own
                // parasitic, not the antenna's input, and the realized gain came out 15 dB low on an
                // antenna matched to a quarter of a dB. The number it needs is the PUBLISHED,
                // de-embedded, renormalised S_jj of this same point, which this loop produces one
                // line later. So the pattern is taken after it rather than before, and
                // RealizedGainDbi is published instead of refused. Nothing else about the point
                // changes: the raw solve, the currents and the de-embedding are the same calls in
                // the same frequency order.
                var (s, cals, calMs) = DeembedAt(f, raw, () => kernel);

                if (farWanted.Contains(points.Count))
                {
                    var (pats, mets, pols) = FarFieldAt(problem, mesh, currents, y, s, ports, f,
                                                        farSettings!, cap, control);
                    farPatterns[points.Count] = pats;
                    farMetrics[points.Count]  = mets;
                    farPol[points.Count]      = pols;
                }

                points.Add(new PlanarFrequencyPoint(
                    f, s, raw, cals, kernelMs, dutMs, standardsMs + calMs));
                control?.Tick();
            }
        }
        else
        {
            var ad = st.Adaptive;
            int budget = Math.Min(Math.Max(ad.MaxSolves, 2), freqs.Length);

            var rawByIndex   = new Dictionary<int, Mat<Complex>>();
            var kernelByIndex = new Dictionary<int, PlanarFrequencyKernel>();
            var timeByIndex  = new Dictionary<int, (double K, double D, double S)>();
            var currentsByIndex = new Dictionary<int, Vec<Complex>[]>();
            var yByIndex     = new Dictionary<int, Mat<Complex>>();
            var solved = new SortedSet<int>();

            // ── ANT-9's storage, empty and untouched unless the search runs ────────────────────
            //
            // Keyed by FREQUENCY, because a found point is by definition not on the grid and has no
            // index to be keyed by. The two stores stay separate rather than being merged into one:
            // phase 1's refinement is index arithmetic over the requested grid and must keep being
            // exactly that, or "the search is off and nothing changed" stops being structural.
            var extraRaw    = new SortedDictionary<double, Mat<Complex>>();
            var extraKernel = new Dictionary<double, PlanarFrequencyKernel>();
            var extraTime   = new Dictionary<double, (double K, double D, double S)>();
            var gridIndexOf = new Dictionary<double, int>();
            for (int i = 0; i < freqs.Length; i++) gridIndexOf.TryAdd(freqs[i], i);

            void Solve(int i)
            {
                if (!solved.Add(i)) return;
                var r = SolveRawAt(freqs[i]);
                control?.Tick();
                rawByIndex[i]      = r.Raw;
                kernelByIndex[i]   = r.Kernel;
                timeByIndex[i]     = (r.KernelMs, r.DutMs, r.StandardsMs);
                currentsByIndex[i] = r.Currents;
                yByIndex[i]        = r.Y;
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
                // One stage for the whole replay — most of it is cache hits, so per-point stages here
                // would flicker through every frequency for no information.
                control?.BeginStage("replaying calibration", solved.Count);
                foreach (int i in solved)
                {
                    outp[i] = DeembedAt(freqs[i], rawByIndex[i], () => kernelByIndex[i], ownStage: false);
                    control?.TickStage();
                }
                return outp;
            }

            // ── ANT-9's replay: the same walk, over the UNION of grid points and found ones ────
            //
            // Keyed by frequency because that is the only key both halves share. It exists rather
            // than `Replay` being generalised in place because `Replay` is what the search-off path
            // runs and must keep running: R-adf-3's "ascending order from a fresh branch state" is a
            // property of ONE loop, and two loops that agree about it today are two loops that can
            // stop agreeing.
            List<double> UnionAscending()
            {
                var all = new List<double>(solved.Count + extraRaw.Count);
                foreach (int i in solved) all.Add(freqs[i]);
                all.AddRange(extraRaw.Keys);
                all.Sort();
                return all;
            }

            Dictionary<double, (Mat<Complex> S, List<PlanarPortCalibration> Cals, double CalMs)>
                ReplayAll(List<double> all)
            {
                foreach (var c in calibrators) c.RestartBranchContinuation();
                flaggedBand.Clear();
                var outp = new Dictionary<double, (Mat<Complex>, List<PlanarPortCalibration>, double)>();
                control?.BeginStage("replaying calibration", all.Count);
                foreach (double f in all)
                {
                    outp[f] = gridIndexOf.TryGetValue(f, out int gi)
                        ? DeembedAt(f, rawByIndex[gi], () => kernelByIndex[gi], ownStage: false)
                        : DeembedAt(f, extraRaw[f], () => extraKernel[f], ownStage: false);
                    control?.TickStage();
                }
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

            // An interval that stopped INSIDE the tolerance converged; one that stopped above it ran
            // out of grid or budget. `worstStopped` is the max over both, so this one comparison is
            // the verdict — and it is computed here rather than inferred from the note, because
            // ANT-9 §4's whole complaint is that a reader was left to infer it.
            converged = !(worstStopped > ad.Tolerance);

            // ANT-4 on the adaptive path. A requested point that the sampler never solved has no
            // basis currents, so the pattern is taken at the nearest point that WAS solved and the
            // substitution is reported — never interpolated, which would be a pattern of nothing.
            if (farWanted.Count > 0)
            {
                var moved = new List<int>();
                var chosen = new SortedSet<int>();
                foreach (int want in farWanted)
                {
                    int near = solved.Min;
                    foreach (int j in solved)
                        if (Math.Abs(freqs[j] - freqs[want]) < Math.Abs(freqs[near] - freqs[want])) near = j;
                    if (near != want) moved.Add(want);
                    chosen.Add(near);
                }
                farWanted.Clear();
                foreach (int i in chosen)
                {
                    farWanted.Add(i);
                    // ANT-12 — the de-embedded S of this same point, which `byIndex` already holds
                    // because the refinement loop replayed the calibration over every solved index
                    // before it stopped. That is RealizedGainDbi's one input; see the non-adaptive
                    // loop above for why the RAW admittance is not.
                    var (pats, mets, pols) = FarFieldAt(problem, mesh, currentsByIndex[i],
                                                        yByIndex[i], byIndex[i].S, ports, freqs[i],
                                                        farSettings!, cap, control);
                    farPatterns[i] = pats;
                    farMetrics[i]  = mets;
                    farPol[i]      = pols;
                }
                if (moved.Count > 0)
                    notes.Add($"{moved.Count} requested far-field frequency point(s) were not solved " +
                              $"by the adaptive sampler; the pattern was taken at the nearest SOLVED " +
                              $"point instead. A pattern needs basis currents, and an interpolated " +
                              $"s-parameter has none.");
            }

            // ══════════════════════════════════════════════════════════════════════════════════
            // ANT-9 — THE RESONANCE SEARCH. Everything above this point ran identically whether or
            // not the mode is on; everything below branches on it. Null does nothing at all.
            // ══════════════════════════════════════════════════════════════════════════════════
            PlanarResonanceOutcome? searchOutcome = null;
            Dictionary<double, (Mat<Complex> S, List<PlanarPortCalibration> Cals, double CalMs)>? byFreq
                = null;

            if (ad.Search is { } rs && freqs.Length >= 2 && solved.Count >= 2)
            {
                // The probe: one real solve, then the WHOLE calibration replayed in ascending
                // frequency order from a fresh branch state. The replay is not optional and is not
                // an optimisation left out — a found point lands BETWEEN two already-calibrated
                // frequencies, and `PlanarPortCalibrator` is stateful in frequency (§3.5: predicting
                // βΔℓ instead runs 15-20 % low and coin-flips the 2π branch). Most of a replay is
                // cache hits; what it re-derives is the branch continuation, which is the part that
                // has to see the new point in its right place.
                PlanarResonanceNodes Probe(double f)
                {
                    if (!extraRaw.ContainsKey(f) && !gridIndexOf.ContainsKey(f))
                    {
                        var r = SolveRawAt(f);
                        control?.Tick();
                        extraRaw[f]    = r.Raw;
                        extraKernel[f] = r.Kernel;
                        extraTime[f]   = (r.KernelMs, r.DutMs, r.StandardsMs);
                    }
                    var all = UnionAscending();
                    byFreq = ReplayAll(all);
                    return new PlanarResonanceNodes(all, all.Select(x => byFreq[x].S).ToArray());
                }

                var all0 = UnionAscending();
                byFreq = ReplayAll(all0);
                var start = new PlanarResonanceNodes(all0, all0.Select(x => byFreq[x].S).ToArray());

                int port = Math.Clamp(rs.PortNumber - 1, 0, ports.Count - 1);
                searchOutcome = PlanarResonanceSearch.Search(
                    start, freqs[0], freqs[^1], z0[port], ad.Interpolant, ad.Tolerance,
                    rs with { PortNumber = port + 1 }, Probe);

                resonances = searchOutcome.Resonances;
                notes.Add(searchOutcome.Note);
            }

            // ── Publish on the USER'S grid (R-adf-2). A solved point carries its own solved matrix
            //    byte for byte; everything else is the interpolant's value.
            //
            // ANT-9: when the search added points, EVERYTHING here is published out of the ONE union
            // replay, not just the found points. Two things follow from that, and both are the
            // reason it is written this way rather than as a splice onto the pre-search result:
            //
            //   * the found points become interpolation NODES for the requested grid. They are the
            //     best information the run has about exactly the region the interpolant was worst
            //     in, and modelling a requested point near a resonance from a node set that excludes
            //     the points found AT that resonance would be throwing away the whole benefit;
            //   * one sweep is one calibration replay. `byIndex` was resolved before the found
            //     points existed, and a published sweep half from each would carry two different
            //     branch continuations with nothing saying where the seam was.
            bool searched = extraRaw.Count > 0 && byFreq is not null;
            var  solvedIdx = solved.ToList();

            // The SEARCH-OFF path below is byte for byte the one L9e shipped — indexed by grid
            // position throughout. It is not re-expressed in terms of frequency keys "for
            // symmetry": `freqs` is sorted but NOT de-duplicated, so a frequency key can name two
            // grid positions and the one a lookup returns need not be the one that was solved. The
            // duplication is the price of the bit-identity gate being structural.
            Mat<Complex> RawAt(double f) =>
                gridIndexOf.TryGetValue(f, out int gi) && rawByIndex.TryGetValue(gi, out var m)
                    ? m : extraRaw[f];

            var nodeF   = searched ? UnionAscending().ToArray() : solvedList;
            var nodeS   = searched ? nodeF.Select(f => byFreq![f].S).ToArray()
                                   : solvedIdx.Select(i => byIndex[i].S).ToArray();
            var nodeRaw = searched ? nodeF.Select(RawAt).ToArray()
                                   : solvedIdx.Select(i => rawByIndex[i]).ToArray();
            var modelS   = PlanarAdaptiveSweep.Model(nodeF, nodeS,   freqs, ad.Interpolant);
            var modelRaw = PlanarAdaptiveSweep.Model(nodeF, nodeRaw, freqs, ad.Interpolant);

            for (int i = 0; i < freqs.Length; i++)
            {
                bool isSolved = solved.Contains(i);

                // The per-port calibration is a DIAGNOSTIC of a solved point; it is carried from the
                // nearest solved frequency rather than interpolated, because interpolating γ across
                // its own 2π branch is a second modelling claim this does not need to make.
                int near = solvedIdx[0];
                foreach (int j in solvedIdx)
                    if (Math.Abs(freqs[j] - freqs[i]) < Math.Abs(freqs[near] - freqs[i])) near = j;

                var cals  = byIndex[near].Cals;
                double calMs = isSolved ? byIndex[i].CalMs : 0.0;
                if (searched)
                {
                    // Same rule, over the union — a found point can be the nearest solved frequency
                    // to a requested one, and it is the honest source when it is.
                    double nf = nodeF[0];
                    foreach (double f in nodeF)
                        if (Math.Abs(f - freqs[i]) < Math.Abs(nf - freqs[i])) nf = f;
                    cals  = byFreq![nf].Cals;
                    calMs = isSolved ? byFreq[freqs[i]].CalMs : 0.0;
                }

                var (kMs, dMs, sMs) = isSolved ? timeByIndex[i] : (0.0, 0.0, 0.0);
                points.Add(new PlanarFrequencyPoint(
                    freqs[i], modelS[i], modelRaw[i], cals, kMs, dMs, sMs + calMs));
            }

            // ── ANT-9: splice the found points in, ascending, each one FLAGGED ────────────────
            //
            // The requested grid above is untouched in the sense that matters — every frequency the
            // user asked for is still published, still in order, and every one of them that was
            // SOLVED still carries the solver's own matrix byte for byte. The found points are
            // inserted between them, announcing themselves as not part of that grid.
            if (searched)
            {
                foreach (var (f, raw) in extraRaw)
                {
                    var (sMat, cals, calMs) = byFreq![f];
                    var (kMs, dMs, stdMs)   = extraTime[f];
                    points.Add(new PlanarFrequencyPoint(f, sMat, raw, cals, kMs, dMs, stdMs + calMs)
                    {
                        AddedBySearch = true,
                    });
                }
                points.Sort(static (a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));

                addedList   = [.. extraRaw.Keys];
                solvedList  = nodeF;
                solvedCount = solvedList.Length;
            }

            // D5's capture stays on the REQUESTED grid's solved points. A found point has basis
            // currents too, but the heat map was asked for at a frequency the user named, and
            // silently moving it onto a frequency the search chose would be a map of somewhere else.
            if (capturePort >= 0 && captureAt >= 0)
            {
                int near = solved.Min;
                foreach (int j in solved)
                    if (Math.Abs(freqs[j] - freqs[captureAt]) < Math.Abs(freqs[near] - freqs[captureAt]))
                        near = j;
                captured  = currentsByIndex[near][capturePort];
                capturedF = freqs[near];
            }

            // ── ANT-9 §4 — THE REPORT LEADS WITH THE VERDICT ─────────────────────────────────
            //
            // It used to lead with the saving and put the tolerance failure in its last clause. The
            // measurement that opened ANT-9 is what that costs: "44 of 51 point(s) were SOLVED" is
            // read as success, and the |ΔS| = 0.02 against a tolerance of 0.001 twenty words later
            // is read as a footnote. They are the wrong way round — the tolerance IS the result and
            // the point count is the price.
            //
            // Two further rules from the same section. A percentage is not a saving unless it is one,
            // so above 80 % solved the note says outright that the adaptive path did not help here —
            // a control presented as doing something while doing nothing is the same failure as a
            // control that silently does nothing. And a run that did not converge NAMES WHAT WOULD
            // HELP in the same sentence, the way the mesher's refusals name the settings that act on
            // the count, rather than leaving a user to work out that the remedy is theirs to apply.
            double solvedFraction = (double)solved.Count / freqs.Length;
            bool   didConverge    = converged == true;
            bool   budgetBound    = solved.Count >= budget && budget < freqs.Length;
            string modelName = ad.Interpolant == PlanarInterpolant.Rational
                ? "barycentric rational" : "complex cubic spline";

            var adaptiveNote = new System.Text.StringBuilder();
            adaptiveNote.Append("Adaptive frequency sampling ")
                        .Append(didConverge ? "CONVERGED" : "DID NOT CONVERGE")
                        .Append(": the worst disagreement refinement stopped at is |ΔS| = ")
                        .Append(worstStopped.ToString("G3"))
                        .Append(didConverge ? ", inside " : ", against ")
                        .Append("a tolerance of ").Append(ad.Tolerance.ToString("G3")).Append(". ");

            adaptiveNote.Append(solved.Count).Append(" of ").Append(freqs.Length)
                        .Append(" requested point(s) were solved (")
                        .Append((solvedFraction * 100).ToString("F0")).Append(" %)");

            if (solved.Count >= freqs.Length)
                adaptiveNote.Append(" — every one of them, because no interval agreed with the " +
                                    "interpolant closely enough to be skipped, so nothing is modelled " +
                                    "and adaptive sampling saved nothing here. ");
            else if (solvedFraction > 0.8)
                adaptiveNote.Append(", so the adaptive path saved little here — the response is not " +
                                    "smooth on the grid you asked for. The rest are modelled by a ")
                            .Append(modelName).Append(" interpolant through them. ");
            else
                adaptiveNote.Append("; the rest are modelled by a ").Append(modelName)
                            .Append(" interpolant through them. ");

            if (!didConverge)
            {
                adaptiveNote.Append("What would help: a FINER requested grid, because the feature " +
                                    "refinement could not resolve is narrower than the spacing of " +
                                    "this one and every point it is allowed to reach is already " +
                                    "solved");
                adaptiveNote.Append(ad.Search is null
                    ? "; or, if that feature is a resonance, the resonance search, which is the one " +
                      "mode allowed to add frequencies between the ones you asked for. "
                    : ". The resonance search is already on, and it reports separately what it found. ");
            }

            if (budgetBound)
                adaptiveNote.Append("The solve budget was reached before every interval converged — " +
                                    "treat the modelled points with that in mind. ");

            adaptiveNote.Append(extraRaw.Count > 0
                ? $"{extraRaw.Count} further frequency(ies) were ADDED by the resonance search and " +
                  "are flagged as such; every point of your own grid is published exactly as it was. "
                : "Every published frequency is the one you asked for. ");

            adaptiveNote.Append("A solved point carries the solver's own matrix exactly, and its " +
                                "per-port calibration diagnostics are carried from the nearest solved " +
                                "frequency rather than interpolated.");

            notes.Add(adaptiveNote.ToString());
        }

        if (st.Deembed && PassivityNote(points, z0) is { } passivity) notes.Add(passivity);

        if (flaggedBand.Count > 0)
            notes.Add($"{flaggedBand.Count} of {freqs.Length} frequency point(s) fall outside the " +
                      $"[{PlanarCalibrationSettings.UsableLoDegrees:F0}°, " +
                      $"{PlanarCalibrationSettings.UsableHiDegrees:F0}°] electrical length the two-line " +
                      $"calibration is well conditioned over — first at {SurfaceMesher.Eng(flaggedBand[0])}Hz. " +
                      "Narrow the sweep, or accept that those points carry a larger de-embedding error.");

        // ── P2/M4 — the cores are built lazily now, so the run's core time is SUMMED from the
        //    contexts that actually built one rather than measured around their constructors. The
        //    setup time those constructors do still cost (meshing the standards, resolving their
        //    ports) is carried with it, so the reported number still covers the same work.
        double coreBuildMs = setupMs + dut.CoreBuildMs;
        foreach (var cal in calibrators) coreBuildMs += cal.CoreBuildMs;

        // ── ANT-4/ANT-5 — the patterns, the metrics, and the sentences that make them readable ──
        PlanarFarFieldSet?       farSet    = null;
        PlanarMetricSet?         metricSet = null;
        PlanarPolarizationSet?   polSet    = null;
        if (farSettings is not null && !farVerdict.Ok)
        {
            // Present and refused, which is the house shape: the sweep is not thrown away for a
            // diagnostic it cannot produce, and the reason is the engine's own wording.
            notes.Add("No far field was computed. " + farVerdict.Reason);
        }
        else if (farPatterns.Count > 0)
        {
            var idx = farPatterns.Keys.OrderBy(i => i).ToArray();
            var flat = new List<PlanarFarFieldPattern>(idx.Length * ports.Count);
            foreach (int i in idx) flat.AddRange(farPatterns[i]);
            farSet = new PlanarFarFieldSet(
                farSettings!.EffectiveGrid,
                idx.Select(i => freqs[i]).ToArray(),
                ports.Select(pp => pp.Number).ToArray(),
                flat);

            var first = farSet.At(0, 0);
            notes.Add(first.ScaleCaption);
            notes.Add("The pattern is the radiation of the structure AS MESHED, which includes any " +
                      "uniform feed lead R-fed-1 grew for the calibration. The s-parameters beside it " +
                      "have that lead de-embedded away; a pattern cannot, because the lead's current " +
                      "is real current and it really radiates.");

            // ── ANT-5 — the metrics. Every refusal is said ONCE, in the registry's own wording, and
            //    the sweep is never thrown away for one of them (present and refused).
            var flatMetrics = new List<PlanarMetricReport>(idx.Length * ports.Count);
            foreach (int i in idx) flatMetrics.AddRange(farMetrics[i]);
            metricSet = PlanarMetricSet.From(
                idx.Select(i => freqs[i]).ToArray(),
                ports.Select(pp => pp.Number).ToArray(),
                flatMetrics);

            var firstMetrics = metricSet.At(0, 0);
            notes.Add(firstMetrics.Budget.Caption);
            if (firstMetrics.Budget.SurfaceWave is { } guided) notes.Add(guided.Caption);
            notes.Add(PlanarPowerBudget.BoundNote);
            foreach (string refusal in metricSet.Refusals) notes.Add(refusal);

            // ── ANT-6 — polarization. The reference angle is REPORTED whether it was named or
            //    derived (R-ant-9), and the mesh-limited cross-pol floor is said wherever a cross-pol
            //    number is (R-ant-11) rather than left for a user to discover.
            var flatPol = new List<PlanarPolarizationPattern>(idx.Length * ports.Count);
            foreach (int i in idx) flatPol.AddRange(farPol[i]);
            polSet = PlanarPolarizationSet.From(
                idx.Select(i => freqs[i]).ToArray(),
                ports.Select(pp => pp.Number).ToArray(),
                flatPol);

            var firstPol = polSet.At(0, 0);
            notes.Add(firstPol.ScaleCaption);
            if (firstPol.Reference is { } reference) notes.Add(reference.Note);
            if (polSet.CoCrossVerdict.Ok) notes.Add(PlanarPolarization.MeshFloorNote);
            foreach (string refusal in polSet.Refusals) notes.Add(refusal);
        }

        return new PlanarSolveResult
        {
            Points        = points,
            CoreFillCount = cores,
            UnknownCount  = mesh.Bases.Count,
            StandardCount = standards,
            CoreBuildMs   = coreBuildMs,
            Notes         = notes,
            CapturedCurrents    = captured,
            CapturedFrequencyHz = capturedF,
            CapturedPortNumber  = captured is null ? 0 : st.CurrentDensityPortNumber,
            Metrics       = metricSet,
            Polarization  = polSet,
            SolvedPointCount          = solvedCount,
            WorstAdaptiveDisagreement = worstAdaptive,
            SolvedFrequencies         = solvedList,
            AdaptiveConverged         = converged,
            AddedFrequencies          = addedList,
            Resonances                = resonances,
            FarField                  = farSet,
        };
    }

    /// <summary>
    /// One solved point's patterns, one per port. <b>Every port, not one</b> — unlike the current
    /// map, whose whole cost is the extra solve it would need; here the solution columns are already
    /// in hand and a pattern is an exact sum over them, so the port axis is free at construction and
    /// is what makes array pattern synthesis possible later. Retro-fitting an axis onto a shipped
    /// cube is not free.
    /// </summary>
    /// <summary>
    /// One frequency's patterns AND the metrics they imply, one of each per port.
    ///
    /// <para><b>ANT-5 — the metric report is built here, beside the pattern, because this is the only
    /// place the four things a metric needs are in hand at once</b>: the pattern, the basis currents
    /// it was transformed from, the RAW port admittance those currents came out of (R-ant-5's
    /// denominator), and the DE-EMBEDDED S of the same point (ANT-12's mismatch factor). Carrying
    /// the pattern out and asking for metrics later would mean re-deciding which admittance belongs
    /// to it.</para>
    ///
    /// <para><b>The two are different ports on purpose and the split is the ANT-12 correction.</b>
    /// Every ratio here — η_rad, D, G — is SCALE-INVARIANT in the excitation and is therefore
    /// weighed against the raw admittance the transformed currents actually came out of. The
    /// mismatch factor is the one quantity that is not such a ratio: it compares an absolute
    /// admittance against Z₀, and at a de-embedded edge port the raw one is the delta gap's own
    /// series parasitic rather than the antenna's input. So it reads the published S and nothing
    /// else.</para>
    /// </summary>
    /// <param name="deembeddedS">The point's published, renormalised s-matrix — <c>S[j, j]</c> is
    /// the reflection a source connected at port <i>j</i>'s own reference plane would see, which is
    /// <see cref="PlanarMetric.RealizedGainDbi"/>'s only input. Null leaves that metric
    /// present-and-refused, exactly as it was before a caller could supply one.</param>
    private static (PlanarFarFieldPattern[] Patterns, PlanarMetricReport[] Metrics,
                    PlanarPolarizationPattern[] Polarization) FarFieldAt(
        PlanarProblem problem, PlanarMesh mesh, IReadOnlyList<Vec<Complex>> currents,
        Mat<Complex> rawY, Mat<Complex>? deembeddedS, IReadOnlyList<PlanarPortResolution> ports,
        double fHz, PlanarFarFieldSettings settings, int? cap, RunControl? control)
    {
        control?.BeginStage($"far field at {SurfaceMesher.Eng(fHz)}Hz", ports.Count);
        var made    = new PlanarFarFieldPattern[ports.Count];
        var metrics = new PlanarMetricReport[ports.Count];
        var pol     = new PlanarPolarizationPattern[ports.Count];
        for (int j = 0; j < ports.Count; j++)
        {
            made[j] = PlanarFarField.Compute(problem, mesh, currents[j], ports[j].Number, fHz,
                                             settings.EffectiveGrid, cap);
            // ANT-6 reads the SAME context ANT-5's metrics do, which is what makes the derived
            // Ludwig-3 reference angle and the derived beamwidth cut one number rather than two.
            var context = new PlanarMetricContext(
                problem, mesh, currents[j], made[j], rawY[j, j], ports[j].Z0,
                settings.EffectiveMetrics, cap,
                deembeddedS is { } sm ? sm[j, j] : null);
            metrics[j] = PlanarMetrics.Evaluate(context);
            pol[j]     = PlanarPolarization.For(context);
            control?.TickStage();
        }
        return (made, metrics, pol);
    }

    /// <summary>
    /// <b>R-prt-15 — σ_max(S) ≤ 1, checked on the answer that ships.</b>
    ///
    /// <para>The root <c>CLAUDE.md</c> has said since L6 that "reciprocity and passivity are gates;
    /// losslessness is NOT", and passivity was gated in the TEST project only. It is the one property
    /// that separates "this kernel is inaccurate here" from "this number cannot be a network", and
    /// the failure it catches is not hypothetical: the owner's Klopfenstein taper shipped a `.s2p`
    /// with σ_max = 1.03 and |S₂₁| = 0.0008 — a fake open circuit — with nothing said about it.
    /// A structure that is genuinely passive cannot exceed 1, so any excess is the ANALYSIS, and the
    /// user is the one who has to know that before reading the plot.</para>
    ///
    /// <para><b>A note carrying the worst measured value, not a refusal.</b> Refusing would throw an
    /// entire sweep away for one bad point, and this area's standing habit is to report the number
    /// rather than the verdict (<c>AsymmetryResidual</c>, <c>ModeCouplingResidual</c>,
    /// <c>FitResidual</c>, <c>CheckFeedClearance</c>). The tolerance is 1e-3 rather than 0 because an
    /// open structure's own discretisation noise lands there and a 1.0000004 would say nothing.</para>
    ///
    /// <para><b>Measured against a UNIFORM REAL reference</b>, which is what <c>RFNetwork.Passivity</c>
    /// requires and what the published matrix is not: per-port Z₀ may differ (a 50 Ω port and a 12 Ω
    /// port is the ordinary taper case) and may be complex. σ_max is reference-dependent, so asking
    /// it of the shipped matrix directly would flag perfectly passive networks.</para>
    /// </summary>
    private static string? PassivityNote(
        IReadOnlyList<PlanarFrequencyPoint> points, IReadOnlyList<Complex> z0)
    {
        const double Tolerance = 1e-3;

        bool uniformReal = true;
        foreach (var z in z0)
            if (z.Imaginary != 0 || z != z0[0]) { uniformReal = false; break; }

        var common = new Complex[z0.Count];
        Array.Fill(common, new Complex(50.0, 0.0));

        double worst = 0, atHz = 0;
        int over = 0;
        foreach (var pt in points)
        {
            double sigma;
            try
            {
                var s = uniformReal ? pt.S : RFNetwork.SToS(pt.S, [.. z0], common);
                sigma = RFNetwork.Passivity(s);
            }
            catch (Exception)
            {
                // A singular renormalisation says nothing about passivity; the point is skipped
                // rather than reported as a violation it was never measured to have.
                continue;
            }

            if (sigma <= 1.0 + Tolerance) continue;
            over++;
            if (sigma > worst) { worst = sigma; atHz = pt.FrequencyHz; }
        }

        if (over == 0) return null;

        return $"NOT PASSIVE: {over} of {points.Count} de-embedded point(s) have σ_max(S) > 1, worst " +
               $"{worst:F4} at {SurfaceMesher.Eng(atHz)}Hz. A passive structure cannot do that, so the " +
               "excess is this analysis, not your design, and the s-parameters at those points should " +
               "not be used. The usual cause is the de-embedding rather than the fill: D6's peel " +
               "divides by a₂₁² (~1e4 at 1 GHz), so a small error in the error box becomes a large one " +
               "in the answer. Check the port notes above for a feed the calibration could not be " +
               "measured on, narrow the sweep to where the two-line calibration is well conditioned, " +
               "or raise Cells per wavelength.";
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
        PlanarProblem problem, IReadOnlyList<PlanarPortResolution> ports,
        SurfaceMesher.PlanarLengthFormat fmt)
    {
        var stack = problem.EffectiveStack;
        double zPort = problem.LevelZ(ports[0].LayerIndex);

        var above = new List<string>();
        for (int i = 0; i < stack.Layers.Count; i++)
            if (stack.InterfaceZ[i] >= zPort - 1e-15)
                above.Add($"{fmt(stack.Layers[i].ThicknessM)} of " +
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
        PlanarProblem problem, PlanarMesh mesh, double fHiHz, PlanarFillSettings? fill = null,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        var notes  = new List<string>();
        double lam = EmConstants.C0 / fHiHz;
        var    fmt = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;

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
                    $"This structure's vertical (via) current spans {fmt(extent)} " +
                    $"between its most distant VIA FOOTPRINT cells, which at " +
                    $"{SurfaceMesher.Eng(fHiHz)}Hz is ρ/λ = {extent / lam:G3}. This is a separation " +
                    $"between VIAS, not the size of the board: the mesh itself is " +
                    $"{fmt(Diagonal(mesh))} across and that is NOT what is refused " +
                    $"here. Bringing the vias closer together, or lowering the sweep's top, acts on " +
                    $"this; shrinking the surrounding metal does not. Alternatively set " +
                    $"PlanarFillSettings.DirectVerticalKernel, which replaces the FIT with direct " +
                    $"Sommerfeld integration for this one block — accurate at any separation, and " +
                    $"far slower (see M2's own cost measurement). " + range.Reason), notes);

            else notes.Add(
                $"G_A^zz's range was checked over the via footprints ({fmt(extent)}, " +
                $"ρ/λ = {extent / lam:G3}) rather than over the whole mesh " +
                $"({fmt(Diagonal(mesh))}, ρ/λ = {Diagonal(mesh) / lam:G3}) — that " +
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

        // ── MIM-3 — the CELL against the LEVEL SEPARATION. A note, never a refusal ────────────
        notes.AddRange(LevelSeparationNotes(problem, mesh, fHiHz, fmt));

        return (EmSuitability.Yes, notes);
    }

    /// <summary>
    /// <b>MIM-3 — is any pair of conductor levels closer together than the cells that straddle
    /// them can resolve?</b> A NOTE, never a refusal, for R-prt-13's reason: the answer is still
    /// produced, it is still reciprocal and passive, and what is unreliable is a MAGNITUDE. A
    /// refusal would also take away the many multi-level runs where the ratio is fine.
    ///
    /// <para>The quantity is asked per ADJACENT LEVEL PAIR and over the cells that actually sit on
    /// those two levels, because that is the only place the cross-level block is evaluated —
    /// R-zz-1's own discipline. Reporting the mesh's largest cell anywhere would grade a plate pair
    /// on a cell belonging to some unrelated wide conductor.</para>
    ///
    /// <para><b>The remedy names the binding quantity</b> (§3.5's own trap, and the reason
    /// <c>BuildRefusal</c> asks <c>waveBinds</c>): the cell size is
    /// <c>min(λ_g/CellsPerWavelength, width/MinCellsAcrossConductor)</c>, and only the FIRST term
    /// responds to the two frequency knobs. Where the second wins — which on a plate small enough
    /// to sit this close to another level it always does by orders of magnitude — lowering "cells
    /// per wavelength" or the mesh frequency changes nothing at all.
    ///
    /// <para><b>And it is DECIDED rather than hedged, by turning the knob round.</b> The note
    /// reports how large <c>CellsPerWavelength</c> would have to be for the λ_g term to reach the
    /// pitch this mesh already has: <c>λ_g / cell</c> at the sweep's top, with λ_g taken at the
    /// stack's largest εᵣ. On a plate small enough to sit this close to another level that number
    /// runs to thousands, and quoting it is what lets the note say the frequency knobs do not act
    /// here without asserting anything it has not computed.</para></para>
    /// </summary>
    public static List<string> LevelSeparationNotes(
        PlanarProblem problem, PlanarMesh mesh, double fHiHz,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        var notes = new List<string>();
        var fmt   = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        var levels = PlanarLevels.From(problem);
        if (levels.Z.Count < 2) return notes;

        double worstRatio = 0, worstSep = 0, worstCell = 0;
        int worstLo = -1;

        for (int lo = 0; lo + 1 < levels.Z.Count; lo++)
        {
            double sep = Math.Abs(levels.Z[lo + 1] - levels.Z[lo]);
            if (!(sep > 0)) continue;

            double cell = 0;
            foreach (var c in mesh.Cells)
            {
                if (c.LayerIndex != lo && c.LayerIndex != lo + 1) continue;
                cell = Math.Max(cell, Math.Max(c.Width, c.Height));
            }
            if (!(cell > 0)) continue;

            double ratio = cell / sep;
            if (ratio <= worstRatio) continue;
            worstRatio = ratio; worstSep = sep; worstCell = cell; worstLo = lo;
        }

        if (worstLo < 0) return notes;

        // The two frequency knobs reach the cell size ONLY through the λ_g/CellsPerWavelength cap,
        // so the question "do they act here" has an arithmetic answer: what would CellsPerWavelength
        // have to be for that cap to equal the pitch this mesh already has? λ_g at the stack's
        // largest εᵣ is the shortest guided wavelength anywhere in it, i.e. the most generous form of
        // the question. (§3.5's trap is naming a remedy without asking whether it BINDS; this is the
        // asking.)
        double epsMax = 1.0;
        foreach (var l in problem.EffectiveStack.Layers) epsMax = Math.Max(epsMax, l.Material.EpsR);
        double lambdaG = fHiHz > 0 ? EmConstants.C0 / (fHiHz * Math.Sqrt(epsMax)) : double.NaN;
        double cellsPerWavelengthNeeded = lambdaG / worstCell;

        string where = $"levels {worstLo} and {worstLo + 1} ({fmt(worstSep)} apart, largest " +
                       $"straddling cell {fmt(worstCell)})";

        if (worstRatio <= PlanarLevels.ValidatedCellOverSeparation)
        {
            notes.Add(
                $"The closest conductor levels are resolved by the mesh: cell/separation = " +
                $"{worstRatio:G3} at {where}, inside the " +
                $"{PlanarLevels.ValidatedCellOverSeparation} MIM-3 measured the cross-level fill " +
                $"over (≤ 4.1e-3 against forced-high quadrature; the extracted plate capacitance " +
                $"within 10% of ε₀εᵣA/d).");
            return notes;
        }

        notes.Add(
            $"CELL/SEPARATION = {worstRatio:G3} at {where}, PAST the " +
            $"{PlanarLevels.ValidatedCellOverSeparation} the cross-level fill is measured over. " +
            $"MIM-3 measured the cross-level matrix block against forced-high quadrature at " +
            $"2.3e-7 / 4.1e-3 / 3.9e-2 / 1.5e-1 for cell/separation of 1 / 5 / 10 / 20 — four " +
            $"decades of it, steepest at the bottom — and the capacitance extracted from a plate " +
            $"pair follows it: within 10% of " +
            $"ε₀εᵣA/d up to 5, 1.46× at 12.5, and the WRONG SIGN at 25. The KERNEL is not the " +
            $"problem (it is flat in the separation down to 0.05 µm); the quadrature is, because a " +
            $"cross-level entry carries a peak of width {fmt(worstSep)} inside a cell of " +
            $"{fmt(worstCell)}. Nothing downstream shows it: reciprocity and passivity hold " +
            $"throughout. What acts on this is the CELL PITCH across the metal on those two levels. " +
            "That pitch is min(λ_g/CellsPerWavelength, width/MinCellsAcrossConductor), " +
            $"and only the first term responds to the frequency knobs" +
            (double.IsFinite(cellsPerWavelengthNeeded) && cellsPerWavelengthNeeded > 200
                ? $" — and here it would take Cells per wavelength ≥ {cellsPerWavelengthNeeded:N0} " +
                  $"(or the mesh frequency raised by that factor) before that term even reaches the " +
                  $"{fmt(worstCell)} this mesh already has, which is far past the unknown ceiling " +
                  $"this kernel solves under. At any usable setting the second term binds, and it " +
                  $"is the metal's own width — so neither frequency knob acts here. "
                : $"; where the second term binds — the metal's own width — neither frequency knob " +
                  $"acts at all. ") +
            $"Coupling " +
            $"between these two levels — a thin-film capacitor's plate capacitance above all — is " +
            $"the part of this answer to distrust; everything on a single level is unaffected.");
        return notes;
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
