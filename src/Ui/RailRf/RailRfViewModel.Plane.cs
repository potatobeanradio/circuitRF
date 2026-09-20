// The plane-pair run: §4.5's modes and §2.4's |Z| map, on their own button
// (docs/sonnet-briefs/brief-railrf-15-modes-and-maps.md R-rail15-1 … R-rail15-3; railrf.md §2.4,
//  §2.9, §4.5).
//
// ── IT IS A BUTTON AND NOT PART OF THE EDIT LOOP, FOR §2.9's OWN REASON ────────────────────────
//
// "Never entered automatically, and never left silently." Accuracy is on that button for the price
// of a mesh; this is on one for the price of a DENSE EIGENSOLVE — 36 s on the design note's own
// 90 x 70 mm board at 1.4 mm cells (src/Engine/RESOLVED.md). QueueResolve runs on every committed
// row edit, and putting this in it would make a window nobody could type in.
//
// ── THE FREQUENCY IS AN INPUT BECAUSE IT SETS THE MESH, NOT ONLY THE MAP ───────────────────────
//
// §2.8: the extraction is FOR one frequency. That one number chooses the cell size (λ/20 there),
// the copper's skin-effect resistance and the dielectric's G = ωC·tan δ — so the modes and the map
// are both of it, and a map at "some other frequency" would carry this frequency's losses.
// RailPlaneRun's header carries the whole argument; what is here is the box it is typed into.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// What actually runs a plane-pair request. <see cref="RailPlaneRun.Run"/> in the application;
    /// injectable for the same reasons <c>SolveFunc</c> and <c>SweepFunc</c> are.
    /// </summary>
    internal Func<RailPlaneRequest, RailPlaneResult> PlaneFunc { get; set; } = RailPlaneRun.Run;

    /// <summary>
    /// The plane pair's own answer for the selected rail, or null before one has been run.
    /// </summary>
    /// <remarks>
    /// <b>Not filed by model kind, unlike the DC answer and the sweep.</b> §2.9's A/B comparison is
    /// between the two READINGS OF THE COPPER, and there is only one reading of a plane pair: the
    /// Fast model refuses above a tenth of the first cavity resonance (R-rail4-4), which is below
    /// everything this run is about. A "Fast plane answer" would be a row nobody could produce.
    /// </remarks>
    [ObservableProperty]
    private PdnPlaneAnswer? _plane;

    partial void OnPlaneChanged(PdnPlaneAnswer? value)
    {
        BoardOverlayLayer.Plane = value;
        OnPropertyChanged(nameof(ModeLines));
        OnPropertyChanged(nameof(HasModes));
        OnPropertyChanged(nameof(PlaneMessage));
        OnPropertyChanged(nameof(HasPlaneMessage));
        AnnounceImpedanceMap();
    }

    // ── §2.4's map has its own controls ON ITS OWN TAB (owner, 2026-09-19) ─────────────────────
    //
    // The |Z| tab used to be a dead end. It said "No |Z| map yet" and pointed at a card on a
    // DIFFERENT tab, so producing the picture meant leaving it; and because a refused run leaves
    // Plane null on purpose — the previous answer is worth more than a cleared one — the tab went
    // on saying "no map yet" to somebody who had just pressed Find and been refused, with the
    // refusal printed somewhere they were not looking. On the shipped Power Rail example that was
    // every press: see RailPlaneRun.Fit for the mesh collision that made it so.
    //
    // So the frequency box, the Find button, the busy line and the refusal are all on the tab that
    // shows the map. The Plane resonances card keeps all four as well, because the MODE LIST is a
    // frequency-tab answer and it is driven by the same command and the same box.

    /// <summary>True once there is an impedance map to draw.</summary>
    public bool HasImpedanceMap => Plane is { Refusal: null } p && p.ImpedanceMap.Count > 0;

    /// <summary>True while the |Z| tab is showing and has no map on it — when its own controls and
    /// its own sentence are what the board panel draws.</summary>
    /// <remarks><b>And only with a board on screen.</b> The empty-board placeholder is centred
    /// too, and two centred panels on one canvas is the collision the map note already had with
    /// it.</remarks>
    public bool ShowImpedanceFinder =>
        HasBoard && SelectedBoardOverlay == RailBoardOverlay.Impedance && !HasImpedanceMap;

    /// <summary>
    /// True when the |Z| tab is showing a map — when the frequency controls move to a compact
    /// strip over the top of it rather than going away.
    /// </summary>
    /// <remarks>
    /// <b>The map is at ONE frequency, so changing the frequency is the main thing to do next</b>
    /// (owner, 2026-09-19: <i>"how do I change the frequency for it after I've run one?"</i>).
    /// The first cut hid the controls the moment a map appeared and left the Plane resonances
    /// card on the Frequency tab as the only way back — which is the same "leave the picture to
    /// change the picture" this tab's own controls existed to end, reintroduced one state later.
    ///
    /// <para>A strip and not the centred panel: the panel is an EMPTY state and carries the
    /// sentence explaining what the map is, and a reader looking at a map has already had that
    /// answered. It sits at the top because the bottom two corners are the value readout and the
    /// cursor position.</para>
    /// </remarks>
    public bool ShowImpedanceRefind =>
        HasBoard && SelectedBoardOverlay == RailBoardOverlay.Impedance && HasImpedanceMap;

    /// <summary>What the strip says the map on screen is of.</summary>
    public string ImpedanceMapAt =>
        Plane is { Refusal: null } p && p.ImpedanceMap.Count > 0
            ? $"|Z| at {PdnMask.Hertz(p.MapFrequencyHz)} from {p.MapPortName}"
            : "";

    /// <summary>
    /// What the empty |Z| tab says: the last run's refusal, or what the map is for.
    /// </summary>
    /// <remarks>
    /// The default half is <see cref="RailMapScene.EmptyImpedanceNote"/> and not a second copy —
    /// the renderer prints the same sentence where there is no window to print it, and two
    /// spellings of one empty state is how they come to disagree.
    /// </remarks>
    public string ImpedanceFinderNote =>
        PlaneRefusal is { Length: > 0 } why ? why : RailMapScene.EmptyImpedanceNote;

    /// <summary>Why the last plane run produced nothing, or empty.</summary>
    /// <remarks>Kept apart from <see cref="PlaneMessage"/>, which also carries the NOTES of a run
    /// that succeeded: the |Z| tab prints a refusal in place of its own sentence and must not
    /// print a success summary there.</remarks>
    [ObservableProperty]
    private string _planeRefusal = "";

    partial void OnPlaneRefusalChanged(string value)
    {
        OnPropertyChanged(nameof(ImpedanceFinderNote));
        OnPropertyChanged(nameof(HasPlaneRefusal));
    }

    /// <summary>True when the last plane run was refused.</summary>
    public bool HasPlaneRefusal => PlaneRefusal.Length > 0;

    /// <summary>Re-raises everything the |Z| tab's own panel binds.</summary>
    internal void AnnounceImpedanceMap()
    {
        OnPropertyChanged(nameof(HasImpedanceMap));
        OnPropertyChanged(nameof(ShowImpedanceFinder));
        OnPropertyChanged(nameof(ShowImpedanceRefind));
        OnPropertyChanged(nameof(ImpedanceFinderNote));
        OnPropertyChanged(nameof(ImpedanceMapAt));
    }

    /// <summary>The extraction the plane answer is of — <b>its own</b>, not the DC run's.</summary>
    [ObservableProperty]
    private PdnProvenance? _planeProvenance;

    /// <summary>
    /// The frequency the cavity is read at, as the box holds it.
    /// </summary>
    /// <remarks>
    /// <b>Opens on the top of the rail's own band</b> rather than on a constant. That is the highest
    /// frequency the document says it cares about, so it is the mesh the whole band is resolvable
    /// on — and a user who wants the map somewhere else types it, which is one edit rather than a
    /// number to invent from nothing.
    /// </remarks>
    [ObservableProperty]
    private string _planeFrequencyEntry = "";

    /// <summary>The frequency the box parses to, or null where it does not parse.</summary>
    internal double? PlaneFrequencyHz =>
        RailValueFormat.TryParse(PlaneFrequencyEntry, RailQuantity.Frequency, out double hz) && hz > 0
            ? hz
            : null;

    partial void OnPlaneFrequencyEntryChanged(string value)
    {
        OnPropertyChanged(nameof(CanRunPlane));
        PlaneResonancesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Seeds the frequency box from the selected rail's band. Called when the rail changes.</summary>
    private void SyncPlaneFrequencyDefault()
    {
        if (PlaneFrequencyEntry.Length > 0) return;      // never overwrite what a user typed
        if (SelectedRail is not { } rail || !(rail.Band.StopHz > 0)) return;
        PlaneFrequencyEntry = RailValueFormat.FormatWithUnit(rail.Band.StopHz, RailQuantity.Frequency);
    }

    /// <summary>True when the plane run can do something.</summary>
    /// <remarks><b>Off while one is in flight.</b> This run is tens of seconds and the press has
    /// to be visibly taken — the same rule Run and Accuracy already follow (owner, 2026-09-19).</remarks>
    public bool CanRunPlane =>
        CanRun && Board is not null && PlaneFrequencyHz is not null && !IsFindingModes;

    /// <summary>True while a plane run is in flight.</summary>
    [ObservableProperty]
    private bool _isFindingModes;

    partial void OnIsFindingModesChanged(bool value)
    {
        PlaneResonancesCommand.NotifyCanExecuteChanged();
        if (!value) PlaneStage = "";
    }

    /// <summary>
    /// What the plane run is doing right now — <b>the progress the |Z| tab can honestly show</b>.
    /// </summary>
    /// <remarks>
    /// <b>A stage and an indeterminate bar, never a percentage</b> (owner asked for a progress
    /// bar, 2026-09-19). The dominant cost of this run is one dense LAPACK call: cubic in the
    /// cell count, no callbacks, nothing to subdivide. A determinate bar would therefore be
    /// animating a number nobody computed. What the run CAN say is which of four things it is on
    /// and how big the problem turned out to be — "re-meshing at 0.42 mm, 4,700 cells is over the
    /// 4,000 the dense solve stops at" is the sentence that explains a thirty-second wait, and a
    /// bar creeping at an invented rate explains none of it.
    /// </remarks>
    [ObservableProperty]
    private string _planeStage = "";

    /// <summary>The plane run currently in flight, or null — what a test awaits.</summary>
    internal Task? PendingPlane { get; private set; }

    /// <summary>What the plane answer has to say that is not a picture — a refusal, or its notes.</summary>
    [ObservableProperty]
    private string _planeMessage = "";

    /// <summary>True while there is something to say beside the mode list.</summary>
    public bool HasPlaneMessage => PlaneMessage.Length > 0;

    /// <summary>
    /// §2.4's plane-resonance table, as the docked list reads it — <b>a frequency AND a place</b>.
    /// </summary>
    /// <remarks>
    /// Projected to strings here rather than bound through <c>PdnPlaneMode.Describe()</c> in the
    /// AXAML, for the reason <c>PortLines</c> and <c>BreakdownRows</c> already give: a compiled
    /// binding cannot call a method, and doing it here is what makes the window and a headless
    /// report print the same sentence.
    /// </remarks>
    public IReadOnlyList<string> ModeLines =>
        Plane is { Refusal: null } p ? [.. p.Modes.Select(m => m.Describe())] : [];

    /// <inheritdoc cref="HasPlaneMessage"/>
    public bool HasModes => ModeLines.Count > 0;

    /// <summary>
    /// Runs the plane pair — <b>one mesh extraction at the stated frequency, then §4.5's
    /// eigenproblem and §2.4's map</b>.
    /// </summary>
    /// <remarks>
    /// Off the UI thread and cancellable, like every other run in this window, and for a stronger
    /// reason than the others: this one is tens of seconds rather than milliseconds.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunPlane))]
    private void PlaneResonances()
    {
        if (Board is not { } board || SelectedRailName is not { Length: > 0 } railName) return;
        if (PlaneFrequencyHz is not { } hz) return;

        CancelPlaneInFlight();

        PlaneRefusal = "";
        PlaneStage = "starting…";

        var cts = new CancellationTokenSource();
        _planeCts = cts;
        IsFindingModes = true;

        // Built HERE, on the UI thread, because it reads the document rows the user is editing —
        // the same rule BuildRequest and BuildSweepRequest already follow.
        var request = new RailPlaneRequest
        {
            Board = BuildRequest(board, PdnModelKind.Accurate),
            RailName = railName,
            FrequencyHz = hz,
            MapPortIndex = 0,
            // Off the run's thread and onto the UI's, like every other cross-thread report here.
            Progress = stage => PostToUi(() => { if (IsFindingModes) PlaneStage = stage; }),
        };

        var token = cts.Token;

        PendingPlane = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var result = PlaneFunc(request);
            watch.Stop();
            return (result, watch.Elapsed.TotalMilliseconds);
        }, token)
        .ContinueWith(t => PostToUi(() => FinishPlane(t, cts)),
                      CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                      TaskScheduler.Default);
    }

    private CancellationTokenSource? _planeCts;

    private void FinishPlane(Task<(RailPlaneResult Result, double Ms)> task, CancellationTokenSource cts)
    {
        // A result from a run the user has since superseded is DROPPED, exactly as a superseded DC
        // solve is: it was computed against a document that has changed underneath it.
        if (!ReferenceEquals(_planeCts, cts)) { cts.Dispose(); return; }

        _planeCts = null;
        IsFindingModes = false;
        PendingPlane = null;

        if (task.IsCanceled) { cts.Dispose(); return; }

        if (task.IsFaulted)
        {
            PlaneMessage = PlaneRefusal =
                "The plane-pair run did not finish: " +
                task.Exception?.GetBaseException().Message;
            cts.Dispose();
            return;
        }

        var (result, ms) = task.Result;
        cts.Dispose();

        PlaneProvenance = result.Provenance;

        if (result.Refusal is { } why)
        {
            // NOTHING replaces what is on screen — the previous answer stays, and the refusal is
            // what changes. A map cleared by a refusal is a picture the user cannot get back
            // without re-running the whole thing.
            PlaneMessage = PlaneRefusal = why;
            return;
        }

        Plane = result.Answer;
        PlaneRefusal = "";
        PlaneMessage = string.Join(
            " ",
            new[]
            {
                $"{result.Answer!.Modes.Count} mode(s) over " +
                $"{result.Provenance!.CellCount:N0} cells at " +
                $"{result.Provenance.CellSizeMetres * 1e3:0.###} mm, in {ms / 1000.0:0.0} s.",
            }.Concat(result.Answer.Notes));
    }

    private void CancelPlaneInFlight()
    {
        if (_planeCts is not { } cts) return;
        cts.Cancel();
        _planeCts = null;
        IsFindingModes = false;
    }

    /// <summary>Drops the plane answer. Called wherever the board or the document changed.</summary>
    /// <remarks>
    /// <b>For <c>ByModel</c>'s own stated reason</b>: a mode list of one board beside a drop map of
    /// another is worse than no mode list, and the cavity is exactly the answer somebody would
    /// forget had been computed before the last import.
    /// </remarks>
    private void ClearPlane()
    {
        CancelPlaneInFlight();
        Plane = null;
        PlaneProvenance = null;
        PlaneMessage = "";
        PlaneRefusal = "";
    }
}
