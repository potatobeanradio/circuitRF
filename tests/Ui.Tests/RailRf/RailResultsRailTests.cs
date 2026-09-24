// The Results pane says which rail it is showing, the |Z| half follows the rail selector, and the
// run's progress bar reads the whole run (owner, 2026-09-24).
//
// A run solves every rail's DC but sweeps |Z| for the rail selected when it started, and the sweep
// was filed by model kind alone — so changing rail left the previous rail's curve, mask verdict and
// removal ranking on screen under the new rail's name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using CircuitRF.Engine.Pdn;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailResultsRailTests
{
    /// <summary>
    /// <b>Changing rail puts THAT rail's curve on screen, and going back reuses the one already
    /// taken</b> — until an edit re-runs the DC answer, which makes every other rail's sweep stale.
    /// </summary>
    [Fact]
    public void TheImpedanceCurveFollowsTheRailSelector()
    {
        var vm = Ready(TwoRails());
        var swept = new List<string>();
        vm.SweepFunc = request =>
        {
            swept.Add(request.Rail.Name);
            return new PdnSweepResult(null, null, [], [], [], [$"swept {request.Rail.Name}"], []);
        };

        vm.RunCommand.Execute(null);
        Assert.Equal(["+1V8"], swept);
        Assert.Equal("swept +1V8", vm.Sweep!.Notes[0]);

        vm.SelectedRailName = "+3V3";
        Assert.Equal(["+1V8", "+3V3"], swept);
        Assert.Equal("swept +3V3", vm.Sweep!.Notes[0]);

        // Back again: the +1V8 sweep was taken beside the reading still on screen, so no new sweep.
        vm.SelectedRailName = "+1V8";
        Assert.Equal(2, swept.Count);
        Assert.Equal("swept +1V8", vm.Sweep!.Notes[0]);

        // An edit re-runs the DC answer (and this rail's sweep); +3V3's old sweep is now stale.
        vm.Loads[0].CurrentEntry = "120 mA";
        vm.SelectedRailName = "+3V3";
        Assert.Equal(["+1V8", "+3V3", "+1V8", "+3V3"], swept);
    }

    /// <summary>
    /// <b>A run that finishes after the selector moved files its curve under the rail it was taken
    /// for</b>, and the rail now showing gets its own.
    /// </summary>
    [Fact]
    public void ARunFinishingAfterTheRailChangedDoesNotShowTheOldRailsCurve()
    {
        var vm = Ready(TwoRails());
        vm.SweepFunc = request => new PdnSweepResult(null, null, [], [], [], [$"swept {request.Rail.Name}"], []);

        var release = new TaskCompletionSource<RailResultView>();
        Func<RailResultView>? held = null;
        vm.RunOffThread = (work, _) => { held ??= work; return held == work ? release.Task : Task.FromResult(work()); };

        vm.RunCommand.Execute(null);          // started on +1V8, held
        vm.SelectedRailName = "+3V3";         // mid-run: nothing to show yet
        Assert.Null(vm.Sweep);

        release.SetResult(held!());           // the +1V8 run finishes
        Assert.Equal("swept +3V3", vm.Sweep!.Notes[0]);
    }

    /// <summary>The header line names the rail and what state its numbers are in.</summary>
    [Fact]
    public void TheHeaderLineSaysWhichRailAndWhatReading()
    {
        var vm = Ready(TwoRails());
        Assert.Equal("+1V8 · not yet run", vm.ResultsRailText);

        vm.SolveFunc = (request, _) =>
            new RailDcRunResult(null, [], [.. request.Document.Rails.Select(r => r.Name)], [])
            {
                RailRefusals = [("+3V3", "Rail '+3V3' was not solved.")],
            };
        vm.RunCommand.Execute(null);

        vm.SelectedRailName = "+3V3";
        Assert.Equal("+3V3 · not solved", vm.ResultsRailText);
    }

    /// <summary>A solved rail reads "rail · reading · cost", on the shipped example's real solve.</summary>
    [Fact]
    public void ASolvedRailNamesItselfAndItsReading()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.ReadCopperOffThread = work => { work(); return Task.CompletedTask; };
        Assert.Empty(vm.LoadDocumentReferences());

        vm.RunCommand.Execute(null);

        Assert.StartsWith($"{vm.SelectedRailName} · Fast · ", vm.ResultsRailText);
        Assert.EndsWith(" ms", vm.ResultsRailText);
    }

    /// <summary>
    /// <b>The bar reads the whole run</b>: rails done plus the current rail's counted stage, so the
    /// second rail's count starting again at zero does not send it backwards.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 5, 10, 0.25)]    // halfway through rail 1 of 2
    [InlineData(1, 2, 0, 10, 0.50)]    // rail 2's count has just restarted
    [InlineData(1, 2, 10, 10, 1.00)]
    [InlineData(0, 0, 3, 4, 0.75)]     // no rail count: the stage alone
    public void TheFractionIsRailsDonePlusTheCurrentStage(
        long done, long rails, long stageDone, long stageTotal, double expected)
    {
        var p = new RunProgress("x", done, rails, stageDone, stageTotal);
        Assert.Equal(expected, RailRfViewModel.Fraction(p)!.Value, 9);
    }

    [Fact]
    public void AStageWithNoDenominatorIsIndeterminate() =>
        Assert.Null(RailRfViewModel.Fraction(new RunProgress("reading the copper", 1, 2)));

    /// <summary>RailDcRun ticks once per rail, so a control whose Total is the rail count ends full.</summary>
    [Fact]
    public void TheRunTicksOncePerRail()
    {
        var control = new RunControl { Total = 2 };
        RailDcRun.Run(new RailDcRequest { Document = TwoRails(), Shapes = [], Technology = TechWithGround(), Control = control });
        Assert.Equal(2, control.Completed);
    }

    /// <summary>
    /// <b>"no result yet" is the strip's own coloured lead, and nothing is drawn over the board</b>
    /// (owner, 2026-09-24).
    /// </summary>
    [Fact]
    public void NoResultYetLeadsTheStrip_AndTheBoardMapHasNoNoteOfItsOwn()
    {
        var fresh = new RailRfViewModel(TwoRails(), null);
        Assert.Equal("no result yet", fresh.StatusLeadText);
        Assert.Equal(fresh.StatusLine, fresh.StatusLeadText + fresh.StatusTailText);

        var vm = Ready(TwoRails());
        vm.RunCommand.Execute(null);
        Assert.False(vm.HasStatusLead);
        Assert.Equal(vm.StatusLine, vm.StatusTailText);

        Assert.Null(CircuitRF.Render.RailMapScene.Build(null, CircuitRF.Render.RailMapKind.Drop, 1000).Note);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static RailDocument TwoRails()
    {
        var doc = new RailDocument { Name = "two rails" };
        foreach (var (name, source) in new[] { ("+1V8", "BT1"), ("+3V3", "BT2") })
        {
            var rail = new RailSpec { Name = name, NetName = name };
            rail.Sources.Add(new RailSource { Anchor = Pad(source, "1"), OpenCircuitVoltageV = 3.7 });
            rail.Loads.Add(new RailLoad { Anchor = Pad("U1", name), DcCurrentA = 0.1 });
            doc.Rails.Add(rail);
        }
        return doc;
    }

    private static RailRfViewModel Ready(RailDocument doc)
    {
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.SolveFunc = (request, _) =>
            new RailDcRunResult(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);
        vm.Board = new RailBoardInputs { Shapes = [], Technology = TechWithGround(), ArtworkCellRef = "/ws/b.clay" };
        foreach (var rail in doc.Rails)
        {
            vm.SelectedRailName = rail.Name;
            vm.ConfirmReferenceCommand.Execute(null);
        }
        vm.SelectedRailName = doc.Rails[0].Name;
        Assert.True(vm.CanRun, vm.RunBlockedReason);
        return vm;
    }

    private static RailPortAnchor Pad(string refdes, string pin) => new() { Refdes = refdes, Pin = pin };

    private static Technology TechWithGround()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "L1" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "L2" });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L1", DrawingLayers = [new LayerKey(1, 0)],
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L2", IsGroundReference = true,
            DrawingLayers = [new LayerKey(2, 0)],
        });
        return tech;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
