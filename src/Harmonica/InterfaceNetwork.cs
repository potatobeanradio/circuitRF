using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Harmonica;

/// <summary>
/// The pre-terminated interface network (harmonicarf.md §6.2, R-hrf-6) — the optimisation that makes
/// a marker move free.
///
/// <para><b>What it holds.</b> The linear partition, extracted ONCE per harmonic as an
/// (N_nl + N_term)-port with the termination ports left OPEN, together with the Norton excitation the
/// circuit's own bias supplies produce at those ports. Both are invalidated only by a STRUCTURAL
/// change; a termination move re-uses them untouched.</para>
///
/// <para><b>How a termination is closed — and why in the IMPEDANCE domain.</b> §6.2 writes the
/// closure as a Schur complement of the admittance matrix,
/// <c>Y_NN = Y_aa − Y_ab (Y_bb + Y_t)⁻¹ Y_ba</c>. That is algebraically right and NUMERICALLY WRONG
/// here, and the measurement is what says so. An OPEN port's driving-point impedance runs to the
/// ideal bias choke's ~10 GΩ while a terminated one sits at tens of ohms, so the open-port <c>Z</c>
/// spans eight or nine decades — and <c>Y = Z⁻¹</c>, which the extractor forms before anything else
/// happens, spends them. Closing after that inversion reproduced direct extraction to only
/// <b>1e-4 … 1e-7</b>, against R-hrf-6's 1e-12. Closing in the impedance domain instead:</para>
/// <code>
///   M      = I + Z·Y_t                          Y_t = diag(termination admittance)
///   Z_c    = [M⁻¹ Z]_PP                          the TERMINATED device-facing block
///   Y_NN   = Z_c⁻¹                               one well-conditioned inverse
///   w      = [M⁻¹ Z]_P,A · J + [M⁻¹ V_oc]_P      J = the Norton drive
///   I_src  = −Y_NN · w
/// </code>
/// <para>reproduces it to <b>1e-13</b>. The inverse that remains is of the terminated block — exactly
/// the matrix the shipped path inverts, and conditioned exactly as well. This is a deviation from the
/// design note's stated formula and it is reported as one; the physics is identical.</para>
///
/// <para><b>The overlap case is not a special case.</b> §6.2 writes the partition as though the
/// termination ports were disjoint from the device-facing ones. They are not, when the model states
/// no embedding at all: the source marker then attaches directly to the device's gate terminal. The
/// form above covers both, because <c>Y_t</c> lands wherever its node sits and the P/Q split is over
/// the augmented set rather than over the terminations.</para>
///
/// <para><b>The DC block is part of the TERMINATION, not of the netlist.</b> Ideal bias (§4.4) says
/// the RF terminations never see DC. Modelling the blocking capacitor as an explicit component would
/// leave the termination plane floating at ω = 0 — an all-but-singular row in exactly the extraction
/// the whole scheme rests on. Folding it into <c>Y_t</c> instead makes band 0 an EXACT open
/// (<c>Y_t(0) = 0</c>, not a large impedance) and leaves the plane an ordinary, well-connected node
/// at every harmonic.</para>
/// </summary>
public sealed class InterfaceNetwork
{
    private readonly Complex[][,] _zOpen;   // [k][a, a] open-port impedance over the augmented set
    private readonly Complex[][]  _vOc;     // [k][a]    open-circuit voltages from the bias supplies
    private readonly int[]        _augmented;    // circuit node numbers, ascending
    private readonly int[]        _devicePos;    // indices into _augmented that are device-facing
    private readonly int[]        _termPos;      // indices into _augmented that are termination-only
    private readonly int[]        _termNodePos;  // [side] → index into _augmented, or −1

    private InterfaceNetwork(Complex[][,] zOpen, Complex[][] vOc, int[] augmented,
                             int[] devicePos, int[] termPos, int[] termNodePos,
                             int[] deviceNodes, int harmonicCount, double f0)
    {
        _zOpen        = zOpen;
        _vOc          = vOc;
        _augmented    = augmented;
        _devicePos    = devicePos;
        _termPos      = termPos;
        _termNodePos  = termNodePos;
        DeviceNodes   = deviceNodes;
        HarmonicCount = harmonicCount;
        FrequencyHz   = f0;
    }

