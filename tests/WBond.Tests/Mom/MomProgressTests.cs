using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.WBond.Mom;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// <see cref="WBondRunControl"/>, and the kernel actually reporting through it.
///
/// <para><b>Why the kernel needs a progress contract of its own at all</b> is recorded on
/// <see cref="WBondRunControl"/>: this project has no project references and cannot acquire one
/// (<c>Core</c> references it, <c>Engine</c> references <c>Core</c>), so <c>CircuitRF.Engine.RunControl</c>
/// is out of reach and the shape is deliberately duplicated. These tests hold the duplicate to the same
/// behaviour the original has, because a progress bar that lies is worse than none.</para>
/// </summary>
public sealed class MomProgressTests
{
    private static WBondDesign SmallDesign() =>
        TestDesigns.PowerAmplifier(wireCount: 2, arrayCount: 1, pointsPerWire: 5);

    private static WireMomSettings Coarse(int segments = 4) =>
        WireMomSettings.Default with { TargetSegmentsPerWire = segments };

    private sealed class Recorder : IProgress<WBondProgress>
    {
        public ConcurrentQueue<WBondProgress> Seen { get; } = new();

        public void Report(WBondProgress value) => Seen.Enqueue(value);
    }

    // ────────────────────────────────────────────────── the control itself

    /// <summary>
    /// The throttle drops the flood, and the LAST tick of a known total is delivered anyway — a bar
    /// left one point short of its own end reads as a run that never finished.
    /// </summary>
    [Fact]
    public void Tick_ThrottlesButAlwaysDeliversTheFinalUnit()
    {
        var recorder = new Recorder();
        var run = new WBondRunControl
        {
            Total = 500,
            Progress = recorder,
            // A whole second, so nothing but the guaranteed final delivery can get through.
            MinReportIntervalMs = 1000,
        };

        for (int i = 0; i < 500; i++) run.Tick();

        Assert.Equal(500, run.Completed);

        var seen = recorder.Seen.ToArray();
        Assert.NotEmpty(seen);

        // Far fewer than one per tick — that is the throttle doing its job.
        Assert.True(seen.Length < 10, $"the throttle let {seen.Length} observations through");

        // …and the last one is complete, not 499 / 500.
        Assert.Equal(500, seen[^1].Completed);
        Assert.Equal(500, seen[^1].Total);
    }

    /// <summary>
    /// A stage change is reported IMMEDIATELY and unthrottled: it is the one event a user is always
    /// waiting to see, and it is the only thing that moves during the frequency-independent setup.
    /// </summary>
    [Fact]
    public void BeginStage_ReportsImmediately_EvenUnderTheThrottle()
    {
        var recorder = new Recorder();
        var run = new WBondRunControl { Progress = recorder, MinReportIntervalMs = 60_000 };

        run.BeginStage("filling the inductance matrix", 100);
        run.BeginStage("filling the potential matrix", 200);

        var seen = recorder.Seen.ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Equal("filling the inductance matrix", seen[0].Stage);
        Assert.Equal(100, seen[0].StageTotal);
        Assert.Equal("filling the potential matrix", seen[1].Stage);
        Assert.Equal(200, seen[1].StageTotal);

        // The new stage's sub-counter starts at zero — a stage bar that carried the previous stage's
        // count would open somewhere in the middle.
        Assert.Equal(0, seen[1].StageCompleted);
    }

    /// <summary>
    /// <b>The two setup fills tick from every worker thread of a <c>Parallel.For</c> at once.</b> The
    /// counter has to be exact under that and the throttle has to let exactly one thread through per
    /// interval — a compare-and-swap, not a stopwatch restart, which is the difference between one
    /// observation per interval and one per thread.
    /// </summary>
    [Fact]
    public void TickStage_IsExactUnderParallelTicking()
    {
        var recorder = new Recorder();
        var run = new WBondRunControl { Progress = recorder, MinReportIntervalMs = 5 };
        run.BeginStage("filling", 10_000);

        Parallel.For(0, 10_000, _ => run.TickStage());

        // The final tick of a known stage total is always delivered, so the last observation is the
        // full count — and it can only be exact if every increment landed.
        var seen = recorder.Seen.ToArray();
        Assert.Equal(10_000, seen[^1].StageCompleted);
    }

