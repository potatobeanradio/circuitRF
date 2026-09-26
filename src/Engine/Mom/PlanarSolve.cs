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
using System.Globalization;
using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>LF3 — a de-embedding ceiling the ACCELERATOR would clear, raised so the caller can act on it
/// rather than a user being told to go and turn a switch on.</b>
///
/// <para>Thrown only in the one case that is recoverable without changing an answer: the run is
/// dense, one standard is past the dense ceiling, and it is inside the accelerated one. Every other
/// ceiling case stays the ordinary refusal it was, because there is nothing to recover with.
/// <c>PlanarKernel.Solve</c> is what catches it — it owns the settings — and re-runs once with the
/// accelerator on. This type exists so that retry is keyed on the ONE recoverable case rather than
/// on matching a sentence.</para>
/// </summary>
public sealed class PlanarAcceleratorWouldFitException(int portNumber, int standardUnknowns,
                                                       string message) : Exception(message)
{
    public int PortNumber       { get; } = portNumber;
    public int StandardUnknowns { get; } = standardUnknowns;
}

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
    ///
    /// <para><b>And this is the ONE place the sampling path is widened for a low frequency</b>
    /// (<see cref="Dcim.ForStackAtFrequency"/>). Every fit in the application arrives here — the
    /// sweep, the adaptive sweep's own probes, the resonance search, the current-density recompute —
    /// so putting it anywhere else would leave one of them fitting a path that cannot see the stack,
    /// silently and only at the bottom of a band. It is a no-op at every frequency where the default
    /// already reaches <see cref="Dcim.CalibratedPathProduct"/>, which is what keeps §L8/§L9's
    /// recorded numbers bit-identical.</para>
    /// </summary>
    public static PlanarFrequencyKernel Fit(
        PlanarProblem problem, double fHz,
        PlanarExtractionOrder order = PlanarExtractionOrder.Constant, DcimSettings? dcim = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        dcim = Dcim.ForStackAtFrequency(dcim, 2.0 * Math.PI * fHz / EmConstants.C0,
                                        problem.RequiresGeneralKernel ? problem.EffectiveStack.TopZ
                                                                      : problem.Slab.HeightM);
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
/// <summary>
/// <b>PCAL4 — one calibration GROUP's answer at one frequency</b>: the modal error box, each mode's
/// Z_c, and the transformation back to the terminal ports the user asked for.
/// </summary>
/// <param name="Box">The N×N error box, in the modal basis whose gauge
/// <see cref="PlanarModalCalibration"/> fixed and whose SIGNS it agreed with <paramref name="Tv"/>
/// about.</param>
/// <param name="Zc">Z_c,m per mode — D7's γ/(jωC) with a modal C.</param>
/// <param name="Tv">The voltage modal matrix: column m is mode m's terminal voltage pattern.</param>
/// <param name="ModeCouplingResidual">What the lossless modal reduction discarded in [C].</param>
/// <param name="Usable">Whether every mode's βΔℓ is inside TRL's usable interval at this
/// frequency — one mode outside it makes the whole group's box unusable, because the box is
/// solved from all of them at once.</param>
public sealed record PlanarGroupCalibration(
    PlanarModalErrorBox    Box,
    IReadOnlyList<Complex> Zc,
    Mat<double>            Tv,
    IReadOnlyList<double>  ReportedZcScale,
    double                 ModeCouplingResidual,
    bool                   Usable);

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

    // ── QSC — which separations are measured, which one is the short quasi-static one, and the
    //    crossover they were drawn at. Built once in the constructor from the port and the band;
    //    `_plan.IsQuasiStaticAt(f)` is the ONE place a frequency's path is decided. ─────────────
    private readonly PlanarCalibration.PlanarSeparationPlan _plan;
    private PlanarQuasiStaticLine? _qsLine;

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
        // CL4 — a CONDUCTING floor is the slab's floor too. The one-slab image series is
        // electrostatics, where a plane of any finite σ is an equipotential exactly as a PEC is
        // (LayeredStaticGreens.TerminationCoefficient), so asking Kind == Pec here would have
        // quietly moved a lossy-ground run onto the interior route and changed the reference
        // impedance of a calibration that reads no termination at all. R-cl4-1's second half.
        //
        // CL7 — and it has to be the SAME floor, not merely a conducting one. CL6 gave GroundedSlab
        // a floor of its own, so "is this stack the slab" stopped being answerable without comparing
        // it: a stack with a copper floor and a slab with a PEC one are two different electrostatic
        // problems, and answering yes would put one's reference impedance on the other's image
        // series. Nothing shipped can build that pair — the extractor writes ONE floor into both —
        // which is exactly why it has to be asserted here rather than relied on.
        if (!stack.Bottom.IsConductor) return false;
        if (!SameFloor(stack.Bottom, slab.Floor)) return false;
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

    /// <summary>
    /// <b>Two terminations that are the same GROUND.</b> Not record equality: every PERFECT spelling
    /// of a conducting plane is the same physical floor as a PEC and is bit-identical to it in both
    /// kernels (CL4 §1, CL6 §2), so treating them as different would split the one-slab route on a
    /// distinction that does not exist in the arithmetic.
    /// </summary>
    private static bool SameFloor(Termination a, Termination b)
    {
        bool pa = a.Kind != TerminationKind.SurfaceImpedance ||
                  PlanarSurfaceImpedance.IsPerfect(a.ConductivitySm, a.ThicknessM);
        bool pb = b.Kind != TerminationKind.SurfaceImpedance ||
                  PlanarSurfaceImpedance.IsPerfect(b.ConductivitySm, b.ThicknessM);
        if (pa || pb) return pa && pb;
        return a.ConductivitySm.Equals(b.ConductivitySm) && a.ThicknessM.Equals(b.ThicknessM);
    }

    /// <param name="standardLayerName">
    /// <b>CL1 — the PROBLEM's name for the conductor level this port sits on.</b> A standard's mesh
    /// numbers its one level 0 whatever level the port is on, so the fill resolves its metal by NAME
    /// (<see cref="PlanarConductorLoss.SheetTable"/>) and falls back to index 0; left at the
    /// placeholder on a multi-level problem, every standard is filled with level 0's σ and thickness.
    /// The default is bit-identical on a single-level problem, where both routes give index 0.
    /// </param>
    /// <param name="separations">
    /// <b>QSC — the A-vs-B seam, and the ONLY way to make a sub-crossover point take the measured
    /// two-line path.</b> Null draws the plan from the band, which is what
    /// <see cref="PlanarSolve"/> always does and therefore what every run does. It exists because
    /// the overlap gate has to compare the two paths at the SAME frequency on the SAME meshes, and
    /// there is no other way to ask for that; it is deliberately not on
    /// <see cref="PlanarCalibrationSettings"/>, not in the <c>.cem</c> and not reachable from the
    /// panel — the crossover is measured and fixed per stack, exactly as <c>UseRadialTable</c> and
    /// <c>UseSymmetricFactorization</c> are reachable as oracles and are not user controls.
    /// <b>A plan passed here must have built <paramref name="standards"/>.</b>
    /// </param>
    public PlanarPortCalibrator(PlanarPortResolution port, GroundedSlab slab,
                                double fLoHz, double fHiHz,
                                PlanarCalibrationSettings? calibration = null,
                                PlanarFillSettings? fill = null,
                                double standardLevelZ = double.NaN,
                                IReadOnlyList<PlanarStandard>? standards = null,
                                LayerStack? mediumStack = null,
                                PlanarCalibration.PlanarSeparationPlan? separations = null,
                                string standardLayerName = PlanarCalibration.DefaultLayerName)
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

        // QSC — drawn from the port and the band before anything is meshed, because it decides
        // WHICH standards exist. A caller that supplied its own set (PlanarSolve does, so the
        // ceiling refusal can size the meshes before paying for them) built it from BuildSet, which
        // asks this same function with these same arguments.
        _plan = separations ?? PlanarCalibration.SeparationPlan(slab, fLoHz, fHiHz, port, calibration);

        var set = standards is null
                ? PlanarCalibration.BuildSet(port, slab, _plan,
                                             PlanarCalibration.SuggestLengths(slab, fLoHz, fHiHz, calibration).Short,
                                             calibration, standardLayerName)
                : [.. standards];
        Standards = set;

        _standards   = new PlanarSolveContext[set.Length];
        for (int i = 0; i < set.Length; i++)
            _standards[i] = new PlanarSolveContext(set[i].Mesh, set[i].Ports, fill, _standardLevels,
                                                   slab.HeightM);

        _shortLength = set[0].LengthM;
        _deltas      = new double[set.Length - 1];
        for (int i = 1; i < set.Length; i++) _deltas[i - 1] = set[i].LengthM - set[0].LengthM;

        // PCAL3 — a widened standard's neighbour spans the standard's WHOLE meshed length, open at
        // both ends, so its own half-wave resonances are at βL = nπ. The span is a property of the
        // mesh and is taken once here; whether a frequency is near one is asked per frequency, from
        // the MEASURED β rather than from the pre-solve estimate.
        // PCAL4 — a group's standards carry 2N ports and the run's port numbers ride on the
        // profile, in the standard's own conductor order.
        _groupPorts     = set[0].IsGroup ? port.Group!.PortNumbers : null;

        _hasNeighbour   = port.Neighbourhood is not null;
        _standardSpanM  = new double[set.Length];
        for (int i = 0; i < set.Length; i++)
        {
            var g = port.Direction == PlanarBasisDirection.X ? set[i].Mesh.GridX : set[i].Mesh.GridY;
            _standardSpanM[i] = g[^1] - g[0];
        }
    }

    private readonly bool     _hasNeighbour;
    private readonly double[] _standardSpanM;

    // ── PCAL4 — the group's own state. Null on every ordinary calibrator, which is every one that
    //    exists on a run that passes today. ──────────────────────────────────────────────────────
    private readonly IReadOnlyList<int>? _groupPorts;
    private PlanarModalMedium?           _medium;
    private Complex[]?                   _prevGamma;

    /// <summary>PCAL4 — does this calibrator own a multi-conductor group's standard set?</summary>
    public bool IsGroup => _groupPorts is not null;

    /// <summary>The SHORT standard's realised plane-to-plane length. PCAL6 reports it on the
    /// mode-separation refusal, because it is the quantity that refusal is actually about.</summary>
    public double ShortLengthM => _shortLength;

    /// <summary>PCAL4 — the run's ports this calibrator covers, in its standards' conductor
    /// order.</summary>
    public IReadOnlyList<int> GroupPortNumbers => _groupPorts ?? [];

    /// <summary>
    /// <b>PCAL4 — the quasi-static modal description of the group's cross-section</b>, from its own
    /// two extreme standards. Frequency-independent, so it is built once and kept, exactly as D7's
    /// scalar <c>C_pul</c> is; and like it, this is what builds the LONGEST standard's cores even
    /// when no frequency selected it.
    /// </summary>
    private PlanarModalMedium Medium() =>
        _medium ??= PlanarModalMedium.Extract(Standards[0], Standards[^1], _slab,
                                              _standards[0].Settings,
                                              _standards[0].Cores, _standards[^1].Cores);

    /// <summary>
    /// <b>PCAL4/R-pcal4-6 — the group's mode separation at one frequency, from the ELECTROSTATICS,
    /// before any Green's-function fit.</b> The smallest |β_i − β_j|·Δℓ over mode pairs, in degrees,
    /// over the separation that frequency would select. It is the quantity the measured
    /// <see cref="PlanarModalErrorBox.ModeSeparationDegrees"/> reports, asked of the quasi-static
    /// modes instead — available at setup, so a group that cannot be calibrated is refused before
    /// the sweep is paid for rather than in the middle of it.
    /// </summary>
    public double QuasiStaticModeSeparationDegrees(double fHz)
    {
        var medium = Medium();
        int n = medium.ModeCount;
        if (n < 2) return double.PositiveInfinity;

        double dl = _deltas[SelectedIndexAt(fHz)];

        double worst = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                worst = Math.Min(worst,
                    Math.Abs(medium.Beta(fHz, i) - medium.Beta(fHz, j)) * dl * 180.0 / Math.PI);
        return worst;
    }

    /// <summary>PCAL4 — the group's quasi-static modal description, for a caller that wants to
    /// report ε_eff per mode or the reference impedance's own accuracy separately (R-pcal4-4).</summary>
    public PlanarModalMedium GroupMedium() => Medium();

    /// <summary>
    /// <b>PCAL4 — γ per mode, the modal error box and each mode's Z_c at one frequency.</b> The
    /// group's analogue of <see cref="At"/>, and stateful in the same way and for the same reason:
    /// the 2π branch of every mode is continued from the previous point, and near a mode crossing
    /// the assignment to modes is made against that prediction rather than against an ordering
    /// (R-pcal4-3).
    /// </summary>
    public PlanarGroupCalibration ModalAt(Func<PlanarFrequencyKernel> kernelFor, double fHz)
    {
        ArgumentNullException.ThrowIfNull(kernelFor);
        if (_groupPorts is null)
            throw new InvalidOperationException("This calibrator owns no calibration group.");

        if (PrepareAt(kernelFor, fHz) is { } work)
        {
            foreach (var solve in work.Solves) solve();
            work.Commit();
        }

        int pick = SelectedIndexAt(fHz);
        var slots  = _rawCache[fHz];
        var sShort = slots[0]!.Value;
        var sLong  = slots[pick + 1]!.Value;

        var medium = Medium();
        int n = medium.ModeCount;
        double dl = _deltas[pick];

        // The prediction: the previous frequency's own β per mode scaled by frequency, or the
        // electrostatic estimate at the first point. It is the ONLY thing that assigns a measured
        // mode to a mode of the cross-section, so it is per mode and never an average.
        var expect = new double[n];
        for (int m = 0; m < n; m++)
            expect[m] = (_prevGamma is null ? medium.Beta(fHz, m)
                                            : _prevGamma[m].Imaginary * (fHz / _prevF)) * dl;

        var box = PlanarModalCalibration.Solve(sShort, sLong, _shortLength, _shortLength + dl,
                                               expect, medium.Tv);

        _prevGamma = [.. box.Gamma];
        _prevF     = fHz;

        // SelectSeparation and the branch prediction below both need ONE β for the group; the mean
        // over modes is what they get, and it only has to be right to ~20% for either.
        double mean = 0;
        for (int m = 0; m < n; m++) mean += box.Gamma[m].Imaginary;
        _prevBeta = mean / n;

        var zc = new Complex[n];
        bool usable = true;
        for (int m = 0; m < n; m++)
        {
            zc[m] = medium.Zc(box.Gamma[m], fHz, m);
            usable &= box.ElectricalDegrees[m] >= PlanarCalibrationSettings.UsableLoDegrees
                   && box.ElectricalDegrees[m] <= PlanarCalibrationSettings.UsableHiDegrees;
        }

        return new PlanarGroupCalibration(box, zc, medium.Tv,
                                          medium.ReportedZcScale, medium.ModeCouplingResidual, usable);
    }

    /// <summary>
    /// <b>PCAL3 — how far βL is from the nearest nπ, in degrees, over the two standards this
    /// frequency reads.</b> NaN when the standard reproduces no neighbour, which is every port whose
    /// feed was clear and therefore every run that passed before PCAL3.
    /// </summary>
    private double NeighbourResonanceDegrees(double beta, int pick)
    {
        if (!_hasNeighbour || !(beta > 0)) return double.NaN;
        double worst = double.PositiveInfinity;
        foreach (int i in (int[])[0, pick + 1])
        {
            double theta = beta * _standardSpanM[i];

            // n = 0 is NOT a resonance and the distinction is not pedantry: below its first half
            // wave a standard's βL passes through every small angle on the way up, and a window
            // around nπ that admits n = 0 flags the whole bottom of every sweep — on the series'
            // own fixture it flagged 1 GHz, where the answer is at the floor.
            double n = Math.Round(theta / Math.PI);
            if (n < 1) continue;

            double d = Math.Abs(theta - n * Math.PI) * 180.0 / Math.PI;
            worst = Math.Min(worst, d);
        }
        return worst;
    }

    /// <summary>
    /// <b>How close to its own resonance a reproduced neighbour may be before the point is flagged —
    /// 25°, and it is measured rather than chosen for roundness.</b> On the series' own fixture the
    /// error is at the A-vs-B floor (|ΔS| ≈ 0.04 against a floor of 0.052) out to 27° from nπ and
    /// climbs to 0.07 at 20°, 0.145 at 6° and 0.22 at the resonance itself.
    /// </summary>
    public const double NeighbourResonanceGuardDegrees = 25.0;

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
        int    pick   = SelectedIndexAt(fHz);

        var slots  = _rawCache[fHz];
        var sShort = slots[0]!.Value;              // Mat<T> is a struct, so these are Nullable<Mat<T>>
        var sLong  = slots[pick + 1]!.Value;

        // ── QSC — γ'S SOURCE IS THE ONLY THING THAT CHANGES BELOW THE CROSSOVER ─────────────
        //
        // The error box is still solved from TWO standards, because it has to be: one line gives
        // two complex equations for three complex unknowns and no symmetry argument closes that.
        // What a supplied γ buys is that Δℓ need not be electrically long, which is what makes the
        // two standards down here small and frequency-independent (SeparationPlan).
        bool quasi = _plan.IsQuasiStaticAt(fHz);

        // Selected here rather than inside GammaBest, because PrepareAt has already solved exactly
        // these two meshes and no others — asking GammaBest to re-select would mean handing it an
        // array that is null everywhere except at `pick`. Same rule, same arithmetic, asked once.
        var g = quasi
            ? QuasiStaticLine().ResultAt(fHz, _deltas[pick])
            : PlanarCalibration.Gamma(sShort, sLong, _deltas[pick], expect * _deltas[pick]);

        // The branch continuation is stepped on BOTH paths. It is what the first measured point
        // above the crossover predicts from, and the quasi-static β is a better prediction than
        // EstimateBeta's own 15-20 %-low estimate would be — so a sweep that crosses the crossover
        // hands the measured path a seed it could not otherwise have.
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
        // QSC — on the quasi-static path the dielectric-filled differencing has ALREADY been done,
        // by QuasiStaticLine() above, and its real part IS this number bit for bit
        // (CapacitancePerMetre is CapacitancePerMetreComplex(...).Real). Reading it back rather than
        // re-solving is what keeps the quasi-static route's extra cost to the AIR-FILLED half.
        if (double.IsNaN(_cPerMetre) && quasi) _cPerMetre = QuasiStaticLine().CPerMetre;

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
            PlanarDeembed.CharacteristicImpedance(g.Gamma, _cPerMetre, fHz), _cPerMetre,
            NeighbourResonanceDegrees(g.Beta, pick),
            quasi ? PlanarCalibrationSource.QuasiStatic : PlanarCalibrationSource.Measured);
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
    private int[] NeededAt(double fHz) => [0, 1 + SelectedIndexAt(fHz)];

    /// <summary>
    /// <b>PCAL6/R-pcal6-3 — THE ONE PLACE THAT DECIDES WHICH SEPARATION A FREQUENCY USES.</b>
    /// <see cref="QuasiStaticModeSeparationDegrees"/>, <see cref="ModalAt"/>, <see cref="At"/> and
    /// <see cref="NeededAt"/> all ask it, and <see cref="NeededAt"/> is the one that matters: it
    /// decides which standard meshes are SOLVED at all. A rule reachable from three of the four
    /// means a run solves one standard and calibrates against another, silently — and until PCAL6
    /// the setup guard genuinely did ask a different question from the sweep, because it predicted β
    /// from <see cref="PlanarCalibration.EstimateBeta"/> while the sweep predicted it from the
    /// previous point.
    ///
    /// <para><b>PCAL6/R-pcal6-4 — a GROUP's choice does not depend on the frequencies before
    /// it.</b> It is made on the quasi-static modal β, which is a property of the group's own
    /// cross-section and of this frequency alone: <see cref="PlanarModalMedium"/> is built once from
    /// the two extreme standards' electrostatics, costs no Green's-function fit, and PCAL6/M1
    /// measured it against the full-wave answer at 0.1-0.8 % over 200 MHz - 1 GHz — far better than
    /// the ±20 % the choice needs and better than <see cref="PlanarCalibration.EstimateBeta"/>'s own
    /// 15-20 %. The mean over modes is what is handed over, exactly as
    /// <see cref="ModalAt"/> already averages for the branch prediction.</para>
    ///
    /// <para><b>A port that is not in a group keeps <see cref="ExpectedBeta"/>, unchanged</b> — that
    /// is the whole of a single-port calibration's selection and it is outside this brief's scope,
    /// so every ungrouped run is bit-identical by construction rather than by measurement. Its
    /// history dependence is real, is what §5 describes, and is recorded rather than fixed here.</para>
    ///
    /// <para>The BRANCH continuation is a different question and stays on
    /// <see cref="ExpectedBeta"/>: it is a continuation by definition and has no history-free
    /// spelling (L9e/M1's own note says why predicting it from the pre-solve estimate is a coin flip
    /// on the 2π branch).</para>
    /// </summary>
    public int SelectedIndexAt(double fHz)
    {
        // ── QSC — BELOW THE CROSSOVER THERE IS NOTHING TO SELECT ────────────────────────────
        //
        // The selection exists to put βΔℓ near the middle of TRL's usable interval, which is a
        // constraint on the γ EXTRACTION and nothing else. With γ supplied quasi-statically there
        // is one separation down here by construction (PlanarCalibration.SeparationPlan), sized
        // from the substrate rather than from λ, and asking SelectSeparation would hand it the
        // measured ladder's entries as candidates — the long standards this path exists to avoid
        // building, let alone solving.
        if (_plan.IsQuasiStaticAt(fHz)) return _plan.QuasiStaticIndex;

        // …and above it, the candidates are the MEASURED separations only. The quasi-static one is
        // the last entry and is a fraction of a degree up there, so leaving it in the list would
        // let a frequency just above the crossover score it as "closest to the interval's centre"
        // on a log measure and calibrate against a standard with no phase in it.
        int n = _plan.QuasiStaticIndex >= 0 ? _plan.QuasiStaticIndex : _deltas.Length;
        var measured = n == _deltas.Length ? _deltas : _deltas[..n];

        if (_groupPorts is null)
            return PlanarCalibration.SelectSeparation(measured, ExpectedBeta(fHz));

        var medium = Medium();
        double mean = 0;
        for (int m = 0; m < medium.ModeCount; m++) mean += medium.Beta(fHz, m);
        return PlanarCalibration.SelectSeparation(measured, mean / medium.ModeCount);
    }

    /// <summary>
    /// <b>QSC — the two standards' quasi-TEM description, built once and reused</b>, exactly as D7's
    /// scalar C_pul is and for the same reason: [C] and [C₀] are frequency-independent (R-mom-11).
    /// It costs the air-filled electrostatic solve of both extreme standards; the dielectric-filled
    /// half is the solve <see cref="At(Func{PlanarFrequencyKernel}, double, int)"/> already owes for
    /// C_pul, and is not paid twice.
    /// </summary>
    public PlanarQuasiStaticLine QuasiStaticLine() =>
        _qsLine ??= _interiorStack is { } medium
            ? PlanarQuasiStaticLine.Extract(Standards[0], Standards[^1], medium, _standardLevelZ,
                                            _standardLevelZ - medium.InterfaceZ[0],
                                            _standards[0].Settings,
                                            _standards[0].Cores, _standards[^1].Cores,
                                            _interiorModel)
            : PlanarQuasiStaticLine.Extract(Standards[0], Standards[^1], _slab,
                                            _standards[0].Settings,
                                            _standards[0].Cores, _standards[^1].Cores);

    /// <summary><b>QSC — the crossover this calibrator's plan was drawn at</b>, so a caller can
    /// report which path each point took without recomputing it.</summary>
    public double QuasiStaticCrossoverHz => _plan.CrossoverHz;

    /// <summary>
    /// <b>CL7 — whether the supplied γ carries the GROUND PLANE's term.</b> True on the one-slab
    /// electrostatic route with a conducting floor, which is what an ordinary microstrip gets;
    /// <see cref="QuasiStaticGroundTermMissing"/> is the case where the floor conducts and this route
    /// cannot read it. Asked WITHOUT extracting the line, so a run that never reaches the
    /// quasi-static path pays nothing to report on it.
    /// </summary>
    public bool QuasiStaticGroundTermSupplied =>
        _interiorStack is null && Conducting(_slab.Floor);

    /// <summary>
    /// <b>CL7 — the floor conducts and this route cannot supply its term, so the residue is
    /// REPORTED rather than assumed away.</b> MIM-4's interior route puts the standard on a level
    /// inside a stratified stack, which is not one horizontal current one image-height above one
    /// plane — see <see cref="PlanarGroundReturn"/>'s header. Below the crossover such a run's γ
    /// carries the strip's conductor term and not the plane's.
    /// </summary>
    public bool QuasiStaticGroundTermMissing =>
        _interiorStack is { } st && Conducting(st.Bottom);

    private static bool Conducting(Termination t) =>
        t.Kind == TerminationKind.SurfaceImpedance &&
        !PlanarSurfaceImpedance.IsPerfect(t.ConductivitySm, t.ThicknessM);

    /// <summary>Whether this frequency's γ comes from the quasi-static line rather than from D5's
    /// two-line extraction. <b>The one question, asked in one place</b> — see
    /// <see cref="SelectedIndexAt"/>.</summary>
    public bool IsQuasiStaticAt(double fHz) => _plan.IsQuasiStaticAt(fHz);

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
        _prevBeta  = double.NaN;
        _prevF     = 0;
        _prevA21   = null;
        _prevGamma = null;
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

        // ── PCAL4 — TWO PORTS OF ONE GROUP SHARE ONE STANDARD, BY CONSTRUCTION ──────────────────
        //
        // A group's standard is built from the GROUP's profile, which is the same object for every
        // member, so the members' OWN conductors need not match at all — on an asymmetric pair they
        // legitimately do not, and comparing them would build the identical 2N-port mesh once per
        // port and solve it once per port at every frequency.
        if (a.Group is { } ag && b.Group is { } bg &&
            ag.PortNumbers.Count == bg.PortNumbers.Count &&
            ag.ConductorOf.Count == bg.ConductorOf.Count)
        {
            bool sameGroup = true;
            for (int i = 0; i < ag.PortNumbers.Count && sameGroup; i++)
                if (ag.PortNumbers[i] != bg.PortNumbers[i]) sameGroup = false;
            for (int i = 0; i < ag.ConductorOf.Count && sameGroup; i++)
            {
                if (ag.ConductorOf[i] != bg.ConductorOf[i]) { sameGroup = false; break; }
                double da = ag.Lines[i + 1] - ag.Lines[i];
                double db = bg.Lines[i + 1] - bg.Lines[i];
                if (Math.Abs(da - db) > Tol * Math.Max(da, db)) sameGroup = false;
            }
            if (sameGroup) return true;
        }

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

        // ── PCAL3 — AND ONLY IF THEY SHARE THE WIDENED PROFILE TOO ──────────────────────────────
        //
        // Same sentence as RP-2c's directly above, one conductor over: two ports whose own runs match
        // cell for cell can have neighbours at different distances, or one neighbour and two. Sharing
        // a standard across that would calibrate port 2 against port 1's neighbourhood — a complete,
        // plausible error box for a structure that is not there.
        if ((a.Neighbourhood is null) != (b.Neighbourhood is null)) return false;
        if (a.Neighbourhood is { } na && b.Neighbourhood is { } nbb)
        {
            if (na.IsMetal.Count != nbb.IsMetal.Count) return false;
            if (na.OwnLo != nbb.OwnLo || na.OwnHi != nbb.OwnHi) return false;
            for (int i = 0; i < na.IsMetal.Count; i++)
            {
                if (na.IsMetal[i] != nbb.IsMetal[i]) return false;
                double da = na.Lines[i + 1] - na.Lines[i];
                double db = nbb.Lines[i + 1] - nbb.Lines[i];
                if (Math.Abs(da - db) > Tol * Math.Max(da, db)) return false;
            }
        }

        // ── PCAL4 — AND ONLY IF THEY ARE THE SAME SHAPE OF GROUP ────────────────────────────────
        //
        // The same sentence again, N conductors over. Two groups share a standard only if they have
        // the same conductors at the same spacings in the same order; sharing across that would
        // calibrate one group against another's cross-section, which is a complete, plausible modal
        // error box for a port region that is not there. The two ends of one coupled pair DO match,
        // which is what makes a four-port run cost two group standards rather than four.
        if ((a.Group is null) != (b.Group is null)) return false;
        if (a.Group is { } ga && b.Group is { } gb)
        {
            if (ga.ConductorOf.Count != gb.ConductorOf.Count) return false;
            if (ga.ConductorCount != gb.ConductorCount) return false;
            for (int i = 0; i < ga.ConductorOf.Count; i++)
            {
                if (ga.ConductorOf[i] != gb.ConductorOf[i]) return false;
                double da = ga.Lines[i + 1] - ga.Lines[i];
                double db = gb.Lines[i + 1] - gb.Lines[i];
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
/// <b>R-pcal7-4 — one de-embedded point that came back non-passive</b>, and by how much.
/// σ_max &gt; 1 is not a degraded answer; it is not a network at all, so the excess is a property of
/// the ANALYSIS. Carried on the result rather than only written into a note, because the note does
/// not survive onto the <c>.sNp</c> and the file is what a reader opens six months later (PCAL2).
/// </summary>
public sealed record PlanarPassivityExcess(double FrequencyHz, double SigmaMax);

/// <summary>
/// <b>MIM-3 / MIM-8 / MIM-9 — one adjacent pair of conductor levels, and the largest mesh cell that
/// straddles them.</b> <see cref="CellOverSeparation"/> is the quantity both bounds are drawn on:
/// <see cref="PlanarLevels.FullWaveCellOverSeparation"/> (a refusal, on the de-embedded two-port)
/// and <see cref="PlanarLevels.ValidatedCellOverSeparation"/> (a note, on the cross-level fill and
/// the electrostatic plate capacitance). Those are different quantities measured over different
/// ranges, which is why there are two constants and why a run past the first is refused while a run
/// past only the second is not.
/// </summary>
/// <param name="Lower">Index of the lower of the two levels; the upper is <c>Lower + 1</c>.</param>
/// <param name="SeparationM">Their vertical separation, metres.</param>
/// <param name="CellM">The largest cell dimension over the two levels' own cells, metres — never
/// the mesh's largest cell anywhere, which would grade a plate pair on an unrelated conductor.</param>
public readonly record struct PlanarLevelPair(int Lower, double SeparationM, double CellM)
{
    public double CellOverSeparation => CellM / SeparationM;
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
    /// <summary>EM-SEV R-emsev-1: the sweep's own findings, each carrying whether it changed what
    /// was solved. Stored; <see cref="Notes"/> is a view of it.</summary>
    public required IReadOnlyList<EmFinding>            Findings      { get; init; }

    /// <summary>Every finding's text, class discarded — the pre-EM-SEV spelling, DERIVED so the two
    /// cannot drift.</summary>
    public IReadOnlyList<string>                        Notes => EmFindings.Texts(Findings);

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
    /// <b>R-pcal7-4 — every de-embedded point whose σ_max exceeds 1</b>, ascending in frequency, or
    /// empty when the whole sweep is a network. The same measurement the run's own NOT PASSIVE note
    /// is made from, handed to the caller so it can reach the file the notes do not.
    /// </summary>
    public IReadOnlyList<PlanarPassivityExcess> NonPassivePoints { get; init; } = [];

    /// <summary>
    /// <b>MIM-9 item 4 — WHY those points are not a network, decided once and read twice.</b> Empty
    /// when the whole sweep is a network.
    ///
    /// <para>The run's own NOT PASSIVE note and the caveat <c>EmSnpProvenance</c> stamps into the
    /// <c>.sNp</c> both end in this string. They used to end in two independently written guesses,
    /// which is how one of them could be corrected and the other go on being wrong in every file
    /// already on disk. ASCII, because the Touchstone writer transliterates and a subscript comes
    /// out of it as a question mark. See <see cref="PlanarSolve.NonPassivityCause"/>.</para>
    /// </summary>
    public string NonPassivityCause { get; init; } = "";

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

    /// <summary>
    /// <b>PCAL2/R-pcal2-5 — how close each de-embedded port's feed came to its neighbour</b>, one
    /// entry per de-embeddable port, in port order. Empty when de-embedding was off, when no port
    /// is de-embeddable, or when there was no end run to scan.
    ///
    /// <para>A <see cref="PlanarFeedClearance.Breached"/> entry can only be here when the run was
    /// explicitly allowed to de-embed outside the calibration's validity — otherwise the run was
    /// refused and produced no result at all. That is what makes this list the thing the Touchstone
    /// writer keys its own caveat line off.</para>
    /// </summary>
    public IReadOnlyList<PlanarFeedClearance> FeedClearances { get; init; } = [];

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
/// <param name="DeembedOutsideCalibrationValidity">
/// <b>PCAL2/R-pcal2-2 — de-embed and publish even where the two-line calibration is not valid,
/// instead of refusing.</b> Off by default, which is the refusal.
///
/// <para><b>It is named for what it does rather than for the check it turns off</b>, because that
/// is what it does: the answer still comes out of a calibration measured on an isolated line that
/// the DUT's port does not have, and the file is still wrong in the way
/// <see cref="PlanarFeedClearance"/> describes. There are real reasons to want it — comparing
/// against a previous run, debugging, or knowing the port region is not where your answer lives —
/// and there is no reason for it to be quiet about itself: the run says so in its notes and the
/// Touchstone the run service writes says so in its provenance block, which is the half that
/// survives the file being opened somewhere else a month later.</para>
///
/// <para>The other way past a refusal is <see cref="Deembed"/> = false, which is a different
/// answer rather than the same answer with a caveat: the raw solve includes the port
/// discontinuity.</para>
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
    PlanarFarFieldSettings?    FarField    = null,
    bool                       DeembedOutsideCalibrationValidity = false,
    /// <summary>
    /// <b>LF2 — a requested point below <see cref="Dcim.LowestFittableFrequency"/> carries the
    /// 0 Hz conduction solve instead of refusing the sweep.</b> On by default: the alternative is a
    /// run that stops, and what the conduction answer omits (reactance) is the part that is on its
    /// way to nothing as the frequency falls, so the substitution's error SHRINKS down the band
    /// where the fit's grows. Set false to get L9e/D8's refusal back verbatim — which is what a
    /// caller wants when it is measuring the fit rather than using it.
    /// </summary>
    bool                       SubstituteConductionBelowFitFloor = true)
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
    /// <b>PCAL4/R-pcal4-6 — refuse a calibration group whose modes the cascade eigenproblem cannot
    /// separate, at SETUP.</b> See <see cref="PlanarPortCalibrator.QuasiStaticModeSeparationDegrees"/>
    /// for why the question can be asked before a single frequency has been solved.
    ///
    /// <para><b>PCAL6/R-pcal6-7 — asked at EVERY requested frequency, not only at the band's
    /// bottom, and that is a correction rather than caution.</b> The quasi-static separation is
    /// Δβ·Δℓ and Δβ is exactly proportional to frequency, so if Δℓ were fixed the bottom of the band
    /// would provably be the worst point. It is not fixed: <see cref="PlanarCalibration.SuggestDeltas"/>
    /// hands out one separation per sub-band and the selection steps DOWN to a shorter one as the
    /// frequency rises, so the product drops by most of that step at every switch. Measured on the
    /// series' own pair over 100 MHz - 1 GHz, where the candidates are 171.0 mm and 54.1 mm and the
    /// switch is at 298 MHz: 2.38° at the band's bottom against <b>2.24° just above the switch</b>.
    /// A band with four candidates has four such steps. The question costs arithmetic on an
    /// electrostatic solve the run already owes, so it is asked at all of them.</para>
    /// </summary>
    internal static void GuardModeSeparation(PlanarPortCalibrator cal, PlanarPortResolution port,
                                             IReadOnlyList<double> freqsHz,
                                             PlanarCalibrationSettings calSt,
                                             SurfaceMesher.PlanarLengthFormat fmt)
    {
        double worst = double.PositiveInfinity, at = 0;
        foreach (double f in freqsHz)
        {
            if (!(f > 0)) continue;
            double sep = cal.QuasiStaticModeSeparationDegrees(f);
            if (sep < worst) { worst = sep; at = f; }
        }
        if (!(worst < calSt.ModeSeparationFloorDegrees)) return;

        var g = port.Group!;
        throw new PlanarFeedClearanceRefusedException(
            $"Ports {string.Join(", ", g.PortNumbers)} would be calibrated together as one group — " +
            $"their feeds are mutually coupled, the nearest pair {fmt(g.NearestM)} apart — but their " +
            $"{g.ConductorCount} modes are not separable: at {SurfaceMesher.Eng(at)}Hz the closest " +
            $"pair differs by {worst:F3}° of electrical length over the calibration separation, against " +
            $"a floor of {calSt.ModeSeparationFloorDegrees:F2}°. The modal error box is extracted from " +
            "the eigenvectors of the two standards' cascade, and at equal eigenvalues those " +
            "eigenvectors are not determined at all. Separate the feeds by at least the driven " +
            "clearance so each port calibrates on its own, or move the port plane to a station where " +
            "the conductors are not coupled.",
            []);
    }

    /// <summary>
    /// <b>The 0 Hz point, in the shape every other point of the sweep has.</b> Its RawS IS its S —
    /// there is no port discontinuity to remove at DC, because a discontinuity is a reactance — and
    /// it carries no calibrations for the same reason, which is the honest thing for a point that was
    /// not calibrated rather than an identity box pretending it was.
    /// </summary>
    private static PlanarFrequencyPoint DcPoint(PlanarDcResult dc) =>
        new(0.0, dc.S, dc.S, [], KernelFitMs: 0, DutMs: dc.ElapsedMs, CalibrationMs: 0);

    /// <summary>
    /// <b>LF2 — a point below the fit's floor, carrying the conduction solve's answer.</b> Exactly
    /// <see cref="DcPoint"/>'s shape at a non-zero frequency, and for the same reasons: its RawS IS
    /// its S because there is no reactance to remove, and it carries no calibration because it was
    /// not calibrated. The frequency is the user's own, so a <c>.sNp</c> has the rows it asked for.
    /// </summary>
    private static PlanarFrequencyPoint ConductionPoint(double fHz, PlanarDcResult dc, double ms) =>
        new(fHz, dc.S, dc.S, [], KernelFitMs: 0, DutMs: ms, CalibrationMs: 0);

    /// <summary>
    /// <b>One sentence, because a sentence is what gets read.</b> It has to carry three things and
    /// no more: where the boundary is, which points moved, and that the reactance is not in them.
    /// The mesh clause is there because the conduction answer is read on whatever mesh the sweep
    /// was given, and a mesh pinned for a microwave run is a crude resistor ladder — measured at
    /// 0.750x of a 20 mm trace's true resistance on a 2 GHz mesh, 0.982x on a 40 GHz one
    /// (docs/design/mom-engine.md §10.13(e)).
    /// </summary>
    private static string ConductionSubstitutionNote(
        IReadOnlyList<double> fs, double stackHeightM, double meshHz)
    {
        string which = fs.Count == 1
            ? $"the {SurfaceMesher.Eng(fs[0])}Hz point carries"
            : $"{fs.Count} points ({SurfaceMesher.Eng(fs[0])}Hz to "
              + $"{SurfaceMesher.Eng(fs[^1])}Hz) carry";
        // The L8d entry point builds its problem with no mesh frequency at all, so the clause that
        // names one is conditional rather than printing "the 0Hz mesh".
        string onMesh = meshHz > 0
            ? $", read on the {SurfaceMesher.Eng(meshHz)}Hz mesh (a coarse mesh reads resistance low)"
            : "; a coarse mesh reads resistance low";
        // PEEL — this used to say "the full-wave fit has no valid range", which reads as "trouble
        // ends here" when it is one of four low-frequency walls and not the highest of them. That is
        // exactly how the reported 59 dB arrived with a sentence beside it that seemed to say
        // otherwise. "The field solver" names the same wall in words a designer already has; the
        // de-embedding wall gets its own note (PeelConditioningNote) when it binds.
        //
        // Owner instruction, 2026-09-14: plain terms, short, and no shouting. "Fit" is this file's
        // word for the DCIM Green's-function fit and means nothing to the person reading the run.
        // It stays ONE sentence — LF1's own ask was that an RF designer will not read it if the text
        // is too long, and PlanarDcPointTests asserts that as a rule.
        return $"Below {SurfaceMesher.Eng(Dcim.LowestFittableFrequency(stackHeightM))}Hz the "
             + $"field solver has no valid range, so {which} a DC solve — resistance only, no "
             + $"reactance{onMesh}.";
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // PEEL — WHAT THE DE-EMBEDDING IS EXPECTED TO BE WRONG BY, AND THE TWO THRESHOLDS
    // ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // Measured, in |ΔS|, against the uniform-line control — the only oracle here that smuggles in no
    // second model, because a de-embedded S₁₁ on a plain line must be exactly 0. Across three
    // stacks, four mesh densities, five separations and three decades of frequency the realised
    // error came out at 0.73-1.06 times PlanarErrorBox.DeembedErrorFloor, so a threshold ON the
    // floor IS a threshold on |ΔS| (src/Engine/Mom/RESOLVED.md §PEEL).
    //
    // BOTH NUMBERS ARE ANCHORED ON WHAT A PERFECT MATCH GETS PUBLISHED AS, which is the defect that
    // reached a user: on the reported board a −60.2 dB line was published as −1.37 dB.
    //
    //   • 0.05 is −26 dB. A design's own return loss is usually worse than that, so up to here the
    //     instrument is not what a reader is looking at. Above it, the points are NAMED and still
    //     published.
    //   • 0.25 is −12.0 dB, which is the shape of the report itself. At that level the published
    //     number is not a degraded version of the answer, it is a different answer, so the POINT is
    //     left out of the sweep — the rest of which is published exactly as it was solved. A run
    //     that printed such a point with a note would be inviting the note to be skipped, and a run
    //     that REFUSED over it would throw away every other frequency the user waited for.
    //
    // They are deliberately NOT settings and there is no .cem field and no panel control: QSC's own
    // reasoning about its crossover, unchanged. The one way to get the dropped points back in the
    // file is PlanarSolveSettings.DeembedOutsideCalibrationValidity, which already exists and
    // already says in the run's notes and in the .sNp's provenance that it was used — this is the
    // same claim (the calibration is not valid here) and it does not deserve a second flag.
    public const double PeelErrorBudgetDS   = 0.05;
    public const double PeelErrorRefusalDS  = 0.25;

    /// <summary>
    /// <b>PEEL — the per-point floor a sweep came back with, and what is to be done about it.</b>
    /// </summary>
    /// <param name="Flagged">Points at or above <see cref="PeelErrorBudgetDS"/> that are still
    /// published, ascending.</param>
    /// <param name="Unanswerable">Points at or above <see cref="PeelErrorRefusalDS"/>. <b>These are
    /// DROPPED from the published sweep, not refused</b> — see
    /// <see cref="PeelConditioningNote"/>.</param>
    /// <param name="WorstFloor">The largest floor seen, over every (point, port).</param>
    /// <param name="WorstHz">Where it was seen.</param>
    /// <param name="BudgetMetAboveHz">
    /// The frequency above which the floor is expected to fall under <see cref="PeelErrorBudgetDS"/>.
    /// <b>Extrapolated from the run's own measurement rather than modelled</b>: the floor is
    /// (residual ∝ ω) / (|a₂₁|² ∝ ω²), so it goes as 1/f, and the worst point's own floor scaled by
    /// its own frequency is the constant. That is the remedy sentence's whole content — a band edge
    /// the user can actually type — and it is the analogue of
    /// <see cref="Dcim.LowestFittableFrequency"/> for the wall one level up.</param>
    internal readonly record struct PeelConditioning(
        IReadOnlyList<double> Flagged, IReadOnlyList<double> Unanswerable,
        double WorstFloor, double WorstHz, double BudgetMetAboveHz);

    /// <summary>
    /// <b>PEEL — the worst <see cref="PlanarErrorBox.DeembedErrorFloor"/> over a point's ports</b>,
    /// and 0 on a point that carries no calibration at all (the 0 Hz row and LF2's substituted ones,
    /// which are not de-embedded and must never be dropped for a de-embedding diagnostic).
    /// </summary>
    private static double PointFloor(PlanarFrequencyPoint pt) => WorstFloorAt(pt).Floor;

    /// <summary>
    /// <b>LFP — the worst floor over a point's ports, WITH the term that set it.</b> The two terms
    /// <see cref="PlanarErrorBox.DeembedErrorFloor"/> takes the larger of obey different frequency
    /// laws — the standards' share falls as 1/f and the DUT's as 1/f² — so the band edge the remedy
    /// sentence offers cannot be extrapolated without knowing which one is binding here.
    /// </summary>
    private static (double Floor, bool DutBound) WorstFloorAt(PlanarFrequencyPoint pt)
    {
        if (!(pt.FrequencyHz > 0)) return (0, false);
        double worst = 0;
        bool dut = false;
        foreach (var c in pt.Calibrations)
        {
            double v = c.Box.DeembedErrorFloor;
            if (!double.IsNaN(v) && !double.IsInfinity(v) && v > worst)
            {
                worst = v;
                dut   = c.Box.FloorIsDutBound;
            }
        }
        return (worst, dut);
    }

    /// <summary>
    /// <b>PEEL — read the peel's own conditioning off the points the sweep already produced.</b>
    /// Nothing is solved and nothing is re-derived: every ingredient is on
    /// <see cref="PlanarPortCalibration.Box"/>, which is why this is asked after the sweep rather
    /// than in its hot path.
    /// </summary>
    internal static PeelConditioning? MeasurePeelConditioning(IReadOnlyList<PlanarFrequencyPoint> points)
    {
        var flagged = new List<double>();
        var dropped = new List<double>();
        double worst = 0, worstHz = 0, above = 0;
        bool any = false;

        foreach (var pt in points)
        {
            var (here, dutBound) = WorstFloorAt(pt);
            if (!(pt.FrequencyHz > 0) || pt.Calibrations.Count == 0) continue;
            any = true;
            if (here <= 0) continue;

            if (here >= PeelErrorRefusalDS) dropped.Add(pt.FrequencyHz);
            else if (here >= PeelErrorBudgetDS) flagged.Add(pt.FrequencyHz);
            if (here > worst) { worst = here; worstHz = pt.FrequencyHz; }

            // The law, read off THIS point, and WHICH law depends on which of the floor's two terms
            // is binding here (LFP). The standards' residual rises as ω against an |a₂₁|² that rises
            // as ω², so that share falls as 1/f and floor·f is the constant; the DUT's own share is
            // a CONSTANT over the same ω², so it falls as 1/f² and floor·f² is the constant. Getting
            // this wrong is not cosmetic: on the shipped spiral the 1/f reading of its 160 MHz row
            // would name 2.4 GHz as the band edge where the measured one is 663 MHz, and a user
            // would throw away most of a band that answers.
            //
            // Taken over every point and kept at its largest, because a sweep that crosses a
            // separation switch — or the crossover between these two terms — has more than one
            // constant in it.
            double implied = dutBound
                ? pt.FrequencyHz * Math.Sqrt(here / PeelErrorBudgetDS)
                : pt.FrequencyHz * (here / PeelErrorBudgetDS);
            if (implied > above) above = implied;
        }
        if (!any) return null;
        flagged.Sort();
        dropped.Sort();
        return new PeelConditioning(flagged, dropped, worst, worstHz, above);
    }

    /// <summary>
    /// <b>PEEL — what the run tells the person reading it.</b> Three things and no more: which points
    /// are missing, the band edge that gets them back, and where to find the per-point number.
    ///
    /// <para><b>Owner instruction, 2026-09-14: plain terms, short, and no shouting.</b> The first
    /// version of this note was eight sentences of mechanism — a₂₁ ∝ ω, the peel's division, why
    /// <c>DeembedResidual</c> is anti-correlated — and a designer would not have read any of it.
    /// None of that is lost; it lives in this file's own comments, in
    /// <see cref="PlanarErrorBox.DeembedErrorFloor"/> and in <c>RESOLVED.md</c> §PEEL, which are
    /// where someone asking "why" will look. The run's notes are for someone asking "what do I
    /// do".</para>
    ///
    /// <para>The one clause that is neither a symptom nor a remedy — that longer calibration lines
    /// do not help — earns its place because it is the move a user who reads this will otherwise
    /// make, and it costs a day.</para>
    /// </summary>
    /// <param name="everythingWent">
    /// The one case that is a refusal rather than a dropped point: every de-embedded point of the
    /// sweep was unanswerable, so there is no result to hand back and nothing is lost by saying so.
    /// </param>
    private static string PeelConditioningNote(PeelConditioning p, bool everythingWent)
    {
        string edge = $"De-embedding on this port is reliable above about " +
                      $"{SurfaceMesher.Eng(p.BudgetMetAboveHz)}Hz";
        const string tail = " Longer calibration lines do not help. " +
                            "The DeembedErrorFloor result estimates the de-embedding error at " +
                            "every point.";

        if (everythingWent)
            return "Every de-embedded point of this sweep is below the port de-embedding limit, so " +
                   $"there is nothing to publish. {edge} — raise the sweep's lower edge." + tail;

        if (p.Unanswerable.Count > 0)
        {
            string names = p.Unanswerable.Count == 1
                ? SurfaceMesher.Eng(p.Unanswerable[0]) + "Hz"
                : $"{SurfaceMesher.Eng(p.Unanswerable[0])}Hz to " +
                  $"{SurfaceMesher.Eng(p.Unanswerable[^1])}Hz";
            return $"{p.Unanswerable.Count} point(s) ({names}) were dropped: port de-embedding is " +
                   $"not reliable that low, and the rest of the sweep is unaffected. {edge} — raise " +
                   "the sweep's lower edge to get them back." + tail;
        }

        return $"{p.Flagged.Count} point(s) from {SurfaceMesher.Eng(p.Flagged[0])}Hz carry a " +
               $"de-embedding error of up to {p.WorstFloor:0.##} in |S|, which is enough to hide a " +
               $"good match. {edge} — raise the sweep's lower edge to avoid them." + tail;
    }

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
    /// <b>PCAL5 — how far apart two members of a calibration group's automatic feed leads may be
    /// before the group is declined</b>, as a fraction of the calibration's own end run.
    ///
    /// <para>This is a ROUND-OFF tolerance and nothing else. R-fed-1 grows every member's lead from
    /// one station to one end run, so the lengths come out equal to the last bit and the subtraction
    /// <c>|plane − drawn edge|</c> is the only thing between them and exact equality. Any difference
    /// that is not round-off is at least one mesh cell — nine or more orders above this — so there is
    /// no middle ground for a looser value to buy and no tuning question here.</para>
    /// </summary>
    public const double GroupPeelToleranceFraction = 1e-9;

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
        var notes = new List<EmFinding>();

        // Ascending, because both branch resolutions are continuations (PlanarPortCalibrator).
        var freqs = freqsHz.ToArray();
        Array.Sort(freqs);

        // ── DOES THE MESHED STRUCTURE CONDUCT WHERE THE ARTWORK DOES? (2026-09-12) ──────────────
        //
        // Before anything is filled, because a severed conductor makes every number after this point
        // meaningless and costs a full sweep to discover.
        //
        // LF2 MOVED IT ABOVE THE 0 Hz AND SUB-FLOOR SPLIT, AND THAT IS NOT TIDYING.
        //
        // It used to sit below them, so a sweep with no fitted point in it — 0 Hz alone, or a band
        // entirely under the fit's floor — returned early and never asked. That is the WORST place
        // to skip it: the conduction solve answers a disconnected port with EXACTLY zero by design
        // (LF1 §4 — the island's own free nodes settle at the driven potential, so the number comes
        // off the component graph rather than out of the solve), so a severed mesh publishes a
        // clean, exact, entirely wrong open circuit with nothing anywhere to say so. The question
        // needs only the problem, the mesh and the ports, none of which a frequency changes, so
        // there is no cost to asking it once for every sweep shape. A rooftop exists only where both cells'
        // share of the edge is swept by metal, so a conformally cut oblique rim can decline every
        // rooftop across a bend and cut the conductor in two — and NOTHING downstream notices: the
        // matrix is well formed, the solve converges, and the answer is a smooth, plausible OPEN
        // CIRCUIT that is passive at every frequency, so R-prt-15's own gate is silent too.
        //
        // This is the same judgement PCAL2 made about the clearance breach and for the same reason:
        // a `.sNp` on disk carries no notes, so a run that cannot produce a usable answer has to stop
        // rather than publish one with a caveat attached to the window it came from.
        {
            var severed = PlanarConductors.FindSeveredConductors(problem, mesh, ports);
            if (severed.Count > 0)
            {
                var s = severed[0];
                string islands = string.Join(" and ", s.Islands.Select(
                    g => g.Count == 1 ? $"port {g[0]}" : "ports " + string.Join(", ", g)));

                // ── NAME ONLY REMEDIES THAT BIND (the standing rule, broken three times here) ────
                //
                // "Raise Cells per wavelength" is the obvious sentence and it is INERT on exactly
                // this artwork: the pitch at a mitre is set by the metal's own width and by the
                // detail floor, not by λ, so on the fixture this was measured on the mesh is
                // bit-identical at cells/λ 5, 10, 20 and 40 — severed at every one of them, and
                // raising MinCellsAcrossConductor from 2 to 4 does not clear it either. What was
                // measured to restore conduction is resolving the RIM, which is the edge mesh; and,
                // where the cells are cut, staircasing them.
                bool anyCut = false;
                foreach (var cell in mesh.Cells) if (cell.IsCut) { anyCut = true; break; }

                string remedy = anyCut
                    ? "Turn the EDGE MESH on — that resolves the rim and is what was measured to " +
                      "restore conduction here — or set Boundary cells back to \"Staircase\", which " +
                      "removes the cut cells altogether."
                    : "Turn the EDGE MESH on: that is what resolves a rim, and it is what was " +
                      "measured to restore conduction here.";

                throw new InvalidOperationException(
                    "The mesh has SEVERED a conductor the artwork draws as one piece: on " +
                    $"'{problem.Layers[s.LayerIndex].Name}', {islands} stand on the same polygon and " +
                    "no chain of basis functions joins them, so no current can pass between them " +
                    "however this structure is driven. The s-parameters would read as an OPEN " +
                    "CIRCUIT — smooth, plausible and passive at every frequency, which is why " +
                    "nothing else here catches it. A rooftop is only built where the shared edge of " +
                    "two cells is swept by metal on both sides, and at an oblique rim — a mitre, a " +
                    "taper flank, a chamfer — a coarse mesh can fail that right across a conductor. " +
                    remedy + " Raising Cells per wavelength is NOT a remedy here: where the metal is " +
                    "narrower than a wavelength cell the pitch is set by the geometry and that " +
                    "setting cannot move this mesh at all.");
            }
        }


        // ── LF1 — 0 Hz IS TAKEN OUT OF THE SWEEP BEFORE ANYTHING ELSE READS IT ──────────────────
        //
        // It is not a frequency this machinery can carry: the kernel is written in k₀, the
        // calibration standard is an electrical length, and the de-embedding peel divides by a₂₁ ∝ ω.
        // Splitting it out HERE rather than special-casing it downstream is what keeps every other
        // point's arithmetic bit-identical — nothing below this line has a zero to branch on — and it
        // is why PlanarDcSolve can be an independent, testable answer instead of a limit taken inside
        // a sweep. It is spliced back on at the end, first, which is where a .sNp wants it.
        bool wantDc = freqs.Length > 0 && freqs[0] <= 0;
        if (wantDc)
        {
            int firstAc = 0;
            while (firstAc < freqs.Length && freqs[firstAc] <= 0) firstAc++;
            freqs = freqs[firstAc..];
        }

        // ── LF2 — AND SO ARE THE POINTS BELOW THE FIT'S OWN FLOOR, FOR THE SAME REASON ──────────
        //
        // L9e/D8's refusal stood here because below k₀H = 1e-4 the fit is fitting roundoff. What it
        // could not say is that the fit is the LAST of four walls at the bottom of a band, not the
        // first: the calibration standard's N goes as 1/f and is refused an octave or more higher,
        // the de-embedding peel divides by a₂₁ ∝ ω, and the raw uncalibrated answer is the delta
        // gap rather than the structure at ANY frequency (docs/design/mom-engine.md §10.13). None
        // of those has a knob, and all four of them stop mattering at 0 Hz, where the problem is a
        // conduction network with an exact answer. So the points down there take that answer.
        //
        // Split HERE, beside 0 Hz and above everything else, for the reason LF1 gives: nothing
        // below this line then has a sub-floor frequency to branch on, and every remaining point's
        // arithmetic is bit-identical to what it was before this existed.
        double stackHeightM = problem.RequiresGeneralKernel ? problem.EffectiveStack.TopZ
                                                            : slab.HeightM;
        double[] subFloor = [];
        if (st.SubstituteConductionBelowFitFloor && stackHeightM > 0)
        {
            int firstFit = 0;
            while (firstFit < freqs.Length &&
                   Dcim.IsBelowFitFloor(2.0 * Math.PI * freqs[firstFit] / EmConstants.C0,
                                        stackHeightM))
                firstFit++;
            if (firstFit > 0) { subFloor = freqs[..firstFit]; freqs = freqs[firstFit..]; }
        }

        if (freqs.Length == 0)
        {
            // A sweep of nothing but DC and points below the fit's floor. The mesh is already built
            // and the ports already resolved, so there is an exact answer here and no reason to
            // refuse it.
            var only = PlanarDcSolve.Solve(problem, mesh, ports, leads);
            notes.AddRange(EmFindings.AsNotes(only.Notes));
            var onlyPoints = new List<PlanarFrequencyPoint>(1 + subFloor.Length);
            if (wantDc) onlyPoints.Add(DcPoint(only));
            for (int i = 0; i < subFloor.Length; i++)
                onlyPoints.Add(ConductionPoint(subFloor[i],
                                               only, i == 0 && !wantDc ? only.ElapsedMs : 0));
            if (subFloor.Length > 0)
                notes.Add(ConductionSubstitutionNote(subFloor, stackHeightM, problem.MaxFrequencyHz));
            return new PlanarSolveResult
            {
                Points        = onlyPoints,
                CoreFillCount = 0,
                UnknownCount  = mesh.Bases.Count,
                StandardCount = 0,
                CoreBuildMs   = 0,
                Findings      = notes,
                SolvedPointCount = onlyPoints.Count,
            };
        }

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
        //
        // ── LF1 — AND IT IS ASKED OF THE LOWEST NON-ZERO POINT ──────────────────────────────────
        //
        // 0 Hz is not a frequency this guard has anything to say about: it is not fitted at all, it
        // is solved as a conduction network (PlanarDcSolve), and asking a question about k₀H of a
        // point where k₀ = 0 would refuse the one case that is exact.
        double stackH = stackHeightM;
        double fLoAc  = 0;
        foreach (double f in freqs) if (f > 0) { fLoAc = f; break; }
        if (fLoAc > 0)
        {
            var lowFreq = Dcim.CanFitAtFrequency(2.0 * Math.PI * fLoAc / EmConstants.C0, stackH);
            if (!lowFreq.Ok)
                throw new InvalidOperationException(
                    $"The sweep's lowest frequency is {SurfaceMesher.Eng(fLoAc)}Hz. " + lowFreq.Reason);

            // ── LF1 — SAY SO WHEN THE PATH WAS WIDENED, ONCE, AND IN ONE LINE ───────────────────
            //
            // The widening happens per frequency inside PlanarFrequencyKernel.Fit and is invisible
            // in the answer: there is no refusal, no warning and no cost. An RF designer still has
            // to be able to find out that the bottom of their band was fitted differently from the
            // top, so the run says which points were widened and stops there — the derivation lives
            // in Dcim.CalibratedPathProduct, not in the Messages panel.
            var baseDcim = st.Dcim ?? DcimSettings.Default;
            double khLo  = 2.0 * Math.PI * fLoAc / EmConstants.C0 * stackH;
            if (baseDcim.PathExtent * khLo < Dcim.CalibratedPathProduct)
            {
                // The highest frequency that needed nothing — everything below it was widened.
                double fPlain = Dcim.CalibratedPathProduct * EmConstants.C0
                                / (2.0 * Math.PI * stackH * baseDcim.PathExtent);
                double widest = Dcim.ForStackAtFrequency(
                    baseDcim, 2.0 * Math.PI * fLoAc / EmConstants.C0, stackH).PathExtent;
                notes.Add(
                    $"Below {SurfaceMesher.Eng(fPlain)}Hz the Green's function's sampling path widens " +
                    $"as the frequency falls — {widest:N0}·k₀ at {SurfaceMesher.Eng(fLoAc)}Hz — so the " +
                    "fit still sees the stack. Same sample count, same cost.");
            }
        }

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
        // ── CL3 — THE METAL IS A REAL CONDUCTOR BY DEFAULT, AND THIS IS THE ONE PLACE IT IS DECIDED ──
        //
        // σ and t live on the PROBLEM, not on a mesh or on a fill setting, so this is the only
        // scope in which "model the metal's loss" can resolve to the two numbers it needs. CL1
        // shipped the term behind a null `ConductorLoss` and CL3 turns it on; what stays reachable
        // is `PerfectConductor`, the PEC oracle every CL1 and CL2 accuracy gate compares against.
        //
        // A caller that supplied its own `ConductorLoss` keeps it — that is the seam CL1's own
        // gates drive the fill through, and it is how a test compares two metals on one mesh.
        var fillIn = st.Fill ?? PlanarFillSettings.Default;
        var fillSt = fillIn with
        {
            MaxDegreeOfParallelism = cap,
            Budget                 = parallelBudget,
            ConductorLoss          = fillIn.PerfectConductor ? null
                                   : fillIn.ConductorLoss ?? PlanarConductorLoss.For(problem),
        };

        var sw    = Stopwatch.StartNew();
        var dut   = new PlanarSolveContext(mesh, ports, fillSt, levels, slab.HeightM);
        double setupMs = sw.Elapsed.TotalMilliseconds;
        int    cores   = 1;

        // ── CL2 — what the power budget needs to book a conductor term, and it is the FILL's ─────
        //
        // Null while the fill modelled no conductor loss, which is CL1's PEC oracle and is still the
        // default; then the budget is bit-identical to ANT-5's. Where it is non-null every piece
        // comes from the objects the fill itself read — `dut.Cores.Gram` rather than a rebuild — so a
        // loss term in the matrix and a loss term in the budget cannot mean different things. It is
        // built once and memoised because the budget is per (frequency, port) and the Gram is not.
        //
        // `dut.Cores` is LAZY, so this is a local function rather than a plain local: touching it
        // eagerly on a PEC run would force a core build that run might never need.
        PlanarConductorLossInputs? conductorInputs = null;
        PlanarConductorLossInputs? ConductorLoss() =>
            fillSt.ConductorLoss is { } cl
                ? conductorInputs ??= new PlanarConductorLossInputs(cl, dut.Cores.Gram, dut.Levels)
                : null;

        // ── R-prt-2/3 + PCAL2: what the ports resolved to, and whether their feeds are clear ─────
        //
        // PCAL2/R-pcal2-1 — A BREACH IS A REFUSAL NOW, NOT A NOTE.
        //
        // It used to be a note, and the note was accurate and well worded; the problem was that the
        // artefact it was attached to outlived it. A `.s4p` on disk carries no notes, so the next
        // person to open it in the Data Display saw a plausible curve that was 22 dB out in S21 and
        // non-passive at 48 of 51 points. The precedent is the mesh-ceiling refusal directly above:
        // stop a run that cannot produce a usable answer, name the quantity that made it unusable,
        // and name the setting that answers it.
        //
        // Only when de-embedding is ON. With it off there is no calibration standard, nothing is
        // being replaced by an isolated line, and a neighbour is simply part of the structure.
        var calSt = st.Calibration ?? PlanarCalibrationSettings.Default;
        int groupCount = 0;
        var clearances = new List<PlanarFeedClearance>();
        var widenNotes = new List<string>();
        if (st.Deembed)
        {
            var conductors  = PlanarConductors.Of(mesh);
            double endRunM  = calSt.EndRunHeights * slab.HeightM;
            double drivenM  = calSt.DrivenNeighbourClearanceHeights  * slab.HeightM;
            double passiveM = calSt.PassiveNeighbourClearanceHeights * slab.HeightM;
            var    widened  = new List<PlanarPortResolution>(ports);

            PlanarFeedClearance? Measure(PlanarPortResolution p) =>
                PlanarPorts.MeasureFeedClearance(
                    mesh, p, ports,
                    endRunM:          endRunM,
                    drivenRequiredM:  drivenM,
                    passiveRequiredM: passiveM,
                    slabHeightM:      slab.HeightM,
                    conductors:       conductors);

            // ── PCAL4/R-pcal4-1 — A DRIVEN BREACH IS A CALIBRATION GROUP, NOT A RUN TO REFUSE ────
            //
            // It comes FIRST because a group changes the profile every one of its member ports is
            // then measured against: once ports 1 and 3 share a standard that reproduces both
            // conductors, neither of them has a neighbour any more, and asking the clearance question
            // before the grouping would have both of them refuse the run they are about to fix.
            //
            // Attempted only on a DRIVEN breach, which since PCAL2 is a refusal — a clear feed
            // measures Breached = false and never reaches this line, which is the whole of
            // R-pcal4-1's "a group of one is today's case and must remain bit-identical".
            if (calSt.IncludeDrivenGroups)
            {
                for (int i = 0; i < widened.Count; i++)
                {
                    if (widened[i].Group is not null) continue;
                    var c0 = Measure(widened[i]);
                    if (c0 is not { Breached: true, Neighbour: PlanarNeighbourClass.Driven }) continue;

                    var profile = PlanarPorts.TryFormCalibrationGroup(
                        mesh, widened[i], ports,
                        PlanarCalibration.EndRunCellsFor(widened[i], slab, st.Calibration),
                        drivenM, Math.Max(2, calSt.MaxCalibrationGroupSize), conductors,
                        out string? groupDeclined);

                    if (profile is null)
                    {
                        // A null with NO reason means the walk found nothing to group, which is the
                        // ordinary answer for a clear feed — but the clearance predicate has just
                        // said this feed is not clear, so PCAL3's own sentence applies unchanged and
                        // only this call site knows both halves.
                        widenNotes.Add(groupDeclined ??
                            $"Port {widened[i].Number}'s feed has metal " +
                            $"{fmt(c0.NearestM)} away carrying a port, that does not cross THIS " +
                            "port's own reference plane on its own conductor level — it is on " +
                            "another level, behind the end face, or it begins further into the " +
                            "structure. A calibration standard is a uniform extrusion of what " +
                            "crosses that plane, so there is nothing there for a group to be made " +
                            "of.");
                        continue;
                    }

                    // ── PCAL5 — A GROWN FEED LEAD IS PEELED MODALLY, WHEN THE GROUP SHARES ONE ──
                    //
                    // R-pcal4-6 declined every group whose members had grown a lead, on the grounds
                    // that R-fed-2's peel states ONE γ per port while a group's region carries one
                    // per MODE. That reasoning is right about the algebra and wrong about the
                    // geometry, and the case it refuses is the one real boards are made of: a port
                    // lands on a PAD, the pad is shorter than the end run, and R-fed-1 grows a lead
                    // — on every member of the group, since they share a reference plane and the
                    // same cross-section question. The leads are then collinear, of equal length,
                    // and side by side at the group's own separation, so together they ARE a uniform
                    // N-conductor section of exactly the cross-section the group's standard
                    // reproduces. The peel is a matched length of the GROUP's modes, and
                    // PlanarFeedExtension.Peel already runs in the modal basis (it is applied to
                    // ApplyBlocks' output, before ModalToTerminal) — all it was missing was γ_m.
                    //
                    // WHAT STILL HAS TO BE TRUE, and it is the whole gate: every member peels the
                    // SAME length. A mode is a combination of the group's conductors, so "how far
                    // has this mode travelled" has one answer for the group or none; peeling
                    // different lengths on different rows of a modal matrix is not a length of line
                    // at all. Unequal leads also mean the grown region is not one cross-section —
                    // past the shorter lead's end only the other conductor is there.
                    //
                    // A group NONE of whose members grew a lead measures 0 for every member, takes
                    // this branch trivially, and is bit-identical to PCAL4 (R-pcal4-1's own rule).
                    var members = new List<PlanarPortResolution>(profile.PortNumbers.Count);
                    foreach (int num in profile.PortNumbers)
                    {
                        var member = widened.Find(q => q.Number == num);
                        if (member is not null) members.Add(member);
                    }

                    var peel = PlanarFeedExtension.CommonPeelLength(
                        members, leads, GroupPeelToleranceFraction * endRunM);

                    if (!peel.Ok)
                    {
                        string who = string.Join(", ", profile.PortNumbers);
                        widenNotes.Add(peel.NegativePort != 0
                            ? $"Ports {who} would form one calibration group, but port " +
                              $"{peel.NegativePort}'s automatic feed lead is shorter than the " +
                              "outermost mesh cell, so its reference plane sits inside your drawn " +
                              "metal rather than on its edge. A group is peeled back to ONE plane, " +
                              "and there is no positive length to peel here. Raise Cells per " +
                              "wavelength — a finer mesh at the port puts the plane back on the edge."
                            : $"Ports {who} would form one calibration group, but the automatic feed " +
                              "leads they need are not the same length " +
                              $"({fmt(peel.LengthM)} against {fmt(peel.UnequalLengthM)} on port " +
                              $"{peel.UnequalPort}). One group is calibrated at ONE plane and its " +
                              "error box is modal, so a lead is peeled as a matched length of the " +
                              "GROUP's modes — and a mode runs on every conductor at once, so it has " +
                              "one length or none. Where the leads differ the longer one is beside " +
                              "no second conductor for part of its run, which is a different " +
                              "cross-section again. Draw both feeds to the same length, or separate " +
                              "them.");
                        continue;
                    }

                    for (int j = 0; j < widened.Count; j++)
                        if (profile.PortNumbers.Contains(widened[j].Number))
                            widened[j] = widened[j] with { Group = profile };

                    widenNotes.Add(profile.Describe(fmt));
                }
            }

            for (int i = 0; i < widened.Count; i++)
            {
                var c = Measure(widened[i]);

                // ── PCAL3/R-pcal3-1 — A PASSIVE BREACH IS A PROFILE TO WIDEN, NOT A RUN TO REFUSE ──
                //
                // The metal is reproducible: it carries no port, so there is still one driven mode at
                // the reference plane and D6's per-port scalar error box still describes it. What was
                // missing was any way for a conductor OUTSIDE the profile to get INSIDE it — the
                // profile machinery itself has been multi-conductor since RP-2c.
                //
                // Attempted only on a PASSIVE breach, which is the whole of R-pcal3-4's proof that
                // nothing passing today changes: a clear feed measures Breached = false and never
                // reaches this line, and a DRIVEN breach is brief 4's and is declined by name inside.
                if (c is { Breached: true, Neighbour: PlanarNeighbourClass.Passive } &&
                    calSt.IncludePassiveNeighbours)
                {
                    var wider = PlanarPorts.TryWidenForNeighbours(
                        mesh, widened[i], ports,
                        PlanarCalibration.EndRunCellsFor(widened[i], slab, st.Calibration),
                        passiveM, conductors, out string? declined);

                    if (wider is not null)
                    {
                        widened[i] = wider;
                        c = Measure(wider);            // whatever is STILL outside the profile
                        widenNotes.Add(wider.Neighbourhood!.Describe(fmt));
                    }
                    else
                    {
                        // R-pcal3-2 — a decline falls through to today's behaviour, which since
                        // PCAL2 is the refusal below. It is said by name either way, because a
                        // refusal whose cause the user cannot see is one they cannot act on.
                        //
                        // A null with NO reason means the widening found nothing to take in, which
                        // is the ordinary answer for a clear feed — but the clearance predicate has
                        // just said this feed is not clear, and only this call site knows both
                        // halves. So the metal that breached is not on the port's own conductor
                        // level at the port's own reference plane, and that is what is said.
                        widenNotes.Add(declined ??
                            $"Port {widened[i].Number}'s feed has metal " +
                            $"{fmt(c.NearestM)} away that does not cross the port's own reference " +
                            "plane on the port's own conductor level — it is on another level, " +
                            "behind the end face, or it begins further into the structure. A " +
                            "calibration standard is a uniform extrusion of what crosses that " +
                            "plane, so there is nothing there for it to reproduce. (PCAL1 measured " +
                            "no case of metal on another level at all, so nothing is assumed about " +
                            "one here.)");
                    }
                }

                if (c is not null) clearances.Add(c);
            }

            ports = widened;
            foreach (var pr in ports) if (pr.Group is not null) groupCount++;
        }

        foreach (var p in ports)
        {
            notes.Add(p.Describe());
            if (p.Neighbourhood is { } nbh) notes.Add(nbh.Describe(fmt));
            foreach (var c in clearances)
                if (c.PortNumber == p.Number) notes.Add(c.Margin(fmt));
        }
        foreach (string w in widenNotes)
            if (!notes.Contains(w)) notes.Add(w);

        var breaches = clearances.FindAll(c => c.Breached);
        if (breaches.Count > 0)
        {
            if (!st.DeembedOutsideCalibrationValidity)
            {
                // ── R-pcal3-2 / R-pcal4-6 — THE DECLINE TRAVELS WITH THE REFUSAL ────────────────
                //
                // A refusal discards the run's notes, so a "this is why the standard could not be
                // built" sentence collected above never reaches anyone: the user sees the clearance
                // refusal and no reason why the machinery that exists to fix it did not. Both PCAL3's
                // widening and PCAL4's grouping write their declines into `widenNotes`, and this is
                // where they become part of the sentence the run actually says.
                string why = widenNotes.Count == 0 ? ""
                    : "\n\nWhy this feed's calibration standard could not simply reproduce the " +
                      "neighbour: " + string.Join(" ", widenNotes);
                throw new PlanarFeedClearanceRefusedException(
                    PlanarFeedClearance.RefusalFor(breaches, fmt) + why, breaches);
            }

            // R-pcal2-2 — the override is explicit and it is NOT silent. The note says it here and
            // the run service writes the same fact into the Touchstone's provenance block, because
            // this note does not survive the file and the whole finding was that the file outlives
            // its notes.
            notes.Add(
                "The de-embedding was applied OUTSIDE the condition it is valid under, because " +
                "\"de-embed outside the calibration's validity\" is on: " +
                string.Join(", ", breaches.ConvertAll(b => $"port {b.PortNumber} at " +
                    (double.IsNaN(b.Heights) ? fmt(b.NearestM) : $"{b.Heights:0.##} substrate heights"))) +
                ". These s-parameters are not a measurement of this structure and should not be " +
                "compared with one; the Touchstone this run writes says so on its own face.");
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
                double d = PlanarFeedExtension.PeelLengthM(ports[i], lead);

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
                            "station where only the pair crosses it, or cut this port as an internal " +
                            "delta gap instead (an interior cut has no feed, no error box and needs " +
                            "no standard).");

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
                    // QSC — the plan is drawn ONCE and both the standards that are BUILT and the
                    // separations that are SELECTED come from it. Evaluating SeparationPlan twice
                    // with the same arguments would agree today and is exactly the shape PCAL6's
                    // own R-pcal6-3 found a run solving one standard and calibrating against
                    // another. The set is the short line followed by one standard per separation,
                    // so the quasi-static separation's standard is at QuasiStaticIndex + 1.
                    var stdPlan = PlanarCalibration.SeparationPlan(slab, fLo, fHi, ports[i], st.Calibration);

                    // CL1 — the standard is a piece of THIS PORT'S LEVEL, so it is named after it.
                    // A standard's mesh numbers its one conductor level 0 whatever level the port
                    // is on, and the fill resolves a level's Z_s by NAME with an index-0 fallback
                    // (PlanarConductorLoss.SheetTable). Left at the placeholder, every standard on
                    // a multi-level problem was filled with level 0's σ and thickness however high
                    // the port sat — silently, as a plausible α. One level resolves to index 0 by
                    // either route, so nothing about a single-level run moves.
                    string stdLayerName = problem.Layers[
                        Math.Clamp(ports[i].LayerIndex, 0, problem.Layers.Count - 1)].Name;

                    var stdSet  = PlanarCalibration.BuildSet(
                        ports[i], slab, stdPlan,
                        PlanarCalibration.SuggestLengths(slab, fLo, fHi, st.Calibration).Short,
                        st.Calibration, stdLayerName);

                    for (int si = 0; si < stdSet.Length; si++)
                    {
                        var std  = stdSet[si];
                        int nStd = std.Mesh.Bases.Count;
                        if (nStd <= stdCeiling) continue;

                        // The short line (index 0) is shared by both paths, so it is "quasi-static"
                        // exactly when there is no measured ladder for it to serve as well.
                        bool quasiStd = stdPlan.QuasiStaticIndex >= 0 &&
                                        (si == stdPlan.QuasiStaticIndex + 1 ||
                                         (si == 0 && stdPlan.DeltaLM.Length == 1));

                        var stdSizes = stdSet.Select(z => z.Mesh.Bases.Count.ToString("N0"));

                        // ── LF3 — IF THE ACCELERATOR WOULD CLEAR THIS, SAY SO TO THE CALLER ──────
                        //
                        // The refusal below names turning it on as its first remedy, which means the
                        // run already knows the answer and is asking a person to type it. That is a
                        // knob nobody can be expected to find from a sentence about a calibration
                        // standard, and it changes no answer — P11 put the standards' static
                        // capacitance solve on the accelerator too, so an accelerated run is judged
                        // against one ceiling throughout. Only the recoverable case is signalled;
                        // past the accelerated ceiling, or already accelerated, the refusal stands.
                        if (!accStd && !general && nStd <= SurfaceMesher.AcceleratedUnknownCeiling)
                            throw new PlanarAcceleratorWouldFitException(
                                ports[i].Number, nStd,
                                $"Port {ports[i].Number}'s calibration standard needs {nStd:N0} " +
                                $"unknowns, past the {stdCeiling:N0}-unknown dense ceiling and " +
                                $"inside the accelerated one.");

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
                                  "switch to turn on. "
                                : "Turn ON the accelerated solve (the EM setup's Accelerated solve, " +
                                  "PlanarFillSettings.Aim): since P11 it covers the standards' static " +
                                  "capacitance solve (Z_c = γ/(jωC_pul)) as well as every " +
                                  "frequency-domain system, and its ceiling is " +
                                  $"{SurfaceMesher.AcceleratedUnknownCeiling:N0} unknowns. ") +
                            BandEdgeRemedy(std, nStd, stdCeiling, slab, fLo, fHi, st.Calibration,
                                           quasiStd) +
                            $" The DUT's own mesh is N = {mesh.Bases.Count:N0}; this port's " +
                            $"standard(s) are N = {string.Join(" / ", stdSizes)}.");
                    }

                    var cal = new PlanarPortCalibrator(
                        ports[i], slab, fLo, fHi, st.Calibration, fillSt,
                        standardLevelZ: general ? problem.LevelZ(ports[i].LayerIndex) : double.NaN,
                        standards: stdSet,
                        mediumStack: general ? problem.EffectiveStack : null,
                        separations: stdPlan,
                        standardLayerName: stdLayerName);

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
                            "is exact, not an approximation), or bring the feed out on a level with " +
                            "a simpler stack beneath it.");
                    // ── R-pcal4-6 — THE SAME QUESTION, ASKED AT SETUP, FROM THE ELECTROSTATICS ──
                    //
                    // The measured separation is not known until a frequency has been solved, and
                    // refusing there costs the whole sweep's Green's-function fits first. The
                    // QUASI-STATIC one is known as soon as the group's own standards have been
                    // solved electrostatically — which is work the run owes anyway (D7') and which
                    // costs no fit at all — and it is the same quantity to the accuracy the two
                    // routes agree to. Asked at the band's BOTTOM, where the separation in electrical
                    // length is smallest and where PCAL1 measured the conditioning to be worst.
                    if (cal.IsGroup) GuardModeSeparation(cal, ports[i], freqs, calSt, fmt);

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

            // ── QSC — WHICH CALIBRATION EACH POINT TOOK, SAID IN THE RUN'S OWN NOTES ──────────
            //
            // The two paths are not interchangeable and the run must not present one as the other.
            // Which one a point took is also in the `.npy` as `CalQuasiStatic`, per (freq, port);
            // this is the sentence that says what the flag MEANS, and it is emitted only when the
            // quasi-static path actually engaged, so a run entirely above the crossover reads
            // exactly as it did before this existed.
            if (QuasiStaticCalibrationNote(calibrators, freqs) is { } qsNote) notes.Add(qsNote);

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
        var flaggedResonance = new List<double>();

        // ── PCAL4/gate 5 — the modal diagnostics, per frequency, kept for the run's notes ────────
        //
        // R-pcal4-2's mode separation and R-pcal4-3's discarded residuals are most needed exactly
        // where the conditioning is worst, which PCAL1 measured is the BOTTOM of the band (D6's
        // 1/a₂₁² amplification). So the worst of each is reported with the frequency it happened at
        // rather than as a sweep average, which would hide the one point that matters.
        var groupDiagnostics = new List<string>();
        double worstSeparation = double.PositiveInfinity, worstSeparationF = 0;
        double worstCascade = 0, worstCascadeF = 0;
        double worstGauge = 0, worstGaugeF = 0;
        double worstSign = 1.0, worstSignF = 0;
        double worstQuasiStatic = 0, worstQuasiStaticF = 0;
        double worstNullGap = 0, worstNullGapF = 0;
        double worstPalindrome = 0;
        double worstModeCoupling = 0;

        void RecordGroupDiagnostics(double f, PlanarPortGroupProfile g, PlanarGroupCalibration gc,
                                    PlanarPortCalibrator cal)
        {
            var b = gc.Box;
            if (b.ModeSeparationDegrees < worstSeparation) { worstSeparation = b.ModeSeparationDegrees; worstSeparationF = f; }
            if (b.CascadeResidual > worstCascade) { worstCascade = b.CascadeResidual; worstCascadeF = f; }
            if (b.GaugeResidual > worstGauge) { worstGauge = b.GaugeResidual; worstGaugeF = f; }
            if (b.SignMargin < worstSign) { worstSign = b.SignMargin; worstSignF = f; }
            if (b.QuasiStaticBetaError > worstQuasiStatic) { worstQuasiStatic = b.QuasiStaticBetaError; worstQuasiStaticF = f; }
            if (b.NullSpaceGap > worstNullGap) { worstNullGap = b.NullSpaceGap; worstNullGapF = f; }
            worstPalindrome   = Math.Max(worstPalindrome, b.PalindromeResidual);
            worstModeCoupling = Math.Max(worstModeCoupling, gc.ModeCouplingResidual);

            // ── R-pcal4-6 — DEGENERATE MODES ARE A REFUSAL, NOT A NOTE ──────────────────────────
            //
            // At zero separation the cascade's two eigenvalues coincide, the null space of (M − μI)
            // is a plane rather than a line, and which line in it is which mode is decided by
            // round-off. A modal de-embedding built on that is smooth, plausible and wrong, which is
            // the one outcome this whole series exists to remove.
            //
            // ── PCAL7/R-pcal7-5 — WHY THIS GUARD SURVIVES ALONGSIDE THE SETUP ONE ──────────────
            //
            // PCAL7 asked whether this per-frequency question is a second spelling of the setup
            // guard's and should be deleted. It is not, and the measurement is what says so: on a
            // pair 4.4 mm apart at 500 MHz the ELECTROSTATIC separation reads 0.517°, over the
            // floor, so the setup guard passes — and the measured one reads 0.026°, and the answer
            // the run would have published is max |ΔS| 0.999 against an A-vs-B floor of 0.073,
            // 13.6x the floor and not physical. Neither number catches the other's failure: an
            // equal-width triple whose electrostatics reads 0.28° publishes 1.11 in |ΔS| with a
            // MEASURED separation of 0.53°, over the floor. Two questions, two places, and each is
            // load-bearing. See src/Engine/Mom/RESOLVED.md, PCAL7.
            if (b.ModeSeparationDegrees < calSt.ModeSeparationFloorDegrees)
            {
                var breaches = clearances.FindAll(cc => g.PortNumbers.Contains(cc.PortNumber));

                // ── PCAL6 — THE SAME QUANTITY, ASKED OF THE ELECTROSTATICS, IS REPORTED BESIDE IT ──
                //
                // "These modes are genuinely degenerate" and "this standard could not measure them"
                // read identically on the measured number alone, and PCAL6/M1 measured that the
                // second is what a low band actually hits: with the shipped 3 h short line the
                // measured separation is 0.27-0.30x the quasi-static one at 200 MHz. The
                // quasi-static one is a property of the cross-section and does not move when the
                // standards do, so the PAIR of numbers is what tells a user which refusal they have.
                //
                // It is reported and NOT acted on, and PCAL6's own §M3 records why: regrowing the
                // short standard until the measurement agrees was built, measured against the
                // cross-section oracle, and made the published s-parameters WORSE at every
                // frequency — 1.87 in |ΔS| at the bottom of a decade band, against 0.13 without it.
                // A separation that reads low does not mean the answer is bad, and lengthening the
                // standard until it reads high does not make the answer good.
                double qs = cal.QuasiStaticModeSeparationDegrees(f);

                // ── PCAL7/R-pcal7-7 — THE REMEDY THAT BINDS IS NOT THE SAME ONE IN BOTH CASES ────
                //
                // GuardModeSeparation has already asked the quasi-static question at EVERY requested
                // frequency before a single standard was solved (R-pcal6-7), so a group that reaches
                // this line has metal whose modes ARE separable at this frequency and a pair of
                // standards that could not resolve them. "Separate the feeds" is the right advice
                // for the other case and does not bind here, and PCAL7 measured what does: the
                // calibration separation Δℓ is chosen from the SWEEP's band, so the band is the
                // lever, and the standards' own MESH is the second one.
                //
                // ── THE THREE MEASURED SIZES THIS MESSAGE QUOTES WERE RE-MEASURED, AND THE OLD
                //    ONES HAD ACQUIRED A DIRECTION THEY DO NOT HAVE ──────────────────────────────
                //
                // They were taken at PCAL7 on a different board with PEC metal, and §CL3 §5 showed
                // that a real conductor moves a mode separation and does NOT move it monotonically
                // (loss adds |Δα|·Δℓ, but it also changes which Δℓ is selected and both modes' β).
                // The sentence had come to read as "narrow the band and the separation rises", which
                // is not what the lever does. Re-measured on this series' own coupled pair, at the
                // same 200 MHz, with the shipped real metal — `PlanarGroupSeparationTests`'
                // `Board()`, and gated by `TheBandAndTheEdgeMeshAreTheTwoLevERS`:
                //
                //     edge mesh OFF (N = 48):   200-400 MHz  0.405° REFUSED
                //                               200-800 MHz  1.29°  publishes
                //                               100 MHz-1 GHz 1.31° publishes
                //     edge mesh ON  (N = 424):  200-400 MHz  0.96°  publishes
                //                               200-800 MHz  0.155° REFUSED
                //
                // So WIDENING helps with the edge mesh off and hurts with it on, on one fixture and
                // one frequency. The lever is real; its sign is not a rule, and the message must say
                // "try the other edge and read the reported figure back" rather than name a
                // direction. The accuracy figure behind "narrowing is free" (0.1099 against 0.1091
                // in max |ΔS|) was measured on the same PEC board and is dropped rather than
                // re-quoted — it supported a recommendation that is no longer being made.
                //
                // The other spelling survives for the case that can still reach here — an adaptive
                // sweep solves points the setup guard never saw, and the quasi-static separation is
                // not monotone in frequency (PCAL6/M5).
                bool instrument = qs >= calSt.ModeSeparationFloorDegrees;

                throw new PlanarFeedClearanceRefusedException(
                    $"Ports {string.Join(", ", g.PortNumbers)} are calibrated together as one group, " +
                    $"and at {SurfaceMesher.Eng(f)}Hz their {b.ModeCount} modes are not separable: the " +
                    $"closest pair differs by {b.ModeSeparationDegrees:F3}° of electrical length over " +
                    $"the calibration separation, against a floor of " +
                    $"{calSt.ModeSeparationFloorDegrees:F2}° (the group's own electrostatics puts the " +
                    $"same quantity at {qs:F3}°, on a short standard of {fmt(cal.ShortLengthM)}). The " +
                    "modal error box is extracted from the " +
                    "eigenvectors of the two standards' cascade, and at equal eigenvalues those " +
                    "eigenvectors are not determined at all — the de-embedded s-parameters would be " +
                    "smooth, plausible and wrong rather than visibly bad. " +
                    (instrument
                        ? "Your metal is not the problem — the electrostatic figure above is over the " +
                          "floor, so these modes are far enough apart in principle and it is this pair " +
                          "of standards that could not tell them apart. Two things move that " +
                          "measurement and neither of them changes your design. (1) The calibration " +
                          "separation is chosen from the SWEEP's band, so moving either band edge " +
                          "picks a different one. (2) The measurement is made on the standards' own " +
                          "MESH, so the edge mesh moves it too. WHICH WAY EITHER ONE GOES IS NOT A " +
                          "RULE — try it and read this figure back from the run. Measured on this " +
                          "series' own coupled pair, all at the same 200 MHz: with the edge mesh OFF " +
                          "a 200 MHz - 400 MHz band reads 0.405° and refuses while 200 MHz - 800 MHz " +
                          "reads 1.29° and publishes; with the edge mesh ON the same two bands read " +
                          "0.96° (publishes) and 0.155° (refuses). Separating the feeds by at least " +
                          "the driven clearance also works, by removing the group altogether."
                        : "Separate the feeds by at " +
                          "least the driven clearance so each port calibrates on its own, or move the " +
                          "port plane to a station where the conductors are not coupled."),
                    breaches);
            }

            var modes = new string[b.ModeCount];
            for (int m = 0; m < b.ModeCount; m++)
                modes[m] = $"{b.ElectricalDegrees[m]:F1}° / ε_eff " +
                           $"{EffectivePermittivityOf(b.Gamma[m], f):F3} / Z_c " +
                           $"{(gc.Zc[m] * gc.ReportedZcScale[m]).Real:F2}Ω";
            // PCAL7 — the ELECTROSTATIC separation is reported beside the measured one at every
            // point, not only on a refusal. They are the same quantity asked of two different
            // things, PCAL6/M1 measured that the measured one swings by 17x over a Δℓ ladder the
            // electrostatic one is constant across, and a reader handed only the measured number
            // cannot tell a degenerate cross-section from a standard that could not resolve one.
            // It costs nothing: the medium is extracted once per group and cached.
            groupDiagnostics.Add(
                $"{SurfaceMesher.Eng(f)}Hz, ports {string.Join("+", g.PortNumbers)}: modes " +
                string.Join(" · ", modes) +
                $"; separation {b.ModeSeparationDegrees:F2}°, electrostatics " +
                $"{cal.QuasiStaticModeSeparationDegrees(f):F2}°, cascade residual {b.CascadeResidual:E2}, " +
                $"pair {b.ReciprocalPairResidual:E2}, gauge {b.GaugeResidual:E2}, " +
                $"null-space gap {b.NullSpaceGap:E2}, sign margin {b.SignMargin:F3}.");
        }

        static double EffectivePermittivityOf(Complex gamma, double fHz)
        {
            double b = gamma.Imaginary / (2.0 * Math.PI * fHz / EmConstants.C0);
            return b * b;
        }

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

        // ── THE PATTERN AT A FOUND RESONANCE, KEYED BY FREQUENCY (owner report, 2026-09-11) ─────
        //
        // Everything above is keyed by GRID INDEX, and a resonance the search locates is by
        // definition not on the grid. The consequence, until this store existed, was the worst
        // ordering an antenna tool could have: the resonance search would locate f0 to a hair, and
        // the one frequency the whole run was for had no radiation pattern at it — because the
        // far-field block had already run, over the grid, before the search began.
        //
        // A SECOND STORE rather than re-keying the first. `freqs` is sorted but NOT de-duplicated
        // (the publish step below says so in its own words), so one frequency can name two grid
        // positions and a frequency-keyed merge of the existing store would silently drop a slice.
        // Kept separate, the grid half stays index arithmetic and is byte for byte what it was with
        // the search off, which is the property this file defends everywhere else.
        var farResPatterns = new SortedDictionary<double, PlanarFarFieldPattern[]>();
        var farResMetrics  = new SortedDictionary<double, PlanarMetricReport[]>();
        var farResPol      = new SortedDictionary<double, PlanarPolarizationPattern[]>();

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

                var perCal   = new PlanarPortCalibration[calibrators.Count];
                var perGroup = new PlanarGroupCalibration?[calibrators.Count];
                for (int j = 0; j < calibrators.Count; j++)
                {
                    if (ownStage)
                        control?.SetStageLabel(
                            $"{FormatHz(f)} — calibration standard {j + 1} of {calibrators.Count}");
                    if (calibrators[j].IsGroup) perGroup[j] = calibrators[j].ModalAt(kernelFor, f);
                    else                        perCal[j]   = calibrators[j].At(kernelFor, f);
                    if (ownStage) control?.TickStage();
                }

                // ── PCAL4 — the BLOCK peel, taken only when some port is in a group ──────────────
                //
                // R-pcal4-1: a run with no multi-conductor group must produce the bytes it produces
                // today, and the surest way to guarantee that is for it to run the same code below,
                // not the same arithmetic re-expressed over 1×1 matrices — which is the same
                // arithmetic in a different ORDER, and a different order is a different last bit.
                if (groupCount > 0)
                {
                    var res = DeembedGroupsAt(f, raw, perCal, perGroup, cals);
                    return (res, cals, sw.Elapsed.TotalMilliseconds);
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
                    if (c.NeighbourResonanceDegrees < PlanarPortCalibrator.NeighbourResonanceGuardDegrees
                        && !flaggedResonance.Contains(f)) flaggedResonance.Add(f);
                }

                // R-fed-2 — the lead comes off BEFORE renormalisation: "matched" means matched in
                // Z_c, which is the reference Apply hands back and the one the section is uniform in.
                var atZc = PlanarFeedExtension.Peel(PlanarDeembed.Apply(raw, boxes), peelM, gam);
                s = PlanarDeembed.Renormalise(atZc, zc, z0);
            }
            return (s, cals, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// <b>PCAL4 — the peel when at least one calibration group is present.</b> The group's rows
        /// and columns of the de-embedded matrix are MODAL ports at their own Z_c,m; every other
        /// port's are what they always were. <see cref="PlanarDeembed.ModalToTerminal"/> turns the
        /// first back into the terminal ports the user asked for and renormalises the lot in one
        /// step, through Z, because the modal-to-terminal half is a change of port VARIABLES and has
        /// no meaning as a renormalisation.
        /// </summary>
        Mat<Complex> DeembedGroupsAt(double f, Mat<Complex> raw,
                                     PlanarPortCalibration[] perCal,
                                     PlanarGroupCalibration?[] perGroup,
                                     List<PlanarPortCalibration> cals)
        {
            int p = ports.Count;
            var blocks    = new List<PlanarDeembed.PlanarBlockBox>(p);
            var zModal    = new Complex[p];
            var gam       = new Complex[p];
            var transform = new Mat<Complex>(p, p);
            var handled   = new bool[p];

            for (int i = 0; i < p; i++)
            {
                if (handled[i] || ports[i].Group is not { } g) continue;
                var gc = perGroup[byPort[i]]!;

                var slot = new int[g.ConductorCount];
                for (int k = 0; k < g.ConductorCount; k++)
                {
                    slot[k] = -1;
                    for (int j = 0; j < p; j++) if (ports[j].Number == g.PortNumbers[k]) slot[k] = j;
                    if (slot[k] < 0)
                        throw new InvalidOperationException(
                            $"Calibration group port {g.PortNumbers[k]} is not in the run's port list.");
                }

                blocks.Add(new PlanarDeembed.PlanarBlockBox(slot, gc.Box.A11, gc.Box.A12,
                                                            gc.Box.A21, gc.Box.A22));
                for (int k = 0; k < g.ConductorCount; k++)
                {
                    zModal[slot[k]] = gc.Zc[k];

                    // PCAL5 — after ApplyBlocks this row IS mode k, so the automatic feed lead is
                    // peeled with the MODE's own γ, not a per-conductor one. `peelM` already carries
                    // one length for the whole group: the gate where the group formed declined it
                    // otherwise, which is the condition under which "this mode has travelled ℓ"
                    // means anything at all. A group that grew no lead has peelM = 0 here and
                    // Peel returns its argument untouched, so nothing about PCAL4 moves.
                    gam[slot[k]]     = gc.Box.Gamma[k];
                    handled[slot[k]] = true;
                    for (int m = 0; m < g.ConductorCount; m++)
                        transform[slot[k], slot[m]] = gc.Tv[k, m];
                }

                if (!gc.Usable) flaggedBand.Add(f);
                RecordGroupDiagnostics(f, g, gc, calibrators[byPort[i]]);
            }

            for (int i = 0; i < p; i++)
            {
                if (handled[i]) continue;
                transform[i, i] = Complex.One;

                if (!ports[i].IsDeembeddable)
                {
                    blocks.Add(One(i, IdentityBox));
                    zModal[i] = z0[i];
                    gam[i]    = Complex.Zero;
                    continue;
                }

                var c = perCal[byPort[i]];
                cals.Add(c with { PortNumber = ports[i].Number });
                blocks.Add(One(i, c.Box));
                zModal[i] = c.Zc;
                gam[i]    = c.Gamma.Gamma;
                if (!c.Gamma.Usable) flaggedBand.Add(f);
                if (c.NeighbourResonanceDegrees < PlanarPortCalibrator.NeighbourResonanceGuardDegrees
                    && !flaggedResonance.Contains(f)) flaggedResonance.Add(f);
            }

            var atZc = PlanarFeedExtension.Peel(PlanarDeembed.ApplyBlocks(raw, blocks), peelM, gam);
            return PlanarDeembed.ModalToTerminal(atZc, transform, zModal, z0);

            static PlanarDeembed.PlanarBlockBox One(int index, PlanarErrorBox b)
            {
                Mat<Complex> M(Complex v) { var m = new Mat<Complex>(1, 1); m[0, 0] = v; return m; }
                return new PlanarDeembed.PlanarBlockBox([index], M(b.A11), M(b.A21), M(b.A21), M(b.A22));
            }
        }

        int    solvedCount = freqs.Length;
        double worstAdaptive = double.NaN;
        var    solvedList = Array.Empty<double>();
        bool?  converged  = null;
        var    addedList  = Array.Empty<double>();
        IReadOnlyList<PlanarResonance> resonances = [];

        // ── STOP: FINISH NOW AND KEEP WHAT IS SOLVED (owner request, 2026-09-11) ────────────────
        //
        // Read at the same boundaries the cancellation token is read at, and answered by taking no
        // MORE work rather than by throwing. What each path does with it differs, and the difference
        // is forced by what each path can honestly publish:
        //
        //   fixed grid — every point is solved in order, so a stop truncates the frequency axis to
        //                the prefix that WAS solved. A shorter sweep is a sweep; inventing the rest
        //                would be publishing an interpolation nobody asked for.
        //   adaptive   — the published grid is the REQUESTED grid, modelled from the solved nodes,
        //                which is what the path does at every budget too. So a stop publishes the
        //                whole grid off fewer nodes, and the note says the tolerance was not met.
        //
        // Either way the run is a completed run downstream: the same DataSet, the same cubes, the
        // same `.snp`. What it is not is SILENT — every branch adds its own sentence.
        bool stoppedEarly = false;

        // Stopping during REFINEMENT is a different fact from stopping after it, and only this one
        // is recorded where it happens. `worstStopped` is a max over the intervals refinement
        // actually reached, so on a run cut short it is a max over a set that stopped growing — it
        // reads as a converged verdict when what really happened is that nobody looked. A stop
        // pressed after refinement finished takes nothing away from the model and must not be
        // reported as though it did.
        bool refinementStopped = false;

        if (st.Adaptive is null)
        {
            // L8d's own loop, untouched but for the stop check at its own point boundary.
            foreach (double f in freqs)
            {
                // ONE point is the floor, for the same reason the adaptive path's floor is two: a
                // stop pressed before the first point has finished — during the mesh or the core
                // fill, which on a large board is minutes of its own — would otherwise publish a
                // sweep of NO points. That is not "keep what you solved", it is an empty answer, and
                // it would be written straight over whatever `.snp` the last good run left at the
                // setup's own predictable path.
                if (points.Count >= 1 && control?.StopRequested == true) { stoppedEarly = true; break; }
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
                                                        farSettings!, cap, control, ConductorLoss());
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
            // The basis currents and the raw admittance of a found point, kept for the same reason
            // `currentsByIndex`/`yByIndex` keep the grid's: a far-field pattern is an exact sum over
            // them, and without them a resonance the search located is a frequency no pattern can be
            // taken at. They were dropped on the floor until 2026-09-11 — see the far-field block
            // below the search for what that cost.
            var extraCurrents = new Dictionary<double, Vec<Complex>[]>();
            var extraY        = new Dictionary<double, Mat<Complex>>();
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
                flaggedResonance.Clear();
                var outp = new Dictionary<int, (Mat<Complex>, List<PlanarPortCalibration>, double)>();
                // One stage for the whole replay — most of it is cache hits, so per-point stages here
                // would flicker through every frequency for no information.
                control?.BeginStage("replaying calibration", solved.Count, "point(s)");
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
                flaggedResonance.Clear();
                var outp = new Dictionary<double, (Mat<Complex>, List<PlanarPortCalibration>, double)>();
                control?.BeginStage("replaying calibration", all.Count, "point(s)");
                foreach (double f in all)
                {
                    outp[f] = gridIndexOf.TryGetValue(f, out int gi)
                        ? DeembedAt(f, rawByIndex[gi], () => kernelByIndex[gi], ownStage: false)
                        : DeembedAt(f, extraRaw[f], () => extraKernel[f], ownStage: false);
                    control?.TickStage();
                }
                return outp;
            }

            foreach (int i in PlanarAdaptiveSweep.SeedIndices(freqs.Length, ad.InitialPoints))
            {
                // The seeds are full-wave points like every other, so the stop is read between them
                // too. TWO is the floor rather than zero: everything below this line — the
                // refinement, the search, and the interpolant the requested grid is published from —
                // needs at least two nodes to be a curve rather than a single value, and a result
                // that cannot be published is not what "keep what you have solved" means.
                if (solved.Count >= 2 && control?.StopRequested == true)
                { stoppedEarly = refinementStopped = true; break; }
                Solve(i);
            }
            var byIndex = Replay();

            var work = new List<(int Lo, int Hi)>();
            {
                var seedList = solved.ToList();
                for (int i = 0; i + 1 < seedList.Count; i++) work.Add((seedList[i], seedList[i + 1]));
            }

            double worstStopped = 0;
            while (work.Count > 0 && solved.Count < budget && control?.StopRequested != true)
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

                    // ── THE BOUNDARY THE STOP WAS MISSING (owner report, 2026-09-11) ──────────
                    //
                    // The `while` above reads the stop once per ROUND, and a round is not one solve:
                    // every interval that failed its tolerance splits in two, so the probe list
                    // doubles each time and a late round is sixteen or thirty-two full-wave points,
                    // tens of seconds each. A stop pressed inside one of those rounds was therefore
                    // answered only when the whole round finished — which is exactly the "it kept
                    // solving frequencies and would not stop" that was reported. The stop belongs
                    // where the WORK is, which is per probe.
                    //
                    // Breaking here leaves the round half-taken and that is well-formed: `taken`
                    // carries only the probes that were actually solved, the replay below runs over
                    // `solved` as it now stands, and the error test that follows reads each taken
                    // probe's own solved matrix. The `while` then sees the stop and exits.
                    if (control?.StopRequested == true) { refinementStopped = true; break; }

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

            if (control?.StopRequested == true)
            {
                stoppedEarly = true;
                // Work still on the list when the loop let go: those intervals were never probed,
                // so no verdict below covers them.
                if (work.Count > 0) refinementStopped = true;
            }

            solvedCount   = solved.Count;
            worstAdaptive = worstStopped;
            solvedList    = solved.Select(i => freqs[i]).ToArray();

            // An interval that stopped INSIDE the tolerance converged; one that stopped above it ran
            // out of grid or budget. `worstStopped` is the max over both, so this one comparison is
            // the verdict — and it is computed here rather than inferred from the note, because
            // ANT-9 §4's whole complaint is that a reader was left to infer it.
            // A run that was stopped mid-refinement has NO verdict — not a false one and not a
            // true one. Null is what the result type already has for "not answered".
            converged = refinementStopped ? null : !(worstStopped > ad.Tolerance);

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

                // ── THE FAR-FIELD BLOCK IS ITS OWN STAGE, WITH ITS OWN DENOMINATOR ────────────
                //
                // Reported 2026-09-11: with the resonance search on, the sweep row's point count
                // sat still "for a VERY long number of work cycles" and then started climbing
                // again. It was telling the truth — no new POINT is solved here — but the stage row
                // beside it was no help either: the stage was begun PER PATTERN with a total of the
                // PORT count, so on a one-port it read 1 of 1, finished, and was begun again, over
                // and over, for however many minutes the block took. A bar that completes every
                // time it is looked at says nothing about a block that is running for minutes.
                //
                // One stage for the whole block, counted in PATTERNS, is the honest denominator:
                // this is the one part of a sweep whose cost is a known number of equal pieces. The
                // outer counter is deliberately left alone — it counts points SOLVED, and none are.
                // (The climb the reporter saw afterwards is the search's own probes, which do solve.)
                // The COUNT is not in the label. It used to be — "far field (101 pattern(s))" —
                // and the row then ended in a bare "71 / 101" with the same 101 already said once
                // to its left, directly under a sweep row reading "101 point(s) solved". Three
                // numbers, two of them the same by coincidence, and nothing saying which was which
                // (owner report, 2026-09-11). The denominator's unit belongs to the COUNTER, which
                // is where `unit` puts it; the label is then free to be the changing part it is
                // meant to be.
                control?.BeginStage("far field", chosen.Count, "pattern(s)");
                // ── THE STOP REACHES INTO THIS BLOCK, AT THE PATTERN BOUNDARY ─────────────────
                //
                // This was written the other way first, and the argument for that was: a pattern
                // SOLVES nothing, it is an exact sum over basis currents the run already had, so
                // finishing the block is the "keep what you have solved" a Stop asks for rather
                // than more work it declines. The premise is true and the conclusion does not
                // follow, because the block is not cheap — ANT-12's own measurement, thirty lines
                // up in EmRunService, says a 1 degree x 1 degree hemisphere at N = 1,611 costs
                // ~6.4 s per pattern against ~4.6 s for the de-embedded SOLVE it rides on. At one
                // pattern per solved point that makes the far field the LONGEST block in the run,
                // and a Stop that declined every frequency and then sat through a hundred patterns
                // is a Stop that did not stop (owner report, 2026-09-11 — the second report of the
                // same complaint, on a 101-point patch antenna where the block was still climbing
                // through 71 of 101 long after the button was pressed).
                //
                // ONE pattern is the floor, and that is what survives of the earlier instruction:
                // a stopped antenna run must still publish a far field somebody can plot, and it
                // does. What it no longer does is take the other hundred. `farPatterns` is keyed by
                // index and everything downstream walks its KEYS, so a short set is already a
                // well-formed one — the non-adaptive path has always published a subset this way.
                bool farStopped = false;
                foreach (int i in chosen)
                {
                    if (farPatterns.Count >= 1 && control?.StopRequested == true)
                    { farStopped = true; break; }

                    farWanted.Add(i);
                    // ANT-12 — the de-embedded S of this same point, which `byIndex` already holds
                    // because the refinement loop replayed the calibration over every solved index
                    // before it stopped. That is RealizedGainDbi's one input; see the non-adaptive
                    // loop above for why the RAW admittance is not.
                    var (pats, mets, pols) = FarFieldAt(problem, mesh, currentsByIndex[i],
                                                        yByIndex[i], byIndex[i].S, ports, freqs[i],
                                                        farSettings!, cap, control, ConductorLoss(),
                                                        ownStage: false);
                    farPatterns[i] = pats;
                    farMetrics[i]  = mets;
                    farPol[i]      = pols;
                    control?.TickStage(nextLabel: $"far field — {FormatHz(freqs[i])}");
                }

                // Which of the two things happened has to be SAID, because the cubes look identical
                // either way: a far field over every solved point, and a far field over the first
                // few of them, differ only in how many slices the frequency axis carries.
                if (farStopped)
                {
                    var took = farPatterns.Keys.OrderBy(k => k).ToArray();
                    notes.Add($"The far field was CUT SHORT by the stop: {took.Length} of " +
                              $"{chosen.Count} pattern(s), covering " +
                              $"{FormatHz(freqs[took[0]])} to {FormatHz(freqs[took[^1]])}. Patterns " +
                              $"are taken in ascending frequency, so the ones missing are the top " +
                              $"of the band — including, on a resonant structure, quite possibly " +
                              $"the resonance. Each pattern that WAS taken is complete and exact: " +
                              $"nothing here is interpolated or partial. The s-parameters are " +
                              $"unaffected — they were finished before this block began.");
                }
                else if (stoppedEarly)
                    notes.Add($"The far field was taken in full despite the stop: {chosen.Count} " +
                              $"pattern(s), one at every point the run had actually solved. The " +
                              $"stop arrived after this block, or never had a pattern left to " +
                              $"decline, so every radiation quantity is published exactly as a " +
                              $"completed run publishes it. What the stop changed is how many " +
                              $"SOLVED points there are to take one at, which is the same thing it " +
                              $"changed about the s-parameters.");

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

            if (ad.Search is { } rs && freqs.Length >= 2 && solved.Count >= 2
                && control?.StopRequested != true)
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
                        extraRaw[f]      = r.Raw;
                        extraKernel[f]   = r.Kernel;
                        extraTime[f]     = (r.KernelMs, r.DutMs, r.StandardsMs);
                        extraCurrents[f] = r.Currents;
                        extraY[f]        = r.Y;
                    }
                    var all = UnionAscending();
                    byFreq = ReplayAll(all);
                    return new PlanarResonanceNodes(all, all.Select(x => byFreq[x].S).ToArray());
                }

                var all0 = UnionAscending();
                byFreq = ReplayAll(all0);
                var start = new PlanarResonanceNodes(all0, all0.Select(x => byFreq[x].S).ToArray());

                int port = Math.Clamp(rs.PortNumber - 1, 0, ports.Count - 1);
                // The stop reaches INTO the search, because the search is the long part: it is the
                // one stage that keeps adding solved points after the grid is covered, and it is
                // exactly what the owner was waiting on when they asked for a Stop.
                searchOutcome = PlanarResonanceSearch.Search(
                    start, freqs[0], freqs[^1], z0[port], ad.Interpolant, ad.Tolerance,
                    rs with { PortNumber = port + 1 }, Probe,
                    stopped: control is null ? null : () => control.StopRequested);

                resonances = searchOutcome.Resonances;
                notes.Add(searchOutcome.Note);
                if (searchOutcome.StoppedEarly) stoppedEarly = true;

                // ══════════════════════════════════════════════════════════════════════════════
                // A PATTERN AT EACH FOUND RESONANCE (owner report, 2026-09-11)
                // ══════════════════════════════════════════════════════════════════════════════
                //
                // The far-field block above ran over the grid, BEFORE the search, so on a patch
                // antenna the run published a pattern at every point the sampler happened to solve
                // and none at the frequency the search then went and found. That is the one
                // frequency the user turned both switches on for.
                //
                // THE FIX IS ADDITIVE, AND THAT IS DELIBERATE. Moving the whole far-field block
                // below the search was the other candidate and it is worse: the block maps each
                // REQUESTED far-field frequency onto the nearest solved point, so with the found
                // points in the solved set a requested grid frequency would start being answered by
                // a pattern taken at some frequency the user never asked for, quietly, and the
                // pattern set would stop being reproducible from the request alone. Here the grid
                // half is untouched and the resonances are EXTRA slices that announce their own
                // frequency.
                //
                // "AT f0" MEANS AT THE NEAREST SOLVED POINT TO f0, never at f0 itself, and that is
                // the same rule the grid half already states: a pattern is an exact sum over basis
                // currents, and f0 is a ROOT located between two solved ends rather than a point
                // anybody solved. The search brackets it to PlanarResonanceSettings.FrequencyTolerance,
                // so the point this lands on is inside that bracket — which is what makes the
                // substitution worth making rather than worth refusing. The distance is REPORTED.
                if (farSettings is not null && farVerdict.Ok && searchOutcome.Resonances.Count > 0)
                {
                    // Every frequency the run has basis currents for: the solved grid points and the
                    // points the search added. An interpolated point is not a candidate and never
                    // can be — it has no currents behind it.
                    var haveCurrents = new List<double>(solved.Count + extraCurrents.Count);
                    foreach (int i in solved) haveCurrents.Add(freqs[i]);
                    haveCurrents.AddRange(extraCurrents.Keys);

                    var resMoved = new List<(double F0, double At)>();
                    var resOnGrid = new List<double>();
                    bool resStopped = false;

                    // Which ones actually need a pattern computed, worked out BEFORE the stage is
                    // begun so the denominator is the real count rather than the resonance count —
                    // a resonance that landed on a grid point the far-field block already covered
                    // costs nothing and must not be counted as work.
                    var todo = new List<(double F0, double At)>();
                    foreach (var r in searchOutcome.Resonances)
                    {
                        double at = haveCurrents[0];
                        foreach (double f in haveCurrents)
                            if (Math.Abs(f - r.FrequencyHz) < Math.Abs(at - r.FrequencyHz)) at = f;

                        // Already drawn by the grid block? Then the resonance is served and there is
                        // nothing to add. This is the good case and it is silent on purpose.
                        if (gridIndexOf.TryGetValue(at, out int gi) && farPatterns.ContainsKey(gi))
                        { resOnGrid.Add(r.FrequencyHz); continue; }

                        if (farResPatterns.ContainsKey(at) || todo.Any(t => t.At == at)) continue;
                        todo.Add((r.FrequencyHz, at));
                    }

                    if (todo.Count > 0)
                    {
                        control?.BeginStage("far field at the resonance", todo.Count, "pattern(s)");
                        foreach (var (f0, at) in todo)
                        {
                            // The stop reaches in here for the same reason it reaches into the grid
                            // block: a pattern is not cheap, and a Stop that then sat through a
                            // further set of them is a Stop that did not stop. There is no floor of
                            // one here — the grid block has already published a far field somebody
                            // can plot, so declining all of these still leaves a well-formed result.
                            if (control?.StopRequested == true) { resStopped = true; break; }

                            var cur = gridIndexOf.TryGetValue(at, out int gi2) && currentsByIndex.TryGetValue(gi2, out var cc)
                                        ? cc : extraCurrents[at];
                            var yy  = gridIndexOf.TryGetValue(at, out int gi3) && yByIndex.TryGetValue(gi3, out var yv)
                                        ? yv : extraY[at];

                            // The DE-EMBEDDED S of this same frequency, out of the union replay —
                            // RealizedGainDbi's only input, and ANT-12 measured that reading the raw
                            // self-admittance instead runs it 15 dB low at a de-embedded edge port.
                            var (pats, mets, pols) = FarFieldAt(problem, mesh, cur, yy, byFreq![at].S,
                                                                ports, at, farSettings, cap, control,
                                                                ConductorLoss(), ownStage: false);
                            farResPatterns[at] = pats;
                            farResMetrics[at]  = mets;
                            farResPol[at]      = pols;
                            if (Math.Abs(at - f0) > 0) resMoved.Add((f0, at));
                            control?.TickStage(nextLabel: $"far field at the resonance — {FormatHz(at)}");
                        }
                    }

                    if (farResPatterns.Count > 0)
                        notes.Add($"{farResPatterns.Count} additional far-field pattern(s) were taken " +
                                  $"AT the resonance(s) the search located, over and above the one per " +
                                  $"solved grid point above. The grid block runs before the search, so " +
                                  $"without these a run that located a resonance would carry no " +
                                  $"radiation pattern at it. These slices are extra frequencies in the " +
                                  $"far-field set, not replacements for any requested one.");

                    foreach (var (f0, at) in resMoved)
                        notes.Add($"The pattern for the resonance at {FormatHz(f0)} was taken at " +
                                  $"{FormatHz(at)}, the nearest SOLVED frequency — a pattern needs " +
                                  $"basis currents and f0 is a root located BETWEEN solved points, " +
                                  $"not a point anybody solved. The offset is " +
                                  $"{FormatHz(Math.Abs(at - f0))}; compare it against that " +
                                  $"resonance's own half-power bandwidth before reading the pattern " +
                                  $"as the pattern at resonance.");

                    if (resOnGrid.Count > 0)
                        notes.Add($"{resOnGrid.Count} located resonance(s) fell on a solved grid point " +
                                  $"that already had a pattern, so no extra one was taken.");

                    if (resStopped)
                        notes.Add($"The resonance far field was CUT SHORT by the stop: " +
                                  $"{farResPatterns.Count} of {todo.Count} pattern(s). The " +
                                  $"s-parameters and the located resonances are unaffected — both " +
                                  $"were finished before this block began.");
                }
            }
            else if (ad.Search is not null && freqs.Length >= 2 && solved.Count >= 2
                     && control?.StopRequested == true)
            {
                // A search that was never STARTED still has to be accounted for. The condition above
                // declines it silently, and a user who asked for a resonance search and got a run
                // with no resonances in it would have no way to tell that from a run that looked and
                // found none — which is the opposite of what the search is for.
                notes.Add("The resonance search was NOT run: the stop was asked for before it began. " +
                          "No resonance in this result means none was looked for, not that none is " +
                          "there — and no frequency was added between the ones you asked for.");
            }

            // A stop that arrives after refinement — during the far field, or while the search was
            // being declined above — sets nothing on its own, and a result that says nothing about
            // it looks exactly like a run nobody touched. Everything it changed is described by the
            // notes around it; this is what guarantees there is a sentence saying it happened.
            if (control?.StopRequested == true) stoppedEarly = true;

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
            if (refinementStopped)
                // Neither verdict is available here, so neither is printed. The number still is —
                // it just has to be said what it is a maximum OVER, because an unprobed interval
                // contributes nothing to it and there is no way to tell from the value alone.
                adaptiveNote.Append("Adaptive frequency sampling was STOPPED at the user's request " +
                                    "before it reached a verdict: the worst disagreement it had " +
                                    "measured by then is |ΔS| = ")
                            .Append(worstStopped.ToString("G3"))
                            .Append(", against a tolerance of ")
                            .Append(ad.Tolerance.ToString("G3"))
                            .Append(" — but that is a maximum over the intervals it had reached, " +
                                    "and the ones it never probed are not in it at all. ");
            else
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

            // Not on a stopped run: the remedy there is not a finer grid, it is not stopping, and
            // sending a user to change their sweep over a result they cut short themselves is
            // advice that costs them a re-run to discover was never needed.
            if (!didConverge && !refinementStopped)
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

            // LF1/LF2 — the count above is over the FULL-WAVE grid, and that is not the whole sweep
            // when 0 Hz or a point below the field solver's range was asked for: both were taken off
            // the front before any of this ran. Without this sentence "9 of 16 were solved" is read
            // against a seventeen-point sweep as "and the other one was modelled", which is the
            // reading that sent an owner looking for a DC point that had in fact been solved.
            if (wantDc || subFloor.Length > 0)
            {
                bool   many  = (wantDc ? 1 : 0) + subFloor.Length > 1;
                string below = subFloor.Length == 1
                    ? "the point below the field solver's range"
                    : $"the {subFloor.Length} points below the field solver's range";
                string subject = wantDc && subFloor.Length > 0
                    ? $"The 0 Hz point and {below}"
                    : wantDc
                        ? "The 0 Hz point"
                        : $"{char.ToUpperInvariant(below[0])}{below[1..]}";
                adaptiveNote.Append(subject)
                            .Append(many
                                ? " are not in that count and were never candidates for modelling: " +
                                  "the conduction solve answers them directly, and they are " +
                                  "published as SOLVED points. "
                                : " is not in that count and was never a candidate for modelling: " +
                                  "the conduction solve answers it directly, and it is published " +
                                  "as a SOLVED point. ");
            }

            adaptiveNote.Append(extraRaw.Count > 0
                ? $"{extraRaw.Count} further frequency(ies) were ADDED by the resonance search and " +
                  "are flagged as such; every point of your own grid is published exactly as it was. "
                : "Every published frequency is the one you asked for. ");

            adaptiveNote.Append("A solved point carries the solver's own matrix exactly, and its " +
                                "per-port calibration diagnostics are carried from the nearest solved " +
                                "frequency rather than interpolated.");

            notes.Add(adaptiveNote.ToString());
        }

        // ── STOP — the sentence, and it is never left out ──────────────────────────────────────
        //
        // A stopped run publishes a complete, ordinary result, which is exactly what makes saying so
        // mandatory: nothing in the DataSet, the `.snp` or the plot distinguishes it from a run that
        // finished. The note says which of the two shapes it took, and what is therefore NOT in the
        // answer, so a number read off it is read knowing that.
        if (stoppedEarly)
            notes.Add(st.Adaptive is null
                ? $"STOPPED EARLY at the user's request: {points.Count} of {freqs.Length} requested " +
                  $"frequency point(s) were solved, and the sweep published here is that PREFIX — " +
                  $"the frequencies above " +
                  $"{(points.Count > 0 ? SurfaceMesher.Eng(points[^1].FrequencyHz) + "Hz" : "the start")} " +
                  $"are not in it and were not estimated. Everything that IS here is the same " +
                  $"answer a completed run would have given for those points; nothing is " +
                  $"interpolated and nothing is approximate."
                : $"STOPPED EARLY at the user's request after {solvedCount} solved point(s). The " +
                  $"full requested grid IS published — that is what adaptive sampling does at every " +
                  $"budget, modelling the points it did not solve from the ones it did — " +
                  // The stop is only a TRUNCATION OF THE MODEL if refinement still had work to do.
                  // A stop pressed after the last interval had already met the tolerance took
                  // nothing away from the s-parameters, and claiming it did would send a reader
                  // hunting for missing accuracy that is not missing — the thing it actually
                  // declined is whatever followed, and each of those says so in its own note.
                  (converged == true && !refinementStopped
                      ? "and refinement had already met its tolerance before the stop, so the model " +
                        "is the one a completed run would have published. What the stop declined is " +
                        "the work that comes AFTER refinement; each part of it has its own note here."
                      : "but it is modelled from FEWER nodes than the tolerance asked for, so read " +
                        "the adaptive note beside this one for the disagreement that was actually " +
                        "reached rather than assuming the requested tolerance was met."));

        // ── PEEL — THE PEEL'S OWN CONDITIONING, WHICH NOTHING PUBLISHED COULD SEE BEFORE ───────
        //
        // The defect this closes is not that a de-embedded point at the bottom of a band is wrong.
        // It is that it arrived with NO SENTENCE ATTACHED, and that the one diagnostic a reader
        // would have checked — DeembedResidual — is anti-correlated with the truth down there.
        //
        // A POINT THE PEEL CANNOT ANSWER IS DROPPED; THE SWEEP IS NOT THROWN AWAY FOR IT. That is
        // this file's own house shape, stated a few hundred lines down where a far field is refused:
        // "present and refused — the sweep is not thrown away for a diagnostic it cannot produce".
        // The first version of this guard refused the RUN, which costs a user every other frequency
        // they waited for — on the reported sweep, nine solved points discarded to suppress two.
        // The only case that is still a refusal is the one where every de-embedded point went, since
        // there is then no result to hand back and nothing is lost by saying so.
        //
        // Asked of the points the sweep already produced, so nothing in the hot path moved and a
        // run that is nowhere near either threshold takes exactly the arithmetic it took before.
        // It runs BEFORE the passivity note on purpose: a point that is about to be left out must
        // not also be reported as a passivity violation, which would be one defect described twice.
        if (st.Deembed && MeasurePeelConditioning(points) is { } peelCond &&
            (peelCond.Flagged.Count > 0 || peelCond.Unanswerable.Count > 0))
        {
            bool drop = peelCond.Unanswerable.Count > 0 && !st.DeembedOutsideCalibrationValidity;
            int deembedded = points.Count(pt => pt.FrequencyHz > 0 && pt.Calibrations.Count > 0);
            bool everythingWent = drop && peelCond.Unanswerable.Count >= deembedded;

            string sentence = PeelConditioningNote(peelCond, everythingWent);
            if (everythingWent) throw new PlanarFeedClearanceRefusedException(sentence, []);

            if (drop) points.RemoveAll(pt => PointFloor(pt) >= PeelErrorRefusalDS);
            notes.Add(sentence);
        }

        IReadOnlyList<PlanarPassivityExcess> nonPassive = [];
        string nonPassiveCause = "";
        if (st.Deembed)
        {
            nonPassive = PassivityExcesses(points, z0);

            // ── MIM-9 items 1 and 4 — the attribution, decided ONCE, from the run's own counters.
            //
            // Computed here rather than inside the note because the .sNp caveat needs the same
            // string: the notes do not survive onto the file, and the file is what a reader opens
            // six months later. Both the excess and the peel's own error floor are already in hand
            // — nothing is solved or re-derived for this.
            double worstExcess = 0;
            foreach (var e in nonPassive) worstExcess = Math.Max(worstExcess, e.SigmaMax - 1.0);
            double worstFloor = 0;
            foreach (var pt in points) worstFloor = Math.Max(worstFloor, PointFloor(pt));
            nonPassiveCause = nonPassive.Count == 0
                ? ""
                : NonPassivityCause(worstExcess, worstFloor, WorstLevelPair(problem, mesh));

            // EM-SEV R-emsev-1 — a WARNING, not a note. This sentence says outright that the
            // s-parameters at those points should not be used, which is the definition of "an
            // answer was produced and something in it is not what was drawn". It reached the
            // Messages panel as Info, ranked level with the core count, because `notes` is a
            // List<EmFinding> and a bare string converts to EmFinding.Note. User-reported
            // 2026-09-16: a run that was 75% non-passive read as thirty ordinary lines.
            if (PassivityNote(nonPassive, points.Count, nonPassiveCause) is { } passivity)
                notes.Add(EmFinding.Warn(passivity));
        }

        // ── PCAL3 — THE INSTRUMENT'S OWN RESONANCE, NAMED RATHER THAN PUBLISHED SILENTLY ────────
        //
        // A standard widened to reproduce a passive neighbour contains a conductor open at both ends
        // and driven by nothing, so at βL = nπ it is a resonator and the two-line cascade stops
        // describing one mode. The DUT's neighbour is a different length and does not resonate
        // there — this is the instrument, not the design — and every other frequency comes back AT
        // the A-vs-B floor, which is why the points are named rather than the run refused.
        if (flaggedResonance.Count > 0)
        {
            flaggedResonance.Sort();
            notes.Add(
                $"{flaggedResonance.Count} of {freqs.Length} frequency point(s) sit within " +
                $"{PlanarPortCalibrator.NeighbourResonanceGuardDegrees:F0}° of a HALF-WAVE RESONANCE " +
                "of the passive neighbour the calibration standard reproduces — first at " +
                $"{SurfaceMesher.Eng(flaggedResonance[0])}Hz. That conductor is open at both ends " +
                "in the standard, so at βL = nπ its own standing wave dominates the standard's " +
                "response and the two-line calibration stops measuring a single mode; the " +
                "de-embedded s-parameters at those points carry a much larger error than the rest " +
                "of the sweep (measured at |ΔS| 0.22 against 0.04 elsewhere on the fixture this was " +
                "developed against). It is a property of the STANDARD's length, not of your design: " +
                "your neighbour is whatever length you drew. Narrow the sweep past those points, " +
                "separate the feeds so no neighbour has to be reproduced, or read them knowing this.");
        }

        // ── PCAL4/gate 5 — THE MODAL DIAGNOSTICS, PER FREQUENCY, IN THE RUN'S OWN NOTES ────────
        //
        // R-pcal4-2 and R-pcal4-3 both ask for these to be REPORTED rather than merely acted on, and
        // PCAL1 measured why the summary has to name its frequency: the error is worst at the BOTTOM
        // of the band in every case measured, so a sweep average would hide exactly the point that
        // decides whether the answer is usable. The per-point lines follow the summary.
        if (groupDiagnostics.Count > 0)
        {
            notes.Add(
                $"MODAL CALIBRATION over {groupDiagnostics.Count} point(s). Worst mode separation " +
                $"{worstSeparation:F2}° at {SurfaceMesher.Eng(worstSeparationF)}Hz (floor " +
                $"{calSt.ModeSeparationFloorDegrees:F2}°) — this is the quantity that decides whether " +
                "the modes can be told apart at all, and everything else here is conditional on it. " +
                $"Worst discarded cascade residual {worstCascade:E2} at " +
                $"{SurfaceMesher.Eng(worstCascadeF)}Hz; worst modal-gauge residual {worstGauge:E2} at " +
                $"{SurfaceMesher.Eng(worstGaugeF)}Hz; worst eigenvector null-space gap " +
                $"{worstNullGap:E2} at {SurfaceMesher.Eng(worstNullGapF)}Hz; smallest per-mode sign " +
                $"margin {worstSign:F3} at {SurfaceMesher.Eng(worstSignF)}Hz; characteristic " +
                $"polynomial palindrome residual {worstPalindrome:E2}. These are honest measures of " +
                "what the modal extraction discarded and of how well determined its choices were; " +
                "they are NOT an error bound, and PCAL1 measured that the scalar versions of them are " +
                "not even correlated with the de-embedding error (src/Engine/Mom/RESOLVED.md, PCAL1 " +
                "§5).");

            notes.Add(
                $"MODAL REFERENCE IMPEDANCE (R-pcal4-4, reported SEPARATELY from the de-embedding's " +
                "own accuracy because they are two different things). Z_c,m = γ_m/(jωC_m) takes γ " +
                "full-wave from the calibration and C from the standards' own electrostatics, so the " +
                "reference impedance is quasi-static and the de-embedding is not. The two routes' β " +
                $"disagree by at most {worstQuasiStatic:P2} at " +
                $"{SurfaceMesher.Eng(worstQuasiStaticF)}Hz, which is the size of that quasi-static " +
                $"assumption on this cross-section; the lossless modal reduction of [C] discarded at " +
                $"most {worstModeCoupling:E2} of its own diagonal. A run in which these are large has " +
                "a perfectly good de-embedding and a reference impedance that is out by about that " +
                "much, and the two should never be read as one figure of merit.");

            foreach (string line in groupDiagnostics) notes.Add("  " + line);
        }

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
        else if (farPatterns.Count > 0 || farResPatterns.Count > 0)
        {
            // ── ONE ASCENDING LIST, OUT OF TWO STORES ───────────────────────────────────────
            //
            // The grid half is walked in INDEX order (see the store's own comment for why it is
            // not re-keyed by frequency) and the resonance half in frequency order; the two are
            // then merged by a STABLE sort, so a duplicated grid frequency keeps its index order
            // and a resonance slice lands between the grid slices that bracket it. With the
            // resonance search off the second store is empty and this produces the same list,
            // in the same order, that the index walk produced on its own.
            var slices = new List<(double F, PlanarFarFieldPattern[] P, PlanarMetricReport[] M,
                                   PlanarPolarizationPattern[] L)>();
            foreach (int i in farPatterns.Keys.OrderBy(i => i))
                slices.Add((freqs[i], farPatterns[i], farMetrics[i], farPol[i]));
            foreach (var (f, pats) in farResPatterns)
                slices.Add((f, pats, farResMetrics[f], farResPol[f]));
            slices = slices.OrderBy(x => x.F).ToList();

            // brief-em3d-31 — the sets are assembled by the solver-agnostic stage the 3D solvers share.
            var assembled = FarFieldStage.Assemble(
                farSettings!.EffectiveGrid,
                ports.Select(pp => pp.Number).ToArray(),
                [.. slices.Select(x => new FarFieldSlice(x.F, x.P, x.M, x.L))]);
            farSet    = assembled.FarField;
            metricSet = assembled.Metrics;
            polSet    = assembled.Polarization;

            var first = farSet.At(0, 0);
            notes.Add(first.ScaleCaption);
            notes.Add("The pattern is the radiation of the structure AS MESHED, which includes any " +
                      "uniform feed lead R-fed-1 grew for the calibration. The s-parameters beside it " +
                      "have that lead de-embedded away; a pattern cannot, because the lead's current " +
                      "is real current and it really radiates.");

            // ── ANT-5 — the metrics. Every refusal is said ONCE, in the registry's own wording, and
            //    the sweep is never thrown away for one of them (present and refused).
            var firstMetrics = metricSet.At(0, 0);
            notes.Add(firstMetrics.Budget.Caption);
            if (firstMetrics.Budget.SurfaceWave is { } guided) notes.Add(guided.Caption);
            notes.Add(firstMetrics.Budget.BoundNoteForRun);
            foreach (string refusal in metricSet.Refusals) notes.Add(refusal);

            // ── ANT-6 — polarization. The reference angle is REPORTED whether it was named or
            //    derived (R-ant-9), and the mesh-limited cross-pol floor is said wherever a cross-pol
            //    number is (R-ant-11) rather than left for a user to discover.
            var firstPol = polSet.At(0, 0);
            notes.Add(firstPol.ScaleCaption);
            if (firstPol.Reference is { } reference) notes.Add(reference.Note);
            if (polSet.CoCrossVerdict.Ok) notes.Add(PlanarPolarization.MeshFloorNote);
            foreach (string refusal in polSet.Refusals) notes.Add(refusal);
        }

        // ── LF1/LF2 — and the points that were taken off the front go back on it ────────────────
        //
        // One conduction solve serves both: 0 Hz and every sub-floor point are the SAME answer at
        // different labels, and solving it twice would be two chances to disagree.
        if (wantDc || subFloor.Length > 0)
        {
            var dc = PlanarDcSolve.Solve(problem, mesh, ports, leads);
            notes.AddRange(EmFindings.AsNotes(dc.Notes));
            for (int i = subFloor.Length - 1; i >= 0; i--)
                points.Insert(0, ConductionPoint(subFloor[i],
                                                 dc, i == 0 && !wantDc ? dc.ElapsedMs : 0));
            if (subFloor.Length > 0)
                notes.Add(ConductionSubstitutionNote(subFloor, stackHeightM, problem.MaxFrequencyHz));
            if (wantDc) points.Insert(0, DcPoint(dc));

            // ── AND SO DOES THEIR PROVENANCE: THESE POINTS WERE SOLVED (owner report, 2026-09-16)
            //
            // `solvedList` is the ADAPTIVE path's set of solved grid positions, and it is built from
            // `freqs` — which is the grid with 0 Hz and the sub-floor points already taken off it.
            // So a sweep that asked for DC published it with PointSolved = 0, i.e. "this matrix came
            // out of the interpolant through the points that were solved". That is exactly backwards:
            // the DC row is the one row of the file nothing is interpolated into. It is a conduction
            // solve of the meshed structure (LF1), the answer an HB or s-parameter run reads the bias
            // path off, and a reader checking the mask before trusting it was told the opposite.
            //
            // The sub-floor points go in for the same reason and it is the same reason, not a second
            // one: they carry that solve's answer at the user's own frequency (LF2) and no
            // interpolant touches them either. What they ARE is said by ConductionSubstitutionNote
            // above, which is where "resistance only, no reactance" belongs — the mask answers a
            // narrower question ("was this computed, or modelled from the ones that were?") and for
            // these points the answer is computed.
            //
            // Only when the list is non-empty: an EMPTY solved list is the non-adaptive convention
            // for "every published point was solved" (SampleProvenance.BuildSolvedCube fills ones),
            // and seeding it here would invert it into "only DC was solved".
            if (solvedList.Length > 0)
            {
                var withLowFrequency = new List<double>(1 + subFloor.Length + solvedList.Length);
                if (wantDc) withLowFrequency.Add(0.0);
                withLowFrequency.AddRange(subFloor);
                withLowFrequency.AddRange(solvedList);
                solvedList  = withLowFrequency.ToArray();
                solvedCount = solvedList.Length;
            }
        }

        return new PlanarSolveResult
        {
            Points        = points,
            CoreFillCount = cores,
            UnknownCount  = mesh.Bases.Count,
            StandardCount = standards,
            CoreBuildMs   = coreBuildMs,
            Findings      = notes,
            NonPassivePoints = nonPassive,
            NonPassivityCause = nonPassiveCause,
            FeedClearances = clearances,
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
        double fHz, PlanarFarFieldSettings settings, int? cap, RunControl? control,
        PlanarConductorLossInputs? conductorLoss, bool ownStage = true)
    {
        // ownStage false when the CALLER is running a block of these and counting them itself — the
        // adaptive path's far-field block. Beginning a stage per pattern there resets a bar that is
        // measuring the whole block, and on a one-port it reset it to "1 of 1" every time.
        if (ownStage)
            control?.BeginStage($"far field at {SurfaceMesher.Eng(fHz)}Hz", ports.Count, "port(s)");
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
                deembeddedS is { } sm ? sm[j, j] : null,
                conductorLoss);
            (metrics[j], pol[j]) = FarFieldStage.Evaluate(context);
            if (ownStage) control?.TickStage();
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
    ///
    /// <para><b>The measurement and the sentence are two functions since R-pcal7-4</b>, because the
    /// answer is needed as DATA as well as as prose. THIS one is the measurement: every de-embedded
    /// point that cannot be a network, ascending in frequency; <see cref="PassivityNote"/> below is
    /// the prose, and <c>EmSnpProvenance.ValidityCaveats</c> is the other reader.</para>
    ///
    /// <para><b>R-pcal7-4 — it is returned as data because the note does not survive the file.</b>
    /// That is PCAL2's own finding one step further on: a `.sNp` on disk carries no notes, and the
    /// shipped spiral's bottom decade is exactly the case — 110 nH against a real 2.8 nH at
    /// 160 MHz, with a 9 % passivity excess, at the end of a long notes list. The caveat line
    /// <c>EmSnpProvenance</c> stamps onto the Touchstone is what a reader of that file gets
    /// instead, and it needs the frequencies rather than a sentence.</para>
    /// </summary>
    private static IReadOnlyList<PlanarPassivityExcess> PassivityExcesses(
        IReadOnlyList<PlanarFrequencyPoint> points, IReadOnlyList<Complex> z0)
    {
        const double Tolerance = 1e-3;

        bool uniformReal = true;
        foreach (var z in z0)
            if (z.Imaginary != 0 || z != z0[0]) { uniformReal = false; break; }

        var common = new Complex[z0.Count];
        Array.Fill(common, new Complex(50.0, 0.0));

        var over = new List<PlanarPassivityExcess>();
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
            over.Add(new PlanarPassivityExcess(pt.FrequencyHz, sigma));
        }
        return over;
    }

    /// <param name="attribution">
    /// The cause clause, from <see cref="NonPassivityCause"/> — the one place it is decided. It is a
    /// PARAMETER rather than a call from inside here because the same string goes onto the
    /// <c>.sNp</c> through <see cref="PlanarSolveResult.NonPassivityCause"/>, and the two readers
    /// must not be able to disagree about what the run blamed (MIM-9 item 4).
    /// </param>
    private static string? PassivityNote(
        IReadOnlyList<PlanarPassivityExcess> over, int pointCount, string attribution)
    {
        if (over.Count == 0) return null;

        double worst = 0, atHz = 0;
        foreach (var e in over) if (e.SigmaMax > worst) { worst = e.SigmaMax; atHz = e.FrequencyHz; }

        // ── R-pcal7-4 — THE FREQUENCIES, NOT JUST THE COUNT ──────────────────────────────────────
        //
        // "4 of 50 points" tells a user that some of their plot is wrong and not WHICH of it. On the
        // shipped spiral the four are the bottom of the band, which is the decade a reader looks at
        // first, and the numbers there are 30x out. The band is quoted the way PEEL's own drop note
        // quotes its dropped points, because a user acts on it the same way — by moving the sweep's
        // lower edge.
        string which = over.Count == 1
            ? $"at {SurfaceMesher.Eng(over[0].FrequencyHz)}Hz"
            : $"from {SurfaceMesher.Eng(over[0].FrequencyHz)}Hz to " +
              $"{SurfaceMesher.Eng(over[^1].FrequencyHz)}Hz";

        return $"NOT PASSIVE: {over.Count} of {pointCount} de-embedded point(s) have σ_max(S) > 1, " +
               $"{which}, worst {worst:F4} at {SurfaceMesher.Eng(atHz)}Hz. A passive structure cannot " +
               "do that, so the excess is this analysis, not your design, and the s-parameters at " +
               $"those points should not be used. {attribution}";
    }

    /// <summary>
    /// <b>MIM-9 items 1 and 4 — WHY the answer is not a network, taken from the run's own counters,
    /// and written in exactly ONE place.</b>
    ///
    /// <para><b>The defect this closes cost a user days.</b> The sentence that shipped named the
    /// de-embedding as "the usual cause" on every non-passive run, and offered three remedies. On
    /// the class of run that actually produced it, three of those four clauses are false and the
    /// engine already computes the numbers that say so:</para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>Not the de-embedding.</b> The identical structure with the second
    ///   level removed — same artwork, same feeds, same ports, same technology — is passive at
    ///   σ_max = 0.9986 and reads a fringing capacitance flat to 0.4% over 3:1 in frequency. And the
    ///   peel's OWN error estimate is the arithmetic that settles it: <c>DeembedErrorFloor</c>
    ///   1.6e-3 against a non-passivity excess of 0.73, three decades apart.</description></item>
    ///   <item><description><b>No well-conditioned sub-band to narrow to.</b> Non-passive at 1, 2
    ///   and 3 GHz alike, incoherent below 1 GHz.</description></item>
    ///   <item><description><b>Cells per wavelength is inert.</b> MIM-9 measured σ_max unchanged
    ///   to four decimals at 1.0443 from 20 to 400 on the structure that produced this; MIM-14
    ///   re-measured the knob itself on the repaired kernel and it is inert for a structural reason
    ///   rather than a coincidental one — on a plate small enough to sit this close to another
    ///   level the mesh is BIT-IDENTICAL at 20, 40, 100 and 400, because the pitch is set by the
    ///   metal's own width and not by λ.</description></item>
    /// </list>
    ///
    /// <para><b>So the attribution follows the counters rather than a fixed guess</b>, and it is a
    /// separate function from the sentence around it for the reason item 4 gives: the guess was
    /// written TWICE, here and in <c>EmSnpProvenance.ValidityCaveats</c>, independently. The second
    /// copy outlives the session — it is stamped into the <c>.sNp</c> and is the first thing anyone
    /// reads six months later — so fixing one and not the other would leave every file already
    /// written saying the wrong thing. This is computed once per run and carried on
    /// <see cref="PlanarSolveResult.NonPassivityCause"/>; both readers take it from there.</para>
    ///
    /// <para><b>ASCII, deliberately, and it costs the panel a subscript.</b> Touchstone is written
    /// in an encoding <c>EmSnpProvenance</c> transliterates to, and a σ or an a₂₁ comes out of it as
    /// "?" — measured on that very line. One function producing two spellings is two chances to
    /// drift; one function producing one string that both surfaces can carry is not.</para>
    /// </summary>
    /// <param name="excess">The worst σ_max − 1 over the sweep: how much gain has to be explained.</param>
    /// <param name="peelErrorFloor">The worst <see cref="PlanarErrorBox.DeembedErrorFloor"/> over the
    /// sweep — the peel's own estimate of its own error, in |S|, and therefore the one number that
    /// can say whether the de-embedding is even capable of accounting for the excess.</param>
    /// <param name="pair">The worst adjacent level pair, when the mesh has one.</param>
    internal static string NonPassivityCause(
        double excess, double peelErrorFloor, PlanarLevelPair? pair)
    {
        // Microns spelled out rather than through PlanarLengthFormat, and that is the ASCII rule
        // above rather than an oversight: every length format in this area reaches for "µ", which is
        // the one character the Touchstone writer cannot carry. The panel loses the document's own
        // unit on this one clause; the file gains a caveat that can be read.
        static string Um(double m) =>
            (m * 1e6).ToString("G4", CultureInfo.InvariantCulture) + " um";

        // ── 1. A CONDUCTOR PAIR PAST THE FULL-WAVE FLOOR ────────────────────────────────────────
        //
        // Asked first because it is a property of the STRUCTURE and dominates whatever else is true
        // of the sweep. PlanarSolve.Run cannot reach this arm today — LevelSeparationVerdict refuses
        // such a run before a matrix is filled — and it is here rather than deleted because
        // FullWaveCellOverSeparation is a measurement's name and has already moved once: MIM-14 took
        // it from 40 to 200 when the ladder was re-run on MIM-12a's repaired kernel. It is reached
        // today through this function's own gate, and through any caller that measures a sweep it
        // did not solve.
        //
        // MIM-14 ALSO CHANGED WHAT IT CAN HONESTLY CLAIM. The sentence used to say the series
        // element "loses its magnitude and then its sign", which was MIM-12's measurement of the
        // broken kernel. Past 200 there is now no measurement at all — the two-port and the
        // cross-level fill both stop there — so it says unmeasured, which is what it is.
        if (pair is { } lp && lp.CellOverSeparation > PlanarLevels.FullWaveCellOverSeparation)
            return $"The cause is the conductor pair at levels {lp.Lower} and {lp.Lower + 1}, " +
                   $"{Um(lp.SeparationM)} apart with a straddling cell of {Um(lp.CellM)} - " +
                   $"cell/separation = {lp.CellOverSeparation:G3}, past the " +
                   $"{PlanarLevels.FullWaveCellOverSeparation} a de-embedded two-port of a close " +
                   $"level pair is measured over. Inside that range the series element between the " +
                   $"two levels comes back within 10% of the electrostatic mutual capacitance of " +
                   $"the same mesh and every point is passive; past it nothing is measured, and a " +
                   $"gain is what an unresolved close pair looks like in S. Thicken the film " +
                   $"between those levels in the technology, or take the upper level out of the EM " +
                   $"run and model the part it carries as a circuit element. Cells per wavelength " +
                   $"does NOT act here and has been measured not to: on a plate small enough to sit " +
                   $"this close to another level, 20 to 400 leaves the mesh bit-identical, because " +
                   $"that knob sets the pitch along the current and the metal's own width sets it " +
                   $"across.";

        // ── 2. THE DE-EMBEDDING, WHEN ITS OWN COUNTERS CAN ACCOUNT FOR THE EXCESS ───────────────
        //
        // The sentence that shipped, kept verbatim in substance, because it is right for the case it
        // was written for — the shipped spiral's bottom decade, where the floor genuinely reaches
        // the size of the excess. What changed is that it is now EARNED. DeembedErrorFloor is the
        // peel's own estimate of its own error in |S|; where it is at least the excess, the peel can
        // produce that much gain on its own and is the first thing to check. Where it is decades
        // below, it arithmetically cannot, and saying so anyway is the misdiagnosis.
        if (peelErrorFloor >= excess && excess > 0)
            return "The usual cause on a run like this one is the de-embedding rather than the " +
                   "fill: the peel divides by a21 squared (~1e4 at 1 GHz), so a small error in the " +
                   "error box becomes a large one in the answer - and the peel's own estimate of " +
                   $"that error, DeembedErrorFloor, reads {peelErrorFloor:G3} in |S| here against " +
                   $"an excess of {excess:G3}, so it is large enough to be the whole of it. Check " +
                   "the port notes above for a feed the calibration could not be measured on, or " +
                   "narrow the sweep to where the two-line calibration is well conditioned.";

        // ── 3. NOT ATTRIBUTED, AND SAYING SO IS THE POINT ───────────────────────────────────────
        //
        // The honest answer when neither of the two above holds, and the one the shipped sentence
        // never had: it named a cause anyway. The number it exonerates the peel WITH is quoted,
        // because a reader who has been told "not the de-embedding" will otherwise go and check it.
        string peel = peelErrorFloor > 0
            ? $"It is NOT the de-embedding: the peel's own estimate of its own error, " +
              $"DeembedErrorFloor, reads {peelErrorFloor:G3} in |S| here against an excess of " +
              $"{excess:G3} - too small to account for it by " +
              $"{(excess / peelErrorFloor):G3}x."
            : "The de-embedding reports no error floor on this sweep, so it is not the cause.";

        return peel + " The other thing this run can attribute an excess to - a conductor pair too " +
               "close together for the cells that straddle it - is ruled out as well, so what " +
               "produced the gain is not identified here. Do not read the flagged points; the rest " +
               "of the sweep is unaffected.";
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
    /// <summary>
    /// <b>The remedy that actually binds when a calibration standard is over the ceiling: the
    /// BOTTOM of the sweep.</b>
    ///
    /// <para>This replaced "coarsen the mesh, or turn de-embedding off and read the raw solve", and
    /// both halves of that were wrong. The raw solve is not a degraded answer — for an edge port the
    /// cut sits one cell inside the metal, so the outer terminal is an isolated sliver and the port
    /// reads as an OPEN; on a 3.8 mm microstrip it published S₂₁ = −107 dB and S₁₁ = +1, and
    /// doubling the line changed S₁₁ in the fourth decimal. And the mesh moves this far less than a
    /// reader would assume: a standard reproduces the DUT's transverse gridlines verbatim (D4), so
    /// coarsening the DUT 4× cut the standard only 5.8× on the geometry that prompted this.</para>
    ///
    /// <para>What sets the count is LENGTH, and length comes from λ at each sub-band's geometric
    /// mean — see <see cref="PlanarCalibration.LongestStandardLengthM"/>. So the number this names
    /// is a lower band edge, estimated by scaling the standard that was actually built.</para>
    ///
    /// <para><b>QSC RE-POINTED IT, AND THE RE-POINTING IS THE WHOLE OF M4.</b> Below
    /// <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> γ is supplied rather than extracted, so
    /// Δℓ is sized from the substrate and the mesh and is frequency-independent — the λ ladder is
    /// drawn from the CROSSOVER upward, not from the user's lower edge. Two consequences, and both
    /// are sentences that used to be printed and were about to become false: the length and the
    /// separation count must be asked of <see cref="PlanarCalibration.MeasuredBandBottomHz"/>, and a
    /// band edge already below the crossover is <b>not a remedy at all</b> — lowering or raising it
    /// down there changes no standard. Telling a user to raise it would be the same defect RAW1 §4
    /// found in this very message, one phase on.</para>
    /// </summary>
    /// <param name="quasiStandard">Whether the standard that is over the ceiling is the SHORT,
    /// frequency-independent one. Its size is set by the port's transverse mesh alone, so no band
    /// edge touches it and <see cref="PlanarCalibration.StartFrequencyThatFits"/>'s length-ratio
    /// scaling does not describe it.</param>
    internal static string BandEdgeRemedy(PlanarStandard std, int nStd, int ceiling,
                                         GroundedSlab slab, double fLo, double fHi,
                                         PlanarCalibrationSettings? calSettings,
                                         bool quasiStandard = false)
    {
        double cross = PlanarCalibration.QuasiStaticCrossoverHz(slab);
        double fEff  = PlanarCalibration.MeasuredBandBottomHz(slab, fLo, fHi);

        if (quasiStandard)
            return
                $"The band edge is NOT the remedy here, and saying so is the point: below " +
                $"{HzOf(cross)} this port is calibrated with a QUASI-STATIC γ, so its two standards " +
                $"are sized from the substrate height and the port's own bulk cell — " +
                $"{SurfaceMesher.Eng(std.LengthM)}m of line — and are the same size at 1 kHz as at " +
                $"{HzOf(cross)}. What sets this count is the port's TRANSVERSE mesh, which a " +
                "standard reproduces verbatim (D4): narrow the port, or coarsen the mesh ACROSS it " +
                "(Min cells across conductor, Edge cells), or cut this port as an internal delta " +
                "gap, which has no feed outside it and needs no standard at all.";

        int nSep = PlanarCalibration.SuggestDeltas(slab, fEff, fHi, calSettings).Length;
        double lMax = PlanarCalibration.LongestStandardLengthM(slab, fEff, fHi, calSettings);

        var fit = PlanarCalibration.StartFrequencyThatFits(
            slab, fEff, fHi, calSettings, ceiling, nStd, std.LengthM);

        // Asked of the EFFECTIVE bottom, because that is the one the ladder was drawn from. Below
        // the crossover there is one short separation and no λ scaling at all, so quoting the
        // user's own f_lo here would describe a regime this run does not have.
        string band =
            $"What sets that count is the standard's LENGTH, and the length is set by the BOTTOM of " +
            $"the sweep, not by the mesh and not by the port's width: each line separation is aimed " +
            $"at {(calSettings ?? PlanarCalibrationSettings.Default).TargetElectricalDegrees:0.#}° of " +
            $"electrical length at its own sub-band's geometric mean, so halving the lower band edge " +
            $"roughly doubles the longest standard. " +
            (fEff > fLo * 1.000001
                ? $"Below {HzOf(cross)} this port is calibrated quasi-statically on short, " +
                  $"frequency-independent standards, so the measured ladder is drawn from " +
                  $"{HzOf(fEff)} rather than from {HzOf(fLo)}: "
                : "") +
            $"{HzOf(fEff)}–{HzOf(fHi)} needs {nSep} " +
            $"separation(s), the longest standard being {SurfaceMesher.Eng(lMax)}m of line. ";

        string remedy = fit is { } f
            ? $"Raising the lower band edge to about " +
              $"{HzOf(PlanarCalibration.RoundUpToTidyFrequency(f))} brings it under the ceiling."
            : "No lower band edge inside this sweep brings it under the ceiling — narrow the sweep " +
              "from BOTH ends, or cut these ports as internal delta gaps, which have no feed outside " +
              "them and need no standard at all." +
              (fEff > fLo * 1.000001
                  ? $" Note that raising it anywhere below {HzOf(cross)} changes nothing: the " +
                    "standards down there do not scale with frequency."
                  : "");

        // Said explicitly, because it is the remedy a reader reaches for first and it is the weak
        // one — and because the previous message recommended it.
        string mesh =
            " Coarsening the mesh moves this much less than it looks: a standard reproduces the " +
            "DUT's transverse gridlines verbatim, so only the transverse half of the count responds " +
            "to it.";

        return band + remedy + mesh;

        static string HzOf(double f) =>
            f >= 1e9 ? $"{f / 1e9:0.###} GHz" :
            f >= 1e6 ? $"{f / 1e6:0.###} MHz" :
            f >= 1e3 ? $"{f / 1e3:0.###} kHz" :
                       $"{f:0.###} Hz";
    }

    /// <summary>
    /// <b>QSC — the run says which calibration produced which points, and why.</b> Null when no
    /// frequency took the quasi-static path, which is every run whose band sits entirely above
    /// <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> and therefore every run that passed
    /// before this existed.
    /// </summary>
    private static string? QuasiStaticCalibrationNote(
        IReadOnlyList<PlanarPortCalibrator> calibrators, IReadOnlyList<double> freqs)
    {
        double cross = double.PositiveInfinity;
        int    quasi = 0, measured = 0;
        bool   withGround = false, withoutGround = false;

        foreach (var cal in calibrators)
        {
            if (double.IsInfinity(cal.QuasiStaticCrossoverHz)) continue;
            cross = Math.Min(cross, cal.QuasiStaticCrossoverHz);
            foreach (double f in freqs)
                if (f > 0) { if (cal.IsQuasiStaticAt(f)) quasi++; else measured++; }
            withGround    |= cal.QuasiStaticGroundTermSupplied;
            withoutGround |= cal.QuasiStaticGroundTermMissing;
        }
        if (quasi == 0) return null;

        // CL7 — the supplied γ's own conductor terms, and the one case where half of it is absent.
        // Reported rather than left to be assumed: below the crossover this γ is what the error box
        // is solved against, so a missing ground term is a published α that is low by the size of it
        // and looks entirely ordinary.
        string groundClause =
            withoutGround
                ? "On a STRATIFIED stack, or a standard on a buried level, that supplied γ carries " +
                  "the drawn metal's conductor term but NOT the ground plane's — the plane's return " +
                  "current has a closed form only for one plane one height below one level, which is " +
                  "what a single-slab design is. Below the crossover such a point's α is low by the " +
                  "plane's share, which on an ordinary microstrip is of order a fifth to a quarter " +
                  "of the conductor loss. The measured points above the crossover carry it. "
            : withGround
                ? "That supplied γ carries BOTH conductor terms — the drawn metal's, from the same " +
                  "charge vector, and the ground plane's, from the return current that charge " +
                  "induces on it — so α does not step at the crossover. "
                : "";

        return
            $"γ for {quasi} of {quasi + measured} calibrated point(s) is QUASI-STATIC, not measured: " +
            $"below {SurfaceMesher.Eng(cross)}Hz it comes from the two standards' own electrostatics " +
            $"(γ = jω√(LC), with L = μ₀ε₀/C₀ from the same geometry with the dielectric removed) " +
            $"rather than from the two-line extraction. The error box is still solved from TWO " +
            $"standards either way — one line cannot give three unknowns — but a supplied γ frees Δℓ " +
            $"from having to be electrically long, so the standards down there are sized from the " +
            $"substrate and the mesh and do not grow as the band edge falls. " +
            (measured > 0
                ? $"The other {measured} point(s) use the measured two-line γ, which is what captures " +
                  "dispersion and is the right instrument above the crossover. "
                : "") + groundClause +
            "Per-point, the `planar.CalQuasiStatic` diagnostic reads 1 where γ was supplied and 0 " +
            "where it was measured. Both paths agree to about 1 % at the crossover; below it the " +
            "MEASURED value is the less accurate of the two (it converges toward this one as the " +
            "mesh is refined), and above it the measured one is right and this one under-predicts β.";
    }

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
    public static (EmSuitability Verdict, List<EmFinding> Notes) VerticalRangeVerdict(
        PlanarProblem problem, PlanarMesh mesh, double fHiHz, PlanarFillSettings? fill = null,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        var notes  = new List<EmFinding>();
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

        // ── MIM-3 / MIM-8 / MIM-9 — the CELL against the LEVEL SEPARATION ───────────────────────
        //
        // The note first, then the verdict, and the ORDER is the point: past the full-wave floor the
        // caller gets both, so whatever surfaces the refusal has the sentence explaining the scale
        // beside it rather than a bare number. VerticalRangeVerdict's own contract already returns
        // the notes alongside a refusal (the vertical-range arm above does the same).
        notes.AddRange(LevelSeparationNotes(problem, mesh, fHiHz, fmt));

        if (LevelSeparationVerdict(problem, mesh, fmt) is { Ok: false } tooThin)
            return (tooThin, notes);

        return (EmSuitability.Yes, notes);
    }

    /// <summary>
    /// <b>MIM-3 / MIM-8 / MIM-9 — the adjacent conductor-level pair whose own cells resolve their
    /// own separation WORST, and the one measurement three different consumers ask for.</b>
    ///
    /// <para>It is asked per ADJACENT LEVEL PAIR and over the cells that actually sit on those two
    /// levels, because that is the only place the cross-level block is evaluated — R-zz-1's own
    /// discipline. Reporting the mesh's largest cell anywhere would grade a plate pair on a cell
    /// belonging to some unrelated wide conductor.</para>
    ///
    /// <para><b>It is one function because it feeds three answers that must not be able to
    /// disagree</b>: <see cref="LevelSeparationVerdict"/>'s refusal, <see cref="LevelSeparationNotes"/>'
    /// note, and the attribution the NOT PASSIVE sentence takes (<see cref="NonPassivityCause"/>).
    /// A run that refuses on one ratio and explains itself with another is worse than either.</para>
    ///
    /// <para>Null when there is no such pair: a single-level problem, or one whose levels have no
    /// meshed cells on them.</para>
    /// </summary>
    public static PlanarLevelPair? WorstLevelPair(PlanarProblem problem, PlanarMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);

        var levels = PlanarLevels.From(problem);
        if (levels.Z.Count < 2) return null;

        PlanarLevelPair? worst = null;
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

            var here = new PlanarLevelPair(lo, sep, cell);
            if (worst is { } w && here.CellOverSeparation <= w.CellOverSeparation) continue;
            worst = here;
        }
        return worst;
    }

    /// <summary>
    /// <b>MIM-9 item 3 / EM-SEV R-emsev-4 — the FULL-WAVE floor, and it is a REFUSAL.</b>
    ///
    /// <para><b>Why it is not the note one line down.</b> The bound up there certifies the
    /// cross-level FILL and the ELECTROSTATIC plate capacitance. Neither of them is a de-embedded
    /// s-parameter, which is what a user reads, and the whole of MIM-9 is that quoting the first at
    /// someone reading the second sent a user the wrong way for days. They are still two
    /// measurements; since MIM-14 they simply stop at the same ratio.</para>
    ///
    /// <para><b>MIM-14 moved this from 40 to 200 and the reason is that the kernel was repaired
    /// under it.</b> MIM-12's rungs — correct to 40, sign-INVERTED by 80, noise at 200 — were taken
    /// on the kernel MIM-12a then fixed, and MIM-12a did not re-run them. Re-run on the peeled
    /// kernel, with the fixture's own irreducible feed inductance separated in closed form, no rung
    /// from cell/separation 0.5 to 200 departs from the electrostatic mutual capacitance of the same
    /// mesh by more than 10 % and every point is passive. See
    /// <see cref="PlanarLevels.FullWaveCellOverSeparation"/> for both ladders.</para>
    ///
    /// <para><b>R-emsev-4 was deferred once, conditionally, and what it now refuses is different in
    /// kind.</b> MIM-8 declined to build it because past its own bound the answer was UNMEASURED
    /// rather than wrong, and refusing on "unmeasured" would be inventing a limit rather than
    /// reporting one. MIM-12 then measured a sign inversion and the refusal was earned on it. That
    /// inversion is gone; what is left past 200 is "unmeasured" again — on BOTH sides now, the
    /// two-port and the fill — and the refusal stays there because
    /// <see cref="PlanarLevels.CanRepresentVias"/>' own argument holds: approximating it would give
    /// a plausible wrong capacitance rather than an obvious failure. <b>Removing it instead of
    /// moving it is how the next regime becomes silent</b> (MIM-9's own instruction).</para>
    ///
    /// <para><b>It names only the remedies that ACT</b> (§3.5's trap, and MIM-9 item 1's whole
    /// subject), and MIM-14 re-measured which ones those are on the repaired kernel rather than
    /// quoting MIM-12's numbers: Cells per wavelength 20 → 40 → 100 → 400 leaves the mesh
    /// BIT-IDENTICAL on a plate this small (94 cells, a 20 µm straddling cell, σ_max 0.99920 at all
    /// four), because that knob sets the pitch ALONG the current while the plate's own width sets it
    /// across. Min cells across conductor DOES act on this quantity, and in the HELPFUL direction —
    /// 1 → 2 → 4 takes the straddling cell 40 → 20 → 10 µm, i.e. cell/separation 200 → 100 → 50 —
    /// which is why the refusal does not list it among the inert knobs the way MIM-9's did. It is
    /// still not offered as a remedy, for a reason about the LAYOUT rather than about the knob: it
    /// is GLOBAL, and on a real layout the whole run's shared tensor grid sets the plate's pitch
    /// rather than the plate does (MIM-10 finding 3), so taking it from 2 to 8 to resolve a 60 µm
    /// plate also puts 1.25 µm cells on a 10 µm interconnect track and the unknown count with it.
    /// A per-LEVEL override is what would make it reachable; that is MIM-14's Case B, scoped there
    /// and deliberately not built because the floor landed at 200 and nothing needs it.</para>
    ///
    /// <para>Separate from <see cref="LevelSeparationNotes"/> so a caller can have the verdict
    /// without the prose and vice versa, which is what <c>EmRunService.Preflight</c> and
    /// <c>circuitrf check</c> need: they report the refusal and the findings together.</para>
    /// </summary>
    public static EmSuitability LevelSeparationVerdict(
        PlanarProblem problem, PlanarMesh mesh,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        if (WorstLevelPair(problem, mesh) is not { } pair) return EmSuitability.Yes;
        if (pair.CellOverSeparation <= PlanarLevels.FullWaveCellOverSeparation)
            return EmSuitability.Yes;

        var fmt = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        return EmSuitability.No(
            $"Conductor levels {pair.Lower} and {pair.Lower + 1} are {fmt(pair.SeparationM)} apart " +
            $"and the largest cell straddling them is {fmt(pair.CellM)}, i.e. cell/separation = " +
            $"{pair.CellOverSeparation:G3}, past the {PlanarLevels.FullWaveCellOverSeparation} this " +
            $"solve is measured over. Inside it the de-embedded two-port of a pair this close reads " +
            $"the series element between the two levels within 10% of the electrostatic mutual " +
            $"capacitance of the same mesh, and passive: MIM-14's ladder, one plate pair with only " +
            $"the film thickness moving, reads 0.99 / 0.97 / 1.02 / 1.03 / 1.04 / 1.08 of it at " +
            $"cell/separation 2 / 20 / 40 / 67 / 80 / 200, and the same fixture with the film pinned " +
            $"at 0.2 µm and only the CELL moving reads 0.93 / 1.00 / 1.02 / 1.02 / 1.03 at " +
            $"cell/separation 200 / 100 / 66.7 / 50 / 33.3. Past 200 NOTHING is measured — not the " +
            $"two-port, and not the cross-level fill either — and approximating it would give a " +
            $"plausible wrong capacitance rather than an obvious failure, which is why it is " +
            $"refused rather than noted. (MIM-12 measured this same ladder at 1.13 / 1.05 / −1.02 / " +
            $"−1.52 and the floor stood at 40 for it; that was the kernel MIM-12a then repaired, " +
            $"and the sign inversion is gone.) What acts on this: thicken the film between these " +
            $"two levels in the technology, or take the upper level out of the EM run and model the " +
            $"part it carries as a circuit element beside the EM result. What does NOT act, " +
            $"measured on this structure rather than assumed: Cells per wavelength — on a plate " +
            $"small enough to sit this close to another level, 20 → 400 leaves the mesh " +
            $"bit-identical, because that knob sets the pitch ALONG the current and the metal's own " +
            $"width sets it across.");
    }

    /// <summary>
    /// <b>MIM-3 / MIM-8 — is any pair of conductor levels closer together than the cells that
    /// straddle them can resolve?</b> A NOTE, never a refusal, for R-prt-13's reason: the answer is
    /// still produced, it is still reciprocal and passive, and what is unreliable is a MAGNITUDE. A
    /// refusal would also take away the many multi-level runs where the ratio is fine.
    ///
    /// <para><b>MIM-8 moved the bound from 5 to 200 and changed what the note MEANS past it.</b>
    /// Before, past the bound was a measured wrongness — 1.46× at 12.5 and the wrong sign at 25.
    /// Now the peak is subtracted and integrated in closed form, the whole measured ladder is inside
    /// 10%, and past 200 is simply unmeasured. The wording follows that: it reports where the ladder
    /// stops rather than predicting what happens beyond it. See
    /// <see cref="PlanarLevels.ValidatedCellOverSeparation"/> for both ladders.</para>
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
    public static List<EmFinding> LevelSeparationNotes(
        PlanarProblem problem, PlanarMesh mesh, double fHiHz,
        SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        var notes = new List<EmFinding>();
        var fmt   = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        if (WorstLevelPair(problem, mesh) is not { } pair) return notes;

        double worstRatio = pair.CellOverSeparation;
        double worstSep = pair.SeparationM, worstCell = pair.CellM;
        int    worstLo  = pair.Lower;

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

        // ── MIM-9 item 2 — THE REASSURING CLAUSE IS THE NARROWEST OF THE THREE ARMS, AND IT WAS
        //    THE WIDEST ────────────────────────────────────────────────────────────────────────
        //
        // This sentence used to fire at every ratio up to 200, and every clause in it was true: the
        // cross-level fill IS measured over that range and so IS the electrostatic plate
        // capacitance. It was still the most misleading line in the run, because MIM-8's validation
        // is ELECTROSTATIC — ScalarPotentialMatrix with a 1 V / 0 V instrument and no port in it —
        // and the quantity a user reads is a de-embedded s-parameter. A run whose published
        // capacitor had the WRONG SIGN was being told its capacitance was within 1%.
        //
        // So "resolved by the mesh" is now said only where the FULL-WAVE answer is measured too,
        // and past that the note states what was validated and what was not, in those words.
        if (worstRatio <= PlanarLevels.FullWaveCellOverSeparation)
        {
            notes.Add(
                $"The closest conductor levels are resolved by the mesh: cell/separation = " +
                $"{worstRatio:G3} at {where}, inside the " +
                $"{PlanarLevels.FullWaveCellOverSeparation} the de-embedded two-port of a close " +
                $"level pair is measured over (MIM-14's ladder: the extracted series element within " +
                $"10% of the electrostatic mutual capacitance of the same mesh, and passive, at " +
                $"every rung from cell/separation 2 to 200 — and that electrostatic value is itself " +
                $"within 1% of ε₀εᵣA/d) and inside the " +
                $"{PlanarLevels.ValidatedCellOverSeparation} MIM-8 measured the cross-level fill " +
                $"over (≤ 1.7e-4 against forced-high quadrature, and since MIM-12a at EVERY " +
                $"frequency in the band — 1.006 at 1, 2, 3 and 10 GHz on two meshes, where MIM-12 " +
                $"measured the same instrument at −0.54 / 1.34 / 1.60 at 1 / 2 / 3 GHz before the " +
                $"film's own image series was taken out of the kernel fit).");
            return notes;
        }

        if (worstRatio <= PlanarLevels.ValidatedCellOverSeparation)
        {
            // ── CURRENTLY UNREACHABLE, AND KEPT DELIBERATELY ────────────────────────────────
            //
            // This arm is the band between the two constants, and since MIM-14 they carry the same
            // number — the de-embedded two-port stopped failing before the fill does, so the band is
            // empty. It is kept rather than deleted because they remain two independent
            // measurements over two different instruments (see PlanarLevels' own two doc comments),
            // either of which can move on its own; deleting the arm would make the next person who
            // moves one of them discover that the scale has a hole in it.
            notes.Add(EmFinding.Warn(
                $"CELL/SEPARATION = {worstRatio:G3} at {where}. The cross-level FILL is measured " +
                $"over this range (≤ 1.7e-4 against forced-high quadrature, out to " +
                $"{PlanarLevels.ValidatedCellOverSeparation}) and so is the ELECTROSTATIC plate " +
                $"capacitance (within 1% of ε₀εᵣA/d, and since MIM-12a at every frequency in the " +
                $"band — 1.006 at 1, 2, 3 and 10 GHz, where MIM-12 measured 1.60 / 1.34 / −0.54 at " +
                $"3 / 2 / 1 GHz). NEITHER OF THOSE IS A DE-EMBEDDED " +
                $"S-PARAMETER, and the full-wave two-port for a pair this close is measured only to " +
                $"cell/separation {PlanarLevels.FullWaveCellOverSeparation}. That is why a " +
                $"full-wave solve on this structure is refused rather than published — see the " +
                $"refusal for the remedies that act."));
            return notes;
        }

        // EM-SEV R-emsev-4 — a WARNING, and the capitals this sentence already carried were the
        // author reaching for a severity the type system did not have. The brief also asked for a
        // REFUSAL past the wrong-sign rung, CONDITIONAL on MIM-8 being declined; MIM-8 landed, the
        // peak is subtracted in closed form, and the FILL's ladder now holds to cell/separation 200
        // with nothing measured beyond it. That is still true and every number below is unchanged.
        //
        // ── MIM-9 — AND THE REFUSAL IS NOW BUILT. MIM-14 — AND IT NOW FIRES WHERE BOTH HALVES
        //    STOP AT ONCE. MIM-12 measured a sign inversion in the DE-EMBEDDED two-port between
        //    cell/separation 40 and 80 and the refusal was earned on it; MIM-14 re-ran that ladder
        //    on the kernel MIM-12a repaired and the inversion is gone, so the two-port now reaches
        //    the same 200 the fill does. Past 200 NEITHER is measured, which is what this sentence
        //    has to say, and LevelSeparationVerdict refuses for the reason CanRepresentVias refuses:
        //    a plausible wrong capacitance rather than an obvious failure.
        notes.Add(EmFinding.Warn(
            $"CELL/SEPARATION = {worstRatio:G3} at {where}, past the " +
            $"{PlanarLevels.FullWaveCellOverSeparation} a de-embedded two-port of a close level " +
            $"pair is measured over — which is why this run is refused — and past the " +
            $"{PlanarLevels.ValidatedCellOverSeparation} the cross-level fill is measured over as " +
            $"well. Both halves stop here and neither is known wrong beyond it; what is past this " +
            $"ratio is UNMEASURED. MIM-8 subtracts the peak a cross-level entry carries — one of " +
            $"width {fmt(worstSep)} inside a cell of {fmt(worstCell)} — and integrates it in closed " +
            $"form, which held the cross-level matrix block at 2.4e-11 / 7.9e-8 / 2.1e-6 / 1.7e-4 " +
            $"against forced-high quadrature for cell/separation of 5 / 20 / 50 / 200, and the " +
            $"ELECTROSTATIC capacitance extracted from a plate pair — no port in it — within 1% of " +
            $"ε₀εᵣA/d over the whole of it: 1.003 at cell/separation 75 and 0.996 at 300, where " +
            $"before it read −0.046 and −0.003. MIM-12 measured that same instrument down the band " +
            $"and it did NOT hold there — 1.60 / 1.34 / −0.54 at 3 / 2 / 1 GHz on the same " +
            $"capacitor, because the kernel FIT carried 2.7e-2 at 1 GHz against 8.3e-5 at 10 and a " +
            $"plate capacitance divides that by d/cell; MIM-12a peels the film's own image series " +
            $"out of that fit and it now reads 1.006 across the band. The FULL-WAVE, de-embedded " +
            $"two-port is the other half, and MIM-14 re-ran ITS ladder on that repaired kernel: " +
            $"within 10% of " +
            $"the electrostatic mutual capacitance of the same mesh, and passive, at every rung out " +
            $"to 200 — where MIM-12 had read the sign INVERTED from 80 up. So the old wrong-sign " +
            $"regime is gone and what this sentence now reports is the end of the evidence rather " +
            $"than a measured failure. What acts on this is " +
            $"the CELL PITCH across the metal on those two levels. " +
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
            $"the part of this answer that rests on the unmeasured end; everything on a single " +
            $"level is unaffected."));
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
