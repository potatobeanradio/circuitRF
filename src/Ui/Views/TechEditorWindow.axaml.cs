using Avalonia.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views;

public partial class TechEditorWindow : Window
{
    public TechEditorWindow()
    {
        InitializeComponent();
    }

    public TechEditorWindow(TechDocument document)
    {
        InitializeComponent();
        DataContext = document;
    }
}
