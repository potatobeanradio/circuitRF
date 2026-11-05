// ================================================================
//  FieldCosimTests.cs — the gates of brief-sar-9 that need no openEMS: per-port fields recombined by a circuit.
//
//    FieldRecombinationTests  synthetic per-port fields with known weights give the hand-computed sum, local SAR, B1± and the
//                             averaged SAR of the summed field; the fast path's plane is the full path's; aᴴQa is the absorbed power
//    CosimNoEmRunTests        a capacitor change re-runs a schematic's fields analysis with zero openEMS launches, and the
//                             weights move with it
//    FieldSetStaleTests       a field set from another run, or with a port's file missing, is refused naming the file
// ================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>A small two-port field set held in memory, random but repeatable: every cell tissue but a few air cells.</summary>
internal sealed class MemoryFieldSet : IPortFieldSet
{
    public SarAxis X { get; } = new(-0.01, [0.004, 0.005, 0.004, 0.006]);
    public SarAxis Y { get; } = new(0.0, [0.003, 0.003, 0.004]);
    public SarAxis Z { get; } = new(0.02, [0.005, 0.005, 0.005, 0.005, 0.005]);
    public int PortCount => 2;
    public bool HasH => true;
    public readonly Complex[][] E, H;      // [port][3·cell + component]
    public readonly float[] Sigma, Rho, Volume;
    public int Count => X.Count * Y.Count * Z.Count;

    public MemoryFieldSet(int seed = 7)
    {
        var r = new Random(seed);
        Complex C() => new(r.NextDouble() * 2 - 1, r.NextDouble() * 2 - 1);
        E = [new Complex[3 * Count], new Complex[3 * Count]];
        H = [new Complex[3 * Count], new Complex[3 * Count]];
        for (int p = 0; p < 2; p++)
            for (int c = 0; c < 3 * Count; c++) { E[p][c] = 40 * C(); H[p][c] = 0.2 * C(); }
        Sigma = new float[Count];
        Rho = new float[Count];
        Volume = new float[Count];
        for (int i = 0, c = 0; i < X.Count; i++)
            for (int j = 0; j < Y.Count; j++)
                for (int k = 0; k < Z.Count; k++, c++)
                {
                    Sigma[c] = (float)(0.3 + r.NextDouble());
                    Rho[c] = c % 7 == 3 ? 0 : (float)(1000 + 100 * r.NextDouble());
                    Volume[c] = (float)(X.Widths[i] * Y.Widths[j] * Z.Widths[k]);
                }
    }

    public void ReadE(int port, long start, int count, Span<float> d) => Read(E[port], start, count, d);
    public void ReadH(int port, long start, int count, Span<float> d) => Read(H[port], start, count, d);

    public void ReadCells(long start, int count, Span<float> s, Span<float> rho, Span<float> v)
    {
        Sigma.AsSpan((int)start, count).CopyTo(s);
        Rho.AsSpan((int)start, count).CopyTo(rho);
        Volume.AsSpan((int)start, count).CopyTo(v);
    }

    private static void Read(Complex[] f, long start, int count, Span<float> d)
    {
        for (int c = 0; c < 3 * count; c++)
        {
            d[2 * c] = (float)f[3 * start + c].Real;
            d[2 * c + 1] = (float)f[3 * start + c].Imaginary;
        }
    }

