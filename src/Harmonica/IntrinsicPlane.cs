using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Harmonica;

/// <summary>
/// What the current generator is doing (harmonicarf.md §4.5) — the quantity harmonicaRF exists to
/// show, and the part of this brief most able to be plausibly wrong.
///
/// <para><b>Intrinsic means CONDUCTION CURRENT ONLY (D1).</b> The <c>i</c> half of the
/// <c>(i, q, dg, dc)</c> contract, excluding <c>jωq</c>. Both for the loadline and for the glyphs.
/// The consequence is intended: with charge on, the load glyph separates from its marker even with a
/// bare device, because terminal current is no longer conduction current. Had terminal current been
/// used, KCL would pin the glyph to the marker for ever and it would convey nothing.</para>
///
/// <para><b>The two sides are NOT symmetric.</b> §4.5.1's ratio is legitimate at the drain because
/// the conduction current there is an ideal current source. At the gate there is no generator: the
/// gate is the CONTROLLED node, and a voltage-over-current ratio at a driven node returns the LOAD,
/// never the source — <c>V = Vs·Z_L/(Z_S+Z_L)</c>, <c>I = Vs/(Z_S+Z_L)</c>, so <c>V/I = Z_L</c>. With
/// conduction-only current it is worse still: a FET's gate conduction current is essentially zero
/// below turn-on, so the ratio is numerically meaningless as well as conceptually wrong.
/// <b>This codebase has already shipped that error once</b> — loadpull's <c>Zin</c> divided by the
/// SDD's intrinsic gate current and reported 5000 Ω where the true source-seen value was 192 Ω. See
/// <c>src/Engine/Loadpull/CLAUDE.md</c>. The source side takes the <c>J′</c> route below instead, and
/// <see cref="SourceImpedanceIsNotAVoltageCurrentRatio"/> pins the difference.</para>
/// </summary>
public static class IntrinsicPlane
{
    /// <summary>
    /// The DUT's intrinsic port spectra: the CONDUCTION current at each port and the port voltage
    /// (R-hrf-1). The charge contribution is excluded from the current and never from the voltage.
    /// </summary>
    /// <param name="portCurrents">
    /// <c>[port, harmonic]</c> — conduction current into each port, <c>jωq</c> excluded.
    /// </param>
    /// <param name="portVoltages"><c>[port, harmonic]</c> — the port's own terminal voltage.</param>
    /// <param name="portChargeCurrents">
    /// <c>[port, harmonic]</c> — the displacement current <c>jωq</c> that was left out, reported so a
    /// caller can show the split rather than infer it.
    /// </param>
    public readonly record struct DeviceSpectra(
        Complex[,] portCurrents,
        Complex[,] portVoltages,
        Complex[,] portChargeCurrents)
    {
        public int PortCount     => portCurrents.GetLength(0);
        public int HarmonicCount => portCurrents.GetLength(1) - 1;
    }

    /// <summary>
    /// Evaluates the DUT over the converged interface spectrum and separates conduction from
    /// displacement, per port and per harmonic.
    ///
    /// <para>This walks the same IFFT → evaluate → FFT path the Newton loop does, because that is
    /// what defines the quantity: <c>i(t)</c> is the model's own <c>I</c>, and the spectrum is its
    /// transform. Reading the interface's <c>INl</c> instead would give the node total, which already
    /// has <c>jωq</c> in it — the exact thing D1 excludes.</para>
    /// </summary>
    /// <param name="sourcePort">
    /// The port whose voltage every reported port voltage is referenced to, or −1 for ground. It
    /// changes the REPORTED voltages only, never the ones the device is evaluated at: those are the
    /// model's own ports and are what <c>Evaluate</c> is defined in. For a two-port device the ports
    /// are already source-referenced and this is −1; for an external model, whose every port is
    /// node-to-ground, it is what turns V(drain) into Vds (§4.5.5).
    /// </param>
    public static DeviceSpectra Evaluate(
        ElaboratedComponent dut, Complex[,] v, int[] interfaceNodes, int k, int gridN, double f0,
        int sourcePort = -1)
    {
        int n = interfaceNodes.Length;
        int p = dut.Model.PortCount;

        var vTime = new double[n][];
        for (int i = 0; i < n; i++)
        {
            vTime[i] = new double[gridN];
            var spec = new Complex[k + 1];
            for (int h = 0; h <= k; h++) spec[h] = v[i, h];
            HbFft.Inverse(spec, k, vTime[i]);
        }

        var (plus, minus) = PortNodeIndices(dut, interfaceNodes);

        var iTime = new double[p, gridN];
        var qTime = new double[p, gridN];
        var vPortTime = new double[p, gridN];

        var points = new double[gridN][];
        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[p];
            for (int q = 0; q < p; q++)
            {
                double a = plus[q]  >= 0 ? vTime[plus[q]][t]  : 0.0;
                double b = minus[q] >= 0 ? vTime[minus[q]][t] : 0.0;
                pv[q] = a - b;
                vPortTime[q, t] = pv[q];
            }
            points[t] = pv;
        }

