// Brief NZ-9 — the oscillator analysis's surfaces: the directive through .cnl and .csch, the `osc` verb as a process over a
// tuning sweep with a Kvco measurement, and `check` saying when the OscProbe is missing.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class OscillatorSurfaceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("osc-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // A cubic negative conductance across an LC tank (the engine's gate 1): at MaxHarm=1 it oscillates at exactly
    // 1/(2π√(LC)), so a sweep of C is a tuning curve with a closed form, f = f0·√(C0/C), and Kvco = df/dC = −f/(2C).
    private const string Tank = @"
cpf = 10
L:L1   n 0  L=10 nH
C:C1   n 0  C=cpf pF
R:RL   n 0  R=1000 Ohm
SDD:D1 n 0  Ports=1  I[1,0]=-2e-3*_v1+1e-3*_v1^3
OscProbe:OP1 n 0 Mode=shunt
analysis OSC1 type=osc Fguess=0.5 Unit=GHz MaxHarm=1 FFTOverSample=4 Tol=1e-12
analysis SW1 type=parametric_sweep Var=cpf Values=9.99,10,10.01 Inner=OSC1
measure Kvco = deriv(OSC1.Fosc)
";

    [Fact]
    public void TheDirective_SurvivesCnlAndCsch()
    {
        var original = new OscillatorAnalysis("OSC7")
        {
            Probe = "X1.OP", FguessExpr = "fv*1.1", FstartExpr = "0.8", FstopExpr = "1.4", Unit = "MHz", MaxHarmonicExpr = "11",
            FFTOverSampleExpr = "2", TolExpr = "1e-9", AguessExpr = "0.3",
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(original);
        var (_, readBack) = new CnlReader().Read(CnlWriter.Write(tb));
        var fromCnl = Assert.IsType<OscillatorAnalysis>(Assert.Single(readBack.Analyses));
        var fromCsch = Assert.IsType<OscillatorAnalysis>(CircuitRF.Design.Schematic.AnalysisSerialization.FromDto(
                                                             CircuitRF.Design.Schematic.AnalysisSerialization.ToDto(original)));
        foreach (var a in new[] { fromCnl, fromCsch })
            Assert.Equal(
                (original.Name, original.Probe, original.FguessExpr, original.FstartExpr, original.FstopExpr, original.Unit,
                 original.MaxHarmonicExpr, original.FFTOverSampleExpr, original.TolExpr, original.AguessExpr),
                (a.Name, a.Probe, a.FguessExpr, a.FstartExpr, a.FstopExpr, a.Unit, a.MaxHarmonicExpr, a.FFTOverSampleExpr, a.TolExpr,
                 a.AguessExpr));
    }

    [Fact]
    public void TheVerb_RunsTheTuningSweep_AndDerivGivesKvco()
    {
        string cnl = Path.Combine(_dir, "tank.cnl");
        File.WriteAllText(cnl, Tank);
        var (exit, stdout, stderr) = RunCli("osc", cnl, "--json");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        string json = doc.RootElement.GetRawText();
        // The middle point's slope, from the run, against −f/(2C) per pF.
        double f0 = 1 / (2 * Math.PI * Math.Sqrt(10e-9 * 10e-12));
        double expected = -f0 / (2 * 10);   // Hz per pF
        double kvco = doc.RootElement.GetProperty("result").GetProperty("groups").GetProperty("measurements")
                         .GetProperty("Kvco").GetProperty("values")[1].GetDouble();
        output.WriteLine($"Kvco {kvco:G9} Hz/pF, closed form {expected:G9}");
        Assert.True(Math.Abs(kvco / expected - 1) < 1e-4, $"Kvco {kvco} vs {expected}; {json[..Math.Min(400, json.Length)]}");
    }

    [Fact]
    public void Check_ReportsAnOscillatorWithNoOscProbe()
    {
        string cnl = Path.Combine(_dir, "noprobe.cnl");
        File.WriteAllText(cnl, Tank.Replace("OscProbe:OP1 n 0 Mode=shunt", ""));
        var (exit, stdout, stderr) = RunCli("check", cnl);
        output.WriteLine(stdout + stderr);
        Assert.Equal(1, exit);
        Assert.Contains("needs one OscProbe", stdout + stderr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(OscillatorSurfaceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
