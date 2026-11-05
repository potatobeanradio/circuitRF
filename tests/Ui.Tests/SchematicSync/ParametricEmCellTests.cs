using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-6 §5 — a schematic simulated by EM of its own layout (Path A), against the real planar solver, ONCE: the
/// identity test, on C1's three lines at a coarse mesh and three frequencies. Every other SDL-6 behaviour is held with a
/// stub provider (<see cref="ParametricEmStubTests"/>) or without solving (counting, refusals, the cache key).
/// </summary>
public sealed class ParametricEmCellTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl6-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Path A identity: C1 with <c>SP1 model=em</c> gives the S-parameters of running <c>em</c> on C1's layout; the cached
    /// Touchstone is that run's byte for byte but the provenance write-timestamp; a second run reads the cache; and the
    /// <c>.csch</c>, <c>.clay</c> and <c>.cem</c> are unchanged.
    /// </summary>
    /// <para>Measured at 14 s (two coarse solves: the reference and the run), so it is in the Benchmark tier.</para>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void PathA_C1_EqualsEmOfItsLayout_AndWritesNoUserFile()
    {
        var fx = EmCellFixture.Create("C1-through-line", _scratch, sparamModel: AnalysisModel.Em);

        // The reference: `em` on the layout as saved, as the CLI and Simulate run it.
        var setup = EmSetupPersistence.LoadFromFile(fx.Cem);
        var source = EmSetupResolver.Resolve(fx.Cem, setup.LayoutRef, fx.Cws, new TechnologyCache()).Source;
        string refRoot = Path.Combine(_scratch, "reference");
        var reference = EmRunService.Run(setup, source, refRoot);
        Assert.Equal(EmRunStatus.Ok, reference.Status);

        var before = fx.UserFileBytes();
        var circuit = PreparedCircuit.FromSchematic(fx.Csch);
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { EmMaxSolves = 1 });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);

        var s = run.Results.Single(r => r.Name == "SP1").Data["S"];
        AssertSame(RfCore.TouchstoneIO.ReadFile(reference.SnpPath!), s);
        Assert.Contains(EmResultStamp.Read(run.Results.Single(r => r.Name == "SP1").Data), l => l.StartsWith("model = em"));

        string cached = Assert.Single(Directory.GetFiles(EmCellCache.CellFolder(fx.Workspace, "C1-through-line"), "*.s2p"));
        Assert.Equal(WithoutTimestamp(File.ReadAllText(reference.SnpPath!)), WithoutTimestamp(File.ReadAllText(cached)));
        Assert.Equal(before, fx.UserFileBytes());

        // A second run reads the cache: the count says no solve is needed.
        var plan = CircuitEvaluation.Plan(PreparedCircuit.FromSchematic(fx.Csch), new CircuitEvaluationRequest());
        var count = CircuitEvaluation.CountEmSolves(plan);
        Assert.Equal((1, 1, 0), (count.Geometries, count.Cached, count.ToSolve));
    }

    private static void AssertSame(RfCore.SNP expected, DataCube s)
    {
        int n = expected.Ports;
        Assert.Equal(expected.Frequencies.Length, s.Axes[0].Length);
        for (int f = 0; f < expected.Frequencies.Length; f++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var a = expected.Matrices[f][i, j];
                    var b = s.At("freq", f).At("i", i).At("j", j).ComplexValues[0];
                    Assert.True((a - b).Magnitude < 1e-6, $"S{i + 1}{j + 1} at point {f}: {a} vs {b}");
                }
    }

    private static string WithoutTimestamp(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.Contains("circuitRF-EM written:", StringComparison.Ordinal) &&
                                                       !l.Contains(EmProvenanceStamp.StampPrefix, StringComparison.Ordinal)));
}
