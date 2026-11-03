using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// brief-em3d-43 R-em3d43-6b — a 3D view's selection in the Properties Inspector. Each typed field commits on Enter or
/// when it loses focus (one undo entry), and Esc puts the field back. 3D editor round 1: moved out of the editor's own
/// panel into the application's Properties Inspector, which follows the active 3D view as it follows a layout.
/// </summary>
public partial class C3dPropertiesView : UserControl
{
    private C3dPropertiesViewModel? _vm;

    public C3dPropertiesView()
    {
        InitializeComponent();
        // 3D editor round 1 — Esc in a field cancels the edit and leaves the field.
        EscapeCancelsEdit.Attach(this);
        // brief-em3d-92 — the slider previews while it moves and writes on release: the pointer let go (its thumb captures it,
        // so the release may arrive as a lost capture) or an arrow key let go.
        TransparencySlider.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        // brief-em3d-101 — a face image's own transparency: written on release, one entry per drag.
        FaceImageTransparencySlider.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitFaceImageTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        FaceImageTransparencySlider.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitFaceImageTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        FaceImageTransparencySlider.AddHandler(KeyUpEvent, (_, _) => _vm?.CommitFaceImageTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        TransparencySlider.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        TransparencySlider.AddHandler(KeyUpEvent, (_, _) => _vm?.CommitTransparencySlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        // The clip plot's offset slider: the same preview-then-commit, one undo entry per drag.
        PlotOffsetSlider.AddHandler(PointerReleasedEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        PlotOffsetSlider.AddHandler(PointerCaptureLostEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
        PlotOffsetSlider.AddHandler(KeyUpEvent, (_, _) => _vm?.CommitPlotOffsetSlider(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RenameRequested -= OnRenameRequested;
        _vm = DataContext as C3dPropertiesViewModel;
        if (_vm is not null) _vm.RenameRequested += OnRenameRequested;
    }

    private void OnRenameRequested()
        => Dispatcher.UIThread.Post(() => { NameBox.Focus(); NameBox.SelectAll(); }, DispatcherPriority.Loaded);

    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitName(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitName();

    // brief-em3d-92 — the transparency box, and the slider's release: a drag is one undo entry, written when it ends.
    private void OnTransparencyKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitTransparencyText(); e.Handled = true; }
    }

    private void OnTransparencyLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitTransparencyText();

    // brief-em3d-83 — a field plot's clip-plane offset, and the Other frequency box.
    private void OnPlotOffsetKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlotOffset(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPlotOffsetLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPlotOffset();

    // brief-em3d-100 — a field plot's drive power.
    private void OnPlotDriveKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPlotDrive(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPlotDriveLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPlotDrive();

    private void OnPlotOtherKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitOtherFrequency(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    // 3D editor round 4 — a bond wire's loop height and span.
    private void OnWireLoopHeightKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitWireLoopHeight(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnWireLoopHeightLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitWireLoopHeight();

    private void OnWireSpanKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitWireSpan(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnWireSpanLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitWireSpan();

    private void OnRotateKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitRotate(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnRotateLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitRotate();

    private void OnOutlinePointKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: C3dOutlinePointRow row }) return;
        if (e.Key == Key.Enter) { _vm?.CommitOutlinePoint(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnOutlinePointLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: C3dOutlinePointRow row }) _vm?.CommitOutlinePoint(row);
    }

    private void OnVertexKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitVertex(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnVertexLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitVertex();

    private void OnPortKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitPort(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPortLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitPort();

    private void OnComponentPortKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitComponentPort(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnComponentPortLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitComponentPort();

    private void OnDimensionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dDimensionField f) return;
        _vm?.CommitField(f);
        e.Handled = true;
    }

    private void OnDimensionLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dDimensionField f) _vm?.CommitField(f);
    }

    // 3D editor round 3 — a bond wire's points.
    private void OnWirePointKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dWirePointRow row) return;
        _vm?.CommitWirePoint(row);
        e.Handled = true;
    }

    private void OnWirePointLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dWirePointRow row) _vm?.CommitWirePoint(row);
    }

    private void OnWirePointAddAbove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dWirePointRow row) _vm?.AddWirePoint(row, below: false);
    }

    private void OnWirePointAddBelow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dWirePointRow row) _vm?.AddWirePoint(row, below: true);
    }

    private void OnWirePointRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dWirePointRow row) _vm?.RemoveWirePoint(row);
    }

    // 3D editor round 1 — the air box's padding per axis.
    // brief-em3d-75 R-em3d75-1c — a thermal place's field: Enter or lost focus commits it, one undo entry.
    private void OnThermalFieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not C3dThermalTextField f) return;
        _vm?.CommitThermalField(f);
        e.Handled = true;
    }

    private void OnThermalFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dThermalTextField f) _vm?.CommitThermalField(f);
    }

    // A field of a few values commits on the pick; the binding is one way, so a selection cleared by a reload writes nothing.
    // After the pick, not inside it: the commit rebuilds the field list, which would tear this ComboBox down mid-selection.
    private void OnThermalFieldChoice(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string chosen, DataContext: C3dThermalTextField f } || chosen == f.Loaded) return;
        f.Text = chosen;
        Dispatcher.UIThread.Post(() => _vm?.CommitThermalField(f));
    }

    // brief-em3d-90 R-em3d90-4 — a thermal boundary's field: Enter or lost focus commits it, one undo entry.
    private void OnBoundaryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        _vm?.CommitBoundary();
        e.Handled = true;
    }

    private void OnBoundaryLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitBoundary();

    private static string FaceOf(object? sender) => (sender as Control)?.Tag as string ?? "xmin";

    private void OnPadKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.CommitAirBoxPercent(FaceOf(sender)); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Reload(); e.Handled = true; }
    }

    private void OnPadLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitAirBoxPercent(FaceOf(sender));

    // brief-em3d-101 — the Image section's width and height commit on Enter and on leaving the box.
    private void OnImageWidthKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (e.Key == Avalonia.Input.Key.Enter) (DataContext as CircuitRF.Ui.ThreeD.C3dPropertiesViewModel)?.CommitImageWidth(); }
    private void OnImageWidthLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as CircuitRF.Ui.ThreeD.C3dPropertiesViewModel)?.CommitImageWidth();
    private void OnImageHeightKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (e.Key == Avalonia.Input.Key.Enter) (DataContext as CircuitRF.Ui.ThreeD.C3dPropertiesViewModel)?.CommitImageHeight(); }
    private void OnImageHeightLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as CircuitRF.Ui.ThreeD.C3dPropertiesViewModel)?.CommitImageHeight();

    // brief-em3d-101 R-em3d101-9d — a face image's boxes commit on Enter and on leaving the box.
    private static bool Enter(Avalonia.Input.KeyEventArgs e) => e.Key == Avalonia.Input.Key.Enter;
    private void OnFaceImageTransparencyKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (Enter(e)) _vm?.CommitFaceImageTransparencyText(); }
    private void OnFaceImageTransparencyLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CommitFaceImageTransparencyText();
    private void OnFaceImageRotationKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (Enter(e)) _vm?.CommitFaceImageRotation(); }
    private void OnFaceImageRotationLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CommitFaceImageRotation();
    private void OnFaceImageWidthKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (Enter(e)) _vm?.CommitFaceImageSize(width: true); }
    private void OnFaceImageWidthLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CommitFaceImageSize(width: true);
    private void OnFaceImageHeightKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (Enter(e)) _vm?.CommitFaceImageSize(width: false); }
    private void OnFaceImageHeightLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CommitFaceImageSize(width: false);
    private void OnFaceImageOffsetKey(object? sender, Avalonia.Input.KeyEventArgs e) { if (Enter(e)) _vm?.CommitFaceImageOffset(); }
    private void OnFaceImageOffsetLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CommitFaceImageOffset();
}
