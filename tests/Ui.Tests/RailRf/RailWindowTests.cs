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
using CircuitRF.Engine.Pdn;
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
    /// <b>Every edit that changes the ANSWER re-solves — including the four that did not.</b>
    /// </summary>
    /// <remarks>
    /// §2.3 step 6 is "the result follows the edit", and four edits did not follow: the flat
    /// impedance target (owner, 2026-09-19), the two band ends, and adding or removing an aggressor.
    /// All four are drawn or judged off the sweep RESULT — the target is the mask trace on the |Z|
    /// plot, the band is that plot's own X axis, and the aggressor lines and every coincidence row
    /// come out of the result too — so each one left the picture describing a document the field no
    /// longer showed, which is worse than a control that visibly does nothing.
    ///
    /// <para>Written as one test over the four because the claim is one claim. The gate is
    /// <c>SolvesStarted</c> rather than a rendered curve: what went wrong was that the loop was
    /// never entered.</para>
    /// </remarks>
    [Fact]
    public void EveryEditThatChangesTheAnswerReSolves_IncludingTheTargetTheBandAndTheAggressors()
    {
        var vm = Ready(out _);

        void Edits(string what, Action edit)
        {
            int before = vm.SolvesStarted;
            edit();
            Assert.True(vm.SolvesStarted > before, $"{what} did not re-solve.");
        }

        Edits("the impedance target", () => vm.ImpedanceTargetEntry = "50 mohm");
        Assert.Equal(50.0, vm.SelectedRail!.ImpedanceTarget!.FlatMilliohms!.Value, 6);

        Edits("the band start", () => vm.BandStartEntry = "10 kHz");
        Edits("the band stop",  () => vm.BandStopEntry  = "100 MHz");
        Assert.Equal(1e4, vm.SelectedRail.Band.StartHz, 3);
        Assert.Equal(1e8, vm.SelectedRail.Band.StopHz, 3);

        Edits("adding an aggressor", () => vm.AddAggressorCommand.Execute(null));
        Assert.Single(vm.Aggressors);

        Edits("removing an aggressor", () => vm.RemoveAggressorCommand.Execute(vm.Aggressors[0]));
        Assert.Empty(vm.Aggressors);

        // The drop budget beside them always did, and still does — the behaviour the other four
        // were brought into line with.
        Edits("the drop budget", () => vm.DropBudgetEntry = "80 mV");
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

    /// <summary>
    /// Pressing Accuracy while it is showing the accurate reading puts the Fast one back, without
    /// re-solving anything.
    /// </summary>
    /// <remarks>
    /// Reported by the owner on 2026-09-19: once Accuracy is on there is no way to turn it off.
    /// The lamp going out on the next edit is §2.9's "never left silently" and it is not an answer to
    /// a press. Both readings are already in hand, so this is a SWITCH and not a run — which is what
    /// the solve count asserts: leaving the mesh must not cost another solve of anything.
    /// </remarks>
    [Fact]
    public void PressingAccuracyWhileItIsLit_GoesBackToTheFastReading_WithoutSolvingAgain()
    {
        var vm = Ready(out _);

        vm.RunCommand.Execute(null);
        vm.AccuracyCommand.Execute(null);
        Assert.True(vm.IsShowingAccuracy);

        int solves = vm.SolvesStarted;
        vm.AccuracyCommand.Execute(null);

        Assert.False(vm.IsShowingAccuracy);
        Assert.Equal(PdnModelKind.Fast, vm.ResultsModelKind);
        Assert.Equal(solves, vm.SolvesStarted);

        // And the mesh answer is kept, so pressing it again is a switch back rather than a re-run.
        Assert.True(vm.ByModel.ContainsKey(PdnModelKind.Accurate));
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

    /// <summary>
    /// <b>The electrical columns carry the library's NUMBERS, not a word for where they came from</b>
    /// (owner, 2026-09-19: the ESR column says "stated" — why not list the mΩ from the file?).
    /// </summary>
    /// <remarks>
    /// One test over the whole column set, because they have one cause: the row read the library
    /// ROW — which can only answer "is there an ESR" — instead of the <c>RailPartModel</c>
    /// <c>RailPartResolver</c> produces, which R-rail11-6 already says is what the parts table's
    /// rows read. So the ESR column named a provenance, the derated column read <i>unresolved</i>
    /// for every part of every document although the shipped library carries bias curves, and the
    /// three numbers a PDN reader actually works from — the ohms, the mounted resonance and the
    /// package inductance — were nowhere on the table at all.
    ///
    /// <para>The arithmetic is checked rather than just the presence of a number: 100 nF with the
    /// row's 20 MHz gives 633 pH of package inductance, which with the rail's 0.85 nH of mounting
    /// puts the MOUNTED resonance at 13.3 MHz — below the row's own 20 MHz, which is the whole
    /// reason <c>RailPartModel</c> refuses to re-print the row's figure.</para>
    /// </remarks>
    [Fact]
    public void ThePartsTableCarriesTheLibrarysNumbers_NotJustTheirProvenance()
    {
        var doc = OneRail();
        var rail = doc.Rails[0];
        rail.Parts.Clear();
        rail.Parts.Add(new RailPart
        {
            Refdes = "C1", PartNumber = "CAP-100N-0402", MountingInductanceHenries = 0.85e-9,
        });

        var library = LibraryKnowing("CAP-100N-0402");
        library.Rows[0].EsrOhms = 0.032;                       // 32 mΩ, as the shipped library states

        var vm = Window(doc);
        vm.PartLibrary = library;

        var c1 = Assert.Single(vm.Parts);

        // The ESR is the OHMS, and the basis is still said — in the tooltip, where the table has room
        // for the sentence Q-15 requires rather than one word.
        Assert.Equal("32 mΩ", c1.EsrText);
        Assert.Equal("stated", c1.EsrBasisText);
        Assert.False(c1.IsEsrIndicative);

        // L = 1/((2π·20 MHz)²·100 nF) = 633 pH, and the branch carries it plus the 0.85 nH mounting
        // loop — the two terms of one sum, in the total's own unit.
        Assert.Equal("0.633 + 0.85 nH", c1.InductanceText);

        // 1/(2π·√(1.483 nH · 100 nF)) = 13.1 MHz. NOT the row's stated 20 MHz: that is the part on
        // its own, and this one is mounted.
        Assert.Equal("13.1 MHz", c1.SelfResonanceText);

        // No bias curve on this row, so nothing derated it and the cell says so by carrying no
        // arrow — a "100 nF → 100 nF" would claim a curve had been applied.
        Assert.Equal("100 nF", c1.CapacitanceText);
        Assert.Equal(RailPartRowViewModel.UnresolvedText, c1.DeratedText);

        // With a curve, both numbers are on the row: 100 nF marked, 74 nF at the rail's own voltage.
        library.Rows[0].BiasCurve.Add(new PartBiasPoint(0.0, 100e-9));
        library.Rows[0].BiasCurve.Add(new PartBiasPoint(rail.NominalVoltageV ?? 3.3, 74e-9));
        vm.RebuildParts();

        c1 = Assert.Single(vm.Parts);
        Assert.Equal("100 nF → 74 nF", c1.CapacitanceText);
        Assert.Equal("74 nF", c1.DeratedText);
        Assert.Equal("derated", c1.ValueUsedText);
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

        // And the `.ctech` it resolved is CARRIED, not just its contents — the board panel offers to
        // OPEN it (owner, 2026-09-19), and the path is resolved in three places that can each forget.
        Assert.True(vm.HasTechnologyFile, "the board resolved a technology but not the file it came from.");
        Assert.EndsWith(".ctech", vm.TechnologyPath!, StringComparison.OrdinalIgnoreCase);

        // And the README's next word is "press Run" — which is only true if the reference the
        // document already names is one this stackup offers, so no click is charged for a decision
        // somebody made when they saved the file.
        Assert.True(vm.CanRun, vm.RunBlockedReason);
    }

    /// <summary>
    /// <b>Opening a <c>.crail</c> resolves the board netlist and the placement it names, so a REFDES
    /// means something and a mounting loop is read off the artwork.</b>
    /// </summary>
    /// <remarks>
    /// <b>What this closes was total and silent.</b> <c>RailBoardInputs.Pads</c> was assigned nowhere
    /// in <c>src/</c>: the import read a board netlist into the view model and used it only to fill
    /// the net pick list, and opening a document read no netlist at all. So every source and load
    /// anchor had to be a coordinate, and <c>PdnMountingLoopExtractor</c> — the whole of brief 13's
    /// <c>L_p + L_r − 2M + L_pad</c> — answered "the board netlist has no pad for it" for every part
    /// on every board. Neither failed; both degraded to the typed path, which is the path a document
    /// with no netlist takes, so nothing on any report said the netlist had been read and dropped.
    ///
    /// <para>Driven on the SHIPPED example for <see cref="OpeningACrailLoadsTheArtworkAndPartLibraryItNames"/>'s
    /// reason, and asserting the BASIS rather than a number: what is under test is that the value
    /// came off the geometry, not that this board's C1 is any particular size.</para>
    /// </remarks>
    [Fact]
    public void OpeningACrailResolvesItsNetlistSoMountingLoopsAreReadOffTheArtwork()
    {
        string crail = Path.Combine(RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.Empty(vm.LoadDocumentReferences());

        // The netlist resolved and became pads. Two per capacitor plus the three ports.
        Assert.NotEmpty(vm.Board!.Pads);
        Assert.Equal("GND", vm.Board!.ReferenceNet);
        Assert.Contains(vm.Board!.Pads, p => p.Refdes == "C1" && p.Net == "+3V3");
        Assert.Contains(vm.Board!.Pads, p => p.Refdes == "C1" && p.Net == "GND");

        // And the placement resolved, which is what the parts table's Position column reads.
        Assert.NotNull(vm.Placement);
        Assert.Null(vm.Placement!.Refusal);

        var rail = vm.Document.Rails[0];
        var loops = PdnMountingLoopExtractor.ComputeAll(
            new PdnMountingLoopRequest
            {
                Rail         = rail,
                Shapes       = vm.Board!.Shapes,
                Technology   = vm.Board!.Technology,
                DbuPerMicron = vm.Board!.DbuPerMicron,
                Pads         = vm.Board!.Pads,
                ReferenceNet = vm.Board!.ReferenceNet,
            },
            rail.ShuntParts.Select(p => p.Refdes));

        // EVERY SHUNT part, not most of them: one unresolved row is a part that silently keeps a
        // typed value, and on a board this example authored deliberately there is no excuse for one.
        //
        // The SERIES element is excluded and that is not a loophole. A mounting loop is the path from
        // a pad through its via to the plane pair and BACK, and a ferrite in the rail has both of its
        // pads on the rail: there is no return half of a loop to compute, the extractor says so by
        // name rather than guessing, and a rail's one series element is priced by its own DCR and its
        // own impedance over frequency instead.
        Assert.All(loops, l => Assert.Null(l.Unresolved));
        Assert.All(loops, l => Assert.InRange(l.Henries!.Value, 0.3e-9, 5e-9));
        Assert.Equal(rail.Parts.Count - 1, loops.Count);

        // §4.3's lever, which is the whole reason the artwork is worth reading: C11-C13 are the same
        // purchased part as C1-C3 and reach their vias down 0.9 mm of fan-out instead of through the
        // land. If that does not cost them, nothing about computing this from geometry is worth doing.
        double near = loops.First(l => l.Refdes == "C1").Henries!.Value;
        double far  = loops.First(l => l.Refdes == "C11").Henries!.Value;
        Assert.True(far > near * 1.5,
                    $"C11's fan-out should cost it: C1 is {near * 1e12:0.#} pH, C11 {far * 1e12:0.#} pH.");

        // And the resolver PREFERS these over a typed number only because the example states none —
        // a computed value is a default, not a fact (§2.2). The basis is what says which is on show.
        // Asked of the SHUNT rows, for the reason above: a series element has no mounting loop to
        // compute and therefore no basis to report.
        var models = new RailPartResolver(vm.PartLibrary!)
            .ResolveAll(rail.Parts, rail.NominalVoltageV,
                        loops.ToDictionary(l => l.Refdes, l => l.Henries!.Value));
        Assert.All(models.Models.Where(m => m.Connection == RailPartConnection.Shunt),
                   m => Assert.Equal(RailMountingBasis.ComputedFromGeometry, m.MountingBasis));
    }

    /// <summary>
    /// <b>Picking a part in the table marks it on the board, and Escape clears both.</b>
    /// </summary>
    /// <remarks>
    /// The parts table listed thirteen capacitors beside a picture of the board with no way to find
    /// any of them on it (owner, 2026-09-19). Driven on the shipped example because the link only
    /// exists where the board netlist places the part — on a document that names none, the right
    /// answer is no mark at all, which the last assertion here is.
    /// </remarks>
    [Fact]
    public void SelectingAPartMarksItOnTheBoardAndEscapeClearsIt()
    {
        string crail = Path.Combine(RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        vm.LoadDocumentReferences();
        vm.RebuildParts();

        Assert.Null(vm.PartHighlight);                              // nothing selected is no mark
        Assert.Null(vm.BoardOverlayLayer.PartHighlight);

        vm.SelectedPart = vm.Parts.First(p => p.Refdes == "C11");

        var mark = vm.PartHighlight;
        Assert.NotNull(mark);
        Assert.Equal("C11", mark!.Label);
        Assert.Equal(2, mark.Pads.Count);                           // both pads, never a centroid
        Assert.False(mark.Outline.IsEmpty);

        // It reached the PICTURE, not just the view model — the overlay is what the canvas draws.
        Assert.Equal(mark, vm.BoardOverlayLayer.PartHighlight);

        // The mark is where C11 is, not where some other part is.
        var c11 = vm.Board!.Pads.Where(p => p.Refdes == "C11").ToList();
        Assert.All(c11, p => Assert.True(mark.Outline.Contains(p.X, p.Y)));

        // A rebuild — a solve, a part edit, the placement arriving — keeps the selection by REFDES.
        // Holding the row OBJECT would drop it, because every row here is new on every rebuild.
        vm.RebuildParts();
        Assert.Equal("C11", vm.SelectedPart?.Refdes);
        Assert.NotNull(vm.BoardOverlayLayer.PartHighlight);

        vm.ClearRowSelectionCommand.Execute(null);

        Assert.Null(vm.SelectedPart);
        Assert.Null(vm.PartHighlight);
        Assert.Null(vm.BoardOverlayLayer.PartHighlight);
    }

    /// <summary>
    /// <b>One row is selected in this window at a time, Escape clears whichever it is, and a source
    /// or a load is marked on the board exactly as a part is.</b>
    /// </summary>
    /// <remarks>
    /// Three reports, one cause (owner, 2026-09-19). Each list owned its own selection, so a row
    /// could be highlighted in the parts table AND in the sources list at once — which reads as two
    /// selections when nothing in the window acts on a pair. Escape was wired to the parts table
    /// only, which is the one shape a user cannot diagnose: a key that does nothing looks the same
    /// as a key that is not wired. And a source or a load is ANCHORED — at a refdes and pin — so it
    /// has a place on the board and had no mark on it.
    ///
    /// <para>Driven on the shipped example because the mark only exists where the board netlist
    /// places the anchor.</para>
    /// </remarks>
    [Fact]
    public void OnlyOneRowIsSelectedAtATime_AndASourceOrLoadIsMarkedOnTheBoard()
    {
        string crail = Path.Combine(RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        vm.LoadDocumentReferences();
        vm.RebuildParts();

        vm.SelectedPart = vm.Parts.First(p => p.Refdes == "C11");
        Assert.True(vm.HasRowSelection);

        // Picking a LOAD takes the selection off the parts table — one window, one selected row.
        var load = vm.Loads.First(l => l.Load.Anchor.Refdes is { Length: > 0 });
        vm.SelectedLoad = load;

        Assert.Null(vm.SelectedPart);
        Assert.Same(load, vm.SelectedLoad);

        // And it is marked on the board, on the pads PdnAttachments resolves — the same resolver
        // every port goes through, so a pin FIELD marks the whole field rather than one pad.
        var mark = vm.PartHighlight;
        Assert.NotNull(mark);
        Assert.Equal(load.Anchor, mark!.Label);
        Assert.NotEmpty(mark.Pads);
        Assert.Equal(mark, vm.BoardOverlayLayer.PartHighlight);

        var pads = vm.Board!.Pads.Where(p =>
            string.Equals(p.Refdes, load.Load.Anchor.Refdes, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(mark.Pads, pad => Assert.Contains(pads, p => p.X == pad.X && p.Y == pad.Y));

        // A source next: the load goes, the source arrives, and the mark follows.
        var source = vm.Sources.First(s => s.Source.Anchor.Refdes is { Length: > 0 });
        vm.SelectedSource = source;

        Assert.Null(vm.SelectedLoad);
        Assert.Equal(source.Anchor, vm.PartHighlight?.Label);

        // An aggressor is a FREQUENCY, not a place — selecting one takes the mark off rather than
        // inventing somewhere to put it.
        if (vm.Aggressors.Count > 0)
        {
            vm.SelectedAggressor = vm.Aggressors[0];
            Assert.Null(vm.SelectedSource);
            Assert.Null(vm.PartHighlight);
            Assert.Null(vm.BoardOverlayLayer.PartHighlight);
        }

        // Escape clears whichever list holds it — all four through one command, which is what the
        // window's key handler calls.
        vm.SelectedSource = source;
        Assert.True(vm.HasRowSelection);

        vm.ClearRowSelectionCommand.Execute(null);

        Assert.False(vm.HasRowSelection);
        Assert.Null(vm.SelectedPart);
        Assert.Null(vm.SelectedSource);
        Assert.Null(vm.SelectedLoad);
        Assert.Null(vm.SelectedAggressor);
        Assert.Null(vm.BoardOverlayLayer.PartHighlight);
    }

    /// <summary>
    /// <b>A rail the window is not showing blocks the run, and the sentence goes when it is fixed.</b>
    /// </summary>
    /// <remarks>
    /// <c>RailDcRun.Run</c> solves EVERY rail of the document and a refusal on any one refuses the
    /// run — so a window that gated on the selected rail alone let a run start, took back
    /// <i>"Rail 'GND' was not solved. Rail 'GND' states no reference layer…"</i>, and then had no way
    /// to see the fix: a refusal raised by a solve is only replaced by another solve (owner,
    /// 2026-09-19). Asked as a GATE, the sentence is re-derived on every refresh and is gone the
    /// moment the rail is given a reference.
    /// </remarks>
    [Fact]
    public void ARailTheWindowIsNotShowingBlocksTheRun_AndItsSentenceGoesWhenItIsFixed()
    {
        var doc = OneRail();
        doc.Rails.Add(new RailSpec { Name = "GND", NetName = "GND" });   // states no reference layer

        var vm = Window(doc);
        vm.Board = Board();
        vm.ConfirmReferenceCommand.Execute(null);          // the SELECTED rail is answered for

        Assert.True(vm.IsReferenceConfirmed);
        Assert.False(vm.CanRun);
        Assert.Contains("GND", vm.RunBlockedReason, StringComparison.Ordinal);
        Assert.Contains("states no reference layer", vm.RunBlockedReason, StringComparison.Ordinal);

        // It names the control that answers it, which is the rail SELECTOR — the sentence's own two
        // remedies both live there, and the reference combo beside it belongs to the rail on screen,
        // whose reference is correctly set (owner, 2026-09-20: it was outlined in the warning colour
        // whatever he did to it, because this refusal used to flag it).
        Assert.Equal(RailRefusalControl.RailSelector, vm.Refusal?.Control);
        Assert.False(vm.IsReferenceLayerFlagged);
        Assert.Contains("rail selector", vm.Refusal!.Sentence, StringComparison.Ordinal);

        // Fix it the way the sentence says to: show that rail and confirm its reference.
        vm.SelectedRailName = "GND";
        vm.ConfirmReferenceCommand.Execute(null);

        Assert.True(vm.CanRun);
        Assert.Null(vm.Refusal);
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

    // ══ Owner round 2, 2026-09-18 — the board panel is a VIEW of the .clay ══════════════════════

    /// <summary>
    /// <b>The board panel draws the layout's OWN model, not a copy of its shapes.</b>
    /// </summary>
    /// <remarks>
    /// This is the whole of the live-view fix: an edit in the layout editor mutates that
    /// <c>LayoutView</c>, which raises its own <c>Changed</c>, which <c>LayoutCanvas</c> is already
    /// subscribed to. Asserted as object identity rather than by driving an edit and looking at
    /// pixels, because identity is the property that makes every later edit live — a test that moved
    /// one shape would pass just as well against a copy that happened to be re-read.
    /// </remarks>
    [Fact]
    public void TheBoardPanelBindsTheLayoutsOwnModelWhenThereIsOne()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var vm = new RailRfViewModel
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };

        Assert.Same(view, vm.BoardLayout!.Model);
    }

    /// <summary>
    /// An artwork edit drops the numbers — and does NOT rebuild the viewport.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they pull opposite ways. A result computed against copper that has since
    /// moved is the one thing this window's status strip exists to prevent; re-fitting the view while
    /// the user is watching it is the tool taking the picture away at the moment they are using it. So:
    /// the result goes, the <see cref="LayoutEditorViewModel"/> stays the same object.
    /// </remarks>
    [Fact]
    public void AnArtworkEditClearsTheResultAndKeepsTheViewport()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var vm = new RailRfViewModel
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };

        var boundBefore = vm.BoardLayout;
        vm.NotifyArtworkChanged();

        Assert.Same(boundBefore, vm.BoardLayout);
        Assert.Null(vm.Current);
    }

    /// <summary>
    /// <b>The railRF board canvas is a viewer.</b> Every route into the layout's own tools is gated;
    /// navigation and the overlay are not.
    /// </summary>
    /// <remarks>
    /// A source scan rather than a synthesised gesture, and deliberately: what is under test is that no
    /// editing call site was MISSED, which is a property of the file and not of one input. It was worse
    /// than a stray gesture before the gate — <c>LayoutView.Shapes</c> is a list of references, so the
    /// snapshot the panel drew shared every shape object with the real document and a drag here mutated
    /// it from outside its command stack.
    /// </remarks>
    [Fact]
    public void TheBoardCanvasIsReadOnlyAndEveryEditingCallGoesThroughTheGate()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");
        var canvas = Regex.Match(xaml, @"<ctl:LayoutCanvas\s+Name=""BoardCanvas""[^>]*?>", RegexOptions.Singleline);
        Assert.True(canvas.Success);
        Assert.Contains(@"ReadOnly=""True""", canvas.Value, StringComparison.Ordinal);

        // Every call that could CHANGE the document goes through EditTarget, which is null in
        // read-only mode. A new one reaching for _viewModel directly is how this gets broken.
        //
        // The assertion is "not on _viewModel", not "on EditTarget": the same member names exist on
        // ILayoutCanvasOverlay, whose calls are DELIBERATELY still made — railRF's own overlay reads
        // the board out under the cursor and declines every gesture, and gating it would take the
        // readout away as well.
        string src = Src("src/Ui/Controls/LayoutCanvas.cs");
        foreach (string mutator in new[]
                 {
                     "OnPointerPressed(wx", "OnPointerMoved(wx", "OnPointerReleased(wx",
                     "OnKeyDown(e.Key", "DropBitmap(", "CommitDragInstancePlacement(",
                     "SetGripLockArmed(true)", "DeselectAllCommand", "CommitCompanionMove()",
                 })
            foreach (System.Text.RegularExpressions.Match m in
                     Regex.Matches(src, @"_viewModel[?!]?\." + Regex.Escape(mutator)))
                Assert.Fail($"LayoutCanvas calls {m.Value} on _viewModel rather than through "
                          + "EditTarget, so it still runs on a read-only canvas.");
    }

    /// <summary>
    /// <b>A menu accelerator acts on the window in front.</b>
    /// </summary>
    /// <remarks>
    /// <c>Edit ▸ Undo</c> is a <c>NativeMenuItem</c> with <c>Gesture="Meta+Z"</c>, and on macOS that is
    /// an APPLICATION key equivalent. railRF is shown unowned and carries no menu of its own, so ⌘Z
    /// pressed there was undoing whatever the workspace's active document had last done — an edit in a
    /// window the user was not looking at, with nothing on screen saying so.
    /// </remarks>
    [Fact]
    public void UndoAndRedoActOnlyWhenTheWorkspaceWindowIsInFront()
    {
        string src = Src("src/Ui/ViewModels/WorkspaceViewModel.cs");

        foreach (string command in new[] { "UndoLast()", "RedoLast()" })
        {
            var m = Regex.Match(src, @"private void (?:Undo|Redo)\(\)[^
]*" + Regex.Escape(command));
            Assert.True(m.Success, $"{command} is no longer reached from a one-line command body.");
            Assert.Contains("IsShellWindowActive()", m.Value, StringComparison.Ordinal);
        }

        // And the unowned tool windows are the reason: the guard has to be able to SEE one, which a
        // workspace-windows-only lookup cannot.
        Assert.Contains("public static Window? ActiveWindow()",
            Read("src/Ui/Views/WorkspaceLocator.cs"), StringComparison.Ordinal);
    }

    // ══ Owner round 2 — DBU is a storage unit, not a reading unit ═══════════════════════════════

    /// <summary>
    /// A coordinate anchor reads in the BOARD's units, and says "DBU" only when nothing stated one.
    /// </summary>
    [Fact]
    public void ACoordinateAnchorReadsInTheBoardsOwnUnits()
    {
        var anchor = new RailPortAnchor { Point = (26_500_000, 9_875_000) };

        Assert.Equal("(26.5, 9.875) mm", anchor.Describe(new RailLengthFormat(LayoutUnit.Mm, 1000)));
        Assert.Equal("(26500, 9875) µm", anchor.Describe(new RailLengthFormat(LayoutUnit.Um, 1000)));

        // No artwork, no unit — and it says so rather than picking one. A number printed in a unit
        // nobody stated is the trap every unit note in this repository is about.
        Assert.Equal("(26500000, 9875000) DBU", anchor.Describe());
    }

    /// <summary>
    /// The window's own rows, the parts placement column and the mesh cell all read in board units.
    /// </summary>
    /// <remarks>
    /// Driven through the view model rather than scanned, because the claim is about what a user sees
    /// and each of these three reaches the format by a different route: a row holds a FUNCTION, the
    /// parts column calls it per rebuild, and the mesh cell both formats and PARSES through it.
    /// </remarks>
    [Fact]
    public void TheWindowsCoordinatesAndMeshCellReadInBoardUnits()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var document = new RailDocument();
        var rail = new RailSpec { Name = "+3V3", NetName = "+3V3" };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (26_500_000, 9_875_000) } });
        document.Rails.Add(rail);

        var vm = new RailRfViewModel(document, null)
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };

        Assert.Equal("(26.5, 9.875) mm", Assert.Single(vm.Loads).Anchor);

        // The mesh cell round-trips through the same unit, and an explicit suffix still overrides.
        vm.MeshCellEntry = "0.2";
        Assert.Equal(0.2e-3, vm.MeshCellMetres!.Value, 12);
        Assert.Equal("0.2 mm", vm.MeshCellEntry);

        vm.MeshCellEntry = "50 µm";
        Assert.Equal(50e-6, vm.MeshCellMetres!.Value, 12);
    }

    /// <summary>
    /// Changing the board's display unit re-states the strings, on the activation that follows.
    /// </summary>
    /// <remarks>
    /// A display unit raises no <c>Changed</c> event — the layout editor deliberately keeps it off the
    /// undo stack and out of the notification the spatial index listens to — so there is nothing to
    /// subscribe to and the window asks on activation instead. What is asserted here is the asking.
    /// </remarks>
    [Fact]
    public void ChangingTheBoardsDisplayUnitRestatesTheCoordinates()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var document = new RailDocument();
        var rail = new RailSpec { Name = "+3V3", NetName = "+3V3" };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (26_500_000, 9_875_000) } });
        document.Rails.Add(rail);

        var vm = new RailRfViewModel(document, null)
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };
        var row = Assert.Single(vm.Loads);
        Assert.Equal("(26.5, 9.875) mm", row.Anchor);

        // The layout editor's unit picker writes straight to the model.
        view.DisplayUnit = LayoutUnit.Um;

        int restated = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.Anchor)) restated++; };
        vm.RefreshIfUnitChanged();

        Assert.Equal(1, restated);
        Assert.Equal("(26500, 9875) µm", row.Anchor);
    }

    /// <summary>
    /// Removing an aggressor takes away <b>the row that was selected</b>, with duplicates present.
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19: "+" adds one and "−" does not take it away. <c>RailAggressor</c> is a record
    /// and the add button makes identical ones, so <c>List.Remove</c> took the FIRST equal row —
    /// leaving the selected row on screen, untouched, which is a button that does nothing as far as
    /// anyone watching it is concerned. Two unedited rows is the whole fixture; one row could never
    /// have shown it.
    /// </remarks>
    [Fact]
    public void RemovingAnAggressorTakesTheSelectedRow_EvenWhenTheRowsAreIdentical()
    {
        var vm = Window(OneRail());

        vm.AddAggressorCommand.Execute(null);
        vm.AddAggressorCommand.Execute(null);
        Assert.Equal(2, vm.Aggressors.Count);

        // Tell them apart by what the DOCUMENT holds, after editing the second one only.
        vm.Aggressors[1].Name = "second";
        Assert.Equal("new",    vm.SelectedRail!.Aggressors[0].Name);
        Assert.Equal("second", vm.SelectedRail!.Aggressors[1].Name);

        vm.RemoveAggressorCommand.Execute(vm.Aggressors[1]);

        var left = Assert.Single(vm.SelectedRail!.Aggressors);
        Assert.Equal("new", left.Name);

        // And with nothing selected the button is DISABLED rather than silently inert.
        Assert.False(vm.RemoveAggressorCommand.CanExecute(null));
        Assert.True(vm.RemoveAggressorCommand.CanExecute(vm.Aggressors[0]));
    }

    /// <summary>
    /// Adopting a re-resolved technology <b>keeps the canvas</b> and drops the numbers.
    /// </summary>
    /// <remarks>
    /// The owner turned a layer's <c>Vis</c> off in the <c>.ctech</c> and railRF went on drawing it:
    /// this window held the instance it resolved when the board was opened and nothing replaced it.
    /// The two halves asserted here are the ones that are easy to get wrong in opposite directions —
    /// the drawing has to follow, and the VIEWPORT must not be thrown away doing it (assigning
    /// <c>Board</c> would rebuild the <c>LayoutEditorViewModel</c> and with it the pan and zoom the
    /// user is looking at).
    /// </remarks>
    [Fact]
    public void AdoptingAReResolvedTechnologyKeepsTheCanvasAndDropsTheNumbers()
    {
        var vm = Ready(out _);
        vm.RunCommand.Execute(null);
        Assert.NotEmpty(vm.ByModel);

        var canvas = vm.BoardLayout;
        Assert.NotNull(canvas);

        var replacement = new Technology();
        vm.AdoptTechnology(replacement);

        Assert.Same(canvas, vm.BoardLayout);                 // the viewport survived
        Assert.Same(replacement, canvas!.Technology);        // and the drawing follows
        Assert.Same(replacement, vm.Board!.Technology);      // as does the next run's stackup
        Assert.Empty(vm.ByModel);                            // measured against the old one
    }

    /// <summary>
    /// Turning a layer's visibility off <b>keeps the numbers</b> — and takes the layer off the map.
    /// </summary>
    /// <remarks>
    /// The other half of the rule above, and the half the owner's actual workflow needs: a <c>Vis</c>
    /// box is a question about the PICTURE, so invalidating a solved rail for it would make every
    /// toggle cost a re-run. What decides it is the STACKUP — thicknesses, conductivities, dielectrics
    /// and the drawing layers each entry claims — and visibility is not in it.
    /// </remarks>
    [Fact]
    public void HidingALayerKeepsTheResultAndTakesTheLayerOffTheMap()
    {
        var vm = Ready(out _);
        vm.RunCommand.Execute(null);
        Assert.NotEmpty(vm.ByModel);

        var hidden = TechWithGround();
        hidden.Layers[0].Visible = false;

        vm.AdoptTechnology(hidden);

        Assert.NotEmpty(vm.ByModel);                         // nothing about the copper changed
        Assert.Contains(new LayerKey(1, 0), vm.BoardOverlayLayer.HiddenLayers);
        Assert.DoesNotContain(new LayerKey(2, 0), vm.BoardOverlayLayer.HiddenLayers);
    }

    /// <summary>
    /// The FREQUENCY answer names its ports in the board's units too — <b>"Against the target" was
    /// the one readout still printing DBU</b> (owner, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// The DC request passed the board's <c>LengthFormat</c> and the sweep request did not, so it took
    /// <c>PdnSweepRequest</c>'s own default and every port the sweep named — the mask verdict's rows,
    /// the plot's trace labels — printed a coordinate anchor as a bare database integer. Two halves of
    /// one window disagreeing about one port.
    /// </remarks>
    [Fact]
    public void TheFrequencyRequestCarriesTheBoardsUnits()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var document = new RailDocument();
        var rail = new RailSpec { Name = "+3V3", NetName = "+3V3" };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (26_500_000, 9_875_000) } });
        document.Rails.Add(rail);

        var vm = new RailRfViewModel(document, null)
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };

        var request = vm.BuildSweepRequest(PdnModelKind.Fast);
        Assert.NotNull(request);
        Assert.Equal(LayoutUnit.Mm, request!.LengthFormat.Unit);

        // And a finished row re-states in whatever unit the board is in NOW, because a display unit
        // changes no number in a result — it changes how one is spelled.
        var port = new PdnPortImpedance(
            0, rail.Loads[0].Anchor.Describe(request.LengthFormat), rail.Loads[0].Anchor,
            [], null, PdnMask.Judge(null, [], [], false), [], []);

        Assert.Equal("(26.5, 9.875) mm", port.Name);
        Assert.Equal("(26500, 9875) µm", port.NameIn(new RailLengthFormat(LayoutUnit.Um, 1000)));
    }

    /// <summary>
    /// A display-unit change re-states the window <b>while it is on screen</b>, with no activation.
    /// </summary>
    /// <remarks>
    /// The unit deliberately stays off <c>LayoutView.Changed</c> — it is a preference, not geometry —
    /// but it is shared state on a shared model, and railRF's board panel is a second window drawing
    /// it. <c>DisplayUnitChanged</c> is that notification, and this drives the pair the way the window
    /// wires them: the model raises, the view model re-states (owner, 2026-09-19).
    /// </remarks>
    [Fact]
    public void ADisplayUnitChangeRestatesTheWindowWithoutWaitingForAnActivation()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mm };
        var document = new RailDocument();
        var rail = new RailSpec { Name = "+3V3", NetName = "+3V3" };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (26_500_000, 9_875_000) } });
        document.Rails.Add(rail);

        var vm = new RailRfViewModel(document, null)
        {
            Board = new RailBoardInputs
            {
                Shapes = view.Shapes, Technology = new Technology(), View = view,
            },
        };

        // What RailRfWindow.WatchArtwork subscribes.
        int raised = 0;
        view.DisplayUnitChanged += (_, _) => { raised++; vm.RefreshIfUnitChanged(); };

        var row = Assert.Single(vm.Loads);
        Assert.Equal("(26.5, 9.875) mm", row.Anchor);

        view.DisplayUnit = LayoutUnit.Um;
        Assert.Equal(1, raised);
        Assert.Equal("(26500, 9875) µm", row.Anchor);

        // Setting it to what it already is is not a change, so nothing is re-stated for it.
        view.DisplayUnit = LayoutUnit.Um;
        Assert.Equal(1, raised);
    }

    // ══ The window's own reports, 2026-09-19 ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>Opening a <c>.crail</c> fills the pick list, and the "no netlist named any nets" note is
    /// therefore NOT shown.</b>
    /// </summary>
    /// <remarks>
    /// The reported shape, on the shipped Power Rail example: the specification column said "No
    /// board netlist named any nets, so pick the rail by clicking its pour on the board" over a
    /// document whose <c>.ipc</c> names <c>+3V3</c> and <c>GND</c> — and clicking the pour did
    /// nothing either, because that route had never been wired. <see cref="AvailableNets"/> was
    /// rebuilt only by the IMPORT path, so every opened document reported having no nets.
    ///
    /// <para>The example is the fixture deliberately: it is the document the report is about, it
    /// ships, and a synthetic netlist would not have caught this (the defect is in which code path
    /// resolves the file, not in the file).</para>
    /// </remarks>
    [Fact]
    public void OpeningADocumentWhoseNetlistNamesNets_FillsThePickList()
    {
        string path = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(path), $"The shipped example moved: {path}");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
        vm.LoadDocumentReferences();

        Assert.Contains(vm.AvailableNets, r => r.Name == "+3V3");
        Assert.Contains(vm.AvailableNets, r => r.Name == "GND");
        Assert.True(vm.HasPickableNets);

        // The other half, and it is the half the user actually saw.
        Assert.False(vm.HasNoPickableNets);

        // AND THE GESTURE IS STILL THERE. It used to be armed only while HasNoPickableNets, so that
        // it could not come apart from the note — which is how the two came apart in the first
        // place: the note was shown on every opened document and the click it names was never wired
        // at all. brief-authored-board-2 R-ab2-4c states the gesture outright instead: it is the
        // gesture for THIS COPPER HERE, and a board that names its nets does not make it redundant
        // — a drawn board resolving its nets from a schematic would otherwise have LOST the click
        // that used to work on it.
        //
        // What changed on 2026-09-20 is WHEN it is live: R-ab2-4c armed it on every board, and a
        // bare left click — the gesture for LOOKING at a board — therefore edited the document.
        // The gesture is the same and it is now armed by the button beside the rail selector. See
        // PourPickArmingTests.
        Assert.True(vm.CanPickFromBoard);
        Assert.Null(vm.BoardOverlayLayer.PourPick);
    }

    /// <summary>
    /// <b>The pick button is dead until a net is highlighted, and it says which of its two things
    /// it will do.</b>
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19: <i>"I press it and nothing happens."</i> Both halves of that are real.
    /// The command was enabled with nothing selected and returned immediately, and on a net this
    /// document ALREADY carries as a rail — which is the state the shipped example opens in —
    /// <c>PickRail</c> selects the existing rail rather than adding a second one, so the press
    /// moved a selector that was already where it was going.
    /// </remarks>
    [Fact]
    public void ThePickButtonIsGatedOnASelection_AndSaysWhichOfItsTwoThingsItWillDo()
    {
        string path = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
        vm.LoadDocumentReferences();

        Assert.False(vm.PickSelectedNetCommand.CanExecute(null));

        // A net this document ALREADY carries: the button says it will show it, not add a second.
        vm.SelectNet("+3V3");
        Assert.True(vm.PickSelectedNetCommand.CanExecute(null));
        Assert.Equal("Show this rail", vm.PickRailButtonText);

        vm.PickSelectedNetCommand.Execute(null);
        Assert.Equal("+3V3", vm.SelectedRailName);
        Assert.Single(vm.Rails);

        // Take the rail away (R-rail19-1a) and the same row offers to make it one again. The other
        // net on this board is the reference return, which R-rail19-1d refuses on purpose — see
        // RailRemovalTests — so the "Make it a rail" face is reached this way rather than on GND.
        vm.RemoveRailCommand.Execute(null);
        Assert.Empty(vm.Rails);
        Assert.Equal("Make it a rail", vm.PickRailButtonText);

        vm.PickSelectedNetCommand.Execute(null);
        Assert.Contains("+3V3", vm.Rails);
        Assert.Equal("Show this rail", vm.PickRailButtonText);
    }

    /// <summary>
    /// <b>The window renders the marker info boxes it creates.</b>
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19: adding a marker on the results plot showed no info box. The providers
    /// were all wired the day before — so <c>DataDisplayViewModel</c> really did build a
    /// <c>MarkerInfoBoxViewModel</c> — and nothing in the AXAML rendered
    /// <c>PlotHost.MarkerInfoBoxes</c>, so the box existed and had nowhere to be drawn. This is the
    /// same omission the Match Designer's own overlay comment records, one layer further out.
    ///
    /// <para>The second half is the container sync: without it the container keeps
    /// <c>BuildPlotHost</c>'s seed rectangle at the origin, and a box is placed in the top left of
    /// the WINDOW rather than beside its marker.</para>
    /// </remarks>
    [Fact]
    public void TheWindowRendersTheMarkerInfoBoxesItCreates()
    {
        string xaml = Read("src/Ui/Views/RailRf/RailRfWindow.axaml");

        Assert.Contains("PlotHost.MarkerInfoBoxes", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkerInfoBoxView", xaml, StringComparison.Ordinal);

        // Placed on a Canvas at the view model's own coordinates — anything else ignores a drag.
        Assert.Contains("Canvas.Left", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewLeft", xaml, StringComparison.Ordinal);

        // And the container's rectangle is kept equal to the PlotControl's, or every box lands at
        // the window's origin.
        string code = Src("src/Ui/Views/RailRf/RailRfWindow.axaml.cs");
        Assert.Contains("NotifyViewProperties", code, StringComparison.Ordinal);
        Assert.Contains("LayoutUpdated", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An anchor is typed on the row that prints it, in both of its forms.</b>
    /// </summary>
    /// <remarks>
    /// The reported shape: "+" made a source row reading <c>(no anchor)</c> and there was no
    /// control anywhere in the window that named a refdes or a pin, so the row could never be
    /// given one. What is asserted here is the round trip — a coordinate anchor has to come back
    /// out of the field it was printed into, IN THE BOARD'S UNIT, or a user who opens the editor
    /// and presses Return has silently moved the port.
    /// </remarks>
    [Fact]
    public void AnAnchorIsTypedOnTheRow_InBothOfItsForms()
    {
        var format = new RailLengthFormat(LayoutUnit.Mm, 1000);

        var pad = RailAnchorEntry.Parse("U1.VDD", format);
        Assert.Equal("U1", pad!.Refdes);
        Assert.Equal("VDD", pad.Pin);
        Assert.Null(pad.Point);

        // A part with one pin needs no pin, which is RailPortAnchor's own rule.
        Assert.Equal("BT1", RailAnchorEntry.Parse("BT1", format)!.Refdes);

        // THE ROUND TRIP. What Describe printed is what Parse reads back, to the DBU.
        var point = new RailPortAnchor { Point = (26_500_000, 9_875_000) };
        string printed = RailAnchorEntry.Text(point, format);
        Assert.Equal("(26.5, 9.875) mm", printed);
        Assert.Equal((26_500_000L, 9_875_000L), RailAnchorEntry.Parse(printed, format)!.Point);

        // Clearing unanchors rather than being refused — the row then flags, which is the state
        // the "+" button creates and one a user has to be able to get back to.
        var cleared = RailAnchorEntry.Parse("", format);
        Assert.NotNull(cleared);
        Assert.False(cleared!.IsPad);
        Assert.Null(cleared.Point);

        // And nonsense is REJECTED rather than stored as a refdes with a comma in it.
        Assert.Null(RailAnchorEntry.Parse("26.5, over there", format));
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
