using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region.
/// Hosts five mutually-exclusive contexts: schematic component editor, symbol primitive
/// inspector, cell properties, data display plot inspector, and the layout shape-selection
/// properties panel (L1c). The <see cref="HeaderText"/> observable reflects which context is active.
/// </summary>
public partial class PropertiesTool : Tool
{
    [ObservableProperty]
    private ParameterEditorViewModel _editorVm = new();

    [ObservableProperty]
    private SymbolPrimitiveInspectorViewModel _symbolInspectorVm = new();

    [ObservableProperty]
    private LayoutShapePropertiesViewModel _layoutInspectorVm = new();

    /// <summary>wbond.md §6.9 — everything about one selected bond wire, incl. its coordinates.</summary>
    [ObservableProperty]
    private CircuitRF.Ui.WBond.WBondWirePropertiesViewModel _wireInspectorVm = new();

    /// <summary>True when a layout editor document is active; the Properties pane shows the
    /// layout shape properties panel rather than the schematic parameter editor.</summary>
    [ObservableProperty]
    private bool _isLayoutActive;

    /// <summary>
    /// True when a symbol editor document is active; the Properties pane shows the
    /// symbol primitive inspector rather than the schematic parameter editor.
    /// </summary>
    [ObservableProperty]
    private bool _isSymbolEditorActive;

    /// <summary>True when a cell parameter editor document is active.</summary>
    [ObservableProperty]
    private bool _isCellActive;

    /// <summary>True when a data display document is active and a single plot is selected.</summary>
    [ObservableProperty]
    private bool _isDataDisplayActive;

    /// <summary>True when a Known-File leaf or OtherFile node is selected in the project tree.</summary>
    [ObservableProperty]
    private bool _isFileInfoActive;

    /// <summary>True when a single bond wire is selected in a wBond editor (wbond.md §6.9).</summary>
    [ObservableProperty]
    private bool _isWireActive;

    /// <summary>File-info VM for the currently selected tree leaf, or null.</summary>
    [ObservableProperty]
    private FileInfoInspectorViewModel? _fileInfoVm;

    /// <summary>
    /// The cell editor VM for the active cell document, or null.
    /// Bound by PropertiesView to show compact cell properties in the inspector.
    /// </summary>
    [ObservableProperty]
    private CellParameterEditorViewModel? _cellEditorVm;

    /// <summary>
    /// The plot inspector VM for the single-selected plot in the active data display, or null.
    /// Bound by PropertiesView to show the plot inspector.
    /// </summary>
    [ObservableProperty]
    private PlotInspectorViewModel? _plotInspectorVm;

