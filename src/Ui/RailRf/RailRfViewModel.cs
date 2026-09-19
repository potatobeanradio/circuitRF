// The railRF window's view model (docs/sonnet-briefs/brief-railrf-7-window.md; railrf.md §11).
//
// ── WHAT THIS FILE IS, AND WHAT IT IS NOT ─────────────────────────────────────────────────────
//
// §11 is "deliberately short on invention: it names what is taken, and only describes what is
// genuinely new", and this file is the same. It EDITS a RailDocument, PROPOSES a reference layer,
// holds the four lists, and hands a request to briefs 3-6. It extracts nothing, solves nothing,
// draws nothing and plots nothing of its own:
//
//   * the extraction and the solve are RailDcRun's (briefs 3-6). The Fast/Accurate split is
//     PdnModelKind's, not a mode flag invented here.
//   * the board in the centre is LayoutCanvas — the layout editor's own canvas (§11.6). Brief 8
//     owns every gesture and every pixel of overlay; this holds the session and the tab strip,
//     and the tab strip switches the OVERLAY and never the geometry.
//   * the plots are PlotControls in rectangular mode fed from a DataSet (§11.1). Brief 12 builds
//     the DataSet; what is here is the host the plots hang off.
//
// ── THE ONE INVARIANT THIS FILE OWES (R-rail4-2, R-rail7-5) ───────────────────────────────────
//
// THE MODEL KIND ON SCREEN ALWAYS MATCHES THE NUMBERS ON SCREEN. A strip reading "Fast" over an
// Accuracy result, for even one frame, is the exact failure PdnModelKind exists to prevent — so
// there is ONE field holding both (RailResultView), it is assigned atomically, and nothing anywhere
// stores a model kind beside a result. See `RailRfViewModel.Solve.cs`.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>Which map the board tab strip is showing. <b>The tabs switch the OVERLAY and never the
/// geometry</b> (§11.3's first point) — the artwork underneath is the same on all four.</summary>
public enum RailBoardOverlay
{
    /// <summary>The artwork as drawn, with the rail's own copper highlighted.</summary>
    Copper,

    /// <summary>§2.4's drop map — the artwork coloured by node voltage.</summary>
    Drop,

    /// <summary>The |Z| map over frequency.</summary>
    Impedance,

    /// <summary>§2.9's trace-versus-mesh classification. <b>A requirement rather than a
    /// diagnostic</b>: a wide supply polygon mistaken for a trace is optimistic and invisible, and
    /// drawing it is what makes it neither.</summary>
    Class,
}

/// <summary>Which half of the results column is showing.</summary>
public enum RailResultsTab
{
    /// <summary>The drop, the breakdown, the via check.</summary>
    Dc,

    /// <summary>|Z| against frequency, its mask and the aggressor lines.</summary>
    Frequency,
}

