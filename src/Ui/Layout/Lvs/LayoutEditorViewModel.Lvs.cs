// Layout-versus-schematic, from the editor's side — brief-lvs-12-gui.md, docs/design/lvs.md §8.3.
//
// This file is `LayoutEditorViewModel.Drc.cs` with one check swapped for another, and that is
// deliberate (R-lvs12-1a): the panel is the DRC panel's pattern, not a new one. A list, click to
// zoom, severity grouping, a run button, a summary strip — and R16b's rule, which is R-lvs12-1d
// here: LVS NEVER BLOCKS EDITING. It runs on demand, reports, and gets out of the way.
//
// ── NOTHING IN THIS FILE COMPUTES A FINDING (R-lvs12-1c, R-lvs12-5a) ─────────────────────────
//
// `RunLvs` calls `LvsRun.Run` — the same function `circuitrf lvs` calls, with the same arguments —
// and everything below it consumes the finished `LvsRunResult`. No extraction, no comparison, no
// geometry above the wall. Brief 11's gate asserts the two results are equal object for object,
// and `tests/Ui.Tests/Lvs/LvsPanelTests.cs` asserts it again from this side.
//
// ── WHY IT READS THE CELL FOLDER RATHER THAN THIS DOCUMENT'S OWN MODEL ───────────────────────
//
// The verb takes a cell folder and both surfaces must pass the same arguments, so this does too.
// The consequence is worth stating rather than hiding: an LVS result describes what is ON DISK. A
// document edited since is not what was compared, which is exactly what R-lvs12-3d already
// requires the panel to say — so the staleness mark serves both facts with one sentence.

