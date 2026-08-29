// L8c — dense storage, the factorisation, and the sweep driver that proves D6.
//
// D7: DENSE, NO COMPRESSION. §10.7 is explicit — N² × 16 bytes, a factorisation per frequency, and
// ACA/MLFMM out of scope. L8c reused ChargeSolver's idiom (`var lu = a.Lu(); lu.Solve(rhs)`) rather
// than inventing a second dense-solve path.
//
// P7 (2026-08-29) REPLACED THAT IDIOM HERE, AND ONLY HERE. Z is complex-symmetric bit for bit, and
// NumFlat's general LU neither exploits that nor threads: measured 42.8 s on one core at N = 4,933,
// against a 21.8 s fill that parallelises 5.4×, while holding L and U as two further full N×N
// matrices beside the one this class keeps. SymmetricFactorization does the same job in place, in
// half the arithmetic, over every core the run's ONE parallel cap allows. The general LU is still
// reachable — `PlanarFillSettings.UseSymmetricFactorization = false` — and is the oracle every P7
// accuracy gate compares against, exactly as `UseRadialTable = false` is kept for the remainder.
// ChargeSolver and PlanarDeembed's own dense solves are NOT touched by this: they are over CELLS,
// they are a different matrix, and P7's brief scopes itself to this one.
//
// R-fil-10: R17'S CEILING IS ENFORCED HERE AS WELL AS IN THE MESH REPORT, and it refuses BEFORE any
// Mat<Complex> of that size is constructed. A "lightweight" simulator that OOMs instead of refusing
// is not lightweight, and a refusal that arrives as an OutOfMemoryException from inside a library is
// not a refusal.
//
// D8: NO PORTS, NO EXCITATION, NO S-PARAMETERS. Nothing here builds a right-hand side. The sweep
// below fills and factors and reports what that cost; it does not solve anything, because there is
// nothing yet to solve for. That is L8d. The matrix is still fully gateable — see the four rungs of
// the oracle ladder in the tests.

using System.Diagnostics;
using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>M5 — what an excitation actually needs from a "system": a size and a solve.</b>
///
/// <para>The dense path satisfies it with an LU; the AIM accelerator satisfies it with GMRES against a
/// product it never forms as a matrix. Introducing the seam here rather than branching inside
/// <see cref="PlanarExcitation"/> is what keeps <c>Y = BᵀZ⁻¹B</c> one piece of code — the port
/// algebra is identical either way, and a second copy of it is exactly where a sign convention drifts.</para>
/// </summary>
public interface IPlanarOperator
{
    /// <summary>N — the unknown count.</summary>
    int Size { get; }

    /// <summary>One right-hand side, solved.</summary>
    Vec<Complex> Solve(Vec<Complex> rhs);

    /// <summary>
    /// <b>P right-hand sides against ONE factorisation</b> — the shape <c>Y = BᵀZ⁻¹B</c> actually
    /// has. The default is the loop every operator did before P7; an operator whose substitution can
    /// amortise the factor's memory traffic across the P vectors (<see cref="PlanarSystem"/>'s can)
    /// overrides it. Overriding changes no answer: the arithmetic per vector is identical.
    /// </summary>
    Vec<Complex>[] Solve(IReadOnlyList<Vec<Complex>> rhs)
    {
        var xs = new Vec<Complex>[rhs.Count];
        for (int i = 0; i < rhs.Count; i++) xs[i] = Solve(rhs[i]);
        return xs;
    }
}

/// <summary>
/// The dense complex system for one frequency: the matrix, its size, and its factorisation — with
/// R17's refusal asked before the allocation rather than after it.
///
/// <para><b>P7 — <see cref="Matrix"/> IS THE FACTORISATION'S WORKSPACE, AND IT IS CONSUMED.</b> The
/// shipped path (<see cref="SymmetricFactorization"/>) overwrites Z's lower triangle in place, which
/// is where two thirds of a frequency point's memory went. Reading <see cref="Matrix"/> after
/// <see cref="Factor"/> or <see cref="Solve"/> therefore throws rather than silently handing back a
/// half-eliminated matrix: a caller that needs Z afterwards takes its own copy first, and a caller
/// that wants a residual on every solve asks for it with
/// <c>PlanarFillSettings.TrackFactorizationResidual</c> and pays for the copy knowingly.</para>
/// </summary>
public sealed class PlanarSystem : IPlanarOperator
{
    private readonly Mat<Complex> _a;
    private readonly PlanarFillSettings _st;

