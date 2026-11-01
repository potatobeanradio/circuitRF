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
// CAP-2 (2026-10-31): THE MATRIX IS ITS LOWER TRIANGLE. Z is held as a BlockLowerMatrix — the
// block-column panels P7's factorisation walks — at ~8·N² bytes where the square held 16·N². The fills
// write into it directly and no longer mirror; `Matrix` is the expensive expansion for a small-N
// reader, and the NumFlat LU reference path expands to a square for itself.
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
    /// <summary>CAP-2 — Z's lower triangle, the factorisation's workspace. Null once the NumFlat
    /// reference path has expanded it into the square its LU needs.</summary>
    private BlockLowerMatrix? _a;
    private readonly int _n;
    private readonly PlanarFillSettings _st;

    /// <summary>P7 — the copy kept ONLY when the caller asked for residuals. Null otherwise, which
    /// is the default and is the whole memory saving.</summary>
    private readonly BlockLowerMatrix? _residualReference;

    /// <summary>PM-2 — the merge this system was filled in, or null for the elemental system.
    /// Set, the matrix is <c>Tᵀ Z T</c> and <see cref="Solve(Vec{Complex})"/> takes and returns
    /// ELEMENTAL vectors, so nothing that excites or reads back a solve knows it happened.</summary>
    private readonly PlanarRooftopMerge? _merge;

    private SymmetricFactorization? _ldl;
    private LuDecompositionComplex? _lu;
    private bool _consumed;

    /// <summary>
    /// The filled matrix as a full square — <b>CAP-2: an EXPANSION of <see cref="Lower"/>, 16·N²
    /// bytes, for small-N readers only</b>, and <b>readable only until it is factored</b>, see the
    /// class note. The guard is an exception rather than a comment because the failure it prevents is
    /// silent: a factored triangle returns plausible numbers that are simply not Z.
    /// </summary>
    public Mat<Complex> Matrix => Lower.ToDense();

    /// <summary>
    /// <b>CAP-2 — the filled matrix as it is held: its lower triangle</b>, block-column panels.
    /// Readable only until it is factored, on <see cref="Matrix"/>'s terms; its element accessor
    /// answers either triangle by symmetry.
    /// </summary>
    public BlockLowerMatrix Lower => _consumed || _a is null
        ? throw new InvalidOperationException(
            "This system has been factored, and P7's factorisation runs IN PLACE — the matrix's " +
            "lower triangle now holds L, not Z. Copy the matrix before factoring if you need it " +
            "afterwards, or set PlanarFillSettings.TrackFactorizationResidual to have the system " +
            "keep its own copy (which costs a whole second triangle, and is a diagnostic).")
        : _a;

    /// <summary>The length of the vectors <see cref="Solve(Vec{Complex})"/> takes — the ELEMENTAL
    /// rooftop count, also when the matrix is a merged one (<see cref="MatrixSize"/>).</summary>
    public int Size => _merge?.ElementalCount ?? _n;

    /// <summary>The dimension of <see cref="Matrix"/> — the merged count under PM-2's rooftop
    /// merging, <see cref="Size"/> otherwise.</summary>
    public int MatrixSize => _n;

    /// <summary>PM-2 — the merge this system is solved in, or null.</summary>
    public PlanarRooftopMerge? Merge => _merge;

    /// <summary>P7's stability instruments for this point, or null while the shipped path has not
    /// been used (nothing factored yet, or the NumFlat setting selected).</summary>
    public SymmetricFactorization? Factorization => _ldl;

    /// <summary>
    /// <c>‖Zx − b‖/‖b‖</c> of the most recent <see cref="Solve"/>, or null when
    /// <c>PlanarFillSettings.TrackFactorizationResidual</c> is off — which it is by default, because
    /// the reference matrix it needs is the memory P7 exists to recover.
    /// </summary>
    public double? LastResidual { get; private set; }

    private PlanarSystem(BlockLowerMatrix matrix, PlanarFillSettings settings, PlanarRooftopMerge? merge = null)
    {
        _a  = matrix;
        _n  = matrix.Size;
        _st = settings;
        _merge = merge;
        if (settings.TrackFactorizationResidual) _residualReference = matrix.Copy();
    }

    /// <summary>
    /// <b>Bytes of the matrix as it is held — §10.7's own budget line, and ONLY the matrix.</b>
    /// CAP-2: the lower triangle in block-column panels, <see cref="BlockLowerMatrix.Bytes"/> —
    /// 16·(N²/2 + N·nb/2) when nb = 64 divides N. It was the full square, 16·N², until then;
    /// <see cref="DenseMatrixBytes"/> is that figure, and is what the general-LU path still expands to.
    /// </summary>
    public static long MatrixBytes(int n) => BlockLowerMatrix.Bytes(n);

    /// <summary>Bytes of a full N×N complex square — the pre-CAP-2 matrix, and the size of each of
    /// NumFlat's L and U.</summary>
    public static long DenseMatrixBytes(int n) => 16L * n * n;

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
    internal const double BasesPerCell = 1.95;

    /// <summary>
    /// <b>CAP-3 — bytes the cached frequency-independent cores hold, a priori</b>: what
    /// <see cref="PlanarCoreFootprint.Estimate"/> states for N unknowns on <paramref name="cellCount"/>
    /// cells (derived from N when 0) — the class layout's 4-byte index over the band at 0.61·m², its
    /// class table at a stated reuse, and O(N) vectors. A mesh in hand is quoted from its own count
    /// instead (<see cref="PlanarCoreFootprint.Count"/>, which equals <c>PlanarFillCores.CoreBytes</c>).
    ///
    /// <para>Until CAP-3 this was P4's triangle layout, reconstructed (≈ 6·N²): 3–12× what the class
    /// layout allocates on real artwork, and every ceiling was sized on it. <c>RESOLVED.md</c> §CAP-3.</para>
    /// </summary>
    public static long CoreBytes(int n, int cellCount = 0) => PlanarCoreFootprint.Estimate(n, cellCount).Bytes;

    /// <summary>
    /// <b>CAP-3 — what a dense FILL holds beside the triangle and the cores while it runs</b>: the rows of
    /// the cell potential P it forms panel by panel (capped at <see cref="PlanarFill.PotentialBudgetBytes"/>,
    /// or the whole of P where that is smaller), the per-frequency class remainder tables
    /// (<see cref="PlanarCoreFootprint.RemainderBytes"/>), and the per-basis halves and row plan. Released
    /// before the factorisation starts.
    /// </summary>
    public static long FillTransientBytes(int n, PlanarCoreFootprint cores)
    {
        long m = cores.Cells;
        return Math.Min(PlanarFill.PotentialBudgetBytes(n), 16L * m * m)
             + cores.RemainderBytes + 64L * n + 32L * m;
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
    public static long LuFactorBytes(int n) => 2 * DenseMatrixBytes(n) + 4L * n;

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
    /// <b>What one dense frequency point actually holds at its peak</b> — the number the ceiling, every
    /// refusal and warning, <c>PlanarMemoryCheck</c>, <see cref="PlanarRunMemory"/> and the EM panel all
    /// quote: the stored triangle (<see cref="MatrixBytes"/>), the cached cores, and the larger of what the
    /// fill holds beside them (<see cref="FillTransientBytes"/>) and what the factorisation holds beside
    /// them (<see cref="FactorBytes"/>, the diagonal D).
    ///
    /// <para><b>CAP-3 (2026-10-31).</b> The cores are the class layout's own bytes — counted for a mesh,
    /// stated for a bare unknown count — and the fill's transient is charged. Until CAP-3 the cores were
    /// quoted at P4's triangle layout (≈ 6·N²) and the fill's m×m P (16·m², ≈ 4.2·N²) was left out on the
    /// grounds that it was gone before the factorisation; once CAP-2 halved the matrix that P was what
    /// set the live peak, and only the over-quoted cores were covering it. The fill now forms P in
    /// bounded panels, so its share is the panel budget.</para>
    ///
    /// <para><b>P7 / CAP-2.</b> <see cref="SymmetricFactorization"/> overwrites the triangle in place, so
    /// the factors ARE the matrix: <c>8·N²</c> of triangle + <c>16·N</c> of diagonal. Up to P6 the factors
    /// were <c>32·N²</c> of separate L and U beside a <c>16·N²</c> square; see <c>HISTORY.md</c> §P1 and
    /// <c>RESOLVED.md</c> §CAP-2.</para>
    /// </summary>
    /// <param name="cellCount">The mesh's cell count, or 0 when the caller has none, in which case it
    /// is derived from <paramref name="n"/> — see <see cref="CoreBytes"/>.</param>
    /// <param name="symmetric">False for the general-LU reference path
    /// (<c>PlanarFillSettings.UseSymmetricFactorization = false</c>), which expands the triangle into
    /// a square for itself (dropping the triangle) and holds L and U beside it — the pre-CAP-2 figure.
    /// It is not a capacity path, and no ceiling is sized on it.</param>
    public static long ResidentBytes(int n, int cellCount = 0, bool symmetric = true)
        => ResidentBytes(n, PlanarCoreFootprint.Estimate(n, cellCount), symmetric);

    /// <summary>
    /// <b>CAP-3 — the one function</b>: <see cref="ResidentBytes(int, int, bool)"/> for a stated or
    /// counted core footprint. <paramref name="n"/> is the size of the matrix held (the merged count on a
    /// merged mesh); <paramref name="cores"/> is the elemental mesh's, which is what the fill cores.
    /// </summary>
    public static long ResidentBytes(int n, PlanarCoreFootprint cores, bool symmetric = true)
        => symmetric
            ? MatrixBytes(n) + cores.Bytes + Math.Max(FactorBytes(n), FillTransientBytes(n, cores))
            : DenseMatrixBytes(n) + LuFactorBytes(n) + cores.Bytes + FillTransientBytes(n, cores);

    /// <summary>
    /// <b>CAP-3 — one dense frequency point of THIS mesh</b>: its solved unknown count and its own counted
    /// cores (<see cref="PlanarCoreFootprint.Count"/>, cached per mesh).
    /// </summary>
    public static long ResidentBytes(PlanarMesh mesh, PlanarFillSettings? settings = null)
        => ResidentBytes(mesh.SolvedUnknownCount, PlanarCoreFootprint.Count(mesh, settings));

    /// <summary>
    /// <b>The one parenthetical all three ceiling refusals quote, so the three cannot drift.</b> It
    /// says what the number IS, because a bare megabyte figure next to a ceiling reads as a machine
    /// limit — which R17 is not (see <see cref="SurfaceMesher"/>'s own note on that).
    /// </summary>
    public static string ResidentPhrase(int n, int cellCount = 0)
        => ResidentPhrase(n, PlanarCoreFootprint.Estimate(n, cellCount));

    /// <inheritdoc cref="ResidentPhrase(int, int)"/>
    public static string ResidentPhrase(int n, PlanarCoreFootprint cores)
        => $"{Megabytes(ResidentBytes(n, cores)):N0} MB resident at the peak of one frequency " +
           $"point (the matrix's lower triangle, which P7's in-place factorisation consumes, its " +
           $"diagonal, the cached " +
           $"cores and the fill's working space), against " +
           $"{Megabytes(ResidentBytes(SurfaceMesher.UnknownCeiling)):N0} MB at the ceiling";

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>
    /// R-fil-10 / R17, in <see cref="SurfaceMesher"/>'s own words and with the same numbers, asked
    /// before anything of that size is allocated.
    /// </summary>
    /// <param name="limit">The run's memory limit. ADVISORY: a run that carries one was asked about its size
    /// before it started (<see cref="SurfaceMesher.LimitOwner"/>'s note), so this guard lets it through.</param>
    /// <param name="mesh">Kept for the callers' signature; the fixed ceiling below needs no mesh.</param>
    public static void GuardCeiling(int n, int cellCount = 0, long? limit = null,
                                    PlanarMemoryLimitSource source = PlanarMemoryLimitSource.MachineLimit,
                                    PlanarMesh? mesh = null)
    {
        if (limit is not null) return;   // a memory limit is asked about, never refused

        if (n <= SurfaceMesher.UnknownCeiling) return;
        // ── MIM-13 item 1 — WHAT THIS SENTENCE USED TO END ON, AND WHY IT HAD TO CHANGE ─────────
        //
        // It ended "full-wave analysis of a structure this size needs matrix compression, which is
        // not built." That was true when it was written and has not been since M5: AIM is matrix
        // compression, it is built, it is a checkbox in Solver options, and it carries its own,
        // higher, separately measured ceiling. A user reading the old clause concludes the tool
        // cannot do this at all and stops — which is exactly what the 2026-09 spiral report looked
        // like. So the two ceilings are both named, with the path each governs, and the accelerated
        // one is offered rather than denied.
        //
        // The REMEDY LIST is still not this guard's to write. SurfaceMesher.BuildRefusal asks which
        // quantity actually binds before naming a knob, and this call site has no mesh geometry left
        // to ask it of — naming "lower Cells per wavelength" unconditionally from here is the defect
        // the 2026-08-14 owner report was about, in a second place. It points at the mesh report,
        // which is where the remedies that BIND are written.
        throw new InvalidOperationException(
            $"This geometry needs {n:N0} unknowns, which is past the " +
            $"{SurfaceMesher.UnknownCeiling:N0}-unknown ceiling this kernel is built for on the " +
            "DENSE path " +
            $"({ResidentPhrase(n, cellCount)}). " +
            $"The ACCELERATED solve (Solver options) has its own, higher ceiling — " +
            $"{SurfaceMesher.AcceleratedUnknownCeiling:N0} unknowns, on a single-level mesh — and " +
            (n <= SurfaceMesher.AcceleratedUnknownCeiling
                ? "this mesh fits under it, so turning it on is the first thing to try. "
                : $"this mesh is past that too ({(double)n / SurfaceMesher.AcceleratedUnknownCeiling:G3}× over), " +
                  "so the count itself has to come down. ") +
            "Mesh the setup for the budget and for the remedies that act on this particular " +
            "geometry — which knob binds depends on the artwork, and this guard has no mesh left to " +
            "ask it of.");
    }

    /// <summary>Fills the Galerkin matrix at one angular frequency and wraps it. The cores' own
    /// settings choose the factorisation and the parallel cap it will run under.</summary>
    public static PlanarSystem Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                     PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        // PM-2 — a merged mesh is held and factored at its MERGED size; its fill never forms the
        // elemental matrix at all.
        if (cores.Mesh.Merge is { } merge)
        {
            GuardCeiling(merge.Count, cores.CellCount, cores.Settings.MemoryLimitBytes, cores.Settings.MemoryLimitSource, cores.Mesh);
            return new PlanarSystem(PlanarFill.FillMergedLower(cores, termsA, termsQ, omega), cores.Settings, merge);
        }
        GuardCeiling(cores.UnknownCount, cores.CellCount, cores.Settings.MemoryLimitBytes, cores.Settings.MemoryLimitSource, cores.Mesh);
        return new PlanarSystem(PlanarFill.FillLower(cores, termsA, termsQ, omega), cores.Settings);
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
        if (cores.Mesh.Merge is not null)
            throw new InvalidOperationException(
                "Rooftop merging is built for a single conductor level with no vias, and the mesher " +
                "never attaches a merge to any other mesh. This one carries one; solve it elemental.");
        GuardCeiling(cores.UnknownCount, cores.CellCount, cores.Settings.MemoryLimitBytes, cores.Settings.MemoryLimitSource, cores.Mesh);
        return new PlanarSystem(PlanarFill.FillMultiLevelLower(cores, set, levels, omega), cores.Settings);
    }

    /// <summary>
    /// Wraps an already-filled matrix (the reduction tests build theirs directly).
    ///
    /// <para><b>CAP-2 — the square's LOWER triangle is copied into a <see cref="BlockLowerMatrix"/>,
    /// and the square is left as it was</b>; its upper triangle is not read. A small-N convenience:
    /// the copy is half a square on top of the square the caller already holds.</para>
    /// </summary>
    public static PlanarSystem Wrap(Mat<Complex> matrix, PlanarFillSettings? settings = null)
    {
        GuardCeiling(matrix.RowCount, limit: settings?.MemoryLimitBytes,
                     source: settings?.MemoryLimitSource ?? PlanarMemoryLimitSource.MachineLimit);
        return new PlanarSystem(BlockLowerMatrix.FromLower(matrix), settings ?? PlanarFillSettings.Default);
    }

    /// <summary>
    /// Wraps an already-filled lower triangle. <b>P7 — it is CONSUMED by the first factorisation.</b>
    /// A caller that still wants it afterwards passes <c>matrix.Copy()</c>; it is deliberately not
    /// copied here, because the copy is the whole cost P7 removed.
    /// </summary>
    public static PlanarSystem Wrap(BlockLowerMatrix matrix, PlanarFillSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        GuardCeiling(matrix.Size, limit: settings?.MemoryLimitBytes,
                     source: settings?.MemoryLimitSource ?? PlanarMemoryLimitSource.MachineLimit);
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
            _ldl = SymmetricFactorization.Factor(_a!, _st);
        }
        else
        {
            // The pre-P7 path. NumFlat's LU wants the square, so CAP-2's triangle is expanded for it
            // — the mirror is a copy, so this is the very matrix the fill's own mirror used to make —
            // and dropped, so the reference path holds the square and its factors, not a third copy.
            // `Matrix` is sealed off either way, because a property whose validity depends on a
            // setting is worse than one that is simply gone.
            var dense = _a!.ToDense();
            _a = null;
            _lu = dense.Lu();
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
        var b = _merge is { } mg ? mg.Restrict(rhs) : rhs;
        var x = _ldl is { } ldl ? ldl.Solve(b) : _lu!.Solve(b);
        if (_residualReference is { } z) LastResidual = SymmetricFactorization.Residual(z, x, b);
        return _merge is { } mp ? mp.Prolong(x) : x;
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

        IReadOnlyList<Vec<Complex>> bs = rhs;
        if (_merge is { } mg)
        {
            var restricted = new Vec<Complex>[rhs.Count];
            for (int r = 0; r < rhs.Count; r++) restricted[r] = mg.Restrict(rhs[r]);
            bs = restricted;
        }

        Vec<Complex>[] xs;
        if (_ldl is { } ldl)
        {
            xs = ldl.Solve(bs);
        }
        else
        {
            xs = new Vec<Complex>[bs.Count];
            for (int r = 0; r < bs.Count; r++) xs[r] = _lu!.Solve(bs[r]);
        }

        if (_residualReference is { } z)
        {
            double worst = 0;
            for (int r = 0; r < xs.Length; r++)
                worst = Math.Max(worst, SymmetricFactorization.Residual(z, xs[r], bs[r]));
            LastResidual = worst;
        }
        if (_merge is { } mp)
            for (int r = 0; r < xs.Length; r++) xs[r] = mp.Prolong(xs[r]);
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
        PlanarSystem.GuardCeiling(mesh.SolvedUnknownCount, mesh.Cells.Count, st.MemoryLimitBytes, st.MemoryLimitSource, mesh);

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
