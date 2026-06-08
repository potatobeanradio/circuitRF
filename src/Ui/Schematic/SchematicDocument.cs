using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open schematic in a Content tab.
/// In 6d the ViewModel owns the mutable EditModel and command dispatch.
/// The read-only Model property is a convenience alias to ViewModel.RenderModel.
/// </summary>
public sealed class SchematicDocument : Document
{
    public SchematicViewModel ViewModel { get; }

    /// <summary>Message sink for posting save/error messages; null if no sink was provided at construction.</summary>
    public IMessageSink? Messages { get; init; }

    /// <summary>Current render snapshot (convenience alias for canvas binding).</summary>
    public SchematicModel? Model => ViewModel.RenderModel;

    public SchematicDocument(string cellName, SchematicViewModel viewModel)
    {
        Id       = cellName;
        Title    = cellName;
        ViewModel = viewModel;

        // Keep the Model property change notification alive so bindings update.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SchematicViewModel.RenderModel))
                OnPropertyChanged(nameof(Model));
        };
    }
}
