namespace CircuitRF.WBond;

/// <summary>
/// The array-basis capacitance, and the lumped end split the stamp uses (wbond.md §3.7).
///
/// <h3>The reduction is a plain congruence transform — and that is NOT what §3.4 does</h3>
/// <code>
/// C_wire = P⁻¹          (Maxwell capacitance, N × N)
/// C_arr  = Aᵀ P⁻¹ A     (M × M)
/// </code>
/// <para>Wires in one array share both nodes, so they share a <i>voltage</i> and their <b>charges
/// add</b>: <c>Q_arr = AᵀQ = AᵀC_wire A u</c>. Sharing a voltage makes charges add and currents
/// divide, which is why the inductive reduction has to invert its congruence
/// (<c>L_arr = (AᵀL⁻¹A)⁻¹</c>, <see cref="ArrayReduction"/>) and this one does not.</para>
///
/// <para><b><c>P⁻¹</c> is never formed.</b> <c>Aᵀ P⁻¹ A</c> needs M triangular solves, one per array;
/// forming the full inverse would need N. At N = 600 that is ~0.4 s of pure waste for a quantity
/// nothing downstream reads. One EXTRA solve — against a right-hand side of ones — yields every
/// wire's row sum of <c>C_wire</c>, which is the only per-wire quantity the end split needs.</para>
///
/// <para><b>P is symmetric positive definite</b>, so <see cref="CholeskyFactor"/> applies directly —
/// unlike the complex-symmetric <c>Z</c>, which is why <see cref="ComplexLu"/> exists.</para>
///
/// <h3>What the numbers mean</h3>
/// <para><see cref="Maxwell"/> is the Maxwell matrix: <c>Q_k = Σ_j C_arr[k,j] V_j</c>, with negative
/// off-diagonals. The circuit form is the usual one — a shunt to the reference of
/// <c>Σ_j C_arr[k,j]</c> (<see cref="GroundShunt"/>) and a capacitor of <c>−C_arr[k,j]</c>
/// (<see cref="Mutual"/>) between arrays <i>k</i> and <i>j</i>.</para>
/// </summary>
public sealed class CapacitanceReduction
{
    private readonly double[] _cArr;        // M x M, row-major, farads (Maxwell)
    private readonly double[] _wireGround;  // N, farads: each wire's row sum of C_wire
    private readonly double[] _c1;          // M, farads: shunt at the input node
    private readonly double[] _c2;          // M, farads: shunt at the output node
    private readonly double[] _c12;         // M, farads: across the array, input to output

    private CapacitanceReduction(double[] cArr, double[] wireGround,
                                 double[] c1, double[] c2, double[] c12, int arrayCount)
    {
        _cArr = cArr;
        _wireGround = wireGround;
        _c1 = c1;
        _c2 = c2;
        _c12 = c12;
        ArrayCount = arrayCount;
    }

    public int ArrayCount { get; }

    public int WireCount => _wireGround.Length;

    /// <summary>The array-basis Maxwell capacitance in farads. Symmetric; off-diagonals are negative.</summary>
    public double Maxwell(int i, int j) => _cArr[i * ArrayCount + j];

    /// <summary>Array <i>k</i>'s total capacitance to the reference conductor: <c>Σ_j C_arr[k,j]</c>.</summary>
    public double GroundShunt(int k)
    {
        double sum = 0.0;
        for (int j = 0; j < ArrayCount; j++) sum += _cArr[k * ArrayCount + j];
        return sum;
    }

    /// <summary>The lumped capacitor between two arrays, <c>−C_arr[k,j]</c>. Non-negative. Zero for k = j.</summary>
    public double Mutual(int k, int j) => k == j ? 0.0 : -_cArr[k * ArrayCount + j];

    /// <summary>
    /// C1 — the input-end diagonal of the array's own two-port capacitance matrix (the (18) end split).
    /// </summary>
    public double InputSelfCapacitance(int k) => _c1[k];

    /// <summary>C2 — the output-end diagonal of the same matrix.</summary>
    public double OutputSelfCapacitance(int k) => _c2[k];