    /// <summary>The HB unknown nodes, ascending — the row order of every <c>Y_NN</c> produced here.</summary>
    public int[] DeviceNodes   { get; }
    public int   HarmonicCount { get; }
    public double FrequencyHz  { get; }
    public int   InterfaceCount => DeviceNodes.Length;

    /// <summary>The augmented interface, for diagnostics and for the tests that inspect the split.</summary>
    public IReadOnlyList<int> AugmentedNodes => _augmented;

    /// <summary>How many ports the Schur step eliminates — 2 with an embedding, 0 without.</summary>
    public int EliminatedPortCount => _termPos.Length;

    /// <summary>
    /// Extracts the open-port network once. This is the expensive half and the only half a structural
    /// change invalidates.
    /// </summary>
    public static InterfaceNetwork Extract(
        ElaboratedNetlist netlist, AnalysisSettings settings,
        int sourcePlaneNode, int loadPlaneNode, int harmonicCount, double f0)
    {
        var extractor = new HbLinearExtractor(netlist, settings,
                                              [sourcePlaneNode, loadPlaneNode]);

        int[] augmented = extractor.InterfaceNodes;
        var zOpen = new Complex[harmonicCount + 1][,];
        var vOc   = new Complex[harmonicCount + 1][];

        for (int k = 0; k <= harmonicCount; k++)
            (zOpen[k], vOc[k]) = extractor.ExtractImpedance(k * 2.0 * Math.PI * f0);

        var deviceNodes = netlist.NonlinearNodes.Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
        var deviceSet   = deviceNodes.ToHashSet();

        var devicePos = new List<int>();
        var termPos   = new List<int>();
        for (int i = 0; i < augmented.Length; i++)
            (deviceSet.Contains(augmented[i]) ? devicePos : termPos).Add(i);

        int[] termNodePos =
        [
            Array.IndexOf(augmented, sourcePlaneNode),
            Array.IndexOf(augmented, loadPlaneNode),
        ];

        return new InterfaceNetwork(zOpen, vOc, augmented, [.. devicePos], [.. termPos],
                                    termNodePos, deviceNodes, harmonicCount, f0);
    }

    /// <summary>
    /// The termination admittance a plane presents at harmonic <paramref name="k"/>, INCLUDING the
    /// ideal DC block. Exactly zero at k = 0 — band 0 is an open, which is what ideal bias means.
    /// </summary>
    public static Complex TerminationAdmittance(Complex z, int k, double f0, double blockFarads)
    {
        if (k == 0) return Complex.Zero;
        double omega = k * 2.0 * Math.PI * f0;
        return Complex.One / (z + Complex.One / new Complex(0, omega * blockFarads));
    }

