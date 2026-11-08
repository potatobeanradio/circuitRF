// ================================================================
//  PortBlockMeasurementTests.cs — two DUTs on one bench, each read as its own S matrix
//
//  A bench simulating two 2-ports gives one 4-port S cube. measure DUT_2 = SP1.S(3:4, 3:4) makes the
//  second DUT a 2-port of its own, ports renumbered from 1, so DUT_2(2,1) — its gain — is the bench's S43.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

public class PortBlockMeasurementTests
{
    [Fact]
    public void ABlockOfPorts_IsAMatrixOfItsOwn_ReadBackByItsOwnPortNumbers()
    {
        var (lib, tb) = new CnlReader().Read(
            "Port:P1 a 0 Num=1 Z=50 Ohm\n" +
            "Port:P2 b 0 Num=2 Z=50 Ohm\n" +
            "Port:P3 c 0 Num=3 Z=50 Ohm\n" +
            "Port:P4 d 0 Num=4 Z=50 Ohm\n" +
            "R:R1 a b R=50 Ohm\n" +
            "R:R2 c d R=50 Ohm\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n" +
            "measure DUT_1 = SP1.S(1:2, 1:2)\n" +
            "measure DUT_2 = SP1.S(3:4, 3:4)\n" +
            "measure G2 = DUT_2(2, 1)\n");
        var netlist = new Elaborator(lib).Elaborate(tb);

        // value = 10*i + j + 100*f, so each entry says which S it is
        double[] f = [1e9, 2e9];
        double[] ports = [1, 2, 3, 4];
        var v = new Complex[2 * 4 * 4];
        for (int k = 0; k < 2; k++) for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
            v[(k * 4 + i) * 4 + j] = 10 * (i + 1) + (j + 1) + 100 * k;
        var sp = new DataSet();
        sp.Add("S", new DataCube([new Axis("freq", f, "Hz"), new Axis("i", ports, "port"), new Axis("j", ports, "port")], v));

        var outDs = new DataSet();
        var errors = new MeasurementEvaluator(tb, netlist, new Dictionary<string, DataSet> { ["SP1"] = sp }).EvaluateInto(outDs);
        Assert.Empty(errors);

        var dut2 = outDs["DUT_2"];
        Assert.Equal(["freq", "i", "j"], dut2.Axes.Select(a => a.Name));
        Assert.Equal([1.0, 2.0], dut2.Axes[1].Values);
        Assert.Equal([1.0, 2.0], dut2.Axes[2].Values);
        Assert.Equal([new Complex(43, 0), new Complex(143, 0)], outDs["G2"].ComplexValues);
        Assert.Equal(new Complex(21, 0), outDs["DUT_1"].ComplexValues[2]);   // freq 0, i=2, j=1
    }
}
