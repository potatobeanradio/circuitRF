using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views;

public partial class SymbolEditorWindow : Window
{
    public SymbolEditorWindow(SymbolEditorDocument document)
    {
        InitializeComponent();
        DataContext = document;
    }
}
