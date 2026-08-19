using System.Threading.Tasks;

namespace CircuitRF.WBond;

/// <summary>
/// A dense lower-triangular Cholesky factor of a symmetric positive-definite matrix, with rank-1
/// update/downdate.
///
/// <para><b>Why this exists rather than <c>NumFlat.CholeskyDecompositionDouble</c>.</b> NumFlat
/// provides <c>Decompose</c> and <c>Solve</c> but <b>no rank-k update</b>, and the incremental drag
/// path (R-wb-9) is built entirely on updating the factor rather than refactorising: moving one wire
/// is a rank-2 change whatever N is, at 0.144 ms against 22.9 ms for a fresh factorisation at
/// N = 600. Writing one factor that serves both the cold and the incremental path is cheaper than
/// marshalling into NumFlat for the cold half and hand-rolling the update half anyway.</para>
///
/// <para><b>Maintain the FACTOR, not an explicit inverse (D6 / WB14).</b> Rank-k updating the factor
/// is O(kN²) and numerically stable; maintaining L⁻¹ by Sherman–Morrison is comparably fast but
/// drifts, and the M × M array answer only ever needs M triangular solves, never a full
/// inverse.</para>
/// </summary>
public sealed class CholeskyFactor
{
    private readonly double[] _l;   // lower triangular, row-major, n x n
    private bool _inverted;         // set by InvertInPlace; _l then holds A^-1, not L

    private CholeskyFactor(double[] l, int n)
    {
        _l = l;
        Order = n;
    }

    public int Order { get; }

    /// <summary>The lower-triangular factor, row-major. Entries above the diagonal are zero.</summary>
    public double[] Lower => _l;

    /// <summary>
    /// Overwrites this factor with <c>A⁻¹</c> — full, symmetric, row-major — and returns it.
    /// <b>The factor is consumed</b>: <see cref="SolveInPlace"/> throws afterwards.
    ///
    /// <h3>Why an explicit inverse here, when the doc comment above says to maintain the factor</h3>
    /// <para>That rule is about the <i>incremental</i> path, where the factor is updated per drag frame
    /// and an explicitly maintained inverse drifts. This is the opposite regime: a factor built once,
    /// consumed once, against <b>N right-hand sides</b>. N triangular solves cost <c>N³</c>; forming the
    /// inverse costs <c>N³/3</c>, and the products that consume it in <see cref="Mom.MomAssembly"/> are
    /// index expressions rather than GEMMs (<c>R</c> has one non-zero per row and <c>Ã</c> two), so the
    /// whole reduction drops from <c>~2N³</c> to <c>~⅔N³</c>. WM-3 §2. Nothing incremental uses this.</para>
    ///
    /// <h3>Genuinely in place, in three passes</h3>
    /// <para><c>A⁻¹ = L⁻ᵀL⁻¹ = U Uᵀ</c> with <c>U = (L⁻¹)ᵀ</c>. So: invert the triangle in place, transpose
    /// it in place, then form <c>U Uᵀ</c> in place (LAPACK's <c>lauum</c> ordering — column <i>i</i> of the
    /// result reads only columns <i>&gt; i</i>, which are still <c>U</c>). No second N × N array is
    /// allocated at any point, which is what keeps §8's memory arithmetic unchanged.</para>
    /// </summary>
    /// <param name="parallel">
    /// Parallelise the two cubic passes over their independent rows. Each pass is a sequence of N
    /// dependent steps whose <i>interiors</i> are independent, so this is a per-step fan-out and is
    /// worth nothing at small N — it is skipped below <see cref="ParallelThreshold"/> whatever is
    /// passed.
    /// </param>
    /// <param name="run">Cancellation and progress, or null.</param>
    /// <param name="stage">
    /// The stage name to report under. <b>Null means report nothing</b> — ticking without owning a stage
    /// would advance whichever counter the caller had already opened, which is how a bar ends up past
    /// its own denominator. Cancellation is honoured whenever <paramref name="run"/> is non-null,
    /// labelled or not: an inverse at N_s = 4,800 is tens of seconds and a Stop must not have to wait
    /// for it.
    /// </param>
    public double[] InvertInPlace(bool parallel = true, WBondRunControl? run = null, string? stage = null)
    {
        if (_inverted) throw new InvalidOperationException("This factor has already been inverted in place.");

        int n = Order;
        _inverted = true;
        var l = _l;
        bool fan = parallel && n >= ParallelThreshold;

        // TWO of the four passes are cubic (1 and 3); passes 2 and 4 are O(n^2) transposes and are not
        // worth a unit of their own. So the stage counts 2n column-steps.
        var report = stage is null ? null : run;
        report?.BeginStage(stage!, 2L * n);

        // ---- 1. X = L^-1, lower triangular, in place.
        //
        // From X L = I rather than L X = I: X[i,j] = -(1/L[j,j]) * SUM_{k=j+1..i} X[i,k] L[k,j], which
        // depends on row i's own already-inverted columns (k > j) and on the ORIGINAL column j. Going j
        // downward makes every row of the step independent — the L X = I form does not, because there
        // X[i,j] depends on X[k,j] for k < i and the step is a serial recurrence.
        var column = new double[n];
        for (int j = n - 1; j >= 0; j--)
        {
            double djj = l[j * n + j];
            if (djj == 0.0)
                throw new InvalidOperationException($"The Cholesky factor is singular at index {j}.");

            for (int k = j + 1; k < n; k++) column[k] = l[k * n + j];
            double inv = -1.0 / djj;
            l[j * n + j] = -inv;

            if (fan && n - j > ParallelThreshold) InvertRowsParallel(l, n, j, inv, column);
            else for (int i = j + 1; i < n; i++) InvertRow(l, n, j, inv, column, i);

            if (report is not null) report.TickStage();
            else run?.ThrowIfCancellationRequested();
        }

        // ---- 2. U = Xᵀ, upper triangular, in place.
        for (int i = 0; i < n; i++)
            for (int k = i + 1; k < n; k++)
                (l[i * n + k], l[k * n + i]) = (l[k * n + i], l[i * n + k]);

        // ---- 3. A^-1 = U Uᵀ, upper triangle, in place. Column i of the result is written from rows
        // 0..i-1 and columns i+1..n-1 only — everything it reads is still U, which is what makes the
        // ascending order the correct one (LAPACK's lauum ordering).
        for (int i = 0; i < n; i++)
        {
            int ir = i * n;
            double aii = l[ir + i];

            double diagonal = 0.0;
            for (int k = i; k < n; k++) diagonal += l[ir + k] * l[ir + k];

            if (fan && i > ParallelThreshold) ProductRowsParallel(l, n, i, aii);
            else for (int r = 0; r < i; r++) ProductRow(l, n, i, aii, r);

            l[ir + i] = diagonal;

            if (report is not null) report.TickStage();
            else run?.ThrowIfCancellationRequested();
        }

        // ---- 4. Mirror. The inverse of a symmetric matrix is symmetric, and a caller that has to
        // remember which triangle is populated is a caller that will read the wrong one.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                l[j * n + i] = l[i * n + j];

        return l;
    }

