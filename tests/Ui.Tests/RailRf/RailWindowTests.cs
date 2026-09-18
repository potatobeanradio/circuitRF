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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
