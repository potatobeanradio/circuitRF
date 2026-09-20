using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The overlays and the markers — <b>the reference material under the work</b>
/// (<c>brief-smith-12-overlays-via-the-inspector.md</c>; <c>docs/design/smith-chart.md</c> §5.7,
/// §4.5).
/// </summary>
/// <remarks>
/// <b>There is no overlays panel and there must not be one.</b> Reference data goes onto this chart
/// the way it goes onto a Smith chart in a Data Display: pick a source in the strip above the chart,
/// open <b>Plot Properties…</b>, press <b>Add</b>, and edit the trace card. Brief 8 built a second,
/// smaller version of that card in a side panel — seven properties where the card has thirty — and
/// a panel left in place beside the inspector would be two authors of one list.
///
/// <para><b>What this file is, then, is the DOCUMENT half of that.</b>
/// <c>SmithPlotBuilder.Fill</c> clears the plot's traces and refills them from the design on every
/// committed edit, so a trace added in the inspector would be gone by the next keystroke and one
/// removed would be back — which is exactly why brief 5 closed the trace set in the first place.
/// Three things reopen it: the plot says so (<c>Plot.AllowUserTraces</c>), the trace INSTANCES are
/// carried across the rebuild rather than re-resolved (<c>R-smith12-5a</c> — the inspector's cards,
/// its selection and the trace's markers all hold the object), and the set is harvested back into
/// the `.csmith` as the Data Display's own trace configs (<c>R-smith12-4</c>).</para>
///
/// <para><b>Nothing here resolves or writes a trace.</b> <c>PlotConfigLoader.LoadTrace</c> is the
/// one reader and <c>DataDisplayViewModel.BuildTraceConfig</c> is the one writer, and both are the
/// `.cdd`'s.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    // ── where the data is (R-smith12-3) ──────────────────────────────────────

    /// <summary>
    /// The host's own data sources — what a CUBE overlay resolves against.
    /// </summary>
    /// <remarks>
    /// <b>Supplied by the shell, and null is an honest answer.</b> A scratch `.csmith` opened with no
    /// workspace has no data sets open, and that has to keep working — it is what makes this a real
    /// document rather than a workspace feature. When a workspace IS open the shell hands over the
    /// same <c>IPlotDataSources</c> seam a Data Display's trace cards resolve through, so a cube
    /// overlay and a `.cdd` trace over the same run go through ONE lookup.
    ///
    /// <para>A Touchstone overlay needs none of it: that one is a path relative to the document, and
    /// <see cref="OverlaySources"/> is what resolves it either way.</para>
    /// </remarks>
    public IPlotDataSources? OverlayDataSources
    {
        get => _overlayDataSources;
        set
        {
            _overlayDataSources = value;
            _overlaySources     = null;
            // FORCED: the document's overlay list has not changed, but what it RESOLVES against
            // has, so the guard in ReloadOverlays would leave the old answers standing.
            ReloadOverlays(force: true);
        }
    }
    private IPlotDataSources? _overlayDataSources;

    /// <summary>
    /// The document's own sources: the host's library first, then files beside the `.csmith`.
    /// </summary>
    /// <remarks>
    /// <b>The relative-path convention is brief 8's and is kept</b> (<c>R-smith8-2</c>): an overlay
    /// is a REFERENCE and never a copy, by a path relative to the document wherever that is
    /// possible, so the pair moves together and the reference still resolves. That is the opposite
    /// choice from the generator's `.s1p` import, deliberately — the generator is part of the design
    /// so a copy is right there, and reference material the user is comparing against would be wrong
    /// as a stale copy.
    /// </remarks>
    internal IPlotDataSources OverlaySources =>
        _overlaySources ??= new SmithDocumentSources(() => DocumentDirectory, _overlayDataSources);
    private IPlotDataSources? _overlaySources;

    // ── the source combo, in the chart's own top strip ───────────────────────

    /// <summary>The sources the strip's combo lists — the library's own collection, not a copy.</summary>
    public ObservableCollection<DataSourceItem> AvailableDataSources =>
        PlotHost.Library?.AvailableDataSources ?? _noSources;
    private readonly ObservableCollection<DataSourceItem> _noSources = [];

    /// <summary>
    /// Bound two-way to the strip's combo — <c>DisplayWindowViewModel.SelectedDataSourceItem</c>'s
    /// own shape, because <b>Add</b> seeds from <c>DataSourceLibraryViewModel.SelectedEntry</c> and
    /// without a way to set one the button would add nothing and say nothing about why.
    /// </summary>
    public DataSourceItem? SelectedDataSourceItem
    {
        get => PlotHost.Library is { } lib
                   ? lib.AvailableDataSources.FirstOrDefault(i => i.LogicalId == lib.SelectedDataSourceRef)
                   : null;
        set
        {
            if (value is null || PlotHost.Library is not { } lib) return;
            _ = lib.SelectDataSourceAsync(value.LogicalId);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDataSourceAbs));
        }
    }

    /// <summary>What the combo's tooltip shows — the file the selection actually names.</summary>
    public string? SelectedDataSourceAbs => PlotHost.Library?.SelectedDataSourceAbs;

    /// <summary>Re-enumerates the sources without loading any file. The shell calls it when the
    /// workspace's results change.</summary>
    public void RefreshAvailableDataSources()
    {
        PlotHost.Library?.RefreshAvailableDataSources();
        OnPropertyChanged(nameof(SelectedDataSourceItem));
        OnPropertyChanged(nameof(SelectedDataSourceAbs));
    }

    /// <summary>
    /// The file picker the combo's <b>Add from file…</b> row opens, supplied by the view.
    /// </summary>
    /// <remarks>
    /// <b>The same picker the Overlays panel used, wired to the library instead</b>
    /// (<c>R-smith12-3</c>) — so a Touchstone can be loaded and drawn with nothing else open, which
    /// is the scratch document's whole case. It returns an ABSOLUTE path, because that is what the
    /// library loads; the document-relative reference is computed on the way OUT, in
    /// <see cref="HarvestOverlays"/>.
    /// </remarks>
    public Func<Task<string?>>? OverlayFileChooser
    {
        get => PlotHost.Library?.AddSourceFileRequested;
        set { if (PlotHost.Library is { } lib) lib.AddSourceFileRequested = value; }
    }

    // ── the user's traces, across a rebuild (R-smith12-5a) ───────────────────

    /// <summary>
    /// The resolved overlay traces, in document order — <b>the same <c>Trace</c> objects from one
    /// rebuild to the next</b>.
    /// </summary>
    /// <remarks>
    /// <b>Not re-resolved per rebuild, and that is a requirement rather than an optimisation.</b>
    /// The inspector's trace cards, its selection and each trace's markers all hold the <c>Trace</c>
    /// OBJECT; replacing it leaves every one of them pointing at a discarded copy. Round three's own
    /// marker test held a stale <c>Trace</c> across one rebuild and removed a marker from nothing.
    ///
    /// <para>They are re-resolved exactly when the DOCUMENT is replaced — a load, an undo, a
    /// snapshot restore — because then the configs they were built from are gone too.</para>
    /// </remarks>
    private List<Trace> _overlayTraces = [];

    /// <summary>Overlay configs that did not resolve, kept verbatim so a harvest cannot drop them.
    /// Paired with the sentence saying why.</summary>
    private List<(JsonElement Json, string Why)> _unresolvedOverlays = [];

    /// <summary>The chart Z₀ <see cref="_overlayTraces"/> are currently referenced to.</summary>
    private double _overlayZ0 = double.NaN;

    /// <summary>
    /// The overlay list <see cref="_overlayTraces"/> were built from, as
    /// <see cref="SmithOverlays.Signature"/> spells it.
    /// </summary>
    /// <remarks>
    /// <b>This is what keeps the trace INSTANCES alive across an ordinary edit</b>
    /// (<c>R-smith12-5a</c>). Every committed edit in this window replaces the whole design through
    /// a snapshot — that is <see cref="SmithSnapshotCommand"/>'s whole design — so "the document was
    /// replaced" is not the question to ask. The question is whether the OVERLAY LIST in it changed,
    /// and only then are the traces rebuilt.
    /// </remarks>
    private string _overlaySignature = "";

    /// <summary>True when some of the document's reference material could not be read.</summary>
    public bool HasUnresolvedOverlays => _unresolvedOverlays.Count > 0;

    /// <summary>
    /// The first unresolved overlay's sentence, for the status strip.
    /// </summary>
    /// <remarks>
    /// <b>A reference that does not resolve says why and stops there</b> (<c>R-smith8-2</c>): the
    /// document still opens, the rest of the chart still draws, and the sentence names the path.
    /// That is the opposite of an S1P ELEMENT, whose missing file is a refusal because the cascade
    /// cannot be walked without it — an overlay's absence costs the user a comparison and nothing
    /// else. It is a STANDING CONDITION and is re-raised on every refresh, like the band's clamp
    /// note, because it stays true until the file comes back.
    /// </remarks>
    public string? UnresolvedOverlayNote => _unresolvedOverlays.Count switch
    {
        0 => null,
        1 => _unresolvedOverlays[0].Why,
        _ => $"{_unresolvedOverlays[0].Why} ({_unresolvedOverlays.Count - 1} more overlay(s) did "
           + "not resolve either.)",
    };

    /// <summary>
    /// Rebuilds <see cref="_overlayTraces"/> from the document. <b>Called when the design object is
    /// REPLACED</b> — construction, load, undo, snapshot restore — and never on an ordinary refresh.
    /// </summary>
    internal void ReloadOverlays(bool force = false)
    {
        // The brief-8 rows, if this document still carries any. Migration needs TraceConfig, which
        // src/Design cannot see, so the reader hands them over and this is where they land
        // (R-smith12-4c). It runs once: the next write is in the new shape and has no old block.
        SmithOverlayMigration.Apply(_design, OverlaySources);

        string signature = SmithOverlays.Signature(_design.Overlays);
        if (!force && signature == _overlaySignature) return;
        _overlaySignature = signature;

        // A SENTENCE RAISED ABOUT THE PREVIOUS RESOLUTION IS VOID. The commonest case is the
        // ordinary open: the design arrives before the document's FOLDER does, so every relative
        // reference fails once and then resolves — and the note from the first attempt would sit in
        // the strip naming a file that is in fact right there. Only the note this file raised is
        // cleared; one somebody else just put up is left alone.
        if (StripNotice is not null && StripNotice == UnresolvedOverlayNote) StripNotice = null;

        _overlayTraces      = [];
        _unresolvedOverlays = [];
        _overlayZ0          = _design.Chart.Z0Ohm;

        foreach (var stored in _design.Overlays)
        {
            string why = "This overlay is not a trace circuitRF can read.";

            if (SmithOverlays.Read(stored) is { } cfg)
            {
                var trace = SmithOverlays.Load(cfg, OverlaySources);
                if (trace is not null) { _overlayTraces.Add(trace); continue; }

                why = OverlaySources is SmithDocumentSources docs
                          && docs.WhyUnresolved(cfg.SourcePath) is { } sentence
                      ? sentence
                      : $"'{cfg.SourcePath}' is not one of the data sets that are open.";
            }

            _unresolvedOverlays.Add((stored, why));
        }
    }

    /// <summary>
    /// The overlay traces, ready for <c>SmithPlotBuilder.Fill</c>.
    /// </summary>
    /// <remarks>
    /// <b>The chart's Z₀ is followed rather than frozen.</b> Every overlay is renormalized to the
    /// document's one reference impedance (<c>R-smith8-3</c>) and the trace's own
    /// <c>Z0OverrideEnabled</c>/<c>Z0</c> pair is the single gate on it — so when the chart's number
    /// changes, a trace still pointing at the OLD one is re-pointed and its path rebuilt. A trace
    /// the user gave a Z₀ of its own is left alone, because that one has already been told what it
    /// is referenced to.
    /// </remarks>
    private List<SmithOverlayTrace> ResolveOverlays()
    {
        double z0 = _design.Chart.Z0Ohm;

        if (_overlayZ0 != z0)
        {
            var was = new Complex(_overlayZ0, 0.0);
            foreach (var t in _overlayTraces)
                if (t.Z0OverrideEnabled && t.Z0 == was)
                {
                    t.Z0 = new Complex(z0, 0.0);
                    RebuildOverlayPath(t);
                }
            _overlayZ0 = z0;
        }

        return [.. _overlayTraces.Select(t => new SmithOverlayTrace(OverlayKey(t), t))];
    }

    /// <summary>The name a marker on <paramref name="trace"/> is stored against — derived from the
    /// trace's own config so it is the same string across a rebuild AND across a reorder
    /// (<c>R-smith12-5c</c>).</summary>
    private string OverlayKey(Trace trace)
        => SmithOverlays.Key(DataDisplayViewModel.BuildTraceConfig(
               trace, DocumentDirectory ?? "", PlotHost.Library));

    private void RebuildOverlayPath(Trace t)
    {
        if (t.IsCubeBound)
            TraceResolve.ResolveCubeTrace(t, OverlaySources, PlotType.Smith, FreqUnit.GHz);
        else
            t.BuildPath(PlotType.Smith, FreqUnit.GHz);
    }

    // ── the harvest (R-smith12-5b) ───────────────────────────────────────────

    /// <summary>
    /// Writes the traces now on the chart back into the document.
    /// </summary>
    /// <remarks>
    /// <b>Subscribed to the inspector's <c>PlotStructureChanged</c></b>, which fires on add, remove
    /// and reorder. Every trace on the plot WITHOUT <c>ExcludeFromAxisLabels</c> is a user trace —
    /// the tool sets that flag on everything it derives — and its <c>BuildTraceConfig</c> is the
    /// document's overlay list.
    ///
    /// <para><b>An add or a remove is ONE undo entry; anything else rides along on the next
    /// save.</b> <see cref="HarvestMarkers"/>' split, for <see cref="HarvestMarkers"/>' reason
    /// (<c>R-smith4-3</c>): an entry per card keystroke is the Match Designer's "eight edits took
    /// fourteen undos" by a slower route.</para>
    ///
    /// <para><b>An overlay that did not resolve is preserved.</b> It has no trace on the plot, so a
    /// harvest that wrote only what it could see would delete a user's reference material because
    /// the file it names happened to be missing. The unresolved ones are re-appended verbatim.</para>
    /// </remarks>
    public void HarvestOverlays()
    {
        var harvested = ChartPlot.Traces
            .Where(t => !t.ExcludeFromAxisLabels)
            .Select(ToStoredOverlay)
            .ToList();

        // The traces the inspector now holds, in ITS order — which a reorder has just changed.
        _overlayTraces = [.. ChartPlot.Traces.Where(t => !t.ExcludeFromAxisLabels)];

        foreach (var (json, _) in _unresolvedOverlays) harvested.Add(json);

        // BEFORE the Edit below, which replaces the design through a snapshot and comes straight
        // back here through ApplySnapshot: the traces that were just harvested ARE this list, so the
        // reload that follows must recognise it and leave them alone.
        _overlaySignature = SmithOverlays.Signature(harvested);

        int before = _design.Overlays.Count;
        if (harvested.Count != before)
            Edit(harvested.Count > before ? "Add overlay" : "Remove overlay", () => Replace(harvested));
        else
            Replace(harvested);

        void Replace(List<JsonElement> overlays)
        {
            _design.Overlays.Clear();
            foreach (var o in overlays) _design.Overlays.Add(o);
        }
    }

    /// <summary>
    /// One live trace as the document stores it.
    /// </summary>
    /// <remarks>
    /// <b>The writer is the `.cdd`'s own and there is no second one</b> (<c>R-smith12-9</c>). The
    /// one thing added afterwards is the reference SPELLING: a source under the document's own
    /// folder is stored relative to it, which is brief 8's convention and the one that survives an
    /// archived or moved workspace. <c>BuildTraceConfig</c> writes the alias relative to the results
    /// ROOT, which is right for a workspace run and cannot reach a file sitting beside a scratch
    /// `.csmith`.
    /// </remarks>
    private JsonElement ToStoredOverlay(Trace trace)
    {
        var cfg = DataDisplayViewModel.BuildTraceConfig(trace, DocumentDirectory ?? "", PlotHost.Library);

        if (DocumentDirectory is { Length: > 0 } dir
            && cfg.SourcePath is { Length: > 0 } sref
            && System.IO.Path.IsPathRooted(sref))
        {
            string rel = System.IO.Path.GetRelativePath(dir, sref);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel))
                cfg.SourcePath = rel.Replace('\\', '/');
        }

        return SmithOverlays.Write(cfg);
    }

    /// <summary>
    /// Seeds a trace the inspector has just added — <b>the two things the Add button cannot know</b>.
    /// </summary>
    /// <remarks>
    /// <b>Renormalization to the chart's Z₀ is a requirement, not an option</b> (<c>R-smith8-3</c>,
    /// <c>R-smith12-7</c>). A 75 Ω part drawn on a 50 Ω chart without it is a curve in the wrong
    /// place that looks entirely plausible — so a new trace seeds with the chart's Z₀ and the
    /// override ON, and the card is where a user who wants the file's own numbers turns it off.
    ///
    /// <para><b>And out of the autoscale</b> (<c>R-smith12-6</c>): a stability circle can be
    /// enormous, and one unlucky overlay should not reframe the work. Also the card's, also
    /// changeable there.</para>
    ///
    /// <para>Seeded ONCE, on the traces this harvest has not seen before — re-applying it on every
    /// harvest would put back the two settings the user had just turned off.</para>
    /// </remarks>
    private void SeedNewOverlay(Trace trace)
    {
        // THE SENTINEL IS NOT A REFERENCE A DOCUMENT CAN KEEP. Add stamps `DataSourceRef.Selected`
        // — "whichever source this display has selected" — which is right for a `.cdd`, where the
        // combo is part of the document and travels with it. A `.csmith` has no such selection to
        // come back to, so an overlay stored against the sentinel would resolve to nothing the next
        // time it was opened. It is pinned to the concrete file here, at the one moment the answer
        // is known.
        if (trace.SourceRef is null or DataSourceRef.Selected
            && PlotHost.Library?.SelectedDataSourceAbs is { Length: > 0 } abs)
            trace.SourceRef = abs;

        trace.Z0                   = new Complex(_design.Chart.Z0Ohm, 0.0);
        trace.Z0OverrideEnabled    = true;
        trace.ExcludeFromAutoscale = true;
        RebuildOverlayPath(trace);
    }

    /// <summary>
    /// Wires the inspector to this document: its trace set is harvested, and a newly added trace is
    /// seeded first.
    /// </summary>
    internal void WireOverlayInspector()
    {
        ChartContainer.Inspector.SetDataSources(OverlaySources);
        ChartContainer.Inspector.PlotStructureChanged += (_, _) =>
        {
            // Not while this document is the one replacing them — see RebuildChart.
            if (_rebuildingChart) return;

            foreach (var t in ChartPlot.Traces)
                if (!t.ExcludeFromAxisLabels && !_overlayTraces.Contains(t)) SeedNewOverlay(t);

            HarvestOverlays();
        };
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
    /// deletable — the AXAML sets <c>CanDeletePlot="False"</c>. A gesture that could silently take
    /// the chart with the marker would be worse than no gesture.
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
