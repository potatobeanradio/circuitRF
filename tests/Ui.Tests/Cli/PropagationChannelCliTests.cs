using System.Diagnostics;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

/// <summary>
/// Brief PCH-6, through the CLI as a process. Gate 6: a cycle of two Propagation Channels in an envelope bench is refused by
/// <c>check</c>, naming both. And <c>explain --analysis</c> says each channel's model and edition, its breakdown, τ0 and ν0,
/// the rays and, for an envelope run, the regime.
/// </summary>
public sealed class PropagationChannelCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pch6-cli-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Gate6_ACycleOfTwoChannels_IsRefusedNamingBoth()
    {
        // side A (a, a2) reaches side B (b, b2) through CH1, and side B reaches side A again through CH2
        string path = Write("cycle.cnl", """
            MOD_Source:S1 a 0 Fc=2e9 Pavg=-10 Z=50 Ohm Carrier[1]="LTE;BW=1.4MHz;Mod=QPSK;Off=0;P=0"
            R:RA a a2 R=10 Ohm
            PropagationChannel:CH1 a b Distance=100
            Amp:AB b 0 b2 0 Gain=10 IP3=200
            PropagationChannel:CH2 b2 a2 Distance=100
            R:RB b2 0 R=50 Ohm
            analysis ENV1 type=env Stop=20e-6
            """);
        var run = RunCli("check", path, "--json");
        output.WriteLine(run.StdOut + run.StdErr);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("check.channel.cycle", run.StdOut);
        Assert.Contains("'CH1'", run.StdErr);                         // the JSON escapes the apostrophes; the sentence on stderr does not
        Assert.Contains("'CH2'", run.StdErr);
    }

    [Fact]
    public void Explain_SaysTheModelTheBreakdownAndTheRegime()
    {
        string path = Write("link.cnl", """
            MOD_Source:S1 a 0 Fc=2e9 Pavg=-10 Z=50 Ohm Carrier[1]="LTE;BW=5MHz;Mod=QPSK;Off=0;P=0"
            PropagationChannel:CH1 a b Distance=300 PathLoss=tworay TxHeight=10 RxHeight=1.5
            R:RL b 0 R=50 Ohm
            analysis ENV1 type=env Stop=20e-6
            """);
        var run = RunCli("explain", path, "--analysis");
        output.WriteLine(run.StdOut + run.StdErr);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Channel 'CH1': model tworay", run.StdOut);
        Assert.Contains("ground reflection", run.StdOut);
        Assert.Contains("bulk delay τ0", run.StdOut);
        Assert.Contains("2 ray(s)", run.StdOut);
        Assert.Contains("regime: Doppler spread f_D = 0 Hz", run.StdOut);
        Assert.Contains("coherence bandwidth", run.StdOut);
    }

    // ── The CLI as a process (as NoiseExplainCliTests runs it) ───────────────────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(PropagationChannelCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}
