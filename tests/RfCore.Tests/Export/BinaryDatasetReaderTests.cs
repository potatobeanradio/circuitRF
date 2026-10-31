using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace RfCore.Tests.Export;

/// <summary>
/// A binary simulation dataset (.ds) written by <see cref="BinaryDatasetWriter"/> reads back through
/// <see cref="BinaryDatasetReader"/> as the grid it was written from — every value at its point, in both container
/// versions; a complete S[i,j] family reads as one S table; a large object is stored in longer page runs; a sweep
/// that is not a full grid is refused by name. Every number here is made up.
/// </summary>
public sealed class BinaryDatasetReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-ds-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly double[] Vgs = [-3.1, -1.7, 0.4], Vds = [0, 2.5], F = [0.5e9, 1.5e9, 4e9, 11e9];
    private static readonly double[] VdsN = [0, 1.5, 9, 33], VgsN = [-4.5, -2.25, -0.75];

    private static double Id(double vg, double vd) => 0.37 * Math.Max(vg + 3.6, 0) * Math.Tanh(0.9 * vd);
    private static Complex S(int i, int j, double vg, double vd, double f) =>
        new(0.11 * i + 0.07 * j + 0.01 * vg, 0.003 * vd - f * 1e-11 * (i + 2 * j));

    internal static BinaryDatasetVariable R(string name, double[] v, string q = "") => new(name, q, BinaryValueKind.Real, v, null, null);
    internal static BinaryDatasetVariable C(string name, Complex[] v, string q = "") =>
        new(name, q, BinaryValueKind.Complex, [.. v.Select(z => z.Real)], [.. v.Select(z => z.Imaginary)], null);
    internal static BinaryDatasetVariable T(string name, string[] v) => new(name, "", BinaryValueKind.Text, [], null, v);
    internal static BinaryDatasetBlock Block(string name, BinaryDatasetVariable[] axes, params BinaryDatasetVariable[] values) =>
        new(name, "", axes, values);

    internal static IEnumerable<int[]> Points(params int[] n)
    {
        int count = n.Aggregate(1, (a, b) => a * b);
        for (int c = 0; c < count; c++)
        {
            var idx = new int[n.Length];
            for (int k = n.Length - 1, rem = c; k >= 0; k--) { idx[k] = rem % n[k]; rem /= n[k]; }
            yield return idx;
        }
    }

    private string Written(string name, BinaryDatasetVersion version, params BinaryDatasetBlock[] blocks)
    {
        string path = Path.Combine(_dir, name);
        BinaryDatasetWriter.Write(path, new BinaryDataset(blocks), version);
        return path;
    }

    /// <summary>Version 3: S as four element arrays, the DC block beside it, a text block; the names prefixed.</summary>
    private string ScriptLayout() => Written("v3.ds", BinaryDatasetVersion.V3,
        Block("Xvar_SP", [R("vgs", Vgs), R("vds", Vds), R("freq", F)],
            [.. from i in new[] { 1, 2 } from j in new[] { 1, 2 }
                select C($"S{i}{j}", [.. Points(3, 2, 4).Select(p => S(i, j, Vgs[p[0]], Vds[p[1]], F[p[2]]))])]),
        Block("Xvar_DC", [R("vgs", Vgs), R("vds", Vds)],
            R("id", [.. Points(3, 2).Select(p => Id(Vgs[p[0]], Vds[p[1]]))]), R("ig", [.. Points(3, 2).Select(p => -1e-9 * (p[0] + 1))])),
        Block("Xvar_Info", [R("index", [0])], T("description", ["synthetic"])));

    /// <summary>Version 4: Vds outermost, S[i,j] elements, port impedances and names, each variable's quantity.</summary>
    private string QuantityLayout() => Written("v4.ds", BinaryDatasetVersion.V4,
        Block("SweepVds.DC1.DC", [R("Vds", VdsN), R("Vgs", VgsN)],
            R("vd", [.. Points(4, 3).Select(p => VdsN[p[0]])], "voltage"),
            R("VD.i", [.. Points(4, 3).Select(p => -Id(VgsN[p[1]], VdsN[p[0]]))], "current")),
        Block("SweepVds.SweepVgs.SP1.SP", [R("Vds", VdsN), R("Vgs", VgsN), R("freq", F, "frequency")],
            [.. (from i in new[] { 1, 2 } from j in new[] { 1, 2 }
                 select C($"S[{i},{j}]", [.. Points(4, 3, 4).Select(p => S(i, j, VgsN[p[1]], VdsN[p[0]], F[p[2]]))], "s-param")),
             C("PortZ[1]", [.. Enumerable.Repeat(new Complex(50, 0), 48)], "port-impedance"),
             C("PortZ[2]", [.. Enumerable.Repeat(new Complex(50, 0), 48)], "port-impedance"),
             T("PortName[1]", [.. Enumerable.Repeat("P1", 48)]),
             T("PortName[2]", [.. Enumerable.Repeat("P2", 48)])]));

    [Fact]
    public void Version3_ReadsBackEveryValueAtItsPoint()
    {
        var ds = BinaryDatasetReader.Read(ScriptLayout());

        Assert.Equal(["SP", "DC", "Info"], ds.Blocks.Select(b => b.Group));
        var sp = ds.Blocks[0];
        Assert.Equal([3, 2, 4], sp.Shape);
        Assert.Equal(Vgs, sp.Axes[0].Real);
        Assert.Equal(F, sp.Axes[2].Real);
        var s21 = sp.Values.Single(v => v.Name == "S21");
        int k = 0;
        foreach (var p in Points(3, 2, 4))
        {
            Assert.Equal(S(2, 1, Vgs[p[0]], Vds[p[1]], F[p[2]]), new Complex(s21.Real[k], s21.Imag![k]));
            k++;
        }
        Assert.Equal(Points(3, 2).Select(p => Id(Vgs[p[0]], Vds[p[1]])), ds.Blocks[1].Values[0].Real);
        Assert.Equal(["synthetic"], ds.Blocks[2].Values[0].Text!);
    }

    [Fact]
    public void Version4_ReadsBackQuantitiesUnitsAndGroups()
    {
        var ds = BinaryDatasetReader.Read(QuantityLayout());

        Assert.Equal(["DC1", "SP1"], ds.Blocks.Select(b => b.Group));
        var dc = ds.Blocks[0];
        Assert.Equal(VdsN, dc.Axes[0].Real);
        Assert.Equal(VgsN, dc.Axes[1].Real);
        var current = dc.Values.Single(v => v.Name == "VD.i");
        Assert.Equal(("current", "A"), (current.Quantity, current.Unit));
        Assert.Equal(Points(4, 3).Select(p => -Id(VgsN[p[1]], VdsN[p[0]])), current.Real);
        Assert.Equal("Hz", ds.Blocks[1].Axes[2].Unit);
        Assert.Equal("P2", ds.Blocks[1].Values.Single(v => v.Name == "PortName[2]").Text![0]);
    }

    [Fact]
    public void ACompleteElementFamily_ReadsAsOneTable_TextIsNamedAsSkipped()
    {
        var (arrays, skipped) = BinaryDatasetReader.List(QuantityLayout());

        var s = arrays["SP1.S"];
        Assert.Equal([4, 3, 4, 2, 2], s.Shape);
        // S[1,2] at Vds index 2, Vgs index 1, freq index 3 sits at [2,1,3,0,1].
        int at = (((2 * 3 + 1) * 4 + 3) * 2 + 0) * 2 + 1;
        Assert.Equal(S(1, 2, VgsN[1], VdsN[2], F[3]), s.At(at));
        Assert.Equal([4, 3, 4, 2], arrays["SP1.PortZ"].Shape);
        Assert.Equal([4], arrays["DC1.Vds"].Shape);
        Assert.Contains(("SP1.PortName[1]", "holds text, not numbers."), skipped);
        // Elements spelled without brackets are kept as they are.
        var script = BinaryDatasetReader.List(ScriptLayout()).Arrays;
        Assert.True(script.ContainsKey("SP.S11"));
        Assert.False(script.ContainsKey("SP.S"));
    }

    [Fact]
    public void AsADataSet_EachBlockIsAGroup_EachArrayACubeOnItsAxes()
    {
        var ds = BinaryDatasetReader.ReadDataSet(QuantityLayout());

        Assert.Contains("SP1", ds.Groups);
        var s = ds["SP1.S"];
        Assert.Equal(["Vds", "Vgs", "freq", "i", "j"], s.Axes.Select(a => a.Name));
        Assert.Equal(DataKind.Complex, s.DataKind);
        Assert.Equal("A", ds["DC1.VD.i"].Unit);
        Assert.False(ds.Contains("DC1.Vds"));   // an axis, not a cube
    }

    [Fact]
    public void ADataSetExportedAsDs_ReadsBackItsCubes_WithZ0AsPortZBesideS()
    {
        var ds = new DataSet();
        double[] vds = [5, 15, 28], f = [1e9, 2e9, 3e9, 4e9];
        Complex[] s = [.. Enumerable.Range(0, 48).Select(k => new Complex(0.1 * (k % 7), -0.01 * k))];
        ds.AddToGroup("SP1", "S", new DataCube([new Axis("Vds", vds, "V"), new Axis("freq", f, "Hz"),
            new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], s));
        ds.AddToGroup("SP1", "Z0", new DataCube([new Axis("port", [1, 2], "port")], [new Complex(50, 0), new Complex(25, 0)]));
        ds.AddToGroup("DC1", "Id", new DataCube([new Axis("Vds", vds, "V")], [0.11, 0.18, 0.21]) { Unit = "A" });
        string path = Path.Combine(_dir, "export.ds");

        DataSetExporter.Export(ds, path, ExportFormat.Ds);

        var back = BinaryDatasetReader.ReadDataSet(path);
        Assert.Equal(s, back["SP1.S"].ComplexValues);
        Assert.Equal(["Vds", "freq", "i", "j"], back["SP1.S"].Axes.Select(a => a.Name));
        Assert.Equal(new[] { 0.11, 0.18, 0.21 }, back["DC1.Id"].RealValues);
        Assert.Equal("A", back["DC1.Id"].Unit);
        var portZ = back["SP1.PortZ"];
        Assert.Equal(["Vds", "freq", "port"], portZ.Axes.Select(a => a.Name));
        Assert.Equal(new Complex(25, 0), portZ.ComplexValues[^1]);
        Assert.Equal(["SP1", "DC1"], back.Groups);
    }

    [Fact]
    public void ManyObjects_SpreadTheDirectoryOverSeveralPages_AndReadBackWhole()
    {
        // 30 outer points of a three-level sweep make ~95 objects: more than one 1 KB directory page holds.
        double[] outer = [.. Enumerable.Range(0, 30).Select(i => 0.5 * i)];
        string path = Written("many.ds", BinaryDatasetVersion.V4, Block("Sweep.SP2.SP",
            [R("Vds", outer), R("Vgs", [-1.0, 0.0]), R("freq", [1e9, 2e9])],
            C("S[1,1]", [.. Points(30, 2, 2).Select(p => new Complex(outer[p[0]], p[1] + 0.1 * p[2]))])));

        var b = BinaryDatasetReader.Read(path).Blocks.Single();
        Assert.Equal([30, 2, 2], b.Shape);
        Assert.Equal(14.5, b.Values[0].Real[^1]);
        Assert.Equal(1.1, b.Values[0].Imag![^1], 12);
    }

    [Fact]
    public void AnObjectTooLargeForOnePageList_IsStoredInLongerRuns_AndReadsBack()
    {
        // 60,000 values × 24 bytes ≈ 1.4 MB: past what one 1 KB page list addresses in runs of eight pages.
        double[] x = [.. Enumerable.Range(0, 300).Select(i => (double)i)], y = [.. Enumerable.Range(0, 200).Select(i => 0.01 * i)];
        string path = Written("large.ds", BinaryDatasetVersion.V4,
            Block("Big", [R("x", x), R("y", y)], R("z", [.. Points(300, 200).Select(p => x[p[0]] + y[p[1]])])));

        var z = BinaryDatasetReader.Read(path).Blocks.Single().Values.Single();
        Assert.Equal(60_000, z.Count);
        Assert.Equal(299 + 1.99, z.Real[^1], 12);
    }

    [Fact]
    public void ABlockWhoseValuesDoNotFillItsGrid_IsNotWritten()
    {
        var ex = Assert.Throws<ArgumentException>(() => BinaryDatasetWriter.ToBytes(new BinaryDataset(
            [Block("DC", [R("vgs", [-1.0, 0.0]), R("vds", [1.0, 2.0])], R("id", [1.0, 2.0, 3.0]))])));
        Assert.Contains("'id'", ex.Message);
    }

    [Fact]
    public void ASweepWhoseInnerCountChanges_IsRefusedByName()
    {
        var data = new BinaryDataset([Block("DC", [R("vgs", [-1.0, 0.0]), R("vds", [1.0, 2.0, 3.0])], R("id", new double[6]))]);
        string path = Path.Combine(_dir, "uneven.ds");
        File.WriteAllBytes(path, BinaryDatasetWriter.ToBytes(data, BinaryDatasetVersion.V3, innerCounts: [3, 2], innerAxis: null));

        var ex = Assert.Throws<InvalidDataException>(() => BinaryDatasetReader.Read(path));
        Assert.Contains("'vds'", ex.Message);
        Assert.Contains("only a full grid", ex.Message);
    }

    [Fact]
    public void ASweepWhoseInnerValuesChange_IsRefusedByName()
    {
        var data = new BinaryDataset([Block("DC", [R("vgs", [-1.0, 0.0]), R("vds", [1.0, 2.0])], R("id", new double[4]))]);
        string path = Path.Combine(_dir, "notgrid.ds");
        File.WriteAllBytes(path, BinaryDatasetWriter.ToBytes(data, BinaryDatasetVersion.V3, innerCounts: null, innerAxis: [[1.0, 2.0], [1.0, 2.5]]));

        var ex = Assert.Throws<InvalidDataException>(() => BinaryDatasetReader.Read(path));
        Assert.Contains("'vds'", ex.Message);
        Assert.Contains("different values", ex.Message);
    }

    [Fact]
    public void AFileOfAnotherFormat_IsNotTakenForOne()
    {
        string path = Path.Combine(_dir, "other.ds");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 2048).Select(i => (byte)i)]);

        Assert.False(BinaryDatasetReader.IsBinaryDataset(path));
        Assert.True(BinaryDatasetReader.IsBinaryDataset(ScriptLayout()));
        var ex = Assert.Throws<InvalidDataException>(() => BinaryDatasetReader.Read(path));
        Assert.Equal("other.ds is not a binary dataset.", ex.Message);
    }
}