    /// <summary>Writes the set as the files a 3D run leaves (PortFieldSet's layout), with the .sNp itself, a two-port Touchstone around
    /// <paramref name="hz"/>, its .npy and the result record <c>ems.cemr</c>, all four carrying <paramref name="stamp"/>.</summary>
    public string WriteBeside(string dir, double hz, string stamp)
    {
        Directory.CreateDirectory(dir);
        string snp = Path.Combine(dir, "ems.s2p");
        string folder = PortFieldSet.FolderFor(snp);
        Directory.CreateDirectory(folder);
        var freq = new PortFieldFrequency { Index = 1, Hz = hz, Shape = [X.Count, Y.Count, Z.Count], HasH = true };
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(1, 'x')), X.Faces);
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(1, 'y')), Y.Faces);
        SarVolumeFiles.WriteFloat64(Path.Combine(folder, PortFieldSet.FacesFile(1, 'z')), Z.Faces);
        SarVolumeFiles.WriteFloat32(Path.Combine(folder, PortFieldSet.CellFile(1, "sigma")), Sigma, freq.Shape);
        SarVolumeFiles.WriteFloat32(Path.Combine(folder, PortFieldSet.CellFile(1, "rho")), Rho, freq.Shape);
        SarVolumeFiles.WriteFloat32(Path.Combine(folder, PortFieldSet.CellFile(1, "volume")), Volume, freq.Shape);
        var owners = Rho.Select(v => v > 0 ? (short)0 : (short)-1).ToArray();
        Raw(Path.Combine(folder, PortFieldSet.CellFile(1, "owner")), "<i2", MemoryMarshal.AsBytes(owners.AsSpan()), freq.Shape);
        for (int p = 0; p < 2; p++)
        {
            Raw(Path.Combine(folder, PortFieldSet.FieldFile(p + 1, 1, 'E')), "<c8", Floats(E[p]), [.. freq.Shape, 3]);
            Raw(Path.Combine(folder, PortFieldSet.FieldFile(p + 1, 1, 'H')), "<c8", Floats(H[p]), [.. freq.Shape, 3]);
        }
        Complex[,] s = { { new(0.3, -0.2), new(0.1, 0.05) }, { new(0.1, 0.05), new(-0.25, 0.1) } };
        freq.S = PortFieldSet.Pack(s);
        freq.TissueMatrix = PortFieldSet.Pack(FieldRecombination.TissueMatrix(this));
        foreach (string f in Directory.EnumerateFiles(folder, "*.npy").Order())
        {
            string name = Path.GetFileName(f);
            freq.Files.Add(new PortFieldFile { Name = name, Bytes = new FileInfo(f).Length, Port = name.StartsWith('p') ? name[1] - '0' : 0 });
        }
        PortFieldSet.WriteManifest(folder, new PortFieldManifest
        {
            Stamp = stamp, Snp = "ems.s2p", Setup = "test",
            Ports = [new() { Number = 1, Name = "a", Z0Re = 50 }, new() { Number = 2, Name = "b", Z0Re = 50 }],
            Frequencies = [freq], Tissues = ["block"], TissueMaterials = ["tissue"], RegionName = "all tissue",
            B1Region = ["block"], B1RegionName = "block", MassesGrams = [0.001], B1 = true,
        });
        var lines = new List<string> { "! " + CircuitRF.Design.Layout.Em.EmProvenanceStamp.StampPrefix + stamp, "# Hz S RI R 50" };
        double[] sweep = [0.6 * hz, 0.8 * hz, hz, 1.2 * hz, 1.4 * hz];
        foreach (double f in sweep)
            lines.Add($"{f:R} {s[0, 0].Real} {s[0, 0].Imaginary} {s[1, 0].Real} {s[1, 0].Imaginary} " +
                      $"{s[0, 1].Real} {s[0, 1].Imaginary} {s[1, 1].Real} {s[1, 1].Imaginary}");
        File.WriteAllLines(snp, lines);

        var values = new Complex[sweep.Length * 4];
        for (int k = 0; k < sweep.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) values[(k * 2 + i) * 2 + j] = s[i, j];
        var data = new DataSet();
        data.AddToGroup("em", "S", new DataCube([new Axis("f", sweep, "Hz"), new Axis("i", [1.0, 2.0], ""), new Axis("j", [1.0, 2.0], "")], values));
        CircuitRF.Design.Layout.Em.EmResultRecord.StampData(data, stamp);
        using (var npy = File.Create(Path.Combine(dir, "ems_em.npy"))) RfCore.Export.DataSetExporter.ExportNpy(data, npy);
        File.WriteAllText(Path.Combine(dir, "ems.cemr"), new CircuitRF.Design.Layout.Em.EmResultRecord
        {
            Stamp = stamp, Written = DateTimeOffset.Now, Solver = new() { Name = "test" },
            Files = new() { Snp = "ems.s2p", Npy = "ems_em.npy", Fields = "ems.fields" },
            Ports = [new() { Number = 1, Name = "a", Z0Re = 50 }, new() { Number = 2, Name = "b", Z0Re = 50 }],
            Frequencies = new() { SweepHz = [.. sweep], FieldSetHz = [hz] },
        }.Serialize());
        return snp;
    }

    private static byte[] Floats(Complex[] f)
    {
        var v = new float[2 * f.Length];
        for (int c = 0; c < f.Length; c++) { v[2 * c] = (float)f[c].Real; v[2 * c + 1] = (float)f[c].Imaginary; }
        return MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    }

    private static void Raw(string path, string descr, ReadOnlySpan<byte> data, int[] shape)
    {
        using var fs = File.Create(path);
        fs.Write(PortFieldSet.NpyHeader(descr, [.. shape.Select(v => (long)v)]));
        fs.Write(data);
    }
}