    /// <summary>P7 — the copy kept ONLY when the caller asked for residuals. Null otherwise, which
    /// is the default and is the whole memory saving.</summary>
    private readonly Mat<Complex>? _residualReference;

    private SymmetricFactorization? _ldl;
    private LuDecompositionComplex? _lu;
    private bool _consumed;

    /// <summary>
    /// The filled matrix — <b>readable only until it is factored</b>, see the class note. The guard
    /// is an exception rather than a comment because the failure it prevents is silent: the upper
    /// triangle survives the factorisation untouched, so a stale read of a symmetric matrix's upper
    /// half returns plausible numbers that are simply the wrong frequency's.
    /// </summary>
    public Mat<Complex> Matrix => _consumed
        ? throw new InvalidOperationException(
            "This system has been factored, and P7's factorisation runs IN PLACE — the matrix's " +
            "lower triangle now holds L, not Z. Copy the matrix before factoring if you need it " +
            "afterwards, or set PlanarFillSettings.TrackFactorizationResidual to have the system " +
            "keep its own copy (which costs a whole N×N matrix, and is a diagnostic).")
        : _a;

    public int Size => _a.RowCount;

    /// <summary>P7's stability instruments for this point, or null while the shipped path has not
    /// been used (nothing factored yet, or the NumFlat setting selected).</summary>
    public SymmetricFactorization? Factorization => _ldl;

    /// <summary>
    /// <c>‖Zx − b‖/‖b‖</c> of the most recent <see cref="Solve"/>, or null when
    /// <c>PlanarFillSettings.TrackFactorizationResidual</c> is off — which it is by default, because
    /// the reference matrix it needs is the memory P7 exists to recover.
    /// </summary>
    public double? LastResidual { get; private set; }

    private PlanarSystem(Mat<Complex> matrix, PlanarFillSettings settings)
    {
        _a  = matrix;
        _st = settings;
        if (settings.TrackFactorizationResidual) _residualReference = matrix.Copy();
    }

    /// <summary>Bytes of dense complex matrix — §10.7's own budget line, and ONLY the matrix.</summary>
    public static long MatrixBytes(int n) => 16L * n * n;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P1 — honest memory accounting. ONE function, three refusals.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The measured basis-count-to-cell-count ratio of a rooftop mesh</b>, used only when a caller
    /// has no cell count to hand (<see cref="Wrap"/> holds a bare matrix). A cell carries one x̂ and
    /// one ŷ rooftop except at the metal's rim, so the ratio approaches 2 from below as the rim's
    /// share falls: measured 1.68 at N = 94, 1.86 at N = 552, 1.88 at N = 1,980, 1.885 at N = 4,836
    /// on the shipping mesh, and 1.96 on L8c's own uniform test meshes. <b>1.95 is deliberately at
    /// the top of that range</b> — a larger ratio means a smaller derived cell count and therefore a
    /// smaller cores term, which keeps <see cref="CoreBytes"/> a floor on this path exactly as its
    /// other two assumptions do.
    /// </summary>
    private const double BasesPerCell = 1.95;

