using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// The six two-element members of the RLC family — SRL, SRC, SLC, PRL, PRC, PLC (owner,
/// 2026-09-20).
///
/// <para><b>The oracle is the closed form, not an equivalent circuitRF netlist</b>, for the reason
/// <see cref="SeriesParallelRlcTests"/> gives at length: the two paths share the whole engine
/// underneath, so a sign error in a constraint diagonal would agree with itself. The one
/// equivalence asserted here is the Mutual case, where sameness IS the claim.</para>
///
/// <para><b>One test per claim.</b> Six components times two frequencies is one theory, not twelve
/// facts — what differs between the rows is data, and the arithmetic that turns a reference name
/// into an expected impedance is written once.</para>
/// </summary>
public class TwoElementRlcTests
{
    private const double Z0 = 50.0;
    private const double R = 2.0, L = 3e-9, C = 4e-12;

    private static DataSet Run(string cnl, double[] freqsHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return SParameterEngine.Run(nl, freqsHz);
    }

    private static Complex S11(DataSet ds) => (Complex)ds["S"][0, 0, 0];

    /// <summary>The values the netlist line carries, for a reference like "SRL" or "PLC".</summary>
    private static string Values(string reference) =>
        (reference.Contains('R') ? "R=2 Ohm " : "")
      + (reference.Contains('L') ? "L=3 nH "  : "")
      + (reference.Contains('C') ? "C=4 pF"   : "");

    /// <summary>
    /// The element's own impedance at ω, written out rather than simulated. Series adds impedances
    /// over the elements present; parallel adds admittances. An ABSENT element contributes nothing
    /// in either sum, which is the same statement <c>RlcElements</c> makes on the model side.
    /// </summary>
    private static Complex ClosedFormZ(string reference, double w)
    {
        bool hasR = reference.Contains('R'), hasL = reference.Contains('L'), hasC = reference.Contains('C');

        if (reference[0] == 'S')
            return new Complex(hasR ? R : 0.0,
                               (hasL ? w * L : 0.0) - (hasC ? 1.0 / (w * C) : 0.0));

        var y = new Complex(hasR ? 1.0 / R : 0.0,
                            (hasC ? w * C : 0.0) - (hasL ? 1.0 / (w * L) : 0.0));
        return Complex.One / y;
    }

    /// <summary>
    /// Every one of the six presents exactly the impedance of the elements it carries — measured as
    /// a shunt one-port, against the textbook reflection formula (Z − Z0)/(Z + Z0).
    /// </summary>
    [Theory]
    [InlineData("SRL")]
    [InlineData("SRC")]
    [InlineData("SLC")]
    [InlineData("PRL")]
    [InlineData("PRC")]
    [InlineData("PLC")]
    public void EachTwoElementPart_PresentsItsClosedFormImpedance(string reference)
    {
        const double F = 1.7e9;
        double w = 2 * Math.PI * F;

        var ds = Run($"Port:P1  n1 0  Num=1 Z=50 Ohm\n{reference}:X1  n1 0  {Values(reference)}\n", [F]);

        var z = ClosedFormZ(reference, w);
        var expected = (z - Z0) / (z + Z0);
        Assert.True((S11(ds) - expected).Magnitude < 1e-9,
            $"{reference}: S11={S11(ds):G6}, expected {expected:G6}");
    }

