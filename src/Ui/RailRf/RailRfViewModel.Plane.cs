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
    public bool CanRunPlane => CanRun && Board is not null && PlaneFrequencyHz is not null;

    /// <summary>True while a plane run is in flight.</summary>
    [ObservableProperty]
    private bool _isFindingModes;

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
    /// AXAML, for the reason <c>PortLines</c> and <c>BreakdownLines</c> already give: a compiled
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
            PlaneMessage = "The plane-pair run did not finish: " +
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
            PlaneMessage = why;
            return;
        }

        Plane = result.Answer;
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
    }
}