    /// <summary>
    /// Bytes the cached frequency-independent geometric cores held at this size in P4's triangle
    /// layout, reconstructed from N and the cell count so a refusal can quote a figure BEFORE a mesh
    /// has been cored.
    ///
    /// <para><b>P5 (2026-08-29): this is an a-priori FIGURE, no longer an exact reconstruction.</b>
    /// The production layout (<c>PlanarCoreLayout.Classes</c>) holds a 4-byte translation-class
    /// index per ordered band pair (≈ 0.6 m² of them) plus 112 bytes per class, and the class count
    /// is a property of the artwork: it meets this figure at a reuse of ≈ 3× and falls to a quarter
    /// of it on a long line (measured: 25 MB against 83 at N = 3,731). A mesh with NO translation
    /// reuse — every spacing distinct — would exceed it, by up to ~2.9×; none of the seven fixtures
    /// the brief measured comes near that, and the number is kept here as the conservative quote.
    /// <c>PlanarFillCores.CoreBytes</c> reports what was actually allocated.</para>
    ///
    /// <para>The arrays are <c>PlanarFill.BuildCores</c>'s own: two packed upper-triangles over cell
    /// pairs (<c>S0</c>, <c>SLog</c>) and — since P2 — <b>two</b> over same-direction basis pairs per
    /// direction (<c>V*0</c>, <c>V*Log</c>), plus one length-N vector of per-basis moments
    /// (<c>VMoment</c>), all <c>double</c>. <b>P2 dropped the third packed vector triangle</b>: the
    /// extracted constant's vector core is ∫w_m·∫w_n, an outer product, and it is now formed from the
    /// O(N) vector at the point of use. <b>Two stated assumptions</b>, both of which make this a floor
    /// rather than a ceiling: the extraction order is <c>PlanarExtractionOrder.Constant</c> (the
    /// shipped default — <c>Linear</c> adds a radial array in each family, +50% to both the scalar and
    /// the vector term), and the x̂/ŷ split is even, which MINIMISES the vector term (a wholly
    /// one-directional mesh doubles it).</para>
    /// </summary>
    public static long CoreBytes(int n, int cellCount = 0)
    {
        long m  = cellCount > 0 ? cellCount : (long)Math.Round(n / BasesPerCell);
        long nx = n / 2, ny = n - nx;
        long scalarPairs = m * (m + 1) / 2;
        long vectorPairs = nx * (nx + 1) / 2 + ny * (ny + 1) / 2;
        return 8L * (2 * scalarPairs + 2 * vectorPairs + n);
    }

    /// <summary>
    /// <b>Bytes NumFlat's dense LU holds</b> — the pre-P7 path, still reachable through
    /// <c>PlanarFillSettings.UseSymmetricFactorization = false</c>. <see cref="LuDecompositionComplex"/>
    /// stores <c>L</c> and <c>U</c> as two SEPARATE full <c>Mat&lt;Complex&gt;</c> of stride n
    /// (verified by reflection and by measurement, 2026-08-29) — it is not a packed in-place
    /// factorisation — plus an <c>int[n]</c> permutation. The matrix itself is NOT included here:
    /// on that path <see cref="Matrix"/> stays live beside the factors.
    ///
    /// <para>The factorisation additionally ALLOCATES about 0.6·16n² of scratch that it releases
    /// again — measured as 3.64·16n² allocated against 2·16n² retained. That transient is visible in a
    /// process working set and not in the live heap, so it is deliberately outside this number, which
    /// counts what is still held when the factorisation returns.</para>
    /// </summary>
    public static long LuFactorBytes(int n) => 2 * MatrixBytes(n) + 4L * n;

    /// <summary>
    /// <b>P7 — bytes the SHIPPED factorisation holds beyond the matrix it consumed.</b>
    /// <see cref="SymmetricFactorization"/> writes L into the lower triangle of Z itself, so the only
    /// new array is the diagonal D: one length-n complex vector, <c>16n</c> bytes. At the ceiling
    /// that is 78 kB against the 763 MB of L and U the general LU held.
    /// </summary>
    public static long SymmetricFactorBytes(int n) => 16L * n;

    /// <summary>
    /// What the factorisation of the DEFAULT path retains. P7 made that
    /// <see cref="SymmetricFactorBytes"/>; <see cref="LuFactorBytes"/> is what the same line meant
    /// up to P6 and is what the NumFlat setting still costs.
    /// </summary>
    public static long FactorBytes(int n) => SymmetricFactorBytes(n);

    /// <summary>
    /// <b>What one dense frequency point actually holds at its peak</b> — the number the ceiling
    /// refusals quote, and the reason they no longer quote <see cref="MatrixBytes"/> alone.
    ///
    /// <para><b>P7 (2026-08-29) — the factors no longer sit beside the matrix; they ARE the matrix.</b>
    /// <see cref="SymmetricFactorization"/> overwrites Z's lower triangle in place, so the peak is
    /// <c>16n²</c> (the matrix, which the factorisation consumes) + <c>16n</c> (the diagonal D) + the
    /// cores. Up to P6 the middle term was <c>32n²</c> of separate L and U. The transient m×m
    /// scalar-potential matrix <c>P</c> that the FILL builds
    /// (<c>PlanarFill.ScalarPotentialMatrix</c>) is released before the factorisation starts and the
    /// fill's own peak is the lower of the two, so it does not enter — see <c>HISTORY.md</c>'s P1
    /// table, which measures both.</para>
    ///
    /// <para><b>Measured at P1 (2026-08-29) at N = 552 / 1,980 / 4,836</b> and recorded in
    /// <c>HISTORY.md</c> §P1: with the general LU, <c>16·N²</c> understated the peak by <b>3.52×</b>,
    /// flat across all three, which P2 brought to <b>3.39×</b>. <b>P7 takes both factor matrices out
    /// entirely</b>, so the same ratio is now <b>1.39×</b> and the ceiling refusal quotes 527 MB
    /// rather than 1,290. Setting <c>PlanarFillSettings.UseSymmetricFactorization = false</c> puts
    /// <see cref="LuFactorBytes"/> back and with it the old number; this function describes what
    /// ships.</para>
    /// </summary>
    /// <param name="cellCount">The mesh's cell count, or 0 when the caller has none, in which case it
    /// is derived from <paramref name="n"/> — see <see cref="CoreBytes"/>.</param>
    public static long ResidentBytes(int n, int cellCount = 0)
        => MatrixBytes(n) + FactorBytes(n) + CoreBytes(n, cellCount);