    /// <summary>
    /// C12 — the OFF-DIAGONAL of the array's own two-port capacitance matrix, and <b>positive</b>.
    ///
    /// <para>Positive because the two ends are the same conductor: raising the far end's potential
    /// raises the potential everywhere along the wire and so raises the near end's charge. It is a
    /// Maxwell coefficient, not a circuit element — the circuit element it produces is
    /// <see cref="EndBridge"/>, which is its negative.</para>
    /// </summary>
    public double EndToEndCapacitance(int k) => _c12[k];

    /// <summary>The capacitor from the array's INPUT node to the reference: <c>C1 + C12</c>.</summary>
    public double InputShunt(int k) => _c1[k] + _c12[k];

    /// <summary>The capacitor from the array's OUTPUT node to the reference: <c>C2 + C12</c>.</summary>
    public double OutputShunt(int k) => _c2[k] + _c12[k];

    /// <summary>
    /// The capacitor across the array, input node to output node: <c>−C12</c>, and so <b>negative</b>.
    ///
    /// <para><b>That is not a defect and it is not a sign error.</b> The three numbers together are
    /// the nodal form of the two-port Maxwell matrix <c>[[C1, C12], [C12, C2]]</c>, which is a Gram
    /// matrix of <c>√C·(1−w)</c> and <c>√C·w</c> and therefore positive semi-definite — so the
    /// network is passive however its individual elements are signed. Realising a PSD capacitance
    /// matrix whose off-diagonal is positive REQUIRES a negative bridge; the alternative, dropping it,
    /// would violate charge conservation by exactly <c>2·C12</c>.</para>
    ///
    /// <para>Checked against the distributed limit: with the far end shorted the bridge lands in
    /// parallel with the input shunt and the total becomes <c>C1</c> alone, which for a uniform line
    /// is <c>C_total/3</c> — exactly the first term of the shorted-stub expansion
    /// <c>Z_in = jZ₀tan(βl) ⇒ L(1 + (βl)²/3)</c>. A 50/50 split would give <c>C/2</c> and be wrong by
    /// 50 % in that limit.</para>
    /// </summary>
    public double EndBridge(int k) => -_c12[k];

    /// <summary>
    /// One wire's own capacitance to the reference — its row sum of <c>C_wire</c>, i.e. the charge it
    /// carries when every conductor is held at one volt.
    ///
    /// <para>This is the quantity the shielding argument is about: it is materially SMALLER than the
    /// isolated wire's <c>1/P_ii</c>, because <c>C_wire[i,j] &lt; 0</c> for <c>i ≠ j</c>. Summed over
    /// an array's wires it equals that array's <see cref="GroundShunt"/>, by construction.</para>
    /// </summary>
    public double WireGroundCapacitance(int wire) => _wireGround[wire];

    // ---------------------------------------------------------------- construction

    /// <summary>
    /// Builds the reduction for a design, or returns <c>null</c> when there is nothing to be
    /// capacitive <b>to</b>.
    ///
    /// <para><b>Null when the ground plane is disabled</b>, and that is not a shortcut: the plane at
    /// z = 0 IS the reference conductor, so with it off there is no defined shunt for the charge to
    /// return to. <c>WBondModel.RefuseIfReturnPathUndeclared</c> already refuses that configuration
    /// for the inductance; the editor's panel, which is allowed to show it, falls back to the partial
    /// inductance exactly as it did before capacitance existed.</para>
    /// </summary>
    public static CapacitanceReduction? Create(WBondDesign design, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(design);
        var mesh = WireMesh.Build(design);
        return Create(mesh, parallel);
    }

    /// <inheritdoc cref="Create(WBondDesign, bool)"/>
    public static CapacitanceReduction? Create(WireMesh mesh, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.HasImages) return null;

