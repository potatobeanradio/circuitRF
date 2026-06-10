using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open cell-parameter editor in a content tab.
/// Implements IUndoableDocument so the workspace routes Undo/Redo to the editor's
/// own stack while this document is active.
/// </summary>
public sealed class CellParameterEditorDocument : Document, IUndoableDocument
{
    public CellParameterEditorViewModel ViewModel { get; }
    public UndoRedoStack                UndoRedo  => ViewModel.UndoRedo;

    public CellParameterEditorDocument(string title, CellParameterEditorViewModel viewModel)
    {
        Id        = title;
        Title     = title;
        ViewModel = viewModel;
    }
}
