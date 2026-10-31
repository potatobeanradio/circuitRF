using System.Numerics;
using CircuitRF.Design.Modeling;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// A binary simulation dataset (.ds) imports through the array importer by its <c>Group.Variable</c> names: a
/// simulator's file — Vds outermost, S as S[i,j], the drain current as the source's current — reads with the current
/// mapped negated (<c>-DC1.VD.i</c>), and is flagged with that spelling when it is not; a script's file, its S as four
/// element arrays and its DC currents in a block of their own, reads with the two blocks joined on their shared axes.
/// The files are written by <see cref="BinaryDatasetWriter"/> from made-up numbers.
/// </summary>
public sealed class BinaryDatasetImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-dsimport-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly double[] Vds = [0.5, 6, 21], Vgs = [-2.8, -1.9, -0.6, 0.3], F = [0.7e9, 2.9e9, 8.5e9];

    private static double Id(double vg, double vd) => 0.21 * Math.Max(vg + 3.2, 0) * Math.Tanh(1.3 * vd);
    private static double Ig(double vg) => 2e-8 * Math.Exp(vg);
    private static Complex S(int i, int j, double vg, double vd, double f) =>
        new(0.9 - 0.13 * i * j + 0.02 * vg, -f * 2e-11 * (i + j) + 0.001 * vd);

    private static IEnumerable<int[]> Points(params int[] n)
    {
        int count = n.Aggregate(1, (a, b) => a * b);
        for (int c = 0; c < count; c++)
        {
            var idx = new int[n.Length];
            for (int k = n.Length - 1, rem = c; k >= 0; k--) { idx[k] = rem % n[k]; rem /= n[k]; }
            yield return idx;
        }
    }

    private static BinaryDatasetVariable R(string name, IEnumerable<double> v, string q = "") => new(name, q, BinaryValueKind.Real, [.. v], null, null);
    private static BinaryDatasetVariable C(string name, IEnumerable<Complex> v, string q = "")
    {
        var z = v.ToArray();
        return new(name, q, BinaryValueKind.Complex, [.. z.Select(x => x.Real)], [.. z.Select(x => x.Imaginary)], null);
    }

    private void Write(string name, BinaryDatasetVersion version, params BinaryDatasetBlock[] blocks) =>
        BinaryDatasetWriter.Write(Path.Combine(_dir, name), new BinaryDataset(blocks), version);

    private string SimulatorFile()
    {
        int d = Vds.Length, g = Vgs.Length, f = F.Length;
        Write("sim.ds", BinaryDatasetVersion.V4,
            new BinaryDatasetBlock("SweepVds.DC1.DC", "", [R("Vds", Vds), R("Vgs", Vgs)],
                [R("VD.i", Points(d, g).Select(p => -Id(Vgs[p[1]], Vds[p[0]])), "current"),
                 R("VG.i", Points(d, g).Select(p => -Ig(Vgs[p[1]])), "current")]),
            new BinaryDatasetBlock("SweepVds.SweepVgs.SP1.SP", "", [R("Vds", Vds), R("Vgs", Vgs), R("freq", F, "frequency")],
                [.. from i in new[] { 1, 2 } from j in new[] { 1, 2 }
                    select C($"S[{i},{j}]", Points(d, g, f).Select(p => S(i, j, Vgs[p[1]], Vds[p[0]], F[p[2]])), "s-param")]));
        return "sim.ds";
    }

    private ImportResult Import(string source, DataSetKind kind, params (string Quantity, string Array)[] map) =>
        DataImport.Import(Set(source, kind, map), _dir);

    private static CmcDataSet Set(string source, DataSetKind kind, (string Quantity, string Array)[] map) => new()
    {
        Name = "t", Kind = kind, Sources = [source],
        Import = new ImportSettings { Format = ImportSettings.Ds, Arrays = map.ToDictionary(m => m.Quantity, m => m.Array) },
    };

    private static void AtEveryPoint(NormalisedData data, Action<BiasPoint, int> check)
    {
        Assert.Equal(Vds.Length * Vgs.Length, data.Count);
        for (int p = 0; p < data.Count; p++) check(data.Points[p], p);
    }

    [Fact]
    public void ASimulatorsFile_ReadsItsDcWithTheSourceCurrentNegated_AndItsSTable()
    {
        string src = SimulatorFile();

        var dc = Import(src, DataSetKind.DcIv, ("vgs", "DC1.Vgs"), ("vds", "DC1.Vds"), ("id", "-DC1.VD.i"), ("ig", "-DC1.VG.i")).Data;
        AtEveryPoint(dc, (pt, _) =>
        {
            Assert.Equal(Id(pt.Vgs, pt.Vds), pt.Id);
            Assert.Equal(Ig(pt.Vgs), pt.Ig);
        });

        var sp = Import(src, DataSetKind.SimulatedTables, ("vgs", "SP1.Vgs"), ("vds", "SP1.Vds"), ("freq", "SP1.freq"), ("S", "SP1.S")).Data;
        Assert.Equal(F, sp.FrequenciesHz!);
        AtEveryPoint(sp, (pt, p) =>
        {
            for (int f = 0; f < F.Length; f++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        Assert.Equal(S(i + 1, j + 1, pt.Vgs, pt.Vds, F[f]), sp.SAt(p, f, i, j));
        });
    }

    [Fact]
    public void ASourceCurrentMappedAsItIs_IsFlagged_WithTheNegatedSpelling()
    {
        string src = SimulatorFile();
        var set = Set(src, DataSetKind.DcIv, [("vgs", "DC1.Vgs"), ("vds", "DC1.Vds"), ("id", "DC1.VD.i")]);

        var flag = DataHygiene.Check([(set, DataImport.Import(set, _dir).Data)]).Single(f => f.Kind == HygieneKind.CurrentSign);
        Assert.Contains("map it as -DC1.VD.i=id", flag.Detail);
    }

    [Fact]
    public void AScriptsFile_JoinsItsSAndDcBlocksOnTheirSharedAxes()
    {
        int g = Vgs.Length, d = Vds.Length, f = F.Length;
        Write("script.ds", BinaryDatasetVersion.V3,
            new BinaryDatasetBlock("SP", "", [R("vgs", Vgs), R("vds", Vds), R("freq", F)],
                [.. from i in new[] { 1, 2 } from j in new[] { 1, 2 }
                    select C($"S{i}{j}", Points(g, d, f).Select(p => S(i, j, Vgs[p[0]], Vds[p[1]], F[p[2]])))]),
            new BinaryDatasetBlock("DC", "", [R("vgs", Vgs), R("vds", Vds)],
                [R("id", Points(g, d).Select(p => Id(Vgs[p[0]], Vds[p[1]])))]));

        var data = Import("script.ds", DataSetKind.SimulatedTables,
            ("vgs", "SP.vgs"), ("vds", "SP.vds"), ("freq", "SP.freq"), ("id", "DC.id"),
            ("S11", "SP.S11"), ("S12", "SP.S12"), ("S21", "SP.S21"), ("S22", "SP.S22")).Data;
        AtEveryPoint(data, (pt, p) =>
        {
            Assert.Equal(Id(pt.Vgs, pt.Vds), pt.Id);
            Assert.Equal(S(2, 1, pt.Vgs, pt.Vds, F[2]), data.SAt(p, 2, 1, 0));
        });
    }
}