    partial void OnPlotInspectorVmChanged(PlotInspectorViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedPlot));

    /// <summary>True only once a single plot is actually selected — gates the plot-inspector
    /// portion of the "Plot" context separately from <see cref="IsDataDisplayActive"/> (which is
    /// true for the whole Data Display document, plot selected or not, so the Datasets list stays
    /// visible). Without this, the plot inspector's own chrome (Add Trace, plot-type buttons, …)
    /// would render with a null DataContext — visible but uninitialized — whenever a Data Display
    /// document is active with no plot selected.</summary>
    public bool HasSelectedPlot => PlotInspectorVm is not null;

    /// <summary>Datasets section (R-dd-4/5) shown beside the plot inspector whenever a Data
    /// Display document is active — not gated on a single plot being selected, since it is
    /// document-level state (aliases, missing/live status), not plot-level.</summary>
    [ObservableProperty]
    private DatasetsListViewModel _datasetsVm = new();

    /// <summary>
    /// Observable header text driven by the active context.
    /// "Cell" / "Component" / "Symbol" / "Plot" / "Layout" / "Properties" (fallback).
    /// Use this instead of Title — Dock Tool.Title changes are not reliably picked
    /// up by Avalonia compiled bindings (see ui-CLAUDE.md §Dock gotchas).
    /// </summary>
    [ObservableProperty]
    private string _headerText = "Properties";

    /// <summary>
    /// True when none of the specific contexts (symbol/cell/data-display/file-info/layout) is
    /// active — i.e., when the schematic parameter editor (or empty placeholder) should be shown.
    /// </summary>
    public bool IsSchematicContextActive =>
        !IsSymbolEditorActive && !IsCellActive && !IsDataDisplayActive && !IsFileInfoActive
        && !IsLayoutActive && !IsWireActive;

    partial void OnIsSymbolEditorActiveChanged(bool value)  => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsCellActiveChanged(bool value)          => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsDataDisplayActiveChanged(bool value)   => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsFileInfoActiveChanged(bool value)      => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsWireActiveChanged(bool value) => OnPropertyChanged(nameof(IsSchematicContextActive));

    partial void OnIsLayoutActiveChanged(bool value)        => OnPropertyChanged(nameof(IsSchematicContextActive));

    public PropertiesTool()
    {
        Id    = "Properties";
        Title = "Properties";

        // React to schematic selection changes so the header updates within the same tab.
        EditorVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ParameterEditorViewModel.IsEmptyState)
                && !IsSymbolEditorActive && !IsCellActive && !IsDataDisplayActive)
            {
                HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
            }
        };
    }

    // ── Context setters ───────────────────────────────────────────────────────

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsWireActive  = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(vm);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        WireInspectorVm.SetContext(null);
        HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
    }

    /// <summary>Called by WorkspaceViewModel when the active symbol-editor document changes.</summary>
    public void SetActiveSymbolEditor(SymbolEditorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = vm is not null;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsWireActive  = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(vm);
        LayoutInspectorVm.SetContext(null);
        WireInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Symbol" : "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active cell parameter editor changes.</summary>
    public void SetActiveCell(CellParameterEditorViewModel? vm)
    {
        IsCellActive          = vm is not null;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsWireActive  = false;
        IsLayoutActive        = false;
        CellEditorVm          = vm;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        WireInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Cell" : "Properties";
    }

    /// <summary>
    /// Called by WorkspaceViewModel when the active data display document or its plot selection
    /// changes. <paramref name="window"/> (the whole document) is what gates
    /// <see cref="IsDataDisplayActive"/> — the Datasets section (R-dd-4/5) must show regardless of
    /// whether a single plot is selected; <paramref name="vm"/> (the single-selected plot's own
    /// inspector) additionally gates the plot inspector portion. Both null clears the context and
    /// falls back to the placeholder.
    /// </summary>
    public void SetActiveDataDisplay(PlotInspectorViewModel? vm, DisplayWindowViewModel? window = null)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = window is not null;
        IsFileInfoActive      = false;
        IsWireActive  = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = vm;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        WireInspectorVm.SetContext(null);
        DatasetsVm.SetWindow(window);
        HeaderText = window is not null ? "Plot" : "Properties";
    }

    /// <summary>
    /// Called when a Known File leaf or OtherFile node is selected in the project tree.
    /// Null clears the file-info context.
    /// </summary>
    public void SetActiveFileInfo(FileInfoInspectorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        WireInspectorVm.SetContext(null);
        IsWireActive          = false;
        IsFileInfoActive      = vm is not null;
        FileInfoVm            = vm;
        HeaderText            = vm is not null ? "File" : "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active layout editor document changes.</summary>
    public void SetActiveLayout(LayoutEditorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsWireActive  = false;
        IsLayoutActive        = vm is not null;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(vm);
        WireInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Layout" : "Properties";
    }
}

public partial class PropertiesTool
{
    /// <summary>
    /// Shows the bond-wire inspector (wbond.md §6.9), clearing every other context.
    ///
    /// <para>Mirrors <see cref="SetActiveLayout"/> exactly. A wBond document has BOTH a wire context
    /// and a layout context available, and they are mutually exclusive here on purpose: the panel
    /// follows whichever the user last selected in, and showing both at once would leave two
    /// coordinate lists on screen with no way to tell which one an edit lands in.</para>
    /// </summary>
    public void SetActiveWire(CircuitRF.Ui.WBond.WBondViewModel? vm)
    {
        IsLayoutActive = false;
        IsSymbolEditorActive = false;
        IsCellActive = false;
        IsDataDisplayActive = false;
        IsFileInfoActive = false;

        // Set LAST, after every other flag is cleared — the mechanical "clear them all then set mine"
        // shape of these setters means an own-flag assignment placed first is silently undone.
        IsWireActive = vm is not null;

        FileInfoVm = null;
        CellEditorVm = null;
        PlotInspectorVm = null;

        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);

        WireInspectorVm.SetContext(vm);

        HeaderText = vm is not null ? "Wire" : "Properties";
    }
}