    // The two cubic passes are written as STATIC row kernels with every operand passed in, and each has
    // its serial and its parallel driver. Written as local functions instead, the C# compiler hoists
    // every captured local into a display class — and the JIT then cannot keep the array in a register
    // or drop the bounds check, which measured 7x slower on the serial path at N = 200. The duplication
    // is two lines each and it is the difference between 0.3 and 2 GFLOP/s.

    private static void InvertRow(double[] l, int n, int j, double negInvDjj, double[] column, int i)
    {
        int ir = i * n;
        double sum = 0.0;
        for (int k = j + 1; k <= i; k++) sum += l[ir + k] * column[k];
        l[ir + j] = negInvDjj * sum;
    }

    private static void InvertRowsParallel(double[] l, int n, int j, double negInvDjj, double[] column) =>
        Parallel.For(j + 1, n, i => InvertRow(l, n, j, negInvDjj, column, i));

    private static void ProductRow(double[] l, int n, int i, double aii, int r)
    {
        int rr = r * n, ir = i * n;
        double sum = aii * l[rr + i];
        for (int k = i + 1; k < n; k++) sum += l[rr + k] * l[ir + k];
        l[rr + i] = sum;
    }

    private static void ProductRowsParallel(double[] l, int n, int i, double aii) =>
        Parallel.For(0, i, r => ProductRow(l, n, i, aii, r));

    /// <summary>
    /// Below this order the per-step fan-out of <see cref="InvertInPlace"/> costs more than it saves —
    /// each step is only O(N) rows of O(N) work, so the thread-pool hand-off dominates. <b>Measured</b>:
    /// 1.00× at N = 200, 0.87× at N = 400, 1.17× at N = 928 and 1.95× at N = 1,416, so the
    /// fan-out is only worth taking well above a thousand. <b>Both passes are matrix-VECTOR work</b>
    /// (an unblocked triangular inversion and an unblocked <c>U Uᵀ</c>), which is bandwidth-bound
    /// rather than core-bound — that, and not the fan-out cost, is why the speedup is 2× and not 10×.
    /// A blocked BLAS-3 formulation would change that and is a project, not a knob.
    /// </summary>
    private const int ParallelThreshold = 512;

