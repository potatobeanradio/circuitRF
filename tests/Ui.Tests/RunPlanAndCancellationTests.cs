using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's run round: the parametric-sweep message must be readable BEFORE the simulation starts
/// (so a wrong sweep can be abandoned rather than waited out), Stop must actually stop, and the run
/// must report progress while it runs.
/// </summary>
public sealed class RunPlanAndCancellationTests
{
    // A two-port RC divider with two Terms and a nested sweep over two globals. Small enough that the
    // whole 3 x 4 = 12-point run is milliseconds, which is what lets a cancellation test cancel it
    // deterministically (by count) rather than by racing a clock.
    private const string NestedSweepCnl = """
        Ra = 50
        Rb = 50
        R:R1  in mid  R=Ra Ohm
        R:R2  mid 0    R=Rb Ohm
        Term:T1  in  0   Num=1 Z=50 Ohm
        Term:T2  mid 0   Num=2 Z=50 Ohm
        analysis SP1 type=sparam start=1 GHz stop=2 GHz step=1 GHz
        analysis SW_INNER type=parametric_sweep Var=Rb Values=10,20,30,40 Inner=SP1
        analysis SW_OUTER type=parametric_sweep Var=Ra Values=10,20,30 Inner=SW_INNER
        """;

    private const int OuterPoints = 3;
    private const int InnerPoints = 4;
    private const int LeafPoints  = OuterPoints * InnerPoints;   // 12

