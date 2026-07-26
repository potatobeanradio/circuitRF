using Avalonia.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views;

public partial class LayoutEditorWindow : Window
{
    public LayoutEditorWindow()
    {
        InitializeComponent();
    }

    public LayoutEditorWindow(LayoutDocument document)
    {
        InitializeComponent();
        DataContext = document;
    }
}
