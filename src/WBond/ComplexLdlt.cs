using System.Numerics;

namespace CircuitRF.WBond;

/// <summary>
/// Dense <b>unpivoted LDLᵀ</b> for a complex <b>symmetric</b> (not Hermitian) matrix — half the flops
/// of <see cref="ComplexLu"/>, at the cost of a stability guarantee that has to be checked rather than
/// assumed.
///
/// <code>
/// A = L D Lᵀ        L unit lower triangular, D diagonal, BOTH complex
/// </code>
///
/// <h3>Why this exists, and what it is allowed to be used for</h3>
/// <para><c>M̃(ω) = −ω²L + K̃ + jωD(ω)</c> is symmetric and not Hermitian, so
/// <see cref="CholeskyFactor"/> cannot apply and <see cref="ComplexLu"/>'s partial pivoting is the
/// robust answer. Pivoting is also what forbids exploiting the symmetry, and it costs <c>2N³/3</c>
/// against this factorisation's <c>N³/3</c> — which, at one factorisation per frequency point over a
/// 201-point sweep, is the single largest term in a wirebond MoM sweep. WM-3 §3.</para>
///
/// <h3>Unpivoted LDLᵀ CAN break down on a well-conditioned matrix, so it reports whether it did</h3>
/// <para>A symmetric complex matrix has no diagonal-dominance argument to lean on: a pivot can be
/// annihilated by cancellation between the real and imaginary parts even when the matrix itself is far
/// from singular. <b>That failure is silent</b> — the factorisation completes and returns finite
/// garbage. <see cref="PivotRatio"/> is <c>min|d_k| / max|d_k|</c> and is the caller's guard: below a
/// declared threshold the caller must refactorise the same matrix with <see cref="ComplexLu"/> and say
/// so, which is exactly what <see cref="Mom.WireMomSolver"/> does per frequency point.</para>
///
/// <para>Whether it ever happens for real bond geometry is a <b>measurement</b>, not an argument, and
/// <c>src/WBond/Mom/RESOLVED.md</c> carries it: a 201-point sweep with both factorisations agreeing to
/// 1e-9 at every point, with the number of fallbacks recorded.</para>
/// </summary>
public sealed class ComplexLdlt
{
    private readonly Complex[] _a;   // unit-lower L strictly below the diagonal, D on it

    private ComplexLdlt(Complex[] a, int n, double pivotRatio)
    {
        _a = a;
        Order = n;
        PivotRatio = pivotRatio;
    }

    public int Order { get; }

    /// <summary>
    /// <c>min|d_k| / max|d_k|</c> over the factorisation's own pivots — the breakdown detector. It is
    /// <b>not</b> a condition number and must not be reported as one; it is a cheap necessary condition
    /// that catches the failure mode this factorisation has and partial pivoting does not.
    /// </summary>
    public double PivotRatio { get; }

    /// <summary>Factorises a copy of <paramref name="matrix"/>, leaving the caller's array intact.</summary>
    public static ComplexLdlt Factor(Complex[] matrix, int n)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length < (long)n * n)
            throw new ArgumentException($"Matrix is {matrix.Length} long, expected at least {(long)n * n}.", nameof(matrix));

        var copy = new Complex[(long)n * n];
        Array.Copy(matrix, copy, copy.Length);
        return FactorInPlace(copy, n);
    }

    /// <summary>
    /// The same, <b>overwriting</b> <paramref name="matrix"/> with the factor. The sweep path takes
    /// this one: <c>M̃</c> is already a per-point scratch buffer, and at N_s = 4,800 a second copy is
    /// 369 MB <i>per thread</i> — which is the term that decides how many threads §4.1 may use.
    /// </summary>
    public static ComplexLdlt FactorInPlace(Complex[] matrix, int n)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length < (long)n * n)
            throw new ArgumentException($"Matrix is {matrix.Length} long, expected at least {(long)n * n}.", nameof(matrix));

        var w = new Complex[n];
        double smallest = double.MaxValue, largest = 0.0;

        // Left-looking: column j is finished against columns 0..j-1 before anything to its right is
        // touched. Row-major makes both inner loops contiguous in k, which the right-looking form does
        // not — it would sweep a trailing submatrix column by column.
        for (int j = 0; j < n; j++)
        {
            int jr = j * n;

            Complex d = matrix[jr + j];
            for (int k = 0; k < j; k++)
            {
                w[k] = matrix[jr + k] * matrix[k * n + k];
                d -= matrix[jr + k] * w[k];
            }

            matrix[jr + j] = d;

            double magnitude = _Magnitude(d);
            if (magnitude < smallest) smallest = magnitude;
            if (magnitude > largest) largest = magnitude;

            if (magnitude == 0.0)
                throw new InvalidOperationException(
                    $"The complex-symmetric factorisation broke down at index {j} (zero pivot). The " +
                    "matrix may still be well conditioned — an unpivoted LDLt has no diagonal-dominance " +
                    "guarantee — so the caller should fall back to a pivoted LU rather than conclude " +
                    "the system is singular.");

            Complex inv = Complex.One / d;
            for (int i = j + 1; i < n; i++)
            {
                int ir = i * n;
                Complex s = matrix[ir + j];
                for (int k = 0; k < j; k++) s -= matrix[ir + k] * w[k];
                matrix[ir + j] = s * inv;
            }
        }

        double ratio = largest > 0.0 ? Math.Sqrt(smallest / largest) : 0.0;
        return new ComplexLdlt(matrix, n, ratio);
    }

    /// <summary>Solves <c>A x = b</c>, returning x. <paramref name="rhs"/> is not modified.</summary>
    public Complex[] Solve(Complex[] rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        var x = new Complex[Order];
        Array.Copy(rhs, x, Order);
        SolveInPlace(x, 1);
        return x;
    }

    /// <summary>
    /// Solves for <paramref name="columns"/> right-hand sides at once, in place, over an
    /// <c>Order × columns</c> <b>row-major</b> block.
    ///
    /// <para>One triangular sweep serves all of them: the T port solves of a MoM frequency point read
    /// the factor once instead of T times, which is what makes the per-point cost the factorisation and
    /// nothing else.</para>
    /// </summary>
    public void SolveInPlace(Complex[] block, int columns)
    {
        ArgumentNullException.ThrowIfNull(block);
        int n = Order;
        if (block.Length < (long)n * columns)
            throw new ArgumentException(
                $"Need room for {n} x {columns}, got {block.Length}.", nameof(block));

        // L y = b.
        for (int i = 1; i < n; i++)
        {
            int ir = i * n, ib = i * columns;
            for (int k = 0; k < i; k++)
            {
                Complex lik = _a[ir + k];
                if (lik == Complex.Zero) continue;
                int kb = k * columns;
                for (int c = 0; c < columns; c++) block[ib + c] -= lik * block[kb + c];
            }
        }

        // D z = y.
        for (int i = 0; i < n; i++)
        {
            Complex inv = Complex.One / _a[i * n + i];
            int ib = i * columns;
            for (int c = 0; c < columns; c++) block[ib + c] *= inv;
        }

        // Lᵀ x = z, as an axpy over rows so the factor is read row-major here too.
        for (int k = n - 1; k >= 1; k--)
        {
            int kr = k * n, kb = k * columns;
            for (int i = 0; i < k; i++)
            {
                Complex lki = _a[kr + i];
                if (lki == Complex.Zero) continue;
                int ib = i * columns;
                for (int c = 0; c < columns; c++) block[ib + c] -= lki * block[kb + c];
            }
        }
    }

    private static double _Magnitude(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}