    /// <summary>
    /// Factorises a symmetric positive-definite matrix given row-major. The input is not modified.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The matrix is not positive definite. For an inductance matrix that means the geometry is
    /// degenerate — most often two wires occupying the same points — and saying so is far more use
    /// than a NaN propagating into the array readout.
    /// </exception>
    /// <param name="run">Cancellation and progress, or null.</param>
    /// <param name="stage">The stage name to report under; null reports nothing. See
    /// <see cref="InvertInPlace"/>.</param>
    /// <param name="matrixName">
    /// What to call the matrix in the failure message. <b>Not cosmetic</b>: this routine factorises
    /// the inductance matrix AND the potential-coefficient matrix, whose degenerate geometries are
    /// different (see <see cref="CapacitanceReduction"/>), and a message naming the wrong one sends
    /// the reader hunting for duplicate wires that are not there — which is exactly what happened
    /// when a wire lying in the ground plane crashed the editor (2026-08-19).
    /// </param>
    /// <param name="hint">
    /// What that particular matrix's degeneracy normally means, appended to the message. Null takes
    /// the inductance wording.
    /// </param>
    public static CholeskyFactor Factor(double[] matrix, int n, WBondRunControl? run = null, string? stage = null,
                                        string matrixName = "inductance matrix", string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length < n * n)
            throw new ArgumentException($"Matrix is {matrix.Length} long, expected at least {n * n}.", nameof(matrix));

        var l = new double[n * n];
        var report = stage is null ? null : run;
        report?.BeginStage(stage!, n);

        for (int j = 0; j < n; j++)
        {
            double diagonal = matrix[j * n + j];
            for (int k = 0; k < j; k++)
            {
                double ljk = l[j * n + k];
                diagonal -= ljk * ljk;
            }

            if (diagonal <= 0.0 || double.IsNaN(diagonal))
                throw new InvalidOperationException(
                    $"The {matrixName} is not positive definite (pivot {diagonal:E3} at wire {j}). " +
                    (hint ?? "That normally means two wires share the same geometry, or a wire has zero length."));

            double d = Math.Sqrt(diagonal);
            l[j * n + j] = d;

            double invD = 1.0 / d;
            for (int i = j + 1; i < n; i++)
            {
                double sum = matrix[i * n + j];
                int ir = i * n, jr = j * n;
                for (int k = 0; k < j; k++)
                    sum -= l[ir + k] * l[jr + k];
                l[ir + j] = sum * invD;
            }

            if (report is not null) report.TickStage();
            else run?.ThrowIfCancellationRequested();
        }

        return new CholeskyFactor(l, n);
    }

    /// <summary>
    /// Solves <c>A x = b</c> in place, given <c>b</c> in <paramref name="rhs"/>.
    /// Forward substitution through the factor, then back substitution through its transpose.
    /// </summary>
    public void SolveInPlace(double[] rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        if (_inverted)
            throw new InvalidOperationException(
                "This factor was consumed by InvertInPlace and no longer holds L. Multiply by the " +
                "inverse it returned instead.");

        int n = Order;

        for (int i = 0; i < n; i++)
        {
            double sum = rhs[i];
            int ir = i * n;
            for (int k = 0; k < i; k++)
                sum -= _l[ir + k] * rhs[k];
            rhs[i] = sum / _l[ir + i];
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double sum = rhs[i];
            for (int k = i + 1; k < n; k++)
                sum -= _l[k * n + i] * rhs[k];
            rhs[i] = sum / _l[i * n + i];
        }
    }

    /// <summary>
    /// Rank-1 update: refactorises <c>A + v·vᵀ</c> in O(N²) without touching <c>A</c>.
    /// Pass <paramref name="downdate"/> to apply <c>A − v·vᵀ</c> instead.
    ///
    /// <para>A full row/column change — which is what moving one wire does — is
    /// <c>ΔL = e_k rᵀ + r e_kᵀ</c>, a <b>rank-2</b> change however large N is. It is applied as two
    /// rank-1 steps by writing the symmetric outer product as a difference of squares.</para>
    /// </summary>
    public void RankOneUpdate(double[] v, bool downdate = false)
    {
        ArgumentNullException.ThrowIfNull(v);
        int n = Order;
        var w = (double[])v.Clone();
        double sign = downdate ? -1.0 : 1.0;

        for (int k = 0; k < n; k++)
        {
            double lkk = _l[k * n + k];
            double wk = w[k];
            double r2 = lkk * lkk + sign * wk * wk;

            if (r2 <= 0.0)
                throw new InvalidOperationException(
                    $"Rank-1 {(downdate ? "downdate" : "update")} left the matrix indefinite at index {k}.");

            double r = Math.Sqrt(r2);
            double c = r / lkk;
            double s = wk / lkk;

            _l[k * n + k] = r;
            for (int i = k + 1; i < n; i++)
            {
                int ir = i * n + k;
                _l[ir] = (_l[ir] + sign * s * w[i]) / c;
                w[i] = c * w[i] - s * _l[ir];
            }
        }
    }
}
