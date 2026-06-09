using Avalonia.Controls;
using CircuitRF.Ui.Controls;

namespace CircuitRF.Ui.Views.Content;

public partial class SymbolEditorView : UserControl
{
    public SymbolEditorView()
    {
        InitializeComponent();
    }

    private void OnZoomToFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.FindControl<SymbolEditorCanvas>("SymbolEditorCanvasCtrl") is { } canvas)
            canvas.ZoomToFit();
    }
}
