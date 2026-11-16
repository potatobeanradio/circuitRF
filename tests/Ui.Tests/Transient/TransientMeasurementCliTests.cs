// Gates for brief-trn-5 at the design, display and CLI layers: one trace plots an HB spectrum and a transient's fft unedited; the
// Transient preset's spectrum is the HB probe power; explain steers a long tone-driven transient to harmonic balance; check reports
// a waveform source whose DC value is not its t = 0 value, and a time-domain function written over an S-parameter result.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Design.Circuit;
using CircuitRF.Render.DataDisplay;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Transient;

public sealed class TransientMeasurementCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tran5-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>A weakly nonlinear FET stage, its load behind an IProbe.</summary>
    private const string Amplifier = """
        P1Tone:SRC in 0 Num=1 Pavl=-6 dBm Z=50 Freq=1 GHz
        C:CIN in g C=5p
        L:LG gb g L=20n
        Vdc:VG gb 0 Vdc=-0.8
        FET_Angelov:Q1 g d 0 Ipk=0.08 Vpk=-0.8 P1=1.2 Alpha=2 Lambda=0.02 Cgs=0.4p Cgd=0.04p
        L:LD db d L=20n
        Vdc:VD db 0 Vdc=5
        C:COUT d m C=5p
        L:LM m out L=3n
        C:CM out 0 C=1p
        IProbe:IOUT out o2
        R:RL o2 0 R=50
        """;

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static DataSet Run(string cnl, string analysis)
    {
        var circuit = PreparedCircuit.FromFile(cnl, Path.GetDirectoryName(cnl));
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Analyses = [analysis], Measurements = true });
        var result = CircuitEvaluation.Execute(plan, null, true, [], null);
        Assert.True(result.Status == RunStatus.Success, result.StatusMessage);
        return result.GroupedResults!;
    }

    private DataSet Hb() => Run(Write("hb.cnl", Amplifier + """

        analysis HB1 type=hb Tone=1e9 MaxHarm=12 Tol=1e-9
        measure Vout = HB1.V("out")
        """), "HB1");

    private DataSet Tran() => Run(Write("tr.cnl", Amplifier + """

        analysis TR1 type=tran Stop=30n Step=3.90625p MaxStep=3.90625p RelTol=1e-6
        measure Vout = fft(TR1.V("out"), 1GHz)
        measure Vrms = rms(TR1.V("out"), 25n, 30n)
        measure THD = thd(TR1.V("out"), 1GHz)
        measure Pout = value_at(fft_power(TR1.Vprobe("IOUT"), TR1.I("IOUT"), 1GHz), 1, "harmonic")
        """), "TR1");

    private static double[] Evaluate(string expression, DataSet ds, out string xAxis, out double[] x)
    {
        Assert.True(TraceExpression.TryEvaluate(expression, ds, PlotType.Rect, out x, out var cz, out var rz, out xAxis, out _, out _, out string err), err);
        return rz ?? cz!.Select(z => z.Magnitude).ToArray();
    }

    /// <summary>Gate 3 — one trace definition plots the HB spectrum and the transient fft without edits: the same axis, the same
    /// harmonics, the same values. And the Transient preset's spectrum, the output probe's power through fft_power, is the HB
    /// run's probe power.</summary>
    [Fact]
    public void Gate3_OneTracePlotsBoth_AndThePresetSpectrumIsTheHbProbePower()
    {
        DataSet hb = Hb(), tr = Tran();
        const string trace = "dB(Vout)";
        var yHb = Evaluate(trace, hb, out string axHb, out var xHb);
        var yTr = Evaluate(trace, tr, out string axTr, out var xTr);
        Assert.Equal("harmonic", axHb);
        Assert.Equal(axHb, axTr);
        for (int k = 1; k <= 3; k++)
        {
            output.WriteLine($"k={k}: HB {yHb[k]:F4} dBV, transient {yTr[k]:F4} dBV");
            Assert.Equal(xHb[k], xTr[k]);
            Assert.True(Math.Abs(yHb[k] - yTr[k]) <= 0.01, $"harmonic {k}: {yHb[k]:F4} against {yTr[k]:F4}");
        }

        var plots = TransientPreset.Build(tr, "tr.npy");
        Assert.Equal(3, plots.Count);
        Assert.Contains(plots[0].Traces, t => t.Label == "out");
        string spectrum = plots[1].Traces.Single().Expression!;
        output.WriteLine(spectrum);
        var pTr = Evaluate(spectrum, tr, out string axP, out _);
        var pHb = Evaluate("dBm(HB1.Pprobe_W[0, :])", hb, out _, out _);
        Assert.Equal("harmonic", axP);
        for (int k = 1; k <= 3; k++)
        {
            output.WriteLine($"P k={k}: HB {pHb[k]:F4} dBm, transient {pTr[k]:F4} dBm");
            Assert.True(Math.Abs(pHb[k] - pTr[k]) <= 0.01, $"harmonic {k}: {pHb[k]:F4} against {pTr[k]:F4} dBm");
        }
        Assert.Equal(["Vrms", "THD", "Pout"], plots[2].Traces.Select(t => t.CubeName!));
        // the same power, written as a measure through the probe's name
        Assert.Equal(pHb[1], 10 * Math.Log10(tr["Pout"].RealValues[0] / 1e-3), 0.01);
    }

    /// <summary>Gate 4 — explain on a 2.4 GHz tone over 1 ms prints the steering line naming harmonic balance.</summary>
    [Fact]
    public void Gate4_ExplainSteersALongToneDrivenTransientToHarmonicBalance()
    {
        string cnl = Write("long.cnl", """
            V_1Tone:Vs a 0 Freq=2.4 GHz V=1
            R:R1 a 0 R=50
            analysis TR1 type=tran Stop=1m
            """);
        var run = RunCli("explain", cnl, "--analysis");
        output.WriteLine(run.StdOut + run.StdErr);
        Assert.Contains("Consider another analysis: this bench is 2.4·10⁶ periods of a 2.4 GHz carrier: harmonic balance", run.StdOut);
    }

    /// <summary>R-trn5-6 — check reports a waveform source whose DC value differs from its t = 0 value (info), and a measure calling
    /// a time-domain function over an S-parameter result (an error naming the function).</summary>
    [Fact]
    public void Check_ReportsTheDcStartDifference_AndATimeFunctionOnAFrequencyResult()
    {
        string cnl = Write("chk.cnl", """
            V_Pulse:VP a 0 V1=0 V2=1 Rise=1n Fall=1n Width=10n Period=40n Dc=0.5
            R:R1 a b R=50
            Port:P1 b 0 Num=1 Z=50 Ohm
            analysis TR1 type=tran Stop=100n
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            measure OS = overshoot(SP1.S(1,1))
            measure Ok = overshoot(TR1.V("b"))
            """);
        var run = RunCli("check", cnl);
        output.WriteLine(run.StdOut + run.StdErr);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("'VP' is 0.5 in the DC point", run.StdOut + run.StdErr);
        Assert.Contains("measure 'OS' calls overshoot()", run.StdOut + run.StdErr);
        Assert.DoesNotContain("measure 'Ok'", run.StdOut + run.StdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(TransientMeasurementCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
