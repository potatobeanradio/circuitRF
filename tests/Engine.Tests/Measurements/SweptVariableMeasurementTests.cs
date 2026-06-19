using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// Gate tests for measurements that reference swept variables.
/// Bug fix: a measurement "M = Pin" over a 10-point sweep of Pin now yields
/// a 10-element result cube (one per sweep point) rather than a scalar.
/// </summary>
public class SweptVariableMeasurementTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal TestBench+netlist with a global "Pin" variable and a
    /// ParametricSweepAnalysis named "SW" over "Pin".
    /// </summary>
    private static (TestBench tb, ElaboratedNetlist netlist) BuildMinimalTb(
        double[] pinValues, string? extraGlobal = null)
    {
        var src = "Pin = -20";
        if (extraGlobal is not null) src += $"\n{extraGlobal} = 0";
        var (lib, tb) = new CnlReader().Read(src);

        tb.Analyses.Add(new ParametricSweepAnalysis("SW", "Pin", pinValues, "HB1"));

        var netlist = new Elaborator(lib).Elaborate(tb);
        return (tb, netlist);
    }

    /// <summary>
    /// Build a result DataSet as ParametricSweepEngine would produce:
    /// a flat DataSet whose cubes have a prepended "Pin" axis.
    /// </summary>
    private static DataSet BuildSweptDs(double[] pinValues)
    {
        var pinAxis  = new Axis("Pin",  pinValues);
        var nodeAxis = new Axis("node", [0.0, 1.0], labels: ["Vout", "Vin"]);
        var harmAxis = new Axis("harmonic", [0.0, 1.0, 2.0]);

        int nPts  = pinValues.Length;
        var vData = new Complex[nPts * 2 * 3];
        // fill with distinguishable values
        for (int i = 0; i < vData.Length; i++) vData[i] = new Complex(i + 1, 0);

        var vCube = new DataCube([pinAxis, nodeAxis, harmAxis], vData);
        var ds    = new DataSet();
        ds.Add("V", vCube);
        return ds;
    }

    // ── Test 1: SweptVar_IsCube ───────────────────────────────────────────────

    [Fact]
    public void SweptVar_IsCube()
    {
        double[] pinValues = [-20, -19, -18, -17, -16, -15, -14, -13, -12, -11];
        var (tb, netlist) = BuildMinimalTb(pinValues);
        tb.Measurements.Add(new Measurement("M", "Pin"));

        var hbDs = BuildSweptDs(pinValues);
        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };
        var me = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        Assert.True(outDs.Contains("M"), "Result DataSet must contain 'M'");
        var mCube = outDs["M"];
        Assert.Equal(1, mCube.Rank);
        Assert.Equal("Pin", mCube.Axes[0].Name);
        Assert.Equal(pinValues.Length, mCube.Axes[0].Length);

        // Values must equal the sweep values
        for (int i = 0; i < pinValues.Length; i++)
            Assert.Equal(pinValues[i], mCube.RealValues[i], 1e-12);
    }

    // ── Test 2: SweptVar_Aligns ───────────────────────────────────────────────

    [Fact]
    public void SweptVar_Aligns()
    {
        // HB1.V has shape [Pin(10), node(2), harmonic(3)]
        // M = HB1.V["Vout", 1] - Pin  → should be [Pin(10)] (broadcast by name)
        double[] pinValues = Enumerable.Range(0, 10).Select(i => -20.0 + i).ToArray();
        var (tb, netlist) = BuildMinimalTb(pinValues);
        tb.Measurements.Add(new Measurement("M", "HB1.V[:, \"Vout\", 1] - Pin"));

        var hbDs = BuildSweptDs(pinValues);
        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };
        var me = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        Assert.True(outDs.Contains("M"), "Result DataSet must contain 'M'");
        var mCube = outDs["M"];
        Assert.Equal(1, mCube.Rank);
        Assert.Equal(pinValues.Length, mCube.Axes[0].Length);
    }

    // ── Test 3: NestedSweep ───────────────────────────────────────────────────

    [Fact]
    public void NestedSweep_EachVarItsOwnCube()
    {
        double[] paValues = [-5.0, 0.0, 5.0];
        double[] fbValues = [1e9, 2e9, 3e9, 4e9];

        // Pa outer (3), Fb inner (4) — result DataSet has V[Pa(3), Fb(4), node, harmonic]
        // but test focuses on measurements referencing Pa and Fb individually.
        var src = "Pa = 0\nFb = 1e9";
        var (lib, tb) = new CnlReader().Read(src);
        tb.Analyses.Add(new ParametricSweepAnalysis("SwOuter", "Pa", paValues, "SwInner"));
        tb.Analyses.Add(new ParametricSweepAnalysis("SwInner", "Fb", fbValues, "HB1"));
        tb.Measurements.Add(new Measurement("M_Pa", "Pa"));
        tb.Measurements.Add(new Measurement("M_Fb", "Fb"));

        var netlist = new Elaborator(lib).Elaborate(tb);

        // Build a DataSet with both Pa and Fb axes prepended
        var paAxis   = new Axis("Pa", paValues);
        var fbAxis   = new Axis("Fb", fbValues);
        var nodeAxis = new Axis("node", [0.0], labels: ["Vout"]);
        var harmAxis = new Axis("harmonic", [0.0, 1.0]);
        var vData    = new Complex[paValues.Length * fbValues.Length * 1 * 2];
        var vCube    = new DataCube([paAxis, fbAxis, nodeAxis, harmAxis], vData);
        var hbDs     = new DataSet();
        hbDs.Add("V", vCube);

        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };
        var me = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        var mPa = outDs["M_Pa"];
        var mFb = outDs["M_Fb"];

        Assert.Equal(1, mPa.Rank);
        Assert.Equal("Pa", mPa.Axes[0].Name);
        Assert.Equal(paValues.Length, mPa.Axes[0].Length);

        Assert.Equal(1, mFb.Rank);
        Assert.Equal("Fb", mFb.Axes[0].Name);
        Assert.Equal(fbValues.Length, mFb.Axes[0].Length);
    }

    // ── Test 4: NoSweep_StillScalar ──────────────────────────────────────────

    [Fact]
    public void NoSweep_StillScalar()
    {
        // "Gain" is a regular global, not a sweep variable → stays scalar
        var (lib, tb) = new CnlReader().Read("Gain = 10");
        tb.Measurements.Add(new Measurement("M", "Gain"));
        var netlist = new Elaborator(lib).Elaborate(tb);

        // Empty analysis results (no sweep axis)
        var results = new Dictionary<string, DataSet>();
        var me  = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        Assert.True(outDs.Contains("M"));
        var mCube = outDs["M"];
        Assert.Equal(0, mCube.Rank);  // scalar (rank-0)
        Assert.Equal(10.0, mCube.RealValues[0], 1e-12);
    }

    // ── Test 5: DisabledSweep_Collapsed ──────────────────────────────────────

    [Fact]
    public void DisabledSweep_Collapsed_FallsBackToScalar()
    {
        double[] pinValues = [-20, -15, -10];
        var (tb, netlist) = BuildMinimalTb(pinValues);
        tb.Measurements.Add(new Measurement("M", "Pin"));

        // Result DataSet has NO "Pin" axis (disabled/collapsed sweep produced nothing)
        var ds = new DataSet();
        ds.Add("V", DataCube.Scalar(1.0));  // no sweep axis
        var results = new Dictionary<string, DataSet> { ["HB1"] = ds };

        var me = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        Assert.True(outDs.Contains("M"));
        var mCube = outDs["M"];
        // Should fall back to the scalar global Pin = -20
        Assert.Equal(0, mCube.Rank);
        Assert.Equal(-20.0, mCube.RealValues[0], 1e-12);
    }

    // ── Test 6: CubeMeasurement_WithUnit ─────────────────────────────────────

    [Fact]
    public void CubeMeasurement_WithUnit_IdentityScalePassesThrough()
    {
        double[] pinValues = [-20.0, -15.0, -10.0];
        var (tb, netlist) = BuildMinimalTb(pinValues);
        // Identity-scale unit (dBm) should not throw and should pass the cube through unchanged
        tb.Measurements.Add(new Measurement("M", "Pin", "dBm"));

        var hbDs  = BuildSweptDs(pinValues);
        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };
        var me    = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);   // must not throw

        Assert.True(outDs.Contains("M"));
        var mCube = outDs["M"];
        Assert.Equal(1, mCube.Rank);
        Assert.Equal(pinValues.Length, mCube.Axes[0].Length);
        // Values unchanged (dBm is identity scale)
        for (int i = 0; i < pinValues.Length; i++)
            Assert.Equal(pinValues[i], mCube.RealValues[i], 1e-12);
    }

    [Fact]
    public void CubeMeasurement_WithLinearUnit_ScalesCubeValues()
    {
        double[] pinValues = [1.0, 2.0, 3.0];
        var (tb, netlist) = BuildMinimalTb(pinValues);
        // "k" = 1000× scale: "1k, 2k, 3k"
        tb.Measurements.Add(new Measurement("M", "Pin", "k"));

        var hbDs  = BuildSweptDs(pinValues);
        var results = new Dictionary<string, DataSet> { ["HB1"] = hbDs };
        var me    = new MeasurementEvaluator(tb, netlist, results);
        var outDs = new DataSet();
        me.EvaluateInto(outDs);

        Assert.True(outDs.Contains("M"));
        var mCube = outDs["M"];
        Assert.Equal(1, mCube.Rank);
        for (int i = 0; i < pinValues.Length; i++)
            Assert.Equal(pinValues[i] * 1000.0, mCube.RealValues[i], 1e-9);
    }
}
