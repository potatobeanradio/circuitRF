using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>brief-trn-5's engine gates: the transient-versus-HB consistency gate (docs/design/transient.md §12.2) read through
/// <c>fft</c>, and the steering line (§11).</summary>
public sealed class TransientMeasurementTests(ITestOutputHelper output)
{
    /// <summary>A weakly nonlinear amplifier: a built-in FET law between bias chokes and coupling capacitors, an L-section to
    /// the load, driven by a 1 GHz available-power source.</summary>
    private const string Amplifier = """
        P1Tone:SRC in 0 Num=1 Pavl=-6 dBm Z=50 Freq=1 GHz
        C:CIN in g C=5p
        L:LG gb g L=20n
        Vdc:VG gb 0 Vdc=-0.8
        FET_Angelov:Q1 g d 0 Ipk=0.08 Vpk=-0.8 P1=1.2 Alpha=2 Lambda=0.02 Cgs=0.4p Cgd=0.04p
        L:LD db d L=20n
        Vdc:VD db 0 Vdc=5
        C:COUT d m C=5p
        L:LM m out L=3n
        C:CM out 0 C=1p
        R:RL out 0 R=50
        """;

    /// <summary>Gate 2 — the amplifier run to steady state, its last period through <c>fft</c>: harmonics 1–5 of the output
    /// equal the HB solution within 0.01 dB and 0.1°.</summary>
    [Fact]
    public void Gate2_TheLastPeriodsFftEqualsHarmonicBalance()
    {
        var (lib, tb) = new CnlReader().Read(Amplifier + "\nanalysis HB1 type=hb Tone=1e9 MaxHarm=16 Tol=1e-9\n");
        var nl = new Elaborator(lib).Elaborate(tb);
        var hb = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));
        Assert.Equal(1.0, hb["Converged"].RealValues[0]);

        var (lib2, tb2) = new CnlReader().Read(Amplifier + "\nanalysis TR1 type=tran Stop=30n Step=1.953125p MaxStep=1.953125p RelTol=1e-6\n");
        var nl2 = new Elaborator(lib2).Elaborate(tb2);
        var tr = TransientRunner.Run(TransientRunner.Resolve(tb2.Analyses.OfType<TransientAnalysis>().Single(), nl2.ResolvedGlobals), nl2);

        var ev = new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["HB1"] = hb, ["TR1"] = tr }));
        var h = ev.Eval("HB1.V(\"out\")", new Scope("m")).AsCube().ComplexValues;
        var x = ev.Eval("fft(TR1.V(\"out\"), 1GHz)", new Scope("m")).AsCube().ComplexValues;
        double worstDb = 0, worstDeg = 0;
        for (int k = 1; k <= 5; k++)
        {
            double db = 20 * Math.Log10(x[k].Magnitude / h[k].Magnitude);
            double deg = Math.IEEERemainder((x[k].Phase - h[k].Phase) * 180 / Math.PI, 360);
            output.WriteLine($"k={k}: HB {20 * Math.Log10(h[k].Magnitude):F3} dBV {h[k].Phase * 180 / Math.PI:F3}°, " +
                             $"transient {20 * Math.Log10(x[k].Magnitude):F3} dBV {x[k].Phase * 180 / Math.PI:F3}°: {db:E2} dB, {deg:E2}°");
            worstDb = Math.Max(worstDb, Math.Abs(db));
            worstDeg = Math.Max(worstDeg, Math.Abs(deg));
        }
        output.WriteLine($"accepted {tr["Tran.AcceptedSteps"].RealValues[0]}, rejected {tr["Tran.RejectedSteps"].RealValues[0]}");
        Assert.True(worstDb <= 0.01, $"worst harmonic differs by {worstDb:F4} dB");
        Assert.True(worstDeg <= 0.1, $"worst harmonic differs by {worstDeg:F4}°");
    }

    /// <summary>R-trn5-5 — the steering line names envelope when a source is modulated, harmonic balance when it is not, and
    /// says nothing for a run short of 10⁵ carrier periods.</summary>
    [Fact]
    public void Steering_NamesEnvelopeForAModulatedSource()
    {
        static string? Steer(string source, double stop)
        {
            var (lib, tb) = new CnlReader().Read(source + "\nR:R1 a 0 R=50\nanalysis TR1 type=tran Stop=1u\n");
            return TransientSources.Steering(new Elaborator(lib).Elaborate(tb), stop);
        }
        string? fm = Steer("V_SFFM:Vs a 0 Offset=0 Amplitude=1 Carrier=2.4e9 ModIndex=2 Signal=1e6", 1e-3);
        string? tone = Steer("V_1Tone:Vs a 0 Freq=2.4 GHz V=1", 1e-3);
        output.WriteLine(fm);
        Assert.Contains("an envelope analysis", fm);
        Assert.Contains("harmonic balance", tone);
        Assert.Null(Steer("V_1Tone:Vs a 0 Freq=2.4 GHz V=1", 1e-6));
    }
}
