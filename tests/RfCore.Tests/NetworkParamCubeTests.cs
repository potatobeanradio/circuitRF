// ================================================================
//  NetworkParamCubeTests.cs  —  brief-dd-network-params-and-stability.md §2
//
//  RfCore.Data.NetworkMetrics.ConvertSCube / IsNetworkParamCubeSpec — the authority the virtual
//  Z/Y cubes (materialized in DataSourceEntryViewModel for a simulated S-parameter run) are built
//  from. A simulated run has an S cube and a Z0 cube but no Z/Y cube at all; these are the pieces
//  that make "SP1.Z[:, 1, 1]" resolve to the same value RFNetwork.SToZ produces directly.
// ================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests;

public class NetworkParamCubeTests
{
    private readonly ITestOutputHelper _output;
    public NetworkParamCubeTests(ITestOutputHelper output) => _output = output;

    private static Mat<Complex> Amp2Port(double gain = 3.2)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.60, -0.30);
        m[0, 1] = new Complex(0.05,  0.02);
        m[1, 0] = new Complex(gain,  1.10);
        m[1, 1] = new Complex(0.45, -0.25);
        return m;
    }

    private static DataCube SCubeFromMats(Mat<Complex>[] mats, double[] freqs)
    {
        int n = mats[0].RowCount;
        var portVals = Enumerable.Range(1, n).Select(v => (double)v).ToArray();
        var raw = new Complex[freqs.Length * n * n];
        for (int f = 0; f < freqs.Length; f++)
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            raw[f * n * n + i * n + j] = mats[f][i, j];

        return new DataCube(
            [new Axis("freq", freqs, "Hz"), new Axis("i", portVals, "port"), new Axis("j", portVals, "port")],
            raw);
    }

    [Fact]
    public void ConvertSCube_ToZ_MatchesRFNetwork_SToZ_PerFrequency()
    {
        var m1 = Amp2Port(3.2);
        var m2 = Amp2Port(1.4);
        var sCube = SCubeFromMats([m1, m2], [1e9, 2e9]);
        Complex[] z0 = [new(50, 0), new(50, 0)];

        var zCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Z);

        var expected1 = RFNetwork.SToZ(m1, z0);
        var expected2 = RFNetwork.SToZ(m2, z0);
        var raw = zCube.ComplexValues;

        Assert.Equal(expected1[0, 0], raw[0], new ComplexApproxComparer());
        Assert.Equal(expected1[1, 0], raw[2], new ComplexApproxComparer());
        Assert.Equal(expected2[0, 1], raw[4 + 1], new ComplexApproxComparer());
        Assert.Equal(expected2[1, 1], raw[4 + 3], new ComplexApproxComparer());
    }

    [Fact]
    public void ConvertSCube_ToY_MatchesRFNetwork_SToY()
    {
        var m = Amp2Port();
        var sCube = SCubeFromMats([m], [1e9]);
        Complex[] z0 = [new(50, 0), new(75, 0)];

        var yCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Y);
        var expected = RFNetwork.SToY(m, z0);
        var raw = yCube.ComplexValues;

        Assert.Equal(expected[0, 0], raw[0], new ComplexApproxComparer());
        Assert.Equal(expected[0, 1], raw[1], new ComplexApproxComparer());
        Assert.Equal(expected[1, 0], raw[2], new ComplexApproxComparer());
        Assert.Equal(expected[1, 1], raw[3], new ComplexApproxComparer());
    }

    [Fact]
    public void ConvertSCube_PreservesAxesAndShape()
    {
        var sCube = SCubeFromMats([Amp2Port()], [1e9]);
        Complex[] z0 = [new(50, 0), new(50, 0)];

        var zCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Z);

        Assert.Equal(sCube.Rank, zCube.Rank);
        Assert.Equal(sCube.Axes[0].Name, zCube.Axes[0].Name);
        Assert.Equal(sCube.Axes[1].Length, zCube.Axes[1].Length);
        Assert.Equal(sCube.Axes[2].Length, zCube.Axes[2].Length);
    }

    [Fact]
    public void ConvertSCube_HandlesALeadingSweepAxis_RankFour()
    {
        // [sweep, freq, i, j] — the shape a swept S cube has.
        var m1 = Amp2Port(3.2);
        var m2 = Amp2Port(1.4);
        int n = 2;
        var portVals = Enumerable.Range(1, n).Select(v => (double)v).ToArray();
        var raw = new Complex[2 * n * n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            raw[i * n + j]         = m1[i, j];
            raw[n * n + i * n + j] = m2[i, j];
        }
        var sCube = new DataCube(
            [new Axis("Pin", [0.0, 1.0], "dBm"),
             new Axis("freq", [1e9], "Hz"),
             new Axis("i", portVals, "port"),
             new Axis("j", portVals, "port")],
            raw);
        Complex[] z0 = [new(50, 0), new(50, 0)];

        var zCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Z);

        var expected1 = RFNetwork.SToZ(m1, z0);
        var expected2 = RFNetwork.SToZ(m2, z0);
        var outRaw = zCube.ComplexValues;

        Assert.Equal(expected1[0, 0], outRaw[0], new ComplexApproxComparer());
        Assert.Equal(expected2[1, 1], outRaw[n * n + 3], new ComplexApproxComparer());
    }

    [Fact]
    public void ConvertSCube_RefusesS_AsTargetType()
    {
        var sCube = SCubeFromMats([Amp2Port()], [1e9]);
        Assert.Throws<ArgumentException>(() =>
            NetworkMetrics.ConvertSCube(sCube, [new(50, 0), new(50, 0)], MatrixType.S));
    }

    // ---- IsNetworkParamCubeSpec ------------------------------------------------

    [Fact]
    public void IsNetworkParamCubeSpec_TrueForS_Z_Y_InAGroupCarryingS_AndZ0()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", SCubeFromMats([Amp2Port()], [1e9]));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube([new(50, 0), new(50, 0)]));

        // True even for "Z"/"Y", which are NOT actually in the DataSet yet — the whole point of
        // being a "virtual cube" test: the group carries S+Z0, so Z/Y are derivable on demand.
        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "SP1.S"));
        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "SP1.Z"));
        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "SP1.Y"));
    }

    [Fact]
    public void IsNetworkParamCubeSpec_FalseForOtherCubesOrMissingZ0()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", SCubeFromMats([Amp2Port()], [1e9]));
        // No Z0 in this group.
        ds.AddToGroup("HB1", "V", new DataCube([new Axis("harmonic", [0.0, 1.0], "")], [Complex.One, Complex.One]));

        Assert.False(NetworkMetrics.IsNetworkParamCubeSpec(ds, "SP1.Z"));   // group has S but no Z0
        Assert.False(NetworkMetrics.IsNetworkParamCubeSpec(ds, "HB1.V"));  // not S/Z/Y at all
        Assert.False(NetworkMetrics.IsNetworkParamCubeSpec(ds, "HB1.Z"));  // group has neither S nor Z0
    }

    [Fact]
    public void IsNetworkParamCubeSpec_BareDefaultGroup_Works()
    {
        var snp = new SNP([1e9], [new Mat<Complex>(2, 2)], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
        var ds = DataSetBuilder.FromSnp(snp);

        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "S"));
        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "Z"));
        Assert.True(NetworkMetrics.IsNetworkParamCubeSpec(ds, "Y"));
    }

    // ---- §2 cost guard: "conversion is F × (N×N complex inverse)... measure on the largest
    // fixture you have; if a >8-port × >4001-point case is slow, build per-frequency on demand
    // rather than eagerly." No such fixture exists in the repo, so this synthesizes the stated
    // worst case directly and measures RfCore's own conversion cost (the DataSourceEntryViewModel
    // materialization pass calls exactly this, once per group, eagerly on first Data access).
    [Fact]
    public void ConvertSCube_EightPortFourThousandOnePoint_CostGuard()
    {
        const int nPorts = 8, nFreq = 4001;
        var freqs = Enumerable.Range(0, nFreq).Select(i => 1e9 + i * 1e6).ToArray();
        var mats = new Mat<Complex>[nFreq];
        var rnd = new Random(1);
        for (int f = 0; f < nFreq; f++)
        {
            var m = new Mat<Complex>(nPorts, nPorts);
            for (int i = 0; i < nPorts; i++)
            for (int j = 0; j < nPorts; j++)
                m[i, j] = new Complex(rnd.NextDouble() * 0.5, rnd.NextDouble() * 0.5 - 0.25);
            mats[f] = m;
        }
        var sCube = SCubeFromMats(mats, freqs);
        var z0 = Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray();

        var sw = Stopwatch.StartNew();
        var zCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Z);
        var yCube = NetworkMetrics.ConvertSCube(sCube, z0, MatrixType.Y);
        sw.Stop();

        _output.WriteLine($"8-port x 4001-point S->Z+S->Y conversion: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(nFreq * nPorts * nPorts, zCube.ComplexValues.Length);
        Assert.Equal(nFreq * nPorts * nPorts, yCube.ComplexValues.Length);
        // Generous bound (not a tight perf assertion) — this is the eager-materialization path
        // run on entry.Data's FIRST access, so it must stay well under anything a user would
        // perceive as a stall. Measured well under 1s on dev hardware; a regression that made
        // this scale badly (e.g. an accidental O(F^2)) would blow well past 5s.
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"S->Z/S->Y conversion took {sw.ElapsedMilliseconds} ms for an 8-port x 4001-point cube — " +
            "the brief's own guard says build per-frequency on demand instead of eagerly if this is slow.");
    }

    private sealed class ComplexApproxComparer : System.Collections.Generic.IEqualityComparer<Complex>
    {
        public bool Equals(Complex a, Complex b) =>
            Math.Abs(a.Real - b.Real) < 1e-9 && Math.Abs(a.Imaginary - b.Imaginary) < 1e-9;
        public int GetHashCode(Complex c) => 0;
    }
}
