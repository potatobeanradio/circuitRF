// ================================================================
//  OptimizationExampleTests.cs — the shipped Optimization example (brief-tuneopt-12 §3): an L-section match,
//  a bandpass filter set up for minimax, and an amplifier with a complex source impedance and one range
//  drawn too narrow on purpose.
//
//  Every run is the CLI as a process on the shipped .csch, with the algorithm and seed the file saves, so
//  what is asserted is what a reader gets. Evaluation counts are bounded, never timed.
// ================================================================

using System.Text.Json.Nodes;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Optimization;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class OptimizationExampleTests
{
    /// <summary><c>check</c> on the workspace: every cell clean, no warning.</summary>
    [Fact]
    public void Check_IsClean()
    {
        var (exit, stdout, stderr) = OptCli.Run("check", Root(), "--json");
        Assert.True(exit == 0, stderr);
        var diags = JsonNode.Parse(stdout)!["diagnostics"]!.AsArray();
        Assert.DoesNotContain(diags, d => d!["severity"]!.GetValue<string>() is "error" or "warning");
    }

    /// <summary>The L-section and the filter meet every goal with their saved algorithm and seed, within an
    /// evaluation budget; the filter's "Equiripple" preset is that run's best point.</summary>
    [Theory]
    [InlineData("LSectionMatch", 100)]
    [InlineData("BandpassFilter", 200)]
    public void SavedSetup_MeetsEveryGoal(string cell, int maxEvaluations)
    {
        var r = Optimize(cell, out int exit);
        Assert.Equal(0, exit);
        Assert.Equal("goalsMet", r["outcome"]!.GetValue<string>());
        Assert.InRange(r["evaluations"]!.GetValue<long>(), 1, maxEvaluations);

        if (cell != "BandpassFilter") return;
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cell));
        var preset = model.Tuning!.Presets.Single(p => p.Name == "Equiripple");
        // Each value to 12 significant digits with its unit: the optimizer's last digits follow the platform's maths library.
        foreach (var v in r["variables"]!.AsArray())
            Assert.Equal(Digits12(preset.Values[v!["key"]!.GetValue<string>()]), Digits12(v["best"]!.GetValue<string>()));

        static string Digits12(string t) => System.Text.RegularExpressions.Regex.Replace(t, @"-?\d+\.\d+(?:[eE][-+]?\d+)?", m =>
            double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture).ToString("G12", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The amplifier as shipped: Rstab rails at the top of its range and the goals are unmet
    /// (exit 3) — the README's lesson and the Optimizer figure's caption.</summary>
    [Fact]
    public void Amplifier_RailsRstab_AndFallsShort()
    {
        var r = Optimize("StabilityAndGain", out int exit);
        Assert.Equal(3, exit);
        var rstab = r["variables"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "Rstab")!;
        Assert.Equal("max", rstab["railed"]!.GetValue<string>());
    }

    private static JsonObject Optimize(string cell, out int exit)
    {
        var (code, stdout, stderr) = OptCli.Run("opt", Csch(cell), "--json");
        exit = code;
        Assert.True(code is 0 or 3, stderr);
        return OptCli.Report(stdout);
    }

    internal static string Csch(string cell) => Path.Combine(Root(), cell, "schematic", cell + ".csch");

    private static string Root()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return Path.Combine(dir, "examples", "Optimization");
        throw new InvalidOperationException("repo root not found");
    }
}