    /// <summary>
    /// <b>The one parenthetical all three ceiling refusals quote, so the three cannot drift.</b> It
    /// says what the number IS, because a bare megabyte figure next to a ceiling reads as a machine
    /// limit — which R17 is not (see <see cref="SurfaceMesher"/>'s own note on that).
    /// </summary>
    public static string ResidentPhrase(int n, int cellCount = 0)
        => $"{Megabytes(ResidentBytes(n, cellCount)):N0} MB resident at the peak of one frequency " +
           $"point (the matrix P7's in-place factorisation consumes, its diagonal, and the cached " +
           $"cores), against " +
           $"{Megabytes(ResidentBytes(SurfaceMesher.UnknownCeiling)):N0} MB at the ceiling";

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>
    /// R-fil-10 / R17, in <see cref="SurfaceMesher"/>'s own words and with the same numbers, asked
    /// before anything of that size is allocated.
    /// </summary>
    public static void GuardCeiling(int n, int cellCount = 0)
    {
        if (n <= SurfaceMesher.UnknownCeiling) return;
        throw new InvalidOperationException(
            $"This geometry needs {n:N0} unknowns, which is past the " +
            $"{SurfaceMesher.UnknownCeiling:N0}-unknown ceiling this kernel is built for " +
            $"({ResidentPhrase(n, cellCount)}). " +
            "Lower Cells per wavelength, turn the edge mesh off, or analyse a smaller region — " +
            "full-wave analysis of a structure this size needs matrix compression, which is not built.");
    }

