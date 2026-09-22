using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// `freq` in a measurement. The reserved keyword is injected per-stamping-frequency into a
/// component-value scope, so before this it had no spelling in a measurement at all and
/// "myCalc = 1/2/pi/freq" failed with "Unresolved name 'freq' in scope 'measurements'".
/// </summary>
public class FrequencyInMeasurementTests
{
    private static readonly double[] Grid = [1e9, 2e9, 3e9];

    private static (TestBench tb, ElaboratedNetlist netlist) BuildTb()
    {
        var (lib, tb) = new CnlReader().Read(
            "Port:P1 in 0 Num=1 Z=50 Ohm\n" +
            "Port:P2 out 0 Num=2 Z=50 Ohm\n" +
            "R:R1 in out R=50 Ohm\n" +
            "analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz\n");
        return (tb, new Elaborator(lib).Elaborate(tb));
    }

    /// <summary>An S cube on the given grid — the shape `freq` must broadcast-align with.</summary>
    private static DataSet SpDs(double[] grid)
    {
        var data = new Complex[grid.Length * 2 * 2];
        for (int i = 0; i < data.Length; i++) data[i] = new Complex(i + 1, 0);
        var ds = new DataSet();
        ds.Add("S", new DataCube(
            [new Axis("freq", grid, "Hz"), new Axis("i", [1.0, 2.0]), new Axis("j", [1.0, 2.0])],
            data));
        return ds;
    }

    [Fact]
    public void Freq_IsTheRunsFrequencyAxis_AndAlignsWithAnAnalysisCube()
    {
        var (tb, netlist) = BuildTb();
        tb.Measurements.Add(new Measurement("myCalc", "1/2/pi/freq"));
        tb.Measurements.Add(new Measurement("withS",  "SP1.S(2,1) * freq"));   // must not misalign

        var outDs = new DataSet();
        var errors = new MeasurementEvaluator(tb, netlist,
            new Dictionary<string, DataSet> { ["SP1"] = SpDs(Grid) }).EvaluateInto(outDs);

        Assert.Empty(errors);

        var m = outDs["myCalc"];
        Assert.Equal(1, m.Rank);
        Assert.Equal("freq", m.Axes[0].Name);
        for (int i = 0; i < Grid.Length; i++)
            Assert.Equal(1.0 / (2 * System.Math.PI * Grid[i]), m.RealValues[i], 15);

        Assert.Equal("freq", outDs["withS"].Axes[0].Name);
        Assert.Equal(Grid.Length, outDs["withS"].Axes[0].Length);
    }

    [Fact]
    public void TwoDisagreeingGrids_ReportTheAmbiguity_RatherThanPickingOne()
    {
        var (tb, netlist) = BuildTb();
        tb.Measurements.Add(new Measurement("myCalc", "1/2/pi/freq"));

        var errors = new MeasurementEvaluator(tb, netlist, new Dictionary<string, DataSet>
        {
            ["SP1"] = SpDs(Grid),
            ["SP2"] = SpDs([4e9, 5e9, 6e9]),
        }).EvaluateInto(new DataSet());

        var e = Assert.Single(errors);
        Assert.Contains("frequency grids", e);
    }
}
