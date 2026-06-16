using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// TEMP DIAGNOSTIC (not a gate) — dumps the V cube for the user's HBTest netlist so we can
/// see whether Vout / Vout2 fundamentals are genuinely zero in the engine output or only in
/// the display layer. Delete after diagnosis.
/// </summary>
public class VoutZeroDiagTests(ITestOutputHelper output)
{
    private const string Cnl = @"
Pin = 0

SDD:X1  Vin 0  Vout 0  I[1]=_v1/50  I[2]=if(_v1+0.7>0, 0.05*(_v1+0.7)^2*tanh(0.3*_v2), 0)
C:C1  Vin  n1  C=1 mF
L:L1  n2  Vin  L=1 mH
L:L2  n3  Vout  L=1 mH
R:R2  Vout2  0  R=80 Ohm
C:C2  Vout2  Vout  C=1 mF
P1Tone:P1  n1  0  Pavl=Pin dBm  Z=50 Ohm  Freq=2 GHz  Phase=0 deg  Z[0]=1 Ohm  Z[2]=30 Ohm
Vdc:V1  n2  0  Vdc=-3.05 V
Vdc:V2  n3  0  Vdc=48 V

analysis HB1 type=hb Tone=2e9 MaxHarm=5 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0 Lambda=1 MaxIter=100
analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Values=10,11,12,13,14,15 Inner=HB1
";

    [Fact]
    public void Dump_VCube()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);

        // Run the parametric sweep exactly as the app does.
        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().First();
        var ds    = ParametricSweepEngine.Run(sweep, lib, tb);

        Assert.True(ds.Contains("V"), "no V cube");
        var v = ds["V"];

        output.WriteLine($"V cube rank={v.Rank}");
        for (int d = 0; d < v.Rank; d++)
        {
            var ax = v.Axes[d];
            string labels = ax.Labels is not null ? string.Join(",", ax.Labels) : "(none)";
            output.WriteLine($"  axis[{d}] name='{ax.Name}' len={ax.Length} unit='{ax.Unit}' labels=[{labels}]");
        }

        // Identify axis positions by name (order-independent).
        int pinDim  = v.Axes.Select((a, i) => (a, i)).First(t => t.a.Name == "Pin").i;
        int nodeDim = v.Axes.Select((a, i) => (a, i)).First(t => t.a.Name == "node").i;
        int harmDim = v.Axes.Select((a, i) => (a, i)).First(t => t.a.Name == "harmonic").i;
        output.WriteLine($"pinDim={pinDim} nodeDim={nodeDim} harmDim={harmDim}");

        var nodeAxis = v.Axes[nodeDim];
        var pinAxis  = v.Axes[pinDim];

        // For each node, print DC (h0) and fundamental (h1) at first & last Pin.
        for (int n = 0; n < nodeAxis.Length; n++)
        {
            string nodeName = nodeAxis.Labels is not null ? nodeAxis.Labels[n] : $"#{n}";
            for (int pi = 0; pi < pinAxis.Length; pi++)
            {
                var args = new object[v.Rank];
                args[pinDim]  = pi;
                args[nodeDim] = n;
                args[harmDim] = 1; // fundamental
                var fund = (Complex)v[args].ComplexValue!.Value;

                args[harmDim] = 0; // DC
                var dc = (Complex)v[args].ComplexValue!.Value;

                output.WriteLine(
                    $"node[{n}] {nodeName,-6} Pin={pinAxis.Values[pi],5:F1}  " +
                    $"DC={dc.Real,10:G5}  |fund|={fund.Magnitude,12:G6}");
            }
        }
    }
}
