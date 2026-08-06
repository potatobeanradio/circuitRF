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
/// The dense complex system for one frequency: the matrix, its size, and its LU — with R17's refusal
/// asked before the allocation rather than after it.
/// </summary>
public sealed class PlanarSystem
{
    public Mat<Complex> Matrix { get; }
    public int Size => Matrix.RowCount;

    private LuDecompositionComplex? _lu;

    private PlanarSystem(Mat<Complex> matrix) => Matrix = matrix;

    /// <summary>Bytes of dense complex matrix — §10.7's own budget line.</summary>
    public static long MatrixBytes(int n) => 16L * n * n;

    /// <summary>
    /// R-fil-10 / R17, in <see cref="SurfaceMesher"/>'s own words and with the same numbers, asked
    /// before anything of that size is allocated.
    /// </summary>
    public static void GuardCeiling(int n)
    {
        if (n <= SurfaceMesher.UnknownCeiling) return;
        throw new InvalidOperationException(
            $"This geometry needs {n:N0} unknowns, which is past the " +
            $"{SurfaceMesher.UnknownCeiling:N0}-unknown ceiling this kernel is built for " +
            $"({MatrixBytes(n) / (1024.0 * 1024.0):N0} MB of dense complex matrix, against " +
            $"{MatrixBytes(SurfaceMesher.UnknownCeiling) / (1024.0 * 1024.0):N0} MB at the ceiling). " +
            "Lower Cells per wavelength, turn the edge mesh off, or analyse a smaller region — " +
            "full-wave analysis of a structure this size needs matrix compression, which is not built.");
    }

    /// <summary>Fills the Galerkin matrix at one angular frequency and wraps it.</summary>
    public static PlanarSystem Build(PlanarFillCores cores, PlanarKernelTerms termsA,
                                     PlanarKernelTerms termsQ, double omega)
    {
        ArgumentNullException.ThrowIfNull(cores);
        GuardCeiling(cores.UnknownCount);
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
        GuardCeiling(cores.UnknownCount);
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
        PlanarSystem.GuardCeiling(mesh.Bases.Count);

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