        var batch = dut.PrefersBatchEvaluate ? dut.EvaluateBatch(points) : null;
        for (int t = 0; t < gridN; t++)
        {
            var res = batch is not null ? batch[t] : dut.Evaluate(new PortVoltages(points[t]));
            for (int q = 0; q < p; q++)
            {
                iTime[q, t] = res.I[q];
                qTime[q, t] = res.Q[q];
            }
        }

        var iOut = new Complex[p, k + 1];
        var vOut = new Complex[p, k + 1];
        var qOut = new Complex[p, k + 1];

        var buf = new double[gridN];
        for (int q = 0; q < p; q++)
        {
            for (int t = 0; t < gridN; t++) buf[t] = iTime[q, t];
            HbFft.Forward(buf, k, out var iSpec, out _);

            for (int t = 0; t < gridN; t++) buf[t] = vPortTime[q, t];
            HbFft.Forward(buf, k, out var vSpec, out _);

            for (int t = 0; t < gridN; t++) buf[t] = qTime[q, t];
            HbFft.Forward(buf, k, out var qSpec, out _);

            for (int h = 0; h <= k; h++)
            {
                iOut[q, h] = iSpec[h];
                vOut[q, h] = vSpec[h];
                qOut[q, h] = new Complex(0, h * 2.0 * Math.PI * f0) * qSpec[h];
            }
        }

        // Re-reference the reported voltages to the source terminal. Done AFTER the evaluation, not
        // before it, because the device is defined at its own ports and must be evaluated there; this
        // only changes what the port voltage is reported AS. The source port's own voltage becomes
        // exactly zero, which is what "referenced to the source" means.
        if (sourcePort >= 0 && sourcePort < p)
        {
            var refSpec = new Complex[k + 1];
            for (int h = 0; h <= k; h++) refSpec[h] = vOut[sourcePort, h];
            for (int q = 0; q < p; q++)
                for (int h = 0; h <= k; h++)
                    vOut[q, h] -= refSpec[h];
        }

