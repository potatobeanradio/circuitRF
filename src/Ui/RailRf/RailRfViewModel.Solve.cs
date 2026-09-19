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
        OnPropertyChanged(nameof(HasBoard));
        OnPropertyChanged(nameof(HasNoPickableNets));
        ClearResults();
        RebuildBoardLayout();
        RebuildReferenceOptions();
        RebuildParts();
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
        OnPropertyChanged(nameof(ModelKindText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(SelectedRailResult));
        OnPropertyChanged(nameof(Breakdown));
        OnPropertyChanged(nameof(Ports));
        OnPropertyChanged(nameof(ViaFlagSummary));
        OnPropertyChanged(nameof(HasBothModels));
        OnPropertyChanged(nameof(PortLines));
        OnPropertyChanged(nameof(BreakdownLines));
        OnPropertyChanged(nameof(PlaneCapacitanceLine));
        OnPropertyChanged(nameof(PlaneCapacitanceShort));
        OnPropertyChanged(nameof(HasPlaneCapacitance));
        AnnounceCardVisibility();
        SyncBoardOverlayResult();
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
    public IReadOnlyList<string> PortLines => [.. Ports.Select(p => p.Describe())];

    /// <summary>
    /// Every breakdown row as the docked list reads it — label, drop, share, and how many netlist
    /// elements it aggregates.
    /// </summary>
    /// <remarks>
    /// The element count is on the row rather than in a tooltip because it is what tells a reader
    /// which SHAPE the row is: one element is a part, thousands are a meshed trace section, and the
    /// arithmetic that produced the drop is different for each (R-rail5-4).
    /// </remarks>
    public IReadOnlyList<string> BreakdownLines =>
    [
        .. Breakdown.Select(b =>
            $"{b.Label}: {b.DropV * 1e3:0.###} mV ({b.ShareOfTotal:P0})"
          + $" · {RailValueFormat.FormatWithUnit(b.ResistanceOhms, RailQuantity.Resistance)}"
          + $" · {RailValueFormat.FormatWithUnit(b.CurrentA, RailQuantity.Current)}"
          + (b.ElementCount > 1 ? $" · {b.ElementCount} elements" : "")),
    ];

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

    private void RefreshRunGate()
    {
        string? why =
            Board is null
                ? "There is no board yet. Import one — the artwork, and the placement, BOM and netlist "
                + "that go with it."
            : SelectedRail is null
                ? "This document holds no rails yet. Pick the power net on the board to make one."
            : !IsReferenceConfirmed
                ? "Confirm the reference layer first. railRF proposes one and never assumes it (Q-8), "
                + "and a pre-selected combo tabbed past is not a confirmation."
            : PendingImportRefusal is { } import
                ? import.Sentence
            : _document.Refusal();

        RunBlockedReason = why ?? "";
        CanRun = why is null;
        RunCommand.NotifyCanExecuteChanged();
        AccuracyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunPlane));
        PlaneResonancesCommand.NotifyCanExecuteChanged();

        // The gate's own reason is a refusal in R-rail7-4's sense whenever it names a control, so it
        // shows in the strip and turns that control red rather than hiding behind a disabled button.
        Refusal = why is null ? null : RailRefusals.Classify(why);
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
        if (!CanRun || Board is not { } board || SelectedRail is null) return;
        Start(board, PdnModelKind.Fast);
    }

    /// <summary>Run, in Fast — the bottom bar's own button, for a user who wants to be sure.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
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
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Accuracy()
    {
        if (Board is { } board) Start(board, PdnModelKind.Accurate);
    }

    private void Start(RailBoardInputs board, PdnModelKind kind)
    {
        CancelInFlight();

        var cts = new CancellationTokenSource();
        var control = new RunControl { Token = cts.Token };
        _cts = cts;
        _inFlight = control;
        SolvesStarted++;
        IsSolving = true;

        var request = BuildRequest(board, kind);
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

        if (token.IsCancellationRequested || task.IsCanceled) { cts.Dispose(); return; }

        if (task.IsFaulted)
        {
            Refusal = new RailRefusal(
                $"The solve did not finish: {task.Exception?.GetBaseException().Message}",
                RailRefusalControl.None);
            cts.Dispose();
            return;
        }

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
    }

    private RailDcRequest BuildRequest(RailBoardInputs board, PdnModelKind kind) => new()
    {
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

            if (ElapsedText.Length > 0) parts.Add(ElapsedText);
            parts.Add($"{_document.Settings.CopperTemperatureCelsius:0.#} °C");
            parts.Add("reference " + ExtentText(ReferenceExtent));

            // R-rail14-3. Always on screen, because §9's whole complaint is that a wrong stackup is
            // recognised instantly and never gone looking for.
            if (PlaneCapacitanceShort.Length > 0) parts.Add(PlaneCapacitanceShort);

            if (PartsModelledFromFile > 0)
                parts.Add($"{PartsModelledFromFile} part(s) modelled from a file");

            if (PartsWithoutBiasCurve > 0)
                parts.Add($"{PartsWithoutBiasCurve} part(s) with no bias curve");

            if (IsSolving) parts.Add("solving…");

            return string.Join(" · ", parts);
        }
    }

    private static string ExtentText(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported       => "as imported",
        RailReferenceExtent.FilledToOutline  => "filled to outline — optimistic",
        _                                    => "infinite — an upper bound",
    };
}