    private static T WithNetlist<T>(string cnl, System.Func<string, T> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "crf-run-" + System.Guid.NewGuid().ToString("N")[..8] + ".cnl");
        try
        {
            File.WriteAllText(path, cnl);
            return body(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── The headline: the plan is readable before anything runs ───────────────

    [Fact]
    public void Prepare_NestedSweep_DescribesEveryAxisAndTheProduct()
    {
        var plan = WithNetlist(NestedSweepCnl, path => SchematicRunService.Prepare(path));

        Assert.Equal(RunStatus.Success, plan.Status);
        var sweepLine = Assert.Single(plan.Lines, l => l.Contains("Parametric sweep"));

        // Outermost first — the order the axes are actually traversed in.
        Assert.Contains($"{OuterPoints} pt(s) over Ra", sweepLine);
        Assert.Contains($"{InnerPoints} pt(s) over Rb", sweepLine);
        Assert.Contains($"{LeafPoints} total pt(s)", sweepLine);
    }

    /// <summary>
    /// The property that actually matters, and the one a "does Prepare return lines" test cannot
    /// state: describing the run does NOT run it. Proven with a netlist that DESCRIBES perfectly and
    /// FAILS the moment it is executed (an s-parameter sweep with no Port/Term anywhere) — Prepare
    /// returns a clean plan naming the frequency count, and only Execute produces the error.
    /// </summary>
    [Fact]
    public void Prepare_DescribesWithoutRunning_SoAFailingAnalysisStillPlansCleanly()
    {
        const string cnl = """
            R:R1  a b  R=50 Ohm
            analysis NoPort type=sparam start=1 GHz stop=3 GHz step=1 GHz
            """;

        WithNetlist(cnl, path =>
        {
            var plan = SchematicRunService.Prepare(path);

            Assert.Equal(RunStatus.Success, plan.Status);
            Assert.Contains(plan.Lines, l => l.Contains("S-param") && l.Contains("3 pts"));

            // The same plan, executed, is where the failure surfaces.
            var result = SchematicRunService.Execute(plan);
            Assert.Equal(RunStatus.EngineError, result.Status);
            return 0;
        });
    }

    [Fact]
    public void Prepare_FailedPlan_IsPassedThroughByExecute_Unchanged()
    {
        var plan   = SchematicRunService.Prepare(Path.Combine(Path.GetTempPath(), "does-not-exist.cnl"));
        var result = SchematicRunService.Execute(plan);

        Assert.Equal(RunStatus.EngineError, plan.Status);
        Assert.Equal(plan.Status,        result.Status);
        Assert.Equal(plan.StatusMessage, result.StatusMessage);
        Assert.Empty(result.Results);
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    [Fact]
    public void TotalWorkUnits_IsTheLeafPointCount_NotTheAxisCount()
    {
        var plan = WithNetlist(NestedSweepCnl, path => SchematicRunService.Prepare(path));
        Assert.Equal(LeafPoints, plan.TotalWorkUnits);
    }

    /// <summary>
    /// A nested sweep counts its LEAF points once — not once per nesting level (which would overshoot
    /// the total) and not once per outer point (which would leave the bar stuck at 3/12). The inner
    /// s-parameter's own frequency loop must not count either: it is handed a progress-suppressed
    /// child precisely so it cannot.
    /// </summary>
    [Fact]
    public void NestedSweep_TicksOncePerLeafPoint_AndReachesTheTotal()
    {
        var observations = new List<RunProgress>();
        var control = new RunControl
        {
            Total = LeafPoints,
            MinReportIntervalMs = 0,          // observe every tick; the throttle is not under test here
            Progress = new SynchronousProgress(observations.Add),
        };

        var result = WithNetlist(NestedSweepCnl,
            path => SchematicRunService.Execute(SchematicRunService.Prepare(path), control));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(LeafPoints, control.Completed);
        Assert.Equal(LeafPoints, observations[^1].Completed);
        Assert.Equal(LeafPoints, observations[^1].Total);
    }

    [Fact]
    public void Progress_NamesTheAnalysisBeingRun()
    {
        var observations = new List<RunProgress>();
        var control = new RunControl
        {
            Total = LeafPoints,
            MinReportIntervalMs = 0,
            Progress = new SynchronousProgress(observations.Add),
        };

        WithNetlist(NestedSweepCnl,
            path => SchematicRunService.Execute(SchematicRunService.Prepare(path), control));

        // The result name of a sweep chain is its base analysis, which is what the user recognises.
        Assert.Contains(observations, o => o.Stage == "SP1");
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_BeforeExecute_ReturnsCancelled_AndProducesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = WithNetlist(NestedSweepCnl,
            path => SchematicRunService.Execute(SchematicRunService.Prepare(path),
                                                new RunControl { Token = cts.Token }));

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Empty(result.Results);
        Assert.Null(result.GroupedResults);
    }

    /// <summary>
    /// The reported case: a sweep that is wrong and long. Cancelling part-way must stop the sweep
    /// EARLY — the whole point is not waiting for the rest — and must publish nothing, because a
    /// stacked sweep axis has no shape to hold fewer slices than it has labels.
    /// </summary>
    [Fact]
    public void Cancel_MidSweep_StopsEarly_AndWritesNoResults()
    {
        using var cts = new CancellationTokenSource();
        const int cancelAfter = 3;

        int ticks = 0;
        var control = new RunControl
        {
            Token = cts.Token,
            Total = LeafPoints,
            MinReportIntervalMs = 0,
            Progress = new SynchronousProgress(_ =>
            {
                if (++ticks >= cancelAfter) cts.Cancel();
            }),
        };

        var result = WithNetlist(NestedSweepCnl,
            path => SchematicRunService.Execute(SchematicRunService.Prepare(path), control));

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Empty(result.Results);

        // Stopped early — a run that had quietly finished every point would report the full count and
        // make this test pass for the wrong reason.
        Assert.True(control.Completed < LeafPoints,
            $"expected the sweep to stop before all {LeafPoints} points; it completed {control.Completed}");
    }

    [Fact]
    public void NoControl_RunsExactlyAsBefore()
    {
        var result = WithNetlist(NestedSweepCnl, path => SchematicRunService.RunNetlist(path));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Single(result.Results);
    }

    /// <summary>Delivers on the calling thread, so a test observes ticks in the order the engine made
    /// them. <see cref="System.Progress{T}"/> posts to a synchronization context instead, which in a
    /// test means the observations arrive after the assertions.</summary>
    private sealed class SynchronousProgress(System.Action<RunProgress> onReport) : System.IProgress<RunProgress>
    {
        public void Report(RunProgress value) => onReport(value);
    }
}
