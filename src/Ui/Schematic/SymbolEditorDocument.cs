using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open Symbol Editor session.
/// Hosted as a dockable document tab; the same ViewModel can also be
/// displayed in a tear-off <see cref="Views.SymbolEditorWindow"/>.
/// </summary>
public sealed class SymbolEditorDocument : Document, IUndoableDocument
{
    public SymbolEditorViewModel ViewModel { get; }
    public UndoRedoStack         UndoRedo  => ViewModel.UndoRedo;

    public SymbolEditorDocument(string title, SymbolEditorViewModel viewModel)
    {
        Id    = title;
        Title = title;
        ViewModel = viewModel;
    }
}
