using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The ideal filter and the duplexer in harmonic balance (brief-sys-6): a two-tone signal with one
/// tone in the passband and one out of it comes through at exactly the two levels the RESPONSE
/// states, and the block <b>creates nothing</b>.
///
/// <para><b>Why this is a different gate from the S-parameter one.</b> The HB linear extractor
/// stamps the block once per harmonic <c>ω = k·ω₀</c>, DC included, and this is the first component
/// in the family whose S is a different complex number at every one of them. A rejection that was
/// evaluated at the wrong ω — the fundamental's, say, or the mixing product's index rather than its
/// frequency — would still show two tones at two different levels, and only a rejection compared
/// against the response AT THAT FREQUENCY can catch it.</para>
///
/// <para>Every netlist here is entirely linear, deliberately: there is no nonlinear device anywhere
/// in the circuit, so a mixing product holding anything at all could only have come from the stamp
/// having become drive-dependent.</para>
/// </summary>
public class FilterHbTests(ITestOutputHelper output)
{
    // One tone inside a 0.9–1.1 GHz passband and one well outside it. Widely separated so the
    // out-of-band tone sits where the response is deep, and so no low-order mixing product of the
    // two lands on either.
    private const double InBand = 1.00e9, OutOfBand = 1.60e9;

    private const string Source =
        "PnTone:Ps  a 0  Freq[1]=1.00e9 Pavl[1]=0 Phase[1]=0 Freq[2]=1.60e9 Pavl[2]=0 Phase[2]=0 Z=50";

    private const string Analysis =
        "analysis HB1 type=hb NumFreqs=2 Tone[1]=1.00e9 Tone[2]=1.60e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-12";

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        Assert.True(p.IsMultiTone, "the gate must resolve to a two-tone HB");
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    /// <summary>
    /// Power into the named 50 Ω load, in dBm, from the VOLTAGE there — the same reasoning
    /// <c>SystemBlockHbTests</c> gives: these are linear-only nodes, so the HB cube carries no
    /// nonlinear terminal current at one, and <c>|V|²/2R</c> on the peak-phasor convention is what
    /// the load actually receives.
    /// </summary>
    private static double PDbm(DataSet ds, string net, int k1, int k2)
    {
        double v = TwoToneMeasurements.Tone(ds, 0, net, k1, k2).Magnitude;
        double w = v * v / (2.0 * 50.0);
        return w > 0 ? 10.0 * Math.Log10(w) + 30.0 : double.NegativeInfinity;
    }

    /// <summary>
    /// The gain a matched two-port of transmission <c>S21</c> delivers to a matched load, in dB:
    /// just <c>20·log10|S21|</c>, and the source's 0 dBm available power arrives as that. Computed
    /// from the model rather than read back from the run.
    /// </summary>
    private static double ExpectedDbm(FilterModel m, double freqHz)
        => 20.0 * Math.Log10(m.SAt(2 * Math.PI * freqHz)[1, 0].Magnitude);

    private void AssertCreatesNothing(DataSet ds, string net)
    {
        double carrier = TwoToneMeasurements.Tone(ds, 0, net, 1, 0).Magnitude;
        foreach (var (k1, k2) in new[] { (2, -1), (-1, 2), (3, -2), (-2, 3),
                                         (1, 1), (2, 0), (0, 2), (1, -1), (0, 0) })
        {
            double v = TwoToneMeasurements.Tone(ds, 0, net, k1, k2).Magnitude;
            output.WriteLine($"{net} ({k1},{k2}) = {v:E3} V against a {carrier:E3} V carrier");
            Assert.True(v < 1e-9 * carrier,
                        $"{net} ({k1},{k2}) holds {v:E3} V — an ideal filter creates nothing");
        }
    }

