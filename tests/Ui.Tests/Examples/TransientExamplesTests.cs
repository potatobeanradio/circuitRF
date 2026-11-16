using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Transient;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

/// <summary>
/// brief TRN-9 gate 1 — the three transient examples run headlessly, each bench from its drawn <c>.csch</c> through the path the
/// <c>tran</c> and <c>hb</c> verbs take (<see cref="PreparedCircuit.FromSchematic"/>, then <see cref="CircuitEvaluation"/>), and
/// produce the numbers their READMEs quote. Each run works on a copy of the workspace: an ngspice run writes its deck beside the
/// bench, and nothing may write into <c>examples/</c>.
/// </summary>
public sealed class TransientExamplesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-trn9-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private static string ExamplesRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return Path.Combine(dir, "examples");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from))
            if (Path.GetFileName(d) != "results") Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Runs one bench of a copied example and returns its measures.</summary>
    private DataSet Run(string workspace, string cell)
    {
        string ws = Path.Combine(_tmp, workspace);
        if (!Directory.Exists(ws)) Copy(Path.Combine(ExamplesRoot(), workspace), ws);
        var circuit = PreparedCircuit.FromSchematic(Path.Combine(ws, cell, "schematic", cell + ".csch"), null);
        Assert.True(circuit.ReadError is null, circuit.ReadError);
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Measurements = true });
        var result = CircuitEvaluation.Execute(plan, null, true, [], null);
        Assert.True(result.Status == RunStatus.Success, $"{workspace}/{cell}: {result.StatusMessage}");
        foreach (string n in result.Notes) output.WriteLine($"{cell}: {n}");
        return result.GroupedResults!;
    }

    private static double M(DataSet ds, string name) => ds.CubesIn(DataSet.MeasurementsGroup)[name].RealValues[0];

    private static DataCube Cube(DataSet ds, string name)
        => ds.Groups.SelectMany(g => ds.CubesIn(g)).First(kv => kv.Key == name).Value;

    /// <summary>Every native bench and the seeded harmonic balance, against the README's numbers.</summary>
    [Theory]
    [InlineData("Pulse Response", "Filter_Native", "Overshoot_pct", 12.73, 0.01)]
    [InlineData("Pulse Response", "Filter_Native", "Trise", 409e-12, 1e-12)]
    [InlineData("Pulse Response", "Line_Native", "Gamma", 1.0 / 3, 1e-6)]
    [InlineData("Buck Converter", "SteadyState", "Ripple", 0.0400, 0.0005)]
    [InlineData("Buck Converter", "SteadyState", "Vout", 5.517, 0.002)]
    [InlineData("Class-E with a Transient Seed", "ClassE_HB", "DE_pct", 87.90, 0.01)]
    [InlineData("Class-E with a Transient Seed", "ClassE_HB", "Pout_dBm", 31.42, 0.01)]
    public void NativeBench_GivesItsReadmeNumber(string workspace, string cell, string measure, double expected, double tol)
    {
        double got = M(Run(workspace, cell), measure);
        output.WriteLine($"{cell}.{measure} = {got:G6}");
        Assert.InRange(got, expected - tol, expected + tol);
    }

    /// <summary>The start-up bench settles to the steady-state bench's output, which is what its IC= values were read from; and the
    /// seeded harmonic balance was seeded, settled, and agrees with the transient bench's power to 0.01 dB.</summary>
    [Fact]
    public void StartUpMeetsSteadyState_AndTheSeedAgreesWithTheTransientBench()
    {
        var startup = Run("Buck Converter", "Startup");
        var steady = Run("Buck Converter", "SteadyState");
        output.WriteLine($"Vout {M(startup, "Vout"):G6} / {M(steady, "Vout"):G6}, valley {M(startup, "IL_valley"):G6} / {M(steady, "IL_valley"):G6}");
        Assert.InRange(M(startup, "Vout") - M(steady, "Vout"), -0.002, 0.002);
        Assert.InRange(M(startup, "IL_valley") - M(steady, "IL_valley"), -0.002, 0.002);

        var hb = Run("Class-E with a Transient Seed", "ClassE_HB");
        var tr = Run("Class-E with a Transient Seed", "ClassE_Transient");
        Assert.Equal(1, Cube(hb, "Seed.Settled").RealValues[0]);
        output.WriteLine($"Pout HB {M(hb, "Pout_dBm"):G6} dBm, transient {M(tr, "Pout_dBm"):G6} dBm");
        Assert.InRange(M(hb, "Pout_dBm") - M(tr, "Pout_dBm"), -0.01, 0.01);
    }

    /// <summary>The ngspice benches give the native benches' numbers; skipped, with the reason, where no ngspice is discovered.</summary>
    [NgspiceFact]
    public void NgspiceBenches_MatchTheNativeBenches()
    {
        var fn = Run("Pulse Response", "Filter_Native");
        var fs = Run("Pulse Response", "Filter_ngspice");
        var ln = Run("Pulse Response", "Line_Native");
        var ls = Run("Pulse Response", "Line_ngspice");
        Assert.Contains(fs.Groups.SelectMany(g => fs.CubesIn(g).Keys), k => k.Contains("Ngspice", StringComparison.Ordinal));
        foreach (var (a, b, m, tol) in new[] { (fn, fs, "Overshoot_pct", 0.01), (fn, fs, "Trise", 1e-12), (ln, ls, "Gamma", 1e-6) })
        {
            output.WriteLine($"{m}: native {M(a, m):G6}, ngspice {M(b, m):G6}");
            Assert.InRange(M(a, m) - M(b, m), -tol, tol);
        }
    }
}
