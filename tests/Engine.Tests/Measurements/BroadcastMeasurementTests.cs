using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// Gate tests for named-axis broadcasting in MeasurementEvaluator and for
/// per-measurement resilience (one failure must not suppress successful cubes).
/// </summary>
public class BroadcastMeasurementTests
{
    // ── T7: Nested-sweep broadcast ────────────────────────────────────────────

    [Fact]
    public void Measurement_NestedSweep_BroadcastsSweptVar()
    {
        // Outer sweep: RFfreq (3 pts), inner sweep: Pin (4 pts), base: HB1.
        // V has shape [RFfreq(3), Pin(4), harmonic(2)].
        // Measurement: HB1.V[:, :, 1] - Pin
        //   • HB1.V[:, :, 1] slices harmonic index 1 → rank-2 [RFfreq(3), Pin(4)]
        //   • Pin is injected as rank-1 [Pin(4)] (swept variable)
        //   • broadcast subtraction → result [RFfreq(3), Pin(4)]  (no throw)

        double[] rfValues  = [1e9, 2e9, 3e9];
        double[] pinValues = [-10.0, -5.0, 0.0, 5.0];

        var src = "RFfreq = 1e9\nPin = -10";
        var (lib, tb) = new CnlReader().Read(src);
        tb.Analyses.Add(new ParametricSweepAnalysis("SwOuter", "RFfreq", rfValues, "SwInner"));
        tb.Analyses.Add(new ParametricSweepAnalysis("SwInner", "Pin",    pinValues, "HB1"));
        tb.Measurements.Add(new Measurement("IRL_dB", "HB1.V[:, :, 1] - Pin"));

        var netlist = new Elaborator(lib).Elaborate(tb);

        // Build result: V[RFfreq(3), Pin(4), harmonic(2)]
        var rfAxis   = new Axis("RFfreq",   rfValues);
        var pinAxis  = new Axis("Pin",      pinValues);
        var harmAxis = new Axis("harmonic", [0.0, 1.0]);
        var vData    = new Complex[3 * 4 * 2];
        for (int i = 0; i < vData.Length; i++) vData[i] = new Complex(i + 1, 0);
        var vCube = new DataCube([rfAxis, pinAxis, harmAxis], vData);
        var hbDs  = new DataSet();
        hbDs.Add("V", vCube);
        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };

        var me    = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        var errors = me.EvaluateInto(outDs);

        Assert.Empty(errors);
        Assert.True(outDs.Contains("IRL_dB"), "IRL_dB must be present");

        var mCube = outDs["IRL_dB"];
        Assert.Equal(2, mCube.Rank);
        Assert.Equal("RFfreq", mCube.Axes[0].Name);
        Assert.Equal("Pin",    mCube.Axes[1].Name);
        Assert.Equal(3, mCube.Axes[0].Length);
        Assert.Equal(4, mCube.Axes[1].Length);

        // Spot-check element [r=0, p=1]:
        //   V strides: [8, 2, 1]. V[0,1,1] = vData[0*8+1*2+1] = vData[3] = Complex(4,0).
        //   Pin[1] = -5.0. Result: 4 − (−5) = 9.
        //   In result [RFfreq(3),Pin(4)] strides [4,1]: flat index = 0*4+1 = 1.
        Assert.Equal(9.0, mCube.ComplexValues[1].Real, 1e-9);
    }

    // ── T8: Resilient measurements ────────────────────────────────────────────

    [Fact]
    public void Measurement_Resilient_OneBadDoesNotNukeRest()
    {
        // Three measurements:  M1 (valid), M2 (references undefined var), M3 (valid).
        // Expected: M1 and M3 emitted, M2 absent, exactly 1 error string naming M2.

        var (lib, tb) = new CnlReader().Read("X = 42");
        tb.Measurements.Add(new Measurement("M1", "X"));                // succeeds
        tb.Measurements.Add(new Measurement("M2", "UndefinedVariable")); // fails
        tb.Measurements.Add(new Measurement("M3", "X * 2"));            // succeeds

        var netlist = new Elaborator(lib).Elaborate(tb);
        var results = new Dictionary<string, DataSet>();

        var me     = new MeasurementEvaluator(tb, netlist, results);
        var outDs  = new DataSet();
        var errors = me.EvaluateInto(outDs);

        // Successful measurements must be present
        Assert.True(outDs.Contains("M1"), "M1 must be emitted despite M2 failure");
        Assert.True(outDs.Contains("M3"), "M3 must be emitted despite M2 failure");
        Assert.False(outDs.Contains("M2"), "M2 must not be emitted");

        // Exactly one error, and it names the failing measurement
        Assert.Single(errors);
        Assert.Contains("M2", errors[0]);

        // Values correct
        Assert.Equal(42.0, outDs["M1"].RealValues[0], 1e-12);
        Assert.Equal(84.0, outDs["M3"].RealValues[0], 1e-12);
    }
}
