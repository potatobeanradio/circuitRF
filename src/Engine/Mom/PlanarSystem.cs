// L8c — dense storage, the NumFlat factorisation, and the sweep driver that proves D6.
//
// D7: DENSE, NumFlat LU, NO COMPRESSION. §10.7 is explicit — N² × 16 bytes, an LU per frequency, and
// ACA/MLFMM out of scope. ChargeSolver already establishes the idiom (`var lu = a.Lu(); lu.Solve(rhs)`)
// and it is reused rather than a second dense-solve path being invented.
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
}

/// <summary>
/// The dense complex system for one frequency: the matrix, its size, and its LU — with R17's refusal
/// asked before the allocation rather than after it.
/// </summary>
public sealed class PlanarSystem : IPlanarOperator
{
    public Mat<Complex> Matrix { get; }
    public int Size => Matrix.RowCount;

    private LuDecompositionComplex? _lu;

    private PlanarSystem(Mat<Complex> matrix) => Matrix = matrix;

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
    /// <b>Bytes NumFlat's dense LU holds</b>. <see cref="LuDecompositionComplex"/> stores
    /// <c>L</c> and <c>U</c> as two SEPARATE full <c>Mat&lt;Complex&gt;</c> of stride n (verified by
    /// reflection and by measurement, 2026-08-29) — it is not a packed in-place factorisation — plus an
    /// <c>int[n]</c> permutation. The matrix itself is NOT included here: <see cref="Matrix"/> stays
    /// live beside the factors, and <see cref="ResidentBytes"/> is what adds the two.
    ///
    /// <para>The factorisation additionally ALLOCATES about 0.6·16n² of scratch that it releases
    /// again — measured as 3.64·16n² allocated against 2·16n² retained. That transient is visible in a
    /// process working set and not in the live heap, so it is deliberately outside this number, which
    /// counts what is still held when the factorisation returns.</para>
    /// </summary>
    public static long FactorBytes(int n) => 2 * MatrixBytes(n) + 4L * n;

    /// <summary>
    /// <b>What one dense frequency point actually holds at its peak</b> — the number the ceiling
    /// refusals quote, and the reason they no longer quote <see cref="MatrixBytes"/> alone.
    ///
    /// <para>The peak is inside the factorisation, where the matrix, both factors and the cached cores
    /// are all live at once: <c>16n²</c> (matrix) + <c>32n²</c> (L and U) + the cores. The transient
    /// m×m scalar-potential matrix <c>P</c> that the FILL builds
    /// (<c>PlanarFill.ScalarPotentialMatrix</c>) is released before the factorisation starts and the
    /// fill's own peak is the lower of the two, so it does not enter — see <c>HISTORY.md</c>'s P1
    /// table, which measures both.</para>
    ///
    /// <para><b>Measured 2026-08-29 at N = 552 / 1,980 / 4,836</b> and recorded in <c>HISTORY.md</c>
    /// §P1. <c>16·N²</c> — what the three refusals quoted until P1 — understates this by <b>3.52×,
    /// flat across all three</b>, and a machine sees a further ~1.19× of factorisation scratch that
    /// the live heap does not hold. <b>P2 took the third vector core triangle out</b>, so the same
    /// flat ratio is now <b>3.39×</b> and the ceiling refusal quotes 1,290 MB rather than 1,338.</para>
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
           $"point (matrix + factors + cached cores), against " +
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

    /// <summary>Fills the Galerkin matrix at one angular frequency and wraps it.</summary>
    public static PlanarSystem Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                     PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        GuardCeiling(cores.UnknownCount, cores.CellCount);
        return new PlanarSystem(PlanarFill.Fill(cores, termsA, termsQ, omega));
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
        return new PlanarSystem(PlanarFill.FillMultiLevel(cores, set, levels, omega));
    }

    /// <summary>Wraps an already-filled matrix (the reduction tests build theirs directly).</summary>
    public static PlanarSystem Wrap(Mat<Complex> matrix)
    {
        GuardCeiling(matrix.RowCount);
        return new PlanarSystem(matrix);
    }

    /// <summary>The dense LU, computed once and kept. D7's "reuse ChargeSolver's idiom".</summary>
    public LuDecompositionComplex Lu => _lu ??= Matrix.Lu();

    /// <summary>
    /// Back-substitution against an arbitrary right-hand side. <b>This is NOT a port excitation</b> —
    /// D8 keeps those in L8d — it exists so the factorisation can be exercised and timed, and so
    /// Tier 5's static harness can solve the potential-coefficient system.
    /// </summary>
    public Vec<Complex> Solve(Vec<Complex> rhs) => Lu.Solve(rhs);
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
                _ = system.Lu;
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
