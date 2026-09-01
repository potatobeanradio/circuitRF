using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The passive-intermod overlay (brief-sys-4) at the model level: what turning it on changes about
/// a block, what it must not change, and the two refusals that have to happen at construction
/// because nothing inside a Newton iteration can name the instance.
///
/// <para>Every expected number here is computed IN THIS FILE from the dB and dBm values a user
/// types. The admittance matrices are written out in closed form — <c>(g/(1−t²))·[[1+t², −2t], …]</c>
/// for the attenuator, the antisymmetric zero-diagonal one for the circulator — rather than read
/// back out of the model, which would be the model agreeing with itself.</para>
///
/// <para>The end-to-end halves live in <c>tests/Engine.Tests</c>: the S-parameter equivalence and
/// the quadrature gate in <c>PassiveIntermodSParamTests</c>, the two-tone product level and its
/// routing in <c>PassiveIntermodHbTests</c>.</para>
/// </summary>
public class PassiveIntermodModelTests
{
    private static double Amp(double db) => Math.Pow(10.0, -db / 20.0);

    // ── Off is off ────────────────────────────────────────────────────────────

    [Fact]
    public void EveryBlock_IsLinearAndCarriesNoOverlay_AtTheDefault()
    {
        // The default is -200 dBm, which means "there is no intermod here" — not "there is a very
        // small one". A block at the default must be indistinguishable from the SYS-3 one.
        IdealSBlockModel[] blocks =
        [
            new AttenuatorModel(10, 50, 200),
            new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50),
            new CouplerModel(20, Math.PI / 2, 200, 0, 200, 50),
        ];

