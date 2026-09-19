// brief-railrf-7-window.md §8 — the railRF window's own gate.
//
// ── VIEW-MODEL DRIVEN, WITH THE WINDOW ONLY WHERE CHROME IS THE SUBJECT ──────────────────────────
//
// The Match Designer's own test shape, and for its reason: everything that decides what this window
// MEANS — the lists, the run gate, the Fast loop, the model-kind invariant, the refusals — is on
// RailRfViewModel, which is framework-free and needs no application host. What genuinely IS chrome
// (R-rail7-2's centring rule and its one exception) is a scan of the AXAML, because that is where
// the decision lives and a constructed window would only re-read the same file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailWindowTests
{
    // ══ R-rail7-2 — the owner's two rules, and BOTH halves of the first ══════════════════════════

    /// <summary>
    /// Centred text on every push button and on the combobox's selection and its items —
    /// <b>and its ABSENCE on the sortable column headers</b>.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because the exception is an owner decision and a test that only checked the
    /// rule would delete it.</b> A click-to-sort column header stays left-aligned with the column
    /// under it: the text under it is a bare TextBlock starting at the column's own left edge, and
    /// any centring there is a misalignment the user can see.
    ///
    /// <para><b>The rule is carried by a window-level STYLE rather than by an attribute on each
    /// control</b>, which is the same decision said once instead of once per button — and the reason
    /// the ORDERING assertion below is load-bearing: Avalonia resolves competing styles last-one-wins
    /// rather than by CSS specificity, so <c>Button.gridhdr</c> only beats the bare <c>Button</c>
    /// selector because it is declared after it. Swap the two blocks and every column header silently
    /// centres.</para>
    /// </remarks>
    [Fact]
    public void R_rail7_2_CentresButtonsAndCombos_ButNotTheSortableColumnHeaders()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        int bareButton = IndexOfStyle(xaml, "Button");
        int bareCombo  = IndexOfStyle(xaml, "ComboBox");
        int comboItem  = IndexOfStyle(xaml, "ComboBoxItem");
        int gridHeader = IndexOfStyle(xaml, "Button.gridhdr");

        Assert.True(bareButton >= 0, "The window declares no Button style, so nothing centres a button's text.");
        Assert.True(bareCombo  >= 0, "The window declares no ComboBox style, so nothing centres the selection.");
        Assert.True(comboItem  >= 0, "The window declares no ComboBoxItem style, so the ITEMS are not centred — "
                                   + "the owner's rule names the selection AND its items.");
        Assert.True(gridHeader >= 0, "The window declares no Button.gridhdr style, so the one exception is gone.");

        Assert.Equal("Center", SetterIn(xaml, bareButton, "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterIn(xaml, bareCombo,  "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterIn(xaml, comboItem,  "HorizontalContentAlignment"));

        Assert.Equal("Left", SetterIn(xaml, gridHeader, "HorizontalContentAlignment"));

        Assert.True(gridHeader > bareButton,
            "Button.gridhdr is declared BEFORE the bare Button style. Avalonia resolves competing "
          + "styles last-one-wins, not by specificity, so the bare style would win and every "
          + "click-to-sort column header would centre — which is the misalignment R-rail7-2's one "
          + "exception exists to prevent.");
    }

    /// <summary>The same rule reaches the import dialog, which is the window's other surface.</summary>
    [Fact]
    public void R_rail7_2_TheImportDialogCentresItsButtonsAndCombosToo()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");

        Assert.Equal("Center", SetterIn(xaml, IndexOfStyle(xaml, "Button"),       "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterIn(xaml, IndexOfStyle(xaml, "ComboBox"),     "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterIn(xaml, IndexOfStyle(xaml, "ComboBoxItem"), "HorizontalContentAlignment"));
    }

    // ══ R-rail7-3 — the lists, observe, and the rail selector ════════════════════════════════════

    [Fact]
    public void R_rail7_3_SourceAndLoadListsAddAndRemove()
    {
        var vm = Window(OneRail());

        Assert.Single(vm.Sources);
        Assert.Single(vm.Loads);

        vm.AddSourceCommand.Execute(null);
        vm.AddLoadCommand.Execute(null);
        Assert.Equal(2, vm.Sources.Count);
        Assert.Equal(2, vm.Loads.Count);
        Assert.Equal(2, vm.SelectedRail!.Sources.Count);
        Assert.Equal(2, vm.SelectedRail.Loads.Count);

        vm.RemoveSourceCommand.Execute(vm.Sources[1]);
        vm.RemoveLoadCommand.Execute(vm.Loads[1]);
        Assert.Single(vm.Sources);
        Assert.Single(vm.Loads);
        Assert.Single(vm.SelectedRail.Sources);
        Assert.Single(vm.SelectedRail.Loads);
    }

    /// <summary>
    /// <b>A load row with no current reads <i>observe</i>, because that is what it is.</b>
    /// </summary>
    /// <remarks>
    /// And clearing the field puts it back — the gesture has to exist, or a row typed by accident is
    /// a load with a current the user cannot take back. A defaulted zero and a stated zero are the
    /// same number and mean different things.
    /// </remarks>
    [Fact]
    public void R_rail7_3_ALoadWithNoCurrentReadsObserve_AndClearingRestoresIt()
    {
        var vm = Window(OneRail());
        var row = vm.Loads[0];

        Assert.Equal(RailLoadRowViewModel.ObserveText, row.CurrentEntry);
        Assert.True(row.IsObservationOnly);
        Assert.Null(row.Load.DcCurrentA);

        row.CurrentEntry = "120 mA";
        Assert.Equal(0.120, row.Load.DcCurrentA!.Value, 9);
        Assert.False(row.IsObservationOnly);

        row.CurrentEntry = "";
        Assert.Equal(RailLoadRowViewModel.ObserveText, row.CurrentEntry);
        Assert.True(row.IsObservationOnly);
        Assert.Null(row.Load.DcCurrentA);
    }

    /// <summary>The rail selector switches which rail the WHOLE window is showing — §11.3's fourth
    /// point, and the reason it is a selector rather than a filter on the lists.</summary>
    [Fact]
    public void R_rail7_3_TheRailSelectorSwitchesTheWholeWindow()
    {
        var doc = OneRail();
        var second = new RailSpec { Name = "+3V3" };
        second.Loads.Add(new RailLoad { Anchor = Pad("U9", "VCC"), DcCurrentA = 0.05 });
        second.Loads.Add(new RailLoad { Anchor = Pad("U8", "VCC"), DcCurrentA = 0.01 });
        doc.Rails.Add(second);

        var vm = Window(doc);

        Assert.Equal("+1V8", vm.SelectedRailName);
        Assert.Single(vm.Loads);

        vm.SelectedRailName = "+3V3";

        Assert.Equal(2, vm.Loads.Count);
        Assert.Equal("U9.VCC", vm.Loads[0].Anchor);
        Assert.Same(second, vm.SelectedRail);
    }

    // ══ R-rail7-5 — the Fast edit loop, and the invariant it must not break ══════════════════════

    [Fact]
    public void R_rail7_5_AnEditReSolves()
    {
        var vm = Ready(out _);
        int before = vm.SolvesStarted;

        vm.Loads[0].CurrentEntry = "120 mA";

        Assert.Equal(before + 1, vm.SolvesStarted);
    }

    /// <summary>
    /// <b>A re-solve in flight when another edit arrives is CANCELLED, not queued.</b>
    /// </summary>
    /// <remarks>
    /// Queueing them means a user who types four characters waits for four solves to discover the
    /// answer to the fourth. The stub solve here blocks on a gate so the second edit genuinely
    /// arrives mid-solve — which is the only way to observe the difference at all.
    /// </remarks>
    [Fact]
    public void R_rail7_5_ASecondEditMidSolveCancelsTheFirst()
    {
        // NOT `using`: releasing the gate is the last thing this test does, and a Dispose racing a
        // still-waiting stub solve would throw on the background thread rather than fail an
        // assertion — noise that looks like the defect under test.
        var gate = new ManualResetEventSlim(false);
        var vm = Ready(out _);

        var tokens = new List<CancellationToken>();
        vm.SolveFunc = (request, token) =>
        {
            lock (tokens) tokens.Add(token);
            gate.Wait(TimeSpan.FromSeconds(5));
            return Solved(request);
        };
        vm.RunOffThread = (work, token) => Task.Run(work, CancellationToken.None);

        vm.Loads[0].CurrentEntry = "120 mA";
        SpinUntil(() => { lock (tokens) return tokens.Count == 1; });

        int cancelledBefore = vm.SolvesCancelled;
        vm.Loads[0].CurrentEntry = "130 mA";

        Assert.Equal(cancelledBefore + 1, vm.SolvesCancelled);
        Assert.True(tokens[0].IsCancellationRequested,
            "The first solve's RunControl token was never cancelled, so a second edit QUEUES behind "
          + "the first instead of superseding it.");

        gate.Set();
    }

    /// <summary>
    /// <b>The model kind on screen always matches the numbers on screen</b> — asserted ON THE
    /// TRANSITION rather than at rest.
    /// </summary>
    /// <remarks>
    /// A strip reading <i>Fast</i> over an Accuracy result, for even one frame, is the exact failure
    /// R-rail4-2 exists to prevent — so checking only the settled state would pass on a window that
    /// updated the two in sequence. Every property notification is inspected as it arrives.
    /// </remarks>
    [Fact]
    public void R_rail7_5_TheStripAndTheNumbersAgreeAtEveryTransition()
    {
        var vm = Ready(out _);
        var disagreements = new List<string>();

        vm.PropertyChanged += (_, _) =>
        {
            string strip = vm.ModelKindText;
            var kind = vm.ResultsModelKind;

            bool agrees = kind switch
            {
                PdnModelKind.Fast     => strip == "Fast model",
                PdnModelKind.Accurate => strip == "Accuracy",
                _                     => strip == "no result yet",
            };
            if (!agrees) disagreements.Add($"strip '{strip}' over {kind?.ToString() ?? "no result"}");
        };

        vm.Loads[0].CurrentEntry = "120 mA";
        vm.AccuracyCommand.Execute(null);
        vm.Loads[0].CurrentEntry = "130 mA";

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// §2.9's fourth rule: running Accuracy does not displace the Fast result.
    /// </summary>
    /// <remarks>
    /// The two are compared on the user's own board rather than trusted from a document, and brief 12
    /// can only draw the fast curve beside the accurate one if the fast one is still there.
    /// </remarks>
    [Fact]
    public void R_rail7_5_AccuracyDoesNotDisplaceTheFastResult()
    {
        var vm = Ready(out _);

        vm.RunCommand.Execute(null);
        Assert.True(vm.ByModel.ContainsKey(PdnModelKind.Fast));

        vm.AccuracyCommand.Execute(null);

        Assert.True(vm.ByModel.ContainsKey(PdnModelKind.Fast),
            "Running Accuracy displaced the Fast result, so the two can never be compared on this board.");
        Assert.True(vm.HasBothModels);
        Assert.Equal(PdnModelKind.Accurate, vm.ResultsModelKind);
    }

    /// <summary><b>Accuracy is never entered automatically.</b> Every edit stays in Fast.</summary>
    [Fact]
    public void R_rail7_5_AnEditNeverEntersAccuracy()
    {
        var vm = Ready(out _);
        vm.AccuracyCommand.Execute(null);
        Assert.Equal(PdnModelKind.Accurate, vm.ResultsModelKind);

        vm.Loads[0].CurrentEntry = "120 mA";

        Assert.Equal(PdnModelKind.Fast, vm.ResultsModelKind);
        Assert.Equal("Fast model", vm.ModelKindText);
    }

    // ══ R-rail7-6 — the import lands in a cell, and that checkbox is ON by default ═══════════════

    [Fact]
    public void R_rail7_6_TheImportCheckboxDefaultsOn()
    {
        Assert.True(new RailImportOptions().LandInWorkspace,
            "The import's land-in-workspace default is off. The artwork would be re-imported every "
          + "session, would not open in the layout editor, would not run DRC, would not render "
          + "headlessly and would not be kept by revision control.");

        // And the checkbox on the dialog says the same thing, so the two cannot drift.
        string xaml = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");
        Assert.Matches(new Regex(@"x:Name=""LandInWorkspaceBox""\s+IsChecked=""True"""), xaml);
    }

    /// <summary>With no workspace open, railRF OFFERS to create one — rather than silently falling
    /// back to the throwaway path, which would be invisible until the next session.</summary>
    [Fact]
    public void R_rail7_6_WithNoWorkspaceOpen_TheCreateWorkspaceOfferAppears()
    {
        var options = new RailImportOptions { ArtworkPath = "/b/board.gbr", WorkspaceDir = null };

        Assert.True(options.LandInWorkspace);
        Assert.Null(options.WorkspaceDir);

        string src = Src("src/Ui/Views/RailRf/RailRfWindow.Import.cs");
        Assert.Contains("OfferToCreateWorkspaceAsync", src, StringComparison.Ordinal);
        Assert.Contains("WorkspaceCreate.Create", src, StringComparison.Ordinal);

        string dialog = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");
        Assert.Contains("NoWorkspaceText", dialog, StringComparison.Ordinal);
    }

    /// <summary>Unchecking it produces a document whose <c>ArtworkCellRef</c> is a throwaway — which
    /// here means it is not a cell in the workspace at all.</summary>
    [Fact]
    public void R_rail7_6_UncheckingItGivesAThrowawayArtworkRef()
    {
        var vm = Window(OneRail());
        var options = new RailImportOptions { ArtworkPath = "/b/board.gbr", LandInWorkspace = false };

        vm.ApplyImport(options, Board(artworkCellRef: null));

        Assert.Null(vm.Document.ArtworkCellRef);

        vm.ApplyImport(options with { LandInWorkspace = true },
                       Board(artworkCellRef: "/ws/board/layout/board.clay"));

        Assert.Equal("/ws/board/layout/board.clay", vm.Document.ArtworkCellRef);
    }

    // ══ R-rail7-7 — the two things that must not be guessed ══════════════════════════════════════

    /// <summary>
    /// <b>The origin combo has NO selection on open</b>, and the refusal names the flag.
    /// </summary>
    /// <remarks>
    /// Q-14: there is no house convention to learn, and a default here is the guess the refusal
    /// exists to prevent. Three quarters of a millimetre on an 0402 is the difference between landing
    /// on the part's own pad and landing on its neighbour's — and unlike a wrong drill format, a
    /// wrong origin is SILENT.
    /// </remarks>
    [Fact]
    public void R_rail7_7_TheOriginIsUnanswered_AndRunIsRefusedNamingTheFlag()
    {
        var vm = Ready(out _);

        var options = new RailImportOptions
        {
            ArtworkPath   = "/b/board.gbr",
            PlacementPath = "/b/board-all-pos.csv",
        };
        Assert.Null(options.PlacementOrigin);
        Assert.True(options.NeedsPlacementOrigin);

        vm.ApplyImport(options, Board(), placement: UnstatedOrigin(471));

        Assert.False(vm.CanRun);
        Assert.NotNull(vm.Refusal);
        Assert.Contains("--origin", vm.Refusal!.Sentence, StringComparison.Ordinal);
        Assert.Contains("471", vm.Refusal.Sentence, StringComparison.Ordinal);
        Assert.Equal(RailRefusalControl.PlacementOrigin, vm.Refusal.Control);
        Assert.True(vm.IsPlacementOriginFlagged);

        // Answering it clears the refusal and the gate.
        vm.ApplyImport(options with { PlacementOrigin = PlacementOrigin.PinOne }, Board(),
                       placement: ChosenOrigin(PlacementOrigin.PinOne));
        Assert.True(vm.CanRun);
        Assert.False(vm.IsPlacementOriginFlagged);
    }

    /// <summary>The dialog's own combo carries no pre-selection either — the XAML declares a
    /// placeholder and nothing sets <c>SelectedIndex</c>.</summary>
    [Fact]
    public void R_rail7_7_TheDialogsOriginComboIsNotPreSelected()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailImportDialog.axaml");
        Assert.Contains("PlaceholderText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedIndex=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem=", xaml, StringComparison.Ordinal);

        string src = Src("src/Ui/Views/RailRf/RailImportDialog.axaml.cs");
        Assert.DoesNotMatch(new Regex(@"OriginCombo\.Selected\w+\s*="), src);
    }

    /// <summary>The Excellon refusal is <c>convert</c>'s own, reused rather than re-worded — two
    /// surfaces wording one refusal two ways is a defect that only shows up when someone compares
    /// their answers.</summary>
    [Fact]
    public void R_rail7_7_TheDrillFormatRefusalIsTheExistingDialog()
    {
        string src = Src("src/Ui/Views/RailRf/RailRfWindow.Import.cs");
        Assert.Contains("GerberDrillFormatPromptDialog", src, StringComparison.Ordinal);
        Assert.Contains("resolveDrillFormat", src, StringComparison.Ordinal);
    }

    // ══ R-rail7-8 — the reference is confirmed, never assumed ════════════════════════════════════

    /// <summary>
    /// <b>Run is disabled until the reference layer is affirmatively set</b>, and the proposal is
    /// shown WITH its reason.
    /// </summary>
    /// <remarks>
    /// A pre-selected combo a user tabs past is not a confirmation. This is the one place the window
    /// deliberately costs the user a click.
    /// </remarks>
    [Fact]
    public void R_rail7_8_RunIsRefusedUntilTheReferenceIsConfirmed_AndTheProposalSaysWhy()
    {
        var doc = OneRail();
        doc.Rails[0].ReferenceLayer = null;

        var vm = Window(doc);
        vm.Board = Board();

        Assert.False(vm.IsReferenceConfirmed);
        Assert.False(vm.CanRun);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.Contains("Confirm the reference layer", vm.RunBlockedReason, StringComparison.Ordinal);

        Assert.NotNull(vm.ReferenceProposal);
        Assert.Contains("ground reference", vm.ReferenceProposalReason, StringComparison.Ordinal);
        Assert.Contains("never assumes", vm.ReferenceProposalReason, StringComparison.Ordinal);

        // A proposal is not a selection: the rail still states nothing until it is confirmed.
        Assert.Null(doc.Rails[0].ReferenceLayer);

        vm.ConfirmReferenceCommand.Execute(null);

        Assert.True(vm.IsReferenceConfirmed);
        Assert.True(vm.CanRun);
        Assert.Equal(vm.ReferenceProposal!.Key, doc.Rails[0].ReferenceLayer);
    }

    /// <summary>A stackup that marks no ground reference gets NO proposal and is told so — railRF
    /// never infers one, and a guess dressed as a proposal is what Q-8 closed.</summary>
    [Fact]
    public void R_rail7_8_AStackupThatMarksNoGroundGetsNoProposal()
    {
        var (proposal, reason) = RailRfViewModel.ProposeReference(TechWithoutGround(), OneRail().Rails[0]);

        Assert.Null(proposal);
        Assert.Contains("proposes none", reason, StringComparison.Ordinal);
        Assert.Contains("never infers one", reason, StringComparison.Ordinal);
    }

    /// <summary>The pour-clicking and net-picking routes both make a rail, and the pour route names
    /// no net — railRF will not guess a net name.</summary>
    /// <remarks>
    /// The two flags are asserted alongside, because a board with no netlist has to SAY that the pick
    /// is made on the artwork: a pane showing neither a list nor a sentence reads as a missing
    /// feature rather than as the assisted-Gerber path.
    /// </remarks>
    [Fact]
    public void R_rail7_8_BothPickingRoutesMakeARail()
    {
        var vm = Window(new RailDocument());
        vm.Board = Board();

        Assert.False(vm.HasPickableNets);
        Assert.True(vm.HasNoPickableNets);

        var byNet = vm.PickRail("+1V8");
        Assert.Equal("+1V8", byNet.NetName);
        Assert.Equal("+1V8", vm.SelectedRailName);

        var byPour = vm.PickRailAt(1_000, 2_000);
        Assert.Null(byPour.NetName);
        Assert.Equal((1_000L, 2_000L), byPour.Sources[0].Anchor.Point);
        Assert.Equal(byPour.Name, vm.SelectedRailName);
        Assert.Equal(2, vm.Rails.Count);
    }

    // ══ R-rail7-9 — unresolved is listed AS unresolved ═══════════════════════════════════════════

    [Fact]
    public void R_rail7_9_AnUnresolvedPartRendersAsUnresolved_AndPopulatesNoNumericColumn()
    {
        var vm = Window(OneRail());
        vm.Bom = TwoPartBom();
        vm.PartLibrary = LibraryKnowing("CAP-100N-0402");

        Assert.Equal(2, vm.Parts.Count);

        var known = vm.Parts.Single(p => p.Refdes == "C1");
        Assert.False(known.IsUnresolved);
        Assert.Equal("library row", known.ModelSourceText);

        var unknown = vm.Parts.Single(p => p.Refdes == "C2");
        Assert.True(unknown.IsUnresolved);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, unknown.ModelSourceText);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, unknown.EsrText);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, unknown.MountingInductanceText);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, unknown.DeratedText);
        Assert.Equal(1, vm.PartsUnresolved);
    }

    /// <summary>§9's headline numbers are on the STATUS STRIP, not a column somebody has to total
    /// up: how many parts are modelled from a file, and how many carry no bias curve.</summary>
    [Fact]
    public void R_rail7_9_TheHeadlineCountsAreOnTheStatusStrip()
    {
        var vm = Window(OneRail());
        vm.Bom = TwoPartBom();
        vm.PartLibrary = LibraryKnowing("CAP-100N-0402");

        Assert.Equal(1, vm.PartsWithoutBiasCurve);
        Assert.Contains("1 part(s) with no bias curve", vm.StatusLine, StringComparison.Ordinal);
    }

    // ══ R-rail18-5 — the table is of the RAIL, not of the BOM ═══════════════════════════════════

    /// <summary>
    /// A document with <b>no BOM at all</b> lists its own part rows — the P1 case R-rail11-8 makes
    /// ordinary, and the one the <c>Power Rail</c> example is.
    /// </summary>
    /// <remarks>
    /// <c>RebuildParts</c> returned early unless a BOM had been imported, so a rail whose thirteen
    /// typed capacitors drive every resonance on its curve showed an empty pane — while
    /// <c>BuildSweepRequest</c> resolved those same rows through <c>RailPartResolver</c> and put
    /// them in the answer.
    /// </remarks>
    [Fact]
    public void R_rail18_5_WithNoBomAtAll_TheTableListsTheRailsOwnParts()
    {
        var doc = OneRail();
        var rail = doc.Rails[0];
        rail.Parts.Clear();
        for (int i = 1; i <= 13; i++)
            rail.Parts.Add(new RailPart
            {
                Refdes = $"C{i}", PartNumber = "CAP-100N-0402", MountingInductanceHenries = 0.85e-9,
            });

        var vm = Window(doc);
        vm.PartLibrary = LibraryKnowing("CAP-100N-0402");

        Assert.Null(vm.Bom);
        Assert.Equal(13, vm.Parts.Count);

        // The rail's own columns are filled from the rail: the part number and the mounting loop are
        // the document's, and neither needs a BOM to exist.
        var c7 = vm.Parts.Single(p => p.Refdes == "C7");
        Assert.Equal("CAP-100N-0402", c7.PartNumber);
        Assert.False(c7.IsUnresolved);
        Assert.Equal(0.85e-9, c7.MountingInductanceHenries!.Value, 15);
    }

    /// <summary>
    /// A BOM naming parts that are not on this rail does not add rows. <b>The table is headed by the
    /// rail selector</b>, and listing another rail's decoupling under it is the same class of defect
    /// as listing none.
    /// </summary>
    [Fact]
    public void R_rail18_5_ABomNamingAnotherRailsPartsAddsNoRows()
    {
        var doc = OneRail();                       // its rail names C1 and C2
        var vm = Window(doc);
        vm.PartLibrary = LibraryKnowing("CAP-100N-0402");

        vm.Bom = new BomTable(
            "/b/bom.csv", null, ',',
            [
                new BomRow("C1",  "CAP-100N-0402", "100n", "0402", "MLCC 100n 16V 0402 X7R"),
                new BomRow("C40", "CAP-100N-0402", "100n", "0402", "on the +5V rail"),
                new BomRow("C41", "CAP-100N-0402", "100n", "0402", "on the +5V rail"),
            ],
            SourceRowCount: 3, UnreadableRows: 0, RecognisedAggressors: [], Diagnostics: []);

        Assert.Equal(["C1", "C2"], vm.Parts.Select(p => p.Refdes));

        // C1 is enriched by the BOM — the part number it did not state, and its marked value.
        var c1 = vm.Parts.Single(p => p.Refdes == "C1");
        Assert.Equal("CAP-100N-0402", c1.PartNumber);
        Assert.Equal("100n", c1.MarkedText);

        // C2 is on the rail and not in the BOM, so it is a row with an unresolved part number —
        // never a missing row.
        var c2 = vm.Parts.Single(p => p.Refdes == "C2");
        Assert.Equal(RailPartRowViewModel.UnresolvedText, c2.PartNumber);
        Assert.True(c2.IsUnresolved);
    }

    // ══ R-rail18-6 — the tab strip partitions the column ════════════════════════════════════════

    /// <summary>
    /// With the tab on <c>DC</c> the frequency cards are not visible and the DC ones are; with it on
    /// <c>frequency</c>, the reverse.
    /// </summary>
    /// <remarks>
    /// <c>SelectedResultsTab</c>'s only reader was <c>RailRfWindow.SyncTabs</c>, which assigns the
    /// two <c>ToggleButton.IsChecked</c> values — every card lived in one <c>ScrollViewer</c> gated
    /// on its own <c>Has…</c> property, so pressing <c>frequency</c> lit a button and changed
    /// nothing. Nothing was hidden and nothing was wrong; it is simply not what a tab means.
    ///
    /// <para><b>The XAML binding is asserted too</b>, because the properties below could be correct
    /// and bound to nothing — which is the state this defect was in, one layer along.</para>
    /// </remarks>
    [Fact]
    public void R_rail18_6_TheTabStripPartitionsTheResultsColumn()
    {
        var vm = Window(OneRail());

        vm.SelectedResultsTab = RailResultsTab.Dc;
        Assert.True(vm.ShowDropCard && vm.ShowBreakdownCard && vm.ShowViaCheckCard);
        Assert.False(vm.ShowPlaneResonancesCard);
        Assert.False(vm.ShowCoincidencesCard);
        Assert.False(vm.ShowMaskCard);
        Assert.False(vm.ShowAntiResonancesCard);
        Assert.False(vm.ShowRemovalCard);
        Assert.False(vm.ShowImpedanceMessageCard);

        vm.SelectedResultsTab = RailResultsTab.Frequency;
        Assert.False(vm.ShowDropCard || vm.ShowBreakdownCard || vm.ShowViaCheckCard
                  || vm.ShowStackupCard);
        Assert.True(vm.ShowPlaneResonancesCard);

        // Every card the window binds is one of these ten, and each is bound exactly once.
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");
        foreach (string card in new[]
                 {
                     "ShowStackupCard", "ShowDropCard", "ShowBreakdownCard", "ShowViaCheckCard",
                     "ShowCoincidencesCard", "ShowMaskCard", "ShowAntiResonancesCard",
                     "ShowRemovalCard", "ShowPlaneResonancesCard", "ShowImpedanceMessageCard",
                 })
            Assert.Equal(1, Regex.Matches(xaml, $@"IsVisible=""{{Binding {card}}}""").Count);
    }

    /// <summary>
    /// A card with nothing to say still does not appear — the tab NARROWS what is shown and never
    /// forces an empty card into view.
    /// </summary>
    [Fact]
    public void R_rail18_6_TheTabNeverForcesAnEmptyCardIntoView()
    {
        var vm = Window(OneRail());
        vm.SelectedResultsTab = RailResultsTab.Frequency;

        // Nothing has been swept, so there is no mask verdict, no coincidence list and no ranking.
        Assert.False(vm.HasMaskVerdict);
        Assert.False(vm.ShowMaskCard);
        Assert.False(vm.ShowCoincidencesCard);
        Assert.False(vm.ShowRemovalCard);
    }

    // ══ R-rail7-4 — every refusal states a number and turns its own control red ══════════════════

    /// <summary>
    /// Each of the six refusals of briefs 2-6 renders in the status strip with its number in it, and
    /// names the control that answers it.
    /// </summary>
    /// <remarks>
    /// <b>The sentences here are the ENGINES' OWN</b>, copied from the files that raise them — which
    /// is what makes this test worth having: <c>RailRefusals.Classify</c> matches on the stem of a
    /// sentence it does not own, so a re-worded engine sentence fails here rather than quietly
    /// turning nothing red.
    /// </remarks>
    [Theory]
    // PdnGraphExtractor / PdnMeshExtractor — a rail with no reference layer.
    [InlineData("Rail '+1V8' states no reference layer, so there is nothing to return current "
              + "through and no graph to build.", RailRefusalControl.ReferenceLayer, "+1V8")]
    // PdnGraphExtractor — Fast above its shunt-band threshold (R-rail4-4).
    [InlineData("The fast model cannot answer above 18.1 MHz on this stackup, and 40 MHz was asked "
              + "for. Run Accuracy — it carries the cavity model and answers here.",
                RailRefusalControl.ModelKind, "18.1")]
    // PdnAssembly — an unresolved via span.
    [InlineData("14 hole(s) could not be resolved to a layer span and carry no barrel resistance.",
                RailRefusalControl.Stackup, "14")]
    // Brief 2 / the import — the placement origin.
    [InlineData("board-all-pos.csv holds 471 placement row(s) and does not state its coordinate "
              + "origin; pass --origin or set it here.", RailRefusalControl.PlacementOrigin, "471")]
    // convert's own rule — the Excellon coordinate format.
    [InlineData("board.drl does not state its coordinate format, so circuitRF inferred one: "
              + "mm 3:3 leading.", RailRefusalControl.DrillFormat, "3:3")]
    // RailOrder — a cycle in the rail order.
    [InlineData("The rails cannot be put in a solve order: '+3V3' → '+1V8' → '+3V3'. 'U2' is a load "
              + "on rail '+3V3' and a source on rail '+1V8', which closes the loop.",
                RailRefusalControl.RailSelector, "U2")]
    public void R_rail7_4_EachRefusalStatesItsNumberAndNamesItsControl(
        string sentence, RailRefusalControl expected, string numberInIt)
    {
        var refusal = RailRefusals.Classify(sentence);

        Assert.Equal(expected, refusal.Control);
        Assert.Contains(numberInIt, refusal.Sentence, StringComparison.Ordinal);

        var vm = Window(OneRail());
        vm.Refusal = refusal;

        Assert.True(vm.HasRefusal);
        Assert.Equal(sentence, vm.Refusal!.Sentence);

        Assert.Equal(expected == RailRefusalControl.PlacementOrigin, vm.IsPlacementOriginFlagged);
        Assert.Equal(expected == RailRefusalControl.DrillFormat,     vm.IsDrillFormatFlagged);
        Assert.Equal(expected == RailRefusalControl.ReferenceLayer,  vm.IsReferenceLayerFlagged);
        Assert.Equal(expected == RailRefusalControl.RailSelector,    vm.IsRailSelectorFlagged);
        Assert.Equal(expected == RailRefusalControl.ModelKind,       vm.IsModelKindFlagged);
        Assert.Equal(expected == RailRefusalControl.Stackup,         vm.IsStackupFlagged);
    }

    /// <summary>Every control the classifier can name has somewhere on the window to turn red —
    /// a refusal attributed to a control nothing binds is a sentence with a dead pointer on it.</summary>
    [Fact]
    public void R_rail7_4_EveryAttributableControlIsBoundInTheWindow()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        var bound = new Dictionary<RailRefusalControl, string>
        {
            [RailRefusalControl.ReferenceLayer]  = "IsReferenceLayerFlagged",
            [RailRefusalControl.RailSelector]    = "IsRailSelectorFlagged",
            [RailRefusalControl.ModelKind]       = "IsModelKindFlagged",
            [RailRefusalControl.Stackup]         = "IsStackupFlagged",
        };

        foreach (var control in RailRefusals.Attributable)
        {
            // The two import refusals turn a control red on the IMPORT DIALOG, which is a different
            // surface and is not open while the window shows the sentence — so the window states them
            // and the dialog is where they are answered.
            if (control is RailRefusalControl.PlacementOrigin or RailRefusalControl.DrillFormat) continue;

            Assert.True(bound.TryGetValue(control, out string? flag),
                $"{control} is attributable but this test does not know which control binds it.");
            Assert.Contains(flag!, xaml, StringComparison.Ordinal);
        }
    }

    /// <summary>The status strip is ALWAYS on screen and states the model, the cost, the temperature
    /// and the reference extent — so nobody reads a fast answer as an accurate one.</summary>
    [Fact]
    public void TheStatusStripStatesTheModelTheCostTheTemperatureAndTheReference()
    {
        var vm = Ready(out _);
        vm.RunCommand.Execute(null);

        Assert.Contains("Fast model", vm.StatusLine, StringComparison.Ordinal);
        Assert.Contains("ms", vm.StatusLine, StringComparison.Ordinal);
        Assert.Contains("20 °C", vm.StatusLine, StringComparison.Ordinal);
        Assert.Contains("reference as imported", vm.StatusLine, StringComparison.Ordinal);
    }

    /// <summary>An optimistic reference extent SAYS it is optimistic, on every frame — the choice is
    /// behind Settings, and the consequence is not.</summary>
    [Fact]
    public void AnOptimisticReferenceExtentSaysSoOnTheStrip()
    {
        var vm = Ready(out _);
        vm.ReferenceExtent = RailReferenceExtent.FilledToOutline;

        Assert.Contains("optimistic", vm.StatusLine, StringComparison.Ordinal);
    }

    // ══ helpers ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A view model with an inline UI post and an inline off-thread run, so the Fast loop
    /// runs to completion synchronously — no application host anywhere.</summary>
    private static RailRfViewModel Window(RailDocument doc)
    {
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.SolveFunc = (request, _) => Solved(request);
        return vm;
    }

    /// <summary>A window with a board, a confirmed reference and nothing refused — the state every
    /// Fast-loop test starts from.</summary>
    private static RailRfViewModel Ready(out RailBoardInputs board)
    {
        var vm = Window(OneRail());
        board = Board();
        vm.Board = board;
        vm.ConfirmReferenceCommand.Execute(null);
        Assert.True(vm.CanRun);
        return vm;
    }

    private static RailDcRunResult Solved(RailDcRequest request) =>
        new(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);

    private static RailDocument OneRail()
    {
        var doc = new RailDocument { Name = "evk_1v8_compact" };
        var rail = new RailSpec { Name = "+1V8", NetName = "+1V8" };
        rail.Sources.Add(new RailSource { Anchor = Pad("BT1", "1"), OpenCircuitVoltageV = 3.7 });
        rail.Loads.Add(new RailLoad { Anchor = Pad("U1", "VDD") });   // no current — an observation port

        // The rail's OWN part rows, which is what the table lists (R-rail18-5a). They state no part
        // number, so the BOM supplies it where there is one — which is the enrichment being tested
        // in the two R-rail7-9 cases below.
        rail.Parts.Add(new RailPart { Refdes = "C1" });
        rail.Parts.Add(new RailPart { Refdes = "C2" });

        doc.Rails.Add(rail);
        return doc;
    }

    private static RailPortAnchor Pad(string refdes, string pin) => new() { Refdes = refdes, Pin = pin };

    private static RailBoardInputs Board(string? artworkCellRef = "/ws/board/layout/board.clay") => new()
    {
        Shapes         = [],
        Technology     = TechWithGround(),
        ArtworkCellRef = artworkCellRef,
    };

    /// <summary>A stackup whose second conductor is MARKED as a ground reference — which is the only
    /// thing the proposal reads.</summary>
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

    private static Technology TechWithoutGround()
    {
        var tech = TechWithGround();
        foreach (var l in tech.Stackup.Layers) l.IsGroundReference = false;
        return tech;
    }

    private static PlacementTable UnstatedOrigin(int rows) => new(
        "/b/board-all-pos.csv", null, null, PlacementOriginEvidence.Unstated,
        LayoutUnit.Mm, BoardNetlistUnitsEvidence.Defaulted, ',', [], rows, 0, DrillExtents.Empty, []);

    private static PlacementTable ChosenOrigin(PlacementOrigin origin) => new(
        "/b/board-all-pos.csv", null, origin, PlacementOriginEvidence.Chosen,
        LayoutUnit.Mm, BoardNetlistUnitsEvidence.Defaulted, ',', [], 0, 0, DrillExtents.Empty, []);

    private static BomTable TwoPartBom() => new(
        "/b/bom.csv", null, ',',
        [
            new BomRow("C1", "CAP-100N-0402", "100n", "0402", "MLCC 100n 16V 0402 X7R"),
            new BomRow("C2", "CAP-1U0-0603",  "1u",   "0603", "MLCC 1u0 10V 0603 X5R"),
        ],
        SourceRowCount: 2, UnreadableRows: 0, RecognisedAggressors: [], Diagnostics: []);

    private static PartLibrary LibraryKnowing(string partNumber)
    {
        var library = new PartLibrary { Name = "parts" };
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber        = partNumber,
            CapacitanceFarads = 100e-9,
            SelfResonantFrequencyHz = 20e6,
            DielectricClass   = "X7R",
        });
        return library;
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Assert.True(condition(), "The condition never became true within five seconds.");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>One source file with its comments stripped — this is about what the code does, and
    /// the comments name the very thing being scanned for.</summary>
    private static string Src(string relative)
    {
        string raw = Read(relative);
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "");
        return raw;
    }

    /// <summary>Where a <c>&lt;Style Selector="…"&gt;</c> block for exactly that selector starts, or -1.</summary>
    private static int IndexOfStyle(string xaml, string selector)
    {
        var m = Regex.Match(xaml, $@"<Style\s+Selector=""{Regex.Escape(selector)}""\s*>");
        return m.Success ? m.Index : -1;
    }

    /// <summary>The value of one <c>&lt;Setter&gt;</c> inside the style block starting at
    /// <paramref name="start"/>.</summary>
    private static string? SetterIn(string xaml, int start, string property)
    {
        if (start < 0) return null;
        int end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        if (end < 0) return null;

        var m = Regex.Match(xaml[start..end],
                            $@"<Setter\s+Property=""{Regex.Escape(property)}""\s+Value=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ══ Owner round, 2026-09-18 — what the window showed when nothing was loaded ════════════════

    /// <summary>
    /// <b>Opening a <c>.crail</c> loads the board and the part library it NAMES.</b>
    /// </summary>
    /// <remarks>
    /// Until this it loaded neither: <c>RailRfWindow.Show</c> constructed the view model and stopped,
    /// so the shipped Power Rail example — whose README says the window opens with the board already
    /// loaded — came up saying "No board yet. Import one", with its artwork sitting in the cell folder
    /// beside it. Driven on the SHIPPED example rather than on a fixture, because what is under test is
    /// that a document somebody can actually open resolves its own references.
    /// </remarks>
    [Fact]
    public void OpeningACrailLoadsTheArtworkAndPartLibraryItNames()
    {
        string crail = Path.Combine(RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.False(vm.HasBoard);   // nothing is resolved by the constructor, which is still true

        var notes = vm.LoadDocumentReferences();

        Assert.Empty(notes);
        Assert.True(vm.HasBoard, "The document names its artwork and it did not load.");
        Assert.NotEmpty(vm.Board!.Shapes);
        Assert.NotNull(vm.Board!.Technology);
        Assert.NotNull(vm.PartLibrary);

        // And the README's next word is "press Run" — which is only true if the reference the
        // document already names is one this stackup offers, so no click is charged for a decision
        // somebody made when they saved the file.
        Assert.True(vm.CanRun, vm.RunBlockedReason);
    }

    /// <summary>
    /// A <c>.crail</c> whose artwork has moved still OPENS, and says why the board is not there.
    /// </summary>
    /// <remarks>
    /// The rails, the ports and the target are the document; the artwork is a reference. Refusing the
    /// open would lose the half that is still readable, and opening silently with no board is the
    /// defect above. So: a note, and the window's own run gate then refuses Run for its own reason.
    /// </remarks>
    [Fact]
    public void AnArtworkReferenceThatDoesNotResolveIsReportedAndDoesNotStopTheOpen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-rail-open-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var document = new RailDocument { Name = "moved", ArtworkCellRef = "layout/Gone.clay" };
            document.Rails.Add(new RailSpec { Name = "+3V3", NetName = "+3V3" });

            string path = Path.Combine(dir, "moved.crail");
            RailDocumentIo.SaveToFile(path, document);

            var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
            var notes = vm.LoadDocumentReferences();

            Assert.False(vm.HasBoard);
            Assert.Contains(notes, n => n.Contains("layout/Gone.clay", StringComparison.Ordinal));
            Assert.Single(vm.Rails);   // the half that is still readable came up
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// <b>One empty state on the board panel, not two drawn over each other.</b>
    /// </summary>
    /// <remarks>
    /// <c>RailMapScene.Build</c> returns a centred note of its own ("No result yet. Run the rail.")
    /// whenever there is no result, and the panel's placeholder ("No board yet. Import one …") is
    /// centred too — so Tools ▸ railRF drew both sentences on the same pixels. The canvas is hidden
    /// until there is a board, which is the only state in which both can be true at once.
    /// </remarks>
    [Fact]
    public void TheBoardCanvasIsHiddenUntilThereIsABoard()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        var m = Regex.Match(xaml, @"<ctl:LayoutCanvas\s+Name=""BoardCanvas""[^>]*?>", RegexOptions.Singleline);
        Assert.True(m.Success, "The board canvas is no longer declared under that name.");
        Assert.Contains(@"IsVisible=""{Binding HasBoard}""", m.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The reason Run is refused is said ONCE.</b>
    /// </summary>
    /// <remarks>
    /// <c>RefreshRunGate</c> turns every gate reason into a <see cref="RailRefusal"/>, so the status
    /// strip already carries that exact sentence — in the warning colour, with the control that
    /// answers it turned red. The bottom bar was binding <c>RunBlockedReason</c> as well, so a window
    /// with no board showed the same sentence twice, one row apart, in two different colours. It stays
    /// on the Run button's tooltip, which is where it answers "why is this disabled".
    /// </remarks>
    [Fact]
    public void TheRunBlockedReasonIsNotRepeatedBesideTheStatusStrip()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        Assert.Equal(1, Regex.Matches(xaml, @"\{Binding RunBlockedReason\}").Count);
        Assert.Contains(@"ToolTip.Tip=""{Binding RunBlockedReason}""", xaml, StringComparison.Ordinal);

        // And the strip really does carry it, so removing the row lost nothing.
        var vm = new RailRfViewModel();
        Assert.False(vm.CanRun);
        Assert.Equal(vm.RunBlockedReason, vm.Refusal?.Sentence);
    }

    /// <summary>A reference extent is offered by NAME, never as its enum member.</summary>
    [Fact]
    public void TheReferenceExtentComboShowsNamesRatherThanEnumMembers()
    {
        Assert.Equal("As imported",
            Converters.RailReferenceExtentNameConverter.Label(RailReferenceExtent.AsImported));

        // The two optimistic ones say so on their own face, which is the point of the list.
        Assert.Contains("optimistic",
            Converters.RailReferenceExtentNameConverter.Label(RailReferenceExtent.FilledToOutline),
            StringComparison.Ordinal);

        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");
        Assert.Contains("RailReferenceExtentNameConverter.Instance", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two Settings fields that are legitimately EMPTY say what empty means.
    /// </summary>
    /// <remarks>
    /// An empty Mesh cell is not a missing value — the extractor computes one from the artwork and a
    /// number here overrides it — and an empty Via plating means the stackup's own via entry is read
    /// instead. Both read as blanks somebody forgot to fill in; a watermark is what tells them apart
    /// from a field waiting for input.
    /// </remarks>
    [Fact]
    public void TheSettingsFieldsThatAreLegitimatelyEmptySayWhatEmptyMeans()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        foreach (string binding in new[] { "MeshCellEntry", "ViaPlatingEntry" })
        {
            var m = Regex.Match(xaml,
                @"<ctl:InlineEditText[^>]*?\{Binding " + binding + @",[^>]*?>", RegexOptions.Singleline);
            Assert.True(m.Success, $"{binding} is no longer bound to an InlineEditText.");
            Assert.Contains("Watermark=", m.Value, StringComparison.Ordinal);
        }

        // And empty really is the shipped state of the mesh cell, which is what makes the watermark
        // the ordinary thing a user sees rather than an edge case.
        Assert.Equal("", new RailRfViewModel().MeshCellEntry);
    }

    /// <summary>Help opens the railRF chapter rather than being a dimmed button.</summary>
    [Fact]
    public void TheHelpButtonOpensTheRailRfChapter()
    {
        Assert.Contains("DocLauncher.Open(\"reference/railrf.html\")",
            Src("src/Ui/Views/RailRf/RailRfWindow.axaml.cs"), StringComparison.Ordinal);

        string page = Path.Combine(RepoRoot(), "docs", "user", "reference", "railrf.html");
        Assert.True(File.Exists(page), "Help points at a page that is not built.");
    }

    /// <summary>
    /// <b>Open is not a second import.</b> It resolves an existing document or layout and holds no
    /// import of its own — the rule <c>Authoring.cs</c> states, on the window side.
    /// </summary>
    [Fact]
    public void TheOpenButtonResolvesRatherThanImports()
    {
        string src = Src("src/Ui/Views/RailRf/RailRfWindow.Open.cs");

        Assert.DoesNotContain("GerberImport", src, StringComparison.Ordinal);
        Assert.Contains("TechnologyResolver.ResolveForDocument", src, StringComparison.Ordinal);
        Assert.Contains("RailDocumentIo.LoadFromFile", src, StringComparison.Ordinal);

        // Left of Import on the title bar, which is where the owner asked for it.
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");
        Assert.InRange(xaml.IndexOf(@"Name=""OpenButton""", StringComparison.Ordinal),
                       0, xaml.IndexOf(@"Name=""ImportButton""", StringComparison.Ordinal));
    }

    /// <summary>A label in a label/value row keeps a gap between it and the control beside it.</summary>
    /// <remarks>
    /// The gap is on the LABEL, not on the control: padding on a ComboBox moves its own text in from
    /// its border and leaves the border exactly where it was, which is the edge the label was
    /// touching. And it is a class of its own rather than a margin on <c>detailLabel</c>, because that
    /// class also fills the parts table's seven columns.
    /// </remarks>
    [Fact]
    public void ALabelBesideAControlKeepsAGapFromIt()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        Assert.Equal("0,0,8,0", SetterIn(xaml, IndexOfStyle(xaml, "TextBlock.rowlbl"), "Margin"));
        Assert.Null(SetterIn(xaml, IndexOfStyle(xaml, "TextBlock.detailLabel"), "Margin"));

        // Every combo in the window has one, since that is what the report was about.
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(xaml, @"<ComboBox\s+Grid\.Column=""1""", RegexOptions.None))
        {
            int row = xaml.LastIndexOf("<Grid ColumnDefinitions=\"Auto,*\">", m.Index, StringComparison.Ordinal);
            Assert.True(row >= 0, "A combo in this window is no longer in a label/value row.");
            Assert.Contains("detailLabel rowlbl", xaml[row..m.Index], StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
