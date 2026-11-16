// Gates for brief-trn-1 at the design and CLI layers: Simulate's path (CircuitEvaluation) runs a tran analysis; the verb as a process
// exports the same bytes as that in-process run; a tran inside a parametric sweep is promoted to the sweep; the directive survives the
// netlist and the schematic; and Solver=ngspice with no ngspice discovered is refused with its sentence (brief TRN-7), never run native.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Transient;

public sealed class TransientCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tran-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private const string Rc = """
        V_1Tone:Vs in 0 Freq=1e6 V=1 Phase=0 Vdc=0.5
        R:R1 in out R=1 kOhm
        C:C1 out 0 C=1 nF
        IProbe:I1 out o2
        R:R2 o2 0 R=10 kOhm
        analysis TR1 type=tran Stop=3e-6 Step=10e-9
        """;

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static RunResult Evaluate(string cnl, string analysis, bool measurements)
    {
        var circuit = PreparedCircuit.FromFile(cnl, Path.GetDirectoryName(cnl));
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Analyses = [analysis], Measurements = measurements });
        var result = CircuitEvaluation.Execute(plan, null, measurements, [], null);
        Assert.True(result.Status == RunStatus.Success, result.StatusMessage);
        return result;
    }

    /// <summary>Simulate's path runs the analysis and reports the operating point it started from, every source at its t = 0 value.</summary>
    [Fact]
    public void Simulate_RunsTheTransient_FromTheOperatingPoint()
    {
        var ds = Evaluate(Write("rc.cnl", Rc), "TR1", false).Results[0].Data;
        var v = ds["V"];
        Assert.Equal("time", v.Axes[0].Name);
        int outIdx = Array.IndexOf(v.Axes[1].Labels!, "out");
        Assert.True(outIdx >= 0);
        // The starting state is solved with every source at its value at t = 0. A tone is Re(V e^(j(ωt + φ))), harmonic balance's
        // phasor, so with Phase=0 it starts at its peak: 0.5 + 1 V through 1 kΩ into 10 kΩ. (TRN-2's Delay and Ramp start one at 0.)
        // (to 1e-7: every node carries the 1e-12 S gmin the linear partition always has)
        Assert.Equal(1.5 * 10 / 11, ds["OP.V"].RealValues[outIdx], 7);
        Assert.Equal(1.5 * 10 / 11, v.RealValues[outIdx], 7);
        Assert.True(ds["Tran.AcceptedSteps"].RealValues[0] > 0);
    }

    /// <summary>Gate 7 — the verb as a process exports the same bytes as the in-process run of the same bench.</summary>
    [Fact]
    public void TheCliVerb_ExportsTheSameBytes_AsTheInProcessRun()
    {
        string cnl = Write("rc.cnl", Rc);
        var result = Evaluate(cnl, "TR1", true);
        string inProcess = Path.Combine(_dir, "in.npy");
        DataSetExporter.Export(result.GroupedResults!, inProcess, ExportFormat.Npy, new ExportOptions(Format: ExportFormat.Npy));

        string viaCli = Path.Combine(_dir, "cli.npy");
        var cli = RunCli("tran", cnl, "-o", viaCli);
        output.WriteLine(cli.StdOut);
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        Assert.Equal(File.ReadAllBytes(inProcess), File.ReadAllBytes(viaCli));
    }

    /// <summary>A tran inside a parametric sweep is run as the sweep, with its axis, as `hb` promotes an HB.</summary>
    [Fact]
    public void ATranInsideASweep_IsPromotedToTheSweep()
    {
        string cnl = Write("sw.cnl", """
            Vdc:V1 in 0 Vdc=Vin
            R:R1 in out R=1 kOhm
            C:C1 out 0 C=1 nF
            Vin = 1
            analysis TR1 type=tran Stop=2e-6 Step=20e-9 InitialState=zero
            analysis SW1 type=parametric_sweep Var=Vin Inner=TR1 Values=1,2
            """);
        var cli = RunCli("tran", cnl, "-a", "TR1");
        output.WriteLine(cli.StdOut + cli.StdErr);
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        var ds = Evaluate(cnl, "TR1", false).Results[0].Data;
        var v = ds["V"];
        Assert.Equal("Vin", v.Axes[0].Name);
        Assert.Equal(3, v.Axes.Count);
    }

    /// <summary>Every key survives the netlist writer and reader, and the schematic's serialization.</summary>
    [Fact]
    public void TheDirective_SurvivesTheNetlistAndTheSchematic()
    {
        var original = new TransientAnalysis("TR7")
        {
            Enabled = false, StopExpr = "tstop", StepExpr = "1n", StartExpr = "2n", MaxStepExpr = "5n", Method = TransientAnalysis.MethodGear2,
            RelTolExpr = "1e-4", VTolExpr = "1e-7", ITolExpr = "1e-13", InitialState = TransientAnalysis.InitialZero, IcExpr = "out=0, b=1.2",
            Solver = TransientAnalysis.SolverNgspice, SaveSteps = "yes", MaxStepsExpr = "1000", SpiceOptions = "gmin=1e-15 reltol=1e-4",
            SpiceLines = "rlgc", SpiceLineFreq = "2e9", SpiceFit = "yes",
            Store = TransientAnalysis.StoreBaseband, StoreHarmExpr = "5", MeasureFromExpr = "200n",   // brief MTR-3
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(original);
        var (_, readBack) = new CnlReader().Read(CnlWriter.Write(tb));
        var fromCnl = Assert.IsType<TransientAnalysis>(Assert.Single(readBack.Analyses));
        var fromCsch = Assert.IsType<TransientAnalysis>(CircuitRF.Design.Schematic.AnalysisSerialization.FromDto(
                                                            CircuitRF.Design.Schematic.AnalysisSerialization.ToDto(original)));
        static object Key(TransientAnalysis t) => string.Join("|", t.Name, t.Enabled, t.StopExpr, t.StepExpr, t.StartExpr, t.MaxStepExpr, t.Method,
            t.RelTolExpr, t.VTolExpr, t.ITolExpr, t.InitialState, t.IcExpr, t.Solver, t.SaveSteps, t.MaxStepsExpr, t.SpiceOptions,
            t.SpiceLines, t.SpiceLineFreq, t.SpiceFit, t.Store, t.StoreHarmExpr, t.MeasureFromExpr);
        Assert.Equal(Key(original), Key(fromCnl));
        Assert.Equal(Key(original), Key(fromCsch));
    }

    /// <summary>brief TRN-7 gate 4, the CLI's half — Solver=ngspice with no usable ngspice (CIRCUITRF_NGSPICE names a file that is
    /// not there, which discovery takes as the answer) exits 1 with the sentence naming Settings ▸ Solvers and the install verb,
    /// and never runs native.</summary>
    [Fact]
    public void Ngspice_WithNoneDiscovered_IsRefused_NotRunNative()
    {
        string cnl = Write("ng.cnl", Rc.Replace("Step=10e-9", "Step=10e-9 Solver=ngspice"));
        var env = new System.Collections.Generic.Dictionary<string, string>
        {
            ["CIRCUITRF_NGSPICE"] = Path.Combine(Path.GetTempPath(), "no-such-ngspice-" + Guid.NewGuid().ToString("N")),
        };
        var cli = RunCliWith(env, "tran", cnl);
        Assert.Equal(1, cli.ExitCode);
        Assert.Contains("Settings ▸ Solvers", cli.StdErr + cli.StdOut);
        Assert.Contains("circuitrf solver install ngspice", cli.StdErr + cli.StdOut);
        Assert.DoesNotContain("Tran.AcceptedSteps", cli.StdOut);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args) => RunCliWith(new(), args);

    private static (int ExitCode, string StdOut, string StdErr) RunCliWith(System.Collections.Generic.Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        string cliDir = typeof(TransientCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
