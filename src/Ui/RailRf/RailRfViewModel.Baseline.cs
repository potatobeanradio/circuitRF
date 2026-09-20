// The previous run, kept — so "change one thing and compare" has something to compare against
// (docs/sonnet-briefs/brief-railrf-23-mount-and-unmount.md R-rail23-3a … R-rail23-3d).
//
// ── WHY THIS EXISTS AT ALL ─────────────────────────────────────────────────────────────────────
//
// `RailComparison` compares two `.crail` DOCUMENTS, and the Compare dialog asks for a second file.
// That is the right shape for §2.5's question — "the reference passes; does yours?" — and it is the
// wrong shape for the designer's actual loop, which is: save the result, change one thing, re-run,
// compare. Within ONE document there was nothing to point Compare at, because the other side of
// that comparison is a run that no longer exists.
//
// So railRF keeps one. ONE DEEP, and deliberately not a history (R-rail23-3a): the question is
// "what did that change do", and a list of twelve past runs is a different feature with a different
// UI and a different set of questions about what happens when you delete one.
//
// ── IT IS TAKEN WHEN A RUN IS ACCEPTED, NOT WHEN ONE IS STARTED ────────────────────────────────
//
// The brief says "taken automatically when a new run starts", and the two are the same baseline —
// but only one of them can be taken correctly. QueueResolve fires from a committed edit, so by the
// time a run STARTS the document already carries the change; a snapshot taken there would record
// the NEW inputs against the OLD result and R-rail23-3c would then report that nothing changed. So
// the document snapshot travels with the result it produced, and the previous one is promoted the
// moment a new result is accepted. A baseline is only reachable through Compare, which already
// refuses until something has been solved, so nothing is visible any earlier either way.
//
// ── AND NOTHING HERE RE-SOLVES ─────────────────────────────────────────────────────────────────
//
// The baseline is a result that was already computed and the document state it was computed from.
// Re-running the reference side — which is what comparing against another `.crail` does — would be
// seconds of work to reproduce an answer this window is already holding, and would reproduce it
// from a document that has since changed.

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One completed run, kept for comparison: what it produced, and <b>the document it produced it
/// from</b>.
/// </summary>
/// <remarks>
/// <b>The document half is what makes R-rail23-3c possible.</b> A comparison of two runs of one
/// document has to name what changed between them or the reader is left diffing curves to infer it
/// — and the only place that information exists is the inputs, which the live document no longer
/// holds because the change is exactly what was made to it.
/// </remarks>
/// <param name="Document">A deep COPY of the document as it was run. Never the live one: the live
/// one is what the user is editing, and a "baseline" that followed the edits would compare a design
/// with itself and report that nothing changed.</param>
/// <param name="RailName">Which rail the side is of.</param>
/// <param name="Kind">Which model produced it — §2.9's first rule, and the reason a comparison runs
/// both sides at one kind.</param>
/// <param name="Side">The answer, as <see cref="RailComparison"/> consumes it.</param>
/// <param name="TakenUtc">When, for the strip.</param>
/// <param name="Pinned">R-rail23-3d — held across further runs rather than replaced by each.</param>
public sealed record RailRunBaseline(
    RailDocument Document,
    string RailName,
    PdnModelKind Kind,
    RailComparisonSide Side,
    DateTime TakenUtc,
    bool Pinned = false)
{
    /// <summary>What the Compare menu row and the strip call it.</summary>
    public string Describe() =>
        (Pinned ? "Pinned result" : "The previous run") +
        $" — {RailName}, {(Kind == PdnModelKind.Fast ? "Fast model" : "Accuracy")}, " +
        $"{TakenUtc.ToLocalTime():HH:mm:ss}";
}

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// The run to compare against — the previous one, or the one that was pinned (R-rail23-3a/3d).
    /// Null until two runs have completed, or until one was pinned.
    /// </summary>
    [ObservableProperty]
    private RailRunBaseline? _baseline;

    /// <summary>The run on screen, with the document it was run from. Not a baseline yet — it
    /// becomes one when the next run is accepted, or the moment it is pinned.</summary>
    private RailRunBaseline? _lastCompleted;

    partial void OnBaselineChanged(RailRunBaseline? value)
    {
        OnPropertyChanged(nameof(HasBaseline));
        OnPropertyChanged(nameof(IsBaselinePinned));
        OnPropertyChanged(nameof(BaselineText));
        PinBaselineCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True while there is a previous run to compare against.</summary>
    public bool HasBaseline => Baseline is not null;

    /// <summary>R-rail23-3d — true while a baseline is held across further runs.</summary>
    public bool IsBaselinePinned => Baseline is { Pinned: true };

    /// <summary>What the Compare menu's second row reads, or empty.</summary>
    public string BaselineText => Baseline?.Describe() ?? "";

    /// <summary>
    /// <b>Pin this result</b> — R-rail23-3d's one control.
    /// </summary>
    /// <remarks>
    /// A baseline taken on every run is lost as soon as you run twice, so the loop <i>"unmount, run,
    /// unmount another, run"</i> would compare each cut against the previous cut rather than against
    /// the fitted board. Pinning holds the run that is ON SCREEN — the fitted board, at the moment
    /// the user decides it is the thing worth measuring against — and further runs then leave it
    /// alone.
    ///
    /// <para>Pressing it again unpins, and the baseline reverts to following each run. It is not
    /// cleared: the pinned result IS the previous run until another one completes, and discarding it
    /// on an unpin would throw away an answer the user asked to keep for no reason anyone could
    /// see.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPinBaseline))]
    private void PinBaseline()
    {
        if (IsBaselinePinned) { Baseline = Baseline! with { Pinned = false }; return; }
        if (_lastCompleted is not { } run) return;

        Baseline = run with { Pinned = true };
    }

    /// <summary>Pinning needs a completed run, or a pin to undo.</summary>
    public bool CanPinBaseline => _lastCompleted is not null || IsBaselinePinned;

    /// <summary>
    /// Called once per ACCEPTED result: the run that was on screen becomes the baseline, and this
    /// one takes its place.
    /// </summary>
    /// <remarks>
    /// <b>The pin is what this respects</b> (R-rail23-3d): a pinned baseline is not replaced, so
    /// three successive cuts each measure against the board the user pinned rather than against
    /// each other.
    /// </remarks>
    private void CaptureBaseline(PdnModelKind kind)
    {
        if (SelectedRail is not { } rail) return;
        if (CurrentSide() is not { } side) return;

        var taken = new RailRunBaseline(
            SnapshotDocument(), rail.Name, kind, side, DateTime.UtcNow);

        if (!IsBaselinePinned) Baseline = _lastCompleted;
        _lastCompleted = taken;

        PinBaselineCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPinBaseline));
    }

    /// <summary>
    /// A deep copy of the live document, through its own reader and writer.
    /// </summary>
    /// <remarks>
    /// <b>Unvalidated, and through the file format rather than a hand-written clone</b> — the same
    /// route <c>RailClipboard</c> takes, for the same two reasons. A hand-written clone is a second
    /// list of every field that must be kept in step with <c>RailDocumentIo</c>, and it would lose
    /// exactly the field somebody adds next; and validation is the wrong gate here, because what is
    /// being copied is a document that was solved seconds ago rather than one going to disk.
    /// </remarks>
    private RailDocument SnapshotDocument() =>
        RailDocumentIo.DeserializeUnvalidated(RailDocumentIo.SerializeUnvalidated(_document));

    /// <summary>
    /// The baseline as a comparison, against the run on screen — <b>R-rail23-3b</b>.
    /// </summary>
    /// <remarks>
    /// <b>Everything downstream is <c>RailComparison</c>'s and unchanged</b>: the same match, the
    /// same report, the same per-part table. The only thing this feature supplies is the second
    /// side, and the only thing it adds to the page is the list of what differed in the INPUTS,
    /// which <c>RailComparisonReport.InputChanges</c> computes from the match for any two documents.
    /// </remarks>
    /// <returns>The report, or null where there is no baseline or nothing on screen to compare.</returns>
    public RailComparisonReport? CompareAgainstBaseline()
    {
        if (Baseline is not { } baseline) return null;
        if (CurrentSide() is not { } target || SelectedRail is not { } rail) return null;

        // §6's scope, and it is the same rule the two-document path follows: +1V8 against +1V8.
        // A baseline of another rail is not a comparison, so it is refused by name rather than
        // matched against whatever is showing.
        if (!string.Equals(baseline.RailName, rail.Name, StringComparison.OrdinalIgnoreCase))
            return null;

        var match = RailComparison.Match(
            baseline.Document, _document, rail.Name,
            baseline.Pinned ? "the pinned result" : "the previous run",
            "this run");

        return RailComparisonReport.Build(match, baseline.Side, target);
    }
}
