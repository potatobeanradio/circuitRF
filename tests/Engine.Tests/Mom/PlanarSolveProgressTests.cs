// Owner request, 2026-08-09: EM runs need live feedback, and a Stop.
//
// RunControl carries BOTH, which is why the sweep takes one object rather than a progress delegate
// and a token. These gates drive the REAL solver — a bar that is wired but never ticked, or a token
// that is threaded but never observed, both look exactly like working code from the call site.

using System;
using System.Collections.Generic;
using System.Threading;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarSolveProgressTests
{
    private sealed class Collector : IProgress<RunProgress>
    {
        public readonly List<RunProgress> Seen = [];
        public void Report(RunProgress v) { lock (Seen) Seen.Add(v); }
    }

    /// <summary>Small enough to solve quickly, real enough to exercise the point loop.</summary>
    private static (PlanarProblem Problem, PlanarMeshSettings Mesh) Fixture() =>
        (PlanarLineFixtures.Fr4Line(6e-3, 5e9), PlanarMeshSettings.Default);

    private static IReadOnlyList<PlanarPort> Ports(PlanarProblem p)
    {
        var mesh = SurfaceMesher.Mesh(p, PlanarMeshSettings.Default);
        Assert.True(mesh.CanSolve);
        return PlanarLineFixtures.EndPorts(p);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void ASweep_TicksOncePerFrequencyPoint()
    {
        var (problem, meshSettings) = Fixture();
        var collector = new Collector();
        var control = new RunControl { Total = 3, Progress = collector };

        double[] freqs = [2e9, 3e9, 4e9];
        new PlanarKernel().Solve(problem, meshSettings, Ports(problem), freqs,
            PlanarSolveSettings.Default with { Deembed = false }, default, control);

        Assert.Equal(freqs.Length, control.Completed);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStageLabelNamesWhatIsRunning_AndItsBarMovesInsideOnePoint()
    {
        // The justification for the second bar: within ONE frequency point the outer counter cannot
        // move, and a full-wave point is tens of seconds at the shipping mesh. If the stage counter
        // ever stops advancing while the outer one stands still, the run reads as hung.
        var (problem, meshSettings) = Fixture();
        var collector = new Collector();
        var control = new RunControl { Total = 1, Progress = collector };

        new PlanarKernel().Solve(problem, meshSettings, Ports(problem), [3e9],
            PlanarSolveSettings.Default with { Deembed = false }, default, control);

        // Sweep stages only — matched on the frequency in the label, because the MESHER reports
        // stages too (and is the reason this test cannot simply key on Completed == 0).
        var withinFirstPoint = collector.Seen.FindAll(
            p => p.Completed == 0 && p.StageTotal > 0 && p.Stage.Contains("Hz", StringComparison.Ordinal));
        Assert.NotEmpty(withinFirstPoint);

        long max = 0;
        foreach (var p in withinFirstPoint) max = Math.Max(max, p.StageCompleted);
        Assert.True(max > 0, "the stage counter must advance within a single point");

        // The mesher names its own stages (it runs inside the sweep as well as standalone), then the
        // per-point ones follow. Both must reach the row, or the user sees a bar with no caption for
        // whichever half is missing.
        Assert.Contains(collector.Seen, p => p.Stage.Contains("artwork", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(collector.Seen, p => p.Stage.Contains("grid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(collector.Seen, p => p.Stage.Contains("GHz", StringComparison.Ordinal));
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStageCounterNeverGoesBackwardsWithinAPoint()
    {
        var (problem, meshSettings) = Fixture();
        var collector = new Collector();
        var control = new RunControl { Total = 2, Progress = collector };

        new PlanarKernel().Solve(problem, meshSettings, Ports(problem), [2e9, 3e9],
            PlanarSolveSettings.Default with { Deembed = false }, default, control);

        // Monotone WITHIN A STAGE, which is the invariant that actually matters — BeginStage
        // legitimately restarts the sub-counter at zero, and a run has several stages before the
        // first frequency point (the mesher's own). Keying this on the POINT index instead was the
        // first version and it failed the moment the mesher started reporting: the mesh scan ticked
        // to 9 and the first point's BeginStage reset to 0, both at Completed == 0.
        long prev = -1;
        foreach (var p in collector.Seen)
        {
            if (p.StageTotal == 0) { prev = -1; continue; }  // a stage with no denominator
            if (p.StageCompleted == 0) { prev = 0; continue; }  // a fresh BeginStage
            Assert.True(p.StageCompleted >= prev,
                $"stage counter went backwards inside one stage ('{p.Stage}'): {prev} -> {p.StageCompleted}");
            prev = p.StageCompleted;
        }
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStageCounterNeverExceedsItsOwnTotal_WithDeembeddingOn()
    {
        // Owner report, 2026-08-09: the row read "11 / 4". A per-point stage total cannot survive the
        // ADAPTIVE path, whose Replay() re-runs de-embedding over every already-solved point — those
        // ticks landed on the stage the last raw solve had begun, so the numerator ran away from a
        // denominator that only counted ONE point's work. This is the invariant that would have
        // caught it, and it is asserted with de-embedding ON because that is where the extra ticks
        // came from.
        var (problem, meshSettings) = Fixture();
        var collector = new Collector();
        var control = new RunControl { Total = 5, MinReportIntervalMs = 0, Progress = collector };

        double[] freqs = [2e9, 2.5e9, 3e9, 3.5e9, 4e9];
        new PlanarKernel().Solve(problem, meshSettings, Ports(problem), freqs,
            PlanarSolveSettings.Default with { Adaptive = PlanarAdaptiveSettings.Default },
            default, control);

        foreach (var p in collector.Seen)
            if (p.StageTotal > 0)
                Assert.True(p.StageCompleted <= p.StageTotal,
                    $"stage counter overran its total in '{p.Stage}': {p.StageCompleted} / {p.StageTotal}");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheStageCounterNeverExceedsItsOwnTotal_OnThePlainSweep()
    {
        var (problem, meshSettings) = Fixture();
        var collector = new Collector();
        var control = new RunControl { Total = 3, MinReportIntervalMs = 0, Progress = collector };

        new PlanarKernel().Solve(problem, meshSettings, Ports(problem), [2e9, 3e9, 4e9],
            PlanarSolveSettings.Default, default, control);

        foreach (var p in collector.Seen)
            if (p.StageTotal > 0)
                Assert.True(p.StageCompleted <= p.StageTotal,
                    $"stage counter overran its total in '{p.Stage}': {p.StageCompleted} / {p.StageTotal}");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void Cancelling_StopsTheSweep_RatherThanRunningToCompletion()
    {
        // The token is observed at the same boundaries the progress is reported at — so a threaded
        // token that nothing ever checks (which looks identical at the call site) fails here.
        var (problem, meshSettings) = Fixture();
        using var cts = new CancellationTokenSource();

        var control = new RunControl
        {
            Token = cts.Token,
            Total = 50,
            // Cancel as soon as the sweep genuinely starts on a point.
            Progress = new DelegateProgress(p => { if (p.Completed >= 1) cts.Cancel(); }),
        };

        double[] freqs = new double[50];
        for (int i = 0; i < freqs.Length; i++) freqs[i] = 1e9 + i * 1e8;

        Assert.Throws<OperationCanceledException>(() =>
            new PlanarKernel().Solve(problem, meshSettings, Ports(problem), freqs,
                PlanarSolveSettings.Default with { Deembed = false }, default, control));

        Assert.True(control.Completed < freqs.Length,
            "cancellation must abandon the sweep, not let it run to the end");
    }

    [Fact]
    public void ANullControl_ChangesNothing()
    {
        // Every engine call site is `control?.…`; the no-progress path is the one every existing
        // caller and every prior test already takes, and it must stay byte-identical.
        var (problem, meshSettings) = Fixture();
        double[] freqs = [3e9];
        var settings = PlanarSolveSettings.Default with { Deembed = false };

        var a = new PlanarKernel().Solve(problem, meshSettings, Ports(problem), freqs, settings);
        var b = new PlanarKernel().Solve(problem, meshSettings, Ports(problem), freqs, settings,
                                         default, new RunControl { Total = 1 });

        Assert.Equal(a.Solve.Points.Count, b.Solve.Points.Count);
        Assert.Equal(a.Solve.Points[0].S[0, 0].Real, b.Solve.Points[0].S[0, 0].Real, 12);
        Assert.Equal(a.Solve.Points[0].S[0, 0].Imaginary, b.Solve.Points[0].S[0, 0].Imaginary, 12);
    }

    private sealed class DelegateProgress(Action<RunProgress> f) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => f(value);
    }
}