    /// <summary>Fills the Galerkin matrix at one angular frequency and wraps it. The cores' own
    /// settings choose the factorisation and the parallel cap it will run under.</summary>
    public static PlanarSystem Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                     PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        GuardCeiling(cores.UnknownCount, cores.CellCount);
        return new PlanarSystem(PlanarFill.Fill(cores, termsA, termsQ, omega), cores.Settings);
    }

    /// <summary>
    /// <b>L9d/M1 — the same thing for a MULTI-LEVEL mesh.</b> A separate entry point rather than a
    /// widened <see cref="Build(PlanarFillCores, PlanarKernelTerms, PlanarKernelTerms, double)"/>,
    /// because the two take genuinely different objects: L8's pair is one fit per component for the
    /// whole problem, L9's set is one per component per height PAIRING, and collapsing them would
    /// have to invent a pairing for the one-level case that L8d's own fit does not have.
    /// </summary>
    public static PlanarSystem BuildMultiLevel(PlanarFillCores cores, PlanarKernelSet set,
                                               PlanarLevels levels, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(levels);
        GuardCeiling(cores.UnknownCount, cores.CellCount);
        return new PlanarSystem(PlanarFill.FillMultiLevel(cores, set, levels, omega), cores.Settings);
    }

    /// <summary>
    /// Wraps an already-filled matrix (the reduction tests build theirs directly).
    ///
    /// <para><b>P7 — the matrix handed in is CONSUMED by the first factorisation.</b> A caller that
    /// still wants it afterwards passes <c>matrix.Copy()</c>. This is deliberately not copied here:
    /// the copy is the whole cost this phase removed, and hiding it inside the wrapper would hide it
    /// from the one caller in a position to decide whether it is needed.</para>
    /// </summary>
    public static PlanarSystem Wrap(Mat<Complex> matrix, PlanarFillSettings? settings = null)
    {
        GuardCeiling(matrix.RowCount);
        return new PlanarSystem(matrix, settings ?? PlanarFillSettings.Default);
    }

    /// <summary>
    /// <b>Factor, once.</b> Idempotent, and the only place either factorisation is entered — which
    /// is what makes "the matrix is consumed" a single fact rather than one per call site.
    /// </summary>
    public void Factor()
    {
        if (_consumed) return;

        if (_st.UseSymmetricFactorization)
        {
            _ldl = SymmetricFactorization.Factor(_a, _st);
        }
        else
        {
            // The pre-P7 path. NumFlat's LU does NOT consume its input, so `Matrix` would still be
            // readable here — but it is sealed off either way, because a property whose validity
            // depends on a setting is worse than one that is simply gone.
            _lu = _a.Lu();
        }

        _consumed = true;
    }

    /// <summary>The general LU — <b>only</b> on the path
    /// <c>PlanarFillSettings.UseSymmetricFactorization = false</c> selects.</summary>
    public LuDecompositionComplex Lu
    {
        get
        {
            Factor();
            return _lu ?? throw new InvalidOperationException(
                "This system was factored with P7's complex-symmetric LDLᵀ, which is not an LU and " +
                "has no L, U or permutation to hand back. Set " +
                "PlanarFillSettings.UseSymmetricFactorization = false to take NumFlat's general LU, " +
                "which is kept reachable exactly for this kind of comparison.");
        }
    }

    /// <summary>
    /// Back-substitution against an arbitrary right-hand side. <b>This is NOT a port excitation</b> —
    /// D8 keeps those in L8d — it exists so the factorisation can be exercised and timed, and so
    /// Tier 5's static harness can solve the potential-coefficient system.
    /// </summary>
    public Vec<Complex> Solve(Vec<Complex> rhs)
    {
        Factor();
        var x = _ldl is { } ldl ? ldl.Solve(rhs) : _lu!.Solve(rhs);
        if (_residualReference is { } z) LastResidual = SymmetricFactorization.Residual(z, x, rhs);
        return x;
    }

    /// <summary>
    /// <b>P back-substitutions against one factorisation</b>, which is the shape <c>Y = BᵀZ⁻¹B</c>
    /// asks for. On the shipped path this reads each column of L once for all P vectors instead of
    /// once per vector; on the NumFlat path it is the loop it always was.
    /// </summary>
    public Vec<Complex>[] Solve(IReadOnlyList<Vec<Complex>> rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        Factor();

        Vec<Complex>[] xs;
        if (_ldl is { } ldl)
        {
            xs = ldl.Solve(rhs);
        }
        else
        {
            xs = new Vec<Complex>[rhs.Count];
            for (int r = 0; r < rhs.Count; r++) xs[r] = _lu!.Solve(rhs[r]);
        }

        if (_residualReference is { } z)
        {
            double worst = 0;
            for (int r = 0; r < xs.Length; r++)
                worst = Math.Max(worst, SymmetricFactorization.Residual(z, xs[r], rhs[r]));
            LastResidual = worst;
        }
        return xs;
    }
}

/// <summary>What one frequency of a sweep cost, split the way Tier 8 has to report it.</summary>
public sealed record PlanarSweepPoint(
    double FrequencyHz,
    double KernelFitMs,
    double FillMs,
    double FactorMs);

/// <summary>
/// The result of a fill-and-factor sweep. <b><see cref="CoreFillCount"/> is R-fil-9's counter</b>, and
/// it exists for the reason R-mom-11 gives for <c>RlgcModel.MatrixFillCount</c>: "it is easy to lose
/// in a refactor — so it is enforced by a counter, <i>not by a comment</i>."
///
/// <para>It is an INSTANCE property rather than a static one on purpose. A static counter is the
/// obvious implementation and it makes the test that reads it flaky the moment two fill tests run
/// concurrently, which xUnit does by default across classes.</para>
/// </summary>
public sealed class PlanarSweepResult
{
    public required IReadOnlyList<PlanarSweepPoint> Points { get; init; }

    /// <summary>R-fil-9 — how many times the frequency-independent geometric core was built. Must be
    /// exactly 1 for a sweep of any length.</summary>
    public required int CoreFillCount { get; init; }

    public required double CoreBuildMs   { get; init; }
    public required long   CoreBytes     { get; init; }
    public required int    UnknownCount  { get; init; }
    public required int    CellCount     { get; init; }
    public required long   ScalarPairs   { get; init; }
    public required long   VectorPairs   { get; init; }

