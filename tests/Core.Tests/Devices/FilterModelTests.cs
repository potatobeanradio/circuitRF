using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The two components brief-sys-6 adds, at the MODEL level: the matrix the filter stamps, and the
/// two-stamps-on-one-node shape that is the whole of the duplexer.
///
/// <para>The response itself is gated in <c>FilterPrototypeTests</c> against textbook formulae, and
/// end to end in <c>tests/Engine.Tests/Devices/FilterSParamTests.cs</c>. What is left here is what
/// neither of those can see: which entries reach the matrix, and where.</para>
/// </summary>
public class FilterModelTests
{
    private static FilterModel Bandpass(double zIn = 50, double zOut = 50, double ilDb = 0)
        => new(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3,
               fcHz: 0, f1Hz: 0.9e9, f2Hz: 1.1e9, rippleDb: 0.1, astopDb: 40,
               zIn: zIn, zOut: zOut, ilDb: ilDb);

    [Fact]
    public void TheFilterStampsItsNetworksOwnMatrix_AndIsReciprocal()
    {
        var m = Bandpass();
        foreach (double f in new[] { 0.5e9, 0.95e9, 1.0e9, 1.05e9, 3.0e9 })
        {
            double w = 2 * Math.PI * f;
            var (s11, s21, s22) = m.Network.At(w);
            var s = m.SAt(w);

            Assert.Equal(s11, s[0, 0]);
            Assert.Equal(s22, s[1, 1]);
            Assert.Equal(s21, s[0, 1]);
            Assert.Equal(s21, s[1, 0]);      // S12 = S21 exactly; a filter that is not is an isolator
        }
    }

    /// <summary>
    /// <c>S(−ω) = conj(S(ω))</c>. Inert on today's engine paths — <c>HbEngine</c> extracts at
    /// <c>|ω|</c> and conjugates the whole admittance itself — and the first component in this
    /// family whose S is genuinely complex at every frequency, so the first for which a broken rule
    /// would be visible at all.
    /// </summary>
    [Fact]
    public void TheFilterObeysTheConjugateSymmetryOfARealNetwork()
    {
        var m = Bandpass();
        foreach (double f in new[] { 0.3e9, 0.95e9, 1.0e9, 2.0e9 })
        {
            double w = 2 * Math.PI * f;
            var pos = (Complex[,])m.SAt(w).Clone();
            var neg = m.SAt(-w);
            for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                Assert.Equal(Complex.Conjugate(pos[p, q]), neg[p, q]);
        }
    }

    [Fact]
    public void TheFilterIsALinearModelWithTwoPortsAndNumberedTerminals()
    {
        var m = Bandpass();
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(2, m.PortCount);
        Assert.Equal(["1", "2"], m.TerminalNames);
    }

    /// <summary>
    /// Unequal port impedances are simply what S is referenced against — the property that made an
    /// S-matrix the right stamp rather than a synthesised ladder, which cannot take an arbitrary
    /// termination ratio at all.
    /// </summary>
    [Theory]
    [InlineData(50.0, 25.0)] [InlineData(25.0, 50.0)] [InlineData(75.0, 12.5)]
    public void TheFilterAcceptsAnyPairOfPortImpedances(double zIn, double zOut)
    {
        var m = Bandpass(zIn, zOut);
        Assert.Equal(zIn,  m.PortZOf(0));
        Assert.Equal(zOut, m.PortZOf(1));

        // The RESPONSE does not change with them: it is the same rational S, referenced to a
        // different pair of impedances. That is what makes the block a lossless transformer as well
        // as a filter, and it is why there is no feasibility question to refuse.
        var equal = Bandpass();
        for (double f = 0.5e9; f <= 2e9; f += 0.05e9)
        {
            var a = equal.SAt(2 * Math.PI * f);
            var b = m.SAt(2 * Math.PI * f);
            Assert.Equal(a[0, 0], b[0, 0]);
            Assert.Equal(a[1, 0], b[1, 0]);
        }
    }