        foreach (var b in blocks)
        {
            Assert.Equal(ModelKind.Linear, b.Kind);
            Assert.Null(b.Pim);
        }
    }

    [Fact]
    public void TheOffThreshold_IsMinus190dBm_AndIsTakenLiterallyJustAboveIt()
    {
        // Same shape as MixerModel's IIP3: one side is exactly ideal, the other is built at
        // whatever was asked for. It is NOT the family's 150 dB, because that number is a
        // SUPPRESSION (a ratio) and this one is an absolute level — -150 dBm is an ordinary claim
        // for a good passive part, and switching it off silently would be a wrong answer.
        Assert.Null   (new AttenuatorModel(10, 50, 200, pimDbm: -190.0).Pim);
        Assert.NotNull(new AttenuatorModel(10, 50, 200, pimDbm: -189.9).Pim);
        Assert.NotNull(new AttenuatorModel(10, 50, 200, pimDbm: -150.0).Pim);
        Assert.Equal(ModelKind.Nonlinear, new AttenuatorModel(10, 50, 200, pimDbm: -110.0).Kind);
    }

    [Fact]
    public void TurningPimOn_ChangesNeitherTheSMatrixNorThePortNames()
    {
        // The overlay is derived FROM the S; it must never write back into it. Bit-identical, not
        // close — the same matrix object's worth of numbers either way.
        var off = new AttenuatorModel(3.0, 75.0, 18.0);
        var on  = new AttenuatorModel(3.0, 75.0, 18.0, pimDbm: -110.0, pimPcDbm: 43.0);

        var sOff = off.SAt(2 * Math.PI * 1e9);
        var sOn  = on .SAt(2 * Math.PI * 1e9);
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Assert.Equal(sOff[p, q], sOn[p, q]);

        Assert.Equal(off.TerminalNames, on.TerminalNames);
        Assert.Equal(off.PortCount,     on.PortCount);
        Assert.Equal(75.0, on.PortZOf(0));
    }

    // ── The small-signal Jacobian IS the block's admittance matrix ─────────────

    [Fact]
    public void AtZeroBias_TheAttenuatorJacobianIsItsClosedFormY()
    {
        // ψ(x) = Vsat·tanh(x/Vsat) − x has ψ'(0) = 0 EXACTLY, so the linearisation at the DC
        // operating point is Y with nothing added — whatever the PIM level. That is what makes an
        // S-parameter run with PIM on report the same numbers as one with it off, and it is a
        // stronger statement than the brief's "agrees at 60 dB down".
        const double lossDb = 6.0, z0 = 50.0;
        double t = Amp(lossDb);
        double g = 1.0 / z0;

        // Y = (1/Z0)·(I+S)⁻¹(I−S) for S = [[0,t],[t,0]], written out by hand.
        double ya = g * (1 + t * t) / (1 - t * t);
        double yb = g * (-2 * t)    / (1 - t * t);

        var m = new AttenuatorModel(lossDb, z0, 200, pimDbm: -110.0);
        var r = m.Evaluate(new PortVoltages([0.0, 0.0]));

        Assert.Equal(ya, r.Dg[0, 0], 12);
        Assert.Equal(ya, r.Dg[1, 1], 12);
        Assert.Equal(yb, r.Dg[0, 1], 12);
        Assert.Equal(yb, r.Dg[1, 0], 12);
        Assert.Empty(r.Terms);                       // a real S needs no quadrature bucket
        foreach (double q in r.Q) Assert.Equal(0.0, q);
    }

    [Fact]
    public void AtZeroBias_TheCirculatorJacobianIsTheAntisymmetricY()
    {
        // The matrix CirculatorModel's own doc comment writes down: antisymmetric, ZERO diagonal,
        // and itself singular because every row and column of a floating network's Y sums to zero.
        var m = new CirculatorModel(CirculatorDirection.CW, 0, 200, 200, 50, pimDbm: -110.0);
        var r = m.Evaluate(new PortVoltages([0.0, 0.0, 0.0]));

        double[,] expected =
        {
            {  0,  1, -1 },
            { -1,  0,  1 },
            {  1, -1,  0 },
        };
        for (int p = 0; p < 3; p++)
        for (int q = 0; q < 3; q++)
            Assert.Equal(expected[p, q] / 50.0, r.Dg[p, q], 12);

        for (int p = 0; p < 3; p++)
        {
            double rowSum = 0, colSum = 0;
            for (int q = 0; q < 3; q++) { rowSum += r.Dg[p, q]; colSum += r.Dg[q, p]; }
            Assert.Equal(0.0, rowSum, 12);
            Assert.Equal(0.0, colSum, 12);
        }
    }

    // ── The quadrature bucket ─────────────────────────────────────────────────

    [Fact]
    public void OnlyAComplexS_CarriesAQuadratureBucket()
    {
        var quad   = new CouplerModel(3.0103, Math.PI / 2, 200, 0, 200, 50, pimDbm: -110.0);
        var inPhase = new CouplerModel(3.0103, 0.0,        200, 0, 200, 50, pimDbm: -110.0);

        Assert.True (quad   .Pim!.HasQuadrature);
        Assert.False(inPhase.Pim!.HasQuadrature);

        Assert.Single(quad.Evaluate(new PortVoltages([0, 0, 0, 0])).Terms);
        Assert.Empty (inPhase.Evaluate(new PortVoltages([0, 0, 0, 0])).Terms);
    }

    [Fact]
    public void TheQuadratureWeight_IsJSignOmega_AndVanishesAtDc()
    {
        var m = new CouplerModel(3.0103, Math.PI / 2, 200, 0, 200, 50, pimDbm: -110.0);

        Assert.Equal(new Complex(0,  1), m.Weight(2,  2 * Math.PI * 1e9));
        Assert.Equal(new Complex(0, -1), m.Weight(2, -2 * Math.PI * 1e9));

        // A frequency-domain factor has to be zero at DC, which is what removes the whole bucket
        // from a DC solve rather than leaving it there as a constant.
        Assert.Equal(Complex.Zero, m.Weight(2, 0.0));
    }

    [Fact]
    public void AtZeroBias_TheHybridsTwoBucketsRecombineIntoItsExactY()
    {
        // Re(Y) + j·Im(Y) = Y for ω > 0, which is the whole content of the bucket split. The
        // reference is the ideal 3 dB quadrature hybrid's Y, computed here from its own S:
        //   Z0·Y = −S + S² − S³  (because (I+S)⁻¹ = (I − S + S² − S³)/2 when S⁴ = −I)
        const double z0 = 50.0;
        double t = 1.0 / Math.Sqrt(2.0);
        var s = new Complex[4, 4];
        void Pair(int p, int q, Complex v) { s[p, q] = v; s[q, p] = v; }
        Pair(0, 1, t); Pair(2, 3, t);
        Pair(0, 2, new Complex(0, -t)); Pair(1, 3, new Complex(0, -t));

        var s2 = Mul(s, s);
        var s3 = Mul(s2, s);
        var y  = new Complex[4, 4];
        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
            y[p, q] = (-s[p, q] + s2[p, q] - s3[p, q]) / z0;

        // The EXACT equal split, not the registry's 3.0103 — that spelling is 4 ppb away from
        // 1/√2, which is 5e-9 on the matrix and would make this gate about the rounding in a tile
        // default rather than about the bucket split.
        double exact3dB = 20.0 * Math.Log10(Math.Sqrt(2.0));

        var m = new CouplerModel(exact3dB, Math.PI / 2, 200, 0, 200, z0, pimDbm: -110.0);
        var r = m.Evaluate(new PortVoltages([0, 0, 0, 0]));
        var bucket = Assert.Single(r.Terms);
        Assert.Equal(2, bucket.W);

        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
        {
            var got = new Complex(r.Dg[p, q], bucket.Jac[p, q]);
            Assert.True((y[p, q] - got).Magnitude < 1e-12,
                        $"Y[{p},{q}]: expected {y[p, q]}, got {got}");
        }

        // And the ideal hybrid's Y is PURELY imaginary — it is an open circuit at DC, which is what
        // the DC gate in PassiveIntermodSParamTests then has to survive rather than paper over.
        for (int p = 0; p < 4; p++)
        for (int q = 0; q < 4; q++)
            Assert.Equal(0.0, r.Dg[p, q], 12);
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0);
        var c = new Complex[n, n];
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
        {
            Complex acc = Complex.Zero;
            for (int k = 0; k < n; k++) acc += a[p, k] * b[k, q];
            c[p, q] = acc;
        }
        return c;
    }

    // ── The refusals ──────────────────────────────────────────────────────────

    [Fact]
    public void AMatchedZeroDbAttenuatorWithPim_IsRefusedByName_BecauseAWireHasNoY()
    {
        // brief-sys-4 holds this exact part up as the standalone PIM generator, and it cannot
        // exist: S = [[0,1],[1,0]] gives det(I+S) = 0 EXACTLY, so the block has no admittance
        // matrix and no memoryless i = f(v) at all. Refused where the block can be named.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new AttenuatorModel(0.0, 50.0, 200.0, pimDbm: -110.0));

        Assert.Contains("AttenuatorModel", ex.Message);
        Assert.Contains("0.01 dB", ex.Message);

        // And the remedy in the message is a real one, not a form of words.
        Assert.NotNull(new AttenuatorModel(0.01, 50.0, 200.0, pimDbm: -110.0).Pim);
        Assert.NotNull(new AttenuatorModel(0.00, 50.0,  20.0, pimDbm: -110.0).Pim);
    }

    [Fact]
    public void AZeroDbCouplerWithPim_IsRefusedForTheSameReason()
    {
        // Coupling = 0 dB makes S a pure swap of port pairs — a permutation with −1 in its
        // spectrum, so I + S is singular exactly as the ideal through's is.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new CouplerModel(0.0, 0.0, 200, 0, 200, 50, pimDbm: -110.0));
        Assert.Contains("CouplerModel", ex.Message);
    }

    [Fact]
    public void AFactoryTypeThatCannotHostPim_IsRefusedByName_WithSomethingToDoInstead()
    {
        // The balun and the switch are excluded by their own briefs' decisions, and the filter and
        // the duplexer will be excluded because their S is frequency-dependent — which is why the
        // check lives at the factory's shared entry point rather than in each creator, where the
        // two that do not exist yet would have to remember to add it.
        foreach (string type in new[] { "Balun", "Switch", "Filter", "Duplexer" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ComponentModelFactory.TryCreate(
                type,
                new Dictionary<string, Value>(StringComparer.Ordinal)
                {
                    ["PIM"]   = new Value(-110.0),
                    ["PIMPc"] = new Value(43.0),
                }));

            Assert.Contains(type, ex.Message);
            Assert.Contains("attenuator", ex.Message);
        }

        // And the three that CAN host one are not caught by it.
        foreach (string type in new[] { "Atten", "Circulator", "Coupler" })
            Assert.NotNull(ComponentModelFactory.TryCreate(
                type,
                new Dictionary<string, Value>(StringComparer.Ordinal)
                {
                    ["PIM"]   = new Value(-110.0),
                    ["PIMPc"] = new Value(43.0),
                }));
    }
}
