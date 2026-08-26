using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open cell-parameter editor in a content tab.
/// Implements IUndoableDocument so the workspace routes Undo/Redo to the editor's
/// own stack while this document is active.
/// </summary>
public sealed class CellParameterEditorDocument : Document, IUndoableDocument, IFileBackedDocument
{
    public CellParameterEditorViewModel ViewModel { get; }
    public UndoRedoStack                UndoRedo  => ViewModel.UndoRedo;

    /// <summary>
    /// The cell's own <c>.ccell</c>. Unlike every other file-backed document this one is never
    /// scratch — a cell-parameter editor is only ever opened ON an existing cell, so the file
    /// exists for as long as the tab does.
    /// </summary>
    public string? FilePath => ViewModel.CcellPath;

    public CellParameterEditorDocument(string title, CellParameterEditorViewModel viewModel)
    {
        Id        = title;
        Title     = title;
        ViewModel = viewModel;
    }
}
