using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// Verifies the documented two-tone IMD2 measurement expression, using the function-call accessor
/// with a mixIndex "(k1,k2)" label (resolved via ResolveSpectralArg → ResolvePin):
///   IMD2 = dB( HB1.V("Vout", "(1,-1)") ) - dB( HB1.V("Vout", "(1,0)") )
/// The SAME expression works whether or not the run has a Pin sweep — the accessor keeps any swept
/// axes (defaults to All). Carrier mag 10, IM2 mag 1 → IMD2 = dB(1) - dB(10) = -20 dBc.
/// </summary>
public class TwoToneImd2MeasurementTests
{
    private const string Imd2Expr =
        "dB( HB1.V(\"Vout\", \"(1,-1)\") ) - dB( HB1.V(\"Vout\", \"(1,0)\") )";

    // node(1)="Vout", mixIndex(3) = (0,0)/(1,0)/(1,-1); carrier (1,0) mag 10, IM2 (1,-1) mag 1.
    private static (Axis node, Axis mix) SpectralAxes() =>
        (new Axis("node",     [0.0],                 "",   ["Vout"]),
         new Axis("mixIndex", [0.0, 1.95e9, 0.1e9],  "Hz", ["(0,0)", "(1,0)", "(1,-1)"]));

    private static void FillTriple(Complex[] v, int baseI)
    {
        v[baseI + 0] = new Complex(0, 0);    // (0,0)
        v[baseI + 1] = new Complex(10, 0);   // (1,0) carrier
        v[baseI + 2] = new Complex(1, 0);    // (1,-1) IM2
    }

    // Single two-tone HB point (no sweep): V is [node, mixIndex] — the user's current case.
    [Fact]
    public void Imd2_SinglePoint_NoSweep_IsScalar()
    {
        var (lib, tb) = new CnlReader().Read("X = 0");
        tb.Measurements.Add(new Measurement("IMD2", Imd2Expr));
        var netlist = new Elaborator(lib).Elaborate(tb);

        var (nodeAxis, mixAxis) = SpectralAxes();
        var vData = new Complex[1 * 3];
        FillTriple(vData, 0);
        var hbDs = new DataSet();
        hbDs.Add("V", new DataCube([nodeAxis, mixAxis], vData));

        var outDs  = new DataSet();
        var errors = new MeasurementEvaluator(tb, netlist,
            new Dictionary<string, DataSet> { ["HB1"] = hbDs }).EvaluateInto(outDs);

        Assert.Empty(errors);
        Assert.True(outDs.Contains("IMD2"));
        var m = outDs["IMD2"];
        Assert.Equal(0, m.Rank);                    // scalar — no sweep axis
        Assert.Equal(-20.0, m.RealValues[0], 6);
    }

    // Pin-swept two-tone HB: V is [Pin, node, mixIndex] — the SAME expression yields a curve over Pin.
    [Fact]
    public void Imd2_OverPinSweep_IsRank1()
    {
        double[] pinValues = [-10.0, -5.0, 0.0, 5.0];
        var (lib, tb) = new CnlReader().Read("Pin = -10");
        tb.Analyses.Add(new ParametricSweepAnalysis("SwPin", "Pin", pinValues, "HB1"));
        tb.Measurements.Add(new Measurement("IMD2", Imd2Expr));
        var netlist = new Elaborator(lib).Elaborate(tb);

        var pinAxis = new Axis("Pin", pinValues);
        var (nodeAxis, mixAxis) = SpectralAxes();
        var vData = new Complex[4 * 1 * 3];
        for (int p = 0; p < 4; p++) FillTriple(vData, p * 3);
        var hbDs = new DataSet();
        hbDs.Add("V", new DataCube([pinAxis, nodeAxis, mixAxis], vData));

        var outDs  = new DataSet();
        var errors = new MeasurementEvaluator(tb, netlist,
            new Dictionary<string, DataSet> { ["HB1"] = hbDs }).EvaluateInto(outDs);

        Assert.Empty(errors);
        var m = outDs["IMD2"];
        Assert.Equal(1, m.Rank);                    // curve over Pin
        Assert.Equal("Pin", m.Axes[0].Name);
        Assert.Equal(4, m.Axes[0].Length);
        for (int p = 0; p < 4; p++)
            Assert.Equal(-20.0, m.RealValues[p], 6);
    }
}