    /// <summary>A non-positive reference impedance is not a port; it falls back to 50 Ω.</summary>
    [Theory]
    [InlineData(0.0)] [InlineData(-50.0)]
    public void ANonPositivePortImpedanceFallsBackToFifty(double bad)
    {
        var m = Bandpass(zIn: bad, zOut: bad);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));
    }

    // ══ The stamp ═════════════════════════════════════════════════════════════

    [Fact]
    public void TheFilterStampsOneBranchPerPort_AndTheWaveConstraintRows()
    {
        var mna = new CapturingMnaContext();
        var m = Bandpass();
        double w = 2 * Math.PI * 1e9;
        var c = Component(m, [1, 0, 2, 0]);

        m.Stamp(mna, c, w);

        Assert.Equal(2, mna.BranchCount);
        Assert.Equal([(0, 1, 0), (1, 2, 0)], mna.BranchCurrents);

        var s = m.SAt(w);
        const double R = 7.0710678118654755;   // √50

        // Row p: (v_p − Z0·i_p)/√Z0 − Σ_q S_pq (v_q + Z0·i_q)/√Z0.
        Assert.Equal(new Complex(1.0 / R, 0) - s[0, 0] / R, mna.NodeConstraints[(0, 1)]);
        Assert.Equal(                       -s[0, 1] / R, mna.NodeConstraints[(0, 2)]);
        Assert.Equal(new Complex(-R, 0) - s[0, 0] * R, mna.BranchConstraints[(0, 0)]);
        Assert.Equal(                    -s[0, 1] * R, mna.BranchConstraints[(0, 1)]);

        // Nothing is stamped against the ground returns — node 0 is skipped, as everywhere here.
        Assert.False(mna.NodeConstraints.ContainsKey((0, 0)));
    }

    // ══ The duplexer ══════════════════════════════════════════════════════════

    private static DuplexerModel Duplexer(double zAnt = 50, double zTx = 50, double zRx = 50)
        => new(Arm(0.90e9, 1.00e9), Arm(1.10e9, 1.20e9), zAnt, zTx, zRx);

    private static FilterNetwork Arm(double f1, double f2)
        => FilterNetwork.Create(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, f1, f2, 0.1, 40, 0);

    [Fact]
    public void TheDuplexerIsThreeLinearPortsWithNamedTerminals()
    {
        var d = Duplexer();
        Assert.Equal(ModelKind.Linear, d.Kind);
        Assert.Equal(3, d.PortCount);
        Assert.Equal(["ANT", "TX", "RX"], d.TerminalNames);
    }

    /// <summary>
    /// Each arm's S IS the standalone filter's, to the bit — the duplexer adds no mathematics, it
    /// only stamps two of them onto one node.
    /// </summary>
    [Fact]
    public void EachArmsMatrixIsExactlyTheStandaloneFiltersOwn()
    {
        var d  = Duplexer();
        var tx = new FilterModel(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, 0.90e9, 1.00e9, 0.1, 40, 50, 50, 0);
        var rx = new FilterModel(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3, 0, 1.10e9, 1.20e9, 0.1, 40, 50, 50, 0);

        for (double f = 0.5e9; f <= 2e9; f += 0.013e9)
        {
            double w = 2 * Math.PI * f;
            var expectedTx = (Complex[,])tx.SAt(w).Clone();
            var actualTx   = (Complex[,])d.ArmSAt(d.Tx, w).Clone();
            var expectedRx = (Complex[,])rx.SAt(w).Clone();
            var actualRx   = d.ArmSAt(d.Rx, w);

            for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
            {
                Assert.Equal(expectedTx[p, q], actualTx[p, q]);
                Assert.Equal(expectedRx[p, q], actualRx[p, q]);
            }
        }
    }

    /// <summary>
    /// The shape that IS the duplexer: four branch currents, no internal node, and the ANT pair
    /// appearing in both arms' KCL. Nothing else in this repository stamps two matrices from one
    /// component, so nothing else can catch a mis-wired second arm.
    /// </summary>
    [Fact]
    public void TheDuplexerStampsTwoArmsOntoTheSharedAntennaNodes()
    {
        var mna = new CapturingMnaContext();
        var d = Duplexer();
        double w = 2 * Math.PI * 0.95e9;

        // [ant+, ant−, tx+, tx−, rx+, rx−] — the single-ended tile's own ground returns.
        d.Stamp(mna, Component(d, [1, 0, 2, 0, 3, 0]), w);

        // Four branches, and no fifth: an internal node would show up as one.
        Assert.Equal(4, mna.BranchCount);
        Assert.Equal([0, 1], d.TxBranchIndices);
        Assert.Equal([2, 3], d.RxBranchIndices);

        // Branches 0 and 2 are BOTH the antenna's — that is the shared node, written out.
        Assert.Equal([(0, 1, 0), (1, 2, 0), (2, 1, 0), (3, 3, 0)], mna.BranchCurrents);

        // The TX arm's rows never touch the RX net and vice versa; the arms meet only at ANT.
        Assert.False(mna.NodeConstraints.ContainsKey((0, 3)));
        Assert.False(mna.NodeConstraints.ContainsKey((1, 3)));
        Assert.False(mna.NodeConstraints.ContainsKey((2, 2)));
        Assert.False(mna.NodeConstraints.ContainsKey((3, 2)));

        // …and no branch constraint crosses between them either.
        Assert.False(mna.BranchConstraints.ContainsKey((0, 2)));
        Assert.False(mna.BranchConstraints.ContainsKey((3, 1)));
    }

    [Fact]
    public void TheDuplexersAntennaImpedanceIsSharedByBothArms()
    {
        var mna = new CapturingMnaContext();
        var d = Duplexer(zAnt: 75, zTx: 50, zRx: 25);
        d.Stamp(mna, Component(d, [1, 0, 2, 0, 3, 0]), 2 * Math.PI * 1e9);

        // The self term of a wave-constraint row is −√Z0 plus −S_pp·√Z0, so the ANT rows of the two
        // arms carry the SAME √75 while their far ends carry √50 and √25.
        var txAnt = mna.BranchConstraints[(0, 0)];
        var rxAnt = mna.BranchConstraints[(2, 2)];
        var sTx = (Complex[,])d.ArmSAt(d.Tx, 2 * Math.PI * 1e9).Clone();
        var sRx = d.ArmSAt(d.Rx, 2 * Math.PI * 1e9);

        double rAnt = Math.Sqrt(75.0);
        Assert.Equal(new Complex(-rAnt, 0) - sTx[0, 0] * rAnt, txAnt);
        Assert.Equal(new Complex(-rAnt, 0) - sRx[0, 0] * rAnt, rxAnt);
        Assert.Equal(new Complex(-Math.Sqrt(50.0), 0) - sTx[1, 1] * Math.Sqrt(50.0), mna.BranchConstraints[(1, 1)]);
        Assert.Equal(new Complex(-Math.Sqrt(25.0), 0) - sRx[1, 1] * Math.Sqrt(25.0), mna.BranchConstraints[(3, 3)]);
    }

    [Theory]
    [InlineData(0.0)] [InlineData(-1.0)]
    public void ANonPositiveDuplexerImpedanceFallsBackToFifty(double bad)
    {
        var mna = new CapturingMnaContext();
        var d = Duplexer(zAnt: bad, zTx: bad, zRx: bad);
        d.Stamp(mna, Component(d, [1, 0, 2, 0, 3, 0]), 2 * Math.PI * 1e9);

        // The row's own (v/√Z0) coefficient is 1/√50 when the fallback took.
        Assert.Equal(1.0 / Math.Sqrt(50.0), mna.NodeConstraints[(0, 1)].Real
                     + (d.ArmSAt(d.Tx, 2 * Math.PI * 1e9)[0, 0] / Math.Sqrt(50.0)).Real, 12);
    }

    [Fact]
    public void ADuplexerWithoutBothArmsIsRefusedRatherThanNullReferencing()
    {
        Assert.Throws<ArgumentNullException>(() => new DuplexerModel(null!, Arm(1e9, 1.1e9), 50, 50, 50));
        Assert.Throws<ArgumentNullException>(() => new DuplexerModel(Arm(1e9, 1.1e9), null!, 50, 50, 50));
    }

    private static ElaboratedComponent Component(ComponentModel model, int[] nodes)
        => new("Filter", "F1", nodes, new Dictionary<string, Value>(), model);
}
