// ================================================================
//  CosimSuperpositionTests.cs — brief-sar-9 R-sar9-6's superposition case: a simultaneous two-port openEMS run equals the
//  weighted sum of the two single-port runs' fields, recombined by circuitRF (testdata/sar/circuitrf/pair).
//
//    PairSuperpositionTests        routine, fixed data, no process: the committed per-port field set recombined with the
//                                  incident waves the simultaneous run measured at its own ports gives that run's local SAR
//                                  and B1± in every cell, and the fast path's plane gives the same
//    PairSuperpositionRerunTests   Benchmark: the per-port runs through the CLI, then the simultaneous run — port 1's model
//                                  with port 2's excitation added at −0.5 — by openEMS itself; CRF_WRITE_SAR9_OUTPUTS=1 writes
//                                  the fixed data
//
//  Every run takes the same fixed number of time steps (EndCriterionDb −200: no criterion fires), so the three DFTs cover the
//  same window and superposition holds to the float arithmetic, not to a ring-down.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using RfCore.Hdf5;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

internal static class PairCosim
{
    public static readonly string Workspace = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "circuitrf", "pair");
    public static readonly string Fixed = Path.Combine(Workspace, "fixed");
    public const string C3d = "Pair/3d/Pair.c3d", Setup = "PerPort", Stem = "Pair PerPort.openems";
    public const double Hz = 1e9, Z0 = 50;

    /// <summary>The simultaneous run's excitation amplitudes, port 1 and port 2 (each port's own soft source, scaled).</summary>
    public static readonly double[] Excite = [1, -0.5];

    public const string Simultaneous = "simultaneous", Weights = "weights.json";

    /// <summary>The comparison: the field set in <paramref name="dir"/> recombined with the simultaneous run's own incident waves,
    /// against that run's fields, every cell; and the fast path on the plane through the block's middle.</summary>
    public static void Compare(string dir, ITestOutputHelper output)
    {
        var (set, refusal) = PortFieldSet.Open(Path.Combine(dir, Stem + ".cemr"), Hz);
        Assert.True(set is not null, refusal);
        using (set)
        {
            var w = JsonSerializer.Deserialize<double[][]>(File.ReadAllText(Path.Combine(dir, Simultaneous, Weights)))!;
            Complex[] a = [.. w.Select(x => new Complex(x[0], x[1]))];
            output.WriteLine($"  the simultaneous run's incident waves: a1 = {a[0].Magnitude:G5} ∠ {a[0].Phase * 180 / Math.PI:F2}°, " +
                             $"a2 = {a[1].Magnitude:G5} ∠ {a[1].Phase * 180 / Math.PI:F2}° (√W as solved); a2/a1 = {(a[1] / a[0]).Real:F5}{(a[1] / a[0]).Imaginary:+0.00000;-0.00000}j");
            var r = FieldRecombination.Recombine(set!, a, b1: true);
            var local = SarEngine.LocalSar(r.Grid);

            int n = (int)set!.CellCount;
            var e = ReadC8(Path.Combine(dir, Simultaneous, "E.npy"), n);
            var h = ReadC8(Path.Combine(dir, Simultaneous, "H.npy"), n);
            var sigma = new float[n]; var rho = new float[n]; var vol = new float[n];
            set.ReadCells(0, n, sigma, rho, vol);
            var sar = new double[n]; var b1p = new double[n];
            for (int c = 0; c < n; c++)
            {
                double e2 = e[3 * c].Magnitude * e[3 * c].Magnitude + e[3 * c + 1].Magnitude * e[3 * c + 1].Magnitude + e[3 * c + 2].Magnitude * e[3 * c + 2].Magnitude;
                sar[c] = rho[c] > 0 ? sigma[c] * e2 / (2 * rho[c]) : 0;
                b1p[c] = SarB1.FromH(h[3 * c], h[3 * c + 1], h[3 * c + 2], 2, 1).Plus.Magnitude;
            }
            double sarPeak = sar.Max(), b1Peak = b1p.Max();
            double sarErr = Enumerable.Range(0, n).Max(c => Math.Abs(local[c] - sar[c])) / sarPeak;
            double b1Err = Enumerable.Range(0, n).Max(c => Math.Abs(r.B1Plus![c] - b1p[c])) / b1Peak;
            output.WriteLine($"  {n:N0} cells: local SAR within {sarErr:E2} of the simultaneous run's peak, |B1+| within {b1Err:E2}");
            Assert.True(sarErr < 1e-4, $"local SAR differs from the simultaneous run by {sarErr:E2} of its peak");
            Assert.True(b1Err < 1e-4, $"|B1+| differs from the simultaneous run by {b1Err:E2} of its peak");

            // the fast path's plane is the same field
            int k = set.Z.Count / 2;
            var plane = FieldRecombination.Local(set, a, FieldBox.Plane(set.X, set.Y, set.Z, 2, k), b1: true);
            double planeErr = 0;
            for (int i = 0, at = 0; i < set.X.Count; i++)
                for (int j = 0; j < set.Y.Count; j++, at++)
                    planeErr = Math.Max(planeErr, Math.Abs(plane.LocalSar[at] - sar[(i * set.Y.Count + j) * set.Z.Count + k]) / sarPeak);
            Assert.True(planeErr < 1e-4, $"the fast path's plane differs by {planeErr:E2} of the peak");
        }
    }

    public static Complex[] ReadC8(string path, int cells)
    {
        using var m = PortFieldSet.Mapped.Open(path, "<c8", 3L * cells);
        var f = new float[6 * cells];
        m.Read(0, MemoryMarshal.AsBytes(f.AsSpan()));
        var v = new Complex[3 * cells];
        for (int i = 0; i < v.Length; i++) v[i] = new Complex(f[2 * i], f[2 * i + 1]);
        return v;
    }
}