    /// <summary>
    /// What each one is at DC, which is the case the closed form above cannot reach (ω = 0 divides)
    /// and the one every HB run touches on its DC harmonic.
    /// </summary>
    /// <remarks>
    /// <b>SRL is the row worth having.</b> A series branch with no capacitance must NOT open at DC —
    /// it is R, exactly as the physical part measures — and an implementation that reached for the
    /// SRLC's open-at-DC arm because "a series RLC opens at DC" would be wrong in a way that only
    /// shows up on a bias solve. PRC is its dual: no inductor to short it, so it is R as well.
    /// </remarks>
    [Theory]
    [InlineData("SRL", "R")]
    [InlineData("SRC", "open")]
    [InlineData("SLC", "open")]
    [InlineData("PRL", "short")]
    [InlineData("PRC", "R")]
    [InlineData("PLC", "short")]
    public void AtDc_EachTwoElementPart_IsWhatItsElementsMake(string reference, string expectation)
    {
        var ds = Run($"Port:P1  n1 0  Num=1 Z=50 Ohm\n{reference}:X1  n1 0  {Values(reference)}\n", [0.0]);

        Complex expected = expectation switch
        {
            "open"  => Complex.One,
            "short" => -Complex.One,
            _       => new Complex((R - Z0) / (R + Z0), 0.0),
        };

        Assert.True((S11(ds) - expected).Magnitude < 1e-9,
            $"{reference} at DC: S11={S11(ds):G6}, expected {expected:G6} ({expectation})");
    }

    /// <summary>
    /// A Mutual couples through an SRL's inductor exactly as it does through a plain <c>L</c>
    /// carrying the same optional <c>R=</c>.
    /// </summary>
    /// <remarks>
    /// <b>Here the equivalence IS the claim</b>, which is why it is asserted against another
    /// circuitRF path rather than a closed form: an SRL is a plain inductor's branch with the
    /// resistance moved onto the same diagonal, and what this pins is that the −jωM off-diagonal
    /// still lands on that branch. A model that reported the wrong branch index leaves S21 at zero
    /// while every other number stays plausible.
    /// </remarks>
    [Fact]
    public void AMutual_CouplesThroughAnSrlsInductor_AsThroughAPlainL()
    {
        double[] fs = [1e8, 1.1e9, 6e9];
        const string ports = "Port:P1  n1 0  Num=1 Z=50 Ohm\nPort:P2  n2 0  Num=2 Z=50 Ohm\n";
        const string coupling = "Mutual:M12  M=3 nH  Inductor1=\"X1\"  Inductor2=\"X2\"\n";

        var srl = Run(ports + "SRL:X1  n1 0  R=2 Ohm L=8 nH\nSRL:X2  n2 0  R=2 Ohm L=8 nH\n" + coupling, fs);
        var ind = Run(ports + "L:X1    n1 0  R=2 Ohm L=8 nH\nL:X2    n2 0  R=2 Ohm L=8 nH\n" + coupling, fs);

        for (int f = 0; f < fs.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((((Complex)srl["S"][f, i, j]) - ((Complex)ind["S"][f, i, j])).Magnitude < 1e-12,
                        $"S{i + 1}{j + 1} at f[{f}] differs: {srl["S"][f, i, j]} vs {ind["S"][f, i, j]}");
    }

    /// <summary>
    /// The two members with no inductor refuse a Mutual that names them, and the refusal says what
    /// the part actually is.
    /// </summary>
    /// <remarks>
    /// A silent coupling here would stamp −jωM onto a diagonal that is not an inductance — an SRC's
    /// R and C, or nothing at all in a PRC's case, since it allocates no branch. Both give a number
    /// that solves.
    /// </remarks>
    [Theory]
    [InlineData("SRC", "R=2 Ohm C=4 pF")]
    [InlineData("PRC", "R=2 Ohm C=4 pF")]
    public void AMutual_NamingAPartWithNoInductor_IsRefusedByName(string reference, string values)
    {
        string cnl = "Port:P1  n1 0  Num=1 Z=50 Ohm\nPort:P2  n2 0  Num=2 Z=50 Ohm\n"
                   + $"{reference}:X1  n1 0  {values}\nL:X2  n2 0  L=8 nH\n"
                   + "Mutual:M12  M=3 nH  Inductor1=\"X1\"  Inductor2=\"X2\"\n";

        var ex = Assert.ThrowsAny<Exception>(() => Run(cnl, [1.1e9]));
        Assert.Contains(reference, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no inductor branch", ex.Message, StringComparison.Ordinal);
    }
}
