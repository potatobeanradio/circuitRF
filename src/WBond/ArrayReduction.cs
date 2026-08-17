namespace CircuitRF.WBond;

/// <summary>
/// The array-basis inductance and the per-wire current sharing that falls out of it
/// (wbond.md §3.4, R-wb-7).
///
/// <h3>The reduction</h3>
/// <para>With <b>L</b> the N × N wire-basis inductance matrix and <b>A</b> the N × M 0/1 mapping
/// matrix (exactly one 1 per row — wire <i>i</i> belongs to array <i>k</i>):</para>
/// <list type="bullet">
/// <item><b>Assumption 1</b> — every wire in an array starts on one pad and ends on another, so they
///   share a voltage drop: <b>V</b> = <b>A</b><b>u</b>.</item>
/// <item><b>Assumption 2</b> — KCL at the pads: <b>J</b> = <b>Aᵀ</b><b>I</b>.</item>
/// </list>
/// <para>Substituting into <b>V</b> = jω<b>LI</b> gives <b>J</b> = (jω)⁻¹(<b>AᵀL⁻¹A</b>)<b>u</b>, so</para>
/// <code>
/// L_arr = ( Aᵀ L⁻¹ A )⁻¹
/// </code>
/// <para>a congruence transform on the <i>inverse</i> inductance matrix, inverted back.
/// <b>Resistance never enters</b> — the reduction consumes <b>L</b> and <b>A</b> only, which is what
/// makes L_arr frequency-independent and cheap enough to run inside a drag.</para>
///
/// <h3>Two consequences worth knowing</h3>
/// <para><b>Because A is 0/1, AᵀΓA is a block SUM, not a matrix multiply</b> — literally "add up the
/// sub-blocks of Γ belonging to each array pair". No GEMM is emitted for it.</para>
/// <para><b>Reciprocity is structural, not a tolerance.</b> <b>L</b> symmetric ⇒ <b>L⁻¹</b>
/// symmetric ⇒ <b>AᵀL⁻¹A</b> symmetric, whatever the geometry.</para>
/// </summary>
public sealed class ArrayReduction
{
    private readonly double[] _lArr;        // M x M, row-major, henries
    private readonly double[] _gammaArr;    // M x M, row-major, inverse henries
    private readonly double[] _x;           // N x M, row-major: X = L^-1 A

    private ArrayReduction(double[] lArr, double[] gammaArr, double[] x, int wireCount, int arrayCount,
                           int[] arrayOfWire, string[] arrayNames)
    {
        _lArr = lArr;
        _gammaArr = gammaArr;
        _x = x;
        WireCount = wireCount;
        ArrayCount = arrayCount;
        ArrayOfWire = arrayOfWire;
        ArrayNames = arrayNames;
    }

    public int WireCount { get; }

    public int ArrayCount { get; }

    public int[] ArrayOfWire { get; }

    public string[] ArrayNames { get; }

    /// <summary>The array-basis inductance in henries. Symmetric; the diagonal is each array's own L.</summary>
    public double this[int i, int j] => _lArr[i * ArrayCount + j];

    /// <summary>The array-basis inductance in <b>picohenries</b> — the unit the panel displays (WB27a).</summary>
    public double PicoHenries(int i, int j) => _lArr[i * ArrayCount + j] * 1e12;

    /// <summary><b>Γ_arr = AᵀL⁻¹A</b>, the inverse-inductance form the reduction actually assembles.</summary>
    public double Gamma(int i, int j) => _gammaArr[i * ArrayCount + j];

    /// <summary>
    /// The dimensionless coupling coefficient k = M_ij / √(L_ii·L_jj).
    ///
    /// <para>Offered alongside the pH mutuals because it is scale-free, and it is the number that
    /// tells a user whether two arrays are meaningfully coupled — a bare pH mutual does not, without
    /// mentally dividing by the selfs.</para>
    /// </summary>
    public double CouplingCoefficient(int i, int j)
    {
        double denominator = Math.Sqrt(this[i, i] * this[j, j]);
        return denominator == 0.0 ? 0.0 : this[i, j] / denominator;
    }

