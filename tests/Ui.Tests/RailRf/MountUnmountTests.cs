// ================================================================
//  MountUnmountTests.cs — brief-railrf-23-mount-and-unmount.md, the window's half
//
//  HOW DO YOU TAKE A PLACED PART OFF THE LAYOUT, TO DEPOPULATE AND RE-SIMULATE?
//
//  A first-time designer asked exactly that, and until this brief the answer was to go and delete it
//  from the layout file. That is destructive, it is not what the question meant, and it loses the
//  mounting inductance the artwork gave the part so it cannot be put back the way it was.
//
//  ── THE CROSS-CHECK IS THE TEST ───────────────────────────────────────────────────────────────
//
//  Q2's removal ranking already re-solves the whole sweep once per part and says what DELETING each
//  one would cost. Unmounting a part and re-running is a second, completely separate path to the
//  same number — one goes through PdnSweep's own branch list with a branch dropped, the other
//  through the document, the resolver and a whole new run. The shipped example's README publishes
//  C10's growth and the margin without it, so the two mechanisms have to agree, and the first gate
//  below is that agreement rather than a hard-coded figure.
//
//  THE PUBLISHED FIGURES ARE NOT REPEATED HERE. They moved once already, when brief-footprint-5
//  re-spaced the board onto real footprints, and a second copy of them in this file is a second
//  place to forget. PowerRailExampleTests parses them out of the README and compares them to a live
//  run; what is gated here is the AGREEMENT between the two mechanisms and the shape of the answer.
//
//  Driven on the SHIPPED example throughout, because that is the document the report is about.
//  One test per CLAIM the brief makes.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class MountUnmountTests(ITestOutputHelper output)
{
    // ══ Gates 2 and 9 — the model, and the ranking ══════════════════════════════════════════════

    /// <summary>
    /// <b>Unmounting <c>C10</c> moves the worst margin by the amount Q2's ranking predicted for
    /// it</b> (gate 2) — <b>and <c>C10</c> is then ABSENT from the ranking rather than ranked at
    /// zero</b> (gate 9, R-rail23-4a).
    /// </summary>
    /// <remarks>
    /// Two independent paths to one number. Q2 ranks by dropping a BRANCH from a list the sweep
    /// already built; unmounting goes through the document row, the resolver and a fresh run of the
    /// whole thing. A change that broke either would move one and not the other.
    ///
    /// <para>The ranking's own agreement is the second half and it is the one R-rail23-4a is about:
    /// a ranking computed over the unmounted set would report what removing an absent part would
    /// cost, which is nothing, which reads exactly like a part that is not earning its place.</para>
    /// </remarks>
    [Fact]
    public void UnmountingC10_MovesTheMarginByWhatTheRankingPredicted_AndLeavesTheRanking()
    {
        var vm = Example();
        vm.RunCommand.Execute(null);

        var fitted = vm.Sweep!;
        double before = Worst(fitted);
        var predicted = fitted.Removal.Single(r => r.Name.StartsWith("C10 ", StringComparison.Ordinal));

        output.WriteLine($"fitted worst {before:0.###} dB; Q2 says C10 is worth " +
                         $"{predicted.GrowthDb:0.###} dB, to {predicted.WorstMarginDb:0.###} dB");

        Assert.Equal(1, vm.SetPartsMounted(["C10"], mounted: false));

        // SetPartsMounted re-solves through the ordinary Fast loop (R-rail23-2d) — no apply step.
        var depopulated = vm.Sweep!;
        double after = Worst(depopulated);
        output.WriteLine($"depopulated worst {after:0.###} dB");

        // The two paths agree. The tolerance is not zero because the re-run re-derives the adaptive
        // grid from the new curve, so the two answers are read off grids that differ by a few
        // points — what is being gated is that one mechanism predicts the other, not that two
        // different sweeps land on identical arrays.
        Assert.Equal(predicted.WorstMarginDb, after, 1);

        // And C10 is worth an order of magnitude more than anything else on this board — it is the
        // bulk, and it is holding the low band up on its own. The README carries the figure;
        // PowerRailExampleTests is what holds the README to it.
        Assert.True(after < -10, $"the bulk is no longer the part this gate is about: {after:0.###} dB.");
        Assert.True(before - after > 10, $"C10 is worth only {before - after:0.###} dB now.");

        // Gate 9: absent, not ranked at zero.
        Assert.DoesNotContain(depopulated.Removal,
            r => r.Name.StartsWith("C10 ", StringComparison.Ordinal));
        Assert.Contains(depopulated.Removal, r => r.Name.StartsWith("C1 ", StringComparison.Ordinal));
    }

    // ══ Gates 3 and 4 — what survives, and what is not touched ══════════════════════════════════

    /// <summary>
    /// <b>The row stays, with its computed mounting loop, and re-mounting gives the answer back bit
    /// for bit</b> (gate 3) — <b>and the <c>.clay</c> is not touched</b> (gate 4, R-rail23-1c).
    /// </summary>
    /// <remarks>
    /// <b>That surviving number is the whole feature.</b> Deleting the part from the artwork loses
    /// the loop the geometry gave it — 1.2 nH on <c>C11</c> against 0.56 nH on <c>C1</c>, and the
    /// difference between the two tiers is what the example's own README is about — so a part
    /// deleted and re-drawn does not come back the same part. Unmounted, it does.
    /// </remarks>
    [Fact]
    public void TheRowSurvivesWithItsInductance_ReMountingRestoresTheAnswer_AndTheClayIsUntouched()
    {
        var vm = Example();
        string clay = Path.Combine(RepoRoot(), "examples", "Power Rail", "Sensor board",
                                   "layout", "Board.clay");
        string before = Hash(clay);

        vm.RunCommand.Execute(null);
        double[] fitted = [.. vm.Sweep!.Ports[0].MagnitudeOhms];

        var rowBefore = vm.Parts.Single(p => p.Refdes == "C10");
        double loop = rowBefore.MountingInductanceHenries!.Value;
        string capacitance = rowBefore.CapacitanceText;
        Assert.True(rowBefore.IsMounted);

        vm.SetPartsMounted(["C10"], mounted: false);

        // STILL A ROW, and every column of it still says what it said. This is the difference from
        // deleting it, stated as an assertion.
        var rowAfter = vm.Parts.Single(p => p.Refdes == "C10");
        Assert.False(rowAfter.IsMounted);
        Assert.True(rowAfter.IsUnmounted);
        Assert.Equal(loop, rowAfter.MountingInductanceHenries!.Value, 15);
        Assert.Equal(capacitance, rowAfter.CapacitanceText);
        Assert.Equal(rowBefore.PartNumber, rowAfter.PartNumber);
        Assert.Equal(rowBefore.PositionText, rowAfter.PositionText);

        // And it is NOT unresolved (R-rail23-1d) — the two states are separate on the row.
        Assert.Equal(rowBefore.IsUnresolved, rowAfter.IsUnresolved);

        vm.SetPartsMounted(["C10"], mounted: true);
        Assert.Equal(fitted, vm.Sweep!.Ports[0].MagnitudeOhms);

        // Gate 4. railRF SHOWS the board; depopulating is a statement about what is fitted to the
        // geometry, not a change to it.
        Assert.Equal(before, Hash(clay));
    }

    // ══ Gate 6 — R-rail23-2c, four parts and ONE re-solve ═══════════════════════════════════════

    /// <summary>
    /// <b>Unmounting four parts is one edit and one run.</b>
    /// </summary>
    /// <remarks>
    /// "Unmount these four and re-run" is the real gesture. Four separate clicks and four re-solves
    /// is not — and on a board where a run is seconds rather than half a second, three of those four
    /// answers are of a board the user never wanted to see.
    /// </remarks>
    [Fact]
    public void FourPartsUnmountTogether_InOneReSolve()
    {
        var vm = Example();
        vm.RunCommand.Execute(null);

        int runsBefore = vm.SolvesStarted;

        Assert.Equal(4, vm.SetPartsMounted(["C11", "C12", "C13", "C4"], mounted: false));
        Assert.Equal(runsBefore + 1, vm.SolvesStarted);

        Assert.Equal(4, vm.UnmountedPartCount);
        Assert.All(new[] { "C11", "C12", "C13", "C4" },
                   r => Assert.False(vm.Parts.Single(p => p.Refdes == r).IsMounted));

        // A row already in the asked-for state is not an edit, so nothing re-solves for it.
        Assert.Equal(0, vm.SetPartsMounted(["C11", "C12"], mounted: false));
        Assert.Equal(runsBefore + 1, vm.SolvesStarted);
    }

    // ══ Gate 7 — R-rail23-3b/3c, compare against the run you just did ═══════════════════════════

    /// <summary>
    /// <b>Run, unmount, run, compare: a report naming <c>C10 unmounted</c>, with the per-part
    /// table.</b>
    /// </summary>
    /// <remarks>
    /// The designer's loop is <i>save the result, change one thing, re-run, compare</i> — within ONE
    /// document, which is exactly what Compare could not do, because the other side of that
    /// comparison is a run that no longer exists.
    ///
    /// <para><b>And the report says what differed in the INPUTS</b> (R-rail23-3c). Without that line
    /// the reader is left diffing two curves to work out what they did, which is the one thing the
    /// person who made the change already knows and the report is supposed to confirm.</para>
    /// </remarks>
    [Fact]
    public void ComparingAgainstThePreviousRun_NamesWhatChangedInTheInputs()
    {
        var vm = Example();
        vm.RunCommand.Execute(null);
        Assert.False(vm.HasBaseline);            // one run is not yet a comparison

        vm.SetPartsMounted(["C10"], mounted: false);
        Assert.True(vm.HasBaseline);

        var report = vm.CompareAgainstBaseline();
        Assert.NotNull(report);
        Assert.Null(report!.Refusal);

        output.WriteLine(string.Join("\n", report.Findings));

        Assert.Contains(report.InputChanges,
            l => l.StartsWith("C10 unmounted", StringComparison.Ordinal));

        // At the TOP of the report — the first block, and the first line of it.
        Assert.StartsWith("C10 unmounted", report.Sections[0].Lines[0], StringComparison.Ordinal);

        // Everything downstream is RailComparison's and unchanged: the per-part table the README
        // calls the half worth looking at first.
        Assert.NotEmpty(report.Mounting);
        Assert.Contains(report.Mounting, m => m.Name == "C10");
        Assert.False(report.Equivalent);
    }

    // ══ Gate 8 — R-rail23-3d, the pin ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A pinned baseline survives further runs</b>, so each cut is measured against the fitted
    /// board rather than against the previous cut.
    /// </summary>
    /// <remarks>
    /// A baseline taken on every run is lost as soon as you run twice. That is fine for "what did
    /// that one change do" and useless for the loop this feature exists for — <i>unmount, run,
    /// unmount another, run</i> — where every answer wants holding against the board as built.
    /// </remarks>
    [Fact]
    public void APinnedBaselineSurvivesFurtherRuns()
    {
        var vm = Example();
        vm.RunCommand.Execute(null);

        vm.PinBaselineCommand.Execute(null);
        Assert.True(vm.IsBaselinePinned);

        vm.SetPartsMounted(["C10"], mounted: false);
        vm.SetPartsMounted(["C9"], mounted: false);

        // Still the fitted board, two runs later — so the comparison names BOTH cuts rather than
        // only the most recent one, which is what an unpinned baseline would have reported.
        var report = vm.CompareAgainstBaseline()!;
        output.WriteLine(string.Join("\n", report.InputChanges));

        Assert.Contains(report.InputChanges, l => l.StartsWith("C10 unmounted", StringComparison.Ordinal));
        Assert.Contains(report.InputChanges, l => l.StartsWith("C9 unmounted", StringComparison.Ordinal));

        // Unpinning gives the baseline back to the run loop without discarding it — the pinned
        // result IS the previous run until another one completes.
        vm.PinBaselineCommand.Execute(null);
        Assert.False(vm.IsBaselinePinned);
        Assert.True(vm.HasBaseline);
    }

    // ══ A PINNED Y AXIS SURVIVES THE TOGGLE (owner, 2026-09-20) ═════════════════════════════════

    /// <summary>
    /// <b>Toggling a part must not move the plot's axes when the user has pinned them.</b>
    /// </summary>
    /// <remarks>
    /// <b>The whole point of unmounting a part is to see what it was doing</b>, and you cannot see
    /// that if the frame moves at the same moment the curve does — a 16 dB change and a re-framed
    /// axis look, on screen, remarkably like nothing happening at all.
    ///
    /// <para><c>RebuildImpedancePlot</c> ran <c>Autoscale(force: true)</c>, and <c>force</c> ignores
    /// <c>Plot.AutoscaleY</c> — which is precisely the flag the Plot Inspector's axis-limits panel
    /// clears when a window is pinned. The gate asserts BOTH halves: the window did not move, and
    /// the curve did — a test that only checked the window would pass just as well against a plot
    /// that had stopped updating.</para>
    /// </remarks>
    [Fact]
    public void APinnedYAxisDoesNotMoveWhenAPartIsToggled()
    {
        var vm = Example();
        vm.RunCommand.Execute(null);

        var plot = vm.ImpedancePlot;

        // What the axis-limits panel does: autoscale off, then a window of the user's own.
        plot.AutoscaleX = false;
        plot.AutoscaleY = false;
        var pinned = new PlotRect(0.05, -60, 250.0, 70.0);
        plot.Axes.Window = pinned;

        // The DRAWN points, which is the thing the reader is looking at — a |Z| cube is complex,
        // so the real-valued accessor beside it is empty by construction and would make the
        // vacuity guard below pass without ever having read the curve.
        var curve = plot.Traces.First(t => !t.IsAnnotation);
        float[] before = [.. curve.Points.Select(v => v.Y)];
        Assert.NotEmpty(before);

        vm.SetPartsMounted(["C10"], mounted: false);

        var after = plot.Axes.Window;
        output.WriteLine($"pinned {pinned}; after the toggle {after}");
        Assert.Equal(pinned, after);

        // And the curve underneath it really did move — 16 dB of bulk capacitor.
        var redrawn = plot.Traces.First(t => !t.IsAnnotation);
        Assert.NotEqual<IEnumerable<float>>(before, [.. redrawn.Points.Select(v => v.Y)]);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    private static double Worst(PdnSweepResult sweep) =>
        sweep.Ports.Select(p => p.MaskReport.WorstMarginDb).Where(m => m is not null).Min()!.Value;

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The shipped example, solved in process — the same seams every other railRF window
    /// test uses, so the Fast loop runs synchronously and a test can read the answer.</summary>
    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        return vm;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