public sealed class PairFixedFactAttribute : FactAttribute
{
    public PairFixedFactAttribute()
    {
        if (!File.Exists(Path.Combine(PairCosim.Fixed, PairCosim.Simultaneous, PairCosim.Weights)))
            Skip = "needs the committed two-port fixed data in testdata/sar/circuitrf/pair/fixed: PairSuperpositionRerunTests with CRF_WRITE_SAR9_OUTPUTS=1 makes it";
    }
}

public sealed class PairSuperpositionTests(ITestOutputHelper output)
{
    [PairFixedFact]
    public void ASimultaneousTwoPortRunIsTheWeightedSumOfTheSinglePortRuns() => PairCosim.Compare(PairCosim.Fixed, output);
}

public sealed class PairSuperpositionRerunTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar9-pair-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task TheSimultaneousRun_ThroughOpenEms_IsTheRecombination()
    {
        Assert.True(SolverDiscovery.ReadinessFor(Em3dSolverId.OpenEms).All(r => r.Proceeds), "needs openEMS");
        string ws = Path.Combine(_root, "pair");
        foreach (string f in new[] { ".cws", "tech.ctech", "Pair/.ccell", PairCosim.C3d })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ws, f))!);
            File.Copy(Path.Combine(PairCosim.Workspace, f), Path.Combine(ws, f));
        }
        var (exit, stdout, stderr) = Cli("em", Path.Combine(ws, PairCosim.C3d), "--setup", PairCosim.Setup);
        Assert.True(exit == 0, stderr + stdout);
        string results = Path.Combine(ws, "results");
        string runDir = Path.Combine(results, PairCosim.Stem);

        // ── the simultaneous run: port 1's model with port 2's excitation added, scaled ─────────────────────
        string p1 = File.ReadAllText(Path.Combine(runDir, "p1", CsxcadWriter.ModelFile));
        string p2 = File.ReadAllText(Path.Combine(runDir, "p2", CsxcadWriter.ModelFile));
        var excite2 = Regex.Match(p2, "\\s*<Excitation ID=\"\\d+\" Name=\"port2_excite\".*?</Excitation>", RegexOptions.Singleline);
        var excite1 = Regex.Match(p1, "<Excitation ID=\"\\d+\" Name=\"port1_excite\".*?</Excitation>", RegexOptions.Singleline);
        Assert.True(excite1.Success && excite2.Success, "a port's excitation was not found in its model");
        int id = Regex.Matches(p1, "ID=\"(\\d+)\"").Max(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
        string Scaled(System.Text.RegularExpressions.Match m, double k) => Regex.Replace(m.Value, "Excite=\"([^\"]+)\"", x =>
            "Excite=\"" + string.Join(",", x.Groups[1].Value.Split(',').Select(v => (k * double.Parse(v, CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture))) + "\"");
        string second = Regex.Replace(Scaled(excite2, PairCosim.Excite[1]), "ID=\"\\d+\"", $"ID=\"{id}\"");
        string both = p1.Insert(excite1.Index + excite1.Length, second).Replace(excite1.Value, Scaled(excite1, PairCosim.Excite[0]));
        string sim = Path.Combine(runDir, "p12");
        Directory.CreateDirectory(sim);
        File.WriteAllText(Path.Combine(sim, CsxcadWriter.ModelFile), both);
        var psi = new ProcessStartInfo(SolverDiscovery.OpenEms.Find(out _)!.Path)
        {
            WorkingDirectory = sim, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CsxcadWriter.ModelFile);
        using (var proc = Process.Start(psi)!)
        {
            var o = proc.StandardOutput.ReadToEndAsync();
            var e = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Assert.True(proc.ExitCode == 0, await e + await o);
        }

        // ── what the test reads: the field set, the .s2p, and the simultaneous run's fields and incident waves ──
        string outDir = Path.Combine(_root, "fixed");
        Directory.CreateDirectory(Path.Combine(outDir, PairCosim.Simultaneous));
        foreach (string ext in new[] { ".s2p", "_em.npy", ".cemr" })
            File.Copy(runDir + ext, Path.Combine(outDir, PairCosim.Stem + ext));
        string fields = PortFieldSet.FolderForBase(runDir);
        Directory.CreateDirectory(Path.Combine(outDir, Path.GetFileName(fields)));
        foreach (string f in Directory.EnumerateFiles(fields))
            File.Copy(f, Path.Combine(outDir, Path.GetFileName(fields), Path.GetFileName(f)));

        var a = new double[2][];
        for (int k = 0; k < 2; k++)
        {
            var u = FdtdPortTransform.Dft(OpenEmsRun.ReadProbe(Path.Combine(sim, CsxcadWriter.VoltageProbe(k + 1)), out _)!, [PairCosim.Hz])[0];
            var i = FdtdPortTransform.Dft(OpenEmsRun.ReadProbe(Path.Combine(sim, CsxcadWriter.CurrentProbe(k + 1)), out _)!, [PairCosim.Hz])[0];
            var wave = (u + PairCosim.Z0 * i) / (Math.Sqrt(2) * RFNetwork.ComplexSqrt(PairCosim.Z0));
            a[k] = [wave.Real, wave.Imaginary];
        }
        File.WriteAllText(Path.Combine(outDir, PairCosim.Simultaneous, PairCosim.Weights), JsonSerializer.Serialize(a));

        var raw = OpenEmsHdf5.ReadSarRaw(Path.Combine(sim, Em3dSar.RawDumpFile(0)));
        int n = raw.Ex.Length;
        var ef = new float[6 * n];
        for (int c = 0; c < n; c++)
        {
            ef[6 * c] = raw.Ex[c].Re; ef[6 * c + 1] = raw.Ex[c].Im; ef[6 * c + 2] = raw.Ey[c].Re;
            ef[6 * c + 3] = raw.Ey[c].Im; ef[6 * c + 4] = raw.Ez[c].Re; ef[6 * c + 5] = raw.Ez[c].Im;
        }
        WriteC8(Path.Combine(outDir, PairCosim.Simultaneous, "E.npy"), ef, raw.Mesh.Nx, raw.Mesh.Ny, raw.Mesh.Nz);
        var ax = SarAxis.FromCentres(raw.Mesh.X, raw.CellWidthX);
        var ay = SarAxis.FromCentres(raw.Mesh.Y, raw.CellWidthY);
        var az = SarAxis.FromCentres(raw.Mesh.Z, raw.CellWidthZ);
        string abs = Directory.EnumerateFiles(sim, $"{Em3dSar.HDump(0)}_f=*_abs.vtr").Single();
        var hm = VtrFile.Read(abs);
        var ha = VtrFile.Read(abs[..^"_abs.vtr".Length] + "_arg.vtr");
        int[] mx = OpenEmsSarResults.Map(ax, hm.X)!, my = OpenEmsSarResults.Map(ay, hm.Y)!, mz = OpenEmsSarResults.Map(az, hm.Z)!;
        var hf = new float[6 * n];
        for (int i = 0, c = 0; i < ax.Count; i++)
            for (int j = 0; j < ay.Count; j++)
                for (int k = 0; k < az.Count; k++, c++)
                {
                    int node = (mz[k] * hm.Y.Length + my[j]) * hm.X.Length + mx[i];
                    for (int comp = 0; comp < 3; comp++)
                    {
                        var v = Complex.FromPolarCoordinates(hm.Values[3 * node + comp], ha.Values[3 * node + comp]);
                        hf[6 * c + 2 * comp] = (float)v.Real;
                        hf[6 * c + 2 * comp + 1] = (float)v.Imaginary;
                    }
                }
        WriteC8(Path.Combine(outDir, PairCosim.Simultaneous, "H.npy"), hf, ax.Count, ay.Count, az.Count);
        File.WriteAllText(Path.Combine(outDir, "README.md"),
            "# circuitRF OUTPUT for brief-sar-9's superposition gate — not a reference\n\n" +
            $"`{PairCosim.Stem}.s2p`, its `_em.npy`, its `.cemr` and its `.fields` folder are what `circuitrf em ../Pair/3d/Pair.c3d --setup {PairCosim.Setup}` wrote. " +
            $"`{PairCosim.Simultaneous}/` is one openEMS run of port 1's model with port 2's excitation added at {PairCosim.Excite[1]} times its own " +
            "(PairSuperpositionRerunTests): its E and H on the field set's cells (complex64, as solved) and the incident waves its own probes " +
            "measured at the two ports (√W as solved, the field set's power-wave convention). PairSuperpositionTests recombines the field set " +
            "with those waves and holds the result to the simultaneous run's local SAR and B1+ in every cell.\n");

        output.WriteLine("── the rerun");
        PairCosim.Compare(outDir, output);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_SAR9_OUTPUTS") == "1")
        {
            if (Directory.Exists(PairCosim.Fixed)) Directory.Delete(PairCosim.Fixed, true);
            CopyTree(outDir, PairCosim.Fixed);
        }
    }

    private static void WriteC8(string path, float[] values, int nx, int ny, int nz)
    {
        using var fs = File.Create(path);
        fs.Write(PortFieldSet.NpyHeader("<c8", nx, ny, nz, 3));
        fs.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    internal static (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        string cliDir = typeof(PairSuperpositionRerunTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
