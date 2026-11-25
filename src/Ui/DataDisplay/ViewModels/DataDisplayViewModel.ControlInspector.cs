// ================================================================
//  DataDisplayViewModel.ControlInspector.cs  —  the tab's control inspector
//  (brief-ddl-5 R-ddl5-7)
//
//  One per tab, made the first time the Properties panel asks for it, as
//  the annotation inspector is one per tab.
// ================================================================

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataDisplayViewModel
{
    private ControlInspectorViewModel? _controlInspector;

    /// <summary>Edits the one selected control; hidden while none is (<see cref="SelectedControl"/>).</summary>
    public ControlInspectorViewModel ControlInspector => _controlInspector ??= new(this);

    private SelectionInspectorViewModel? _selectionInspector;

    /// <summary>Edits what two or more selected items have in common; hidden otherwise.</summary>
    public SelectionInspectorViewModel SelectionInspector => _selectionInspector ??= new(this);
}