    [Theory]
    [InlineData("Chebyshev", 3)]
    [InlineData("Butterworth", 5)]
    [InlineData("Elliptic", 5)]
    public void ABandpassFilterPassesTheInBandToneAndRejectsTheOther_AtTheLevelItsResponseStates(
        string response, int order)
    {
        var ds = Run($@"
{Source}
Filter:F1  a 0  b 0  Response={response} Form=Bandpass Order={order} \
  F1=0.9 GHz F2=1.1 GHz Ripple=0.1 Astop=40 Zin=50 Ohm Zout=50 Ohm IL=0
R:RL  b 0  R=50
{Analysis}
");
        var model = new FilterModel(Enum.Parse<FilterResponse>(response), NetworkForm.Bandpass, order,
                                    fcHz: 1e9, f1Hz: 0.9e9, f2Hz: 1.1e9,
                                    rippleDb: 0.1, astopDb: 40.0, zIn: 50, zOut: 50, ilDb: 0);

        double passed   = PDbm(ds, "b", 1, 0);
        double rejected = PDbm(ds, "b", 0, 1);

        output.WriteLine($"{response} order {order}: in-band tone {passed:F6} dBm " +
                         $"(response says {ExpectedDbm(model, InBand):F6}), " +
                         $"out-of-band tone {rejected:F6} dBm " +
                         $"(response says {ExpectedDbm(model, OutOfBand):F6})");

        // 0 dBm available in, so the level out IS the response in dB — and it is the response AT
        // THAT FREQUENCY, which is what makes this a real gate on the per-harmonic stamp.
        Assert.Equal(ExpectedDbm(model, InBand),    passed,   6);
        Assert.Equal(ExpectedDbm(model, OutOfBand), rejected, 6);

        // The rejection is real rejection, not a small number that happens to be small.
        Assert.True(rejected < passed - 20.0,
            $"the out-of-band tone came through at {rejected:F2} dBm against {passed:F2} dBm in band");

        AssertCreatesNothing(ds, "b");
    }

    /// <summary>
    /// The same run through a HIGHPASS, where the roles of the two tones swap. A response evaluated
    /// at the wrong sign of ω, or at |ω| when it should not be, would pass this on a bandpass — whose
    /// response is symmetric about its centre — and fail here.
    /// </summary>
    [Fact]
    public void AHighpassPassesTheUpperToneAndRejectsTheLower()
    {
        var ds = Run($@"
{Source}
Filter:F1  a 0  b 0  Response=Butterworth Form=Highpass Order=7 Fc=1.5 GHz Zin=50 Ohm Zout=50 Ohm
R:RL  b 0  R=50
{Analysis}
");
        var model = new FilterModel(FilterResponse.Butterworth, NetworkForm.Highpass, 7,
                                    fcHz: 1.5e9, f1Hz: 0, f2Hz: 0, rippleDb: 0.1, astopDb: 40,
                                    zIn: 50, zOut: 50, ilDb: 0);

        output.WriteLine($"highpass: 1.0 GHz tone {PDbm(ds, "b", 1, 0):F6} dBm, " +
                         $"1.6 GHz tone {PDbm(ds, "b", 0, 1):F6} dBm");
        Assert.Equal(ExpectedDbm(model, InBand),    PDbm(ds, "b", 1, 0), 6);   // 1.0 GHz: rejected
        Assert.Equal(ExpectedDbm(model, OutOfBand), PDbm(ds, "b", 0, 1), 6);   // 1.6 GHz: passed
        Assert.True(PDbm(ds, "b", 1, 0) < PDbm(ds, "b", 0, 1) - 20.0);

        AssertCreatesNothing(ds, "b");
    }

    /// <summary>
    /// A lowpass carries the DC operating point through as a wire, which is the harmonic the linear
    /// extractor stamps at <c>ω = 0</c> — the degenerate case with no Y matrix. A run that could not
    /// assemble it would not converge at all.
    /// </summary>
    [Fact]
    public void ALowpassSolvesWithADcSourceThroughIt()
    {
        var ds = Run($@"
{Source}
Vdc:Vb  a 0  V=1.5
Filter:F1  a 0  b 0  Response=Butterworth Form=Lowpass Order=4 Fc=1.3 GHz
R:RL  b 0  R=50
{Analysis}
");
        // The filter is an exact through at DC, so the whole 1.5 V stands across the load — the
        // ideal source pins node a, and the through carries it.
        double dc = TwoToneMeasurements.Tone(ds, 0, "b", 0, 0).Magnitude;
        output.WriteLine($"DC at the load: {dc:F12} V");
        Assert.Equal(1.5, dc, 9);
    }

    /// <summary>
    /// The duplexer in HB: the TX-band tone comes out of the TX port and the RX-band tone out of the
    /// RX port, each at the level the whole three-port S states — and neither port creates anything.
    /// </summary>
    [Fact]
    public void ADuplexerRoutesEachToneToItsOwnArm()
    {
        const string TwoBandSource =
            "PnTone:Ps  a 0  Freq[1]=0.95e9 Pavl[1]=0 Phase[1]=0 Freq[2]=1.15e9 Pavl[2]=0 Phase[2]=0 Z=50";
        const string TwoBandAnalysis =
            "analysis HB1 type=hb NumFreqs=2 Tone[1]=0.95e9 Tone[2]=1.15e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-12";

        var ds = Run($@"
{TwoBandSource}
Duplexer:D1  a 0  b 0  c 0  Zant=50 Ohm \
  TxResponse=Chebyshev TxForm=Bandpass TxOrder=5 TxF1=0.9 GHz TxF2=1.0 GHz TxRipple=0.1 \
  RxResponse=Chebyshev RxForm=Bandpass RxOrder=5 RxF1=1.1 GHz RxF2=1.2 GHz RxRipple=0.1
R:RTX  b 0  R=50
R:RRX  c 0  R=50
{TwoBandAnalysis}
");
        double txIn  = PDbm(ds, "b", 1, 0), txOut = PDbm(ds, "b", 0, 1);
        double rxOut = PDbm(ds, "c", 1, 0), rxIn  = PDbm(ds, "c", 0, 1);

        output.WriteLine($"TX port: {txIn:F3} dBm at 0.95 GHz, {txOut:F3} dBm at 1.15 GHz");
        output.WriteLine($"RX port: {rxOut:F3} dBm at 0.95 GHz, {rxIn:F3} dBm at 1.15 GHz");

        // Each tone arrives at its own arm essentially whole, and at the other arm far down. The
        // separation is the isolation, which is a consequence of the two responses and the junction
        // rather than a parameter — so it is measured here rather than compared with a number.
        Assert.True(txIn  > -1.5, $"the TX-band tone reached the TX port at only {txIn:F2} dBm");
        Assert.True(rxIn  > -1.5, $"the RX-band tone reached the RX port at only {rxIn:F2} dBm");
        Assert.True(txOut < txIn - 40.0, $"TX port isolation is only {txIn - txOut:F1} dB");
        Assert.True(rxOut < rxIn - 40.0, $"RX port isolation is only {rxIn - rxOut:F1} dB");

        AssertCreatesNothing(ds, "b");
        AssertCreatesNothing(ds, "c");
    }
}