    /// <summary>A null <c>Progress</c> makes every tick a cancellation check and nothing else — which
    /// is what a caller that wants only Stop passes, and what must not allocate or report.</summary>
    [Fact]
    public void WithNoProgress_TicksStillCancel()
    {
        using var cts = new CancellationTokenSource();
        var run = new WBondRunControl { Token = cts.Token };

        run.Tick();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => run.Tick());
        Assert.Throws<OperationCanceledException>(() => run.TickStage());
    }

    // ────────────────────────────────────────────────── the kernel reporting

    /// <summary>
    /// A real setup + sweep reports every stage it passes through and finishes with the point counter
    /// full.
    ///
    /// <para><b>The setup stages are the point of this.</b> They are the stretch during which the
    /// frequency counter cannot honestly move at all — 34.5 s of it at N_s = 4,800 — so a run with no
    /// stage reporting shows a bar sitting at 0 of N for half a minute, which is indistinguishable from
    /// a hang. Each stage is asserted by name because the names are what the panel renders.</para>
    /// </summary>
    [Fact]
    public void ARun_ReportsEverySetupStage_AndCountsEveryFrequencyPoint()
    {
        double[] frequencies = [1e9, 5e9, 10e9, 20e9];

        var recorder = new Recorder();
        var run = new WBondRunControl { Total = frequencies.Length, Progress = recorder };

        var solver = WireMomSolver.Create(SmallDesign(), Coarse(), run);
        var result = solver.Solve(frequencies, default, run);

        Assert.Equal(frequencies.Length, result.Frequencies.Count);
        Assert.Equal(frequencies.Length, run.Completed);

        var stages = recorder.Seen.Select(p => p.Stage).Distinct().ToArray();

        Assert.Contains("meshing the wires", stages);
        Assert.Contains("filling the inductance matrix", stages);
        Assert.Contains("filling the potential matrix", stages);
        Assert.Contains(stages, s => s.StartsWith("factorising the potential", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("inverting the potential", StringComparison.Ordinal));
        Assert.Contains("reducing to the node-merged basis", stages);
        Assert.Contains("assembling the segment system", stages);
        Assert.Contains(stages, s => s.StartsWith("solving the frequency sweep", StringComparison.Ordinal));

        // The last observation is the full point count: the throttle never leaves the sweep bar short.
        Assert.Equal(frequencies.Length, recorder.Seen.Last().Completed);
    }

    /// <summary>
    /// A stage that declares a denominator never reports past it. This is what stops a bar reaching
    /// 140% — the failure mode of handing one control to two things that each count the same units.
    /// </summary>
    [Fact]
    public void NoStage_EverReportsPastItsOwnDenominator()
    {
        var recorder = new Recorder();
        var run = new WBondRunControl { Total = 2, Progress = recorder };

        var solver = WireMomSolver.Create(SmallDesign(), Coarse(), run);
        solver.Solve([2e9, 8e9], default, run);

        foreach (var p in recorder.Seen)
        {
            if (p.StageTotal > 0)
                Assert.True(p.StageCompleted <= p.StageTotal,
                            $"stage '{p.Stage}' reported {p.StageCompleted} of {p.StageTotal}");
            if (p.Total > 0)
                Assert.True(p.Completed <= p.Total, $"the sweep reported {p.Completed} of {p.Total}");
        }
    }

    /// <summary>
    /// <b>Cancellation reaches inside the SETUP, not only the sweep.</b> Setup is the long half at
    /// large N (34.5 s at N_s = 4,800 against 14 s a point), so a Stop that only took effect between
    /// frequency points would leave a user waiting half a minute after they pressed it.
    /// </summary>
    [Fact]
    public void CancellingDuringSetup_StopsTheSetup()
    {
        using var cts = new CancellationTokenSource();

        // Cancel on the first observation of the FIRST fill — well before any point is solved.
        var run = new WBondRunControl
        {
            Token = cts.Token,
            Progress = new DelegateProgress(_ => cts.Cancel()),
        };

        Assert.Throws<OperationCanceledException>(
            () => WireMomSolver.Create(SmallDesign(), Coarse(24), run));
    }

    private sealed class DelegateProgress(Action<WBondProgress> onReport) : IProgress<WBondProgress>
    {
        public void Report(WBondProgress value) => onReport(value);
    }
}