    /// <summary>
    /// R-fil-8 — the smallest fitted image depth over the whole sweep, divided by the smallest cell
    /// edge. D3's "the images are smooth" claim is conditional on this NOT being small; the sweep
    /// measures it rather than assuming it.
    /// </summary>
    public required double SmallestImageDepthOverCell { get; init; }

    /// <summary>The last frequency's system, when the caller asked to keep it. Not kept by default:
    /// at the R17 ceiling one matrix is 400 MB and a 101-point sweep of them is not a thing.</summary>
    public PlanarSystem? Last { get; init; }

    public long MatrixBytes => PlanarSystem.MatrixBytes(UnknownCount);

    /// <summary>P1 — what one point of this sweep holds at its peak, matrix and factors and cores
    /// together. <see cref="MatrixBytes"/> is about a third of it.</summary>
    public long ResidentBytes => PlanarSystem.ResidentBytes(UnknownCount, CellCount);
    public double TotalFillMs   { get { double s = 0; foreach (var p in Points) s += p.FillMs;      return s; } }
    public double TotalFactorMs { get { double s = 0; foreach (var p in Points) s += p.FactorMs;    return s; } }
    public double TotalKernelMs { get { double s = 0; foreach (var p in Points) s += p.KernelFitMs; return s; } }
    public double TotalMs       => CoreBuildMs + TotalFillMs + TotalFactorMs + TotalKernelMs;
}

/// <summary>
/// Fills — and optionally factors — the planar system across a frequency sweep, reusing D6's
/// geometric core. This is the harness Tier 7's counter test and Tier 8's cost measurement run
/// against; it produces no result cube and no s-parameters, because there is no excitation (D8).
/// </summary>
public static class PlanarSweep
{
    public static PlanarSweepResult Run(PlanarMesh mesh, GroundedSlab slab,
                                        IReadOnlyList<double> freqsHz,
                                        PlanarFillSettings? settings = null,
                                        bool factor = true,
                                        bool keepLast = false,
                                        DcimSettings? dcim = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(freqsHz);

        var st = settings ?? PlanarFillSettings.Default;
        PlanarSystem.GuardCeiling(mesh.Bases.Count, mesh.Cells.Count);

        // ── D6: ONCE, whatever the sweep's length ────────────────────────────────────────────
        var sw = Stopwatch.StartNew();
        var cores = PlanarFill.BuildCores(mesh, st);
        double coreMs = sw.Elapsed.TotalMilliseconds;
        int coreFills = 1;

        var points = new List<PlanarSweepPoint>(freqsHz.Count);
        PlanarSystem? last = null;
        double worstDepthRatio = double.PositiveInfinity;

        foreach (double f in freqsHz)
        {
            sw.Restart();
            var greens = new SpectralGreens(slab, f);
            var termsA = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.VectorPotential, dcim),
                                                    st.Order, cores.RhoFloorM);
            var termsQ = PlanarKernelTerms.FromDcim(Dcim.Fit(greens, GreensKernel.ScalarPotential, dcim),
                                                    st.Order, cores.RhoFloorM);
            double kernelMs = sw.Elapsed.TotalMilliseconds;

            if (cores.MinCellEdgeM > 0)
                worstDepthRatio = Math.Min(worstDepthRatio,
                    Math.Min(termsA.SmallestImageDepth, termsQ.SmallestImageDepth) / cores.MinCellEdgeM);

            sw.Restart();
            var system = PlanarSystem.Build(cores, termsA, termsQ, 2.0 * Math.PI * f);
            double fillMs = sw.Elapsed.TotalMilliseconds;

            double factorMs = 0;
            if (factor)
            {
                sw.Restart();
                system.Factor();
                factorMs = sw.Elapsed.TotalMilliseconds;
            }

            points.Add(new PlanarSweepPoint(f, kernelMs, fillMs, factorMs));
            last = keepLast ? system : null;
        }

        return new PlanarSweepResult
        {
            Points                     = points,
            CoreFillCount              = coreFills,
            CoreBuildMs                = coreMs,
            CoreBytes                  = cores.CoreBytes,
            UnknownCount               = cores.UnknownCount,
            CellCount                  = cores.CellCount,
            ScalarPairs                = cores.ScalarPairs,
            VectorPairs                = cores.VectorPairs,
            SmallestImageDepthOverCell = worstDepthRatio,
            Last                       = last,
        };
    }
}