using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Render;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// The last comparison, or null when none has run. <b>Not cleared on an edit</b> — unlike a DRC
    /// result, which is (see <see cref="DrcResult"/>).
    /// </summary>
    /// <remarks>
    /// <b>The difference is R-lvs12-3d and it is the whole reason this is a separate flag.</b> A
    /// DRC result is only ever about geometry, so geometry that moved makes it meaningless and
    /// dropping it is right. An LVS result also carries the CORRESPONDENCE — what matched what —
    /// which is the thing cross-probing runs on and the thing a user is most likely to still want
    /// after nudging a pad. So the result is kept, <see cref="IsLvsStale"/> goes true, the panel
    /// says so, and cross-probing refuses rather than highlighting against a result that no longer
    /// describes the design.
    /// </remarks>
    /// <remarks>
    /// <b>Hand-written, and it notifies UNCONDITIONALLY — that is the point of not using
    /// <c>[ObservableProperty]</c> here.</b> <see cref="LvsRunResult"/> is a record, so the
    /// generated setter's equality check would compare two results STRUCTURALLY and skip the
    /// notification when they matched. Two structurally equal results are not the same answer: one
    /// taken before an edit and one after are a stale result and a fresh one, and un-waiving the
    /// last waiver produces a findings list that is equal to the one before the waiver existed.
    /// Both are exactly the cases the panel most needs to refresh for, and both would have been
    /// silently dropped.
    /// </remarks>
    private LvsRunResult? _lvsResult;

    /// <inheritdoc cref="_lvsResult"/>
    public LvsRunResult? LvsResult
    {
        get => _lvsResult;
        set
        {
            _lvsResult = value;
            OnPropertyChanged(nameof(LvsResult));
            OnLvsResultChanged(value);
        }
    }

    /// <summary>Draw finding markers over the artwork. A view preference — never persisted, never on
    /// the undo stack, mirroring <see cref="ShowDrcMarkers"/> exactly.</summary>
    [ObservableProperty] private bool _showLvsMarkers = true;

    /// <summary>R-lvs12-3d: the design has changed since the comparison ran.</summary>
    [ObservableProperty] private bool _isLvsStale;

    /// <summary>One row per finding, waived ones included and marked (R-lvs12-4a).</summary>
    public ObservableCollection<LvsFindingRow> LvsFindings { get; } = [];

    /// <summary>R-lvs12-4d: every waiver on this document that the last run matched nothing for,
    /// carrying the finding's text at the time of waiving so a human can recognise it.</summary>
    public ObservableCollection<LvsOrphanWaiverRow> LvsOrphanWaivers { get; } = [];

    [ObservableProperty] private LvsFindingRow? _selectedLvsFinding;

    /// <summary>
    /// The schematic editor for this cell's drawing, when one is open — installed by the workspace,
    /// never persisted, exactly as <see cref="WireDesign"/> is.
    /// </summary>
    /// <remarks>
    /// <b>Cross-probing needs both views and only one of them can own the code</b> (R-lvs12-3a).
    /// Putting it here rather than on the panel keeps the panel a view of this view model, like the
    /// DRC one, and keeps the cross-probe testable with no window in reach. Null is ordinary: the
    /// drawing is simply not open, and every probe below says so rather than highlighting nothing.
    /// </remarks>
    [ObservableProperty] private SchematicViewModel? _lvsSchematic;

    public bool HasLvsResult => LvsResult is not null;

    /// <summary>R-lvs12-1b: what the run actually did, in one strip.</summary>
    /// <remarks>
    /// <b>Device and net counts per side, before and after the collapse, and the reduction mode.</b>
    /// A user reading "32 devices" against "8 devices" needs to see the merge that explains it, and
    /// a result whose reduction mode is not on its face is one two people read differently.
    /// </remarks>
    public string LvsSummaryText => LvsResult is not { } r
        ? "Not compared."
        : (r.IsClean
              ? $"The artwork implements the drawing. {r.Findings.Count} line(s)"
              : $"{r.ErrorCount} error(s), {r.WarningCount} warning(s)")
          + (r.WaivedCount > 0 ? $", {r.WaivedCount} waived" : "")
          + $" — {r.Counts.Describe()}; reduction "
          + (r.Reduction == ReductionMode.On ? "on." : "off.");

    /// <summary>
    /// Which technology the layout was read against, <b>named rather than assumed</b> (R-lvs12-1b).
    /// </summary>
    /// <remarks>
    /// The same thing <see cref="DrcTechnologyText"/> exists for, and it costs nothing: a workspace
    /// holding two processes has a default that may not be the one the designer has in mind, and a
    /// comparison made against the wrong stackup reads exactly like one made against the right one.
    /// </remarks>
    public string LvsTechnologyText => LvsResult?.TechnologyName is { Length: > 0 } n
        ? $"Read against \"{n}\"."
        : LvsResult is null ? "" : "No technology resolved.";

    /// <summary>R-lvs12-3d's own sentence, empty while the result still describes the design.</summary>
    public string LvsStaleText =>
        !IsLvsStale                 ? ""
      : _lvsComparedUnsavedDocument ? "This compared the SAVED .clay and .csch — the open document "
                                    + "has changes that were not in them. Save and run again to "
                                    + "compare what you are looking at. Cross-probing is off."
      :                               "The design has changed since this comparison. Cross-probing "
                                    + "is off until it is run again.";

    /// <summary>
    /// Whether the run in hand read files the open document had already moved on from — see
    /// <see cref="RunLvs"/>. Not a second staleness: it sets the same flag, and only the sentence
    /// differs, because what the user has to DO about it is different.
    /// </summary>
    private bool _lvsComparedUnsavedDocument;

    private void OnLvsResultChanged(LvsRunResult? value)
    {
        LvsFindings.Clear();
        LvsOrphanWaivers.Clear();

        if (value is not null)
        {
            foreach (var f in value.Findings) LvsFindings.Add(new LvsFindingRow(f, this));
            foreach (var w in LvsWaivers.Orphans(Model.LvsWaivers, value.Findings))
                LvsOrphanWaivers.Add(new LvsOrphanWaiverRow(w, this));
        }

        SelectedLvsFinding = null;
        IsLvsStale = false;
        NotifyLvsSurface();
        RebuildOverlay();
    }

    partial void OnIsLvsStaleChanged(bool value)
    {
        OnPropertyChanged(nameof(LvsStaleText));
        OnPropertyChanged(nameof(CanCrossProbeLvs));
    }

    partial void OnShowLvsMarkersChanged(bool value) => RebuildOverlay();

    partial void OnSelectedLvsFindingChanged(LvsFindingRow? value)
    {
        RebuildOverlay();

        // R-lvs12-3a: selecting a finding selects its objects in BOTH open views. Selecting a row is
        // the gesture, not double-clicking it — the zoom is what double-click is for, and a user
        // walking the list with the arrow keys wants to see what each line is about.
        if (value is not null) SelectFindingObjects(value.Finding);
    }

    /// <summary>R-lvs12-3d: cross-probing is refused against a stale correspondence.</summary>
    public bool CanCrossProbeLvs => LvsResult is not null && !IsLvsStale;

    private void NotifyLvsSurface()
    {
        OnPropertyChanged(nameof(LvsSummaryText));
        OnPropertyChanged(nameof(LvsTechnologyText));
        OnPropertyChanged(nameof(HasLvsResult));
        OnPropertyChanged(nameof(LvsStaleText));
        OnPropertyChanged(nameof(CanCrossProbeLvs));
    }

    // ── The run (R-lvs12-1c) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares this document's cell folder — <b>one call to <see cref="LvsRun.Run(string,
    /// LvsRunOptions?, RunControl?)"/> and nothing else</b>.
    /// </summary>
    /// <param name="options">What was asked for; the verb's own defaults when null.</param>
    /// <param name="control">Progress and cancellation. A cancelled run throws and changes
    /// nothing.</param>
    /// <returns>Null when this document has no cell folder to compare — a scratch layout, or a
    /// loose <c>.clay</c> saved outside one. Reported, never thrown.</returns>
    public LvsRunResult? RunLvs(LvsRunOptions? options = null, RunControl? control = null)
    {
        if (CurrentCellDir is not { Length: > 0 } cell)
        {
            ReportWarning("LVS compares a CELL — its layout against its schematic — and this "
                        + "document does not belong to a cell folder. Save it into one first.");
            return null;
        }

        // What the verb reads is what is ON DISK (see this folder's RESOLVED.md §3), so a run made
        // over an edited document did not compare that document. The staleness mark already exists
        // for "edited SINCE the run" and this is the same fact one moment earlier — the one case
        // the mark would otherwise miss, because a fresh result clears it.
        bool unsaved = IsDirty;

        var result = LvsRun.Run(cell, options, control);
        LvsResult = result;                       // clears IsLvsStale and the sentence with it
        _lvsComparedUnsavedDocument = unsaved;
        if (unsaved) IsLvsStale = true;
        return result;
    }

    /// <summary>
    /// R-lvs12-3d. Called from the model's own change notification, beside
    /// <see cref="ClearDrcResultOnEdit"/> — and it MARKS rather than clears, for the reason on
    /// <see cref="LvsResult"/>.
    /// </summary>
    internal void MarkLvsStaleOnEdit()
    {
        if (LvsResult is not null && !IsLvsStale) IsLvsStale = true;
    }

    /// <summary>The same mark, for a change the layout's own model cannot see — the SCHEMATIC being
    /// edited, which invalidates a correspondence just as thoroughly.</summary>
    public void MarkLvsStale() => MarkLvsStaleOnEdit();

    // ── Waivers (R-lvs12-4) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waives (or un-waives) one finding, with a reason. Marks the document dirty so the decision is
    /// saved; deliberately NOT undoable — <see cref="LayoutView.LvsWaivers"/>' own rule, which is
    /// <c>DrcWaiver</c>'s verbatim (R-lvs12-4a).
    /// </summary>
    /// <remarks>
    /// <b>This is the USER writing their own document, and the RUN still writes nothing</b>
    /// (R-lvs12-4g). The waiver lands on the in-memory <see cref="LayoutView"/> and is saved when
    /// the user saves; <see cref="LvsRun"/> only ever reads the list.
    /// </remarks>
    public void SetLvsWaived(LvsFindingRow row, bool waived, string reason = "")
    {
        ArgumentNullException.ThrowIfNull(row);
        string key = LvsWaiverKey.For(row.Finding);
        int existing = Model.LvsWaivers.FindIndex(w => string.Equals(w.Key, key, StringComparison.Ordinal));

        if (waived)
        {
            if (existing >= 0) Model.LvsWaivers[existing].Reason = reason;
            else Model.LvsWaivers.Add(new LvsWaiver
            {
                Key         = key,
                Reason      = reason,
                FindingText = row.Finding.Render(),
            });
        }
        else if (existing >= 0)
        {
            Model.LvsWaivers.RemoveAt(existing);
        }
        else return;   // nothing to do; never dirty the document for a no-op

        IsDirty = true;
        ReapplyLvsWaivers();
    }

    /// <summary>Removes an orphaned waiver — R-lvs12-4d's other half.</summary>
    public void RemoveLvsWaiver(LvsWaiver waiver)
    {
        ArgumentNullException.ThrowIfNull(waiver);
        int existing = Model.LvsWaivers.FindIndex(w => string.Equals(w.Key, waiver.Key, StringComparison.Ordinal));
        if (existing < 0) return;

        Model.LvsWaivers.RemoveAt(existing);
        IsDirty = true;
        ReapplyLvsWaivers();
    }

    /// <summary>
    /// Re-marks the result in hand rather than re-running it.
    /// </summary>
    /// <remarks>
    /// <c>SetWaived</c>'s own reasoning: waiving is a statement about a finding that has ALREADY
    /// been found, and re-comparing would be both slow and — on a design edited since — a different
    /// answer to a question the user did not ask.
    /// </remarks>
    private void ReapplyLvsWaivers()
    {
        if (LvsResult is not { } r) return;

        bool stale = IsLvsStale;
        var selectedKey = SelectedLvsFinding?.Finding.Key;

        LvsResult = r with { Findings = LvsWaivers.Apply(r.Findings, Model.LvsWaivers) };

        // OnLvsResultChanged resets both, and neither reset is true here: no comparison ran.
        IsLvsStale = stale;
        if (selectedKey is not null)
            SelectedLvsFinding = LvsFindings.FirstOrDefault(
                row => string.Equals(row.Finding.Key, selectedKey, StringComparison.Ordinal));
    }

    // ── Markers and zoom (R-lvs12-2) ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void ZoomToSelectedFinding()
    {
        // R-lvs12-2d: an open's Marker is the union of every island's rings, so "zoom to fit ALL of
        // them" needs nothing of its own — seeing the pieces relative to each other IS the report.
        if (SelectedLvsFinding is { } row && !row.Finding.Marker.IsEmpty)
            RequestZoomToRegion(row.Finding.Marker);
    }

    /// <summary>R-lvs12-2b: the rings come off the finding; nothing here recomputes geometry.</summary>
    private IReadOnlyList<LvsFindingMarker> BuildLvsMarkers()
    {
        if (!ShowLvsMarkers || LvsResult is not { Findings.Count: > 0 } r) return [];

        var selectedKey = SelectedLvsFinding?.Finding.Key;
        var markers = new List<LvsFindingMarker>();
        foreach (var f in r.Findings)
        {
            if (!f.HasMarker) continue;
            markers.Add(new LvsFindingMarker(
                f.MarkerRings, f.Severity, f.Waived,
                selectedKey is not null && string.Equals(f.Key, selectedKey, StringComparison.Ordinal)));
        }
        return markers;
    }
}
