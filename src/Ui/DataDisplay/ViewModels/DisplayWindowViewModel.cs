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

public partial class DisplayWindowViewModel : ViewModelBase
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
        get => DataSourceLibrary.AvailableDataSources
                   .FirstOrDefault(i => i.LogicalId == DataSourceLibrary.SelectedDataSourceRef);
        set
        {
            if (value is null) return;
            _ = DataSourceLibrary.SelectDataSourceAsync(value.LogicalId);
            OnPropertyChanged();
        }
    }

    /// <summary>Pass-through: enumerate results + workspace Touchstone without loading any file.</summary>
    public void RefreshAvailableDataSources()
    {
        DataSourceLibrary.RefreshAvailableDataSources();
        OnPropertyChanged(nameof(SelectedDataSourceItem));
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
            // Create a copy with ZoomLevel/ViewOffset zeroed so those changes
            // do not count as "unsaved".
            config.Tabs.Add(new TabConfig
            {
                Name  = full.Name,
                Plots = full.Plots,
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
    public void SetGetClipboardTextAction(Func<Task<string?>> a)          => _getClipboardTextAction       = a;
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

        // Keep the combo selection in sync with the library selection.
        DataSourceLibrary.SelectedDataSourceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedDataSourceItem));
            RaiseDirtyChanged();
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
        // New-plot auto-placement needs the visible viewport size. _getCanvasSizeAction
        // resolves the active tab's canvas; add-plot only ever targets the active tab, so
        // wiring every tab's DataDisplay to it is correct. Null-safe until the action is injected.
        tab.DataDisplay.CanvasSizeProvider = () => _getCanvasSizeAction?.Invoke() ?? (0.0, 0.0);
        tab.CloseRequested += (sender, _) => RemoveTab(sender as TabViewModel);
        return tab;
    }

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
    private void AddPlot() => DataDisplay?.AddPlot(PlotType.Rect);

    [RelayCommand] private void AddSmithPlot() => DataDisplay?.AddPlot(PlotType.Smith);
    [RelayCommand] private void AddPolarPlot() => DataDisplay?.AddPlot(PlotType.Polar);
    [RelayCommand] private void AddTablePlot() => DataDisplay?.AddPlot(PlotType.Table);

    /// <summary>Ctrl/Cmd+A in the data display — select everything (plots + markers) in the active tab.</summary>
    [RelayCommand] private void SelectAll() => DataDisplay?.SelectAll();

    /// <summary>Escape — drops every selection in the active display. See <c>DataDisplayViewModel.DeselectAll</c>.</summary>
    [RelayCommand] private void DeselectAll() => DataDisplay?.DeselectAll();

    [RelayCommand(CanExecute = nameof(CanRemovePlot))]
    private void RemovePlot() => DataDisplay?.RemoveSelected();
    private bool CanRemovePlot() => DataDisplay?.HasAnySelection ?? false;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected() => DataDisplay?.DeleteSelected();
    private bool CanDeleteSelected() => DataDisplay?.HasAnySelection ?? false;

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
    private void ActualSize() => DataDisplay?.ActualSize();

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

    private bool _canPaste;

    /// <summary>
    /// Copies selected plots to clipboard then removes them.
    /// Enabled only when one or more plots are selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCut))]
    private async Task Cut()
    {
        await PerformCopy(selectedOnly: true);
        DataDisplay?.RemoveSelected();
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
        if (display is null || _richCopyAction is null) return;

        var containers = (selectedOnly
            ? display.Plots.Where(p => p.IsSelected)
            : display.Plots).ToList();
        if (containers.Count == 0) return;

        await _richCopyAction(containers, CurrentTheme);
        await CheckPasteStateAsync();
    }

    /// <summary>
    /// Pastes plots from clipboard JSON into the active tab.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task Paste()
    {
        if (_getClipboardTextAction is null) return;
        var display = DataDisplay;
        if (display is null) return;
        var text = await _getClipboardTextAction();
        if (!TryParseDataDisplayConfig(text, out var config) || config is null) return;

        var added = await display.PasteFromConfigAsync(config);
        if (added.Count > 0)
            display.UndoRedo.Push(new PasteCommand(added, display));
    }
    private bool CanPaste() => _canPaste;

    /// <summary>Invokes cut/copy/paste for routing from the workspace Edit menu.</summary>
    public Task InvokeCutAsync()   => Cut();
    public Task InvokeCopyAsync()  => Copy();
    public Task InvokePasteAsync() => Paste();

    public async Task CheckPasteStateAsync()
    {
        if (_getClipboardTextAction is null)
        {
            _canPaste = false;
            PasteCommand.NotifyCanExecuteChanged();
            return;
        }
        string? text = await _getClipboardTextAction();
        _canPaste = TryParseDataDisplayConfig(text, out _);
        PasteCommand.NotifyCanExecuteChanged();
    }

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
            return config?.Plots.Count > 0;
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

        string configDir = Path.GetDirectoryName(path) ?? "";

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

        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        await File.WriteAllTextAsync(path, json);

        // Update baseline so HasUnsavedChanges() returns false right after saving.
        CaptureBaseline();
        RaiseDirtyChanged();
        ConfigPathSaved?.Invoke(path);
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
        DataDisplayConfig? config;
        try { config = JsonSerializer.Deserialize<DataDisplayConfig>(json, DataDisplayViewModel.JsonOpts); }
        catch { return; }
        if (config is null) return;

        if (config.FormatVersion != DataDisplayConfig.CurrentFormatVersion)
            throw new InvalidDataException(
                $".cdd format_version {config.FormatVersion} does not match " +
                $"expected {DataDisplayConfig.CurrentFormatVersion}. Regenerate the file.");

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

        foreach (var tc in tabConfigs)
        {
            var tab = CreateNewTab(tc.Name);
            await tab.DataDisplay.LoadFromTabConfigAsync(tc, configDir);
            Tabs.Add(tab);
        }

        if (Tabs.Count > 0)
        {
            int idx = Math.Clamp(config.ActiveTabIndex, 0, Tabs.Count - 1);
            ActiveTab = Tabs[idx];
        }

        // Apply saved window geometry if present.
        if (config.WindowWidth > 0)
            WindowGeometryLoaded?.Invoke(
                config.WindowLeft, config.WindowTop,
                config.WindowWidth, config.WindowHeight);

        // Update baseline so HasUnsavedChanges() returns false right after loading.
        CaptureBaseline();
        RaiseDirtyChanged();
    }
}
