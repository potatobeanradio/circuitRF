// The Fast edit loop, the Accuracy button, and the one invariant that binds the two
// (brief-railrf-7-window.md R-rail7-4 / R-rail7-5; railrf.md §2.3 step 6, §2.9).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using CircuitRF.Engine.Pdn;
using CircuitRF.Ui.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One result, with the model kind that produced it and what it cost — <b>as ONE value</b>.
/// </summary>
/// <remarks>
/// <b>This record is the invariant.</b> §2.9's first rule is that every result says which model
/// produced it, and R-rail4-2 makes that enforceable by refusing to let a result exist without one.
/// The window's half of the same rule is that the STRIP and the NUMBERS never disagree, even for one
/// frame — so they are not two fields updated in sequence, they are one field assigned once.
/// </remarks>
/// <param name="Kind">Which of §2.9's two readings produced it.</param>
/// <param name="Result">What briefs 3-6 returned.</param>
/// <param name="ElapsedMilliseconds">What it cost — what makes the price of <c>Accuracy</c> obvious
/// BEFORE it is pressed.</param>
public sealed record RailResultView(PdnModelKind Kind, RailDcRunResult Result, double ElapsedMilliseconds)
{
    /// <summary>
    /// The frequency answer computed in the same pass, or null where this document cannot be swept
    /// yet (brief 12).
    ///
    /// <para><b>In the same value as the DC answer for the same reason the model kind is</b>: the
    /// window shows a drop table and an impedance curve side by side, and two fields updated in
    /// sequence would put one run's curve over another run's numbers for a frame. And it is computed
    /// in the same off-thread pass, so a rail with a large decoupling bank does not spend the
    /// removal ranking's own <c>parts × points</c> solves on the UI thread.</para>
    /// </summary>
    public PdnSweepResult? Sweep { get; init; }
}

public sealed partial class RailRfViewModel
{
    // ── The board, and the seams a test drives ────────────────────────────────────────────────

    /// <summary>
    /// The artwork and everything attached to it, or null before an import.
    /// </summary>
    [ObservableProperty]
    private RailBoardInputs? _board;

    /// <summary>True once a board has been imported. Bound rather than a null check, because
    /// Avalonia's <c>!</c> operator needs a bool and a window with no board is an ORDINARY state —
    /// Tools ▸ railRF opens one — which says "import a board" rather than showing an empty canvas.</summary>
    public bool HasBoard => Board is not null;

