using System.IO.Compression;
using System.Numerics;
using System.Text;
using CircuitRF.Cli;
using CircuitRF.Design.Modeling;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// An array import reads a simulator's tables however they were laid out, so long as only one reading fits the shapes: a
/// grid's axes in any order, a temperature axis, a MATLAB-ordered Y, Y as four element arrays, units on the arrays and a
/// NumPy .npz — each held to the one documented layout read from the same data. A map can come from the array names; a
/// shape that fits nothing is refused naming every shape; the layout mistakes that still read are flagged.
/// </summary>
public sealed class ArrayLayoutImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-arrays-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly double[] Vgs = [-2, -1.5, -1, -0.5], Vds = [1, 3, 6], T = [25, 85], F = [1e9, 2e9, 5e9];
    private static int G => Vgs.Length;
    private static int D => Vds.Length;
    private static int NT => T.Length;
    private static int NF => F.Length;

    private static double Id(int g, int d, int t) => 0.1 * (Vgs[g] + 2.5) * Math.Tanh(Vds[d]) * (1 - 0.002 * (T[t] - 25));

    private static Complex Y(int g, int d, int t, int f, int i, int j)
    {
        double w = 2 * Math.PI * F[f], gm = 0.05 * (Vgs[g] + 2.5) * (1 - 0.001 * T[t]), gds = 0.002 * Vgs[g] + 0.01 / Vds[d];
        return (i, j) switch
        {
            (0, 0) => new Complex(1e-4, w * 0.3e-12),
            (0, 1) => new Complex(0, -w * 0.05e-12),
            (1, 0) => new Complex(gm, -w * 0.05e-12),
            _ => new Complex(gds, w * 0.1e-12),
        };
    }

    /// <summary>A cube of <paramref name="shape"/> filled in C order by <paramref name="at"/> of the index.</summary>
    private static DataCube Cube(int[] shape, Func<int[], Complex> at, bool complex)
    {
        var axes = shape.Select((n, k) => new Axis($"a{k}", [.. Enumerable.Range(0, n).Select(x => (double)x)])).ToArray();
        int count = shape.Aggregate(1, (a, b) => a * b);
        var values = new Complex[count];
        var idx = new int[shape.Length];
        for (int c = 0; c < count; c++)
        {
            int rem = c;
            for (int k = shape.Length - 1; k >= 0; k--) { idx[k] = rem % shape[k]; rem /= shape[k]; }
            values[c] = at(idx);
        }
        return complex ? new DataCube(axes, values) : new DataCube(axes, [.. values.Select(v => v.Real)]);
    }

    private string Mat(string name, params (string Name, DataCube Cube)[] arrays)
    {
        var ds = new DataSet();
        foreach (var (n, c) in arrays) ds.Add(n, c);
        DataSetExporter.Export(ds, Path.Combine(_dir, name), ExportFormat.Mat);
        return name;
    }

    private ImportResult Import(string source, string format, Dictionary<string, string> map, Dictionary<string, string>? units = null) =>
        DataImport.Import(new CmcDataSet
        {
            Name = "t", Kind = DataSetKind.SimulatedTables, Sources = [source],
            Import = new ImportSettings
            {
                Format = format,
                Arrays = format == ImportSettings.Mat ? map.ToDictionary(p => p.Key, p => "dataset/" + p.Value) : map,
                ArrayUnits = units,
            },
        }, _dir);

    private static Dictionary<string, string> Map(params string[] q) => q.ToDictionary(x => x, x => x, StringComparer.Ordinal);

    /// <summary>The documented layout: one value per point, vgs slowest then vds then T, Y as [point, freq, i, j].</summary>
    private ImportResult Documented()
    {
        int n = G * D * NT;
        (int g, int d, int t) P(int k) => (k / (D * NT), k / NT % D, k % NT);
        string src = Mat("documented.mat",
            ("vgs", Cube([n], i => Vgs[P(i[0]).g], false)),
            ("vds", Cube([n], i => Vds[P(i[0]).d], false)),
            ("T", Cube([n], i => T[P(i[0]).t], false)),
            ("id", Cube([n], i => { var p = P(i[0]); return Id(p.g, p.d, p.t); }, false)),
            ("freq", Cube([NF], i => F[i[0]], false)),
            ("Y", Cube([n, NF, 2, 2], i => { var p = P(i[0]); return Y(p.g, p.d, p.t, i[1], i[2], i[3]); }, true)));
        var r = Import(src, ImportSettings.Mat, Map("vgs", "vds", "T", "id", "freq", "Y"));
        Assert.Empty(r.Notes);
        return r;
    }

    private static void SameData(ImportResult expected, ImportResult actual)
    {
        Assert.Equal(expected.Data.Count, actual.Data.Count);
        for (int k = 0; k < expected.Data.Count; k++)
        {
            var (e, a) = (expected.Data.Points[k], actual.Data.Points[k]);
            Assert.Equal((e.Vgs, e.Vds, e.AmbientC), (a.Vgs, a.Vds, a.AmbientC));
            Assert.Equal(e.Id, a.Id, 12);
        }
        Assert.Equal(expected.Data.FrequenciesHz, actual.Data.FrequenciesHz);
        Assert.Equal(expected.Data.S!.Length, actual.Data.S!.Length);
        for (int k = 0; k < expected.Data.S.Length; k++) Assert.True((expected.Data.S[k] - actual.Data.S[k]).Magnitude < 1e-12, $"S[{k}]");
    }

    [Fact]
    public void AGridOverTemperature_InAnyAxisOrder_WithAMatlabOrderedY_ReadsAsTheDocumentedLayout()
    {
        var documented = Documented();
        // The bias as three axes; id written [vds, T, vgs]; Y as MATLAB's Y(i, j, f, t, d, g).
        string src = Mat("grid.mat",
            ("vgs", Cube([G], i => Vgs[i[0]], false)),
            ("vds", Cube([D], i => Vds[i[0]], false)),
            ("T", Cube([NT], i => T[i[0]], false)),
            ("id", Cube([D, NT, G], i => Id(i[2], i[0], i[1]), false)),
            ("freq", Cube([NF], i => F[i[0]], false)),
            ("Y", Cube([2, 2, NF, NT, D, G], i => Y(i[5], i[4], i[3], i[2], i[0], i[1]), true)));
        var r = Import(src, ImportSettings.Mat, Map("vgs", "vds", "T", "id", "freq", "Y"));
        SameData(documented, r);
        Assert.Contains(r.Notes, n => n.Contains("'dataset/Y' (2 × 2 × 3 × 2 × 3 × 4) read as [i, j, freq, T, vds, vgs]", StringComparison.Ordinal));
    }

    [Fact]
    public void FourElementArrays_WithUnits_ReadAsTheWholeTableInSi()
    {
        var documented = Documented();
        int n = G * D * NT;
        (int g, int d, int t) P(int k) => (k / (D * NT), k / NT % D, k % NT);
        var arrays = new List<(string, DataCube)>
        {
            ("vgs", Cube([n], i => Vgs[P(i[0]).g], false)),
            ("vds", Cube([n], i => Vds[P(i[0]).d], false)),
            ("T", Cube([n], i => T[P(i[0]).t] + 273.15, false)),
            ("id", Cube([n], i => { var p = P(i[0]); return 1e3 * Id(p.g, p.d, p.t); }, false)),
            ("freq", Cube([NF], i => F[i[0]] / 1e9, false)),
        };
        foreach (var (e, i, j) in new[] { ("Y11", 0, 0), ("Y12", 0, 1), ("Y21", 1, 0), ("Y22", 1, 1) })
            arrays.Add((e, Cube([NF, n], x => { var p = P(x[1]); return Y(p.g, p.d, p.t, x[0], i, j); }, true)));   // [freq, point]
        string src = Mat("elements.mat", [.. arrays]);
        var r = Import(src, ImportSettings.Mat, Map("vgs", "vds", "T", "id", "freq", "Y11", "Y12", "Y21", "Y22"),
                       new() { ["id"] = "mA", ["freq"] = "GHz", ["T"] = "K" });
        Assert.Equal(documented.Data.Count, r.Data.Count);
        for (int k = 0; k < r.Data.Count; k++)
        {
            Assert.Equal(documented.Data.Points[k].AmbientC, r.Data.Points[k].AmbientC, 9);
            Assert.Equal(documented.Data.Points[k].Id, r.Data.Points[k].Id, 12);
        }
        Assert.Equal(documented.Data.FrequenciesHz!, r.Data.FrequenciesHz!, (a, b) => Math.Abs(a - b) < 1e-3);
        for (int k = 0; k < documented.Data.S!.Length; k++) Assert.True((documented.Data.S[k] - r.Data.S![k]).Magnitude < 1e-12);
    }

    [Fact]
    public void ANumpyNpz_ReadsLikeAMat_AndAPlainNpy_IsRefusedWithWhatToWriteInstead()
    {
        var documented = Documented();
        string npz = Path.Combine(_dir, "tables.npz");
        using (var zip = ZipFile.Open(npz, ZipArchiveMode.Create))
        {
            int n = G * D * NT;
            (int g, int d, int t) P(int k) => (k / (D * NT), k / NT % D, k % NT);
            Member(zip, "vgs", [n], [.. Enumerable.Range(0, n).Select(k => Vgs[P(k).g])]);
            Member(zip, "vds", [n], [.. Enumerable.Range(0, n).Select(k => Vds[P(k).d])]);
            Member(zip, "T", [n], [.. Enumerable.Range(0, n).Select(k => T[P(k).t])]);
            Member(zip, "id", [n], [.. Enumerable.Range(0, n).Select(k => { var p = P(k); return Id(p.g, p.d, p.t); })]);
            Member(zip, "freq", [NF], F);
            var y = new List<Complex>();
            for (int k = 0; k < n; k++) for (int f = 0; f < NF; f++) for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
                { var p = P(k); y.Add(Y(p.g, p.d, p.t, f, i, j)); }
            Member(zip, "Y", [n, NF, 2, 2], null, [.. y]);
        }
        SameData(documented, Import("tables.npz", ImportSettings.Npz, Map("vgs", "vds", "T", "id", "freq", "Y")));

        File.WriteAllBytes(Path.Combine(_dir, "id.npy"), Npy([3], [1, 2, 3], null));
        var ex = Assert.Throws<DataImportException>(() => Import("id.npy", ImportSettings.Npy, Map("vgs", "vds")));
        Assert.Contains("numpy.savez", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMap_ComesFromTheArrayNames_WithTheirUnits_AndAnAmbiguityIsLeftToTheUser()
    {
        var s = DataImport.SuggestArrayMap([
            new("Vg", [5], false), new("Vd", [4], false), new("Ids_mA", [5, 4], false), new("Ig_A", [5, 4], false),
            new("freq_GHz", [3], false), new("Y", [5, 4, 3, 2, 2], true), new("labels", [3], false)]);
        Assert.Equal("Vg=vgs, Vd=vds, Ig_A=ig, Ids_mA=id:mA, freq_GHz=freq:GHz, Y=Y", s.Text);
        Assert.Empty(s.Doubts);

        var amb = DataImport.SuggestArrayMap([new("dc.Vg", [5], false), new("sp.Vg", [5], false), new("vds", [4], false)]);
        Assert.False(amb.Arrays.ContainsKey("vgs"));
        Assert.Contains("'dc.Vg' and 'sp.Vg' all look like vgs", amb.Doubts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ModelImport_WithNoMap_UsesAndReportsTheOneTheNamesSuggest()
    {
        string src = Mat("names.mat",
            ("Vg", Cube([G], i => Vgs[i[0]], false)), ("Vd", Cube([D], i => Vds[i[0]], false)),
            ("Ids_mA", Cube([G, D], i => 1e3 * Id(i[0], i[1], 0), false)));
        string cmc = Path.Combine(_dir, "fet.cmc");
        CmcFile.Write(cmc, new CmcDocument { Kind = CmcKind.Active });
        var (realOut, realErr) = (Console.Out, Console.Error);
        var buffer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(buffer); Console.SetError(buffer);
            JsonRun.Reset();
            exit = CliEntry.Run(["model", "import", cmc, Path.Combine(_dir, src), "--as", "DcIv"]);
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
        Assert.True(exit == 0, buffer.ToString());
        Assert.Contains("map from the array names: dataset/Vg=vgs, dataset/Vd=vds, dataset/Ids_mA=id:mA.", buffer.ToString(), StringComparison.Ordinal);
        var set = CmcFile.Read(cmc).DataSets.Single();
        Assert.Equal("mA", set.Import.ArrayUnits!["id"]);
    }

    [Fact]
    public void ASingleBiasTable_AndAFrequencyWrittenOncePerPoint_Read()
    {
        string one = Mat("one.mat", ("vgs", Cube([1], _ => -1, false)), ("vds", Cube([1], _ => 3, false)),
            ("freq", Cube([NF], i => F[i[0]], false)), ("Y", Cube([NF, 2, 2], i => Y(2, 1, 0, i[0], i[1], i[2]), true)));
        var r1 = Import(one, ImportSettings.Mat, Map("vgs", "vds", "freq", "Y"));
        Assert.Equal(1, r1.Data.Count);
        Assert.Equal(NF * 4, r1.Data.S!.Length);

        int n = G * D;
        string rep = Mat("repeated.mat", ("vgs", Cube([G], i => Vgs[i[0]], false)), ("vds", Cube([D], i => Vds[i[0]], false)),
            ("freq", Cube([n, 1], _ => 2e9, false)), ("Y", Cube([G, D, 2, 2], i => Y(i[0], i[1], 0, 1, i[2], i[3]), true)));
        var r2 = Import(rep, ImportSettings.Mat, Map("vgs", "vds", "freq", "Y"));
        Assert.Equal([2e9], r2.Data.FrequenciesHz!);
        Assert.Contains(r2.Notes, x => x.Contains("read as one frequency, repeated", StringComparison.Ordinal));
    }

    [Fact]
    public void ADryRun_WithNoDocumentYet_ReportsTheReadingAndWritesNothing()
    {
        string src = Mat("names.mat",
            ("Vg", Cube([G], i => Vgs[i[0]], false)), ("Vd", Cube([D], i => Vds[i[0]], false)),
            ("Ids", Cube([D, G], i => -Id(i[1], i[0], 0), false)));
        string cmc = Path.Combine(_dir, "kit.cmc");
        var before = Directory.GetFileSystemEntries(_dir).Order().ToList();
        var (exit, text) = Cli(["model", "import", cmc, Path.Combine(_dir, src), "--as", "DcIv", "--dry-run"]);
        Assert.True(exit == 0, text);
        Assert.Contains("dataset/Ids (3 × 4), dataset/Vd (3), dataset/Vg (4).", text, StringComparison.Ordinal);
        Assert.Contains("'dataset/Ids' (3 × 4) read as [vds, vgs].", text, StringComparison.Ordinal);
        Assert.Contains("Id < 0 at", text, StringComparison.Ordinal);
        Assert.Contains("nothing written (dry run).", text, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir).Order().ToList());
    }

    [Fact]
    public void TheDataPage_IsAReferenceTopic_AndTheModelToolOffersTheDryRun()
    {
        var topic = CircuitRF.Design.Reference.ReferenceLibrary.Find("model-data");
        Assert.NotNull(topic);
        string text = CircuitRF.Design.Reference.ReferenceLibrary.Read(topic!);
        Assert.Contains("## From another simulator", text, StringComparison.Ordinal);
        Assert.Contains("Simulated tables as named arrays", text, StringComparison.Ordinal);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        var (_, help) = Cli(["reference", "cmc"]);
        Assert.Contains("reference model-data", help, StringComparison.Ordinal);
    }

    private static (int Exit, string Text) Cli(string[] args)
    {
        var (realOut, realErr) = (Console.Out, Console.Error);
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer); Console.SetError(buffer);
            JsonRun.Reset();
            return (CliEntry.Run(args), buffer.ToString());
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }

    [Fact]
    public void ShapesThatFitNoLayout_AreRefused_NamingEveryShape()
    {
        string src = Mat("bad.mat", ("vgs", Cube([G], i => 0, false)), ("vds", Cube([D], i => 0, false)), ("id", Cube([G, D + 1], i => 0, false)));
        var ex = Assert.Throws<DataImportException>(() => Import(src, ImportSettings.Mat, Map("vgs", "vds", "id")));
        Assert.Contains("dataset/id (4 × 4)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4 × 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrainCurrentOutOfTheTerminal_SwappedPorts_AndGhzReadAsHz_AreEachFlagged()
    {
        string src = Mat("wrong.mat",
            ("vgs", Cube([G], i => Vgs[i[0]], false)), ("vds", Cube([D], i => Vds[i[0]], false)),
            ("id", Cube([G, D], i => -Id(i[0], i[1], 0), false)),
            ("freq", Cube([NF], i => F[i[0]] / 1e9, false)),
            ("Y", Cube([G, D, NF, 2, 2], i => Y(i[0], i[1], 0, i[2], i[4], i[3]), true)));       // transposed
        var set = new CmcDataSet
        {
            Name = "wrong", Kind = DataSetKind.SimulatedTables, Sources = [src],
            Import = new ImportSettings { Format = ImportSettings.Mat, Arrays = Map("vgs", "vds", "id", "freq", "Y").ToDictionary(p => p.Key, p => "dataset/" + p.Value) },
        };
        var data = DataImport.Import(set, _dir).Data;
        // The port check wants conducting biases: the negative current is mirrored for it.
        var mirrored = new NormalisedData
        {
            Points = [.. data.Points.Select(p => p with { Id = -p.Id })], FrequenciesHz = data.FrequenciesHz, S = data.S, Z0 = data.Z0,
        };
        var kinds = DataHygiene.Check([(set, data)]).Where(f => f.Point == "").Select(f => f.Kind).ToList();
        Assert.Contains(HygieneKind.CurrentSign, kinds);
        Assert.Contains(HygieneKind.Unit, kinds);
        Assert.Contains(HygieneKind.PortOrder, DataHygiene.Check([(set, mirrored)]).Select(f => f.Kind));
        Assert.DoesNotContain(DataHygiene.Check([(set, Documented().Data)]), f => f.Point == "");
    }

    // ── A minimal NPY writer, from the published format ──

    private static void Member(ZipArchive zip, string name, int[] shape, double[]? re, Complex[]? c = null)
    {
        using var s = zip.CreateEntry(name + ".npy").Open();
        s.Write(Npy(shape, re, c));
    }

    private static byte[] Npy(int[] shape, double[]? re, Complex[]? c)
    {
        string shp = shape.Length == 1 ? $"({shape[0]},)" : $"({string.Join(", ", shape)})";
        string header = $"{{'descr': '{(c is null ? "<f8" : "<c16")}', 'fortran_order': False, 'shape': {shp}, }}";
        int pad = 64 - (10 + header.Length + 1) % 64;
        header += new string(' ', pad % 64) + "\n";
        using var ms = new MemoryStream();
        ms.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        ms.Write(BitConverter.GetBytes((ushort)header.Length));
        ms.Write(Encoding.ASCII.GetBytes(header));
        if (c is null) foreach (double v in re!) ms.Write(BitConverter.GetBytes(v));
        else foreach (var v in c) { ms.Write(BitConverter.GetBytes(v.Real)); ms.Write(BitConverter.GetBytes(v.Imaginary)); }
        return ms.ToArray();
    }
}
