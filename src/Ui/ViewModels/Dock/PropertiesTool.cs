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
        !IsSymbolEditorActive && !IsCellActive && !IsDataDisplayActive && !IsFileInfoActive && !IsLayoutActive;

    partial void OnIsSymbolEditorActiveChanged(bool value)  => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsCellActiveChanged(bool value)          => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsDataDisplayActiveChanged(bool value)   => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsFileInfoActiveChanged(bool value)      => OnPropertyChanged(nameof(IsSchematicContextActive));
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
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(vm);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
    }

    /// <summary>Called by WorkspaceViewModel when the active symbol-editor document changes.</summary>
    public void SetActiveSymbolEditor(SymbolEditorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = vm is not null;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(vm);
        LayoutInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Symbol" : "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active cell parameter editor changes.</summary>
    public void SetActiveCell(CellParameterEditorViewModel? vm)
    {
        IsCellActive          = vm is not null;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        IsFileInfoActive      = false;
        IsLayoutActive        = false;
        CellEditorVm          = vm;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Cell" : "Properties";
    }

    /// <summary>
    /// Called by WorkspaceViewModel when the active data display's plot selection changes.
    /// Null clears the data display context and falls back to the placeholder.
    /// </summary>
    public void SetActiveDataDisplay(PlotInspectorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = vm is not null;
        IsFileInfoActive      = false;
        IsLayoutActive        = false;
        CellEditorVm          = null;
        PlotInspectorVm       = vm;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Plot" : "Properties";
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
        IsLayoutActive        = vm is not null;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        FileInfoVm            = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        LayoutInspectorVm.SetContext(vm);
        HeaderText = vm is not null ? "Layout" : "Properties";
    }
}
