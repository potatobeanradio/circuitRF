// ================================================================
//  HbOrderAxisTests.cs
//  Gate test for brief-hb-spectrum-2-order-axis — Part A
//
//  1. SingleTone_HarmonicAxis_IsOrders: the V cube's "harmonic" axis carries
//     integer orders [0,1,…,K] with unit "" (not Hz values like k*f0).
// ================================================================

using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class HbOrderAxisTests(ITestOutputHelper output)
{
    private const string SingleToneCnl = @"
TV0 = 3.5
B   = 0.02
Sc  = 0.3
Vgg = -3.0
Vdd = 28.0

SDD:M1  n_gate 0  n_drain 0  Ports=2
  I[1,0]=0
  I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)

V_1Tone:Vs  n_gbias 0  Vdc=Vgg  Freq=2e9  V=0.1  Phase=0
L:Lbias_g   n_gbias n_gate  L=1  R=0

V:Vdd        n_dbias 0  V=Vdd
L:Lbias_d   n_dbias n_drain  L=1  R=0

R:Rload  n_drain 0  R=50

analysis HB1  type=hb  Tone=2e9  MaxHarm=4  Tol=1e-4
";

    // ── 1. SingleTone_HarmonicAxis_IsOrders ──────────────────────────────────
    // After stage 2 the single-tone HB "harmonic" axis must store integer orders
    // [0, 1, 2, …, MaxHarm] with unit "" — never Hz values (k * f0).

    [Fact]
    public void SingleTone_HarmonicAxis_IsOrders()
    {
        var (lib, tb) = new CnlReader().Read(SingleToneCnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        var ds  = (DataSet)new HbEngine(nl, tb).Run(p);

        var vCube = ds["V"];
        var harmAxis = vCube.Axes.First(a => a.Name == "harmonic");

        output.WriteLine($"harmonic axis: unit='{harmAxis.Unit}' values=[{string.Join(", ", harmAxis.Values)}]");

        // Unit must be "" (no longer a frequency axis)
        Assert.Equal("", harmAxis.Unit ?? "");

        // Values must be integer orders 0, 1, 2, 3, 4 (MaxHarm=4 → K1=5 harmonics)
        Assert.Equal(5, harmAxis.Length);
        for (int k = 0; k < harmAxis.Length; k++)
            Assert.Equal((double)k, harmAxis.Values[k], precision: 10);

        output.WriteLine("PASS: SingleTone_HarmonicAxis_IsOrders");
    }
}
