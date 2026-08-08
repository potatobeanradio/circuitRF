using System.Numerics;

namespace CircuitRF.WBond;

/// <summary>
/// Dense LU factorisation with partial pivoting, for the <b>complex symmetric</b> impedance matrix.
///
/// <h3>Why not <see cref="CholeskyFactor"/></h3>
/// <para><c>Z = R + jω(L + L_int)</c> is <b>symmetric but not Hermitian</b>: <c>Zᵀ = Z</c> while
/// <c>Z* ≠ Z</c>. Cholesky requires Hermitian positive-definite and simply does not apply — and,
/// unlike the real SPD case, a complex LDLᵀ <i>without pivoting</i> can break down on a matrix that
/// is perfectly well conditioned. Making the real factor "complex" is therefore the wrong answer:
/// it passes on a well-conditioned test and fails on a real design.</para>
///
/// <para>Partial pivoting costs a permutation vector and buys unconditional robustness. The symmetry
/// is not exploited, so this is ~2× the flops of a symmetric factorisation — a trade taken
/// deliberately, because this runs once per frequency point rather than once per drag frame.</para>
/// </summary>
public sealed class ComplexLu
{
    private readonly Complex[] _lu;   // row-major, n x n, L below the diagonal and U on/above it
    private readonly int[] _pivot;    // row permutation

    private ComplexLu(Complex[] lu, int[] pivot, int n)
    {
        _lu = lu;
        _pivot = pivot;
        Order = n;
    }

    public int Order { get; }

    /// <summary>
    /// Factorises a dense complex matrix given row-major. The input is not modified.
    /// </summary>
    /// <exception cref="InvalidOperationException">The matrix is singular to working precision.</exception>
    public static ComplexLu Factor(Complex[] matrix, int n)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length < n * n)
            throw new ArgumentException(
                $"Matrix is {matrix.Length} long, expected at least {n * n}.", nameof(matrix));

        var lu = new Complex[n * n];
        Array.Copy(matrix, lu, n * n);

        var pivot = new int[n];
        for (int i = 0; i < n; i++) pivot[i] = i;

        for (int k = 0; k < n; k++)
        {
            // Partial pivot on magnitude — the step a symmetric factorisation would skip, and the
            // reason this is robust where a complex LDL^T is not.
            int best = k;
            double bestMagnitude = _Magnitude(lu[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double magnitude = _Magnitude(lu[i * n + k]);
                if (magnitude > bestMagnitude) { bestMagnitude = magnitude; best = i; }
            }

            if (bestMagnitude == 0.0)
                throw new InvalidOperationException(
                    $"The impedance matrix is singular at wire {k}. That normally means two wires " +
                    "share the same geometry, or a wire has zero length.");

            if (best != k)
            {
                for (int j = 0; j < n; j++)
                    (lu[k * n + j], lu[best * n + j]) = (lu[best * n + j], lu[k * n + j]);
                (pivot[k], pivot[best]) = (pivot[best], pivot[k]);
            }

            Complex diagonal = lu[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                Complex factor = lu[i * n + k] / diagonal;
                lu[i * n + k] = factor;
                if (factor == Complex.Zero) continue;

                int ir = i * n, kr = k * n;
                for (int j = k + 1; j < n; j++)
                    lu[ir + j] -= factor * lu[kr + j];
            }
        }

        return new ComplexLu(lu, pivot, n);
    }

    /// <summary>Solves <c>A x = b</c>, returning x. <paramref name="rhs"/> is not modified.</summary>
    public Complex[] Solve(Complex[] rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        int n = Order;

        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = rhs[_pivot[i]];

        // Forward substitution through L (unit diagonal).
        for (int i = 1; i < n; i++)
        {
            Complex sum = x[i];
            int ir = i * n;
            for (int k = 0; k < i; k++) sum -= _lu[ir + k] * x[k];
            x[i] = sum;
        }

        // Back substitution through U.
        for (int i = n - 1; i >= 0; i--)
        {
            Complex sum = x[i];
            int ir = i * n;
            for (int k = i + 1; k < n; k++) sum -= _lu[ir + k] * x[k];
            x[i] = sum / _lu[ir + i];
        }

        return x;
    }

    /// <summary>
    /// <c>|z|</c> without the square root — comparing squared magnitudes is enough to pivot, and it
    /// avoids <c>Complex.Abs</c>'s overflow-safe scaling in the innermost loop.
    /// </summary>
    private static double _Magnitude(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}
