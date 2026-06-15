using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region.
/// Hosts four mutually-exclusive contexts: schematic component editor, symbol primitive
/// inspector, cell properties, and data display plot inspector. The <see cref="HeaderText"/>
/// observable reflects which context is active.
/// </summary>
public partial class PropertiesTool : Tool
{
    [ObservableProperty]
    private ParameterEditorViewModel _editorVm = new();

    [ObservableProperty]
    private SymbolPrimitiveInspectorViewModel _symbolInspectorVm = new();

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
    /// "Cell" / "Component" / "Symbol" / "Plot" / "Properties" (fallback).
    /// Use this instead of Title — Dock Tool.Title changes are not reliably picked
    /// up by Avalonia compiled bindings (see ui-CLAUDE.md §Dock gotchas).
    /// </summary>
    [ObservableProperty]
    private string _headerText = "Properties";

    /// <summary>
    /// True when none of the specific contexts (symbol/cell/data-display) is active —
    /// i.e., when the schematic parameter editor (or empty placeholder) should be shown.
    /// </summary>
    public bool IsSchematicContextActive => !IsSymbolEditorActive && !IsCellActive && !IsDataDisplayActive;

    partial void OnIsSymbolEditorActiveChanged(bool value)  => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsCellActiveChanged(bool value)          => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsDataDisplayActiveChanged(bool value)   => OnPropertyChanged(nameof(IsSchematicContextActive));

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
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        EditorVm.SetContext(vm);
        SymbolInspectorVm.SetContext(null);
        HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
    }

    /// <summary>Called by WorkspaceViewModel when the active symbol-editor document changes.</summary>
    public void SetActiveSymbolEditor(SymbolEditorViewModel? vm)
    {
        IsCellActive          = false;
        IsSymbolEditorActive  = vm is not null;
        IsDataDisplayActive   = false;
        CellEditorVm          = null;
        PlotInspectorVm       = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(vm);
        HeaderText = vm is not null ? "Symbol" : "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active cell parameter editor changes.</summary>
    public void SetActiveCell(CellParameterEditorViewModel? vm)
    {
        IsCellActive          = vm is not null;
        IsSymbolEditorActive  = false;
        IsDataDisplayActive   = false;
        CellEditorVm          = vm;
        PlotInspectorVm       = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
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
        CellEditorVm          = null;
        PlotInspectorVm       = vm;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Plot" : "Properties";
    }
}
