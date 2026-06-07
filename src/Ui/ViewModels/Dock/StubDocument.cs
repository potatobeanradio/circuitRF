using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Placeholder Content tab (Document in the DocumentDock). In 6c this is replaced by the
/// real schematic canvas document; in 6d by symbol-editor and data-display documents.
/// Each stub carries a label and a kind hint so the view can show the right placeholder.
/// </summary>
public class StubDocument : Document
{
    public enum StubKind { Welcome, Schematic, SymbolEditor, DataDisplay }

    public StubKind Kind { get; }
    public string Label { get; }

    public StubDocument(string title = "Welcome", StubKind kind = StubKind.Welcome)
    {
        Id    = title;
        Title = title;
        Kind  = kind;
        Label = kind switch
        {
            StubKind.Welcome      => "Welcome to circuitRF",
            StubKind.Schematic    => $"Schematic: {title}",
            StubKind.SymbolEditor => $"Symbol: {title}",
            StubKind.DataDisplay  => $"Data Display: {title}",
            _                     => title,
        };
    }
}