    partial void OnBoardChanged(RailBoardInputs? value)
    {
        // Every measurement taken off the OLD copper — the flatten, the per-net walks, the measured
        // reference return — is now about a board that is not loaded (R-rail19-2c).
        InvalidateNetWalks();

        // And a pad read settling is of the old board. The new one's pads were read with it, so they
        // are what the next edit is compared against.
        CancelPadRead();
        _netAcrossPadRead = null;
        _padsReadFrom = PinSignature.Of(value?.View);
        OnPropertyChanged(nameof(HasBoard));
        AnnounceImpedanceMap();

        // ── THE SAME SCAR, A THIRD TIME ────────────────────────────────────────────────────────
        //
        // AvailableNets was rebuilt only by AdoptImport, so an OPENED document's pick list was
        // empty; the fix was to rebuild it from the BoardNetlist setter. Brief 2 moves the net names
        // onto the BOARD — a schematic beside the artwork, a stamp on the copper — and a board with
        // no companion netlist never touches that setter, so opening a drawn board would have left
        // the list empty again and printed the same false sentence over it. A derived list follows
        // EVERY write to what derives it.
        RebuildAvailableNets();
        SyncPourPick();
        OnPropertyChanged(nameof(TechnologyPath));
        OnPropertyChanged(nameof(HasTechnologyFile));
        OnPropertyChanged(nameof(EditTechnologyTip));
        ClearResults();
        RebuildBoardLayout();
        RebuildUnclaimedCopperNote();
        RebuildBoardFootprints();
        RebuildReferenceOptions();
        RebuildParts();
        NotifyPartsReadAsTurned();
        TurnPartsProblem = "";
        RefreshRunGate();
        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>
    /// What actually runs a request. <see cref="RailDcRun.Run"/> in the application.
    /// </summary>
    /// <remarks>
    /// <b>Injectable, and only for the two things a test cannot otherwise reach</b>: a solve that
    /// BLOCKS (so a second edit can arrive while the first is in flight — R-rail7-5) and a solve that
    /// refuses with a chosen sentence (so each of the six refusals can be rendered — R-rail7-4).
    /// Everything else about the loop is the real thing.
    /// </remarks>
    internal Func<RailDcRequest, CancellationToken, RailDcRunResult> SolveFunc { get; set; } =
        static (request, _) => RailDcRun.Run(request);

    /// <summary>
    /// How work leaves the UI thread. <c>Task.Run</c> in the application.
    /// </summary>
    internal Func<Func<RailResultView>, CancellationToken, Task<RailResultView>> RunOffThread { get; set; } =
        static (work, token) => Task.Run(work, token);

    /// <summary>
    /// How a finished result gets back to the UI thread.
    /// </summary>
    /// <remarks>
    /// Defaulted to Avalonia's dispatcher and replaced by a test with an inline call, so the loop can
    /// be driven with no application host. The default is set by the WINDOW rather than here, which
    /// keeps this file free of Avalonia exactly as the rest of the view model is.
    /// </remarks>
    internal Action<Action> PostToUi { get; set; } = static a => a();

    // ── What is on screen ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The result the window is SHOWING — one value carrying its own model kind (see
    /// <see cref="RailResultView"/>). Null before anything has been solved.
    /// </summary>
    [ObservableProperty]
    private RailResultView? _current;

    partial void OnCurrentChanged(RailResultView? value)
    {
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(ResultsModelKind));
        OnPropertyChanged(nameof(IsShowingAccuracy));
        OnPropertyChanged(nameof(ModelKindText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(SelectedRailResult));
        OnPropertyChanged(nameof(Breakdown));
        OnPropertyChanged(nameof(Ports));
        OnPropertyChanged(nameof(ViaFlagSummary));
        OnPropertyChanged(nameof(HasBothModels));
        OnPropertyChanged(nameof(CanExport));
        // The MENU items are dimmed by their commands' own CanExecute, which re-asks only when it is
        // told to — see RefreshExportCommands. The toolbar buttons follow the notification above.
        RefreshExportCommands();
        OnPropertyChanged(nameof(ExportBlockedReason));
        OnPropertyChanged(nameof(ExportResults));
        OnPropertyChanged(nameof(PortLines));
        OnPropertyChanged(nameof(BreakdownRows));
        OnPropertyChanged(nameof(BreakdownTotalLine));
        OnPropertyChanged(nameof(BreakdownReconciliation));
        OnPropertyChanged(nameof(HasBreakdownReconciliation));
        OnPropertyChanged(nameof(PlaneCapacitanceLine));
        OnPropertyChanged(nameof(PlaneCapacitanceShort));
        OnPropertyChanged(nameof(HasPlaneCapacitance));
        AnnounceCardVisibility();
        SyncBoardOverlayResult();

        // The |Z| map's readout prints THIS reading's rail impedance beside the plane pair's
        // (R-rail21-1c), and which reading is on screen is exactly what just changed.
        AnnounceImpedanceMap();
    }

    /// <summary>
    /// Every result this board has produced, filed under its own model kind.
    /// </summary>
    /// <remarks>
    /// <b>§2.9's fourth rule: running Accuracy must not displace the Fast result</b>, because the two
    /// are compared on the user's own board rather than trusted from a document. Deliberately NOT
    /// <c>PdnResultsByModel</c> — that class files a <c>PdnExtraction</c> and what a window shows is a
    /// <see cref="RailDcRunResult"/> over the whole rail set, which is a different payload with the
    /// same rule. Cleared whenever the artwork or the document changes, for that class's own stated
    /// reason: a fast curve beside an accurate one from a different board is worse than no comparison.
    /// </remarks>
    public Dictionary<PdnModelKind, RailResultView> ByModel { get; } = [];

    /// <summary>True once both readings of this board are in hand — when the fast curve can actually
    /// be drawn beside the accurate one.</summary>
    public bool HasBothModels =>
        ByModel.ContainsKey(PdnModelKind.Fast) && ByModel.ContainsKey(PdnModelKind.Accurate);

    /// <summary>The model kind of the numbers on screen, or null where there are none.</summary>
    public PdnModelKind? ResultsModelKind => Current?.Kind;

    /// <summary>
    /// True while the numbers on screen ARE the mesh reading — what lights the Accuracy button.
    /// </summary>
    /// <remarks>
    /// <b>This is §2.9 made visible, and it is the whole answer to "how do I know Accuracy is
    /// turned on?"</b> (owner, 2026-09-19). Accuracy is not a mode with an on and an off — it is a
    /// reading, and the honest question is which reading the window is showing. So the button is
    /// lit exactly while <see cref="Current"/> carries the accurate one, and it goes out by itself
    /// the moment an edit puts the Fast answer back on screen (<see cref="QueueResolve"/> runs on
    /// every committed row edit). That is "never left silently" spelled as a lamp rather than as a
    /// sentence at the far end of the status strip.
    ///
    /// <para><b>Pressing it again puts the Fast reading back</b> — reported by the owner on
    /// 2026-09-19 as a button that cannot be turned off again. The lamp going out by itself on the
    /// next edit is correct and is not enough — a user who pressed a lit button and watched it stay
    /// lit has a control that does not answer. §2.9's rule is that the accurate reading is never
    /// entered automatically and never left SILENTLY; a deliberate press is the opposite of silent,
    /// and the mesh answer is kept in <see cref="ByModel"/> either way, so nothing is thrown
    /// away.</para>
    /// </remarks>
    public bool IsShowingAccuracy => Current?.Kind == PdnModelKind.Accurate;

    /// <summary>What the status strip calls it — the SAME value, read off the same field.</summary>
    public string ModelKindText => Current is { } c ? Name(c.Kind) : "no result yet";

    /// <summary>What that result cost, as the strip states it.</summary>
    public string ElapsedText => Current is { } c ? $"{c.ElapsedMilliseconds:0.#} ms" : "";

    /// <summary>The selected rail's own result, or null.</summary>
    public RailDcResult? SelectedRailResult =>
        SelectedRailName is { Length: > 0 } n ? Current?.Result.Rail(n) : null;

    /// <summary>§2.4's ranked breakdown for the selected rail.</summary>
    public IReadOnlyList<PdnBreakdownRow> Breakdown => SelectedRailResult?.Breakdown ?? [];

    /// <summary>Every observation port, including the ones that draw nothing.</summary>
    public IReadOnlyList<RailPortDrop> Ports => SelectedRailResult?.Ports ?? [];

    /// <summary>Every port row as the report prints it — the observation case SAID, never omitted.</summary>
    /// <remarks>
    /// Projected to strings here rather than bound through <c>RailPortDrop.Describe()</c> in the
    /// AXAML: <c>Describe</c> is a METHOD, and a compiled binding cannot call one. Doing it here also
    /// means the window and the headless report print the same sentence, which is the point of
    /// <c>Describe</c> existing on the record at all.
    /// </remarks>
    public IReadOnlyList<string> PortLines =>
        [.. Ports.Select(p => p.Describe(BoardLengthFormat()))];

    /// <summary>
    /// Every breakdown row as the docked list reads it — label, drop, share, and how many netlist
    /// elements it aggregates.
    /// </summary>
    /// <remarks>
    /// The element count is on the row rather than in a tooltip because it is what tells a reader
    /// which SHAPE the row is: one element is a part, thousands are a meshed trace section, and the
    /// arithmetic that produced the drop is different for each (R-rail5-4).
    ///
    /// <para><b>Rows rather than strings since R-rail19-3</b>, because a row that cannot be selected
    /// cannot be located: the list is the answer §2.4 exists to produce and there was no way to find
    /// out where on the board the copper it names actually is. The text is the same text.</para>
    /// </remarks>
    public IReadOnlyList<RailBreakdownRowViewModel> BreakdownRows =>
    [
        .. Breakdown.Select(b => new RailBreakdownRowViewModel(b, BreakdownText(b))),
    ];

    /// <summary>
    /// <b>R-rail21-2a — what the rows add up to, and what that sum IS.</b>
    /// </summary>
    /// <remarks>
    /// The shares are computed against the sum of the rows (<c>PdnBreakdown.Rank</c>, deliberately,
    /// so they add to one exactly whatever the board is), and that sum was nowhere on screen: a
    /// reader who added the millivolts got a number the Drop card contradicted and no statement of
    /// which was which. Printing it NAMED is the whole of the fix — the arithmetic is right and §5
    /// of the brief forbids changing it.
    /// </remarks>
    public string BreakdownTotalLine => PdnBreakdown.TotalLine(Breakdown);

    /// <summary>The port the reconciliation is written against: the one that is furthest below the
    /// source, because that is the reading a budget is judged on.</summary>
    private RailPortDrop? WorstDropPort =>
        Ports.Where(p => p.DropV is not null)
             .OrderByDescending(p => p.DropV!.Value)
             .FirstOrDefault();

    /// <summary>
    /// <b>R-rail21-2b — why the two totals differ, on the boards where they do.</b>
    /// </summary>
    /// <remarks>
    /// <b>The rows' sum is not the drop at a port, and both are right.</b> A row is counted wherever
    /// current flows through it: where the rail divides between parallel paths, every leg is in the
    /// table and the port drops only ONE of them; copper carrying current to a different port is in
    /// the table and is on no path this port sees. Measured on the shipped Power Rail example, the
    /// thirteen rows sum to 50.131 mV against U1.VDD's 48.368 mV, and the 1.763 mV difference is
    /// exactly the second of two parallel legs — each carries part of the 350 mA and each drops
    /// 1.764 mV between the same two nodes (the arithmetic is in <c>src/Design/RESOLVED.md</c>).
    /// Not the reference return, which is in the loop the port voltage is measured across and is
    /// counted once.
    ///
    /// <para><b>Nothing is printed where they agree</b> — the ordinary single-path board. A
    /// reconciliation note about two numbers that match is noise, and noise beside a table is how a
    /// reader learns to stop reading the sentences under it.</para>
    /// </remarks>
    public string BreakdownReconciliation =>
        WorstDropPort is { DropV: { } drop } port
            ? PdnBreakdown.Reconcile(Breakdown, port.Name, drop)
            : "";

    /// <summary>Whether R-rail21-2b has anything to say on this board.</summary>
    public bool HasBreakdownReconciliation => BreakdownReconciliation.Length > 0;

    /// <summary>The one formatter — see <see cref="BreakdownRows"/>.</summary>
    private static string BreakdownText(PdnBreakdownRow b) =>
        $"{b.Label}: {b.DropV * 1e3:0.###} mV ({b.ShareOfTotal:P0})"
      + $" · {RailValueFormat.FormatWithUnit(b.ResistanceOhms, RailQuantity.Resistance)}"
      + $" · {RailValueFormat.FormatWithUnit(b.CurrentA, RailQuantity.Current)}"
      + (b.ElementCount > 1 ? $" · {b.ElementCount} elements" : "");

    /// <summary>
    /// <b>R-rail14-3 / §9 — the extracted plane capacitance, and it is deliberately in TWO places.</b>
    ///
    /// <para><i>"railRF shows the extracted plane capacitance as a single number early and
    /// prominently, because a designer recognises a wrong one instantly and would never notice it
    /// buried in a curve."</i> So it is the first card in the results column and it is on the status
    /// strip, which is always on screen. The sentence is <c>RailDcResult</c>'s own, so the window
    /// and the headless report cannot come to disagree about the same number.</para>
    ///
    /// <para>Empty before a run — there is nothing extracted to report, and a card reading "0 F"
    /// would be a stackup finding about a board nobody has solved.</para>
    /// </summary>
    public string PlaneCapacitanceLine => SelectedRailResult?.PlaneCapacitanceLine ?? "";

    /// <summary>Whether the plane-capacitance card has anything to say yet.</summary>
    public bool HasPlaneCapacitance => PlaneCapacitanceLine.Length > 0;

    /// <summary>The same number, short enough for the status strip — the full sentence is on the
    /// card.</summary>
    public string PlaneCapacitanceShort =>
        SelectedRailResult?.Netlist.Provenance is { PlaneCapacitanceFarads: > 0 } p
            ? $"plane C {RailDcResult.Farads(p.PlaneCapacitanceFarads)}" +
              (p.LossTangentIsClassDefault ? " (tan δ indicative)" : "")
            : "";

    /// <summary>"2 transitions flagged", or the clear case said aloud.</summary>
    public string ViaFlagSummary
    {
        get
        {
            if (SelectedRailResult?.ViaCheck is not { } check) return "";
            int flagged = check.Flags.Count;
            return flagged == 0
                ? $"{check.Transitions.Count} transition(s), none over its limit"
                : $"{flagged} of {check.Transitions.Count} transition(s) flagged";
        }
    }

    private static string Name(PdnModelKind kind) => kind == PdnModelKind.Fast ? "Fast model" : "Accuracy";

    private void ClearResults()
    {
        CancelInFlight();
        ByModel.Clear();
        Current = null;
        ClearSweeps();
        ClearPlane();
    }

    // ── The run gate (R-rail7-8) ──────────────────────────────────────────────────────────────

    /// <summary>True when Run can do something. <b>False until the reference has been affirmatively
    /// set</b>, which is the one place this window deliberately costs the user a click.</summary>
    [ObservableProperty]
    private bool _canRun;

    /// <summary>Why Run is refused, or empty — the tooltip on the disabled button, so the gate never
    /// reads as a broken control.</summary>
    [ObservableProperty]
    private string _runBlockedReason = "";

    /// <summary>
    /// The first rail OTHER than the selected one that states no reference layer, or null.
    /// </summary>
    /// <remarks>
    /// <b>The gate has to look at the whole rail set, because the solve does</b> (owner,
    /// 2026-09-19). <see cref="RailDcRun.Run"/> solves every rail of the document in dependency
    /// order and a refusal on any one of them refuses the run, so a window that checked only the
    /// rail it was SHOWING let a run start, took the engine's "Rail 'GND' was not solved" back, and
    /// then had nowhere to put it: the reference combo it turned red was the selected rail's, which
    /// was correctly set.
    ///
    /// <para>And the sentence then OUTLIVED the fix. A refusal that arrives from a solve is only
    /// replaced by another solve, so a condition the window could not see was a condition it could
    /// not see being repaired either. Asked here, the sentence is re-derived on every gate refresh
    /// and is gone the moment the rail is given a reference — which is what makes it a gate rather
    /// than a message.</para>
    ///
    /// <para>The selected rail is skipped because <see cref="IsReferenceConfirmed"/> answers for it
    /// one branch earlier, and it answers MORE: a rail that names a layer the technology no longer
    /// offers is unconfirmed although it states one.</para>
    /// </remarks>
    private string? UnreferencedRail()
    {
        foreach (var rail in _document.Rails)
        {
            if (string.Equals(rail.Name, SelectedRailName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rail.ReferenceLayer is null) return rail.Name;
        }

        return null;
    }

    /// <summary>
    /// The gate's own refusal, with the control it points at STATED rather than matched.
    /// </summary>
    /// <remarks>
    /// <b>The window knows which control answers its own refusals, and it used to throw that away</b>
    /// (owner, 2026-09-20). Every reason went out through <see cref="RailRefusals.Classify"/>, which
    /// matches the stem of a sentence an ENGINE owns — and on the two reasons this gate raises about
    /// the reference, that got both of them wrong in opposite directions:
    ///
    /// <para><i>"Confirm the reference layer first…"</i> matches no stem, so the one refusal the combo
    /// on screen really does answer turned nothing red at all.</para>
    ///
    /// <para><i>"Rail 'X' states no reference layer…"</i> matches the extractor's stem and turned the
    /// combo red — but that sentence is about a rail the window is NOT showing, and the combo belongs
    /// to the rail it IS showing. So the example opened with the reference correctly set to the plane,
    /// and the control holding the right answer was outlined in the warning colour whatever the user
    /// did to it. Its remedy is in the SELECTOR, which is what the sentence itself says: show that
    /// rail, or remove it.</para>
    ///
    /// <para>Classification is still right for what arrives FROM an extraction — the document's own
    /// refusal, and the import's — because nobody on this side knows what those are about.</para>
    /// </remarks>
    private RailRefusal? GateRefusal()
    {
        if (Board is null)
            return new RailRefusal(
                "There is no board yet. Import one — the artwork, and the placement, BOM and netlist "
              + "that go with it.", RailRefusalControl.None);

        if (SelectedRail is null)
            return new RailRefusal(
                "This document holds no rails yet. Pick the power net on the board to make one.",
                RailRefusalControl.None);

        // BOTH DOORS, exactly as R-rail19-1b's sentence below names both (owner, 2026-09-20). This
        // one named only the first, and the user it is usually shown to did not want that door: the
        // rail was made by a click they did not aim, so "confirm its reference" is an instruction to
        // FINISH the thing they are trying to be rid of. A refusal that names one of two exits traps
        // whoever wanted the other, and this window's Escape and Ctrl+Z were both silent here.
        if (!IsReferenceConfirmed)
            return new RailRefusal(
                SelectedRail is { NetName: null or "" }
                    ? $"'{SelectedRail.Name}' was picked off the board, so railRF knows where it is "
                    + "and not what it is. Confirm its reference layer — proposed and never assumed "
                    + "(Q-8) — or, if this rail was not the one you meant, remove it with the button "
                    + "beside the selector."
                    : "Confirm the reference layer first. railRF proposes one and never assumes it "
                    + "(Q-8), and a pre-selected combo tabbed past is not a confirmation. If this "
                    + "rail was not the one you meant, remove it with the button beside the selector.",
                RailRefusalControl.ReferenceLayer);

        // R-rail19-1b: BOTH doors. The first remedy was the only one named, and it is the wrong one
        // for the user this sentence is usually shown to — a rail added by mistake is one they want
        // GONE, not one they want to give a reference to. A refusal that names one of two exits
        // traps whoever wanted the other.
        if (UnreferencedRail() is { } unreferenced)
            return new RailRefusal(
                $"Rail '{unreferenced}' states no reference layer, so there is nothing to return "
              + "current through. The rail set is solved together, so this one blocks the run as "
              + "well — pick it in the rail selector above and confirm its reference, or remove "
              + "it with the button beside the selector.", RailRefusalControl.RailSelector);

        // The import's own, control and all: it was classified out of its sentence here and came back
        // as something else — a placement-origin refusal that pointed at no control.
        if (PendingImportRefusal is { } import) return import;

        // In the BOARD's own unit — a document refusal naming a coordinate anchor is a refusal
        // about a place somebody has to find on the canvas beside it.
        return _document.Refusal(BoardLengthFormat()) is { } doc
            ? RailRefusals.Classify(doc)
            : null;
    }

    private void RefreshRunGate()
    {
        var refusal = GateRefusal();

        RunBlockedReason = refusal?.Sentence ?? "";
        CanRun = refusal is null;
        OnPropertyChanged(nameof(CanStartRun));
        RunCommand.NotifyCanExecuteChanged();
        AccuracyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunPlane));
        PlaneResonancesCommand.NotifyCanExecuteChanged();

        // The gate's own reason is a refusal in R-rail7-4's sense whenever it names a control, so it
        // shows in the strip and turns that control red rather than hiding behind a disabled button.
        Refusal = refusal;
    }