    /// <summary>
    /// Reduces a wire-basis inductance matrix onto the array basis.
    /// </summary>
    public static ArrayReduction Reduce(InductanceMatrix l, WireMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(l);
        ArgumentNullException.ThrowIfNull(mesh);
        return Reduce(l, null, mesh.ArrayOfWire, mesh.ArrayCount, mesh.ArrayNames);
    }

    /// <summary>
    /// Reduces a wire-basis inductance matrix onto the array basis, given the mapping directly.
    /// </summary>
    /// <param name="arrayOfWire">
    /// The mapping matrix <b>A</b> in compact form: the array index of each wire. Exactly one entry
    /// per wire, which is what makes <b>A</b> 0/1 with one 1 per row.
    /// </param>
    public static ArrayReduction Reduce(InductanceMatrix l, int[] arrayOfWire, int arrayCount,
                                        string[]? arrayNames = null)
        => Reduce(l, null, arrayOfWire, arrayCount, arrayNames);

    /// <summary>
    /// Reduces using an <b>already-maintained</b> Cholesky factor.
    ///
    /// <para><b>This overload is the drag path, and the difference is not marginal.</b> Factorising
    /// from scratch is ~22.7 ms at N = 600 while the twelve triangular solves alone are ~2.5 ms —
    /// so a <see cref="IncrementalFill"/> that carefully rank-2 updates its factor and then calls the
    /// factor-less overload throws away its entire advantage and blows the frame budget. Measured:
    /// 25.4 ms per drag frame with a refactorisation against ~5 ms without.</para>
    ///
    /// <para>The caller owns the contract that <paramref name="factor"/> matches <paramref name="l"/>.
    /// Nothing here can check it cheaply — a mismatched factor produces a plausible wrong answer, so
    /// this overload is for code that maintains both together.</para>
    /// </summary>
    public static ArrayReduction Reduce(InductanceMatrix l, CholeskyFactor? factor, int[] arrayOfWire,
                                        int arrayCount, string[]? arrayNames = null)
    {
        ArgumentNullException.ThrowIfNull(l);
        ArgumentNullException.ThrowIfNull(arrayOfWire);

        int n = l.Order;
        int m = arrayCount;

        if (arrayOfWire.Length != n)
            throw new ArgumentException(
                $"The wire-to-array map has {arrayOfWire.Length} entries but the matrix is {n} x {n}.",
                nameof(arrayOfWire));
        if (m < 0)
            throw new ArgumentOutOfRangeException(nameof(arrayCount), m, "An array count cannot be negative.");

        // ZERO arrays is a valid, fully-defined reduction (owner, 2026-08-16: "make it support 0
        // wires") — every matrix here is 0 × 0, every loop below runs zero times, and the result is
        // an object whose every accessor is honestly empty. It used to be refused, which is why a
        // wBond editor could not delete its own last wire. An empty ARRAY is still refused just
        // below: that one really does make A rank-deficient.
        if (m == 0 && n == 0)
            return new ArrayReduction([], [], [], 0, 0, arrayOfWire, arrayNames ?? []);

        // Every array must be non-empty, or A loses column rank and Gamma_arr is singular. Caught
        // here with a message naming the array rather than as a Cholesky pivot failure.
        var populated = new bool[m];
        foreach (int a in arrayOfWire)
        {
            if (a < 0 || a >= m)
                throw new ArgumentException($"Wire mapped to array index {a}, outside 0..{m - 1}.", nameof(arrayOfWire));
            populated[a] = true;
        }
        for (int a = 0; a < m; a++)
        {
            if (!populated[a])
                throw new InvalidOperationException(
                    $"Array '{arrayNames?[a] ?? a.ToString()}' has no wires. An empty array makes the mapping " +
                    "matrix rank-deficient and the array-basis inductance singular.");
        }

        var cholesky = factor ?? CholeskyFactor.Factor(l.Values, n);

        // X = L^-1 A, one triangular solve per array. A's columns are 0/1 indicators, so the
        // right-hand sides are built by marking membership rather than by a matrix product.
        var x = new double[n * m];
        var column = new double[n];
        for (int a = 0; a < m; a++)
        {
            Array.Clear(column);
            for (int i = 0; i < n; i++)
                if (arrayOfWire[i] == a) column[i] = 1.0;

            cholesky.SolveInPlace(column);

            for (int i = 0; i < n; i++)
                x[i * m + a] = column[i];
        }

        // Gamma_arr = A^T X. A is 0/1, so this is a scatter-add over wires, not a GEMM.
        var gamma = new double[m * m];
        for (int i = 0; i < n; i++)
        {
            int row = arrayOfWire[i] * m;
            int xr = i * m;
            for (int a = 0; a < m; a++)
                gamma[row + a] += x[xr + a];
        }

        // Symmetrise: Gamma_arr is symmetric in exact arithmetic, and forcing it makes reciprocity
        // structural in the returned object rather than true to rounding.
        for (int i = 0; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                double mean = 0.5 * (gamma[i * m + j] + gamma[j * m + i]);
                gamma[i * m + j] = mean;
                gamma[j * m + i] = mean;
            }
        }

