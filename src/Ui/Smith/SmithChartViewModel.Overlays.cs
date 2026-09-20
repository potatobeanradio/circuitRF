using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The overlays list and the markers — <b>the reference material under the work</b>
/// (<c>brief-smith-8-overlays-markers.md</c>; <c>docs/design/smith-chart.md</c> §5.7, §4.5).
/// </summary>
/// <remarks>
/// <b>None of this is needed to match an impedance, which is why it is P2</b> — and all of it is
/// what makes a match checkable against the part it is matching.
///
/// <para><b>Nothing here is a second anything.</b> An overlay is an ordinary Data Display
/// <c>Trace</c> resolved by <see cref="SmithOverlayResolver"/> through the existing machinery — the
/// SNP path, the cube path and the derived path, stability circles included. A marker is the Data
/// Display's own <c>Marker</c> on this <c>Plot</c>, which is why placement, drag, hit-test, the info
/// box, the context menu, the editor and the VSWR circle all arrived with brief 5's hosting and this
/// file only has to persist them.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    // ── the overlay rows ─────────────────────────────────────────────────────

    /// <summary>The list, one view model per document row, in the document's own order.</summary>
    public ObservableCollection<SmithOverlayRowViewModel> OverlayRows { get; } = [];

    /// <summary>Which row <c>[−]</c> removes. Null when nothing is selected.</summary>
    public SmithOverlayRowViewModel? SelectedOverlay
    {
        get => _selectedOverlay;
        set
        {
            if (ReferenceEquals(_selectedOverlay, value)) return;
            _selectedOverlay = value;
            OnPropertyChanged();
            RemoveOverlayCommand.NotifyCanExecuteChanged();
        }
    }
    private SmithOverlayRowViewModel? _selectedOverlay;

    /// <summary>
    /// Where a <see cref="SmithOverlaySource.Cube"/> overlay's data comes from.
    /// </summary>
    /// <remarks>
    /// <b>Supplied by the host, and null is an honest answer.</b> A scratch `.csmith` opened with no
    /// workspace has no data sets open, and a cube row in one says so rather than throwing. When a
    /// workspace IS open the shell hands over the same <c>IPlotDataSources</c> seam a Data Display
    /// resolves through and <c>circuitrf render</c> implements over the files a caller named — so a
    /// cube overlay and a `.cdd` trace over the same run resolve through one lookup.
    /// </remarks>
    public IPlotDataSources? OverlayDataSources
    {
        get => _overlayDataSources;
        set { _overlayDataSources = value; RefreshDerived(); }
    }
    private IPlotDataSources? _overlayDataSources;

    /// <summary>
    /// The file picker <b>Add overlay</b> opens, supplied by the view. Returns a path RELATIVE to
    /// the document where that is possible — the `.cdd` convention, and the one that survives an
    /// archived or moved workspace.
    /// </summary>
    public Func<Task<string?>>? OverlayFileChooser { get; set; }

    /// <summary>Rebuilds the rows from the document — called on every load, undo and snapshot
    /// restore, for <see cref="ApplySnapshot"/>'s reason: the design object is REPLACED, so a row
    /// holding the old <c>SmithOverlayRef</c> would be editing a document nobody can see.</summary>
    private void RebuildOverlayRows()
    {
        string? selected = _selectedOverlay?.Overlay.Source;

        OverlayRows.Clear();
        foreach (var o in _design.Overlays) OverlayRows.Add(new SmithOverlayRowViewModel(this, o));

        _selectedOverlay = OverlayRows.FirstOrDefault(
            r => string.Equals(r.Overlay.Source, selected, StringComparison.Ordinal));

        OnPropertyChanged(nameof(SelectedOverlay));
        OnPropertyChanged(nameof(HasOverlays));
        RemoveOverlayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when the document carries any reference material at all — what the view hides
    /// the empty list behind.</summary>
    public bool HasOverlays => _design.Overlays.Count > 0;

    /// <summary>One committed edit to one row. <see cref="EditGeneratorRow"/>'s shape.</summary>
    internal void EditOverlay(SmithOverlayRowViewModel row, string description,
                              Action<SmithOverlayRef> mutate)
    {
        Edit(description, () => mutate(row.Overlay));
        row.NotifyAll();
    }

    /// <summary><b>Add overlay</b> — a Touchstone file, by a path relative to the document.</summary>
    /// <remarks>
    /// <b>Referenced and not copied, which is the opposite of the generator's <c>.s1p</c> import
    /// (<c>R-smith8-2</c>), and deliberately.</b> An overlay is reference material the user is
    /// comparing against, so a reference is right and a stale copy would be wrong; the generator is
    /// part of the design, so a copy is right and a broken path would be fatal.
    /// </remarks>
    [RelayCommand]
    private async Task AddOverlay()
    {
        string? path = OverlayFileChooser is null ? null : await OverlayFileChooser();
        if (string.IsNullOrWhiteSpace(path))
        {
            StripNotice = "No overlay was added — an overlay IS a reference to data somewhere, and "
                        + "there is nothing for one with no source to draw.";
            return;
        }

        var overlay = new SmithOverlayRef
        {
            SourceKind = SmithOverlaySource.TouchstoneFile,
            Source     = path,
            Quantity   = "S11",
        };

        Edit($"Add overlay {Path.GetFileName(path.Replace('\\', '/'))}",
             () => _design.Overlays.Add(overlay));

        SelectedOverlay = OverlayRows.LastOrDefault();
    }

    /// <summary>
    /// <b>Add overlay</b> — a cube in an open data set, referenced the way a Data Display trace card
    /// references one.
    /// </summary>
    /// <remarks>
    /// <b>The chooser is the host's and the decision is not</b>, which is what keeps the whole path
    /// drivable with no display: this takes a resolved source reference and a quantity, exactly as
    /// <see cref="ImportGeneratorFrom"/> takes a resolved path.
    /// </remarks>
    public void AddCubeOverlay(string sourceRef, string quantity)
    {
        var overlay = new SmithOverlayRef
        {
            SourceKind = SmithOverlaySource.Cube,
            Source     = sourceRef,
            Quantity   = string.IsNullOrWhiteSpace(quantity) ? "S11" : quantity.Trim(),
        };

        Edit($"Add overlay {sourceRef}", () => _design.Overlays.Add(overlay));
        SelectedOverlay = OverlayRows.LastOrDefault();
    }

    private bool CanRemoveOverlay() => SelectedOverlay is not null;

    /// <summary><b>Remove</b> — the selected row. Nothing else changes: an overlay is not part of
    /// the cascade, so removing one takes no element with it.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveOverlay))]
    private void RemoveOverlay()
    {
        if (SelectedOverlay?.Overlay is not { } target) return;

        int index = _design.Overlays.IndexOf(target);
        if (index < 0) return;

        _selectedOverlay = null;
        Edit($"Remove overlay {SmithOverlayResolver.Label(target)}",
             () => _design.Overlays.RemoveAt(index));
    }

    // ── resolution (R-smith8-2, R-smith8-3) ──────────────────────────────────

    /// <summary>
    /// Resolves every overlay row, marking the ones that could not be.
    /// </summary>
    /// <remarks>
    /// <b>A reference that does not resolve marks its row and stops there.</b> The document still
    /// opens, the rest of the chart still draws, and the sentence names the path — which is the
    /// opposite of an S1P ELEMENT, whose missing file is a refusal because the cascade cannot be
    /// walked without it.
    ///
    /// <para><b>The colour index continues the trajectories' own run</b> rather than restarting, so
    /// an overlay never comes up in the same colour as the element it is being compared with.</para>
    /// </remarks>
    private List<SmithOverlayTrace> ResolveOverlays()
    {
        var resolved = new List<SmithOverlayTrace>();
        if (_design.Overlays.Count == 0) return resolved;

        double z0 = _design.Chart.Z0Ohm;

        for (int i = 0; i < _design.Overlays.Count; i++)
        {
            var overlay = _design.Overlays[i];
            var row     = i < OverlayRows.Count ? OverlayRows[i] : null;

            SmithOverlayResolver.Resolution result;
            try
            {
                result = SmithOverlayResolver.Resolve(
                    overlay, DocumentDirectory, z0, _overlayDataSources,
                    SmithPlotBuilder.ColorIndexFor(_design.Elements.Count + i));
            }
            catch (Exception ex)
            {
                // Resolving reference material is a READ, and an unforeseen failure in one has no
                // business taking the document down with it — TraceResolve's own containment, for
                // its own reason.
                result = SmithOverlayResolver.Resolution.No(
                    $"'{overlay.Source}' could not be read: {ex.Message}");
            }

            if (row is not null) row.Unresolved = result.Unresolved;

            if (result.Trace is { } trace)
                resolved.Add(new SmithOverlayTrace(
                    SmithOverlayResolver.Label(overlay), trace, overlay.Visible));
        }

        return resolved;
    }

    // ── markers (R-smith8-5) ─────────────────────────────────────────────────

    /// <summary>The trace keys the last rebuild produced — what a marker is stored against.</summary>
    private IReadOnlyList<SmithTraceKey> _traceKeys = [];

    /// <summary>
    /// Writes the markers now on the chart back into the document.
    /// </summary>
    /// <remarks>
    /// <b>Called from the view on every marker event</b>, because the Data Display's own gestures
    /// are what place, move and edit them — this window adds no marker code of its own and only has
    /// to notice the result.
    ///
    /// <para><b>A placement or a removal is one undo entry; a DRAG is not.</b> That split is the
    /// splitters' and the chart window's own rule (<c>R-smith4-3</c>): <c>PlotControl</c> raises
    /// <c>MarkerMoved</c> on every pointer move and has no drag-finished event to push against, so
    /// an entry per move would be the Match Designer's "eight edits took fourteen undos" defect by a
    /// slower route. A move therefore rides along on the next real save, and the count of markers —
    /// which is what a placement or a removal changes — is what makes the difference here.</para>
    /// </remarks>
    public void HarvestMarkers()
    {
        if (_traceKeys.Count == 0) return;

        var harvested = SmithPlotBuilder.HarvestMarkers(_traceKeys);

        // A PLACEMENT or a REMOVAL changes the count and is one undo entry. A drag, a VSWR toggle or
        // a format change does not, and rides along on the next real save — the splitters' and the
        // chart window's own rule (R-smith4-3), taken here because PlotControl raises MarkerMoved on
        // every pointer move and has no drag-finished event to push against. An entry per move would
        // be the Match Designer's "eight edits took fourteen undos" defect by a slower route.
        if (harvested.Count != _design.Markers.Count)
            Edit(harvested.Count > _design.Markers.Count ? "Add marker" : "Remove marker",
                 () => Replace(harvested));
        else
            Replace(harvested);

        void Replace(List<SmithMarker> markers)
        {
            _design.Markers.Clear();
            foreach (var m in markers) _design.Markers.Add(m);
        }
    }

    /// <summary>
    /// <b>Delete</b> on the chart: removes every selected marker, and harvests
    /// (owner report, 2026-09-19 — the key did nothing).
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <c>DataDisplayViewModel.DeleteSelected</c></b>, for the Match Designer's
    /// own reason: that also removes selected PLOT CONTAINERS, and this document's one chart is not
    /// deletable — the AXAML sets <c>CanDeletePlot="False"</c> because every trace on it is rebuilt
    /// from the design on each edit. A gesture that could silently take the chart with the marker
    /// would be worse than no gesture.
    ///
    /// <para><b>The harvest is the second half and is not optional.</b> The document is the
    /// authority for the marker set (see <see cref="HarvestMarkers"/>), so a marker taken off a
    /// trace and not written back is re-attached on the very next rebuild.</para>
    /// </remarks>
    [RelayCommand]
    public void DeleteSelectedMarkers()
    {
        var boxes = ChartContainer.GetMarkerInfoBoxes().Where(b => b.IsSelected).ToList();
        if (boxes.Count == 0) return;

        foreach (var box in boxes)
            box.Container.RemoveMarkerWithUndo(box.Marker, box.Trace);

        HarvestMarkers();
    }

    /// <summary>
    /// <b>Escape</b>: drops the marker selection and the network strip's element selection
    /// (owner instruction, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>Both, because the window has two selections and one key.</b> A marker's selection lives on
    /// its info box — it is what the Delete above acts on and what the glyph highlight is drawn from
    /// — and the element's is this view model's own single integer. Neither is an edit: no undo
    /// entry and no dirty mark.
    ///
    /// <para><b>Not the same key as an abandoned drag.</b> <c>PlotControl.OnKeyDown</c> consumes
    /// Escape while an overlay gesture is in flight and restores the before-value, which is
    /// <c>R-smith5-8</c>; this only ever runs on the Escape that reaches the document, which is the
    /// one with nothing being dragged.</para>
    /// </remarks>
    [RelayCommand]
    public void ClearSelection()
    {
        foreach (var box in ChartContainer.GetMarkerInfoBoxes())
            box.IsSelected = false;

        SelectElement(-1);
        ChartContainer.RequestPlotRedraw();
    }
}
