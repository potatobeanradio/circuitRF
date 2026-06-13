using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views;

public partial class SymbolEditorWindow : Window
{
    public SymbolEditorWindow()
    {
        InitializeComponent();
    }

    public SymbolEditorWindow(SymbolEditorDocument document)
    {
        InitializeComponent();
        DataContext = document;
    }
}