public sealed class FieldRecombinationTests
{
    private static readonly Complex[] Weights = [new(0.8, -0.3), new(-0.2, 0.55)];

    [Fact]
    public void TheSumOfWeightedPortFieldsGivesTheHandComputedSarAndB1()
    {
        var set = new MemoryFieldSet();
        var r = FieldRecombination.Recombine(set, Weights, b1: true);
        var local = SarEngine.LocalSar(r.Grid);

        // by hand: E = Σ a_k E_k per cell, SAR = σ|E|²/(2ρ); B1± of H = Σ a_k H_k, openEMS's convention (SarB1)
        var ex = new Complex[set.Count]; var ey = new Complex[set.Count]; var ez = new Complex[set.Count];
        for (int c = 0; c < set.Count; c++)
        {
            Complex Sum(Complex[][] f, int comp) => Weights[0] * Cast(f[0][3 * c + comp]) + Weights[1] * Cast(f[1][3 * c + comp]);
            ex[c] = Sum(set.E, 0); ey[c] = Sum(set.E, 1); ez[c] = Sum(set.E, 2);
            double e2 = ex[c].Magnitude * ex[c].Magnitude + ey[c].Magnitude * ey[c].Magnitude + ez[c].Magnitude * ez[c].Magnitude;
            double sar = set.Rho[c] > 0 ? set.Sigma[c] * e2 / (2 * set.Rho[c]) : 0;
            Assert.Equal(sar, local[c], sar * 1e-5 + 1e-9);
            var (bp, bm) = SarB1.FromH(Sum(set.H, 0), Sum(set.H, 1), Sum(set.H, 2), 2, 1);
            Assert.Equal(bp.Magnitude, r.B1Plus![c], bp.Magnitude * 1e-5 + 1e-15);
            Assert.Equal(bm.Magnitude, r.B1Minus![c], bm.Magnitude * 1e-5 + 1e-15);
        }

        // the averaged SAR of the summed field is SAR-5's on the hand-summed grid, and aᴴQa is its absorbed power
        var byHand = SarGrid.FromCells(set.X, set.Y, set.Z, ex, ey, ez, set.Sigma, set.Rho, set.Volume);
        var options = new SarOptions { MassesGrams = [0.005], Method = SarMethod.Simple };
        var mine = SarEngine.Compute(r.Grid, options);
        var theirs = SarEngine.Compute(byHand, options);
        Assert.Equal(theirs.Averages[0].Peak!.Value, mine.Averages[0].Peak!.Value, theirs.Averages[0].Peak!.Value * 1e-5);
        double q = FieldRecombination.QuadraticPower(FieldRecombination.TissueMatrix(set), Weights);
        Assert.Equal(theirs.AbsorbedPowerW, q, theirs.AbsorbedPowerW * 1e-5);
        Assert.Equal(2, r.Counts.PortsUsed);
    }

    [Fact]
    public void TheFastPathsPlaneIsTheFullPathsPlane_AndReadsOnlyItsCells()
    {
        var set = new MemoryFieldSet();
        var full = FieldRecombination.Recombine(set, Weights, b1: true);
        var local = SarEngine.LocalSar(full.Grid);
        var plane = FieldRecombination.Local(set, Weights, FieldBox.Plane(set.X, set.Y, set.Z, 2, 3), b1: true);
        Assert.Equal(set.X.Count * set.Y.Count, plane.Counts.CellsRead);
        int at = 0;
        for (int i = 0; i < set.X.Count; i++)
            for (int j = 0; j < set.Y.Count; j++, at++)
            {
                int c = full.Grid.Index(i, j, 3);
                Assert.Equal(local[c], plane.LocalSar[at], local[c] * 1e-6 + 1e-12);
                Assert.Equal(full.B1Plus![c], plane.B1Plus![at], full.B1Plus[c] * 1e-6);
            }
    }

    private static Complex Cast(Complex c) => new((float)c.Real, (float)c.Imaginary);
}

