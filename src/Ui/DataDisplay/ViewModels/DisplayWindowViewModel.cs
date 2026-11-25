// ================================================================
//  DisplayWindowViewModel.cs  —  ViewModel for one DisplayWindow.
//
//  Owns the tab collection and the active-tab proxy.  All plot-level
//  commands (AddPlot, ZoomIn, Cut/Copy/Paste …) operate on the active
//  tab's DataDisplayViewModel via the DataDisplay proxy property.
//
//  Save / Load are multi-tab aware — the .splot file stores all tabs
//  via DataDisplayConfig.Tabs (v2 format).  Legacy v1 files (Plots at
//  root level) are loaded as a single tab.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DisplayWindowViewModel : ViewModelBase, ITrialSelectionListener
{
    // ---- Data source library -----------------------------------------------

    public DataSourceLibraryViewModel DataSourceLibrary { get; } = new();

    // ---- Window-level undo/redo (tab add / remove) -----------------------
    //
    // Tab operations live here rather than on a per-tab stack because they
    // affect the window, not a single tab's canvas.
    //
    // Undo/Redo routing: prefer the active tab's within-tab stack; fall back
    // to this stack when the tab stack is empty.  This gives chronologically
    // correct behaviour for the common case (undo my last plot action, then
    // undo the tab action I did before that).

    public UndoRedoManager TabUndoRedo { get; } = new();

    // ---- Tabs and active tab -----------------------------------------------

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    private TabViewModel? _activeTab;

    // Track the tab whose DataDisplay.PropertyChanged we are subscribed to.
    private TabViewModel? _subscribedTab;

    partial void OnActiveTabChanged(TabViewModel? value)
    {
        // Unsubscribe from the previous active tab's property and undo events.
        if (_subscribedTab?.DataDisplay is { } old)
        {
            old.MarkerToolArmed = false;
            old.PropertyChanged       -= OnActiveDisplayPropertyChanged;
            old.UndoRedo.StateChanged -= OnUndoRedoStateChanged;
            old.ContentChanged        -= OnActiveDisplayContentChanged;
        }

        _subscribedTab = value;

        // Subscribe to the new active tab.
        if (value?.DataDisplay is { } dd)
        {
            dd.PropertyChanged       += OnActiveDisplayPropertyChanged;
            dd.UndoRedo.StateChanged += OnUndoRedoStateChanged;
            dd.ContentChanged        += OnActiveDisplayContentChanged;
        }

        // Notify bindings that DataDisplay changed.
        OnPropertyChanged(nameof(DataDisplay));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(ActiveInspector));
        RemovePlotCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnUndoRedoStateChanged(object? sender, System.EventArgs e)
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RaiseDirtyChanged();
    }

    private void OnActiveDisplayPropertyChanged(
        object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DataDisplayViewModel.HasAnySelection))
        {
            RemovePlotCommand.NotifyCanExecuteChanged();
            CutCommand.NotifyCanExecuteChanged();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
        if (e.PropertyName == nameof(DataDisplayViewModel.HasSingleSelection))
        {
            OnPropertyChanged(nameof(HasSingleSelection));
            OnPropertyChanged(nameof(ActiveInspector));
        }
    }

    // Null-safe proxy: ActiveTab is transiently null while LoadAllAsync rebuilds
    // the Tabs collection (the TabControl's TwoWay SelectedItem binding pushes
    // null back when Tabs.Clear() removes the currently-selected item).
    // All commands guard against null via their CanExecute predicates.
    public DataDisplayViewModel? DataDisplay => ActiveTab?.DataDisplay;

    // Inspector proxies — never null-intermediate, so XAML binds directly
    // to these instead of traversing DataDisplay?.HasSingleSelection.
    public bool                    HasSingleSelection => DataDisplay?.HasSingleSelection ?? false;
    public PlotInspectorViewModel? ActiveInspector    => DataDisplay?.ActiveInspector;

    // ---- Datasource combo binding (single-source brief) -------------------

    /// <summary>
    /// Bound two-way to the toolbar datasource combo. Setting calls SelectDataSourceAsync.
    /// </summary>
    public DataSourceItem? SelectedDataSourceItem
    {
        // By logical id, else by the file it resolves to: the Yield panel's own display names its result by its full
        // path, and the list names the same file relative to the results root.
        get => DataSourceLibrary.AvailableDataSources
                   .FirstOrDefault(i => i.LogicalId == DataSourceLibrary.SelectedDataSourceRef)
            ?? (DataSourceLibrary.SelectedDataSourceAbs is { } abs
                   ? DataSourceLibrary.AvailableDataSources.FirstOrDefault(i =>
                         string.Equals(Path.GetFullPath(i.AbsolutePath), Path.GetFullPath(abs), StringComparison.OrdinalIgnoreCase))
                   : null);
        set
        {
            if (value is null) return;
            string? was = DataSourceLibrary.SelectedDataSourceRef;
            if (was == value.LogicalId) return;
            _ = DataSourceLibrary.SelectDataSourceAsync(value.LogicalId);
            OnPropertyChanged();
            // The selection is saved in the .cdd, so changing it is an edit like any other: undoable,
            // or the document could never be made clean again by undoing.
            if (was is not null)
                DataDisplay?.UndoRedo.Push(new SelectDataSourceCommand(this, was, value.LogicalId));
        }
    }

    // ---- Canvas grid (the symbol editor's snap button, for this canvas) ------------------------

    private CanvasGrid _canvasGrid = CanvasGrid.Off;

    /// <summary>The snap grid for every tab of this display: coarse, fine or off.</summary>
    public CanvasGrid CanvasGrid
    {
        get => _canvasGrid;
        private set
        {
            if (!SetProperty(ref _canvasGrid, value)) return;
            foreach (var tab in Tabs) tab.DataDisplay.CanvasGrid = value;
            OnPropertyChanged(nameof(IsGridCoarse));
            OnPropertyChanged(nameof(IsGridFine));
            OnPropertyChanged(nameof(GridTooltip));
        }
    }

    public bool IsGridCoarse => CanvasGrid == CanvasGrid.Coarse;
    public bool IsGridFine   => CanvasGrid == CanvasGrid.Fine;

    public string GridTooltip => CanvasGrid switch
    {
        CanvasGrid.Coarse => "Snap: Coarse Grid — plots move and resize onto it  (click for Fine)",
        CanvasGrid.Fine   => "Snap: Fine Grid — plots move and resize onto it  (click for Off)",
        _                 => "Snap: Off  (click for Coarse Grid)",
    };

    /// <summary>Coarse → fine → off, as the symbol editor's snap button cycles.</summary>
    [RelayCommand]
    private void CycleCanvasGrid() => CanvasGrid = CanvasGrid switch
    {
        CanvasGrid.Off    => CanvasGrid.Coarse,
        CanvasGrid.Coarse => CanvasGrid.Fine,
        _                 => CanvasGrid.Off,
    };

    /// <summary>Freeze on or off for the whole display (<see cref="DataSourceLibraryViewModel.IsFrozen"/>).</summary>
    [RelayCommand]
    private void ToggleFreeze() => DataSourceLibrary.IsFrozen = !DataSourceLibrary.IsFrozen;

    /// <summary>Beside the Freeze button while it is on: how many earlier runs it is holding.</summary>
    public string FrozenRunsText => DataSourceLibrary.FrozenRunCount switch
    {
        0 => "Frozen",
        1 => "1 earlier run",
        int n => $"{n} earlier runs",
    };

    /// <summary>Selects a datasource by its logical id for an undo or redo, and refreshes the combo.</summary>
    internal async void SelectDataSourceForUndo(string logicalId)
    {
        await DataSourceLibrary.SelectDataSourceAsync(logicalId);
        OnPropertyChanged(nameof(SelectedDataSourceItem));
        OnPropertyChanged(nameof(AwaitingRunText));
        RaiseDirtyChanged();
    }

    /// <summary>
    /// What the toolbar's source combo reads while it has nothing to show because the selected run
    /// has not happened yet (<see cref="DataSourceLibraryViewModel.SelectedSourceAwaitsRun"/>) —
    /// "Amp — not yet run" rather than a blank combo over plots of invalid traces.
    /// </summary>
    public string? AwaitingRunText =>
        DataSourceLibrary.SelectedSourceAwaitsRun
            ? $"{Path.GetFileNameWithoutExtension(DataSourceLibrary.SelectedDataSourceRef)} \u2014 not yet run"
            : null;

    /// <summary>"Tuning" or "Optimizing" while this display shows published data (R-to3-8); null otherwise.</summary>
    public string? LiveChip => DataSourceLibrary.PublishedChip;

    /// <summary>
    /// <c>Trial 417</c> or <c>23 trials</c> while a source this display holds has trials selected (brief-yield-9
    /// R-ya9-6); null otherwise. Esc clears the selection.
    /// </summary>
    public string? TrialChip
    {
        get
        {
            var mine = DataSourceLibrary.Entries.Select(e => e.FilePath).OfType<string>()
                .Select(p => TrialSelection.Shared.For(p)).OfType<IReadOnlySet<int>>().ToList();
            return mine.Count == 0 ? null : TrialPick.ChipText([.. mine.SelectMany(s => s)]);
        }
    }

    /// <inheritdoc/>
    public void OnTrialSelectionChanged()
    {
        OnPropertyChanged(nameof(TrialChip));
        // A pin following the selection follows it (brief-ddl-4 R-ddl4-5) — a click is a release, not a drag.
        if (FollowsSelection()) RequestLinkFollow(released: true, announce: false);
    }

    /// <summary>Whether any trace in this display follows the trial selection.</summary>
    private bool FollowsSelection()
        => Tabs.SelectMany(t => t.DataDisplay.Plots).SelectMany(c => c.PlotVM.Plot.Traces).Any(t =>
               (t.Slice?.Any(e => LinkDocument.IsSelection(e.Link) || LinkDocument.IsSelection(e.LinkName)) ?? false)
               || (t.CubeName is null && t.Expression is { } x && LinkGraph.OperandNames(x).Any(LinkDocument.IsSelection)));

    /// <summary>Pass-through: enumerate results + workspace Touchstone without loading any file.</summary>
    public void RefreshAvailableDataSources()
    {
        DataSourceLibrary.RefreshAvailableDataSources();
        OnPropertyChanged(nameof(SelectedDataSourceItem));
        OnPropertyChanged(nameof(AwaitingRunText));
    }

    // ---- Unsaved-changes tracking ----------------------------------------
    //
    // _baselineConfigJson stores a serialised snapshot of the display config
    // at the last save/load — excluding window geometry and per-tab zoom/offset
    // (those are exempt from the "unsaved changes" prompt per the spec).
    //
    // null  → window was just created and has never been saved/loaded.
    // ""    → treated as "initial empty state"; compared with current.
    //
    // HasUnsavedChanges() serialises the current config the same way and does
    // a simple string comparison.  This is fast and covers all DisplayConfig
    // fields without requiring a dedicated property-change tracking system.

    private string? _baselineConfigJson;

    /// <summary>
    /// Fired when the document's unsaved state may have changed (content edit, save, or load).
    /// The hosting DataDisplayDocumentViewModel recomputes IsDirty = HasUnsavedChanges() on this.
    /// </summary>
    public event EventHandler? DirtyChanged;

    /// <summary>
    /// Fired after a successful save with the absolute path of the written .cdd file.
    /// DataDisplayDocument subscribes to update its tab title and Id.
    /// </summary>
    public event Action<string>? ConfigPathSaved;

    private void RaiseDirtyChanged() => DirtyChanged?.Invoke(this, EventArgs.Empty);
    private void OnActiveDisplayContentChanged(object? s, EventArgs e) => RaiseDirtyChanged();

    /// <summary>
    /// Returns true when the current display config differs from the last
    /// save/load baseline (ignoring window geometry and zoom level).
    /// Returns false for a brand-new window the user has never touched.
    /// </summary>
    public bool HasUnsavedChanges()
    {
        string current = BuildComparisonJson();

        // Newly created window: baseline was set from the initial default state.
        // Compare against that — if equal the user never touched anything.
        if (_baselineConfigJson is null) return false;

        return current != _baselineConfigJson;
    }

    /// <summary>
    /// Captures the current display config as the "clean" baseline.
    /// Call after Save, SaveAs, and Load so HasUnsavedChanges() returns false
    /// immediately after those operations.
    /// </summary>
    private void CaptureBaseline()
        => _baselineConfigJson = BuildComparisonJson();

    /// <summary>
    /// Builds a serialised representation of the display config suitable for
    /// dirty-checking.  Excludes window geometry and per-tab zoom/offset so
    /// those changes do not trigger the "unsaved changes" prompt.
    /// Uses absolute source paths (configDir = "") for consistency regardless
    /// of where the file happens to be saved.
    /// </summary>
    private string BuildComparisonJson()
    {
        var config = new DataDisplayConfig
        {
            // Window geometry is excluded (zeroed) — not a "meaningful" change.
            // ActiveTabIndex is excluded — tab-switch alone should not prompt.
            SelectedDataSource = DataSourceLibrary.SelectedDataSourceRef,
        };

        foreach (var tab in Tabs)
        {
            var full = tab.DataDisplay.BuildTabConfig(tab.Name, configDir: "");
            // Stacking is compared as an ORDER, not as the indices: bringing an item forward hands it a fresh index,
            // and an order put back by undo is the saved one again even though the numbers moved on.
            int rank = 0;
            foreach (var pc in full.Plots.OrderBy(p => p.ZIndex)) pc.ZIndex = rank++;
            if (full.Controls is { } controls)
                foreach (var layer in controls.GroupBy(c => c.IsPanel))
                {
                    rank = 0;
                    foreach (var c in layer.OrderBy(c => c.ZIndex)) c.ZIndex = rank++;
                }
            // Create a copy with ZoomLevel/ViewOffset zeroed so those changes
            // do not count as "unsaved".
            config.Tabs.Add(new TabConfig
            {
                Name  = full.Name,
                Plots = full.Plots,
                Annotations = full.Annotations,
                Controls = full.Controls,
                // ZoomLevel / ViewOffsetX / ViewOffsetY left at default 0
            });
        }

        return JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
    }

    // ---- Config path and window title -------------------------------------

    private string? _currentConfigPath;
    public  string? CurrentConfigPath => _currentConfigPath;

    [ObservableProperty]
    private string _windowTitle = "circuitRF";

    // ---- Window geometry event -------------------------------------------

    /// <summary>
    /// Fired after LoadAllAsync when the config contains window geometry.
    /// Arguments: (left, top, width, height) in the same units as SaveAllAsync.
    /// </summary>
    public event Action<double, double, double, double>? WindowGeometryLoaded;

    // ---- Theme -----------------------------------------------------------

    [ObservableProperty]
    private RenderTheme _currentTheme = RenderTheme.Light;

    partial void OnCurrentThemeChanged(RenderTheme value)
    {
        foreach (var tab in Tabs)
            tab.DataDisplay.Theme = value;
    }

    // ---- Inspector visibility --------------------------------------------

    [ObservableProperty]
    private bool _isInspectorOpen = false;

    // ---- Settings --------------------------------------------------------

    public AppSettingsViewModel AppSettings => AppSettingsViewModel.Instance;

    // ---- Dialog / clipboard delegates (injected from code-behind) --------

    private Func<Task>?                                        _openFileAction;
    private Func<Task>?                                        _openDataDisplayAction;
    private Func<Task>?                                        _saveDataDisplayAsAction;
    private Action?                                            _openSettingsAction;
    private Action?                                            _newDisplayAction;
    private Action?                                            _closeWindowAction;
    private Action?                                            _quitApplicationAction;
    private Func<(double W, double H)>?                        _getCanvasSizeAction;
    private Func<(double L, double T, double W, double H)>?    _getWindowGeometryAction;
    private Func<Task<string?>>?                               _getClipboardTextAction;
    private Func<IReadOnlyList<PlotContainerViewModel>, RenderTheme, Task>? _richCopyAction;
    private Func<IReadOnlyList<PlotContainerViewModel>, RenderTheme, AnnotationPage, Task>? _annotationCopyAction;
    public void SetAnnotationCopyAction(Func<IReadOnlyList<PlotContainerViewModel>, RenderTheme, AnnotationPage, Task> action)
        => _annotationCopyAction = action;
    // Injected by WorkspaceViewModel: opens the given file as a new document tab.
    private Func<string, Stream, Task>?                        _openFileAsNewDisplayAction;
    // Injected by code-behind: opens folder picker scoped to workspace results/.
    private Func<Task>?                                        _loadRunResultsAction;
    // Injected by code-behind: opens the Data Exporter dialog.
    private Func<Task>?                                        _exportDataAction;
    // Injected by WorkspaceViewModel: returns <workspaceRoot>/results, or null when no workspace.
    public Func<string?>?                                      GetResultsRootAction { get; set; }

    public void SetOpenFileAction(Func<Task> a)                           => _openFileAction               = a;
    public void SetOpenDataDisplayAction(Func<Task> a)                    => _openDataDisplayAction        = a;
    public void SetSaveDataDisplayAsAction(Func<Task> a)                  => _saveDataDisplayAsAction      = a;
    public void SetOpenSettingsAction(Action a)                           => _openSettingsAction           = a;
    public void SetNewDisplayAction(Action a)                             => _newDisplayAction             = a;
    public void SetCloseWindowAction(Action a)                            => _closeWindowAction            = a;
    public void SetQuitApplicationAction(Action a)                        => _quitApplicationAction        = a;
    public void SetGetCanvasSizeAction(Func<(double, double)> a)          => _getCanvasSizeAction          = a;
    public void SetGetWindowGeometryAction(Func<(double, double, double, double)> a)
        => _getWindowGeometryAction = a;
    public void SetGetClipboardTextAction(Func<Task<string?>> a)
    {
        _getClipboardTextAction = a;
        PasteCommand.NotifyCanExecuteChanged();
    }
    public void SetRichCopyAction(Func<IReadOnlyList<PlotContainerViewModel>, RenderTheme, Task> a)
        => _richCopyAction = a;
    public void SetOpenFileAsNewDisplayAction(Func<string, Stream, Task> a) => _openFileAsNewDisplayAction = a;
    public void SetLoadRunResultsAction(Func<Task> a)                     => _loadRunResultsAction         = a;
    public void SetExportDataAction(Func<Task> a)                         => _exportDataAction              = a;

    /// <summary>
    /// Opens <paramref name="path"/> as a new document tab via the workspace-injected action.
    /// Falls back to loading into this document when running outside the workspace (standalone/tests).
    /// </summary>
    public async Task OpenFileAsNewDisplayAsync(string path, Stream stream)
    {
        if (_openFileAsNewDisplayAction is not null)
            await _openFileAsNewDisplayAction(path, stream);
        else
            await LoadAllAsync(path, stream);
    }

    // ---- Constructor -----------------------------------------------------

    public DisplayWindowViewModel()
    {
        var initialTab = CreateNewTab("circuitRF");
        Tabs.Add(initialTab);
        ActiveTab = initialTab;   // also triggers OnActiveTabChanged → subscribe

        // Tab-level undo state changes must also refresh the Undo/Redo commands.
        TabUndoRedo.StateChanged += OnUndoRedoStateChanged;

        // R-to3-8: the Tuning / Optimizing chip follows the library's publications. In the application
        // a published frame ends behind the render pass, so a burst of results coalesces to the newest
        // (R-to3-6); headless (no Application) a frame ends at once.
        DataSourceLibrary.PublicationChanged += (_, _) => OnPropertyChanged(nameof(LiveChip));
        TrialSelection.Shared.Subscribe(this);
        if (Application.Current is not null)
        {
            DataSourceLibrary.FrameScheduler = endFrame =>
                Avalonia.Threading.Dispatcher.UIThread.Post(endFrame, Avalonia.Threading.DispatcherPriority.Background);
            // A drag's moves coalesce to one follow per rendered frame (brief-ddl-2 R-ddl2-3).
            LinkFrameScheduler = follow =>
                Avalonia.Threading.Dispatcher.UIThread.Post(follow, Avalonia.Threading.DispatcherPriority.Background);
        }

        // Keep the combo selection in sync with the library selection.
        DataSourceLibrary.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DataSourceLibraryViewModel.FrozenRunCount)
                               or nameof(DataSourceLibraryViewModel.IsFrozen))
                OnPropertyChanged(nameof(FrozenRunsText));
        };
        DataSourceLibrary.SelectedDataSourceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedDataSourceItem));
            OnPropertyChanged(nameof(AwaitingRunText));
            AddStatisticsTableCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(AddStatisticsTableTip));
            AddNoiseCirclesCommand.NotifyCanExecuteChanged();
            AddSignalQualityCommand.NotifyCanExecuteChanged();
            AddTransientCommand.NotifyCanExecuteChanged();
            AddHbCommand.NotifyCanExecuteChanged();
            RaiseDirtyChanged();
        };
        // A run finishing reloads the selected file in place — it may only now carry its statistics.
        DataSourceLibrary.LibraryChanged += (_, _) =>
        {
            AddStatisticsTableCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(AddStatisticsTableTip));
            AddNoiseCirclesCommand.NotifyCanExecuteChanged();
            AddSignalQualityCommand.NotifyCanExecuteChanged();
            AddTransientCommand.NotifyCanExecuteChanged();
            AddHbCommand.NotifyCanExecuteChanged();
        };

        UpdateThemeFromSystem();
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += (_, _) => UpdateThemeFromSystem();

        // Capture the initial state so HasUnsavedChanges() returns false for
        // a brand-new window with a default plot but no user changes.
        CaptureBaseline();
    }

    private void UpdateThemeFromSystem()
    {
        CurrentTheme = Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;
    }

    private TabViewModel CreateNewTab(string name)
    {
        var tab = new TabViewModel(DataSourceLibrary, name, Tabs.Count < 1); // add empty plot only if there's no tabs
        tab.DataDisplay.Theme = CurrentTheme;
        tab.DataDisplay.CanvasGrid = _canvasGrid;
        // New-plot auto-placement needs the visible viewport size. _getCanvasSizeAction
        // resolves the active tab's canvas; add-plot only ever targets the active tab, so
        // wiring every tab's DataDisplay to it is correct. Null-safe until the action is injected.
        tab.DataDisplay.CanvasSizeProvider = () => _getCanvasSizeAction?.Invoke() ?? (0.0, 0.0);
        tab.CloseRequested += (sender, _) => RemoveTab(sender as TabViewModel);
        // Marker names and links are the DOCUMENT's, not the tab's (brief-ddl-1 R-ddl1-2, R-ddl1-5).
        tab.DataDisplay.DocumentMarkerIndexProvider = GetNextMarkerIndex;
        tab.DataDisplay.LinkDocumentProvider        = BuildLinkDocument;
        tab.DataDisplay.LinkFollowRequested         = RequestLinkFollow;
        tab.DataDisplay.LinkFollowNow               = editing => FollowLinks(released: true, editing);
        tab.DataDisplay.DocumentDisplaysProvider    = () => [.. Tabs.Select(t => t.DataDisplay)];
        // Control names are the document's, as marker names are (brief-ddl-5 R-ddl5-1).
        tab.DataDisplay.DocumentControlsProvider    = () => Tabs.SelectMany(t => t.DataDisplay.Controls).Select(c => c.Model);
        return tab;
    }

    // ---- Live follow (brief-ddl-2 R-ddl2-3) --------------------------------------------------------------

    /// <summary>
    /// Posts work to the end of the frame being rendered. A drag reports a move per pointer event, many per frame;
    /// every move asks for a follow, and only the first in a frame posts one, which then reads the latest position.
    /// Null (no Application — a test, a headless host) runs the follow at once.
    /// </summary>
    internal Action<Action>? LinkFrameScheduler { get; set; }

    /// <summary>How many times the display's links have been re-resolved — what the coalescing gate counts.</summary>
    internal int LinkFollowRuns { get; private set; }

    /// <summary>How many consumer traces those runs re-resolved, in total.</summary>
    internal int LinkConsumerResolutions { get; private set; }

    private bool _followPending, _followReleased, _followAnnounce, _following;

    internal void RequestLinkFollow(bool released, bool announce)
    {
        // What a follow does to the plots it touches (a label, a legend box) reports changes of its own; they are
        // its result, not a new move, and following them again would never end.
        if (_following) return;
        _followReleased |= released;
        _followAnnounce |= announce;
        if (_followPending) return;
        _followPending = true;
        if (LinkFrameScheduler is { } post) post(RunPendingFollow);
        else RunPendingFollow();
    }

    private void RunPendingFollow()
    {
        if (!_followPending) return;
        bool released = _followReleased, announce = _followAnnounce;
        _followPending = _followReleased = _followAnnounce = false;
        FollowLinks(released, editing: null, announce);
    }

    /// <summary>
    /// Re-resolves every linked pin of the display, in dependency order, and has each tab show the result. With
    /// <paramref name="announce"/>, a link that froze in this run — its plot or trace was deleted — is named once in
    /// Messages (R-ddl2-8).
    /// </summary>
    internal void FollowLinks(bool released, PlotContainerViewModel? editing = null, bool announce = false)
    {
        if (_following) return;
        _following = true;
        try { FollowLinksCore(released, editing, announce); }
        finally { _following = false; }
    }

    private void FollowLinksCore(bool released, PlotContainerViewModel? editing, bool announce)
    {
        LinkFollowRuns++;
        var doc = BuildLinkDocument();
        var wasFrozen = doc.Traces().SelectMany(x => x.Trace.FrozenLinks.Select(k => (x.Trace, k))).ToHashSet();
        var resolved = LinkResolve.Resolve(doc, new LibraryDataSources(DataSourceLibrary), released);
        LinkConsumerResolutions += resolved.Count;
        foreach (var tab in Tabs) tab.DataDisplay.RedrawReadouts();   // a readout shows sources, whatever followed
        _variables?.Publish();   // a subscriber hears of every variable that moved, whoever moved it (brief-ddl-7)
        if (resolved.Count == 0) return;
        var set = resolved.ToHashSet();
        foreach (var tab in Tabs) tab.DataDisplay.AfterLinksFollowed(set, editing);
        if (announce) AnnounceFrozen(doc, wasFrozen);
    }

    /// <summary>"Pout and Gt on Detail no longer follow m1; they show gridPoint 17."</summary>
    private void AnnounceFrozen(LinkDocument doc, HashSet<(Trace, string)> wasFrozen)
    {
        if (DataSourceLibrary.LinkMessage is not { } post) return;
        var fresh = new List<(int Tab, Plot Plot, Trace Trace, AxisSlice Pin)>();
        foreach (var (ti, plot, t) in doc.Traces())
            foreach (var key in t.FrozenLinks)
                if (!wasFrozen.Contains((t, key)) && t.Slice?.FirstOrDefault(s => (s.Link ?? s.LinkName) == key) is { } pin)
                    fresh.Add((ti, plot, t, pin));
        foreach (var g in fresh.GroupBy(f => (f.Pin.LinkName, f.Plot)))
        {
            var items = g.ToList();
            string names = string.Join(" and ", items.Select(i => LinkGraph.NameOf(i.Trace)).Distinct());
            string plot  = LinkCatalog.PlotName(doc, items[0].Tab, g.Key.Plot, items[0].Tab);
            string they  = items.Count == 1 ? "it shows" : "they show";
            post($"{names} on {plot} no longer follow{(items.Count == 1 ? "s" : "")} {g.Key.LinkName}; "
               + $"{they} {items[0].Pin.AxisName} {items[0].Pin.Index}.");
        }
    }

    /// <summary>
    /// The lowest <c>m&lt;n&gt;</c> free across EVERY tab of this display (brief-ddl-1 R-ddl1-2), so a typed
    /// <c>@m1</c> means one marker. Names already duplicated across tabs in an existing file are left alone (DDL-0 D5).
    /// </summary>
    public int GetNextMarkerIndex()
        => LinkDocument.NextMarkerIndex(Tabs.SelectMany(t => t.DataDisplay.Plots)
                                            .SelectMany(c => c.PlotVM.Plot.Traces)
                                            .SelectMany(t => t.Markers));

    /// <summary>Every tab and plot of this display, as the links see them.</summary>
    internal LinkDocument BuildLinkDocument()
        => new([.. Tabs.Select(t => new LinkDocument.Tab(t.Name, [.. t.DataDisplay.Plots.Select(c => c.PlotVM.Plot)],
                                                        [.. t.DataDisplay.Controls.Select(c => c.Model)]))])
        {
            // A pin following `@selected` reads the shared selection (brief-ddl-4 R-ddl4-5).
            SelectedTrials = TrialSelection.Shared.For,
        };

    /// <summary>
    /// Resolves every linked pin of the display through the one resolution path (<see cref="LinkResolve"/>, which
    /// <c>circuitrf render</c> also calls), and redraws what moved.
    /// </summary>
    internal void ResolveLinks() => FollowLinks(released: true);

    // ---- Internal tab add / remove (called by undo commands) ------------

    /// <summary>
    /// Inserts <paramref name="tab"/> at <paramref name="index"/> without
    /// recording an undo entry.  Called by AddTabCommand and RemoveTabCommand.
    /// </summary>
    internal void InternalAddTab(TabViewModel tab, int index, bool makeActive)
    {
        index = Math.Clamp(index, 0, Tabs.Count);
        Tabs.Insert(index, tab);
        if (makeActive) ActiveTab = tab;
    }

    /// <summary>
    /// Removes <paramref name="tab"/> without recording an undo entry.
    /// Activates the nearest remaining tab.  No-ops if only one tab is open.
    /// Called by AddTabCommand.Undo() and RemoveTabCommand.Execute().
    /// </summary>
    internal void InternalRemoveTab(TabViewModel tab)
    {
        if (Tabs.Count <= 1) return; // always keep at least one tab open
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;

        Tabs.Remove(tab);

        // The TabControl's TwoWay SelectedItem binding may push null into
        // ActiveTab when the selected tab is removed — restore it here.
        if (ActiveTab is null || !Tabs.Contains(ActiveTab))
            ActiveTab = Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
    }

    private void RemoveTab(TabViewModel? tab)
    {
        if (tab is null || Tabs.Count <= 1) return;
        int  idx       = Tabs.IndexOf(tab);
        bool wasActive = ReferenceEquals(tab, ActiveTab);
        if (idx < 0) return;

        TabUndoRedo.Do(new RemoveTabCommand(tab, this, idx, wasActive));
    }

    // ---- Tab management -------------------------------------------------

    [RelayCommand]
    private void NewTab()
    {
        // Choose a unique "Tab N" name.
        int n = Tabs.Count + 1;
        while (Tabs.Any(t => t.Name == $"Tab {n}")) n++;

        var tab = CreateNewTab($"Tab {n}");
        TabUndoRedo.Do(new AddTabCommand(tab, this));
    }

    // ---- New display / Close / Quit -------------------------------------

    [RelayCommand]
    private void NewDisplay() => _newDisplayAction?.Invoke();

    [RelayCommand]
    private void CloseWindow() => _closeWindowAction?.Invoke();

    [RelayCommand]
    private void QuitApplication() => _quitApplicationAction?.Invoke();

    // ---- Commands -------------------------------------------------------

    [RelayCommand]
    private async Task OpenFile()
    {
        if (_openFileAction is not null) await _openFileAction();
    }

    [RelayCommand]
    private async Task LoadRunResults()
    {
        if (_loadRunResultsAction is not null) await _loadRunResultsAction();
    }

    [RelayCommand]
    private async Task ExportData()
    {
        if (_exportDataAction is not null) await _exportDataAction();
    }

    [RelayCommand]
    private void OpenSettings() => _openSettingsAction?.Invoke();

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private void AddPlot() => AddUserPlot(PlotType.Rect);

    /// <summary>A plot from the toolbar: on a card when Settings ▸ Appearance ▸ Plots as cards is on.</summary>
    private void AddUserPlot(PlotType type) => DataDisplay?.AddPlot(type, card: PlotCardPreference.NewPlots);

    [RelayCommand] private void AddSmithPlot() => AddUserPlot(PlotType.Smith);
    [RelayCommand] private void AddPolarPlot() => AddUserPlot(PlotType.Polar);
    [RelayCommand] private void AddTablePlot() => AddUserPlot(PlotType.Table);

    /// <summary><b>Controls ▸ Slider</b> (brief-ddl-5 R-ddl5-7).</summary>
    [RelayCommand] private void AddSlider() => DataDisplay?.AddSlider();

    /// <summary><b>Controls ▸ Knob</b>, <b>Button</b> and <b>Readout</b> (brief-ddl-6).</summary>
    [RelayCommand] private void AddKnob() => DataDisplay?.AddKnob();
    [RelayCommand] private void AddButton() => DataDisplay?.AddButton();
    [RelayCommand] private void AddReadout() => DataDisplay?.AddReadout();

    /// <summary><b>Controls ▸ Panel</b>: a blank card under the plots and controls.</summary>
    [RelayCommand] private void AddPanel() => DataDisplay?.AddPanel();

    /// <summary>The statistics table of the selected Monte Carlo result (brief-yield-8 R-ya8-5). Disabled, rather than
    /// pressed to no effect, while the selected source carries no statistics — its tooltip says what it needs.</summary>
    [RelayCommand(CanExecute = nameof(CanAddStatisticsTable))]
    private async Task AddStatisticsTable()
    {
        if (DataDisplay is { } d) await d.AddStatisticsTableAsync();
    }

    /// <summary>brief NZ-2 — the Noise and gain circles chart of the selected source's two-port noise result. Disabled, with
    /// its tooltip saying what it needs, while the selected source has none.</summary>
    [RelayCommand(CanExecute = nameof(CanAddNoiseCircles))]
    private async Task AddNoiseCircles()
    {
        if (DataDisplay is { } d) await d.AddNoiseCirclesAsync();
    }

    /// <summary>brief NZ-8 — the constellation, EVM table and spectrum of the selected envelope result. Disabled, with its
    /// tooltip saying what it needs, while the selected source has no EVM.</summary>
    [RelayCommand(CanExecute = nameof(CanAddSignalQuality))]
    private async Task AddSignalQuality()
    {
        if (DataDisplay is { } d) await d.AddSignalQualityAsync();
    }

    /// <summary>brief TRN-5 — the waveforms, output spectrum and measures of the selected transient result. Disabled, with its
    /// tooltip saying what it needs, while the selected source has no transient result.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTransient))]
    private async Task AddTransient()
    {
        if (DataDisplay is { } d) await d.AddTransientAsync();
    }

    /// <summary>Whether the selected source holds a transient result.</summary>
    public bool CanAddTransient => TransientPreset.Available(DataSourceLibrary.SelectedEntry?.Data);

    /// <summary>R-rdh-2 — the waveforms, output spectrum and measures of the selected harmonic-balance result. Disabled, with
    /// its tooltip saying what it needs, while the selected source has no HB result.</summary>
    [RelayCommand(CanExecute = nameof(CanAddHb))]
    private async Task AddHb()
    {
        if (DataDisplay is { } d) await d.AddHbAsync();
    }

    /// <summary>Whether the selected source holds a harmonic-balance result.</summary>
    public bool CanAddHb => HbPreset.Available(DataSourceLibrary.SelectedEntry?.Data);

    /// <summary>
    /// R-rdh-1 — true when this display holds traces and not one of them draws anything: every trace that reads a source is
    /// <c>&lt;invalid&gt;</c>, on every tab. The run's analysis changed (a transient bench now run in harmonic balance) or a
    /// net its traces pin was renamed, and the plot is empty with the run a success. Annotations and owner-data traces read
    /// no source and are not counted; a display with none of the rest is not "showing nothing" of a run, it is empty.
    /// </summary>
    public bool ShowsNothingOfItsSource()
    {
        int reading = 0;
        foreach (var tab in Tabs)
            foreach (var c in tab.DataDisplay.Plots)
                foreach (var t in c.PlotVM.Plot.Traces)
                {
                    if (t.IsAnnotation || t.IsOwnerData) continue;
                    if (t.InvalidSpecText is null) return false;
                    reading++;
                }
        return reading > 0;
    }

    /// <summary>Whether the selected source is an envelope result that measured EVM.</summary>
    public bool CanAddSignalQuality => SignalQualityPreset.Available(DataSourceLibrary.SelectedEntry?.Data);

    /// <summary>Whether the selected source is a two-port S-parameter result with Noise=yes.</summary>
    public bool CanAddNoiseCircles => NoiseCirclesPreset.Available(DataSourceLibrary.SelectedEntry?.Data);

    /// <summary>Whether the selected source is a result with a statistics table — a Monte Carlo or yield run's.</summary>
    public bool CanAddStatisticsTable => StatisticsTablePreset.Available(DataSourceLibrary.SelectedEntry?.Data);

    /// <summary>The button's tooltip: what it adds, or why the selected source has no table to add (R-ya16-3).</summary>
    public string AddStatisticsTableTip => StatisticsTablePreset.Refusal(DataSourceLibrary.SelectedEntry?.Data)
        ?? "Add Statistics Table — one row per goal and measure. Needs a Monte Carlo or yield result as the source (a .yield.npy)";

    /// <summary>
    /// <b>ANT-10's 3D pattern, ADDABLE.</b> Reported 2026-09-11: the 3D pattern is only reachable
    /// after adding a polar plot, because the plot-type row that carries its icon lives on the
    /// INSPECTOR — which needs a plot selected before it shows anything. Every other plot kind the
    /// inspector can switch to has a matching Add button on the toolbar; this one did not, so the
    /// only route to it was "add a polar plot, then convert it", which is not a route anybody
    /// finds. One more button, the same <c>AddPlot</c>, no new placement or sizing rule.
    /// </summary>
    [RelayCommand] private void AddSurfacePlot() => AddUserPlot(PlotType.Surface3D);

    /// <summary>Ctrl/Cmd+A in the data display — select everything (plots + markers) in the active tab.</summary>
    [RelayCommand] private void SelectAll() => DataDisplay?.SelectAll();

    /// <summary>Escape — the first press disarms every armed tool (marker, drawing, magnifier); a press with none armed
    /// drops the selection. See <c>DataDisplayViewModel.DisarmAllTools</c> and <c>DeselectAll</c>.</summary>
    [RelayCommand]
    private void DeselectAll()
    {
        if (DataDisplay?.DisarmAllTools() == true) return;
        DataDisplay?.DeselectAll();
    }

    /// <summary>The magnifier ARMS and does not zoom (owner, 2026-09-11) — the box the next left-drag
    /// draws on the canvas is what gets framed. Ctrl/Cmd +/- is still a single step.</summary>
    [RelayCommand] private void ZoomBox() => DataDisplay?.ToggleZoomBox();

    [RelayCommand(CanExecute = nameof(CanRemovePlot))]
    private void RemovePlot() => DataDisplay?.RemoveSelected();
    private bool CanRemovePlot() => DataDisplay is { HasAnySelection: true, SelectedAnnotation: null };

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => DataDisplay?.DeleteSelected();
    private bool CanDeleteSelected() => DataDisplay?.HasAnySelection ?? false;

    /// <summary>Page Up — scroll the selected Table plot(s) up by one full page.</summary>
    [RelayCommand(CanExecute = nameof(CanScrollTable))]
    private void ScrollTablePageUp()   => DataDisplay?.ScrollSelectedTable(-1);

    /// <summary>Page Down — scroll the selected Table plot(s) down by one full page.</summary>
    [RelayCommand(CanExecute = nameof(CanScrollTable))]
    private void ScrollTablePageDown() => DataDisplay?.ScrollSelectedTable(+1);

    /// <summary>Home — jump the selected Table plot(s) to the first row.</summary>
    [RelayCommand(CanExecute = nameof(CanScrollTable))]
    private void ScrollTableToTop()    => DataDisplay?.ScrollSelectedTableToEdge(toEnd: false);

    /// <summary>End — jump the selected Table plot(s) to the last page.</summary>
    [RelayCommand(CanExecute = nameof(CanScrollTable))]
    private void ScrollTableToBottom() => DataDisplay?.ScrollSelectedTableToEdge(toEnd: true);

    /// <summary>Gates all four table-scroll keys on the selection actually holding a Table, so with
    /// no table selected the keystroke stays unhandled and reaches whatever else wants it.</summary>
    private bool CanScrollTable() => DataDisplay?.HasSelectedTable ?? false;

    [RelayCommand]
    private async Task OpenDataDisplay()
    {
        if (_openDataDisplayAction is not null) await _openDataDisplayAction();
    }

    /// <summary>
    /// Save: overwrites the current config file if one exists; otherwise
    /// falls through to Save As.
    /// </summary>
    [RelayCommand]
    private async Task SaveDataDisplay()
    {
        if (CurrentConfigPath is string path)
        {
            var (l, t, w, h) = _getWindowGeometryAction?.Invoke() ?? (0, 0, 0, 0);
            await SaveAllAsync(path, l, t, w, h);
        }
        else
        {
            await SaveDataDisplayAs();
        }
    }

    /// <summary>Save As: always shows the save dialog.</summary>
    [RelayCommand]
    private async Task SaveDataDisplayAs()
    {
        if (_saveDataDisplayAsAction is not null) await _saveDataDisplayAsAction();
    }

    // ---- Zoom commands --------------------------------------------------

    [RelayCommand]
    private void ZoomIn() => DataDisplay?.ZoomIn();

    [RelayCommand]
    private void ZoomOut() => DataDisplay?.ZoomOut();

    [RelayCommand]
    private void FitAll()
    {
        var display = DataDisplay;
        if (display is null) return;
        var (w, h) = _getCanvasSizeAction?.Invoke() ?? (800.0, 600.0);
        display.FitAll(w, h);
    }

    // ---- Undo / Redo commands -------------------------------------------
    //
    // Routing: prefer the active tab's within-tab stack so that the most
    // recent plot/marker action is undone first.  Fall back to the window-
    // level TabUndoRedo stack for tab add/remove operations.

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (DataDisplay?.UndoRedo.CanUndo == true)
            DataDisplay.UndoRedo.Undo();
        else
            TabUndoRedo.Undo();
    }
    private bool CanUndo() => (DataDisplay?.UndoRedo.CanUndo ?? false) || TabUndoRedo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (DataDisplay?.UndoRedo.CanRedo == true)
            DataDisplay.UndoRedo.Redo();
        else
            TabUndoRedo.Redo();
    }
    private bool CanRedo() => (DataDisplay?.UndoRedo.CanRedo ?? false) || TabUndoRedo.CanRedo;

    // ---- Edit commands (Cut / Copy / Paste) -----------------------------

    /// <summary>
    /// Copies selected plots to clipboard then removes them.
    /// Enabled only when one or more plots are selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCut))]
    private async Task Cut()
    {
        await PerformCopy(selectedOnly: true);
        if (DataDisplay is { SelectedAnnotation: not null } or { AllAnnotationsSelected: true }) DataDisplay.DeleteSelected();
        else DataDisplay?.RemoveSelected();
    }
    private bool CanCut() => DataDisplay?.HasAnySelection ?? false;

    /// <summary>
    /// Copies selected plots (or all plots when none selected) to clipboard.
    /// Always enabled.
    /// </summary>
    [RelayCommand]
    private async Task Copy() => await PerformCopy(selectedOnly: DataDisplay?.HasAnySelection ?? false);

    private async Task PerformCopy(bool selectedOnly)
    {
        var display = DataDisplay;
        if (display is null || _richCopyAction is null && _annotationCopyAction is null) return;

        var containers = (selectedOnly
            ? display.Plots.Where(p => p.IsSelected)
            : display.Plots).ToList();
        var annotations = selectedOnly
            ? display.AllAnnotationsSelected ? display.Annotations.ToArray()
                : display.SelectedAnnotation is { } selected ? new[] { selected } : Array.Empty<CanvasAnnotation>()
            : display.Annotations.ToArray();
        // Display controls go with the plots (brief-ddl-5 R-ddl5-4), lock state and all.
        var controls = (selectedOnly ? display.Controls.Where(c => c.IsSelected) : display.Controls)
                       .Select(c => c.Model.Clone()).ToArray();
        if (containers.Count == 0 && annotations.Length == 0 && controls.Length == 0) return;

        if (_annotationCopyAction is { } copy)
            await copy(containers, CurrentTheme, new AnnotationPage(annotations, display.ZoomLevel, display.ViewOffsetX, display.ViewOffsetY,
                                                                    controls));
        else if (_richCopyAction is { } legacy) await legacy(containers, CurrentTheme);
    }

    /// <summary>
    /// Pastes plots from clipboard JSON into the active tab. Enabled whenever the clipboard can be read, and the
    /// clipboard is read when it runs: whether it holds plots changes with every copy, in this display, another
    /// display, another window or another application, and a state worked out ahead of time was stale for all but
    /// the first. (It was also worked out before the reader was wired, so a display opened after the copy — a new,
    /// unsaved one included — never enabled Ctrl/Cmd+V at all.) A clipboard with no plots on it pastes nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task Paste()
    {
        if (_getClipboardTextAction is null) return;
        var display = DataDisplay;
        if (display is null) return;
        var text = await ReadClipboardTextAsync();
        if (!TryParseDataDisplayConfig(text, out var config) || config is null) return;

        var added = await display.PasteFromConfigAsync(config);
        var commands = new List<IUndoableCommand>();
        if (added.Count > 0) commands.Add(new PasteCommand(added, display));
        if (config.Annotations is { } annotations)
            foreach (var annotation in annotations)
            {
                var command = new AddAnnotationCommand(display, display.Annotations.Count, AnnotationRenderer.Translate(annotation, 20, 20));
                command.Execute();
                commands.Add(command);
            }
        // Pasted controls are new controls: a fresh id each, a name of their own where theirs is taken, and the lock
        // they were copied with (brief-ddl-5 R-ddl5-9).
        var pastedControls = new List<ControlContainerViewModel>();
        if (config.Controls is { } controls)
        {
            var taken = Tabs.SelectMany(t => t.DataDisplay.Controls).Select(c => c.Model).ToList();
            foreach (var model in controls)
            {
                var copy = model.Clone();
                copy.Id = Guid.NewGuid().ToString("D");
                if (taken.Any(c => c.Name == copy.Name)) copy.Name = DisplayControl.NextName(copy.KindName, taken);
                copy.Left += 20; copy.Top += 20;
                taken.Add(copy);
                var vm = new ControlContainerViewModel(copy, display);
                var command = new AddControlCommand(display, display.Controls.Count, vm);
                command.Execute();
                commands.Add(command);
                pastedControls.Add(vm);
            }
        }
        if (commands.Count > 0) display.UndoRedo.Push(new CompositeCommand(commands));
        // What was pasted is what is selected, on top — as a pasted plot is — so the next drag moves the copy, not the
        // original under it.
        if (pastedControls.Count > 0)
        {
            if (added.Count == 0) display.SelectOnly(pastedControls[0]);
            else                  display.ToggleSelect(pastedControls[0]);
            foreach (var vm in pastedControls.Skip(1)) display.ToggleSelect(vm);
        }
    }
    private bool CanPaste() => _getClipboardTextAction is not null;

    /// <summary>Invokes cut/copy/paste for routing from the workspace Edit menu.</summary>
    public Task InvokeCutAsync()   => Cut();
    public Task InvokeCopyAsync()  => Copy();
    public Task InvokePasteAsync() => Paste();

    /// <summary>The clipboard's text through the view's reader, null when it cannot be read: a platform that refuses
    /// the read (Windows throws when the data object is replaced mid-enumeration) pastes nothing, and does not end the
    /// process.</summary>
    private Task<string?> ReadClipboardTextAsync()
        => _getClipboardTextAction is null
            ? Task.FromResult<string?>(null)
            : CircuitRF.Ui.Clipboard.ClipboardRead.Try(_getClipboardTextAction, null);

    private static bool TryParseDataDisplayConfig(
        string? text,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DataDisplayConfig? config)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.TrimStart().StartsWith('{')) return false;
        try
        {
            config = JsonSerializer.Deserialize<DataDisplayConfig>(text, DataDisplayViewModel.JsonOpts);
            return config is not null && (config.Plots.Count > 0 || config.Annotations is { Count: > 0 }
                                          || config.Controls is { Count: > 0 });
        }
        catch { return false; }
    }

    // ---- Multi-tab Save / Load ------------------------------------------

    /// <summary>
    /// R-dd-6 — true only for a bare filename: not rooted (no drive letter, no leading '/'),
    /// and containing no directory separator of either flavor. This is the ONE portability
    /// gate for anything written into <see cref="DataDisplayConfig.SourceAliases"/> — a value
    /// failing this check must never reach the saved file.
    /// </summary>
    internal static bool IsPortableSourceKey(string key) =>
        !string.IsNullOrEmpty(key)
        && !Path.IsPathRooted(key)
        && !key.Contains('/')
        && !key.Contains('\\');

    /// <summary>
    /// R-dd-4 — re-points an already-loaded dataset (broken or live) at a different file. Every
    /// trace across every tab/plot that referenced the OLD path is rewritten to the NEW one
    /// BEFORE the entry's own data reloads — so PlotInspectorViewModel's LibraryChanged handler
    /// (which drops any trace whose SourcePath no longer matches a live entry) finds those traces
    /// already pointing at the entry that is about to become live, rather than treating them as
    /// orphaned. This is what makes "point baseline at run3.npy" update every trace using that
    /// alias in one action instead of silently dropping them.
    /// </summary>
    public async Task RepointDatasetAsync(DataSourceEntryViewModel entry, string newPath)
    {
        string? oldPath = entry.FilePath;
        newPath = Path.GetFullPath(newPath);

        if (!string.IsNullOrEmpty(oldPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            string newRef = DataDisplayViewModel.ComputeSourceKey(newPath, DataSourceLibrary);
            foreach (var tab in Tabs)
            foreach (var container in tab.DataDisplay.Plots)
            foreach (var row in container.Inspector.Traces)
            {
                var t = row.Trace;
                if (t.SourcePath is null || !string.Equals(t.SourcePath, oldPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                t.SourcePath = newPath;
                // The "Selected" sentinel means "whichever source the toolbar has selected" — never
                // rewrite it to a concrete ref, or the trace would stop tracking the toolbar combo.
                if (t.SourceRef is not null && t.SourceRef != DataSourceRef.Selected)
                    t.SourceRef = newRef;
            }
        }

        await DataSourceLibrary.RestoreBrokenEntry(entry, newPath);
    }

    /// <summary>
    /// Serialises all tabs to a .splot file (v2 TabConfig format).
    /// windowLeft/Top are physical pixels; windowWidth/Height are logical DIPs.
    /// </summary>
    public async Task SaveAllAsync(
        string path,
        double windowLeft = 0, double windowTop = 0,
        double windowWidth = 0, double windowHeight = 0)
    {
        _currentConfigPath = path;
        OnPropertyChanged(nameof(CurrentConfigPath));
        WindowTitle = Path.GetFileName(path);
        DataSourceLibrary.DocumentDirectory = Path.GetDirectoryName(Path.GetFullPath(path));

        string configDir = Path.GetDirectoryName(path) ?? "";
        var config = BuildConfig(configDir, windowLeft, windowTop, windowWidth, windowHeight);
        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);

        // Owner-reported, 2026-08-26 ("somehow i managed to get the data display to get corrupted …
        // i had to make a new workspace"): the `.cdd` was the ONE document type circuitRF wrote
        // non-atomically — every other one (`.csch`, `.csym`, `.ccell`, `.clay`, `.ctech`, `.cem`,
        // `.wasm`, `.cws`) already goes through AtomicFile. A plain WriteAllText TRUNCATES the target
        // first, so anything that interrupts the write — a crash, a kill, a network/removable volume
        // going away mid-save, two saves racing — leaves a half-written file on disk in place of a
        // display that was fine a moment ago. Serialize-to-sibling-temp-then-rename cannot: the old
        // file survives intact until the new one is complete. Synchronous on purpose — a `.cdd` is a
        // few kilobytes, and this is the same call every other persistence path in the app makes;
        // Task.Run only keeps the write off the UI thread, matching what WriteAllTextAsync did here.
        await Task.Run(() => AtomicFile.WriteAllText(path, json));

        // Update baseline so HasUnsavedChanges() returns false right after saving.
        CaptureBaseline();
        RaiseDirtyChanged();
        ConfigPathSaved?.Invoke(path);
    }

    /// <summary>
    /// Writes this display as a Data Display template (<see cref="DataDisplayTemplate"/>) to <paramref name="path"/>:
    /// its tabs and plots as they are, every trace reading whatever source the display made from it selects. The
    /// display itself is unchanged — it keeps its own file, its sources and its dirty state.
    /// </summary>
    public async Task SaveAsTemplateAsync(string path)
    {
        var config = DataDisplayTemplate.MakeTemplate(BuildConfig(configDir: "", 0, 0, 0, 0));
        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        await Task.Run(() => AtomicFile.WriteAllText(path, json));
    }

    /// <summary>The display's saved form, with sources keyed relative to <paramref name="configDir"/>.</summary>
    private DataDisplayConfig BuildConfig(
        string configDir, double windowLeft, double windowTop, double windowWidth, double windowHeight)
    {
        var config = new DataDisplayConfig
        {
            FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = DataSourceLibrary.SelectedDataSourceRef,
            WindowLeft         = windowLeft,
            WindowTop          = windowTop,
            WindowWidth        = windowWidth,
            WindowHeight       = windowHeight,
            ActiveTabIndex     = ActiveTab is not null ? Math.Max(0, Tabs.IndexOf(ActiveTab)) : 0,
        };

        foreach (var tab in Tabs)
            config.Tabs.Add(tab.DataDisplay.BuildTabConfig(tab.Name, configDir));

        // R-res-4 — every loaded source's alias, keyed the same way a trace's own SourceRef is.
        // Written unconditionally (even an unrenamed default-file-stem alias) — the alias is stored,
        // never re-derived at load time.
        //
        // R-dd-6 — validated HERE, at save, not only on load: a stored data source is a bare
        // filename resolved against results/, never a rooted path and never one containing a
        // directory separator (the latter is what would otherwise surface only when a macOS-
        // authored workspace is opened on Windows). ComputeSourceKey already returns the bare,
        // portable form for anything under the results root; it falls back to the raw absolute
        // path for a source loaded from OUTSIDE the workspace (an external Touchstone file) —
        // that fallback must never reach the file as an alias key, so it is skipped rather than
        // written. The alias itself is simply not persisted for that source; the entry still
        // loads correctly next session (as an unaliased/default-named source), it just can't be
        // renamed across a reload the way an in-results-root source can.
        foreach (var entry in DataSourceLibrary.Entries)
        {
            if (entry.FilePath is not { } fp) continue;
            string key = DataDisplayViewModel.ComputeSourceKey(fp, DataSourceLibrary);
            if (!IsPortableSourceKey(key)) continue;
            config.SourceAliases[key] = entry.Alias;
        }
        return config;
    }

    /// <summary>
    /// Reads a .splot file, rebuilds all tabs, then fires
    /// <see cref="WindowGeometryLoaded"/> if geometry is present.
    /// Handles v1 (legacy Plots list) and v2 (Tabs list) formats.
    /// </summary>
    /// <param name="jsonStream">
    /// When provided (e.g. for files opened via macOS Apple Events), JSON is read from
    /// this stream instead of the file path so that security-scoped URL access is honoured.
    /// The caller retains ownership of the stream.
    /// </param>
    public async Task LoadAllAsync(string path, Stream? jsonStream = null)
    {
        _currentConfigPath = path;
        OnPropertyChanged(nameof(CurrentConfigPath));
        WindowTitle = Path.GetFileName(path);
        // A relative source is looked for beside the document first, as `circuitrf render` looks (EMB-5 R-emb5-14).
        DataSourceLibrary.DocumentDirectory = Path.GetDirectoryName(Path.GetFullPath(path));

        string configDir = Path.GetDirectoryName(path) ?? "";

        string json;
        if (jsonStream is not null)
        {
            // Stream supplied by caller (e.g. IStorageFile.OpenReadAsync on macOS).
            using var reader = new StreamReader(jsonStream, leaveOpen: true);
            json = await reader.ReadToEndAsync();
        }
        else
        {
            if (!File.Exists(path)) return;
            json = await File.ReadAllTextAsync(path);
        }
        // A `.cdd` that will not parse must FAIL LOUDLY, and the reason is data loss, not tidiness.
        // This used to be `catch { return; }`: a damaged file opened as a perfectly ordinary, CLEAN,
        // materialized document showing one empty plot — no message, no mark, nothing to distinguish
        // it from a display the user had simply not authored yet. The next save then wrote that blank
        // over the file, destroying whatever the damaged copy still held. Both callers already handle
        // InvalidDataException (WorkspaceViewModel.OpenOrActivateDataDisplay → Messages.Error;
        // DataDisplayView.DoOpenDisplayAsync → "Cannot open display: …"), which is the same treatment
        // the format_version mismatch below has always had — so the file is left untouched on disk and
        // is still recoverable. Owner-reported, 2026-08-26.
        var config = ParseConfig(json, Path.GetFileName(path));
        await LoadConfigAsync(config, configDir, restoreSources: true);

        // Apply saved window geometry if present.
        if (config.WindowWidth > 0)
            WindowGeometryLoaded?.Invoke(
                config.WindowLeft, config.WindowTop,
                config.WindowWidth, config.WindowHeight);

        // Update baseline so HasUnsavedChanges() returns false right after loading.
        CaptureBaseline();
        RaiseDirtyChanged();
    }

    /// <summary>
    /// Fills this display from a Data Display template (<see cref="DataDisplayTemplate"/>): its tabs and plots, every
    /// trace reading the source this display has selected. The display stays UNSAVED — it has no file, and it counts
    /// as changed, so closing it asks before the work is lost; saving it asks where. The template file is not
    /// touched. Select a source before calling, so the traces resolve as they load.
    /// </summary>
    /// <exception cref="InvalidDataException">The text is not a readable Data Display.</exception>
    public async Task LoadTemplateAsync(string json, string templateName)
    {
        var config = DataDisplayTemplate.MakeTemplate(ParseConfig(json, templateName));
        await LoadConfigAsync(config, configDir: "", restoreSources: false);
        RaiseDirtyChanged();
    }

    /// <summary>A `.cdd` (or `.cddt`) read, or an <see cref="InvalidDataException"/> naming
    /// <paramref name="fileName"/> — never a partial or empty display in place of one that would not read.</summary>
    private static DataDisplayConfig ParseConfig(string json, string fileName)
    {
        // A `.cdd` that will not parse must FAIL LOUDLY, and the reason is data loss, not tidiness.
        // This used to be `catch { return; }`: a damaged file opened as a perfectly ordinary, CLEAN,
        // materialized document showing one empty plot — no message, no mark, nothing to distinguish
        // it from a display the user had simply not authored yet. The next save then wrote that blank
        // over the file, destroying whatever the damaged copy still held. Both callers already handle
        // InvalidDataException (WorkspaceViewModel.OpenOrActivateDataDisplay → Messages.Error;
        // DataDisplayView.DoOpenDisplayAsync → "Cannot open display: …"), which is the same treatment
        // the format_version mismatch below has always had — so the file is left untouched on disk and
        // is still recoverable. Owner-reported, 2026-08-26.
        DataDisplayConfig? config;
        try { config = JsonSerializer.Deserialize<DataDisplayConfig>(json, DataDisplayViewModel.JsonOpts); }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{fileName}' is not readable as a Data Display — {ex.Message} " +
                "The file has been left unchanged.", ex);
        }
        if (config is null)
            throw new InvalidDataException(
                $"'{fileName}' contains no Data Display. The file has been left unchanged.");

        if (config.FormatVersion != DataDisplayConfig.CurrentFormatVersion)
            throw new InvalidDataException(
                $".cdd format_version {config.FormatVersion} does not match " +
                $"expected {DataDisplayConfig.CurrentFormatVersion}. Regenerate the file.");
        return config;
    }

    /// <summary>Replaces every tab with <paramref name="config"/>'s. With <paramref name="restoreSources"/> the
    /// config's own sources are loaded and its selection made first, as opening a `.cdd` does; without, the display
    /// keeps the sources and selection it has, as a template asks.</summary>
    private async Task LoadConfigAsync(DataDisplayConfig config, string configDir, bool restoreSources)
    {
        // Build the list of TabConfigs to load.
        List<TabConfig> tabConfigs;
        if (config.Tabs.Count > 0)
        {
            tabConfigs = config.Tabs;
        }
        else if (config.Plots.Count > 0)
        {
            // v1 legacy: wrap the root Plots list into a single tab.
            tabConfigs = new List<TabConfig>
            {
                new()
                {
                    Name        = "Tab 1",
                    Plots       = config.Plots,
                    ZoomLevel   = config.ZoomLevel,
                    ViewOffsetX = config.ViewOffsetX,
                    ViewOffsetY = config.ViewOffsetY,
                }
            };
        }
        else
        {
            // Empty config — start with one default tab.
            Tabs.Clear();
            var def = CreateNewTab("Tab 1");
            Tabs.Add(def);
            ActiveTab = def;
            return;
        }

        // Replace all existing tabs.
        // Unsubscribe from the current active tab before clearing.
        if (_subscribedTab?.DataDisplay is { } old)
        {
            old.PropertyChanged       -= OnActiveDisplayPropertyChanged;
            old.ContentChanged        -= OnActiveDisplayContentChanged;
        }
        _subscribedTab = null;

        // Discard any tab-level undo history from the previous session.
        TabUndoRedo.Clear();
        Tabs.Clear();
        ActiveTab = null;

        if (restoreSources) await RestoreSourcesAsync(config);

        foreach (var tc in tabConfigs)
        {
            var tab = CreateNewTab(tc.Name);
            await tab.DataDisplay.LoadFromTabConfigAsync(tc, configDir);
            Tabs.Add(tab);
        }

        // Links cross plots and tabs, so they resolve once every tab is in (brief-ddl-1 R-ddl1-9).
        ResolveLinks();

        if (Tabs.Count > 0)
        {
            int idx = Math.Clamp(config.ActiveTabIndex, 0, Tabs.Count - 1);
            ActiveTab = Tabs[idx];
        }
    }

    private async Task RestoreSourcesAsync(DataDisplayConfig config)
    {
        // R-res-4/R-res-5 — load EVERY declared source and stamp its alias before touching tabs, so
        // (a) a dataset with no current trace still appears (loaded, or broken-and-reported by name),
        // and (b) sentinel/relative trace SourceRefs resolve against an already-loaded, correctly-
        // aliased entry rather than lazy-loading it mid-restore with the default file-stem alias.
        foreach (var (key, alias) in config.SourceAliases)
        {
            var abs = DataSourceLibrary.ResolveAbs(key);
            if (abs is null) continue;

            if (File.Exists(abs)) await DataSourceLibrary.LoadFileAsync(abs);
            else                  DataSourceLibrary.AddBrokenEntry(abs);

            var entry = DataSourceLibrary.Entries.FirstOrDefault(e =>
                string.Equals(e.FilePath, abs, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) entry.Alias = alias;
        }

        // Select the persisted datasource BEFORE loading tabs so sentinel traces resolve correctly.
        await DataSourceLibrary.SelectDataSourceAsync(config.SelectedDataSource);
    }
}

/// <summary>The toolbar's datasource changed: undo selects the one it was.</summary>
internal sealed class SelectDataSourceCommand(DisplayWindowViewModel window, string from, string to) : IUndoableCommand
{
    public void Execute() => window.SelectDataSourceForUndo(to);
    public void Undo()    => window.SelectDataSourceForUndo(from);
}
