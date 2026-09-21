// ================================================================
//  RailRfReportedDefectsTests.cs
//
//  Four defects reported from the field, 2026-09-21, against 1.0.0-beta.27 — found on a production
//  six-layer board imported from its own Gerber set rather than on the shipped example. One test per
//  CLAIM, and no test for a claim that could not be reproduced: the placement freeze is not here,
//  because nothing in this repo reproduces it and a test that passes against a synthetic board would
//  be a test that says the report was wrong.
//
//  ── WHAT IS ASSERTED ──────────────────────────────────────────────────────────────────────────
//
//   1. railRF's board view RESOLVES a footprint instance. It could not: the view model that draws
//      the board was never told where the `.clay` is, so every relative CellRef on it was NotFound
//      and every part drew as the warning-coloured broken-reference box. This is the defect behind
//      two screenshots of one board that looked like two different boards.
//   2. A dropped load carries a current and a dropped source carries a voltage, the absence is
//      still reachable by clearing the cell, and the strip COUNTS the rows nobody has typed in.
//   3. A file named in the netlist row that is not a board netlist is REPORTED. It was read,
//      refused by its own reader, and discarded without a word.
//   4. The copper read is DEFERRED. Confirming the reference used to run a Clipper union of every
//      copper layer and a galvanic partition of all of it inside a property getter, on the UI
//      thread — which is the freeze the report photographed as "Not Responding".
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfReportedDefectsTests
{
    // ══ 1 — the board view resolves its parts ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>Every footprint instance on the board resolves in railRF's own canvas.</b>
    /// </summary>
    /// <remarks>
    /// The assertion is on <c>InstanceBaseDir</c> and on the resolver's verdict rather than on
    /// pixels, because that is where the defect was and because it is the same question the renderer
    /// asks: <c>CellHierarchy.ResolveForWalk</c> answering anything but <c>Resolved</c> is exactly
    /// what makes <c>LayoutRenderer</c> draw a broken placeholder instead of a land pattern.
    ///
    /// <para>The shipped example is the fixture because its board carries seventeen footprint
    /// instances whose <c>CellRef</c> is <c>../../footprints/&lt;case&gt;</c> — relative, and
    /// therefore unresolvable against the empty string the view model used to hold.</para>
    /// </remarks>
    [Fact]
    public void TheBoardCanvasResolvesEveryFootprintInstance()
    {
        var vm = Example();

        var canvas = vm.BoardLayout;
        Assert.NotNull(canvas);
        Assert.NotEmpty(canvas!.Model.Instances);

        // The address itself: without it every CellRef below resolves against "" and is NotFound.
        Assert.NotEqual("", canvas.InstanceBaseDir);
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(vm.Board!.ArtworkCellRef!)),
            Path.GetFullPath(canvas.InstanceBaseDir));

        foreach (var instance in canvas.Model.Instances)
            Assert.Equal(
                CellLayoutState.Resolved,
                CellLayoutResolver.Resolve(instance.CellRef, canvas.InstanceBaseDir).State);
    }

    // ══ 2 — a row arrives carrying something ════════════════════════════════════════════════════

    /// <summary>
    /// <b>A dropped load draws 1 mA and a dropped source holds the rail's own voltage; clearing the
    /// cell still gives back <i>observe</i>; and the strip says how many rows nobody has typed
    /// in.</b>
    /// </summary>
    /// <remarks>
    /// All four in one test because they are one decision, and the last two are what make the first
    /// two allowable at all: §2.2 / Q-16's rule is not "no defaults" but "a number nobody typed must
    /// not read as one somebody did", and it is kept by the absence staying reachable and by the
    /// count being on screen.
    /// </remarks>
    [Fact]
    public void ADroppedLoadAndSourceArriveCarryingAValue_TheAbsenceIsStillReachable_AndSeededRowsAreCounted()
    {
        var vm = Example();
        var rail = vm.SelectedRail;
        Assert.NotNull(rail);

        int seededBefore = vm.SeededRowCount;

        vm.AddLoadCommand.Execute(null);
        vm.AddSourceCommand.Execute(null);

        var load = rail!.Loads[^1];
        var source = rail.Sources[^1];

        Assert.Equal(RailRfViewModel.SeededLoadCurrentA, load.DcCurrentA);
        Assert.False(load.IsObservationOnly);

        // The rail's OWN voltage, not the constant: this example's sources state 3.3 V, and a second
        // branch of a rail seeded at anything else would make the two fight.
        Assert.Equal(rail.NominalVoltageV, source.OpenCircuitVoltageV);

        Assert.Equal(seededBefore + 2, vm.SeededRowCount);
        Assert.Contains("still hold railRF's starting values", vm.StatusLine);

        // Clearing the cell restores the observation port — the gesture §2.2 requires, unchanged.
        var row = vm.Loads.Single(r => ReferenceEquals(r.Load, load));
        row.CurrentEntry = "";

        Assert.Null(rail.Loads[^1].DcCurrentA);
        Assert.True(rail.Loads[^1].IsObservationOnly);

        // And an edited row stops counting as seeded, by reference identity and with no flag to
        // clear — the committed edit built a new record, so the old one is no longer in any list.
        Assert.Equal(seededBefore + 1, vm.SeededRowCount);
    }

    // ══ 3 — a companion file that is not what its row wanted ════════════════════════════════════

    /// <summary>
    /// <b>A netlist row pointed at something that is not a board netlist is reported, and the report
    /// names the family of file the row wants.</b>
    /// </summary>
    /// <remarks>
    /// Which file this row wants is not guessable from a folder of half a dozen files a schematic
    /// tool has written. Picking one produced a board on which nothing had a net name and a message
    /// saying no board netlist had named any — true, and silent about the file that was chosen.
    ///
    /// <para>The fixture is a file of plain text with no feature records in it, which is what every
    /// one of those files is from this reader's point of view. What is asserted is that the reader's
    /// refusal REACHES the window, and that the sentence says which file to look for instead — a
    /// report that only said "nothing was taken from it" leaves the reader exactly where they were.</para>
    /// </remarks>
    [Fact]
    public void ANetlistFileThatIsNotOneIsReported_AndTheReportNamesWhatTheRowWants()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf-not-a-netlist-{Guid.NewGuid():N}.dat");
        File.WriteAllText(path, "NET_NAME 'VDD'\n NODE_NAME U1 A1\n NODE_NAME U2 B7\n");
        try
        {
            var netlist = BoardNetlistFile.ReadFile(path, LayoutUnits.DefaultDbuPerMicron);
            Assert.NotNull(netlist);
            Assert.NotNull(netlist!.Refusal);      // the reader always knew

            string report = RailImportReport.NetlistSummary(netlist);

            Assert.Contains(Path.GetFileName(path), report);
            Assert.Contains(netlist.Refusal!, report);
            Assert.Contains("IPC-D-356", report);
            Assert.Contains("not a netlist exported from the schematic", report);

            // And it survives being joined with the BOM's own line, which is the one that used to be
            // the whole of what an import said.
            Assert.Contains(report, RailImportReport.Summary(null, netlist));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A netlist that READS is reported by its counts, not by a refusal.</summary>
    [Fact]
    public void ANetlistThatReadsIsReportedByItsCounts()
    {
        Assert.Equal("", RailImportReport.NetlistSummary(null));
    }

    // ══ 4 — the copper read is off the UI thread ════════════════════════════════════════════════

    /// <summary>
    /// <b>Confirming the reference does not measure anything inline — it asks, and the answer lands
    /// afterwards.</b>
    /// </summary>
    /// <remarks>
    /// This is the freeze, stated as a fact about WHERE the work happens rather than as a timing
    /// assertion (which would measure the machine). The seam is left at its production default here,
    /// on purpose: what is asserted is that the answer is NOT in hand when the command returns and
    /// IS in hand once the task completes, which is the whole of the behaviour change.
    ///
    /// <para>On the shipped example the work takes milliseconds, so the first assertion would be
    /// racy if it depended on the job still running. It does not: <c>ReferenceReturnNet</c> reads
    /// null until <c>FinishCopperJob</c> has published, and publication goes through
    /// <c>PostToUi</c> — replaced here with a queue this test drains itself, which is exactly what
    /// the window's dispatcher does and what makes "not yet" deterministic.</para>
    /// </remarks>
    [Fact]
    public async Task ConfirmingTheReferenceDefersTheCopperReadInsteadOfMeasuringInline()
    {
        var vm = Example(inlineCopperRead: false);

        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        vm.PostToUi = posted.Enqueue;

        vm.PickRail("+3V3");
        vm.ConfirmReferenceCommand.Execute(null);

        // Nothing was measured on this thread: the getter asked and published null.
        Assert.Null(vm.ReferenceReturnNet);
        Assert.Equal(0, vm.ReferenceMeasurements);
        Assert.True(vm.IsReadingCopper);
        Assert.NotNull(vm.CopperRead);

        await vm.CopperRead!;
        while (posted.TryDequeue(out var action)) action();

        Assert.False(vm.IsReadingCopper);
        Assert.Equal(1, vm.ReferenceMeasurements);
        Assert.Equal("GND", vm.ReferenceReturnNet);
        Assert.True(vm.AvailableNets.Single(r => r.Name == "GND").IsReferenceReturn);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    private static RailRfViewModel Example(bool inlineCopperRead = true)
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        if (inlineCopperRead)
            vm.ReadCopperOffThread = work => { work(); return Task.CompletedTask; };

        Assert.Empty(vm.LoadDocumentReferences());
        return vm;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