        return Compute(mesh, PotentialCoefficients.Fill(mesh, parallel));
    }

    /// <summary>Reduces an already-filled <b>P</b>. Split out so gate C3 can sweep the kernel threshold.</summary>
    public static CapacitanceReduction Compute(WireMesh mesh, PotentialCoefficients p)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(p);

        int n = mesh.WireCount;
        int m = mesh.ArrayCount;
        var map = mesh.ArrayOfWire;

        if (n == 0)
            return new CapacitanceReduction(new double[m * m], [], new double[m], new double[m], new double[m], m);

        var factor = CholeskyFactor.Factor(p.Values, n);

        // X = P^-1 A, one triangular solve per array. A's columns are 0/1 indicators, so the
        // right-hand sides are built by marking membership rather than by a matrix product.
        var cArr = new double[m * m];
        var column = new double[n];
        var x = new double[n * m];

        for (int a = 0; a < m; a++)
        {
            Array.Clear(column);
            for (int i = 0; i < n; i++)
                if (map[i] == a) column[i] = 1.0;

            factor.SolveInPlace(column);
            for (int i = 0; i < n; i++) x[i * m + a] = column[i];
        }

        // C_arr = A^T X — a scatter-add over wires, not a GEMM, because A is 0/1. NO final inverse:
        // see the class note.
        for (int i = 0; i < n; i++)
        {
            int row = map[i] * m;
            int xr = i * m;
            for (int a = 0; a < m; a++) cArr[row + a] += x[xr + a];
        }

        // Symmetrise, so reciprocity is structural in the returned object rather than true to rounding.
        for (int i = 0; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                double mean = 0.5 * (cArr[i * m + j] + cArr[j * m + i]);
                cArr[i * m + j] = mean;
                cArr[j * m + i] = mean;
            }
        }

        // ONE more solve, against a right-hand side of ones, gives every wire's row sum of C_wire —
        // the per-wire shunt total the end split scales to. Forming C_wire itself would cost N solves.
        var wireGround = new double[n];
        for (int i = 0; i < n; i++) wireGround[i] = 1.0;
        factor.SolveInPlace(wireGround);
        for (int i = 0; i < n; i++) if (wireGround[i] < 0.0) wireGround[i] = 0.0;

        var (c1, c2, c12) = EndSplit(mesh, wireGround, m);

        return new CapacitanceReduction(cArr, wireGround, c1, c2, c12, m);
    }

    // ---------------------------------------------------------------- Nazarian's (18) end split

    /// <summary>
    /// Splits each wire's shunt capacitance between its two ends, <b>weighted by each segment's own
    /// self-inductance</b> — Nazarian eq. (18), not 50/50 and not by length.
    ///
    /// <para>With <c>w_i</c> the running self-inductance to segment <i>i</i>'s midpoint divided by the
    /// wire's own total:</para>
    /// <code>
    /// C1 = Σ C_i (1−w_i)²      C2 = Σ C_i w_i²      C12 = Σ C_i w_i(1−w_i)
    /// </code>
    /// <para>which conserves charge exactly — <c>C1 + C2 + 2·C12 = Σ C_i</c>, since
    /// <c>(1−w)² + w² + 2w(1−w) = 1</c>. Gate C5 pins the identity.</para>
    ///
    /// <para><b>Why these three are the two-port MATRIX and not three circuit elements.</b> The
    /// inductive voltage drop makes the potential along the wire the interpolation
    /// <c>V(w) = V₁(1−w) + V₂w</c>, and lumping each segment's charge onto the two ends with the same
    /// weights is a Galerkin projection — which produces exactly the matrix
    /// <c>[[C1, C12], [C12, C2]]</c> above. It is a Gram matrix, hence positive semi-definite, hence
    /// passive. Its nodal form is <see cref="InputShunt"/>, <see cref="OutputShunt"/> and the negative
    /// <see cref="EndBridge"/>; reading C1 and C2 as bare shunts and C12 as a bare series capacitor
    /// instead would make the charge-conservation identity above false, which is how the two readings
    /// are told apart.</para>
    ///
    /// <para><b>The form above is a reconstruction that satisfies the paper's stated conservation
    /// property, not a transcription of the printed equation</b> (brief-wbond-capacitance §2.3 says so
    /// and asks for it to be checked against the paper). It does <b>not</b> reproduce the paper's
    /// reported <c>C12</c> two orders of magnitude below <c>C1</c>/<c>C2</c>, and no geometry will make
    /// it: for a uniform distribution <c>∫w(1−w) = 1/6</c> against <c>∫(1−w)² = 1/3</c>, so this
    /// <c>C12</c> is structurally <b>half</b> of <c>C1</c>. The two claims the brief makes about its
    /// own reconstruction are not simultaneously satisfiable. What IS verified is the limit that
    /// matters: see <see cref="EndBridge"/>, whose shorted-far-end total reproduces the distributed
    /// line's <c>C/3</c> exactly.</para>
    ///
    /// <para>The division of labour is not in doubt either: the per-segment <c>C_i</c> set only the
    /// SHAPE of the distribution, because they are rescaled so each wire's total matches its row sum
    /// of <c>C_wire</c> — so the multi-conductor solve sets the size, with all of the shielding in it,
    /// and the local form sets only where along the wire it sits.</para>
    /// </summary>
    private static (double[] C1, double[] C2, double[] C12) EndSplit(
        WireMesh mesh, double[] wireGround, int arrayCount)
    {
        var c1 = new double[arrayCount];
        var c2 = new double[arrayCount];
        var c12 = new double[arrayCount];

        int n = mesh.WireCount;
        for (int w = 0; w < n; w++)
        {
            int start = mesh.WireStart[w];
            int count = mesh.WireLength[w];
            if (count == 0) continue;

            var shape = new double[count];   // the local analytic C_i — SHAPE only
            var weight = new double[count];  // w_i, the inductive position along the wire

            double shapeTotal = 0.0;
            double inductanceTotal = 0.0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var f = ref mesh.Filaments[start + i];
                shape[i] = LocalSegmentCapacitance(in f);
                shapeTotal += shape[i];
                inductanceTotal += Grover.SelfExternal(in f);
            }

            // Degenerate shapes fall back to length weighting rather than dividing by zero; the
            // magnitudes are rescaled below either way, so this only decides the distribution.
            if (shapeTotal <= 0.0)
            {
                shapeTotal = 0.0;
                for (int i = 0; i < count; i++)
                {
                    shape[i] = mesh.Filaments[start + i].Length;
                    shapeTotal += shape[i];
                }
            }

            double running = 0.0;
            for (int i = 0; i < count; i++)
            {
                double self = Grover.SelfExternal(in mesh.Filaments[start + i]);
                // The segment's charge sits at its MIDDLE, so its inductive position is the running
                // total plus half its own — not the running total before or after it.
                weight[i] = inductanceTotal > 0.0 ? (running + 0.5 * self) / inductanceTotal : 0.5;
                running += self;
            }

            // The multi-conductor solve sets the magnitude; the local form set only the shape.
            double scale = shapeTotal > 0.0 ? wireGround[w] / shapeTotal : 0.0;
            int a = mesh.ArrayOfWire[w];

            for (int i = 0; i < count; i++)
            {
                double ci = shape[i] * scale;
                double wi = weight[i];
                c1[a] += ci * (1.0 - wi) * (1.0 - wi);
                c2[a] += ci * wi * wi;
                c12[a] += ci * wi * (1.0 - wi);
            }
        }

        return (c1, c2, c12);
    }

    /// <summary>
    /// The local analytic capacitance of one filament over the plane — Nazarian (15)/(16),
    /// <c>2πε·l / acosh(h/a)</c>, integrated along a segment whose height varies.
    ///
    /// <para>A bond wire's segments are tilted, so <i>h</i> is not one number over a segment; the
    /// integral is taken with the same 4-point Gauss rule the near kernel uses. <b>Used for the SHAPE
    /// of the end split only</b> — the magnitude comes from the multi-conductor solve — so the
    /// height clamp below cannot bias a reported capacitance, only the position of the split.</para>
    /// </summary>
    private static double LocalSegmentCapacitance(in Filament f)
    {
        // acosh(1) = 0, so a segment lying ON the plane would divide by zero. Clamped rather than
        // refused: this quantity decides only where along the wire the charge sits, and a wire in the
        // plane is already refused by the inductance path (its image cancels it exactly).
        const double MinHeightRatio = 1.05;

        double[] nodes = [-0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526];
        double[] weights = [0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538];

        double a = f.Radius;
        if (a <= 0.0) return 0.0;

        double half = 0.5 * f.Length;
        double zMid = f.Az + half * f.Uz;

        double acc = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            double z = Math.Abs(zMid + half * nodes[i] * f.Uz);
            double ratio = z / a;
            if (ratio < MinHeightRatio) ratio = MinHeightRatio;

            acc += weights[i] / Math.Acosh(ratio);
        }

        return 2.0 * Math.PI * PotentialCoefficients.Epsilon0 * half * acc;
    }

    // ---------------------------------------------------------------- what the panel reports

    /// <summary>
    /// The M × M shunt matrix seen at the array INPUT nodes with every far end shorted to the
    /// reference — the network the panel's effective inductance is read from.
    ///
    /// <para>The output-side capacitors are shorted out by that termination, and the end bridge lands
    /// in parallel with the input shunt, so what survives is each array's own <c>C1</c> plus its
    /// half-share of every inter-array capacitor:</para>
    /// <code>
    /// C′[k,k] = C1_k + Σ_{j≠k} C_kj/2      C′[k,j] = −C_kj/2
    /// </code>
    /// </summary>
    public double[] TerminalShuntMatrix()
    {
        int m = ArrayCount;
        var c = new double[m * m];

        for (int k = 0; k < m; k++)
        {
            // The far-end short puts EndBridge in parallel with InputShunt, and the two C12 terms
            // cancel: what is left is C1 alone. See EndBridge for the distributed-limit check.
            double diagonal = _c1[k];
            for (int j = 0; j < m; j++)
            {
                if (j == k) continue;
                double half = 0.5 * Mutual(k, j);
                diagonal += half;
                c[k * m + j] = -half;
            }
            c[k * m + k] = diagonal;
        }

        return c;
    }

    /// <summary>
    /// <b>The effective inductance at a stated frequency</b> (wbond.md §6.8): the imaginary part of
    /// each array's input impedance, divided by ω, with its far end shorted to the reference plane.
    ///
    /// <code>
    /// L_eff,k(f) = Im( Z_in,k(f) ) / ω,    Y = (jωL_arr)⁻¹ + jωC′,   Z_in = Y⁻¹
    /// </code>
    /// <para>Both terms are purely imaginary, so with <c>B = ωC′ − Γ_arr/ω</c> (real, symmetric, and
    /// using the inverse inductance <see cref="ArrayReduction.Gamma"/> the reduction already carries)
    /// this is simply <c>L_eff,k = −(B⁻¹)[k,k]/ω</c>. For one array it reduces to the familiar
    /// shorted-stub result <c>L/(1 − ω²LC)</c>.</para>
    ///
    /// <para><b>The alternative was considered and rejected, and is named so nobody re-derives it.</b>
    /// The other obvious candidate is the two-port series arm <c>Im(−1/Y₂₁)/ω</c> — but for a π
    /// network that is <i>identically</i> <c>L_arr</c> at every frequency, because the shunt
    /// capacitors do not appear in <c>Y₂₁</c> at all. It would produce a frequency box whose value
    /// never changes anything, which is worse than having none.</para>
    ///
    /// <para><b>With no capacitance the answer is <c>L_arr</c> at every frequency</b>, exactly: the
    /// shunt matrix is zero, <c>B = −Γ/ω</c>, and <c>−B⁻¹/ω = L_arr</c>. That is gate C6, and it is
    /// what makes the frequency box inert whenever capacitance is off.</para>
    /// </summary>
    /// <param name="shunt">
    /// <see cref="TerminalShuntMatrix"/>, or an all-zero M × M matrix for the capacitance-off case.
    /// </param>
    public static double[] EffectiveInductance(ArrayReduction inductance, double[] shunt, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(inductance);
        ArgumentNullException.ThrowIfNull(shunt);

        int m = inductance.ArrayCount;
        var result = new double[m];
        if (m == 0) return result;

        // At DC the shorted stub IS the inductor: Im(Z)/ω -> L_arr, and the expression below is 0/0.
        double omega = 2.0 * Math.PI * frequencyHz;
        if (omega <= 0.0)
        {
            for (int k = 0; k < m; k++) result[k] = inductance[k, k];
            return result;
        }

        var b = new double[m * m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                b[i * m + j] = omega * shunt[i * m + j] - inductance.Gamma(i, j) / omega;

        var inverse = Invert(b, m);
        for (int k = 0; k < m; k++) result[k] = -inverse[k * m + k] / omega;

        return result;
    }

    /// <summary>
    /// The lowest self-resonance of the shorted-far-end network, in hertz, or
    /// <see cref="double.PositiveInfinity"/> when there is no capacitance to resonate with.
    ///
    /// <para>Resonance is where <c>B = ωC′ − L_arr⁻¹/ω</c> becomes singular, i.e.
    /// <c>det(ω²C′ − L_arr⁻¹) = 0</c> — so <c>ω²_min = 1/λ_max(L_arr·C′)</c>, found by power
    /// iteration. Both matrices are positive (semi-)definite, so the product's eigenvalues are real
    /// and positive and the iteration is well behaved. For one array it is the textbook
    /// <c>1/(2π√(LC))</c>.</para>
    /// </summary>
    public static double SelfResonanceHz(ArrayReduction inductance, double[] shunt)
    {
        ArgumentNullException.ThrowIfNull(inductance);
        ArgumentNullException.ThrowIfNull(shunt);

        int m = inductance.ArrayCount;
        if (m == 0) return double.PositiveInfinity;

        var x = new double[m];
        var y = new double[m];
        var t = new double[m];
        for (int i = 0; i < m; i++) x[i] = 1.0;

        double lambda = 0.0;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            for (int i = 0; i < m; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < m; j++) sum += shunt[i * m + j] * x[j];
                t[i] = sum;
            }
            for (int i = 0; i < m; i++)
            {
                double sum = 0.0;
                for (int j = 0; j < m; j++) sum += inductance[i, j] * t[j];
                y[i] = sum;
            }

            double norm = 0.0;
            for (int i = 0; i < m; i++) norm += y[i] * y[i];
            norm = Math.Sqrt(norm);
            if (norm <= 0.0) return double.PositiveInfinity;

            double next = 0.0;
            for (int i = 0; i < m; i++) next += x[i] * y[i];

            for (int i = 0; i < m; i++) x[i] = y[i] / norm;

            if (iteration > 3 && Math.Abs(next - lambda) <= 1e-12 * Math.Abs(next)) { lambda = next; break; }
            lambda = next;
        }

        if (lambda <= 0.0) return double.PositiveInfinity;
        return 1.0 / (2.0 * Math.PI * Math.Sqrt(lambda));
    }

    /// <summary>
    /// Inverts a small real matrix by Gauss-Jordan with partial pivoting.
    ///
    /// <para><b>Not <see cref="CholeskyFactor"/></b>: <c>B = ωC′ − Γ/ω</c> is symmetric but
    /// <b>indefinite</b> — that is exactly what makes it describe a resonance — so a Cholesky would
    /// fail on every well-formed input.</para>
    /// </summary>
    private static double[] Invert(double[] a, int m)
    {
        var work = (double[])a.Clone();
        var inverse = new double[m * m];
        for (int i = 0; i < m; i++) inverse[i * m + i] = 1.0;

        for (int col = 0; col < m; col++)
        {
            int pivot = col;
            double best = Math.Abs(work[col * m + col]);
            for (int r = col + 1; r < m; r++)
            {
                double candidate = Math.Abs(work[r * m + col]);
                if (candidate > best) { best = candidate; pivot = r; }
            }

            if (best == 0.0)
                throw new InvalidOperationException(
                    "The wBond terminal network is exactly at self-resonance, so its input impedance " +
                    "is unbounded and no effective inductance exists at this frequency.");

            if (pivot != col)
            {
                for (int c = 0; c < m; c++)
                {
                    (work[col * m + c], work[pivot * m + c]) = (work[pivot * m + c], work[col * m + c]);
                    (inverse[col * m + c], inverse[pivot * m + c]) = (inverse[pivot * m + c], inverse[col * m + c]);
                }
            }

            double inv = 1.0 / work[col * m + col];
            for (int c = 0; c < m; c++) { work[col * m + c] *= inv; inverse[col * m + c] *= inv; }

            for (int r = 0; r < m; r++)
            {
                if (r == col) continue;
                double f = work[r * m + col];
                if (f == 0.0) continue;
                for (int c = 0; c < m; c++)
                {
                    work[r * m + c] -= f * work[col * m + c];
                    inverse[r * m + c] -= f * inverse[col * m + c];
                }
            }
        }

        return inverse;
    }
}
