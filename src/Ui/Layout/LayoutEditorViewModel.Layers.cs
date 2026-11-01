// The layout editor's own Layers list (brief-layout-layers-panel.md R-lyr-2…R-lyr-5).
//
// ── WHY VISIBILITY AND SELECTABILITY LIVE HERE AND NOT IN THE .ctech ───────────────────────────
//
// They are a property of a VIEW of a layout, not of the process (D1). A technology is a shared
// manufacturing document; using its Vis/Sel boxes as a display switch made one user's navigation
// another user's diff, and stopping a ground plane being selectable dirtied the technology for every
// layout that used it. The field report that prompted this: a first-time user clicking a microstrip
// line over a ground plane kept selecting the plane, and found no way to stop it.
//
// ── ONE SEAM: DrawTechnology ───────────────────────────────────────────────────────────────────
//
// Every renderer, hit test and snap query reads LayerDef.Visible/Selectable off the technology it is
// handed. So the view's choices are applied to a CLONE of the resolved technology, and every
// drawing and picking path in this editor reads that clone. Anything that saves, exports, runs
// DRC/LVS/EM or builds a 3D view reads Technology, which carries no view state at all — a hidden
// layer is never missing from an export or a simulation.
//
// ── AND IT DIRTIES NOTHING ─────────────────────────────────────────────────────────────────────
//
// Not the .clay and not the .ctech. The workspace keeps each layout's choices in the per-user
// .cwsuser (R-lyr-4), told through LayerViewChanged.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    private readonly HashSet<LayerKey> _hiddenLayers = [];
    private readonly HashSet<LayerKey> _lockedLayers = [];

    // ── The Ports row (R-dfr33-1-1) ──────────────────────────────────────────────────────────────
    //
    // A port label lives on the conductor layer it drives, so the layer rows cannot set ports aside on
    // their own: turning Top Copper's Sel box off takes the traces with it. This row governs every port
    // label in the view, whatever its layer, and is stored beside the layer sets.
    private bool _portsHidden, _portsLocked;

    /// <summary>The Ports row's Vis box: off, no port label, arrow, name or width bar is drawn, and none
    /// can be picked.</summary>
    public bool PortsVisible
    {
        get => !_portsHidden;
        set { if (_portsHidden == !value) return; _portsHidden = !value; DropUnpickablePorts(); LayerViewEdited(); }
    }

    /// <summary>The Ports row's Sel box: off, no port enters a click's pick stack, a marquee or Select
    /// All. The Port tool still places ports.</summary>
    public bool PortsSelectable
    {
        get => !_portsLocked;
        set { if (_portsLocked == !value) return; _portsLocked = !value; DropUnpickablePorts(); LayerViewEdited(); }
    }

    /// <summary>A port already selected when its row goes off leaves the selection — otherwise Delete would
    /// remove a port the user had just said they could not pick, and with Vis off could not see.</summary>
    private void DropUnpickablePorts()
    {
        if (!_selectedIndices.Any(i => i >= 0 && i < Model.Shapes.Count && !PortRowAllowsPick(Model.Shapes[i]))) return;
        SetSelection([.. _selectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count && PortRowAllowsPick(Model.Shapes[i]))],
                     clearOtherKind: false);
        _cycleCache.Clear();
    }

    /// <summary>Whether a click, marquee or Select All may take <paramref name="shape"/> as far as the
    /// Ports row is concerned — true for every shape that is not a port.</summary>
    internal bool PortRowAllowsPick(LayoutShape shape) =>
        shape is not LabelShape { IsPort: true } || (!_portsHidden && !_portsLocked);

    /// <summary>The status line's reminder while ports cannot be picked, so a user who switched them off
    /// does not think the editor has stopped responding to them. Empty otherwise.</summary>
    public string PortsRowStatusText => _portsHidden || _portsLocked ? "Ports not selectable" : "";

    /// <summary>Layers this layout draws on that the technology does not define — listed as rows of
    /// their own, drawn with the fallback palette's look.</summary>
    private readonly HashSet<LayerKey> _undefinedLayers = [];

    /// <summary>Bumped on every change of the view's layer state; part of <see cref="DrawTechnology"/>'s
    /// cache key, and a property so the canvas (which repaints on any property change) redraws.</summary>
    public int LayerViewVersion { get; private set; }

    /// <summary>Raised when the user changes this view's layer state — the workspace's cue to remember
    /// it for the <c>.cwsuser</c>. Never raised by <see cref="ApplyLayerView"/>.</summary>
    internal event Action<LayoutEditorViewModel>? LayerViewChanged;

    /// <summary>One row per layer: the technology's drawing layers (or the no-technology placeholder
    /// set), then every layer this layout's shapes use that the technology does not define.</summary>
    public ObservableCollection<LayoutLayerRowViewModel> LayerRows { get; } = [];

    /// <summary>The rows the list SHOWS: <see cref="LayerRows"/> narrowed by <see cref="LayerListFilter"/>.
    /// Show all and the header's count still act on every row — a filter decides what is on screen,
    /// not what the view draws.</summary>
    public ObservableCollection<LayoutLayerRowViewModel> FilteredLayerRows { get; } = [];

    /// <summary>
    /// Case-insensitive substring over the layer names, as the technology editor's Layers tab filters —
    /// a process with hundreds of layers is unworkable without it. Session state, like whether the list
    /// is open.
    /// </summary>
    [ObservableProperty] private string _layerListFilter = "";

    partial void OnLayerListFilterChanged(string value) => ApplyLayerListFilter();

    /// <summary>"3 of 40" while the filter is narrowing the list, the plain total otherwise.</summary>
    public string LayerListFilterSummary =>
        FilteredLayerRows.Count == LayerRows.Count ? $"{LayerRows.Count}" : $"{FilteredLayerRows.Count} of {LayerRows.Count}";

    private void ApplyLayerListFilter()
    {
        string q = LayerListFilter.Trim();
        FilteredLayerRows.Clear();
        foreach (var row in LayerRows)
            if (q.Length == 0 || row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) FilteredLayerRows.Add(row);
        OnPropertyChanged(nameof(LayerListFilterSummary));
        NotifyBulkLayerToggles();
    }

    /// <summary>
    /// Whether the list is expanded. <b>Session state only</b>, for railRF's reason: which layers a
    /// reader wants to see is about this layout, but whether a disclosure triangle is open is about
    /// this minute.
    /// </summary>
    [ObservableProperty] private bool _showLayerList;

    [RelayCommand]
    private void ToggleLayerList() => ShowLayerList = !ShowLayerList;

    /// <summary>
    /// R-lyr-3 — while on, only shapes on the current drawing layer can be picked, whatever the Sel
    /// column says. The column is left as it is, so turning this off returns it. Saved with the rest.
    /// </summary>
    [ObservableProperty] private bool _selectCurrentLayerOnly;

    partial void OnSelectCurrentLayerOnlyChanged(bool value)
    {
        if (_applyingLayerView) return;
        LayerViewEdited();
    }

    private bool _applyingLayerView;

    /// <summary>The list's width before anyone drags it.</summary>
    public const double DefaultLayerListWidth = 240;

    /// <summary>The narrowest and widest the grip lets the list be. The floor keeps the two toggles, the
    /// filter box and a usable name column; the ceiling keeps it a side panel.</summary>
    public const double MinLayerListWidth = 180, MaxLayerListWidth = 700;

    /// <summary>
    /// How wide the list is — dragged by the grip on its left edge, and saved with this layout's layer
    /// choices in the <c>.cwsuser</c>. Set live while the grip moves; <see cref="CommitLayerListWidth"/>
    /// is the end of the drag.
    /// </summary>
    [ObservableProperty] private double _layerListWidth = DefaultLayerListWidth;

    /// <summary>The end of a drag (or a double-click back to the default): tells the workspace once,
    /// not on every pointer move.</summary>
    internal void CommitLayerListWidth(double width)
    {
        LayerListWidth = Math.Clamp(width, MinLayerListWidth, MaxLayerListWidth);
        LayerViewChanged?.Invoke(this);
    }

    /// <summary>The list's header — the count is what says the list is worth opening.</summary>
    public string LayerListHeader
    {
        get
        {
            int hidden = LayerRows.Count(r => !r.Visible);
            return LayerRows.Count == 0 ? "Layers"
                 : hidden == 0 ? $"Layers ({LayerRows.Count})"
                 : $"Layers ({LayerRows.Count - hidden} of {LayerRows.Count} shown)";
        }
    }

    /// <summary>True while any layer is hidden or locked — the only state in which Show all does anything.</summary>
    public bool HasLayerOverrides => _hiddenLayers.Count > 0 || _lockedLayers.Count > 0 || _portsHidden || _portsLocked;

    /// <summary>Every row back to visible and selectable. Disabled, not hidden, while nothing is off.</summary>
    [RelayCommand(CanExecute = nameof(HasLayerOverrides))]
    private void ShowAllLayers()
    {
        if (!HasLayerOverrides) return;
        _hiddenLayers.Clear();
        _lockedLayers.Clear();
        _portsHidden = _portsLocked = false;
        LayerViewEdited();
    }

    // ── Bulk Vis / Sel over the LISTED rows ────────────────────────────────────────────────────────
    // As the technology editor's Layers tab had them: scoped to what the filter is SHOWING, not to every
    // layer. The two coincide with no filter, and with one "hide everything I am looking at" is the
    // useful operation. Setter-not-command, so the ToggleButton's own checked state IS the answer to
    // "is every listed layer visible?" with no second source of truth to keep in step.

    /// <summary>True while every listed row is visible; setting it shows or hides every listed row.</summary>
    public bool AllListedLayersVisible
    {
        get => FilteredLayerRows.Count > 0 && FilteredLayerRows.All(r => r.Visible);
        set => SetListedLayers(_hiddenLayers, hide: !value);
    }

    /// <summary>True while every listed row's Sel box is on; setting it turns every listed row's Sel on
    /// or off.</summary>
    public bool AllListedLayersSelectable
    {
        get => FilteredLayerRows.Count > 0 && FilteredLayerRows.All(r => r.Selectable);
        set => SetListedLayers(_lockedLayers, hide: !value);
    }

    /// <summary>One change for the whole sweep, so the canvas repaints once and the
    /// <c>.cwsuser</c> is told once.</summary>
    private void SetListedLayers(HashSet<LayerKey> set, bool hide)
    {
        bool changed = false;
        foreach (var row in FilteredLayerRows)
            changed |= hide ? set.Add(row.Key) : set.Remove(row.Key);
        if (changed) LayerViewEdited();
        else NotifyBulkLayerToggles();   // a no-op still re-announces, or the button stays where the click left it
    }

    private void NotifyBulkLayerToggles()
    {
        OnPropertyChanged(nameof(AllListedLayersVisible));
        OnPropertyChanged(nameof(AllListedLayersSelectable));
    }

    /// <summary>Whether this view draws <paramref name="key"/>.</summary>
    internal bool IsLayerShown(LayerKey key) => !_hiddenLayers.Contains(key);

    /// <summary>Whether the Sel column has <paramref name="key"/> on — before
    /// <see cref="SelectCurrentLayerOnly"/>, which <see cref="DrawTechnology"/> folds in.</summary>
    internal bool IsLayerSelectableInList(LayerKey key) => !_lockedLayers.Contains(key);

    internal void SetLayerVisible(LayerKey key, bool visible)
    {
        bool changed = visible ? _hiddenLayers.Remove(key) : _hiddenLayers.Add(key);
        if (changed) LayerViewEdited();
    }

    internal void SetLayerSelectable(LayerKey key, bool selectable)
    {
        bool changed = selectable ? _lockedLayers.Remove(key) : _lockedLayers.Add(key);
        if (changed) LayerViewEdited();
    }

    /// <summary>A row's name was clicked: the same as choosing it in the toolbar's Layer: combobox,
    /// which follows because this sets <see cref="CurrentLayerItem"/>.</summary>
    internal void MakeLayerCurrent(LayerKey key)
    {
        if (AvailableLayers.FirstOrDefault(l => l.Key == key) is { } item) CurrentLayerItem = item;
    }

    /// <summary>
    /// Seeds this view's layer state from what the <c>.cwsuser</c> remembered, raising nothing — a
    /// layout being opened has not been changed by anyone.
    /// </summary>
    internal void ApplyLayerView(CwsLayoutLayerView? stored)
    {
        _hiddenLayers.Clear();
        _lockedLayers.Clear();
        foreach (var k in stored?.Hidden ?? []) _hiddenLayers.Add(k);
        foreach (var k in stored?.Locked ?? []) _lockedLayers.Add(k);
        _portsHidden = stored?.PortsHidden ?? false;
        _portsLocked = stored?.PortsLocked ?? false;
        _applyingLayerView = true;
        try { SelectCurrentLayerOnly = stored?.CurrentLayerOnly ?? false; }
        finally { _applyingLayerView = false; }
        LayerListWidth = stored?.ListWidth is { } w ? Math.Clamp(w, MinLayerListWidth, MaxLayerListWidth) : DefaultLayerListWidth;
        LayerViewStateChanged();
    }

    /// <summary>What the <c>.cwsuser</c> should hold for this layout, or null when nothing is off and the
    /// list is its default width (D4: a layout nobody has changed writes nothing).</summary>
    internal CwsLayoutLayerView? LayerViewToPersist()
    {
        bool widthIsDefault = LayerListWidth == DefaultLayerListWidth;
        if (!HasLayerOverrides && !SelectCurrentLayerOnly && widthIsDefault) return null;
        static List<LayerKey>? Sorted(HashSet<LayerKey> set) =>
            set.Count == 0 ? null : [.. set.OrderBy(k => k.Layer).ThenBy(k => k.Datatype)];
        return new CwsLayoutLayerView
        {
            Hidden = Sorted(_hiddenLayers),
            Locked = Sorted(_lockedLayers),
            CurrentLayerOnly = SelectCurrentLayerOnly,
            PortsHidden = _portsHidden,
            PortsLocked = _portsLocked,
            ListWidth = widthIsDefault ? null : LayerListWidth,
        };
    }

    private void LayerViewEdited()
    {
        LayerViewStateChanged();
        LayerViewChanged?.Invoke(this);
    }

    private void LayerViewStateChanged()
    {
        LayerViewVersion++;
        RefreshLayerRows();
        OnPropertyChanged(nameof(LayerViewVersion));
        OnPropertyChanged(nameof(HasLayerOverrides));
        OnPropertyChanged(nameof(PortsVisible));
        OnPropertyChanged(nameof(PortsSelectable));
        OnPropertyChanged(nameof(PortsRowStatusText));
        ShowAllLayersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Records layers this layout draws on that the technology does not define, adding a row
    /// for each one not already listed.</summary>
    private void NoteUndefinedLayers(IEnumerable<LayerKey> keys)
    {
        var defined = AvailableLayers.Select(l => l.Key).ToHashSet();
        bool added = false;
        foreach (var k in keys)
            if (!defined.Contains(k) && _undefinedLayers.Add(k)) added = true;
        if (!added) return;
        LayerViewVersion++;   // DrawTechnology must carry a row for it under current-layer-only
        RebuildLayerRows();
    }

    /// <summary>Rebuilds the rows from the technology (or the placeholder set) and the layout's own
    /// undefined layers. Hidden and locked keys that resolve to no row are KEPT, not pruned: switching
    /// the technology back brings them back, and they cost nothing.</summary>
    private void RebuildLayerRows()
    {
        foreach (var shape in Model.Shapes) _undefinedLayers.Add(shape.Layer);
        var defined = AvailableLayers.Select(l => l.Key).ToHashSet();
        _undefinedLayers.ExceptWith(defined);

        LayerRows.Clear();
        foreach (var item in AvailableLayers)
            LayerRows.Add(Row(ResolveTechLayer(item.Key), defined: true));
        foreach (var key in _undefinedLayers.OrderBy(k => k.Layer).ThenBy(k => k.Datatype))
            LayerRows.Add(Row(FallbackPalette.For(key), defined: false));

        ApplyLayerListFilter();
        OnPropertyChanged(nameof(LayerListHeader));

        LayoutLayerRowViewModel Row(LayerDef def, bool defined) =>
            new(this, def, defined, IsLayerShown(def.Key), IsLayerSelectableInList(def.Key), def.Key == CurrentLayerKey);
    }

    private LayerDef ResolveTechLayer(LayerKey key) =>
        Technology?.Layers.FirstOrDefault(l => l.Key == key) ?? FallbackPalette.For(key);

    private void RefreshLayerRows()
    {
        foreach (var row in LayerRows)
            row.Refresh(IsLayerShown(row.Key), IsLayerSelectableInList(row.Key), row.Key == CurrentLayerKey);
        OnPropertyChanged(nameof(LayerListHeader));
        NotifyBulkLayerToggles();
    }

    // ── DrawTechnology (R-lyr-5) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Technology"/> as the canvas DRAWS and PICKS it: with this view's hidden and locked
    /// layers, and current-layer-only folded into Selectable. The same instance as
    /// <see cref="Technology"/> while nothing is off. Only drawing and picking read it — anything that
    /// saves, exports, simulates or installs a technology reads <see cref="Technology"/>, so view state
    /// can never reach a file.
    /// </summary>
    internal Technology? DrawTechnology
    {
        get
        {
            var tech = Technology;
            bool onlyCurrent = SelectCurrentLayerOnly;
            if (!HasLayerOverrides && !onlyCurrent) return tech;

            if (_drawTech is not null && ReferenceEquals(_drawTechSource, tech) && _drawTechVersion == LayerViewVersion
                && (!onlyCurrent || _drawTechCurrent == CurrentLayerKey))
                return _drawTech;

            // No technology: a technology of the fallback palette's own layers, so the view's choices
            // still have somewhere to live. The renderer draws such a layer exactly as it draws an
            // unknown one.
            var copy = tech is null ? new Technology { Name = TechNameText } : TechPersistence.Clone(tech);
            var present = copy.Layers.Select(l => l.Key).ToHashSet();
            var extra = new HashSet<LayerKey>(_hiddenLayers);
            extra.UnionWith(_lockedLayers);
            if (onlyCurrent || tech is null)
            {
                extra.UnionWith(_undefinedLayers);
                extra.UnionWith(AvailableLayers.Select(l => l.Key));
            }
            foreach (var key in extra.Where(k => !present.Contains(k)).OrderBy(k => k.Layer).ThenBy(k => k.Datatype))
                copy.Layers.Add(FallbackPalette.For(key));

            // ANDed with what the copy already says rather than overwriting it: a technology read from
            // a .ctech is all-on, so this is the view's choice alone, and one built in memory with a
            // layer already off keeps it off.
            foreach (var l in copy.Layers)
            {
                l.Visible    &= IsLayerShown(l.Key);
                l.Selectable &= IsLayerSelectableInList(l.Key) && (!onlyCurrent || l.Key == CurrentLayerKey);
            }

            _drawTech        = copy;
            _drawTechSource  = tech;
            _drawTechVersion = LayerViewVersion;
            _drawTechCurrent = CurrentLayerKey;
            return copy;
        }
    }

    private Technology? _drawTech, _drawTechSource;
    private int _drawTechVersion = -1;
    private LayerKey _drawTechCurrent;
}