        return new DeviceSpectra(iOut, vOut, qOut);
    }

    /// <summary>
    /// The time-domain loadline: <c>Vds_intr(t)</c> against <c>Ids_intr(t)</c>, conduction only
    /// (§7.3). Same evaluation as <see cref="Evaluate"/>, kept in the time domain.
    /// </summary>
    public static (double[] Vds, double[] Ids) Loadline(
        ElaboratedComponent dut, Complex[,] v, int[] interfaceNodes, int k, int gridN,
        int drainPort = 1, int sourcePort = -1)
    {
        int n = interfaceNodes.Length;
        int p = dut.Model.PortCount;

        var vTime = new double[n][];
        for (int i = 0; i < n; i++)
        {
            vTime[i] = new double[gridN];
            var spec = new Complex[k + 1];
            for (int h = 0; h <= k; h++) spec[h] = v[i, h];
            HbFft.Inverse(spec, k, vTime[i]);
        }

        var (plus, minus) = PortNodeIndices(dut, interfaceNodes);
        var vds = new double[gridN];
        var ids = new double[gridN];

        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[p];
            for (int q = 0; q < p; q++)
            {
                double a = plus[q]  >= 0 ? vTime[plus[q]][t]  : 0.0;
                double b = minus[q] >= 0 ? vTime[minus[q]][t] : 0.0;
                pv[q] = a - b;
            }
            vds[t] = pv[drainPort] - (sourcePort >= 0 && sourcePort < p ? pv[sourcePort] : 0.0);
            ids[t] = dut.Evaluate(new PortVoltages(pv)).I[drainPort];
        }

        return (vds, ids);
    }

    /// <summary>
    /// R-hrf-2 — the LOAD-side intrinsic impedance, <c>Z_L,intr(k) = − V_d,k / I_d,k^cond</c>.
    ///
    /// <para>A ratio is legitimate here and only here: at the intrinsic drain the conduction current
    /// is an ideal current source injecting into the node, so this is literally the impedance the
    /// current generator drives into, in the large-signal describing sense. It is also exactly the
    /// impedance the loadline is drawn against, which is why the glyph and the loadline are two views
    /// of one quantity.</para>
    /// </summary>
    public static Complex[] LoadImpedance(in DeviceSpectra s, int drainPort = 1)
    {
        int k = s.HarmonicCount;
        var z = new Complex[k + 1];
        for (int h = 0; h <= k; h++)
        {
            Complex i = s.portCurrents[drainPort, h];
            z[h] = i == Complex.Zero
                ? new Complex(double.PositiveInfinity, 0)
                : -s.portVoltages[drainPort, h] / i;
        }
        return z;
    }

    /// <summary>
    /// R-hrf-3 / D2 — the SOURCE-side intrinsic impedance, as the Schur complement of a MODIFIED
    /// converged Jacobian. Returns the full harmonic-conversion matrix <c>Zs_conv</c>; its diagonal
    /// is what the source glyph plots.
    ///
    /// <para><b>The construction.</b> The converged HB Jacobian
    /// <c>J = Y_NN + ∂I_nl/∂V + ω·∂Q_nl/∂V</c> is exactly the linearisation of the whole coupled
    /// system about the large-signal operating point, and being the conversion matrix it already
    /// carries the harmonic coupling. Form <c>J′</c> by replacing ONLY the gate-port self block with
    /// its linear part:</para>
    /// <code>
    ///   J′_gg = (Y_NN)_gg     — the device's own ∂I_g/∂V_g removed (excludes Cgs and the gate diode)
    ///   J′_gr, J′_rg, J′_rr   — kept, which is what retains gm and the common-source path
    ///   Z_S,intr = (J′⁻¹)_gg
    /// </code>
    /// <para>Removing the gate-port self block is what addresses §4.5.3(b): Cgs and the gate diode are
    /// the thing being TERMINATED, not part of the source, and letting the test current into them
    /// returns <c>Z_source ∥ Z_in</c>, which is neither quantity. Keeping <c>J_rg</c> is what
    /// addresses §4.5.3(a): the impedance the gate control sees depends on the device's own
    /// transconductance whenever anything — a shared source lead, an external feedback capacitance,
    /// an embedding block's gate–drain coupling — closes a path through the device.</para>
    ///
    /// <para><b>The port is a PORT, not a node.</b> §4.5.3 writes <c>g</c> as though it were one
    /// unknown. It is not when the source terminal is lifted off ground by a source lead — and the
    /// source lead is the very case the whole formulation exists for. So the gate port enters through
    /// its incidence vector (+1 at the gate node, −1 at the source node) and
    /// <c>Z_S = bᵀ J′⁻¹ b</c>, which collapses to <c>(J′⁻¹)_gg</c> exactly when the source is
    /// grounded.</para>
    ///
    /// <para><b>Cost is negligible and it is computed once per DISPLAYED operating point, not per
    /// grid point.</b> <c>J</c> is 24 × 24 for one grounded-source FET at K = 5.</para>
    /// </summary>
    public static Complex[,] SourceImpedance(
        HarmonicaContext ctx, OperatingPoint point, int gatePort = 0)
    {
        var dut   = ctx.DutComponent;
        int k     = ctx.Model.Settings.HarmonicCount;
        int n     = ctx.Interface.InterfaceCount;
        int gridN = HbFft.GridSize(k, ctx.Model.Settings.FftOverSample);
        double f0 = ctx.Model.Settings.FrequencyHz;
        double omega0 = 2.0 * Math.PI * f0;

        var (_, _, g, c, buckets) = HbNewton.EvaluateNonlinear(
            point.V, n, k, gridN, ctx.Netlist, ctx.Interface.DeviceNodes);

        // Remove the DUT's own gate-port SELF block from the node-space conversion spectra. It was
        // accumulated four-way over (g+,g−)×(g+,g−), so it comes out the same way.
        var (plus, minus) = PortNodeIndices(dut, ctx.Interface.DeviceNodes);
        var (gSelf, cSelf) = GatePortSelfSpectra(dut, point.V, ctx.Interface.DeviceNodes, k, gridN,
                                                 gatePort, g.GetLength(2) - 1);

        SubtractPortSelf(g, plus[gatePort], minus[gatePort], gSelf);
        SubtractPortSelf(c, plus[gatePort], minus[gatePort], cSelf);

        double[] jPrime = HbNewton.BuildJ(point.YNN, g, c, n, k, omega0,
                                          ctx.Model.Settings.GuardHarmonic, buckets);

        return PortBlockOfInverse(jPrime, n, k, plus[gatePort], minus[gatePort]);
    }

    /// <summary>
    /// The quantity §4.5.2 says is WRONG for the source side: <c>V_g,k / I_g,k</c> on the intrinsic
    /// gate port. It exists only so a regression test can assert that
    /// <see cref="SourceImpedance"/> is not equal to it on a fixture where they differ — pinning the
    /// correction the way the <c>Iin</c> fix is pinned, so it cannot silently revert.
    /// </summary>
    public static Complex[] SourceImpedanceIsNotAVoltageCurrentRatio(in DeviceSpectra s, int gatePort = 0)
    {
        int k = s.HarmonicCount;
        var z = new Complex[k + 1];
        for (int h = 0; h <= k; h++)
        {
            Complex i = s.portCurrents[gatePort, h];
            z[h] = i == Complex.Zero
                ? new Complex(double.PositiveInfinity, 0)
                : s.portVoltages[gatePort, h] / i;
        }
        return z;
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>Interface-vector indices of each port's + and − node; −1 for ground.</summary>
    internal static (int[] Plus, int[] Minus) PortNodeIndices(
        ElaboratedComponent dut, int[] interfaceNodes)
    {
        int p = dut.Model.PortCount;
        var plus = new int[p];
        var minus = new int[p];
        for (int q = 0; q < p; q++)
        {
            int np = dut.Nodes.Length > 2 * q     ? dut.Nodes[2 * q]     : 0;
            int nm = dut.Nodes.Length > 2 * q + 1 ? dut.Nodes[2 * q + 1] : 0;
            plus[q]  = Array.IndexOf(interfaceNodes, np);
            minus[q] = Array.IndexOf(interfaceNodes, nm);
        }
        return (plus, minus);
    }

    /// <summary>
    /// The DUT's own <c>∂I_g/∂V_g</c> and <c>∂Q_g/∂V_g</c> conversion spectra — the single port-space
    /// entry <c>(gatePort, gatePort)</c> of <c>dg</c> and <c>dc</c>, transformed. This is precisely
    /// what J′ removes, and it has to be computed in PORT space because the node-space arrays the
    /// engine builds have already summed every port pair together.
    /// </summary>
    private static (Complex[] G, Complex[] C) GatePortSelfSpectra(
        ElaboratedComponent dut, Complex[,] v, int[] interfaceNodes,
        int k, int gridN, int gatePort, int kj)
    {
        int n = interfaceNodes.Length;
        int p = dut.Model.PortCount;

        var vTime = new double[n][];
        for (int i = 0; i < n; i++)
        {
            vTime[i] = new double[gridN];
            var spec = new Complex[k + 1];
            for (int h = 0; h <= k; h++) spec[h] = v[i, h];
            HbFft.Inverse(spec, k, vTime[i]);
        }

        var (plus, minus) = PortNodeIndices(dut, interfaceNodes);

        var points = new double[gridN][];
        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[p];
            for (int q = 0; q < p; q++)
            {
                double a = plus[q]  >= 0 ? vTime[plus[q]][t]  : 0.0;
                double b = minus[q] >= 0 ? vTime[minus[q]][t] : 0.0;
                pv[q] = a - b;
            }
            points[t] = pv;
        }

        var dgTime = new double[gridN];
        var dcTime = new double[gridN];

        var batch = dut.PrefersBatchEvaluate ? dut.EvaluateBatch(points) : null;
        for (int t = 0; t < gridN; t++)
        {
            var res = batch is not null ? batch[t] : dut.Evaluate(new PortVoltages(points[t]));
            dgTime[t] = res.Dg[gatePort, gatePort];
            dcTime[t] = res.Dc[gatePort, gatePort];
        }

        HbFft.Forward(dgTime, kj, out var gSpec, out _);
        HbFft.Forward(dcTime, kj, out var cSpec, out _);
        return (gSpec, cSpec);
    }

    /// <summary>
    /// Undoes the four-way port accumulation of one port's self block from a node-space conversion
    /// array: <c>+</c> at (p+,p+) and (p−,p−), <c>−</c> at (p+,p−) and (p−,p+).
    /// </summary>
    private static void SubtractPortSelf(Complex[,,] arr, int iPlus, int iMinus, Complex[] spectrum)
    {
        int kj = Math.Min(arr.GetLength(2), spectrum.Length);
        void Add(int a, int b, double sign)
        {
            if (a < 0 || b < 0) return;
            for (int h = 0; h < kj; h++) arr[a, b, h] -= sign * spectrum[h];
        }
        Add(iPlus,  iPlus,  +1);
        Add(iMinus, iMinus, +1);
        Add(iPlus,  iMinus, -1);
        Add(iMinus, iPlus,  -1);
    }

    /// <summary>
    /// <c>bᵀ J′⁻¹ b</c> over the harmonic-conversion space, returned as a complex
    /// <c>(K+1) × (K+1)</c> matrix.
    ///
    /// <para>The real-split convention is the engine's: DOF <c>2(n(K+1)+k) + (Im ? 1 : 0)</c>. A
    /// linear complex map appears as <c>[[x, −y], [y, x]]</c>, so the complex entry of a 2×2 block is
    /// <c>a00 + j·a10</c>. The DC row and column are real-only by construction (Maas §7.3), so the
    /// k = 0 entries come back with zero imaginary part, which is what a DC impedance should be.</para>
    /// </summary>
    private static Complex[,] PortBlockOfInverse(double[] j, int n, int k, int gatePlus, int gateMinus)
    {
        int dof = 2 * n * (k + 1);

        // One right-hand side per (harmonic, Re/Im) of the gate port's incidence vector.
        int cols = 2 * (k + 1);
        var b = new double[dof * cols];
        for (int h = 0; h <= k; h++)
            for (int part = 0; part < 2; part++)
            {
                int col = 2 * h + part;
                if (gatePlus  >= 0) b[Dof(gatePlus,  h, part == 1, n, k) * cols + col] += 1.0;
                if (gateMinus >= 0) b[Dof(gateMinus, h, part == 1, n, k) * cols + col] -= 1.0;
            }

        var x = SolveMultiRhs(j, b, dof, cols);

        // Z[k, i] = bᵀ_k · x_i, in the same real-split 2×2 layout.
        var z = new Complex[k + 1, k + 1];
        for (int row = 0; row <= k; row++)
            for (int col = 0; col <= k; col++)
            {
                double a00 = Project(x, b, dof, cols, 2 * row,     2 * col);
                double a10 = Project(x, b, dof, cols, 2 * row + 1, 2 * col);
                z[row, col] = new Complex(a00, a10);
            }
        return z;
    }

    private static double Project(double[] x, double[] b, int dof, int cols, int rowVec, int colVec)
    {
        double s = 0;
        for (int r = 0; r < dof; r++)
        {
            double bv = b[r * cols + rowVec];
            if (bv != 0) s += bv * x[r * cols + colVec];
        }
        return s;
    }

    private static int Dof(int node, int harmonic, bool isIm, int n, int k)
        => 2 * (node * (k + 1) + harmonic) + (isIm ? 1 : 0);

    /// <summary>
    /// Gauss–Jordan with partial pivoting on a dense real system with several right-hand sides.
    /// <c>J</c> is 24 × 24 for one grounded-source FET at K = 5 — microseconds, once per displayed
    /// operating point.
    /// </summary>
    private static double[] SolveMultiRhs(double[] a, double[] b, int n, int m)
    {
        var A = (double[])a.Clone();
        var B = (double[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(A[r * n + col]) > Math.Abs(A[piv * n + col])) piv = r;

            if (A[piv * n + col] == 0)
                throw new InvalidOperationException(
                    "J' is singular — the modified Jacobian has no inverse at this operating point.");

            if (piv != col)
            {
                for (int j = 0; j < n; j++) (A[col * n + j], A[piv * n + j]) = (A[piv * n + j], A[col * n + j]);
                for (int j = 0; j < m; j++) (B[col * m + j], B[piv * m + j]) = (B[piv * m + j], B[col * m + j]);
            }

            double d = A[col * n + col];
            for (int j = 0; j < n; j++) A[col * n + j] /= d;
            for (int j = 0; j < m; j++) B[col * m + j] /= d;

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = A[r * n + col];
                if (f == 0) continue;
                for (int j = 0; j < n; j++) A[r * n + j] -= f * A[col * n + j];
                for (int j = 0; j < m; j++) B[r * m + j] -= f * B[col * m + j];
            }
        }
        return B;
    }
}