/// <summary>FieldCosimulation.Runs is process-wide: the classes that recombine run one at a time, so a count is this test's own.</summary>
[CollectionDefinition(Name)]
public sealed class FieldCosimulationRunsCollection
{
    public const string Name = "FieldCosimulation.Runs";
}

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class CosimNoEmRunTests
{
    private const double Hz = 300e6;

    private static string Bench(string dir) => $"""
        Cval = 2e-12
        Port:P1 in 0 Num=1 Z=50 Ohm
        EmFieldData:EM1 in p2 File="{Path.Combine(dir, "ems.cemr")}"
        C:C1 p2 0 C=Cval
        analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz drive=1 average=no
        """;

    [Fact]
    public void ACapacitorChangeRecombinesWithNoOpenEmsLaunch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-sar9-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new MemoryFieldSet().WriteBeside(dir, Hz, "stamp-1");
            var circuit = PreparedCircuit.FromText(Bench(dir), dir, dir);
            long launches = OpenEmsRun.Invocations, recombinations = FieldCosimulation.Runs;

            Complex Weight2(string c)
            {
                var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Sets = [("Cval", c)] });
                Assert.True(run.Status == RunStatus.Success, run.StatusMessage + " " + string.Join(" ", run.Warnings));
                var a = run.Results.Single().Data.Cubes[FieldCosimulation.IncidentCube];
                return a.ComplexValues[1];
            }
            var at2p = Weight2("2e-12");
            var at5p = Weight2("5e-12");

            Assert.Equal(launches, OpenEmsRun.Invocations);
            Assert.Equal(recombinations + 2, FieldCosimulation.Runs);
            Assert.True((at2p - at5p).Magnitude > 1e-3 * at2p.Magnitude, $"port 2's wave did not move with the capacitor: {at2p} vs {at5p}");
            Assert.True(File.Exists(Path.Combine(FieldCosimulation.OutputFolder(PortFieldSet.FolderFor(Path.Combine(dir, "ems.s2p")), "F1"),
                                                 FieldCosimulation.RecordFile)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

public sealed class FieldSetStaleTests
{
    [Fact]
    public void AFieldSetFromAnotherRunOrMissingAPortIsRefusedNamingIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-sar9-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new MemoryFieldSet().WriteBeside(dir, 300e6, "stamp-1");
            string record = Path.Combine(dir, "ems.cemr"), manifest = Path.Combine(dir, "ems.fields", PortFieldSet.ManifestFile);
            var (ok, none) = PortFieldSet.Open(record, 300e6);
            Assert.Null(none);
            ok!.Dispose();

            // the field set re-written by another run: its stamp is not the record's
            string kept = File.ReadAllText(manifest);
            File.WriteAllText(manifest, kept.Replace("stamp-1", "stamp-2"));
            var (_, stale) = PortFieldSet.Open(record, 300e6);
            Assert.Contains("from another run (stamp stamp-2)", stale);
            Assert.Contains("ems.fields", stale);

            // the right run, with port 2's E gone
            File.WriteAllText(manifest, kept);
            File.Delete(Path.Combine(PortFieldSet.FolderFor(Path.Combine(dir, "ems.s2p")), PortFieldSet.FieldFile(2, 1, 'E')));
            var (_, missing) = PortFieldSet.Open(record, 300e6);
            Assert.Contains(PortFieldSet.FieldFile(2, 1, 'E'), missing);
            Assert.Contains("lost", missing);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

public sealed class FieldsDirectiveRoundTripTests
{
    [Fact]
    public void AFieldsAnalysisSurvivesTheNetlistAndTheSchematic()
    {
        var (_, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(
            "R:R1 a 0 R=50 Ohm\nanalysis F2 type=fields block=X1.EM1 freq=\"f0*2\" freqUnit=MHz drive=2,3 weights=\"1,polar(1,-90)\" norm=available average=no enabled=false\n");
        var fa = Assert.IsType<CircuitRF.Core.Design.FieldsAnalysis>(Assert.Single(tb.Analyses));
        void Same(CircuitRF.Core.Design.FieldsAnalysis x)
        {
            Assert.Equal(("F2", "X1.EM1", "f0*2", "MHz", "2,3"), (x.Name, x.Block, x.FreqExpr, x.FreqUnit, x.DriveExpr));
            Assert.Equal("1,polar(1,-90)", x.WeightsExpr);      // brief-sar-10 R-sar10-4
            Assert.True(x.PerAvailable);
            Assert.False(x.Average);
            Assert.False(x.Enabled);
        }
        Same(fa);
        var (_, again) = new CircuitRF.Core.Netlist.CnlReader().Read(CircuitRF.Core.Netlist.CnlWriter.Write(tb));
        Same(Assert.IsType<CircuitRF.Core.Design.FieldsAnalysis>(Assert.Single(again.Analyses)));
        var dto = CircuitRF.Design.Schematic.AnalysisSerialization.ToDto(fa);
        Same(Assert.IsType<CircuitRF.Core.Design.FieldsAnalysis>(CircuitRF.Design.Schematic.AnalysisSerialization.FromDto(dto)));
    }
}
