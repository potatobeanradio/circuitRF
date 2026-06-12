using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region.
/// Hosts three mutually-exclusive contexts: schematic component editor, symbol primitive
/// inspector, and cell properties. The <see cref="HeaderText"/> observable reflects which
/// context is active ("Component", "Symbol", "Cell", or "Properties" when nothing specific).
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

    /// <summary>
    /// The cell editor VM for the active cell document, or null.
    /// Bound by PropertiesView to show compact cell properties in the inspector.
    /// </summary>
    [ObservableProperty]
    private CellParameterEditorViewModel? _cellEditorVm;

    /// <summary>
    /// Observable header text driven by the active context.
    /// "Cell" / "Component" / "Symbol" / "Properties" (fallback).
    /// Use this instead of Title — Dock Tool.Title changes are not reliably picked
    /// up by Avalonia compiled bindings (see ui-CLAUDE.md §Dock gotchas).
    /// </summary>
    [ObservableProperty]
    private string _headerText = "Properties";

    /// <summary>
    /// True when neither the symbol editor nor a cell document is the active context —
    /// i.e., when the schematic parameter editor (or empty placeholder) should be shown.
    /// </summary>
    public bool IsSchematicContextActive => !IsSymbolEditorActive && !IsCellActive;

    partial void OnIsSymbolEditorActiveChanged(bool value) => OnPropertyChanged(nameof(IsSchematicContextActive));
    partial void OnIsCellActiveChanged(bool value)        => OnPropertyChanged(nameof(IsSchematicContextActive));

    public PropertiesTool()
    {
        Id    = "Properties";
        Title = "Properties";

        // React to schematic selection changes so the header updates within the same tab.
        EditorVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ParameterEditorViewModel.IsEmptyState)
                && !IsSymbolEditorActive && !IsCellActive)
            {
                HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
            }
        };
    }

    // ── Context setters ───────────────────────────────────────────────────────

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm)
    {
        IsCellActive         = false;
        IsSymbolEditorActive = false;
        CellEditorVm         = null;
        EditorVm.SetContext(vm);
        SymbolInspectorVm.SetContext(null);
        HeaderText = EditorVm.IsEmptyState ? "Properties" : "Component";
    }

    /// <summary>Called by WorkspaceViewModel when the active symbol-editor document changes.</summary>
    public void SetActiveSymbolEditor(SymbolEditorViewModel? vm)
    {
        IsCellActive         = false;
        IsSymbolEditorActive = vm is not null;
        CellEditorVm         = null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(vm);
        HeaderText = vm is not null ? "Symbol" : "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active cell parameter editor changes.</summary>
    public void SetActiveCell(CellParameterEditorViewModel? vm)
    {
        IsCellActive         = vm is not null;
        IsSymbolEditorActive = false;
        CellEditorVm         = vm;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(null);
        HeaderText = vm is not null ? "Cell" : "Properties";
    }
}
