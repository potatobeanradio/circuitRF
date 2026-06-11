using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Content;

public partial class SymbolEditorView : UserControl
{
    // ── Port-count text box sync ──────────────────────────────────────────────
    // The TextBox is not bound via XAML — all two-way sync is done here so we
    // can reject non-digits at the TextInput level and strip them on paste.

    private SymbolEditorViewModel? _portCountVm;
    private bool _portCountChanging;

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

        // Port-count box: wire handlers once (control exists after InitializeComponent).
        if (this.FindControl<TextBox>("PortCountBox") is { } box)
        {
            box.AddHandler(InputElement.TextInputEvent, OnPortCountBoxTextInput,
                RoutingStrategies.Tunnel);
            box.TextChanged += OnPortCountBoxTextChanged;
        }

        DataContextChanged += OnPortCountDataContextChanged;
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<SymbolEditorCanvas>("SymbolEditorCanvasCtrl") is { } canvas)
            canvas.ZoomToFit();
    }

    // ── Port-count box handlers ───────────────────────────────────────────────

    private void OnPortCountDataContextChanged(object? sender, EventArgs e)
    {
        if (this.FindControl<TextBox>("PortCountBox") is not { } box) return;

        if (_portCountVm is not null)
            _portCountVm.PropertyChanged -= OnPortCountVmPropertyChanged;

        _portCountVm = (DataContext as SymbolEditorDocument)?.ViewModel;

        if (_portCountVm is null) return;

        _portCountChanging = true;
        box.Text = _portCountVm.PortCount.ToString();
        _portCountChanging = false;

        _portCountVm.PropertyChanged += OnPortCountVmPropertyChanged;
    }

    private void OnPortCountVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SymbolEditorViewModel.PortCount)) return;
        if (_portCountChanging || _portCountVm is null) return;
        if (this.FindControl<TextBox>("PortCountBox") is not { } box) return;

        _portCountChanging = true;
        box.Text = _portCountVm.PortCount.ToString();
        _portCountChanging = false;
    }

    // Reject non-digit characters before they enter the TextBox.
    private static void OnPortCountBoxTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not null && e.Text.Any(c => !char.IsDigit(c)))
            e.Handled = true;
    }

    // Strip any non-digits introduced by paste, then commit the value to the VM.
    private void OnPortCountBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || _portCountChanging) return;
        var vm = _portCountVm;
        if (vm is null || !vm.IsEditable) return;

        var text = tb.Text ?? "";

        // Strip non-digits from paste operations.
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits != text)
        {
            _portCountChanging = true;
            int caret = Math.Max(0, Math.Min(tb.CaretIndex, digits.Length));
            tb.Text = digits;
            tb.CaretIndex = caret;
            _portCountChanging = false;
            text = digits;
        }

        int val = string.IsNullOrEmpty(text) ? 0
                : int.TryParse(text, out int n) ? Math.Max(0, n) : 0;

        _portCountChanging = true;
        vm.PortCount = val;
        _portCountChanging = false;
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