    /// <summary>
    /// Closes both termination ports and returns what the HB Newton loop consumes: the interface
    /// admittance seen by the devices, and the Norton excitation at that interface.
    ///
    /// <para><paramref name="sourceDriveVolts"/> is the Thévenin open-circuit amplitude of the RF
    /// drive behind the FUNDAMENTAL source termination — <c>|Vs| = √(8·P_avl·Re Z_S(1))</c>, the same
    /// rule <c>TunerModel.SetSourceDrive</c> applies, so a harmonicaRF operating point and a
    /// loadpull one at the same available power are the same drive.</para>
    /// </summary>
    public (Complex[][,] YNN, Complex[][] ISrc) Close(
        TerminationSet terminations, double sourceDriveVolts, double blockFarads = HarmonicaNetlist.IdealBlockF)
    {
        int a  = _augmented.Length;
        int nP = _devicePos.Length;

        var yNN  = new Complex[HarmonicCount + 1][,];
        var iSrc = new Complex[HarmonicCount + 1][];

        for (int k = 0; k <= HarmonicCount; k++)
        {
            var z = _zOpen[k];
            var (m, _, j) = BuildClosure(terminations, sourceDriveVolts, blockFarads, k);

            // One solve for [Z | V_oc] together — the terminated impedance block and the terminated
            // open-circuit response come out of the same factorisation.
            var rhs = new Complex[a, a + 1];
            for (int r = 0; r < a; r++)
            {
                for (int c = 0; c < a; c++) rhs[r, c] = z[r, c];
                rhs[r, a] = _vOc[k][r];
            }
            var solved = SolveDense(m, rhs);   // [M⁻¹Z | M⁻¹V_oc]

            // Z_c = the terminated device-facing block; Y_NN is its inverse — the one inversion
            // left, and it is of a TERMINATED network, so it is conditioned like the shipped path's.
            var zc = new Complex[nP, nP];
            for (int r = 0; r < nP; r++)
                for (int c = 0; c < nP; c++)
                    zc[r, c] = solved[_devicePos[r], _devicePos[c]];

            var eye = new Complex[nP, nP];
            for (int r = 0; r < nP; r++) eye[r, r] = Complex.One;
            var y = SolveDense(zc, eye);

            // w = [M⁻¹Z]_P,A · J + [M⁻¹V_oc]_P  — the voltage the device-facing ports would sit at
            // with the device absent, terminations and drive attached.
            var w = new Complex[nP];
            for (int r = 0; r < nP; r++)
            {
                Complex s = solved[_devicePos[r], a];
                for (int c = 0; c < a; c++)
                    if (j[c] != Complex.Zero) s += solved[_devicePos[r], c] * j[c];
                w[r] = s;
            }

            // F = Y_NN·V + I_src + I_nl, so I_src = −Y_NN·w (the extractor's own convention).
            var src = new Complex[nP];
            for (int r = 0; r < nP; r++)
            {
                Complex s = Complex.Zero;
                for (int c = 0; c < nP; c++) s += y[r, c] * w[c];
                src[r] = -s;
            }

            yNN[k]  = y;
            iSrc[k] = src;
        }

        return (yNN, iSrc);
    }

    /// <summary>
    /// What each termination plane is doing at the converged operating point: its own voltage, and
    /// the TRUE current it delivers into the rest of the circuit.
    ///
    /// <para><b>R-hrf-4, and it is the one thing in this brief the codebase has already got wrong
    /// once.</b> Loadpull's <c>Zin</c> divided by the SDD's INTRINSIC gate current and reported
    /// 5000 Ω where the true source-seen value was 192 Ω, the moment a user wired any passive at the
    /// gate. The fix there was to measure the delivered current instead. Here that current is
    /// available in closed form and needs no back-solve: the termination is a Norton pair
    /// <c>(J, Y_t)</c>, so what it pushes into the node is exactly <c>J − Y_t·V_plane</c> — which by
    /// KCL is whatever the embedding, the bias choke and the device between them take, not the
    /// device's own terminal current.</para>
    ///
    /// <para>The plane voltage itself is recovered from the converged device-facing solution through
    /// the same factorisation the closure used, so this costs one small product per harmonic.</para>
    /// </summary>
    /// <param name="iNlTotal">
    /// The TOTAL converged nonlinear injection at the device-facing ports, <c>i + jωq + Σ H[w]·W</c>
    /// — <see cref="OperatingPoint.INlTotal"/>, NOT the conduction half. KCL at the plane balances
    /// everything the device draws, and passing the conduction current alone gives a plane voltage
    /// that is wrong by whatever charge the device stores.
    /// </param>
    public (Complex[,] PlaneVolts, Complex[,] DeliveredCurrent) PlaneState(
        TerminationSet terminations, double sourceDriveVolts, Complex[,] iNlTotal,
        double blockFarads = HarmonicaNetlist.IdealBlockF)
    {
        int a  = _augmented.Length;
        int nP = _devicePos.Length;

        var volts   = new Complex[2, HarmonicCount + 1];
        var current = new Complex[2, HarmonicCount + 1];

        for (int k = 0; k <= HarmonicCount; k++)
        {
            var (m, yt, j) = BuildClosure(terminations, sourceDriveVolts, blockFarads, k);

            var rhs = new Complex[a, a + 1];
            for (int r = 0; r < a; r++)
            {
                for (int c = 0; c < a; c++) rhs[r, c] = _zOpen[k][r, c];
                rhs[r, a] = _vOc[k][r];
            }
            var solved = SolveDense(m, rhs);

            for (int side = 0; side < 2; side++)
            {
                int pos = _termNodePos[side];
                if (pos < 0) continue;

                // V_A = [M⁻¹Z]_{A,P}·(−I_nl) + [M⁻¹Z]_{A,·}·J + [M⁻¹V_oc]_A
                Complex v = solved[pos, a];
                for (int c = 0; c < a; c++)
                    if (j[c] != Complex.Zero) v += solved[pos, c] * j[c];
                for (int p = 0; p < nP; p++)
                    v -= solved[pos, _devicePos[p]] * iNlTotal[p, k];

                volts[side, k]   = v;
                current[side, k] = j[pos] - yt[pos] * v;
            }
        }

        return (volts, current);
    }

