using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// A COMPLEX reference impedance on an ideal system block, end to end.
///
/// <para><b>The bug this file exists for.</b> Every creator in <c>ComponentModelFactory</c> reads its
/// numbers through a local helper that accepts <c>ValueKind.Real</c> and falls back to the default on
/// anything else. <c>Zin = 5 + j100</c> resolves to a perfectly good <c>Complex</c> Value, missed
/// that test, and the filter was built at <b>50 Ω</b> with nothing said — the only symptom being a
/// response that did not look like the one asked for. Two things are gated here: that a complex
/// reference impedance now reaches the stamp, and that a component which CANNOT read one says so
/// instead of quietly using its default.</para>
///
/// <para><b>The convention, which is the half a user actually has to get right.</b> The stamp is
/// Kurokawa power waves, the same definition <c>SParameterEngine</c> extracts S with, so
/// <c>S_pp = 0</c> is <c>Z_seen = conj(Z0_p)</c> — reference impedance and PRESENTED impedance
/// differ by a conjugate, and the parameter NAME says which one it is. <c>Zin</c>/<c>Zout</c> name
/// what the port PRESENTS (so does the duplexer's <c>Zant</c>/<c>TxZ</c>/<c>RxZ</c>); <c>Z0</c> is
/// the reference. A filter at <c>Zin = 5 + j100</c> therefore presents <c>5 + j100</c> and is
/// conjugate-matched — maximum power transfer — by a <c>Term</c> at <c>Z = 5 − j100</c>. Both halves
/// are measured: the impedance the port presents, directly, and the pair of terminations, so nothing
/// here can pass by being asserted only against itself.</para>
/// </summary>
public class ComplexReferenceImpedanceTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var c  = SParameterEngine.Run(nl, [freqHz])["S"];
        int n  = c.Axes[1].Length;

        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            s[i, j] = (Complex)c[0, i, j];
        return s;
    }

    /// <summary>A `Z=a+jb Ohm` Term line — the unit is a row field, so the expression carries none.</summary>
    private static string Term(int num, string net, double r, double x)
        => $"Port:P{num}  {net} 0  Num={num}  Z={N(r)}{(x < 0 ? "-" : "+")}j{N(Math.Abs(x))} Ohm";

    private static string Chebyshev3(string zIn, string zOut, double ilDb = 0.0) => $@"
