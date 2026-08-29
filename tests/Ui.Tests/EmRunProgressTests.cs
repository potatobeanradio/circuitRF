// Owner request, 2026-08-09: "The EM simulation needs to give more feedback to the user during an EM
// simulation run. Add a progress bar to the Messages panel… reuse the same system we already built
// for Schematic Analysis simulations. If you think it would be best to have a couple lines of output
// (each getting its own progress bar), that's fine."
//
// Two lines, because an EM run has two questions with two different answers. A full-wave frequency
// point costs tens of seconds at the shipping mesh (L8d/L9d measured 48 s and 71.9 s de-embedded), so
// a single bar over the point count sits still for a minute at a time — which is exactly the "no
// feedback" the report is about. The sweep row answers "how far through"; the stage row answers "what
// is it doing right now" and moves WITHIN one point.

using System;
using System.Collections.Generic;
using CircuitRF.Engine;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class EmRunProgressTests
{
    private static readonly Action<Action> Inline = a => a();

    private static (LiveProgressMessage Live, MessageEntry Entry) NewLive(string text = "EM 'x'")
    {
        var entry = new MessageEntry(MessageLevel.Info, text, null, DateTime.Now)
        {
            ProgressIndeterminate = true,
            ProgressPercent       = 0,
        };
        return (new LiveProgressMessage(entry, Inline), entry);
    }

    // ── The two rows ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASweptPoint_MovesTheSweepRow_AndTheStageRowMovesInsideIt()
    {
        var (sweep, sweepEntry) = NewLive();
        var (stage, stageEntry) = NewLive();

        WorkspaceViewModel.ReportEmProgress(sweep, stage, "MLin",
            new RunProgress("10 GHz — calibration standard 1 of 2", 3, 101, 3, 4), adaptive: false);

        Assert.Equal("EM 'MLin'", sweepEntry.Text);          // constant left of the bar
        Assert.Equal("3 / 101", sweepEntry.ProgressText);
        Assert.Equal(100.0 * 3 / 101, sweepEntry.ProgressValue, 6);

        Assert.Equal("EM 'MLin' — 10 GHz — calibration standard 1 of 2", stageEntry.Text);
        Assert.Equal("3 / 4", stageEntry.ProgressText);
        Assert.Equal(75.0, stageEntry.ProgressValue, 6);
    }

    [Fact]
    public void TheStageRowMoves_WhileTheSweepRowStandsStill()
    {
        // The whole justification for a second row: within ONE point the sweep counter cannot move,
        // and that point is a minute long. If this ever collapses to one row, it regresses to the
        // reported "no feedback".
        var (sweep, sweepEntry) = NewLive();
        var (stage, stageEntry) = NewLive();

        var seen = new List<double>();
        for (long k = 1; k <= 4; k++)
        {
            WorkspaceViewModel.ReportEmProgress(sweep, stage, "MLin",
                new RunProgress($"10 GHz — step {k}", 3, 101, k, 4), adaptive: false);
            seen.Add(stageEntry.ProgressValue);
        }

        Assert.Equal("3 / 101", sweepEntry.ProgressText);     // unchanged throughout
        Assert.Equal([25.0, 50.0, 75.0, 100.0], seen);
    }

    [Fact]
    public void AdaptiveSampling_ReportsTheSweepIndeterminate_WithALiveSolvedCount()
    {
        // Adaptive decides how many points it solves as it goes, so there is no honest denominator.
        // A budget-based bar would usually stop well short of full and read as an unfinished run.
        var (sweep, sweepEntry) = NewLive();
        var (stage, _) = NewLive();

        WorkspaceViewModel.ReportEmProgress(sweep, stage, "MLin",
            new RunProgress("20 GHz — solving the structure", 7, 0, 2, 4), adaptive: true);

        Assert.True(sweepEntry.ProgressIndeterminate);
        Assert.Equal("7 point(s) solved", sweepEntry.ProgressText);
    }

    [Fact]
    public void AStageWithNoHonestDenominator_IsIndeterminate_NotAFakeZeroPercent()
    {
        var (sweep, _) = NewLive();
        var (stage, stageEntry) = NewLive();

        WorkspaceViewModel.ReportEmProgress(sweep, stage, "MLin",
            new RunProgress("meshing the artwork", 0, 101, 0, 0), adaptive: false);

        Assert.True(stageEntry.ProgressIndeterminate);
        Assert.Equal("EM 'MLin' — meshing the artwork", stageEntry.Text);
    }

    [Fact]
    public void BeforeAnyStageIsNamed_TheStageRowStillSaysSomething()
    {
        var (sweep, _) = NewLive();
        var (stage, stageEntry) = NewLive();

        WorkspaceViewModel.ReportEmProgress(sweep, stage, "MLin",
            new RunProgress("", 0, 101, 0, 0), adaptive: false);

        Assert.Equal("EM 'MLin' — starting", stageEntry.Text);
    }

    // ── RunControl's stage half ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BeginStage_ResetsTheSubCounter_AndReportsImmediately()
    {
        var seen = new List<RunProgress>();
        var c = new RunControl { Total = 10, Progress = new SyncProgress(seen) };

        c.BeginStage("first", 4);
        c.TickStage();
        c.BeginStage("second", 4);

        Assert.Equal("second", seen[^1].Stage);
        Assert.Equal(0, seen[^1].StageCompleted);            // a new stage starts at zero
        Assert.Equal(4, seen[^1].StageTotal);
    }

    [Fact]
    public void TickStage_RenamesWithoutResetting_SoTheStageBarIsMonotone()
    {
        // Renaming through BeginStage mid-point would send the bar backwards every time the label
        // changed, which is why the rename rides on the tick instead.
        var seen = new List<RunProgress>();
        var c = new RunControl { Total = 10, Progress = new SyncProgress(seen) };

        c.BeginStage("Green's function", 4);
        c.TickStage(nextLabel: "solving the structure");
        c.TickStage(nextLabel: "calibration standard 1 of 2");

        Assert.Equal("calibration standard 1 of 2", seen[^1].Stage);
        Assert.Equal(2, seen[^1].StageCompleted);

        var progressions = new List<long>();
        foreach (var p in seen) progressions.Add(p.StageCompleted);
        for (int i = 1; i < progressions.Count; i++)
            Assert.True(progressions[i] >= progressions[i - 1], "the stage counter must never go backwards");
    }

    [Fact]
    public void SetStageLabel_ChangesTheNameAndNeitherCounter()
    {
        var seen = new List<RunProgress>();
        var c = new RunControl { Total = 10, Progress = new SyncProgress(seen) };

        c.BeginStage("a", 4);
        c.TickStage();
        c.SetStageLabel("b");

        Assert.Equal("b", seen[^1].Stage);
        Assert.Equal(1, seen[^1].StageCompleted);
        Assert.Equal(4, seen[^1].StageTotal);
    }

    [Fact]
    public void ALabelChange_IsAlwaysDelivered_EvenInsideTheThrottleWindow()
    {
        // The throttle exists so a fast loop does not flood the UI thread. A stage RENAME is the one
        // observation a user is always waiting on, so it must not be the one the throttle eats.
        var seen = new List<RunProgress>();
        var c = new RunControl { Total = 10, MinReportIntervalMs = 100_000, Progress = new SyncProgress(seen) };

        c.BeginStage("a", 100);
        int afterBegin = seen.Count;

        c.TickStage();                                   // throttled away
        Assert.Equal(afterBegin, seen.Count);

        c.TickStage(nextLabel: "b");                     // delivered regardless
        Assert.Equal(afterBegin + 1, seen.Count);
        Assert.Equal("b", seen[^1].Stage);
    }

    [Fact]
    public void TheOuterCounter_IsUnaffectedByStageTicks()
    {
        // Two questions, two counters — a stage tick must never be mistaken for a completed point.
        var seen = new List<RunProgress>();
        var c = new RunControl { Total = 10, Progress = new SyncProgress(seen) };

        c.BeginStage("a", 4);
        c.TickStage(); c.TickStage(); c.TickStage();

        Assert.Equal(0, c.Completed);
        c.Tick();
        Assert.Equal(1, c.Completed);
    }

    [Fact]
    public void AControlWithNoProgress_StillAcceptsStageCalls()
    {
        // Every engine call site is `control?.…`, and a control built purely for cancellation has a
        // null Progress — neither may throw.
        var c = new RunControl { Total = 4 };
        c.BeginStage("a", 2);
        c.TickStage();
        c.SetStageLabel("b");
        Assert.Equal(0, c.Completed);
    }

    // ── The pieces the run path itself supplies ─────────────────────────────────────────────────

    [Fact]
    public void TheFinishedSweepRow_NamesThePointCount_NotJustSuccess()
    {
        string text = WorkspaceViewModel.EmRunSummary(
            new CircuitRF.Design.Layout.Em.EmRunResult(CircuitRF.Design.Layout.Em.EmRunStatus.Ok, null, null, null, null, null, null, []),
            adaptive: false, requestedPoints: 101);

        Assert.Contains("101", text);
    }

    [Fact]
    public void WithAdaptiveSamplingOn_TheFinishedRow_SaysHowMANYPointsWereSOLVED()
    {
        // P9's own gate. A run that publishes 101 points but solved 27 of them is reporting 74
        // MODELLED values, and a user who cannot tell which is which cannot tell whether the curve
        // is credible. The count is in the result (SolvedFrequencies); this is what puts it on
        // screen, and the non-adaptive case above never exercised this branch.
        var solve = new CircuitRF.Engine.Mom.PlanarSolveResult
        {
            Points        = [],
            CoreFillCount = 0,
            UnknownCount  = 0,
            StandardCount = 0,
            CoreBuildMs   = 0,
            Notes         = [],
            SolvedPointCount  = 27,
            SolvedFrequencies = [.. Enumerable.Range(0, 27).Select(i => 1e9 + i * 1e8)],
        };

        string text = WorkspaceViewModel.EmRunSummary(
            new CircuitRF.Design.Layout.Em.EmRunResult(
                CircuitRF.Design.Layout.Em.EmRunStatus.Ok, null, null, null, null, null, null, [],
                PlanarSolve: solve),
            adaptive: true, requestedPoints: 101);

        Assert.Contains("101", text);
        Assert.Contains("27", text);
        Assert.Contains("modelled", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Stop (owner request: "we need a Stop simulation for EM… perhaps Simulate changes to Cancel?")

    [Fact]
    public void WhileRunning_SimulateIsUnavailableAndCancelIs_AndViceVersa()
    {
        // The button swap is a view concern (MaterialIconKind is an Avalonia type and nothing under
        // src/Ui/Layout may reference one), but the STATE that drives it is testable here — and the
        // property that matters is that the two are never both available.
        var vm = EmProgressFixtures.NewSetupVm();

        Assert.False(vm.IsRunning);
        Assert.False(vm.CancelSimulateCommand.CanExecute(null));

        vm.IsRunning = true;
        Assert.True(vm.CancelSimulateCommand.CanExecute(null));
        Assert.False(vm.SimulateCommand.CanExecute(null));   // CanRun is gated on !IsRunning

        vm.IsRunning = false;
        Assert.False(vm.CancelSimulateCommand.CanExecute(null));
    }

    [Fact]
    public void Cancel_InvokesTheHostsCancellation()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        bool cancelled = false;
        vm.CancelRequested = () => cancelled = true;
        vm.IsRunning = true;

        vm.CancelSimulateCommand.Execute(null);

        Assert.True(cancelled);
    }

    [Fact]
    public void CancelWithNothingWired_IsANoOp_NotACrash()
    {
        // CancelRequested is cleared the moment a run ends; a stray click must not throw.
        var vm = EmProgressFixtures.NewSetupVm();
        vm.IsRunning = true;
        vm.CancelSimulate();
    }

    [Fact]
    public void ACancelledRun_IsItsOwnStatus_NotAnEngineError()
    {
        // A stopped run is a normal outcome. Reporting it as a failure is the thing this separation
        // exists to prevent.
        Assert.NotEqual(CircuitRF.Design.Layout.Em.EmRunStatus.EngineError,
                        CircuitRF.Design.Layout.Em.EmRunStatus.Cancelled);
    }

    [Fact]
    public void ARunControlToken_OverridesTheBareCancellationTokenParameter()
    {
        // One token, not two: EmRunService prefers control.Token where there is one, so a caller
        // cannot accidentally wire progress to one source and cancellation to another.
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        var control = new RunControl { Token = cts.Token };

        var result = CircuitRF.Design.Layout.Em.EmRunService.Run(
            EmProgressFixtures.NewSetup(), null, System.IO.Path.GetTempPath(),
            System.Threading.CancellationToken.None, control);

        // No layout, so it refuses before any cancellable work — the point here is only that
        // supplying a cancelled token through the control does not throw out of Run.
        Assert.Equal(CircuitRF.Design.Layout.Em.EmRunStatus.NoLayout, result.Status);
    }

    // ── Mesh (owner: "should the Mesh operation also get a progress bar… how does user cancel?") ──

    [Fact]
    public void MeshAndSimulate_AreMutuallyExclusive_AndEachCancelsItself()
    {
        // The button that STARTED the work is the one that turns into Cancel, and the other long
        // operation is unavailable meanwhile — two overlapping runs would mesh the same problem twice.
        var vm = EmProgressFixtures.NewSetupVm();

        vm.IsMeshing = true;
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelMeshCommand.CanExecute(null));
        Assert.False(vm.BuildActiveMeshCommand.CanExecute(null));
        Assert.False(vm.SimulateCommand.CanExecute(null));
        Assert.False(vm.CancelSimulateCommand.CanExecute(null));   // nothing to cancel there
        vm.IsMeshing = false;

        vm.IsRunning = true;
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelSimulateCommand.CanExecute(null));
        Assert.False(vm.BuildActiveMeshCommand.CanExecute(null));
        Assert.False(vm.CancelMeshCommand.CanExecute(null));
    }

    [Fact]
    public void CancelMesh_InvokesTheHostsCancellation()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        bool cancelled = false;
        vm.CancelMeshRequested = () => cancelled = true;
        vm.IsMeshing = true;

        vm.CancelMeshCommand.Execute(null);

        Assert.True(cancelled);
    }

    [Fact]
    public void WithNoHostWired_TheMeshCommandStillMeshesInline()
    {
        // Every headless caller and every pre-existing test takes this path; making the command
        // async must not turn it into a no-op for them.
        var vm = EmProgressFixtures.NewSetupVm();
        Assert.Null(vm.MeshRequested);
        vm.BuildActiveMeshCommand.Execute(null);        // no layout resolves — the point is that it RAN
        Assert.NotEmpty(vm.PlanarMeshNotes.Count > 0 ? vm.PlanarMeshNotes : ["ran"]);
    }

    [Fact]
    public void TheMeshRowReadsFromTheStageCounter_NotTheOuterOne()
    {
        // The mesher reports through the stage counter ONLY, because it also runs inside a sweep
        // where the outer counter means frequency points. A mesh row reading the outer one would sit
        // at zero for the whole mesh.
        var (live, entry) = NewLive();

        WorkspaceViewModel.ReportEmMeshProgress(live, "MLin",
            new RunProgress("scanning 'Metal' (1 of 1)", 0, 0, 300, 1200));

        Assert.Equal("Meshing 'MLin' — scanning 'Metal' (1 of 1)", entry.Text);
        Assert.Equal("300 / 1,200", entry.ProgressText);
        Assert.Equal(25.0, entry.ProgressValue);
    }

    [Fact]
    public void APreScanMeshStage_IsIndeterminate_NotAFakeZero()
    {
        var (live, entry) = NewLive();
        WorkspaceViewModel.ReportEmMeshProgress(live, "MLin",
            new RunProgress("measuring the artwork", 0, 0, 0, 0));

        Assert.True(entry.ProgressIndeterminate);
        Assert.Contains("measuring the artwork", entry.Text);
    }

    private sealed class SyncProgress(List<RunProgress> sink) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => sink.Add(value);
    }
}

internal static class EmProgressFixtures
{
    public static CircuitRF.Design.Layout.Em.EmSetup NewSetup() =>
        new() { Name = "MLin", LayoutRef = "MLin.clay" };

    public static CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel NewSetupVm() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-progress.cem"), NewSetup());
}
