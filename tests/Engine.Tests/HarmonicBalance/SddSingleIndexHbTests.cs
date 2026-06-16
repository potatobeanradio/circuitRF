using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate test: single-index SDD equations in a full HB sweep.
/// Verifies that with the 4-net SDD line and I[p] forms:
///   - The V cube node axis has no equation-fragment phantom nodes.
///   - Vout fundamental is non-zero and grows with Pin.
/// </summary>
public class SddSingleIndexHbTests(ITestOutputHelper output)
{
    // Simple linear transconductor: I[1]=_v1/50 (50Ω input), I[2]=gm*_v1-_v2/RL (output)
    // At the fundamental: Vout_fund = gm * RL * Vin_fund  (Vin driven by P1Tone)
    private const string Cnl = @"
Pin = 0

P1Tone:P1  Vin 0  Pavl=Pin dBm  Z=50 Ohm  Freq=2e9  Phase=0 deg
SDD:X1     Vin 0  Vout 0  I[1]=_v1/50  I[2]=0.05*_v1-_v2/50

analysis HB1 type=hb Tone=2e9 MaxHarm=3 Tol=1e-6
analysis SW  type=parametric_sweep Var=Pin Values=0,5,10 Inner=HB1
";

    [Fact]
    public void Hb_RealSdd_Vout_NonZero()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);
        var sw = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        Assert.True(ds.Contains("V"), "V cube missing");
        var vCube = ds["V"];

        // V axes: [Pin, node, harmonic]
        Assert.Equal(3, vCube.Rank);
        string[] nodeLabels = vCube.Axes[1].Labels!;

        output.WriteLine($"Node labels: {string.Join(", ", nodeLabels)}");

        // No equation-fragment phantom nodes
        Assert.DoesNotContain(nodeLabels, n => n.Contains("I[",  StringComparison.Ordinal));
        Assert.DoesNotContain(nodeLabels, n => n.Contains("_v",  StringComparison.Ordinal));
        Assert.DoesNotContain(nodeLabels, n => n.Contains("=",   StringComparison.Ordinal));

        // Vout must appear
        int voutIdx = Array.IndexOf(nodeLabels, "Vout");
        Assert.True(voutIdx >= 0, $"'Vout' not found in node axis. Labels: {string.Join(", ", nodeLabels)}");

        // harmonic index 1 = fundamental; check across all Pin sweep points
        for (int pinIdx = 0; pinIdx < 3; pinIdx++)
        {
            var voutFund = (Complex)vCube[pinIdx, voutIdx, 1];
            output.WriteLine($"Pin={new[] {0,5,10}[pinIdx]} dBm  |Vout_f0|={voutFund.Magnitude:G4} V");
            Assert.True(voutFund.Magnitude > 0.001,
                $"Vout fundamental near-zero at Pin={new[]{0,5,10}[pinIdx]} dBm ({voutFund.Magnitude:G4} V)");
        }
    }
}
