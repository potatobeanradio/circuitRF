// ================================================================
//  EnvelopeExportCliTests.cs — `env -o` keeps circuitRF's CLI export layout
//
//  Every run verb's `-o` writes the engine's cubes un-grouped (`hb -o`:
//  V, INl, ...); only the GUI's results/<cell>.npy groups them per
//  analysis. `env -o` follows the CLI style. The end-to-end case is also
//  the guard on two crashes: a single-carrier run falling into the two-
//  fundamental branch of the result builder, and a purely resistive
//  network (C = 0) in the engine's tangent check.
// ================================================================

using System.Diagnostics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class EnvelopeExportCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-envexp-" + Guid.NewGuid().ToString("N"));

    public EnvelopeExportCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ASingleCarrierRun_ExportsTheCliLayout_LikeHb()
    {
        string cnl = Path.Combine(_dir, "cw.cnl"), npy = Path.Combine(_dir, "cw.npy");
        File.WriteAllText(cnl,
            "MOD_Source:S1 n1 0 Fc=1e9 Pavg=0 Z=50 Carrier[1]=\"CW;Off=0;P=0\"\n" +
            "IProbe:Iout n1 o\n" +
            "R:RL o 0 R=50\n" +
            "analysis ENV1 type=env Stop=2e-6 Accuracy=draft Probes=\"n1\" Segments=1 Warmup=0\n");

        var run = RunCli("env", cnl, "-o", npy);

        Assert.True(run.ExitCode == 0, run.StdErr);
        var ds = DataSetImporter.Import(npy).DataSet;
        Assert.Equal([DataSet.DefaultGroup], ds.Groups.ToArray());   // bare names, as `hb -o` writes them
        Assert.True(ds.Cubes.ContainsKey("PSD"));
        // 0 dBm available into a matched 50 ohm load is 0 dBm delivered
        Assert.Equal(0.0, ds.Cubes["Pavg_dBm"].RealValues[0], 2);
    }

    /// <summary>
    /// The verb rebuilds the analysis to apply its overrides; the noise keys go with it (NZ-7, NZ-8). Rebuilt without them,
    /// Noise=yes ran noiseless headlessly and EvmSplit wrote no split.
    /// </summary>
    [Fact]
    public void TheNoiseKeys_ReachTheRun()
    {
        string cnl = Path.Combine(_dir, "lte.cnl"), npy = Path.Combine(_dir, "lte.npy");
        File.WriteAllText(cnl,
            "MOD_Source:S1 s 0 Fc=1e9 Pavg=-60 Z=50 Record=0.5e-3 Carrier[1]=\"LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=0\"\n" +
            "IProbe:Iout s o\n" +
            "R:RL o 0 R=50\n" +
            "analysis ENV1 type=env Stop=0.5e-3 Accuracy=draft Segments=1 Warmup=1e-6 Noise=yes EvmSplit=yes\n");

        var run = RunCli("env", cnl, "-o", npy);

        Assert.True(run.ExitCode == 0, run.StdErr);
        var ds = DataSetImporter.Import(npy).DataSet;
        Assert.True(ds.Cubes.ContainsKey("SNR_dB"), string.Join(", ", ds.Cubes.Keys));
        Assert.Contains("SNR dB", run.StdOut);
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(EnvelopeExportCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