        var lArr = InvertSymmetric(gamma, m);

        return new ArrayReduction(
            lArr, gamma, x, n, m, arrayOfWire,
            arrayNames ?? [.. Enumerable.Range(0, m).Select(i => i.ToString())]);
    }

    /// <summary>
    /// Per-wire current sharing for a given set of array terminal currents:
    /// <c>I = L⁻¹ A L_arr J = X · (L_arr J)</c>.
    ///
    /// <para>Two results fall out that a designer pays money for, and both are physics the reduction
    /// captures with no extra machinery: <b>edge wires carry more current than centre wires</b>
    /// (they have less mutual coupling), and an <b>undriven array carries a circulating current
    /// summing to zero</b>, because its wires are tied together at both ends and therefore form a
    /// shorted turn.</para>
    /// </summary>
    /// <param name="arrayCurrents">Terminal current into each array, amps. Length = <see cref="ArrayCount"/>.</param>
    public double[] CurrentShares(double[] arrayCurrents)
    {
        ArgumentNullException.ThrowIfNull(arrayCurrents);
        if (arrayCurrents.Length != ArrayCount)
            throw new ArgumentException(
                $"Expected {ArrayCount} array currents, got {arrayCurrents.Length}.", nameof(arrayCurrents));

        // u = L_arr J
        var u = new double[ArrayCount];
        for (int i = 0; i < ArrayCount; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < ArrayCount; j++)
                sum += _lArr[i * ArrayCount + j] * arrayCurrents[j];
            u[i] = sum;
        }

        // I = X u
        var currents = new double[WireCount];
        for (int i = 0; i < WireCount; i++)
        {
            double sum = 0.0;
            int xr = i * ArrayCount;
            for (int a = 0; a < ArrayCount; a++)
                sum += _x[xr + a] * u[a];
            currents[i] = sum;
        }

        return currents;
    }

    /// <summary>Inverts a small symmetric positive-definite matrix by solving against the identity.</summary>
    private static double[] InvertSymmetric(double[] a, int m)
    {
        var factor = CholeskyFactor.Factor(a, m);
        var inverse = new double[m * m];
        var column = new double[m];

        for (int j = 0; j < m; j++)
        {
            Array.Clear(column);
            column[j] = 1.0;
            factor.SolveInPlace(column);
            for (int i = 0; i < m; i++)
                inverse[i * m + j] = column[i];
        }

        for (int i = 0; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                double mean = 0.5 * (inverse[i * m + j] + inverse[j * m + i]);
                inverse[i * m + j] = mean;
                inverse[j * m + i] = mean;
            }
        }

        return inverse;
    }
}