/// <summary>One entry of the reference-layer combo: the drawing layer, and what it is called.</summary>
/// <param name="Key">The drawing layer, which is what <see cref="RailSpec.ReferenceLayer"/> holds.</param>
/// <param name="Name">The technology's own name for it.</param>
public sealed record RailLayerOption(LayerKey Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One railRF window's state.
/// </summary>
public sealed partial class RailRfViewModel : ObservableObject, IDisposable
{
    /// <summary>A window with no document behind it yet — Tools ▸ railRF, before an import.</summary>
    public RailRfViewModel() : this(new RailDocument(), null) { }

    public RailRfViewModel(RailDocument document, string? path)
    {
        _document = document;
        _documentPath = path;
        BuildPlotHost();     // the one response container — see RailRfViewModel.Response.cs
        BuildBoardPanel();   // the board canvas's overlay — see RailRfViewModel.Board.cs
        RebuildRails();
    }

    private RailDocument _document;
    private string? _documentPath;

    /// <summary>The document this window edits. Never null — a window with nothing imported holds an
    /// empty one, which is what the import fills.</summary>
    public RailDocument Document => _document;

    /// <summary>Where it came from, or null for one that has never been saved.</summary>
    public string? DocumentPath => _documentPath;

    /// <summary>The window's own title — "railRF — evk_1v8_compact".</summary>
    public string Title =>
        "railRF" + (DocumentName is { Length: > 0 } n ? $" — {n}" : "");

    private string? DocumentName =>
        _document.Name is { Length: > 0 } n ? n
        : _documentPath is { Length: > 0 } p ? System.IO.Path.GetFileNameWithoutExtension(p)
        : null;

    /// <summary>Replaces the document this window shows — what the import and a re-open do.</summary>
    public void SetDocument(RailDocument document, string? path)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _documentPath = path;

        // A fast curve beside an accurate one from a DIFFERENT board is worse than no comparison at
        // all (PdnResultsByModel's own note), so the results go with the document.
        ClearResults();
        RebuildRails();

        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(DocumentPath));
        OnPropertyChanged(nameof(Title));
    }

    // ── The rail selector (§11.3's fourth point) ───────────────────────────────────────────────

    /// <summary>Every rail this document holds, in declaration order — what the selector shows.
    /// <see cref="RailOrder"/> computes the SOLVE order, which is not this one.</summary>
    public ObservableCollection<string> Rails { get; } = [];

    /// <summary>
    /// Which rail the whole window is showing (§2.2: one rail is ANALYSED at a time).
    /// </summary>
    /// <remarks>
    /// <b>It is the selector above the source and load lists, and it moves the whole window</b> —
    /// the lists, the target, the band, the aggressors, the board's highlight and the results. That
    /// is what §11.3 means by "says which rail the window is currently showing"; a selector that
    /// moved only the lists would leave the results reading a different rail's numbers with nothing
    /// saying so.
    /// </remarks>
    [ObservableProperty]
    private string? _selectedRailName;

    partial void OnSelectedRailNameChanged(string? value) => RebuildForSelectedRail();

    /// <summary>The selected rail, or null where the document holds none.</summary>
    public RailSpec? SelectedRail =>
        SelectedRailName is { Length: > 0 } n ? _document.Rail(n) : null;

    private void RebuildRails()
    {
        Rails.Clear();
        foreach (var r in _document.Rails) Rails.Add(r.Name);

        // Set ItemsSource BEFORE SelectedItem, always — the wBond round-6 blank-Group-combo bug
        // (§7's second trap): a combo whose ItemsSource binding attaches after a selection was set
        // drops the selection silently, and the user sees an empty combo over a populated list.
        SelectedRailName = Rails.FirstOrDefault();
        RebuildForSelectedRail();
    }

    // ── The four lists ─────────────────────────────────────────────────────────────────────────

    /// <summary>The rail's sources — a list with add and remove, each row a refdes and a pin.</summary>
    public ObservableCollection<RailSourceRowViewModel> Sources { get; } = [];

    /// <summary>The rail's loads. <b>A row with no current reads <i>observe</i></b>.</summary>
    public ObservableCollection<RailLoadRowViewModel> Loads { get; } = [];

    /// <summary>What on this board excites this rail.</summary>
    public ObservableCollection<RailAggressorRowViewModel> Aggressors { get; } = [];

    /// <summary>The parts table (R-rail7-9). Rebuilt by <c>RailRfViewModel.Parts.cs</c>.</summary>
    public ObservableCollection<RailPartRowViewModel> Parts { get; } = [];

    private void RebuildForSelectedRail()
    {
        foreach (var s in Sources) s.Edited -= OnRowEdited;
        foreach (var l in Loads) l.Edited -= OnRowEdited;
        foreach (var a in Aggressors) a.Edited -= OnRowEdited;

        Sources.Clear();
        Loads.Clear();
        Aggressors.Clear();

        if (SelectedRail is { } rail)
        {
            foreach (var s in rail.Sources) Sources.Add(Track(new RailSourceRowViewModel(rail, s)));
            foreach (var l in rail.Loads) Loads.Add(Track(new RailLoadRowViewModel(rail, l)));
            foreach (var a in rail.Aggressors) Aggressors.Add(Track(new RailAggressorRowViewModel(rail, a)));
        }

        RebuildReferenceOptions();
        RefreshTargets();
        RefreshRunGate();
        SyncPlaneFrequencyDefault();

        OnPropertyChanged(nameof(SelectedRail));
        OnPropertyChanged(nameof(StatusLine));
        SyncBoardOverlayResult();
    }

    private RailSourceRowViewModel Track(RailSourceRowViewModel row) { row.Edited += OnRowEdited; return row; }
    private RailLoadRowViewModel Track(RailLoadRowViewModel row) { row.Edited += OnRowEdited; return row; }
    private RailAggressorRowViewModel Track(RailAggressorRowViewModel row) { row.Edited += OnRowEdited; return row; }

    /// <summary>Every committed row edit lands here, and here is where the Fast loop starts.</summary>
    private void OnRowEdited(object? sender, EventArgs e)
    {
        RefreshRunGate();
        QueueResolve();
    }

    // ── Add and remove ─────────────────────────────────────────────────────────────────────────

    /// <summary>Adds an empty source row. It refuses until it is anchored, which is visible on the
    /// row rather than only at Run — an unanchored row the user cannot see is a row they will not
    /// fix.</summary>
    [RelayCommand]
    private void AddSource()
    {
        if (SelectedRail is not { } rail) return;
        rail.Sources.Add(new RailSource());
        RebuildForSelectedRail();
        QueueResolve();
    }

    [RelayCommand]
    private void RemoveSource(RailSourceRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;
        rail.Sources.Remove(row.Source);
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <summary>Adds an empty load row. <b>With no current</b>, which is an observation port and is
    /// what the row reads — never a zero somebody did not type.</summary>
    [RelayCommand]
    private void AddLoad()
    {
        if (SelectedRail is not { } rail) return;
        rail.Loads.Add(new RailLoad());
        RebuildForSelectedRail();
        QueueResolve();
    }

    [RelayCommand]
    private void RemoveLoad(RailLoadRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;
        rail.Loads.Remove(row.Load);
        RebuildForSelectedRail();
        QueueResolve();
    }

    [RelayCommand]
    private void AddAggressor()
    {
        if (SelectedRail is not { } rail) return;
        rail.Aggressors.Add(new RailAggressor("new", 1e6, 1));
        RebuildForSelectedRail();
    }

    [RelayCommand]
    private void RemoveAggressor(RailAggressorRowViewModel? row)
    {
        if (SelectedRail is not { } rail || row is null) return;
        rail.Aggressors.Remove(row.Aggressor);
        RebuildForSelectedRail();
    }

    // ── The reference layer: PROPOSED, never assumed (§2.2, Q-8, R-rail7-8) ────────────────────

    /// <summary>Every conductor drawing layer the technology names — what the combo offers.</summary>
    public ObservableCollection<RailLayerOption> ReferenceLayerOptions { get; } = [];

    /// <summary>
    /// The layer railRF PROPOSES, or null where the stackup gives it no basis for one.
    /// </summary>
    /// <remarks>
    /// <b>A proposal is not a selection.</b> The window shows it and says why; the user confirms.
    /// Q-8 is explicit that the reference is asked for and never inferred, and a pre-selected combo a
    /// user tabs past is not a confirmation — which is why <see cref="IsReferenceConfirmed"/> is a
    /// separate flag and why Run is disabled until it is set. This is the one place this window
    /// deliberately costs the user a click.
    /// </remarks>
    [ObservableProperty]
    private RailLayerOption? _referenceProposal;

    /// <summary>Why that layer was proposed — shown beside it, because a proposal with no reason is
    /// indistinguishable from a default.</summary>
    [ObservableProperty]
    private string _referenceProposalReason = "";

    /// <summary>
    /// What the combo currently shows. Setting it does NOT confirm it — see
    /// <see cref="ConfirmReference"/>.
    /// </summary>
    [ObservableProperty]
    private RailLayerOption? _selectedReferenceLayer;

    partial void OnSelectedReferenceLayerChanged(RailLayerOption? value)
    {
        // Choosing a DIFFERENT layer from the combo is an affirmative act in itself — the user has
        // read the options and picked one. What is not affirmative is the proposal simply standing
        // there, which is why RebuildReferenceOptions sets this without confirming.
        if (value is not null && !_settingProposal) ConfirmReference();
    }

    private bool _settingProposal;

    /// <summary>True once the reference has been affirmatively set for this rail.</summary>
    [ObservableProperty]
    private bool _isReferenceConfirmed;

    /// <summary>
    /// Takes the combo's current layer as the rail's reference. Bound to the <c>Confirm</c> button
    /// beside the combo and to any change the user makes in it.
    /// </summary>
    [RelayCommand]
    public void ConfirmReference()
    {
        if (SelectedRail is not { } rail || SelectedReferenceLayer is not { } option) return;

        rail.ReferenceLayer = option.Key;
        IsReferenceConfirmed = true;
        RefreshRunGate();
        QueueResolve();
    }

    private void RebuildReferenceOptions()
    {
        ReferenceLayerOptions.Clear();

        var tech = Board?.Technology;
        if (tech is not null)
            foreach (var option in ConductorOptions(tech))
                ReferenceLayerOptions.Add(option);

        var (proposal, reason) = ProposeReference(tech, SelectedRail);
        ReferenceProposal = proposal;
        ReferenceProposalReason = reason;

        // ItemsSource first, then the selection — always (§7's second trap).
        _settingProposal = true;
        try
        {
            // A rail that already NAMES a reference is one somebody confirmed before, on a document
            // that was saved with it. Re-asking would be a click charged for a decision already made.
            var stated = SelectedRail?.ReferenceLayer;
            SelectedReferenceLayer = stated is { } key
                ? ReferenceLayerOptions.FirstOrDefault(o => o.Key == key) ?? proposal
                : proposal;
            IsReferenceConfirmed = stated is not null;
        }
        finally { _settingProposal = false; }
    }

    private static IEnumerable<RailLayerOption> ConductorOptions(Technology tech)
    {
        foreach (var entry in tech.Stackup.Layers)
        {
            if (entry.Kind != StackupKind.Conductor) continue;
            foreach (var key in entry.DrawingLayers)
            {
                string name = tech.Layers.FirstOrDefault(l => l.Key == key)?.Name ?? entry.Name;
                yield return new RailLayerOption(key, name);
            }
        }
    }

    /// <summary>
    /// The reference railRF proposes for <paramref name="rail"/>, and why.
    /// </summary>
    /// <remarks>
    /// <b>It reads the stackup's own <c>IsGroundReference</c> mark and nothing else.</b> That flag is
    /// already what the microstrip PCell's substrate resolution keys on, precisely so an intervening
    /// unmarked signal conductor is never mistaken for ground — the same mistake, on the same data,
    /// and a second rule here would be a second answer to one question. Where the stackup marks
    /// nothing, railRF proposes NOTHING and says so: a guess dressed as a proposal is what Q-8 closed.
    /// </remarks>
    internal static (RailLayerOption? Proposal, string Reason) ProposeReference(
        Technology? tech, RailSpec? rail)
    {
        if (tech is null)
            return (null, "No technology is resolved yet, so railRF has no stackup to propose a "
                        + "reference from. Import the artwork first.");

        var marked = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.DrawingLayers.Count > 0)
            .ToList();

        if (marked.Count == 0)
            return (null, "This stackup marks no conductor as a ground reference, so railRF proposes "
                        + "none — it never infers one. Name the reference return's drawing layer.");

        var chosen = marked[0];
        var key = chosen.DrawingLayers[0];
        string name = tech.Layers.FirstOrDefault(l => l.Key == key)?.Name ?? chosen.Name;

        string reason = marked.Count == 1
            ? $"'{name}' is the only conductor this stackup marks as a ground reference. Confirm it, "
            + "or pick another — railRF proposes and never assumes."
            : $"'{name}' is the first of {marked.Count} conductors this stackup marks as a ground "
            + "reference. Confirm it, or pick another — railRF proposes and never assumes.";

        return (new RailLayerOption(key, name), reason);
    }

    // ── The target, the band and the extent ────────────────────────────────────────────────────

    /// <summary>
    /// The DC drop budget, in the settable column. Millivolts, because a budget of 0.08 V reads as a
    /// typo where 80 mV does not — which is why the document stores it that way too.
    /// </summary>
    /// <remarks>
    /// <b>An EMPTY field clears the budget; an unparseable one leaves it alone.</b> The two are not
    /// the same gesture: a rail with no stated budget is a rail with the DC question unanswered, which
    /// brief 5 reports rather than substituting a number for — and deleting it by mistyping would be a
    /// target silently removed, which reads afterwards as a design that passes.
    /// </remarks>
    public string DropBudgetEntry
    {
        get => SelectedRail?.DropBudget?.DropBudgetMillivolts is { } mv
            ? $"{RailValueFormat.Significant(mv, 4)} mV" : "";
        set
        {
            if (SelectedRail is { } rail)
            {
                if (string.IsNullOrWhiteSpace(value)) rail.DropBudget = null;
                else if (RailValueFormat.TryParse(value, RailQuantity.Voltage, out double v))
                    rail.DropBudget = RailTarget.OfDropBudget(v * 1e3);
            }
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>The flat impedance target, in milliohms. Empty clears it; unparseable leaves it —
    /// see <see cref="DropBudgetEntry"/>.</summary>
    public string ImpedanceTargetEntry
    {
        get => SelectedRail?.ImpedanceTarget?.FlatMilliohms is { } mo
            ? $"{RailValueFormat.Significant(mo, 4)} mΩ" : "";
        set
        {
            if (SelectedRail is { } rail)
            {
                if (string.IsNullOrWhiteSpace(value)) rail.ImpedanceTarget = null;
                else if (RailValueFormat.TryParse(value, RailQuantity.Resistance, out double r))
                    rail.ImpedanceTarget = RailTarget.OfFlatImpedance(r * 1e3);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>The bottom of the band Z(f) is answered over.</summary>
    public string BandStartEntry
    {
        get => SelectedRail is { } r ? RailValueFormat.FormatWithUnit(r.Band.StartHz, RailQuantity.Frequency) : "";
        set
        {
            if (SelectedRail is not { } rail) return;
            if (RailValueFormat.TryParse(value, RailQuantity.Frequency, out double hz))
                rail.Band = rail.Band with { StartHz = hz };
            OnPropertyChanged();
        }
    }

    /// <summary>The top of it.</summary>
    public string BandStopEntry
    {
        get => SelectedRail is { } r ? RailValueFormat.FormatWithUnit(r.Band.StopHz, RailQuantity.Frequency) : "";
        set
        {
            if (SelectedRail is not { } rail) return;
            if (RailValueFormat.TryParse(value, RailQuantity.Frequency, out double hz))
                rail.Band = rail.Band with { StopHz = hz };
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// What the reference conductor is taken to be.
    /// </summary>
    /// <remarks>
    /// <b>Behind <c>Settings</c>, not on the face of the window</b> (§11.2). It is an advanced choice
    /// two of whose three values are OPTIMISTIC, and the status strip states the one in force on every
    /// frame — which is the part that has to be unmissable, not the control.
    /// </remarks>
    public RailReferenceExtent ReferenceExtent
    {
        get => SelectedRail?.ReferenceExtent ?? RailReferenceExtent.AsImported;
        set
        {
            if (SelectedRail is not { } rail || rail.ReferenceExtent == value) return;
            rail.ReferenceExtent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            QueueResolve();
        }
    }

    /// <summary>The three extents, for the Settings combo.</summary>
    public IReadOnlyList<RailReferenceExtent> ReferenceExtents { get; } =
        [.. Enum.GetValues<RailReferenceExtent>()];

    // ── The rest of §11.2's list, behind Settings and nowhere else ─────────────────────────────
    //
    // EVERY ONE OF THESE IS STATED ON THE REPORT, which is the whole reason they are document state
    // rather than session state: §2.7's "everything is computed at 20 °C and every report says so" is
    // only true if the 20 is a number the report can read. RailSettings' own note says the same.

    /// <summary>
    /// The plated barrel thickness a via's current limit is computed from.
    /// </summary>
    /// <remarks>
    /// <b>Empty is not a default</b> (Q-22): brief 6 reads the thickness from the stackup's via entry
    /// where one states it, takes this where it does not, and falls back to the drill-size table as a
    /// sanity band — and every flag says which basis produced it. A number typed here by the control
    /// itself would make all three indistinguishable on the report, which is why clearing the field
    /// puts the null back rather than leaving the last value standing.
    /// </remarks>
    public string ViaPlatingEntry
    {
        get => _document.Settings.ViaPlatingThicknessMicrometres is { } um
            ? $"{RailValueFormat.Significant(um, 4)} µm" : "";
        set
        {
            string trimmed = (value ?? "").Trim();
            _document.Settings.ViaPlatingThicknessMicrometres =
                trimmed.Length == 0 ? null
                : double.TryParse(trimmed.TrimEnd('µ', 'u', 'm', ' '),
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out double um)
                    ? um : _document.Settings.ViaPlatingThicknessMicrometres;
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>
    /// The temperature RISE a via's current limit is stated at, in °C of rise.
    /// </summary>
    /// <remarks>
    /// <b>A budget, not a temperature.</b> There is no thermal model anywhere in railRF and this is
    /// not one: the via check computes a limit from the barrel's own annulus, its span and this
    /// number, and nothing else in railRF reads it.
    /// </remarks>
    public string ViaTemperatureRiseEntry
    {
        get => $"{RailValueFormat.Significant(_document.Settings.ViaTemperatureRiseCelsius, 4)} °C";
        set
        {
            if (ParseCelsius(value) is { } c) _document.Settings.ViaTemperatureRiseCelsius = c;
            OnPropertyChanged();
            QueueResolve();
        }
    }

    /// <summary>
    /// The one stated temperature every resistance in this document is computed at.
    /// </summary>
    /// <remarks>
    /// <b>Not a thermal model either</b> (§2.7). It is the single stated basis the copper conductivity
    /// is taken at, so a report can say which — and the status strip says it on every frame for the
    /// same reason.
    /// </remarks>
    public string TemperatureEntry
    {
        get => $"{RailValueFormat.Significant(_document.Settings.CopperTemperatureCelsius, 4)} °C";
        set
        {
            if (ParseCelsius(value) is { } c) _document.Settings.CopperTemperatureCelsius = c;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            QueueResolve();
        }
    }

    /// <summary>
    /// How finely <c>Accuracy</c> meshes, as a cell size.
    /// </summary>
    /// <remarks>
    /// <b>Read by the accurate reading only</b> — the fast one traces instead, and a meshing density
    /// on the face of the window would read as a knob that changes the default answer. It does not.
    /// </remarks>
    public string MeshCellEntry
    {
        get => _meshCellMicrons is { } um ? $"{RailValueFormat.Significant(um, 4)} µm" : "";
        set
        {
            string trimmed = (value ?? "").Trim();
            _meshCellMicrons =
                trimmed.Length == 0 ? null
                : double.TryParse(trimmed.TrimEnd('µ', 'u', 'm', ' '),
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out double um)
                    ? um : _meshCellMicrons;
            OnPropertyChanged();
        }
    }

    private double? _meshCellMicrons;

    /// <summary>The cell size <c>Accuracy</c> is asked for, in METRES — <c>PdnMeshSettings</c>'
    /// own unit — or null for the extractor's computed one, which is the ordinary case.</summary>
    internal double? MeshCellMetres => _meshCellMicrons is { } um ? um * 1e-6 : null;

    private static double? ParseCelsius(string? text)
    {
        string trimmed = (text ?? "").Trim().TrimEnd('C', 'c', '°', ' ');
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double c)
            ? c : null;
    }

    private void RefreshTargets()
    {
        OnPropertyChanged(nameof(DropBudgetEntry));
        OnPropertyChanged(nameof(ImpedanceTargetEntry));
        OnPropertyChanged(nameof(BandStartEntry));
        OnPropertyChanged(nameof(BandStopEntry));
        OnPropertyChanged(nameof(ReferenceExtent));
    }

    // ── The board tab strip and the results tabs ───────────────────────────────────────────────

    /// <summary>
    /// Which map the board is showing. <b>Switching it switches the OVERLAY and never the
    /// geometry</b> — brief 8 draws each one; what is here is which one it is asked for.
    /// </summary>
    [ObservableProperty]
    private RailBoardOverlay _selectedBoardOverlay = RailBoardOverlay.Copper;

    /// <summary>The four tabs, in the strip's own order.</summary>
    public IReadOnlyList<RailBoardOverlay> BoardOverlays { get; } = [.. Enum.GetValues<RailBoardOverlay>()];

    /// <summary>Which half of the results column is showing.</summary>
    [ObservableProperty]
    private RailResultsTab _selectedResultsTab = RailResultsTab.Dc;

    /// <summary>Both tabs.</summary>
    public IReadOnlyList<RailResultsTab> ResultsTabs { get; } = [.. Enum.GetValues<RailResultsTab>()];

    public void Dispose()
    {
        CancelInFlight();
        PlotHost.Dispose();
    }
}
