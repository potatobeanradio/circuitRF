using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Layout;

public partial class LayoutEditorView : UserControl
{
    public LayoutEditorView()
    {
        InitializeComponent();

        LayoutCanvasCtrl.ViewportChanged     += (_, _) => SyncRulers();
        LayoutCanvasCtrl.LayoutUpdated       += (_, _) => SyncRulers();
        LayoutCanvasCtrl.CursorWorldChanged  += OnCanvasCursorWorldChanged;
        LayoutCanvasCtrl.FrameUnknownLayers  += OnFrameUnknownLayers;

        DataContextChanged += (_, _) => SyncRulerUnits();
    }

    private void SyncRulers()
    {
        HRuler.SetViewport(LayoutCanvasCtrl.CurrentPanX, LayoutCanvasCtrl.CurrentPanY, LayoutCanvasCtrl.CurrentZoom,
            LayoutCanvasCtrl.Bounds.Width, LayoutCanvasCtrl.Bounds.Height);
        VRuler.SetViewport(LayoutCanvasCtrl.CurrentPanX, LayoutCanvasCtrl.CurrentPanY, LayoutCanvasCtrl.CurrentZoom,
            LayoutCanvasCtrl.Bounds.Width, LayoutCanvasCtrl.Bounds.Height);
    }

    // Switching the display-unit combo relabels both rulers and moves no geometry (L0b's invariant,
    // now visible) — re-read the VM's current DisplayUnit whenever the document (re)binds.
    private void SyncRulerUnits()
    {
        if (DataContext is not LayoutDocument doc) return;
        HRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
        VRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);

        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayoutEditorViewModel.DisplayUnit))
            {
                HRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
                VRuler.SetUnits(doc.ViewModel.Model.DbuPerMicron, doc.ViewModel.DisplayUnit);
            }
        };
    }

    private void OnCanvasCursorWorldChanged(object? sender, (double X, double Y)? world)
    {
        HRuler.SetCursorWorld(world?.X);
        VRuler.SetCursorWorld(world?.Y);
        if (DataContext is LayoutDocument doc)
            doc.ViewModel.SetCursorWorld(world?.X, world?.Y);
    }

    private void OnFrameUnknownLayers(IReadOnlyList<LayerKey> keys)
    {
        if (keys.Count == 0) return;
        if (DataContext is LayoutDocument doc)
            doc.ViewModel.ReportUnknownLayers(keys);
    }

    private void OnZoomToFit(object? sender, RoutedEventArgs e) => LayoutCanvasCtrl.ZoomToFit();
    private void OnZoomIn(object? sender, RoutedEventArgs e)    => LayoutCanvasCtrl.ZoomIn();
    private void OnZoomOut(object? sender, RoutedEventArgs e)   => LayoutCanvasCtrl.ZoomOut();
    private void OnZoom1To1(object? sender, RoutedEventArgs e)  => LayoutCanvasCtrl.Zoom1To1();

    // ── Toolbar field commit (§1 R6 typed entry — LostFocus commits; Enter commits + refocuses canvas) ──

    private LayoutEditorViewModel? Vm => (DataContext as LayoutDocument)?.ViewModel;

    private void OnCornerRadiusCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitCornerRadiusText(tb.Text ?? "");
    }
    private void OnCornerRadiusKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitCornerRadiusText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    private void OnPathWidthCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitPathWidthText(tb.Text ?? "");
    }
    private void OnPathWidthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitPathWidthText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    private void OnLabelHeightCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitLabelHeightText(tb.Text ?? "");
    }
    private void OnLabelHeightKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox tb) { Vm?.CommitLabelHeightText(tb.Text ?? ""); LayoutCanvasCtrl.Focus(); }
    }

    // Live Rect W/H — gate 9: typing a value commits the shape at exactly that size. Both fields
    // stage first (CommitDrawWidthText/CommitDrawHeightText), Enter finalizes (CommitTypedRect).
    private void OnDrawWidthCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitDrawWidthText(tb.Text ?? "");
    }
    private void OnDrawWidthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox tb) return;
        Vm?.CommitDrawWidthText(tb.Text ?? "");
        Vm?.CommitTypedRect();
        LayoutCanvasCtrl.Focus();
    }

    private void OnDrawHeightCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) Vm?.CommitDrawHeightText(tb.Text ?? "");
    }
    private void OnDrawHeightKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox tb) return;
        Vm?.CommitDrawHeightText(tb.Text ?? "");
        Vm?.CommitTypedRect();
        LayoutCanvasCtrl.Focus();
    }
}
