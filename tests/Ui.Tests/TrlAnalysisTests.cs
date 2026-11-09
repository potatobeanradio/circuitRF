// Gates for brief-de-4: the trl analysis, run as Simulate runs it, writes two fixture files that two Deembed blocks turn back into
// the part; the CLI verb writes the same bytes; the directive survives both file formats; and a wrong bench is refused by name.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class TrlAnalysisTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trl-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Synthetic standards: two different launches, measured through as an instrument would ─────────────────────────────
    // Nodes are joined, never linked by a near-zero resistor: a 1e-9 ohm link beside 50 ohm ports costs ~5e-6 of accuracy
    // in the MNA solve, which is larger than anything this test measures.

    private const string Ports = "Port:P1 p1 0 Num=1 Z=50 Ohm\nPort:P2 p2 0 Num=2 Z=50 Ohm\n";
    private const string A = "L:LA p1 a1 L=0.3 nH\nC:CA a1 0 C=0.15 pF\nTLIN:TA a1 x Z=57.5 E=25 deg F=1 GHz A=0.05\n";
    private static string B(string from) => $"TLIN:TB {from} b1 Z=45 E=20 deg F=1 GHz A=0.04\nC:CB b1 0 C=0.1 pF\nL:LB b1 p2 L=0.2 nH\n";
    private const string Dut = "C:Cd1 x 0 C=0.4 pF\nL:Ld x y L=1.5 nH\nC:Cd2 y 0 C=0.4 pF\n";
    private const string Sweep = "analysis SP1 type=sparam start=1 stop=8 npts=29 Unit=GHz\n";

    /// <summary>Simulates a netlist with circuitRF and writes it as a Touchstone file under the test folder.</summary>
    private string Measure(string name, string netlist)
    {
        string cnl = Path.Combine(_dir, name + ".cnl");
        File.WriteAllText(cnl, netlist + Sweep);
        var snp = DataSetBuilder.ToSnp(Run(cnl, "SP1").Data);
        snp.Format = MatrixFormat.RI;
        string path = Path.Combine(_dir, "m", name + ".s2p");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TouchstoneIO.WriteFile(snp, path, precision: "G17");
        return path;
    }

    private void MeasureStandards(double lineDeg = 20)
    {
        Measure("thru", Ports + A + B("x"));
        Measure("line", Ports + A + $"TLIN:TL x y Z=50 E={lineDeg} deg F=1 GHz A=0.02\n" + B("y"));
        Measure("reflect", Ports + A + "L:Ls1 x 0 L=0.01 nH\nL:Ls2 y 0 L=0.01 nH\n" + B("y"));
        Measure("dut", Ports + A + Dut + B("y"));
    }

    private const string Standards = """
        Port:P1 t1 0 Num=1 Z=50 Ohm
        Port:P2 t2 0 Num=2 Z=50 Ohm
        Port:P3 l1 0 Num=3 Z=50 Ohm
        Port:P4 l2 0 Num=4 Z=50 Ohm
        Port:P5 r1 0 Num=5 Z=50 Ohm
        Port:P6 r2 0 Num=6 Z=50 Ohm
        SnP:THRU t1 t2 File="m/thru.s2p" NumPorts=2
        SnP:LINE l1 l2 File="m/line.s2p" NumPorts=2
        SnP:REFLECT r1 r2 File="m/reflect.s2p" NumPorts=2

        """;

    private const string Cal = "analysis CAL type=trl thru=THRU reflect=REFLECT line=LINE reflectType=short fixtureA=\"fixtures/A.s2p\" fixtureB=\"fixtures/B.s2p\"\n";

    private string WriteBench(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private sealed record Ran(DataSet Data, IReadOnlyList<string> Warnings);

    /// <summary>One analysis of a netlist, through the evaluation Simulate and every run verb share.</summary>
    private static Ran Run(string cnl, string analysis)
    {
        var circuit = PreparedCircuit.FromFile(cnl, Path.GetDirectoryName(cnl));
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Analyses = [analysis] });
        var result = CircuitEvaluation.Execute(plan, null, false, [], null);
        var failed = result.Convergence.Where(c => c.Converged == false && c.Detail is not null).Select(c => c.Detail).ToList();
        if (result.Status != RunStatus.Success || result.Results.Count == 0 || failed.Count > 0)
            throw new InvalidOperationException(failed.FirstOrDefault() ?? result.StatusMessage);
        return new Ran(result.Results[0].Data, result.Warnings);
    }

    private static Mat2[] S(DataSet ds) => [.. DataSetBuilder.ToSnp(ds).Matrices.Select(m => new Mat2(m[0, 0], m[0, 1], m[1, 0], m[1, 1]))];
    private readonly record struct Mat2(Complex S11, Complex S12, Complex S21, Complex S22)
    {
        public double Diff(Mat2 o) => new[] { S11 - o.S11, S12 - o.S12, S21 - o.S21, S22 - o.S22 }.Max(d => d.Magnitude);
    }

    [Fact]
    public void TheFixturesItWrites_TurnTheMeasuredPartBackIntoThePart_ThroughTwoDeembeds()
    {
        MeasureStandards();
        var ran = Run(WriteBench("cal.cnl", Standards + Cal), "CAL");
        Assert.True(File.Exists(Path.Combine(_dir, "fixtures", "A.s2p")) && File.Exists(Path.Combine(_dir, "fixtures", "B.s2p")));
        Assert.Contains("SA", ran.Data.Cubes.Keys);
        Assert.Empty(ran.Warnings);

        string deembed = WriteBench("de.cnl", Ports +
            "Deembed:DA p1 x File=\"fixtures/A.s2p\" NumPorts=2\nSnP:M x y File=\"m/dut.s2p\" NumPorts=2\nDeembed:DB y p2 File=\"fixtures/B.s2p\" NumPorts=2\n" + Sweep);
        string truth = WriteBench("truth.cnl", "Port:P1 x 0 Num=1 Z=50 Ohm\nPort:P2 y 0 Num=2 Z=50 Ohm\n" + Dut + Sweep);
        var got = S(Run(deembed, "SP1").Data);
        var want = S(Run(truth, "SP1").Data);
        double worst = got.Zip(want, (g, w) => g.Diff(w)).Max();
        output.WriteLine($"de-embedded part against the part alone: max |ΔS| = {worst:E2}");
        Assert.True(worst <= 1e-9, $"max |ΔS| = {worst:E2}");
    }

    [Fact]
    public void PointsOutsideTheUsableBand_AreWarnedAboutAsRuns_AndTheRunStillSucceeds()
    {
        MeasureStandards(lineDeg: 10);   // 10° at 1 GHz: under 20° up to 2 GHz, 80° at 8
        var ran = Run(WriteBench("cal.cnl", Standards + Cal), "CAL");
        string w = Assert.Single(ran.Warnings, x => x.Contains("TRL 'CAL'", StringComparison.Ordinal));
        output.WriteLine(w);
        Assert.Contains("(1-1.75 GHz)", w);
        Assert.Contains("20-160 degrees", w);
    }

    [Fact]
    public void TheCliVerb_WritesTheSameFixtureBytes_AsTheInProcessRun()
    {
        MeasureStandards();
        Run(WriteBench("cal.cnl", Standards + Cal), "CAL");
        string inProcess = File.ReadAllText(Path.Combine(_dir, "fixtures", "A.s2p")) + File.ReadAllText(Path.Combine(_dir, "fixtures", "B.s2p"));

        var cli = RunCli("trl", Path.Combine(_dir, "cal.cnl"), "--fixture-a", "cli/A.s2p", "--fixture-b", "cli/B.s2p");
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        string viaCli = File.ReadAllText(Path.Combine(_dir, "cli", "A.s2p")) + File.ReadAllText(Path.Combine(_dir, "cli", "B.s2p"));
        Assert.Equal(inProcess, viaCli);
    }

    // The verb prints and exports the bench's measure lines, as Simulate shows them: they are evaluated into the run's
    // grouped results, beside the analysis's own group.
    [Fact]
    public void TheCliVerb_ReportsTheBenchsMeasurements()
    {
        MeasureStandards();
        WriteBench("meas.cnl", Standards + Cal + "measure LineTwice = 2*CAL.LineDeg\n");
        string npy = Path.Combine(_dir, "meas.npy");
        var cli = RunCli("trl", Path.Combine(_dir, "meas.cnl"), "-o", npy);
        output.WriteLine(cli.StdOut);
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        Assert.Contains("measurements:", cli.StdOut);
        Assert.Contains("LineTwice", cli.StdOut);
        Assert.Contains("CAL:", cli.StdOut);
    }

    [Fact]
    public void TheDirective_SurvivesTheNetlistAndTheSchematic()
    {
        var original = new TrlAnalysis("CAL7")
        {
            Thru = "X1.THRU", Reflect = "R9", Line = "L2", ReflectType = TrlAnalysis.ReflectOpen,
            FixtureA = "fix/a b.s2p", FixtureB = "fix/b.s2p", Enabled = false,
        };
        var tb = new TestBench("tb");
        tb.Analyses.Add(original);
        var (_, readBack) = new CnlReader().Read(CnlWriter.Write(tb));
        var fromCnl = Assert.IsType<TrlAnalysis>(Assert.Single(readBack.Analyses));
        var fromCsch = Assert.IsType<TrlAnalysis>(CircuitRF.Design.Schematic.AnalysisSerialization.FromDto(
                                                       CircuitRF.Design.Schematic.AnalysisSerialization.ToDto(original)));
        foreach (var t in new[] { fromCnl, fromCsch })
            Assert.Equal((original.Name, original.Thru, original.Reflect, original.Line, original.ReflectType, original.FixtureA, original.FixtureB, original.Enabled),
                         (t.Name, t.Thru, t.Reflect, t.Line, t.ReflectType, t.FixtureA, t.FixtureB, t.Enabled));
    }

    [Fact]
    public void AStandardThatIsNotATwoPortSnP_IsRefusedByName()
    {
        MeasureStandards();
        var resistor = Assert.Throws<InvalidOperationException>(() =>
            Run(WriteBench("r.cnl", Standards + "R:R1 t1 0 R=50 Ohm\n" + Cal.Replace("thru=THRU", "thru=R1")), "CAL"));
        Assert.Contains("thru=R1 is a R", resistor.Message);

        File.WriteAllText(Path.Combine(_dir, "m", "four.s4p"), "# GHz S RI R 50\n1" + string.Concat(Enumerable.Repeat(" 0 0", 16)) + "\n");
        var four = Assert.Throws<InvalidOperationException>(() =>
            Run(WriteBench("f.cnl", Standards + "SnP:F4 a b c d File=\"m/four.s4p\" NumPorts=4\n" + Cal.Replace("line=LINE", "line=F4")), "CAL"));
        Assert.Contains("line=F4 is a 4-port SnP", four.Message);
    }

    // ── CLI plumbing, the pattern EmCliVerbTests established ────────────────────────────────────────────────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string cliDir = typeof(TrlAnalysisTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
