using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CircuitRF.Render;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.Views.Viewer3D;

/// <summary>
/// brief-em3d-28 — the 3D view's shell. Wires the pane's frames to the overlay's redraw, shows a
/// present fault in the pane's place, scrolls the object tree to what a click selected, and follows the
/// application theme: a light/dark switch or a new theme regenerates the scene, because colours are
/// baked into the vertices (a regeneration, not a per-frame cost).
/// </summary>
public partial class Viewer3DView : UserControl
{
    private Viewer3DViewModel? _vm;

    public Viewer3DView()
    {
        InitializeComponent();
        Pane.FramePresented += () => Overlay.InvalidateVisual();
        Pane.FaultChanged += why =>
        {
            FaultText.Text = why is null ? "" : "The 3D view cannot draw here: " + why;
            FaultText.IsVisible = why is not null;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.RevealRequested -= Reveal;
        _vm = (DataContext as Viewer3DDocument)?.ViewModel;
        if (_vm is not null)
        {
            _vm.RevealRequested += Reveal;
            ApplyBackground();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        ActualThemeVariantChanged += OnVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeService.ThemeChanged -= OnThemeChanged;
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _vm?.Invalidate();

    private void OnVariantChanged(object? sender, EventArgs e)
    {
        ApplyBackground();
        _vm?.Invalidate();
    }

    private void ApplyBackground()
    {
        if (_vm is null) return;
        _vm.View.Background = ThemeService.CurrentVariant == ColorVariant.Dark ? (0.12f, 0.13f, 0.15f) : (0.93f, 0.94f, 0.96f);
    }

    /// <summary>ItemsControl.ScrollIntoView resolves an item among the TOP-level items only, and a leaf
    /// sits under a group whose children are not realized until it expands — so the group is expanded
    /// first and the leaf's container brought into view once layout has made it.</summary>
    private void Reveal(Viewer3DTreeItem item)
    {
        foreach (var c in ObjectTree.GetRealizedContainers())
            if (c is TreeViewItem group && group.DataContext is Viewer3DTreeGroup g && g.Items.Contains(item))
            {
                group.IsExpanded = true;
                Dispatcher.UIThread.Post(() => group.ContainerFromItem(item)?.BringIntoView(), DispatcherPriority.Loaded);
                return;
            }
    }
}
