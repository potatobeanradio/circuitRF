// ================================================================
//  RailRfFieldReport3Tests.cs
//
//  A third round of outside railRF use, 2026-09-22, on a production six-layer board imported from
//  its own Gerber set. One test per CLAIM, and each claim is something the reporter actually said.
//
//  ── WHAT HE SAID, AND WHAT IS ASSERTED ────────────────────────────────────────────────────────
//
//   1. "the 2 footprints placed manually never showed up on the part list", said twice in one
//      session. They had not, and there was no way to put them there: brief 26's discovery was the
//      only producer of a part row, it needs a completed extraction, and it only offers a part it
//      can PROVE bridges the rail and its reference — which on a Gerber set with no netlist it can
//      never prove about a hand-placed footprint. Meanwhile the empty pane SAID rows could be
//      typed. Asserted: the board's placed designators are offered, a row can be added and removed,
//      and the row that arrives is Typed rather than Artwork.
//
//   2. "it then say solving .. but is probably run in a dead end". The window has built a
//      RunControl since brief 7 and handed it to NOTHING, so the token reached no engine and a
//      "cancel" only stopped the window listening. Asserted: the request carries the control, a
//      cancelled run reports as stopped rather than as an error, and Stop is live exactly while a
//      run is.
//
//   3. The board netlist refusal — it said only that the file held no feature records, naming
//      neither what the file IS nor what railRF wanted. Asserted: it says both, and the OPEN path
//      says the same sentence the import path does, which it did not.
//
//   4. The placement table refusal named --columns to a user in a window. Asserted: the refusal is
//      recognisable as the one a control can answer, a named mapping reads the file, and the
//      mapping SURVIVES a save and reopen — it did not, and neither did the origin.
//
//   5. The same courtyard warning three times in a row. The warning is right and is per-arming on
//      purpose. Asserted: consecutive identical messages collapse to one row with a count, and
//      non-consecutive ones do not.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfFieldReport3Tests
{
    // ══ 1 — a part row can be added by hand ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>The designators the board places are offered, a row can be added and removed, and it
    /// carries the provenance of an assertion rather than of a conclusion.</b>
    /// </summary>
    /// <remarks>
    /// This is the reported defect stated as a property. The offer is filtered against the rail's
    /// OWN rows, so what is asserted is that a designator already on the rail is not offered and one
    /// that is not is — which is what makes the dialog show the two footprints somebody just placed
    /// and not the twenty they added last week.
    ///
    /// <para><c>RailPartOrigin.Typed</c> is the load-bearing assertion. A row added here is a user's
    /// claim that the part is on this rail; <c>Artwork</c> is a claim railRF made and proved. Making
    /// them the same would put a provenance nobody stated beside numbers nobody stated, which is the
    /// failure this whole tool is built to prevent.</para>
    /// </remarks>
    [Fact]
    public void APartRowCanBeAddedByHand_FromTheBoardsOwnDesignators()
    {
        var vm = Example();
        var rail = vm.SelectedRail;
        Assert.NotNull(rail);

        var offered = vm.AddablePlacedParts;
        Assert.NotEmpty(offered);

        // Nothing already on the rail is offered — the filter, which is what keeps the dialog short.
        foreach (var already in rail!.Parts.Select(p => p.Refdes).Where(r => r.Length > 0))
            Assert.DoesNotContain(offered, o => string.Equals(o.Refdes, already, StringComparison.OrdinalIgnoreCase));

        string refdes = offered[0].Refdes;
        Assert.Equal(1, vm.AddParts([refdes]));

        var added = rail.Parts.Single(p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RailPartOrigin.Typed, added.Origin);
        Assert.Equal("", added.PartNumber);          // nothing was derived from the land pattern
        Assert.Contains(vm.Parts, r => string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase));

        // Idempotent by designator, which is what makes a second press of the button do nothing
        // rather than double the bank.
        Assert.Equal(0, vm.AddParts([refdes]));
        Assert.DoesNotContain(vm.AddablePlacedParts, o => string.Equals(o.Refdes, refdes, StringComparison.OrdinalIgnoreCase));

        // And the other half: a row that should never have been added comes off again.
        Assert.Equal(1, vm.RemoveParts([refdes]));
        Assert.DoesNotContain(rail.Parts, p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>A typed designator is added on a document with no board at all</b> — §6's "artwork is
    /// optional", which the pane's own empty-state sentence has promised since brief 26.
    /// </summary>
    [Fact]
    public void APartRowCanBeTypedWithNoBoard()
    {
        var doc = new RailDocument();
        doc.Rails.Add(new RailSpec { Name = "3V3" });
        var vm = new RailRfViewModel(doc, null);

        Assert.Null(vm.Board);
        Assert.True(vm.CanAddPart);
        Assert.Empty(vm.AddablePlacedParts);

        Assert.Equal(2, vm.AddParts(["C1", "C2"]));
        Assert.Equal(["C1", "C2"], doc.Rails[0].Parts.Select(p => p.Refdes));
    }

    /// <summary>
    /// <b>A part PLACED in the artwork reads its position off the artwork</b> — the "where" column
    /// had exactly one source and behaved as though that were the only one there could be.
    /// </summary>
    /// <remarks>
    /// Owner report, 2026-09-22, reproducing the round's own headline one step further on: a row
    /// added by hand, given a part number, then placed in the <c>.clay</c> with that designator,
    /// still read "not placed". It WAS placed — railRF was naming that part's land pattern in the
    /// column immediately to the left, out of <c>PlacedPins.FootprintsOf</c>, off the very instance
    /// it claimed not to know about. Only the placement FILE was ever consulted, and the tooltip
    /// then blamed the absence of one.
    ///
    /// <para><b>The provenance is asserted, not just the coordinate.</b> A placement file states the
    /// manufacturing centroid under a declared origin convention; an instance origin is where the
    /// land pattern's own origin was dropped. They are not the same number in general, so the file
    /// wins where it has a row and the row has to be able to say which it is showing — a coordinate
    /// whose source is invisible is the defaulted-number failure in another column.</para>
    /// </remarks>
    [Fact]
    public void APartPlacedInTheArtworkIsNotReportedAsNotPlaced()
    {
        var vm = Example();
        var rail = vm.SelectedRail;
        Assert.NotNull(rail);

        // THE REPORTER'S OWN GESTURE, REPRODUCED: a footprint dropped into the layout by hand,
        // carrying a designator the placement file — exported before it existed — does not name.
        // The shipped example's placement file names every instance it ships with, so the case has
        // to be built rather than found; building it is what makes the test about the defect
        // instead of about the fixture.
        const string refdes = "C99";
        var board = vm.Board;
        Assert.NotNull(board);
        Assert.DoesNotContain(vm.Placement!.Rows,
            r => string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase));

        board!.View!.Instances.Add(new LayoutInstance
        {
            CellRef = "../../footprints/C0402",
            RefDes  = refdes,
            X       = 14_907_396,
            Y       = 15_666_283,
        });

        // EXACTLY THE SIGNAL THE LAYOUT EDITOR RAISES. Not `Board = board with { }`: RailBoardInputs
        // is a record, so an equal copy never reaches the setter — and the real path does not use
        // the setter either, because assigning it would rebuild the canvas and take the viewport
        // away from a user who is looking at it.
        vm.NotifyArtworkChanged();

        // An added instance also schedules the debounced pad re-read (brief 28). Waited for HERE, so
        // it cannot land on another thread while the assertions below read the parts table.
        vm.PadRead?.GetAwaiter().GetResult();

        Assert.Contains(vm.AddablePlacedParts, o => string.Equals(o.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, vm.AddParts([refdes]));

        var row = vm.Parts.Single(r => string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(RailPartRowViewModel.NotPlacedText, row.PositionText);
        Assert.Equal(RailPartPositionSource.Artwork, row.PositionFrom);
        Assert.Contains("ARTWORK", row.PositionTooltip, StringComparison.Ordinal);

        // And a designator the placement FILE names still reads off the file — the more specific
        // statement wins, which is what stops this fix quietly replacing a stated centroid.
        var fromFile = vm.Parts.FirstOrDefault(r => r.PositionFrom == RailPartPositionSource.PlacementFile);
        if (fromFile is not null)
            Assert.Contains("PLACEMENT FILE", fromFile.PositionTooltip, StringComparison.Ordinal);
    }

    // ══ 2 — the run can be stopped, and says what it is doing ═══════════════════════════════════

    /// <summary>
    /// <b>The request a solve is handed carries the run control</b> — it carried none, which is why
    /// nothing could be stopped.
    /// </summary>
    /// <remarks>
    /// Asserted at the SEAM rather than by timing a real board: what was broken is that the object
    /// existed and was not passed, and that is a fact about the request. The token is checked for
    /// identity with the one the view model cancels, because a control carrying a different token
    /// would look right here and stop nothing in the field.
    /// </remarks>
    [Fact]
    public void TheSolveRequestCarriesTheRunControl_AndItsTokenIsTheOneCancelUses()
    {
        var vm = Example();

        RailDcRequest? seen = null;
        var entered = new ManualResetEventSlim();
        var released = new ManualResetEventSlim();

        vm.RunOffThread = (work, token) => Task.Run(work, token);
        vm.SolveFunc = (request, _) =>
        {
            seen = request;
            entered.Set();
            released.Wait(TimeSpan.FromSeconds(5));
            return RailDcRun.Run(request);
        };

        vm.RunCommand.Execute(null);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the solve never started");

        Assert.NotNull(seen);
        Assert.NotNull(seen!.Control);
        Assert.False(seen.Control!.Token.IsCancellationRequested);

        // The window's own stop reaches THAT token — the whole of the defect in one assertion.
        vm.StopRunCommand.Execute(null);
        Assert.True(seen.Control.Token.IsCancellationRequested);

        released.Set();
    }

    /// <summary>
    /// <b>A cancelled run reports as STOPPED, not as an error</b> — and Stop is live exactly while
    /// a run is.
    /// </summary>
    /// <remarks>
    /// The second half is not decoration. Now that the token reaches an engine, the engine answers
    /// it by THROWING from inside the work — and a task whose body throws an
    /// <see cref="OperationCanceledException"/> for a token its scheduler was not given faults
    /// rather than transitioning to Canceled. Without the fault branch this test asserts, the strip
    /// would read "The solve did not finish: The operation was canceled" for the one outcome the
    /// user asked for.
    /// </remarks>
    [Fact]
    public void ACancelledRunIsReportedAsStopped()
    {
        var vm = Example();

        Assert.False(vm.StopRunCommand.CanExecute(null));

        vm.RunOffThread = (work, token) => Task.Run(work, token);
        vm.SolveFunc = (request, _) =>
        {
            // Exactly what the extraction now does at its own checkpoints.
            request.Control!.Token.ThrowIfCancellationRequested();
            Thread.Sleep(20);
            request.Control.Token.ThrowIfCancellationRequested();
            return RailDcRun.Run(request);
        };

        vm.RunCommand.Execute(null);
        Assert.True(vm.IsSolving);
        Assert.True(vm.StopRunCommand.CanExecute(null));

        vm.StopRunCommand.Execute(null);

        Assert.False(vm.IsSolving);
        Assert.False(vm.StopRunCommand.CanExecute(null));
        Assert.Equal("", vm.SolveStage);
        Assert.NotNull(vm.Refusal);
        Assert.Contains("stopped", vm.Refusal!.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not finish", vm.Refusal.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>A rail with no decoupling says so on the strip</b> — the owner's call on "even without any
    /// part model i was allowed to press run": the DC answer is real and stays reachable, and what
    /// the frequency half cannot do without parts is stated rather than left as an empty curve.
    /// </summary>
    [Fact]
    public void ARailWithNoDecouplingSaysSoRatherThanBeingRefused()
    {
        var doc = new RailDocument();
        doc.Rails.Add(new RailSpec { Name = "3V3" });
        var vm = new RailRfViewModel(doc, null);

        Assert.Contains("no decoupling parts", vm.NoDecouplingText);
        Assert.Contains("no decoupling parts", vm.StatusLine);

        vm.AddParts(["C1"]);
        Assert.Equal("", vm.NoDecouplingText);
    }

    // ══ 3 — the netlist refusal says what the file is, on both surfaces ═════════════════════════

    /// <summary>
    /// <b>A file that is not a board netlist is named for what it IS, and the format railRF reads is
    /// named too.</b>
    /// </summary>
    /// <remarks>
    /// The reporter got "holds no feature records that could be read, so nothing was taken from it"
    /// and nothing else — a sentence that names neither the file nor the want. Of the three readers
    /// in this folder, this was the only one that did not run the import's own classifier over a
    /// file it could not read, and there was no reason for it.
    /// </remarks>
    [Fact]
    public void ANetlistRefusalNamesWhatTheFileIsAndWhatRailRfReads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf-fr3-netlist-{Guid.NewGuid():N}.dat");
        File.WriteAllText(path, "NET_NAME 'VDD'\n NODE_NAME U1 A1\n NODE_NAME U2 B7\n");
        try
        {
            var netlist = BoardNetlistFile.ReadFile(path, LayoutUnits.DefaultDbuPerMicron);
            Assert.NotNull(netlist);

            string refusal = netlist!.Refusal!;
            Assert.Contains("not any file kind circuitRF recognises", refusal);
            Assert.Contains("IPC-D-356", refusal);

            // And the two surfaces say ONE thing. The open path used to print the bare reader
            // sentence while the import path added which family of file the row wants.
            string tail = CircuitRF.Ui.RailRf.RailImportReport.RefusalTail(refusal);
            Assert.Contains(refusal, tail);
            Assert.Contains("not a netlist exported from the schematic", tail);
            Assert.Contains(tail, CircuitRF.Ui.RailRf.RailImportReport.NetlistSummary(netlist));
        }
        finally { File.Delete(path); }
    }

    // ══ 4 — the placement columns can be named, and the naming survives a save ══════════════════

    /// <summary>
    /// <b>A headerless placement table is refused in a way a control can answer, its columns are
    /// inferred from the values, and a named mapping reads it.</b>
    /// </summary>
    /// <remarks>
    /// The fixture is the shape the report describes: a designator, two coordinates, a rotation and
    /// a side, with no header row. The ROTATION is the load-bearing part of the inference — it is
    /// numeric, it sits before the coordinates, and a rule that simply took the first two numeric
    /// columns would read it as X. That is the exact failure the original refusal was written
    /// against, so it is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void AHeaderlessPlacementTableCanHaveItsColumnsNamed()
    {
        const string text =
            "C1,90,12.5,40.0,T\n" +
            "C2,0,31.25,18.75,T\n" +
            "R7,270,55.0,61.5,B\n" +
            "U3,180,22.0,9.25,T\n";

        string path = Path.Combine(Path.GetTempPath(), $"crf-fr3-place-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        try
        {
            // It refuses, and the refusal is the one a control can answer.
            var refused = PlacementFile.Read(path, text, LayoutUnits.DefaultDbuPerMicron,
                                             PlacementOrigin.BodyCentre);
            Assert.NotNull(refused.Refusal);
            Assert.True(PlacementFile.HasNoHeader(refused));

            // AND IT NAMES A ROUTE A WINDOW HAS. The old sentence offered only "Name them with
            // --columns, or state --from", which is exact advice in a terminal and unreachable in a
            // dialog — the reporter met it there. The flag is still named, for the caller who has
            // one; what is asserted is that it is no longer the ONLY thing named.
            Assert.Contains("Say what the columns are", PlacementFile.NoHeaderRefusal);

            // The inference reads the VALUES, and it does not take the rotation for a coordinate.
            var inference = PlacementColumns.Infer(DelimitedTables.Parse(text));
            Assert.True(inference.IsComplete);
            Assert.Equal(
                [
                    PlacementColumnRole.Refdes, PlacementColumnRole.Rotation,
                    PlacementColumnRole.X, PlacementColumnRole.Y, PlacementColumnRole.Side,
                ],
                inference.Roles);

            // And a named mapping reads the file.
            var read = PlacementFile.Read(
                path, text, LayoutUnits.DefaultDbuPerMicron, PlacementOrigin.BodyCentre,
                LayoutUnit.Mm, null, null, PlacementColumns.ToColumns(inference.Roles));

            Assert.Null(read.Refusal);
            Assert.Equal(4, read.Rows.Count);
            Assert.Equal(["C1", "C2", "R7", "U3"], read.Rows.Select(r => r.Refdes));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// <b>The origin, the unit and the column names survive a save and reopen.</b>
    /// </summary>
    /// <remarks>
    /// All three are answers a HUMAN gave the import dialog, and none of them was written down. So
    /// the same document opened again re-raised the origin refusal it had already answered and threw
    /// a column mapping away entirely — which is the "second surface says less than the first"
    /// shape this round found three times.
    /// </remarks>
    [Fact]
    public void ThePlacementReadingSurvivesASaveAndReopen()
    {
        var doc = new RailDocument
        {
            PlacementRef = "place.txt",
            Placement = new RailPlacementReading
            {
                Origin  = PlacementOrigin.PinOne,
                Units   = LayoutUnit.Mil,
                Columns = ["refdes", "x", "y", "", "rotation"],
            },
        };

        string path = Path.Combine(Path.GetTempPath(), $"crf-fr3-{Guid.NewGuid():N}.crail");
        try
        {
            RailDocumentIo.SaveToFile(path, doc);
            var back = RailDocumentIo.LoadFromFile(path);

            Assert.Equal(PlacementOrigin.PinOne, back.Placement.Origin);
            Assert.Equal(LayoutUnit.Mil, back.Placement.Units);
            Assert.Equal(["refdes", "x", "y", "", "rotation"], back.Placement.Columns);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A document that states no reading round-trips as one — which is every document
    /// written before this existed, and every board whose placement file names its own columns.</summary>
    [Fact]
    public void ADocumentWithNoPlacementReadingRoundTripsAsOne()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf-fr3-empty-{Guid.NewGuid():N}.crail");
        try
        {
            RailDocumentIo.SaveToFile(path, new RailDocument());

            // Absent from the file entirely, rather than three nulls a reader has to interpret.
            Assert.DoesNotContain("\"placement\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.True(RailDocumentIo.LoadFromFile(path).Placement.IsEmpty);
        }
        finally { File.Delete(path); }
    }

    // ══ 5 — the same message three times is one row and a count ════════════════════════════════

    /// <summary>
    /// <b>Consecutive identical messages collapse; messages with anything between them do not.</b>
    /// </summary>
    /// <remarks>
    /// The second half is the one that matters. Collapsing across the whole log would hide where
    /// each episode fell, which is what a timestamped log is for — so the property asserted is
    /// CONSECUTIVE, not "distinct".
    /// </remarks>
    [Fact]
    public void ConsecutiveIdenticalMessagesCollapseToOneRowWithACount()
    {
        const string courtyard =
            "technology 'x' declares no courtyard/assembly layer (F.CrtYd or F.Fab), so the "
          + "courtyard outline was omitted.";

        var log = new List<MessageEntry>();

        for (int i = 0; i < 3; i++)
            MessageOrdering.Insert(log, MessageEntry.Warning(courtyard));

        Assert.Single(log);
        Assert.Equal(3, log[0].RepeatCount);
        Assert.Equal("×3", log[0].RepeatText);

        // Something between them makes the next one a new episode, and the count does not carry.
        MessageOrdering.Insert(log, MessageEntry.Info("Saved."));
        MessageOrdering.Insert(log, MessageEntry.Warning(courtyard));

        Assert.Equal(3, log.Count);
        Assert.Equal(1, log[2].RepeatCount);
        Assert.Equal("", log[2].RepeatText);

        // A message said once renders exactly as it always did.
        Assert.False(log[1].HasRepeats);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
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
