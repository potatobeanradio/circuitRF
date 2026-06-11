using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Content;

public partial class SymbolEditorView : UserControl
{
    public SymbolEditorView()
    {
        InitializeComponent();

        // Focus-independent shortcut handler — mirrors SchematicView.OnViewKeyDownTunnel.
        // Toolbar button clicks steal focus; Window.KeyBindings mark Escape handled before
        // visual-tree routing, so a plain OnKeyDown override never fires.
        this.AddHandler(
            InputElement.KeyDownEvent,
            OnViewKeyDownTunnel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<SymbolEditorCanvas>("SymbolEditorCanvasCtrl") is { } canvas)
            canvas.ZoomToFit();
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsKeyboardFocusWithin) return;
        var vm = (DataContext as SymbolEditorDocument)?.ViewModel;
        if (vm is null) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl) return;

        switch (e.Key)
        {
            case Key.Escape:
                // Delegate to VM — handles text mode, pin mode, and general Esc correctly.
                vm.OnKeyDown(e.Key, e.KeyModifiers);
                e.Handled = true;
                break;
            case Key.S:
                vm.SetActiveToolCommand.Execute("Select");
                e.Handled = true;
                break;
            case Key.F:
                if (this.FindControl<SymbolEditorCanvas>("SymbolEditorCanvasCtrl") is { } canvas)
                    canvas.ZoomToFit();
                e.Handled = true;
                break;
        }
    }
}