    // ── The refusals, and the control each one turns red (R-rail7-4) ───────────────────────────

    /// <summary>What the status strip is currently refusing, or null.</summary>
    [ObservableProperty]
    private RailRefusal? _refusal;

    partial void OnRefusalChanged(RailRefusal? value)
    {
        OnPropertyChanged(nameof(HasRefusal));
        OnPropertyChanged(nameof(IsPlacementOriginFlagged));
        OnPropertyChanged(nameof(IsDrillFormatFlagged));
        OnPropertyChanged(nameof(IsReferenceLayerFlagged));
        OnPropertyChanged(nameof(IsRailSelectorFlagged));
        OnPropertyChanged(nameof(IsModelKindFlagged));
        OnPropertyChanged(nameof(IsStackupFlagged));
        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>True while a refusal is on screen.</summary>
    public bool HasRefusal => Refusal is not null;

    /// <summary>The import's placement-origin choice is the answer.</summary>
    public bool IsPlacementOriginFlagged => Refusal?.Control == RailRefusalControl.PlacementOrigin;

    /// <summary>The import's drill coordinate format is the answer.</summary>
    public bool IsDrillFormatFlagged => Refusal?.Control == RailRefusalControl.DrillFormat;

    /// <summary>The reference-layer combo is the answer.</summary>
    public bool IsReferenceLayerFlagged => Refusal?.Control == RailRefusalControl.ReferenceLayer;

    /// <summary>The rail selector is the answer.</summary>
    public bool IsRailSelectorFlagged => Refusal?.Control == RailRefusalControl.RailSelector;

    /// <summary>The <c>Accuracy</c> button is the answer.</summary>
    public bool IsModelKindFlagged => Refusal?.Control == RailRefusalControl.ModelKind;

    /// <summary>The stackup, behind <c>Settings</c>, is the answer.</summary>
    public bool IsStackupFlagged => Refusal?.Control == RailRefusalControl.Stackup;

    /// <summary>
    /// A refusal the import left behind — the placement origin nobody answered, or a drill format
    /// nobody stated. Held rather than reported once, because both remain true until the import is
    /// redone and a sentence that scrolled past is a sentence nobody can act on.
    /// </summary>
    [ObservableProperty]
    private RailRefusal? _pendingImportRefusal;

    partial void OnPendingImportRefusalChanged(RailRefusal? value) => RefreshRunGate();

    // ── The Fast edit loop (§2.3 step 6, R-rail7-5) ────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private RunControl? _inFlight;

    /// <summary>How many solves this window has started. Counted so the coalescing is testable
    /// without timing.</summary>
    internal int SolvesStarted { get; private set; }

    /// <summary>How many were CANCELLED by a later edit rather than finishing.</summary>
    internal int SolvesCancelled { get; private set; }

    /// <summary>The solve currently in flight, or null — what a test awaits.</summary>
    internal Task? Pending { get; private set; }

    /// <summary>True while a solve is running, for the strip's own "solving…" note.</summary>
    [ObservableProperty]
    private bool _isSolving;

    /// <summary>Which model the solve in flight is running. Meaningless while
    /// <see cref="IsSolving"/> is false.</summary>
    private PdnModelKind _solvingKind = PdnModelKind.Fast;

    partial void OnIsSolvingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartRun));
        OnPropertyChanged(nameof(BusyText));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(PartsEmptyText));
        OnPropertyChanged(nameof(HasPartsEmptyText));
        RunCommand.NotifyCanExecuteChanged();
        AccuracyCommand.NotifyCanExecuteChanged();
        StopRunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What the bottom bar says while a solve is in flight, or empty.
    /// </summary>
    /// <remarks>
    /// <b>Because "Accuracy does not seem to do anything" was the report</b> (owner, 2026-09-19),
    /// and it does: on the shipped Power Rail example the mesh reading moves U1's drop from
    /// 48.368 mV to 49.025 mV and replaces every closed-form breakdown row with a meshed one. What
    /// it also does is take about eight times as long as the fast one in a release build and rather
    /// longer in a debug one, during which the only sign of life was the word "solving…" at the end
    /// of a status strip nobody was looking at — so a press with a slow answer and a small visible
    /// delta reads as a dead button.
    ///
    /// <para>So the wait is stated where the button is, and it names the MODEL: the whole point of
    /// §2.9 is that entering Accuracy is deliberate, and a busy note that did not say which reading
    /// is being computed would be the same button press with a spinner on it.</para>
    /// </remarks>
    public string BusyText =>
        !IsSolving ? ""
        : _solvingKind == PdnModelKind.Accurate
            ? "running Accuracy — the mesh solve. Tens of seconds on a real board."
            : "solving…";

    /// <summary>
    /// True when a run can be STARTED — the gate, and nothing already in flight.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not folded into <see cref="CanRun"/>.</b> That property gates the Fast edit
    /// loop as well (<see cref="QueueResolve"/>), and the edit loop's whole contract is that a
    /// re-solve in flight when another edit arrives is CANCELLED and replaced — so making it false
    /// while solving would stop the numbers following the typing, which is R-rail7-5 exactly
    /// backwards. What it gates is the two BUTTONS.
    /// </remarks>
    public bool CanStartRun => CanRun && !IsSolving;

    /// <summary>
    /// Re-extracts and re-solves in <b>Fast</b>, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <b>§2.3 step 6: "the result follows the edit".</b> Change a value, swap a part, delete a part,
    /// change a source ESR or a load current, and the numbers move as you type — so every committed
    /// row edit calls this, and the strip's elapsed time is what tells the user it happened.
    ///
    /// <para><b>A re-solve in flight when another edit arrives is CANCELLED, not queued</b> (the
    /// brief's own first guard). Queueing them means a user who types four characters waits for four
    /// solves to discover the answer to the fourth; and <c>RunControl</c> is the mechanism because it
    /// is already the one <c>em</c> and <c>render</c> cancel through.</para>
    ///
    /// <para><b>It never enters Accuracy.</b> §2.9: never entered automatically, and never left
    /// silently — <see cref="Accuracy"/> is the only way in.</para>
    /// </remarks>
    public void QueueResolve()
    {
        // ── THE GATE FIRST, ALWAYS (owner, 2026-09-19) ────────────────────────────────────────
        //
        // What is on the status strip is a VERDICT ON THE DOCUMENT, and this is called from every
        // committed edit — so by the time it runs, the verdict on screen is about a document state
        // that no longer exists. Refreshing here means one call site rather than a dozen, and it
        // means an edit that leaves the run still refused REPLACES the old sentence with the
        // current one instead of leaving a fixed problem on screen.
        //
        // It is also what clears a refusal the SOLVE raised: RefreshRunGate writes Refusal
        // unconditionally, so a gate that now passes clears it and the solve below re-raises
        // whatever is still wrong. Nothing survives an edit it has stopped being true of.
        RefreshRunGate();

        // AND THE DIRTY MARK, for the same reason and at the same one call site: this runs on every
        // committed edit, so it is where the title learns the document no longer matches disk.
        RefreshDirty();

        // AND THE UNDO ENTRY, for the third time the same reason — see RailRfViewModel.Undo.cs.
        // One funnel, so there is no per-edit-site push to forget at the site somebody adds next.
        NoteEdit();

        if (!CanRun || Board is not { } board || SelectedRail is null) return;
        Start(board, PdnModelKind.Fast);
    }

    /// <summary>Run, in Fast — the bottom bar's own button, for a user who wants to be sure.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private void Run()
    {
        if (Board is { } board) Start(board, PdnModelKind.Fast);
    }

    /// <summary>
    /// The mesh solve, on the one button that changes what the numbers mean.
    /// </summary>
    /// <remarks>
    /// <b>Never entered automatically, and never left silently</b> (§2.9). Pressing it runs the mesh
    /// and the window then shows BOTH results — the Fast one it already had stays in
    /// <see cref="ByModel"/>, which is what lets brief 12 draw the fast curve beside the accurate one
    /// and measure the error on this design rather than promise it in a document.
    ///
    /// <para><b>And pressing it while it is lit goes back to the Fast reading</b>, which is the half
    /// that was missing: the lamp had no way out but an edit. It is a SWITCH of what is on screen
    /// and not a re-solve — both readings are already in hand, so re-running the mesh to leave the
    /// mesh would be tens of seconds to show a number the window is already holding. Where no fast
    /// reading exists yet (Accuracy was the first thing pressed) it runs one, because there is
    /// nothing to switch to.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStartRun))]
    private void Accuracy()
    {
        if (Board is not { } board) return;

        if (!IsShowingAccuracy) { Start(board, PdnModelKind.Accurate); return; }

        if (ByModel.TryGetValue(PdnModelKind.Fast, out var fast))
        {
            Current = fast;
            AcceptSweep(fast.Kind, fast.Sweep);
        }
        else
        {
            Start(board, PdnModelKind.Fast);
        }
    }

    private void Start(RailBoardInputs board, PdnModelKind kind)
    {
        CancelInFlight();

        var cts = new CancellationTokenSource();

        // PROGRESS THROUGH THE WINDOW'S OWN SEAM, not through a `Progress<T>`. `Progress<T>` captures
        // whatever synchronization context happened to be current where it was constructed, and this
        // view model's whole off-thread contract is expressed as `PostToUi` so a test can drive the
        // loop inline with no application host. Two routes to the UI thread would be two answers to
        // the same question.
        var control = new RunControl
        {
            Token = cts.Token,
            Progress = new RailSolveProgress(this, cts),
        };
        _cts = cts;
        _inFlight = control;
        SolvesStarted++;
        _solvingKind = kind;
        SolveStage = "";
        IsSolving = true;

        var request = BuildRequest(board, kind, control);
        // Both requests are built HERE, on the UI thread, because both read the document rows the
        // user is editing — the same rule BuildRequest has always followed.
        var sweepRequest = BuildSweepRequest(kind);
        var token = cts.Token;

        Pending = RunOffThread(() =>
        {
            var watch = Stopwatch.StartNew();
            var result = SolveFunc(request, token);
            var sweep = sweepRequest is null ? null : SweepFunc(sweepRequest);
            watch.Stop();
            return new RailResultView(kind, result, watch.Elapsed.TotalMilliseconds) { Sweep = sweep };
        }, token)
        .ContinueWith(t => PostToUi(() => Finish(t, cts, token)),
                      CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                      TaskScheduler.Default);
    }

    private void Finish(Task<RailResultView> task, CancellationTokenSource cts, CancellationToken token)
    {
        // A result from a solve that was already superseded is DROPPED. It is not merely stale — it
        // was computed against a document the user has since changed, so showing it would put the
        // strip's model kind over numbers that describe a board state nobody can see any more.
        if (!ReferenceEquals(_cts, cts)) { cts.Dispose(); return; }

        _cts = null;
        _inFlight = null;
        IsSolving = false;
        Pending = null;

        if (token.IsCancellationRequested || task.IsCanceled) { SolveStage = ""; cts.Dispose(); return; }

        if (task.IsFaulted)
        {
            // A CANCELLED EXTRACTION ARRIVES HERE, not as IsCanceled. The engine answers a token by
            // throwing OperationCanceledException from inside the work, and a task whose body throws
            // one for a token the TaskScheduler was not given faults rather than transitioning to
            // Canceled. Reporting it as "the solve did not finish: The operation was canceled" would
            // put an error on the strip for the one outcome the user asked for (field report,
            // 2026-09-22 — the run that could not be stopped is the whole reason the token is now
            // threaded at all).
            if (task.Exception?.GetBaseException() is OperationCanceledException)
            {
                SolveStage = "";
                Refusal = new RailRefusal(
                    "The run was stopped. Nothing was computed — a half-finished extraction has no "
                  + "shape to publish, so what is on screen is whatever the last completed run left "
                  + "there.", RailRefusalControl.None);
                cts.Dispose();
                return;
            }

            SolveStage = "";
            Refusal = new RailRefusal(
                $"The solve did not finish: {task.Exception?.GetBaseException().Message}",
                RailRefusalControl.None);
            cts.Dispose();
            return;
        }

        SolveStage = "";

        var view = task.Result;
        cts.Dispose();

        if (view.Result.Refusal is { } why)
        {
            // Nothing was solved, so nothing replaces what is on screen — and the refusal names the
            // control that answers it rather than merely being said.
            Refusal = RailRefusals.Classify(why);
            return;
        }

        Refusal = null;

        // ATOMIC. The kind and the numbers are one value; see RailResultView's own note.
        ByModel[view.Kind] = view;
        Current = view;
        AcceptSweep(view.Kind, view.Sweep);

        // R-rail23-3a. AFTER the assignment above, because the baseline is built from what is on
        // screen — and the run that WAS on screen is what becomes the thing to compare against.
        CaptureBaseline(view.Kind);

        // ── R-rail26-2b: THE OFFER FOLLOWS THE RESULT, AND THE TABLE DOES NOT ──────────────────
        //
        // Both halves of discovery's predicate are the EXTRACTION's galvanic regions, which arrive
        // with the result and did not exist a moment ago — so a board solved for the first time has
        // parts to offer that it had none of before. The parts TABLE is not rebuilt here because
        // nothing on a row comes off the result: its electrical columns are RailPartResolver's, and
        // replacing every row object on every solve would drop the selection the board's mark
        // follows.
        RebuildPartOffer();
    }

    private void CancelInFlight()
    {
        if (_cts is not { } cts) return;

        SolvesCancelled++;
        _inFlight?.RequestStop();
        cts.Cancel();
        _cts = null;
        _inFlight = null;
        IsSolving = false;
        SolveStage = "";
    }

    /// <summary>
    /// Stops the run in flight, at the user's request.
    /// </summary>
    /// <remarks>
    /// <b>The button that was not there</b> (field report, 2026-09-22). A designer pressed Run on a
    /// production six-layer board, got "solving…" and reported it as "probably run in a dead end" —
    /// with no way to tell a long answer from a hung one and nothing to press. There was no Stop
    /// anywhere on the window, and until this round <see cref="CancelInFlight"/> could not have
    /// backed one: the token reached no engine, so cancelling stopped the window listening and left
    /// the work running.
    ///
    /// <para><b>It abandons the run rather than finishing early</b>, which is
    /// <see cref="RunControl"/>'s own distinction and the right one here: a half-finished extraction
    /// has no shape to publish — the netlist is not assembled until every piece is measured — so
    /// there is no partial answer to keep. What is on screen stays what the last completed run put
    /// there, and the strip says so rather than blanking the numbers.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsSolving))]
    private void StopRun()
    {
        if (_cts is null) return;

        CancelInFlight();
        Refusal = new RailRefusal(
            "The run was stopped. Nothing was computed — what is on screen is whatever the last "
          + "completed run left there.", RailRefusalControl.None);
    }

    /// <summary>
    /// What phase the solve in flight is in, or empty — <b>beside <see cref="BusyText"/> rather
    /// than instead of it</b>.
    /// </summary>
    /// <remarks>
    /// <c>BusyText</c> names the MODEL, which is what §2.9 needs on screen and what a press of
    /// Accuracy has to be answerable by. This names the phase and, where the phase has an honest
    /// denominator, how far through it is. Together they are the difference between a long run and a
    /// hung one, which is the distinction the field report could not make — and the reason the next
    /// report of a slow board will arrive saying WHICH phase was slow.
    /// </remarks>
    [ObservableProperty]
    private string _solveStage = "";

    partial void OnSolveStageChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    /// <summary>
    /// Carries <see cref="RunControl"/>'s observations onto the UI thread through the view model's
    /// own <see cref="PostToUi"/> seam.
    /// </summary>
    /// <remarks>
    /// <b>Not a <c>Progress&lt;T&gt;</c></b>: that type captures whatever synchronization context is
    /// current where it is constructed, and this view model's off-thread contract is expressed as
    /// <see cref="PostToUi"/> precisely so a test can drive the whole loop inline with no application
    /// host. Two routes to the UI thread would be two answers to one question.
    ///
    /// <para><b>It carries the token source it was made for</b> and drops an observation whose run
    /// has been superseded — the same guard <c>Finish</c> keeps, for the same reason: a stage name
    /// from a cancelled solve arriving after the replacement started would put the old run's phase
    /// under the new run's numbers.</para>
    /// </remarks>
    private sealed class RailSolveProgress(RailRfViewModel owner, CancellationTokenSource cts)
        : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => owner.PostToUi(() =>
        {
            if (!ReferenceEquals(owner._cts, cts)) return;
            owner.SolveStage = Describe(value);
        });

        private static string Describe(RunProgress p)
        {
            if (p.Stage is not { Length: > 0 } stage) return "";

            // A denominator only where the stage declared one. RunControl's own contract is that a
            // StageTotal of 0 means indeterminate, and printing a fake "1 / 1" for a phase that is
            // one pass over the board would be worse than printing nothing.
            if (p.StageTotal <= 0) return stage;

            string unit = p.StageUnit is { Length: > 0 } u ? $" {u}" : "";
            return $"{stage} — {p.StageCompleted:N0} / {p.StageTotal:N0}{unit}";
        }
    }

    private RailDcRequest BuildRequest(RailBoardInputs board, PdnModelKind kind, RunControl? control = null) => new()
    {
        // ── THE CONTROL WAS BUILT AND HANDED TO NOTHING (field report, 2026-09-22) ─────────────
        //
        // `Start` has constructed a RunControl since brief 7, and QueueResolve's own comment says it
        // is "the mechanism because it is already the one em and render cancel through". No request
        // ever carried it. CancelInFlight therefore stopped the window LISTENING and left the work
        // running: four edits in a row put four whole-board extractions on the thread pool at once,
        // each still competing for the same cores, and the "solving…" a designer reported as a dead
        // end had no way to be taken back and no way to say which phase it was in.
        Control        = control,

        Document       = _document,
        Shapes         = board.Shapes,
        Technology     = board.Technology,
        DbuPerMicron   = board.DbuPerMicron,
        LengthFormat   = board.LengthFormat,
        Pads           = board.Pads,
        NetPoints      = board.NetPoints,
        ReferenceNet   = board.ReferenceNet,
        BoardOutline   = board.BoardOutline,
        SeriesElements = board.SeriesElements,
        ShuntParts     = board.ShuntParts,
        Model          = kind,
        // Stated only where the user stated one. A cell size of "whatever the window last showed"
        // would silently change what Accuracy means between two runs of the same document.
        Mesh           = MeshCellMetres is { } m ? new PdnMeshSettings { CellSizeMetres = m } : new(),
    };

    // ── The status strip (§11.3's second point) ────────────────────────────────────────────────

    /// <summary>
    /// "Fast model · 4.1 ms · 20 °C · reference as imported · 3 parts with no bias curve".
    /// </summary>
    /// <remarks>
    /// <b>Always on screen</b>, so nobody reads a fast answer as an accurate one, and the elapsed time
    /// makes the cost of <c>Accuracy</c> obvious BEFORE it is pressed. The reference-plane choice and
    /// the derating coverage sit beside it for the same reason: all three are things a reader of the
    /// numbers has to know and would otherwise have to go and look up.
    /// </remarks>
    public string StatusLine
    {
        get
        {
            var parts = new List<string>(6) { ModelKindText };

            // R-doc: the elapsed time measures the machine, not the design. It is the point of the
            // strip on screen — the cost of Accuracy before it is pressed — and it is pure churn in
            // a committed doc figure, which read 414.9 ms one regeneration and 400.3 ms the next.
            // Suppressed only while the docs factory is capturing; SvgLint.Measurements is the gate.
            if (ElapsedText.Length > 0 && !UiArtworkGenerator.HeadlessCapture) parts.Add(ElapsedText);
            parts.Add($"{_document.Settings.CopperTemperatureCelsius:0.#} °C");
            parts.Add("reference " + ExtentText(ReferenceExtent));

            // R-rail14-3. Always on screen, because §9's whole complaint is that a wrong stackup is
            // recognised instantly and never gone looking for.
            if (PlaneCapacitanceShort.Length > 0) parts.Add(PlaneCapacitanceShort);

            if (PartsModelledFromFile > 0)
                parts.Add($"{PartsModelledFromFile} part(s) modelled from a file");

            if (PartsWithoutBiasCurve > 0)
                parts.Add($"{PartsWithoutBiasCurve} part(s) with no bias curve");

            // R-ab1-6c. The count AND where it came from — "142 pads: 128 from the board netlist,
            // 14 from the artwork" is R-ab1-3b made visible, and it is the only way a user finds out
            // their netlist is two parts stale. One spelling, shared with the export banner.
            if (Board is { Pads.Count: > 0 } withPads)
                parts.Add(PlacedPinSummary.Describe(withPads.Pads));

            // R-ab2-4d, beside it and in the same shape: a netlist, a schematic and a stamp on the
            // copper are three different claims, and a reader of the numbers below is entitled to
            // know which one named the rail they picked.
            if (NetOriginText.Length > 0) parts.Add(NetOriginText);

            // A run built on numbers railRF put there must never be silent about it. The add gesture
            // now hands a load a current and a source a voltage (RailRfViewModel.Seeds.cs — a field
            // report, 2026-09-21), which is the whole difference between a first
            // answer and a column of zeros; this is the half that keeps the old rule's PURPOSE, which
            // was never "no defaults" but "a number nobody typed must not read as one somebody did".
            if (SeededRowsText.Length > 0) parts.Add(SeededRowsText);

            // ── WHAT A RUN WITH NO DECOUPLING ON IT ACTUALLY ANSWERED (field report, 2026-09-22)
            //
            // "even without any part model i was allowed to press run". Allowing it is right and
            // stays: the DC drop is a real answer that needs no decoupling at all, and on a board
            // being checked for copper it is frequently the whole question — gating Run would refuse
            // something railRF can answer. What was wrong is that nothing said what the OTHER half
            // of the window was showing. With no part on the rail the impedance curve is bare
            // copper and the plane's own capacitance, the target band has nothing in it to meet, and
            // a reader has no way to tell that from a bank that was modelled and found wanting.
            if (NoDecouplingText.Length > 0) parts.Add(NoDecouplingText);

            // The other thing this window can be busy doing, and until 2026-09-21 the one it did
            // WITHOUT saying so — on the UI thread, with the window unresponsive for as long as it
            // took (RailRfViewModel.NetPreview.cs' header). Named rather than spinner-shaped for
            // BusyText's own reason: a user who knows the board is being read knows the answer is
            // coming, and a user who does not thinks the reference combo did nothing.
            if (IsReadingCopper)
                parts.Add("reading the board's copper — the layer flatten and the connectivity walk");

            // Brief 28: the layout moved a part, and until this clears the pads, the pick list and
            // the turned-parts note are still of the placements before it.
            if (IsReadingParts)
                parts.Add("re-reading the board's parts — the layout has moved them");

            if (IsSolving)
            {
                parts.Add(BusyText);
                // The phase, where the run has reached one — see SolveStage's own note for why it
                // is beside BusyText and not instead of it.
                if (SolveStage.Length > 0) parts.Add(SolveStage);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The strip's phrase for a rail carrying no decoupling, or "" where it carries some.
    /// </summary>
    /// <remarks>
    /// <b>A note and not a gate</b>, which is the owner's call on the field report of 2026-09-22.
    /// The complaint was that Run could be pressed "without any part model"; the DC answer needs no
    /// parts and is often the whole question, so refusing it would refuse work railRF can do. What
    /// the run cannot do without them is the frequency half — so that is what is said.
    ///
    /// <para><b>MOUNTED parts only.</b> A rail whose whole bank is unmounted is in exactly the state
    /// this sentence describes, and brief 23's checkbox is how a designer asks "what does this board
    /// do with the decoupling taken off" — which is a question they are entitled to a clear answer
    /// to rather than a silently empty curve.</para>
    /// </remarks>
    public string NoDecouplingText
    {
        get
        {
            if (SelectedRail is not { } rail) return "";
            foreach (var part in rail.Parts)
                if (part.Mounted && part.Connection == RailPartConnection.Shunt) return "";

            return rail.Parts.Count > 0
                ? "no decoupling mounted — the |Z| curve is bare copper"
                : "no decoupling parts — the |Z| curve is bare copper";
        }
    }

    private static string ExtentText(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported       => "as imported",
        RailReferenceExtent.FilledToOutline  => "filled to outline — optimistic",
        _                                    => "infinite — an upper bound",
    };
}