    /// <summary>
    /// <c>M = I + Z·Y_t</c> together with the termination admittance and Norton drive vectors — the
    /// three quantities every closure step needs, built once so <see cref="Close"/> and
    /// <see cref="PlaneState"/> cannot drift apart on how a termination is applied.
    /// </summary>
    private (Complex[,] M, Complex[] Yt, Complex[] J) BuildClosure(
        TerminationSet terminations, double sourceDriveVolts, double blockFarads, int k)
    {
        int a = _augmented.Length;
        var yt = new Complex[a];
        var j  = new Complex[a];

        for (int side = 0; side < 2; side++)
        {
            int pos = _termNodePos[side];
            if (pos < 0) continue;

            Complex zTerm = terminations.Z((TerminationSide)side, Math.Max(k, 1));
            yt[pos] = TerminationAdmittance(zTerm, k, FrequencyHz, blockFarads);

            if (side == (int)TerminationSide.Source && k == 1 && sourceDriveVolts != 0)
                j[pos] = sourceDriveVolts * yt[pos];
        }

        var m = new Complex[a, a];
        for (int r = 0; r < a; r++)
        {
            m[r, r] = Complex.One;
            for (int c = 0; c < a; c++) m[r, c] += _zOpen[k][r, c] * yt[c];
        }
        return (m, yt, j);
    }

    // ── small dense helpers ───────────────────────────────────────────────────

    private static Complex[,] Select(Complex[,] m, int[] rows, int[] cols)
    {
        var r = new Complex[rows.Length, cols.Length];
        for (int i = 0; i < rows.Length; i++)
            for (int j = 0; j < cols.Length; j++)
                r[i, j] = m[rows[i], cols[j]];
        return r;
    }

    private static Complex[] Select(Complex[] v, int[] rows)
    {
        var r = new Complex[rows.Length];
        for (int i = 0; i < rows.Length; i++) r[i] = v[rows[i]];
        return r;
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting on an n×n complex system with several right-hand
    /// sides. n is the number of termination ports — 2 in every shipping topology — so this is a
    /// handful of flops and does not want a library.
    /// </summary>
    internal static Complex[,] SolveDense(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1);
        var A = (Complex[,])a.Clone();
        var B = (Complex[,])b.Clone();

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (A[r, col].Magnitude > A[piv, col].Magnitude) piv = r;

            if (A[piv, col].Magnitude == 0)
                throw new InvalidOperationException(
                    "the termination-port block is singular — a termination plane is connected to " +
                    "nothing at this harmonic, which the open-port extraction cannot represent.");

            if (piv != col)
                for (int j = 0; j < n; j++) (A[col, j], A[piv, j]) = (A[piv, j], A[col, j]);
            if (piv != col)
                for (int j = 0; j < m; j++) (B[col, j], B[piv, j]) = (B[piv, j], B[col, j]);

            Complex d = A[col, col];
            for (int j = 0; j < n; j++) A[col, j] /= d;
            for (int j = 0; j < m; j++) B[col, j] /= d;

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                Complex f = A[r, col];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < n; j++) A[r, j] -= f * A[col, j];
                for (int j = 0; j < m; j++) B[r, j] -= f * B[col, j];
            }
        }
        return B;
    }
}
