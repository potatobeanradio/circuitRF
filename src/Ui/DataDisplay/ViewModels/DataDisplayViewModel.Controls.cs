// ================================================================
//  DataDisplayViewModel.Controls.cs  —  the tab's display controls
//  (brief-ddl-5 R-ddl5-4 … R-ddl5-9)
//
//  Controls sit in a layer of their own above the plots and take part in
//  the display's one selection: a click, Ctrl/Cmd-click, a rubber band,
//  Select All, Escape, Delete and a group move all reach them as they
//  reach plots. A LOCKED control is selected, edited, copied and deleted
//  like any other, and is left where it is by every move.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataDisplayViewModel
{
    private readonly ObservableCollection<ControlContainerViewModel> _controls = new();

    /// <summary>This tab's controls, in the order they were added (later ones draw on top).</summary>
    public ObservableCollection<ControlContainerViewModel> Controls => _controls;

    /// <summary>The panels among <see cref="Controls"/>: the canvas puts them in a layer UNDER the plots.</summary>
    public ObservableCollection<ControlContainerViewModel> PanelControls { get; } = new();

    /// <summary>Every other control: the layer over the plots.</summary>
    public ObservableCollection<ControlContainerViewModel> ForegroundControls { get; } = new();

    /// <summary>Keeps the two layers in step with <see cref="Controls"/> — a panel is drawn under every plot whatever
    /// its stacking, so it cannot share the plots' or the controls' layer.</summary>
    private void SyncControlLayers()
    {
        Sync(PanelControls, _controls.Where(c => c.Model.IsPanel).ToList());
        Sync(ForegroundControls, _controls.Where(c => !c.Model.IsPanel).ToList());

        static void Sync(ObservableCollection<ControlContainerViewModel> layer, List<ControlContainerViewModel> want)
        {
            // Only what changed is removed or inserted, so a view that is already there keeps its state (focus, an open
            // editor) when another control comes or goes.
            for (int i = layer.Count - 1; i >= 0; i--)
                if (!want.Contains(layer[i])) layer.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
                if (i >= layer.Count || !ReferenceEquals(layer[i], want[i]))
                {
                    layer.Remove(want[i]);
                    layer.Insert(i, want[i]);
                }
        }
    }

    // ---- Properties… --------------------------------------------------------------------------------

    /// <summary>Set by the shell: brings the Properties panel forward, floating it when it was closed.</summary>
    public static Action? PropertiesPanelRequested { get; set; }

    /// <summary>A context menu's <b>Properties…</b>: the Properties panel, which edits the selection.</summary>
    internal static void RequestPropertiesPanel() => PropertiesPanelRequested?.Invoke();

    /// <summary>Set by the owning window: every control of the document, for document-wide names (R-ddl5-1).</summary>
    internal Func<IEnumerable<DisplayControl>>? DocumentControlsProvider { get; set; }

    /// <summary>The one control selected, when exactly one is and nothing else is — what the control inspector edits.</summary>
    public ControlContainerViewModel? SelectedControl
    {
        get
        {
            var sel = _controls.Where(c => c.IsSelected).Take(2).ToList();
            return sel.Count == 1 ? sel[0] : null;
        }
    }

    /// <summary>Raised when a control's model changed — the inspector re-reads it.</summary>
    public event Action<ControlContainerViewModel>? ControlChanged;

    // ---- add and remove -----------------------------------------------------------------------------

    /// <summary>
    /// <b>Controls ▸ Slider</b> (R-ddl5-7): a Medium slider at the next free canvas slot — the place Add Plot would put
    /// it, moved down past any control already there, so it is always in view — selected, so the inspector opens on it.
    /// One undo step.
    /// </summary>
    public ControlContainerViewModel AddSlider() => AddControl(new SliderControl());

    /// <summary><b>Controls ▸ Knob</b> (brief-ddl-6 R-ddl6-1), placed as a slider is.</summary>
    public ControlContainerViewModel AddKnob() => AddControl(new KnobControl());

    /// <summary><b>Controls ▸ Button</b> (R-ddl6-2): a toggle, dark.</summary>
    public ControlContainerViewModel AddButton() => AddControl(new ButtonControl());

    /// <summary><b>Controls ▸ Panel</b>: a blank card, drawn under every plot and control.</summary>
    public ControlContainerViewModel AddPanel() => AddControl(new PanelControl());

    /// <summary><b>Controls ▸ Readout</b> (R-ddl6-3): showing the selected marker when there is one, else the first
    /// marker or control of the document, so it reads something at once.</summary>
    public ControlContainerViewModel AddReadout()
    {
        var doc = CurrentLinkDocument();
        var marker = SelectedMarkerForReadout() ?? doc.Markers().Select(m => (LinkDocument.MarkerSite?)m).FirstOrDefault();
        var r = new ReadoutControl();
        if (marker is { } m) { r.Source = m.Marker.Id.ToString("D"); r.SourceName = m.Marker.Name; }
        else if (doc.Controls().Select(x => x.Control).FirstOrDefault(ControlLinkSource.IsSource) is { } c)
        { r.Source = c.Id; r.SourceName = c.Name; }
        var vm = AddControl(r);
        RequestLinkFollow(released: true);
        return vm;
    }

    /// <summary>The one selected marker of this tab, as the link source a new readout shows.</summary>
    private LinkDocument.MarkerSite? SelectedMarkerForReadout()
    {
        var selected = _markerInfoBoxes.Where(b => b.IsSelected).Select(b => b.Marker).Distinct().ToList();
        if (selected.Count != 1) return null;
        foreach (var site in CurrentLinkDocument().Markers())
            if (ReferenceEquals(site.Marker, selected[0])) return site;
        return null;
    }

    /// <summary>A new control of any kind: named, Medium, at the next free canvas slot, selected; one undo step.</summary>
    private ControlContainerViewModel AddControl(DisplayControl model)
    {
        var all = DocumentControlsProvider?.Invoke() ?? _controls.Select(c => c.Model);
        model.Name = DisplayControl.NextName(model.KindName, all);
        // A frequency reads in the application's preferred unit (Settings ▸ Units), fixed when the control is placed.
        if (model is SteppedControl stepped) stepped.FrequencyUnit = CircuitRF.Ui.Theming.PreferredUnits.Frequency;
        model.ApplySize(ControlSize.Medium);
        (model.Left, model.Top) = ComputeNewControlPosition(model.Width, model.Height);
        var vm = new ControlContainerViewModel(model, this);
        Gesture.Note("addControl", model.Name);
        UndoRedo.Do(new AddControlCommand(this, _controls.Count, vm));
        SelectOnly(vm);
        return vm;
    }

    /// <summary>
    /// The first spot inside the visible canvas, scanned row by row from the top-left, where the new control covers no
    /// plot and no other control. Without a canvas size (a test, a headless host) or with no free spot, the place Add
    /// Plot would use — which is always in view.
    /// </summary>
    private (double Left, double Top) ComputeNewControlPosition(double w, double h)
    {
        const double Gap = 12, Grain = 20;
        if (CanvasSizeProvider?.Invoke() is var (cw, ch) && cw > 0 && ch > 0)
        {
            double zoom = _zoomLevel > 0 ? _zoomLevel : 1.0;
            double vx0 = -_viewOffsetX / zoom, vy0 = -_viewOffsetY / zoom;
            double vx1 = (cw - _viewOffsetX) / zoom, vy1 = (ch - _viewOffsetY) / zoom;
            var taken = _plots.Select(p => (p.Left, p.Top, R: p.Left + p.Width, B: p.Top + p.Height))
                              .Concat(_controls.Select(c => (c.Left, c.Top, R: c.Left + c.Model.Width, B: c.Top + c.Model.Height)))
                              .ToList();
            for (double y = vy0 + Gap; y + h <= vy1 - Gap; y += Grain)
                for (double x = vx0 + Gap; x + w <= vx1 - Gap; x += Grain)
                    if (!taken.Any(t => x < t.R + Gap && x + w + Gap > t.Left && y < t.B + Gap && y + h + Gap > t.Top))
                        return (x, y);
        }
        return ComputeNewPlotPosition(w, h);
    }

    internal void InsertControl(int index, ControlContainerViewModel vm)
    {
        _controls.Insert(Math.Clamp(index, 0, _controls.Count), vm);
        SyncControlLayers();
        RequestLinkFollow(released: true);
    }

    internal void RemoveControl(ControlContainerViewModel vm)
    {
        vm.IsSelected = false;
        _controls.Remove(vm);
        SyncControlLayers();
        RefreshSelection();
        // Its dependents freeze where it last put them, and are said to (brief-ddl-2 R-ddl2-8).
        RequestLinkFollow(released: true, announce: true);
    }

    /// <summary>After the links followed: every readout is drawn again, since what it shows may have moved
    /// (brief-ddl-6 R-ddl6-3).</summary>
    internal void RedrawReadouts()
    {
        foreach (var c in _controls)
            if (c.Model is ReadoutControl) c.RequestRedraw();
    }

    /// <summary>A control's model changed: the links that follow it, and the inspector.</summary>
    internal void OnControlChanged(ControlContainerViewModel vm)
    {
        RequestLinkFollow(released: true);
        ControlChanged?.Invoke(vm);
    }

    /// <summary>Adds controls from a paste or a load as they are — ids, names, lock and all.</summary>
    internal ControlContainerViewModel AdoptControl(DisplayControl model)
    {
        var vm = new ControlContainerViewModel(model, this) { ZIndex = model.ZIndex };
        if (model.ZIndex >= _nextZIndex) _nextZIndex = model.ZIndex + 1;
        _controls.Add(vm);
        SyncControlLayers();
        return vm;
    }

    // ---- selection ----------------------------------------------------------------------------------

    public void SelectOnly(ControlContainerViewModel? vm)
    {
        SelectOnly((PlotContainerViewModel?)null);
        if (vm is not null) { vm.IsSelected = true; vm.ZIndex = _nextZIndex++; }
        RefreshSelection();
    }

    public void ToggleSelect(ControlContainerViewModel vm)
    {
        vm.IsSelected = !vm.IsSelected;
        if (vm.IsSelected) vm.ZIndex = _nextZIndex++;
        RefreshSelection();
    }

    /// <summary>The next stacking index: a control given it is drawn over every other.</summary>
    internal int NextControlZ() => _nextZIndex++;

    private void DeselectControls()
    {
        foreach (var c in _controls) c.IsSelected = false;
    }

    // ---- Lock Position (R-ddl5-9) -------------------------------------------------------------------

    /// <summary>The selected controls, or <paramref name="clicked"/> alone when it is not among them.</summary>
    internal IReadOnlyList<ControlContainerViewModel> LockTargets(ControlContainerViewModel clicked)
        => clicked.IsSelected ? _controls.Where(c => c.IsSelected).ToList() : [clicked];

    /// <summary>Ticked only when every target is locked.</summary>
    internal bool AllLocked(ControlContainerViewModel clicked) => LockTargets(clicked).All(c => c.Model.Locked);

    /// <summary>
    /// Locks every target, or — when every one is already locked — unlocks them all. One undo step, saved, and
    /// dirtying the document.
    /// </summary>
    internal void ToggleLock(ControlContainerViewModel clicked)
    {
        var targets = LockTargets(clicked);
        bool lockThem = !targets.All(c => c.Model.Locked);
        var cmds = new List<IUndoableCommand>();
        foreach (var c in targets)
        {
            if (c.Model.Locked == lockThem) continue;
            var before = c.Model.Clone();
            c.Model.Locked = lockThem;
            cmds.Add(new EditControlCommand(c, before, c.Model.Clone()));
            c.Changed();
        }
        if (cmds.Count == 0) return;
        Gesture.Note(lockThem ? "control.lock" : "control.unlock", string.Join(",", targets.Select(t => t.Model.Name)));
        UndoRedo.Push(cmds.Count == 1 ? cmds[0] : new CompositeCommand(cmds));
    }

    // ---- moving with the selection ------------------------------------------------------------------

    private List<(ControlContainerViewModel Vm, double Left, double Top)>? _moveStartControls;

    /// <summary>The controls a group move carries: the selected ones that are not locked.</summary>
    private IEnumerable<ControlContainerViewModel> MovingControls()
        => _controls.Where(c => c.IsSelected && !c.Model.Locked);

    private void BeginControlMove()
        => _moveStartControls = MovingControls().Select(c => (c, c.Left, c.Top)).ToList();

    private void MoveControls(double dx, double dy)
    {
        foreach (var c in MovingControls())
        {
            c.Left += dx;
            c.Top  += dy;
        }
    }

    private List<ControlMoveSnapshot> EndControlMove()
    {
        var snaps = (_moveStartControls ?? []).Select(s => new ControlMoveSnapshot(s.Vm, s.Left, s.Top, s.Vm.Left, s.Vm.Top))
                                              .Where(s => s.StartLeft != s.EndLeft || s.StartTop != s.EndTop).ToList();
        _moveStartControls = null;
        return snaps;
    }

    /// <summary>Starts a move from a control's frame: it is selected first when it is not, as a plot is.</summary>
    internal void BeginMoveFromControl(ControlContainerViewModel vm)
    {
        if (!vm.IsSelected) SelectOnly(vm);
        BeginMoveOperation();
    }

    // ---- save and load ------------------------------------------------------------------------------

    private List<DisplayControl>? ControlsForSave()
        => _controls.Count > 0 ? _controls.Select(c =>
        {
            var m = c.Model.Clone();
            m.ZIndex = c.ZIndex;   // the stacking as it is now: a control selected last is on top when reopened
            return m;
        }).ToList() : null;

    /// <summary>
    /// A bound slider's source is loaded as a trace's is, even when no trace reads it, and its stops are read at once —
    /// so it opens on its axis rather than as a one-stop slider until the first link resolves. A source that is not
    /// there keeps the range the file saved.
    /// </summary>
    private async System.Threading.Tasks.Task LoadControlSourcesAsync()
    {
        if (Library is null) return;
        foreach (var slider in _controls.Select(c => c.Model).OfType<SteppedControl>())
        {
            if (slider.Domain != SliderDomain.Bound || slider.SourcePath is not { Length: > 0 } sref) continue;
            if (sref != DataSourceRef.Selected && Library.ResolveAbs(sref) is { } abs && System.IO.File.Exists(abs)
                && !Library.Entries.Any(e => string.Equals(e.FilePath, abs, StringComparison.OrdinalIgnoreCase)))
                await Library.LoadFileAsync(abs);
            ControlLinkSource.Refresh(slider, new LibraryDataSources(Library));
        }
    }

    private void LoadControls(IEnumerable<DisplayControl>? controls)
    {
        _controls.Clear();
        SyncControlLayers();
        foreach (var c in controls ?? []) AdoptControl(c);
    }
}
