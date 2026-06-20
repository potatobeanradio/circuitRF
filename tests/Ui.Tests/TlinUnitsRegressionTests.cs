// Regression: TLIN electrical-length E must reach the engine with the deg→rad unit applied
// EXACTLY ONCE. The elaborator's generic parameter path multiplies an authored "E=90 deg" by
// Units.Scale("deg")=π/180, so the model must consume E as radians and must NOT re-apply π/180.
// A regression here (double-conversion) collapses a 90° line to a near-zero-length through:
// S21 phase would read ≈0° instead of −90° at the design frequency.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TlinUnitsRegressionTests
{
    private static TestBench OneLineTestBench(string eExpr, string eUnit)
    {
        // Term(1) — TL1(TLIN) — Term(2), all on a 1 GHz, 50 Ω quarter-wave line.
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("T1", "Term",
            new List<string> { "in", "0" },
            new List<ParameterAssignment> { new("Num", "1", null), new("Z", "50", "Ohm") }));
        tb.Instances.Add(new Instance("TL1", "TLIN",
            new List<string> { "in", "out" },
            new List<ParameterAssignment>
            {
                new("Z", "50", "Ohm"),
                new("E", eExpr, eUnit),
                new("F", "1", "GHz"),
            }));
        tb.Instances.Add(new Instance("T2", "Term",
            new List<string> { "out", "0" },
            new List<ParameterAssignment> { new("Num", "2", null), new("Z", "50", "Ohm") }));
        return tb;
    }

    [Fact]
    public void Elaborator_AppliesDegToRad_Once()
    {
        var netlist = new Elaborator().Elaborate(OneLineTestBench("90", "deg"));
        var ec = netlist.Components.First(c => c.ComponentType == "TLIN");

        // Authored "90 deg" must arrive at the model as π/2 radians (deg→rad applied once).
        Assert.Equal(Math.PI / 2.0, ec.Parameters["E"].AsReal(), 6);
    }

    [Fact]
    public void QuarterWaveLine_S21_Is_Minus90Degrees_AtDesignFreq()
    {
        var netlist = new Elaborator().Elaborate(OneLineTestBench("90", "deg"));
        var ds = SParameterEngine.Run(netlist, new[] { 1e9 });

        // S21 of an ideal 90° 50 Ω line into 50 Ω at the design frequency is −j  (∠−90°).
        var s21 = ds.S(2, 1).ComplexValues[0];
        double phaseDeg = s21.Phase * 180.0 / Math.PI;

        Assert.Equal(-90.0, phaseDeg, 3);
        Assert.Equal(1.0, s21.Magnitude, 3);   // lossless ⇒ |S21| = 1
    }
}