Filter:F1  n1 0 n2 0  Response=Chebyshev Form=Bandpass Order=3 \
  F1=0.9 GHz F2=1.1 GHz Ripple=0.1 Zin={zIn} Ohm Zout={zOut} Ohm IL={N(ilDb)}";

    // ══ It reaches the stamp ══════════════════════════════════════════════════

    /// <summary>
    /// The whole reported bug in one assertion: a filter told <c>Zin = 5 + j100</c>, measured against
    /// terms at the CONJUGATE — which is what conjugate-matches a port presenting 5 + j100 — returns
    /// the PROTOTYPE's own S, which is what a 50 Ω filter in a 50 Ω system returns. Before the fix
    /// the model was built at 50 Ω, so the measured S was the S of a 50 Ω filter in a 5 − j100
    /// system: a gross mismatch across the whole band.
    /// </summary>
    [Fact]
    public void AComplexZinReachesTheStampInsteadOfSilentlyReadingFiftyOhms()
    {
        var reference = new FilterModel(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3,
                                        0, 0.9e9, 1.1e9, 0.1, 40.0,
                                        new Complex(5, 100), new Complex(5, 100), 0.0);

        foreach (double f in (double[])[0.85e9, 0.95e9, 1.0e9, 1.05e9, 1.2e9])
        {
            var expected = reference.SAt(2 * Math.PI * f);
            var measured = SAt(Term(1, "n1", 5, -100) + "\n" + Term(2, "n2", 5, -100)
                             + Chebyshev3("5+j100", "5+j100"), f);

            for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                Assert.True((expected[p, q] - measured[p, q]).Magnitude < 1e-12,
                    $"{f / 1e9:F2} GHz S{p + 1}{q + 1}: expected {expected[p, q]}, got {measured[p, q]}");
        }
    }

    /// <summary>
    /// The convention itself, measured with a real 50 Ω probe so the answer depends on no convention
    /// at all: <c>Zin</c> is the impedance port 1 PRESENTS. This is the assertion that decides which
    /// way round the whole thing goes, and it is a direct measurement of <c>V/I</c> rather than a
    /// comparison of the model with its own definition.
    /// </summary>
    [Theory]
    [InlineData(5,  100)]
    [InlineData(5, -100)]
    [InlineData(20, -35)]
    public void ZinIsTheImpedanceThePortPresents(double r, double x)
    {
        string z = $"{N(r)}{(x < 0 ? "-" : "+")}j{N(Math.Abs(x))}";

        // Port 2 terminated in the CONJUGATE of the filter's Zout, so port 2 is matched and port 1's
        // measured reflection is the filter's own. Port 1 probed at a real 50 Ω.
        var s = SAt(Term(1, "n1", 50, 0) + "\n" + Term(2, "n2", r, -x)
                  + Chebyshev3(z, z), 1.0e9);

        var seen = 50.0 * (Complex.One + s[0, 0]) / (Complex.One - s[0, 0]);
        output.WriteLine($"Zin = {z} -> port 1 presents {seen}");

        // Loose: this is a 0.1 dB Chebyshev at band centre, not a lossless matched line, so its own
        // passband ripple moves the presented impedance by a fraction of an ohm. The SIGN of the
        // reactance is the whole point and is not a close call.
        Assert.True(Math.Abs(seen.Real - r) < 1.0,      $"presented R = {seen.Real:G6}, stated {r}");
        Assert.True(Math.Abs(seen.Imaginary - x) < 1.0, $"presented X = {seen.Imaginary:G6}, stated {x}");
    }

    /// <summary>
    /// The CONJUGATE termination matches and the equal one does not — the pair, because either
    /// number alone would pass on a convention that ignored the sign of the reactance.
    /// </summary>
    [Fact]
    public void TheConjugateTerminationMatchesAndTheEqualOneDoesNot()
    {
        double f = 1.0e9;

        var conjugate = SAt(Term(1, "n1", 5, -100) + "\n" + Term(2, "n2", 5, -100)
                          + Chebyshev3("5+j100", "5+j100"), f);
        var equal     = SAt(Term(1, "n1", 5,  100) + "\n" + Term(2, "n2", 5,  100)
                          + Chebyshev3("5+j100", "5+j100"), f);

        output.WriteLine($"|S11| conjugate {conjugate[0, 0].Magnitude:G6}, equal {equal[0, 0].Magnitude:G6}");

        // 0.05 rather than 0: a 0.1 dB Chebyshev's own passband |S11| is ~0.023 here, and that is
        // the response it is supposed to have. The point of the pair is the RATIO, not either number.
        Assert.True(conjugate[0, 0].Magnitude < 0.05, $"|S11| at the conjugate was {conjugate[0, 0].Magnitude:G6}");
        Assert.True(equal[0, 0].Magnitude     > 0.9,  $"|S11| at the equal Z was {equal[0, 0].Magnitude:G6}");
    }

    /// <summary>
    /// The stamp stays LOSSLESS with a complex reference, which is the gate that would catch a
    /// dropped conjugate or a <c>√Z0</c> written where <c>√Re(Z0)</c> belongs — both of which leave
    /// the passband looking plausible and put power in or take it out.
    /// </summary>
    [Fact]
    public void AComplexReferencedFilterIsStillMeasuredLossless()
    {
        for (int k = 1; k <= 30; k++)
        {
            double f = k * 0.06e9;
            var s = SAt(Term(1, "n1", 20, 35) + "\n" + Term(2, "n2", 20, 35)
                      + Chebyshev3("20-j35", "20-j35"), f);

            double power = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
            Assert.True(Math.Abs(power - 1.0) < 1e-12,
                $"{f / 1e9:F2} GHz: |S11|² + |S21|² = {power:G17}");
        }
    }

    /// <summary>
    /// <c>Zout</c> is respected too, and independently of <c>Zin</c> — the owner's own suspicion. A
    /// filter complex at ONE end is an impedance transformer, so it is measured in the uniform 50 Ω
    /// system its own port 1 is stated against and the mismatch is expected at port 2 only.
    /// </summary>
    [Fact]
    public void ZoutIsReadSeparatelyFromZin()
    {
        double f = 1.0e9;
        var s = SAt(Term(1, "n1", 50, 0) + "\n" + Term(2, "n2", 50, 0)
                  + Chebyshev3("50", "10+j40"), f);

        var reference = new FilterModel(FilterResponse.Chebyshev, NetworkForm.Bandpass, 3,
                                        0, 0.9e9, 1.1e9, 0.1, 40.0,
                                        new Complex(50, 0), new Complex(10, 40), 0.0);
        var own = reference.SAt(2 * Math.PI * f);

        output.WriteLine($"in its own reference S11={own[0, 0]} S22={own[1, 1]}");
        output.WriteLine($"measured at 50 ohm    S11={s[0, 0]} S22={s[1, 1]}");

        // Its own S11 is the passband's ~0; measured in a uniform 50 ohm system port 2 is not.
        Assert.True(own[0, 0].Magnitude < 0.05);
        Assert.True(s[1, 1].Magnitude   > 0.5,
            $"a 50 to 10+j40 transformer should look mismatched at port 2 in a 50 ohm system, |S22| = {s[1, 1].Magnitude:G6}");
    }

    /// <summary>
    /// <c>IL</c> is a straight 1:1 loss on <c>S21</c> with a COMPLEX pair too, once the block is
    /// conjugate-matched at both ends — the owner's own check, and the one that would catch an
    /// <c>IL</c> applied in the wrong frame or a <c>√Re(Z0)</c> normalisation that does not cancel.
    ///
    /// <para>Measured as a RATIO against the same filter at <c>IL = 0</c>, at the same frequency,
    /// rather than against 1: a Chebyshev's own passband <c>|S21|</c> ripples, and that ripple is the
    /// response it is supposed to have. The ratio is what <c>IL</c> claims to be.</para>
    /// </summary>
    [Theory]
    [InlineData(0.5)] [InlineData(1.0)] [InlineData(3.0)] [InlineData(10.0)]
    public void InsertionLossIsAStraightOneToOneLossOnS21WithAComplexPairToo(double ilDb)
    {
        // Zin = 5+j100 presents 5+j100, so the conjugate-matched Term is 5-j100. Zout = 20-j35
        // likewise, and the pair is deliberately UNEQUAL so nothing here can pass by symmetry.
        string terms = Term(1, "n1", 5, -100) + "\n" + Term(2, "n2", 20, 35);
        double want  = Math.Pow(10.0, -ilDb / 20.0);

        foreach (double f in (double[])[0.92e9, 0.95e9, 1.0e9, 1.05e9, 1.08e9])
        {
            var lossless = SAt(terms + Chebyshev3("5+j100", "20-j35"), f);
            var lossy    = SAt(terms + Chebyshev3("5+j100", "20-j35", ilDb), f);

            double ratio = lossy[1, 0].Magnitude / lossless[1, 0].Magnitude;
            output.WriteLine($"{f / 1e9:F2} GHz  IL={ilDb} dB: |S21| {lossless[1, 0].Magnitude:G6} -> "
                           + $"{lossy[1, 0].Magnitude:G6}, ratio {ratio:G10} ({20 * Math.Log10(ratio):F6} dB)");

            Assert.True(Math.Abs(ratio - want) < 1e-12,
                $"{f / 1e9:F2} GHz: |S21| fell by {-20 * Math.Log10(ratio):G8} dB, not {ilDb}");
        }
    }

    /// <summary>
    /// And <c>IL</c> DISSIPATES rather than reflecting what it loses: <c>S11</c> is untouched by it,
    /// which is what a real filter's loss does and what makes the 1:1 claim above meaningful.
    /// </summary>
    [Fact]
    public void InsertionLossLeavesTheMatchAloneWithAComplexPair()
    {
        string terms = Term(1, "n1", 5, -100) + "\n" + Term(2, "n2", 20, 35);

        var lossless = SAt(terms + Chebyshev3("5+j100", "20-j35"), 1.0e9);
        var lossy    = SAt(terms + Chebyshev3("5+j100", "20-j35", 3.0), 1.0e9);

        Assert.True((lossless[0, 0] - lossy[0, 0]).Magnitude < 1e-12,
            $"S11 moved from {lossless[0, 0]} to {lossy[0, 0]} when IL was added");
    }

    // ══ The components that cannot read one say so ════════════════════════════

    /// <summary>
    /// A complex value on a parameter that is only ever read as a real number is REFUSED, naming the
    /// parameter — the alternative being the shipped behaviour, which was to build the component at
    /// its default and report nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Mixer",      "Zrf")]
    [InlineData("Mixer",      "Zif")]
    [InlineData("TLIN",       "Z")]
    [InlineData("Filter",     "Ripple")]
    [InlineData("Circulator", "IL")]
    [InlineData("P1Tone",     "Z")]
    public void AComplexValueOnARealOnlyParameterIsRefusedByName(string type, string param)
    {
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            [param] = new Value(new Complex(5, 100)),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate(type, parameters));

        output.WriteLine(ex.Message);
        Assert.Contains(param, ex.Message, StringComparison.Ordinal);
        Assert.Contains(type,  ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a complex value on a parameter that DOES take one is not refused — otherwise the check
    /// above would be satisfied by refusing everything.
    /// </summary>
    [Theory]
    [InlineData("Filter",     "Zin")]
    [InlineData("Filter",     "Zout")]
    [InlineData("Atten",      "Z0")]
    [InlineData("Circulator", "Z0")]
    [InlineData("Coupler",    "Z0")]
    [InlineData("Balun",      "Zbal")]
    [InlineData("Duplexer",   "Zant")]
    public void AComplexValueOnAnImpedanceParameterIsAccepted(string type, string param)
    {
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            [param] = new Value(new Complex(25, -40)),
        };

        var model = ComponentModelFactory.TryCreate(type, parameters);
        Assert.NotNull(model);
    }

    /// <summary>
    /// The NONLINEAR half of the family refuses a complex reference rather than reading its real
    /// part: both the passive-intermod overlay and the amplifier's compression are a real
    /// <c>i = f(v)</c> built from a real admittance matrix. The same amplifier stamps perfectly well
    /// while it is linear, which is what the second half of this checks.
    /// </summary>
    [Fact]
    public void ANonlinearBlockRefusesAComplexReferenceAndTheSameLinearOneDoesNot()
    {
        var withIp3 = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Zout"] = new Value(new Complex(10, 25)),
            ["IP3"]  = new Value(30.0),
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate("Amp", withIp3));
        output.WriteLine(ex.Message);
        Assert.Contains("complex port impedance", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Quoted back in the spelling the user typed it, reactance sign included - not the conjugate
        // the stamp stores.
        Assert.Contains("+ j25", ex.Message, StringComparison.Ordinal);

        // IP3 = 200 is the amplifier's own "exactly linear" spelling. Stated EXPLICITLY, because the
        // tile's default is 40 dBm rather than 200 - so a freshly placed Amp is nonlinear and a
        // complex Zin/Zout on one is refused. That is the deliberate scope line, and it is here as a
        // test rather than as a sentence in a brief.
        var linear = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Zout"] = new Value(new Complex(10, 25)),
            ["IP3"]  = new Value(200.0),
        };
        Assert.NotNull(ComponentModelFactory.TryCreate("Amp", linear));

        var withPim = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Z0"]  = new Value(new Complex(10, 25)),
            ["PIM"] = new Value(-80.0),
        };
        var pimEx = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate("Circulator", withPim));
        output.WriteLine(pimEx.Message);
        Assert.Contains("passive intermod", pimEx.Message, StringComparison.OrdinalIgnoreCase);
    }
}
