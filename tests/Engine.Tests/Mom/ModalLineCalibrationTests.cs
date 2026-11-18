// PCAL4 — the modal two-line calibration, gated on SYNTHETIC data where the answer is known exactly.
// `docs/sonnet-briefs/brief-portcal-4-modal-error-box.md`.
//
// **This is the gate that has teeth, and it costs nothing.** Everything downstream — the group
// formation, the 2N-port standard, the block peel, the run's notes — is plumbing around one piece of
// algebra, and the algebra is the part that can be smoothly, plausibly wrong. Here it is handed a
// structure whose error box, modal matrix and per-mode γ were CHOSEN, so "did it recover them" is an
// equality rather than a tolerance against another circuitRF path. Two wrong implementations can
// agree with each other; a construction cannot agree with the wrong answer.
//
// The construction, for N = 1, 2 and 3:
//   • a complex-ORTHOGONAL Ψ and N distinct γ make the matched line P(ℓ) = Ψ·diag(e^{−γℓ})·Ψᵀ;
//   • a random reciprocal error box X (A11, A22 symmetric, A12 = A21ᵀ) and its exact mirror
//     Y = F X⁻¹ F make the two standards;
//   • a random reciprocal 2N-port DUT is embedded in the same pair of boxes.
// Every number the calibration reports is then checked against what went in.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class ModalLineCalibrationTests(ITestOutputHelper output)
{
    // ── the construction ──────────────────────────────────────────────────────────────────────

    private static Mat<Complex> Sym(Random r, int n, double sc)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            { var v = new Complex(sc * (r.NextDouble() - 0.5), sc * (r.NextDouble() - 0.5)); m[i, j] = v; m[j, i] = v; }
        return m;
    }

    private static Mat<Complex> Gen(Random r, int n, double sc)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = new Complex(sc * (r.NextDouble() - 0.5), sc * (r.NextDouble() - 0.5));
        return m;
    }

    /// <summary>A complex-orthogonal N×N (ΨᵀΨ = I) from Givens rotations with complex angles — the
    /// general shape of the modal-to-terminal wave map, not a symmetric pair's special case.</summary>
    private static Mat<Complex> Orthogonal(Random r, int n)
    {
        var q = Eye(n);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                Complex th = new(0.8 * (r.NextDouble() - 0.5) + 0.6, 0.15 * (r.NextDouble() - 0.5));
                var g = Eye(n);
                g[i, i] = Complex.Cos(th); g[j, j] = Complex.Cos(th);
                g[i, j] = -Complex.Sin(th); g[j, i] = Complex.Sin(th);
                q *= g;
            }
        return q;
    }

    private static Mat<Complex> Eye(int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = Complex.One;
        return m;
    }

    private static Mat<Complex> Block(Mat<Complex> a11, Mat<Complex> a12, Mat<Complex> a21, Mat<Complex> a22)
    {
        int n = a11.RowCount;
        var r = new Mat<Complex>(2 * n, 2 * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                r[i, j] = a11[i, j]; r[i, n + j] = a12[i, j];
                r[n + i, j] = a21[i, j]; r[n + i, n + j] = a22[i, j];
            }
        return r;
    }

    private static Mat<Complex> Transpose(Mat<Complex> a)
    {
        var r = new Mat<Complex>(a.ColCount, a.RowCount);
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) r[j, i] = a[i, j];
        return r;
    }

    private static Mat<Complex> Inv(Mat<Complex> a)
    {
        int n = a.RowCount;
        var lu = a.Lu();
        var r = new Mat<Complex>(n, n);
        for (int c = 0; c < n; c++)
        {
            var e = new Vec<Complex>(n);
            e[c] = Complex.One;
            var x = lu.Solve(e);
            for (int i = 0; i < n; i++) r[i, c] = x[i];
        }
        return r;
    }

    private static double MaxAbs(Mat<Complex> a)
    {
        double m = 0;
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++) m = Math.Max(m, a[i, j].Magnitude);
        return m;
    }

    private static double Diff(Mat<Complex> a, Mat<Complex> b)
    {
        double m = 0;
        for (int i = 0; i < a.RowCount; i++) for (int j = 0; j < a.ColCount; j++)
            m = Math.Max(m, (a[i, j] - b[i, j]).Magnitude);
        return m;
    }

    private sealed record Case(
        int N, double L1, double L2, Complex[] Gamma, Mat<Complex> Psi,
        Mat<Complex> A11, Mat<Complex> A21, Mat<Complex> A22,
        Mat<Complex> Tx, Mat<Complex> Ty, Mat<Complex> SShort, Mat<Complex> SLong);

    private static Case Build(int n, int seed)
    {
        var rnd = new Random(seed * 7919 + n);
        var psi = Orthogonal(rnd, n);
        var gamma = new Complex[n];
        for (int m = 0; m < n; m++)
            gamma[m] = new Complex(2.0 + 0.7 * m, 40.0 * (1.0 + 0.23 * m + 0.05 * rnd.NextDouble()));

        double l1 = 0.010, l2 = 0.032;
        Mat<Complex> P(double len)
        {
            var e = new Mat<Complex>(n, n);
            for (int m = 0; m < n; m++) e[m, m] = Complex.Exp(-gamma[m] * len);
            return psi * e * Transpose(psi);
        }

        var a11 = Sym(rnd, n, 0.5);
        var a22 = Sym(rnd, n, 0.5);
        var a21 = Gen(rnd, n, 0.4);
        for (int i = 0; i < n; i++) a21[i, i] += 0.8;          // a feed is diagonally dominant

        var tx = ModalLineCalibration.SToT(Block(a11, Transpose(a21), a21, a22), n);
        var f  = ModalLineCalibration.Flip(n);
        var ty = f * Inv(tx) * f;

        Mat<Complex> Std(double len) => ModalLineCalibration.TToS(
            tx * ModalLineCalibration.SToT(Block(new Mat<Complex>(n, n), P(len), P(len),
                                                   new Mat<Complex>(n, n)), n) * ty, n);

        return new Case(n, l1, l2, gamma, psi, a11, a21, a22, tx, ty, Std(l1), Std(l2));
    }

    /// <summary>βΔℓ per mode, in the order the true γ are in — the prediction the first frequency of
    /// a sweep gets from the electrostatics.</summary>
    private static double[] Expect(Case c)
    {
        var e = new double[c.N];
        for (int m = 0; m < c.N; m++) e[m] = c.Gamma[m].Imaginary * (c.L2 - c.L1);
        return e;
    }

    private static Mat<double> Identity(int n)
    {
        var t = new Mat<double>(n, n);
        for (int i = 0; i < n; i++) t[i, i] = 1.0;
        return t;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The one structural fact the whole file rests on
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Y = F X⁻¹ F.</b> D4 builds both of a standard's error boxes as exact mirror images, and
    /// that identity is what makes the closed form possible at all — real multiline TRL needs a
    /// reflect standard precisely because its two boxes are unrelated. Asserted as an equality on
    /// the mirror box's own scattering blocks, not on the T-matrix, because the blocks are what the
    /// claim is ABOUT.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheMirrorErrorBoxIsTheFlippedInverse(int n)
    {
        var c = Build(n, 1);
        var sy = ModalLineCalibration.TToS(c.Ty, n);
        double err = Diff(sy, Block(c.A22, c.A21, Transpose(c.A21), c.A11));
        output.WriteLine($"N={n}  |S_Y − mirror(S_X)| = {err:E2}");
        Assert.True(err < 1e-12, $"the mirror identity failed at {err:E2}");
    }

    /// <summary>S ↔ T is a round trip, and the 2-port case is RFNetwork's own convention entry for
    /// entry — the block form is a generalisation, not a second convention.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheBlockCascadeRoundTrips_AndAtOneConductorIsTheTwoPortOne(int n)
    {
        var c = Build(n, 2);
        double trip = Diff(ModalLineCalibration.TToS(ModalLineCalibration.SToT(c.SShort, n), n), c.SShort);
        Assert.True(trip < 1e-12, $"S→T→S drifted by {trip:E2}");

        if (n != 1) return;
        var rf = RfCore.RFNetwork.SToT2Port(c.SShort);
        double same = Diff(ModalLineCalibration.SToT(c.SShort, 1), rf);
        output.WriteLine($"block T vs RFNetwork.SToT2Port: {same:E2}");
        Assert.True(same < 1e-12, $"the block form is a different convention ({same:E2})");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D5' — γ per mode
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><b>At N = 1 the palindromic reduction IS D5's cosh(γΔℓ) = ½tr(M).</b> Asserted
    /// because the constant term of Q(s) is c_N and not c_N·q₀, and the factor of two that
    /// distinguishes them is invisible in every other property the polynomial has.</summary>
    [Fact]
    public void AtOneConductorTheReducedPolynomialIsHalfTheTrace()
    {
        var c = Build(1, 3);
        var t1 = ModalLineCalibration.SToT(c.SShort, 1);
        var t2 = ModalLineCalibration.SToT(c.SLong, 1);
        var m  = t2 * Inv(t1);

        var tail = ModalLineCalibration.ReducedPolynomial(
            ModalLineCalibration.CharacteristicPolynomial(m), 1);
        Complex s = ModalLineCalibration.Roots(tail)[0];
        Complex trace = m[0, 0] + m[1, 1];

        output.WriteLine($"s = {s}, tr(M) = {trace}");
        Assert.True((s - trace).Magnitude < 1e-9 * trace.Magnitude,
                    $"the reduced root is not tr(M): {s} vs {trace}");
        Assert.True((Complex.Cosh(c.Gamma[0] * (c.L2 - c.L1)) - 0.5 * s).Magnitude < 1e-9,
                    "½s is not cosh(γΔℓ)");
    }

    /// <summary>Every mode's γ comes back, for one, two and three conductors, symmetric or not.</summary>
    [Theory]
    [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 3)]
    public void EveryModesPropagationConstantIsRecovered(int n, int seed)
    {
        var c = Build(n, seed);
        var box = ModalLineCalibration.Solve(c.SShort, c.SLong, c.L1, c.L2, Expect(c), Identity(n));

        double worst = 0;
        for (int m = 0; m < n; m++)
            worst = Math.Max(worst, (box.Gamma[m] - c.Gamma[m]).Magnitude / c.Gamma[m].Magnitude);

        output.WriteLine($"N={n} seed={seed}  worst |Δγ|/|γ| = {worst:E2}  " +
                         $"palindrome {box.PalindromeResidual:E2}  cascade {box.CascadeResidual:E2}  " +
                         $"pair {box.ReciprocalPairResidual:E2}  gauge {box.GaugeResidual:E2}");
        Assert.True(worst < 1e-10, $"γ is out by {worst:E2}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D6' — the residuals mean what they say, and the box is the box
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A11 is a TERMINAL quantity on both indices and carries no modal gauge at all</b> — so it
    /// must come back exactly, and it is the one block of the error box that can be checked against
    /// what went in without deciding a sign first.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheExternalReflectionIsRecoveredExactly(int n)
    {
        var c = Build(n, 4);
        var box = ModalLineCalibration.Solve(c.SShort, c.SLong, c.L1, c.L2, Expect(c), Identity(n));
        double err = Diff(box.A11, c.A11) / MaxAbs(c.A11);
        output.WriteLine($"N={n}  |ΔA11|/|A11| = {err:E2}");
        Assert.True(err < 1e-10, $"A11 is out by {err:E2}");
    }

    /// <summary>
    /// <b>Every residual the box reports is a quantity that VANISHES on exact data</b> — the
    /// cascade's off-structure entries, its reciprocal pair, the modal gauge's off-diagonal and the
    /// characteristic polynomial's palindrome. A residual that is small because it is computed
    /// wrongly is worse than none, so they are gated rather than merely printed.
    /// </summary>
    [Theory]
    [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void EveryReportedResidualVanishesOnExactData(int n, int seed)
    {
        var c = Build(n, seed);
        var box = ModalLineCalibration.Solve(c.SShort, c.SLong, c.L1, c.L2, Expect(c), Identity(n));

        output.WriteLine($"N={n} seed={seed}: palindrome {box.PalindromeResidual:E2}, " +
                         $"cascade {box.CascadeResidual:E2}, pair {box.ReciprocalPairResidual:E2}, " +
                         $"gauge {box.GaugeResidual:E2}, null-space gap {box.NullSpaceGap:E2}");
        Assert.True(box.PalindromeResidual      < 1e-10);
        Assert.True(box.CascadeResidual         < 1e-9);
        Assert.True(box.ReciprocalPairResidual  < 1e-10);
        Assert.True(box.GaugeResidual           < 1e-9);
    }

    /// <summary><b>The box is reciprocal once its gauge is fixed</b> — A12 = A21ᵀ is what equation
    /// (5) is solved for, so it is a property of the answer rather than of the input.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheModalBoxIsReciprocal(int n)
    {
        var c = Build(n, 5);
        var box = ModalLineCalibration.Solve(c.SShort, c.SLong, c.L1, c.L2, Expect(c), Identity(n));
        double err = Diff(box.A12, Transpose(box.A21)) / MaxAbs(box.A21);
        output.WriteLine($"N={n}  |A12 − A21ᵀ|/|A21| = {err:E2}");
        Assert.True(err < 1e-10, $"the modal box is not reciprocal ({err:E2})");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The whole peel — a known DUT, embedded and recovered
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The end-to-end claim: a DUT embedded between the two mirror boxes comes back.</b> Up to the
    /// per-mode SIGN, which no bilinear condition can fix and which the production path resolves
    /// against the electrostatic modal matrix — so the check here is over the sign choices, which is
    /// exactly the freedom the algebra genuinely leaves.
    /// </summary>
    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void AnEmbeddedDutIsRecovered(int n, int seed)
    {
        var c = Build(n, seed);
        var rnd = new Random(seed * 13 + n);
        var dutA = Sym(rnd, n, 0.6);
        var dutB = Gen(rnd, n, 0.5);
        var dutC = Sym(rnd, n, 0.6);
        var sDut = Block(dutA, Transpose(dutB), dutB, dutC);

        var tMeas = c.Tx * ModalLineCalibration.SToT(sDut, n) * c.Ty;

        var box = ModalLineCalibration.Solve(c.SShort, c.SLong, c.L1, c.L2, Expect(c), Identity(n));
        var xs  = ModalLineCalibration.SToT(Block(box.A11, box.A12, box.A21, box.A22), n);
        var f   = ModalLineCalibration.Flip(n);
        var ys  = f * Inv(xs) * f;
        var rec = ModalLineCalibration.TToS(Inv(xs) * tMeas * Inv(ys), n);

        // modal → terminal is blkdiag(Ψ,Ψ)·S·blkdiag(Ψ,Ψ)⁻¹, up to one sign per mode
        double best = double.PositiveInfinity;
        for (int bits = 0; bits < 1 << n; bits++)
        {
            var sg = Eye(n);
            for (int m = 0; m < n; m++) sg[m, m] = (bits >> m & 1) == 1 ? -1 : 1;
            var pp  = c.Psi * sg;
            var big = Block(pp, new Mat<Complex>(n, n), new Mat<Complex>(n, n), pp);
            best = Math.Min(best, Diff(big * rec * Inv(big), sDut) / MaxAbs(sDut));
        }

        output.WriteLine($"N={n} seed={seed}  |ΔS_dut|/|S_dut| = {best:E2}");
        Assert.True(best < 1e-9, $"the de-embedded DUT is out by {best:E2}");
    }

    /// <summary>
    /// <b>The block peel with 1×1 blocks IS the scalar peel.</b> The two are separate methods on
    /// purpose — a run with no group must keep running the shipped one, bit for bit — so what has to
    /// be asserted is that they are the same arithmetic rather than two answers that merely look
    /// alike.
    /// </summary>
    [Fact]
    public void TheBlockPeelWithScalarBlocksIsThePeelThatShips()
    {
        var rnd = new Random(97);
        const int p = 4;
        var raw = Sym(rnd, p, 0.9);
        var boxes = new LineErrorBox[p];
        for (int i = 0; i < p; i++)
            boxes[i] = new LineErrorBox(
                new Complex(0.05 * i - 0.1, 0.03), new Complex(-0.2 + 0.04 * i, 0.05),
                new Complex(0.8 - 0.05 * i, 0.1), 0, 0);

        var scalar = LineDeembed.Apply(raw, boxes);

        var blocks = new List<LineBlockBox>();
        for (int i = 0; i < p; i++)
        {
            Mat<Complex> M(Complex v) { var m = new Mat<Complex>(1, 1); m[0, 0] = v; return m; }
            blocks.Add(new LineBlockBox([i], M(boxes[i].A11), M(boxes[i].A21),
                                                        M(boxes[i].A21), M(boxes[i].A22)));
        }
        var block = LineDeembed.ApplyBlocks(raw, blocks);

        double err = Diff(scalar, block) / MaxAbs(scalar);
        output.WriteLine($"|Apply − ApplyBlocks|/|S| = {err:E2}");
        Assert.True(err < 1e-12, $"the two peels disagree by {err:E2}");
    }
}
