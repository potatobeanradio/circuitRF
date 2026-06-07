using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open schematic in a Content tab.
/// Holds the read-only SchematicModel for 6c; editing model arrives in 6d.
/// </summary>
public sealed class SchematicDocument : Document
{
    public SchematicModel Model { get; }

    public SchematicDocument(string cellName, SchematicModel model)
    {
        Id    = cellName;
        Title = cellName;
        Model = model;
    }
}
