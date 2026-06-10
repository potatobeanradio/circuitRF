using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    public ProjectTreeView()
    {
        InitializeComponent();
    }

    // ── On-focus refresh (Layer 4) ─────────────────────────────────────────────

    private bool _refreshPending;
    private Window? _attachedWindow;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_attachedWindow is not null)
            _attachedWindow.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attachedWindow is not null)
        {
            _attachedWindow.Activated -= OnWindowActivated;
            _attachedWindow = null;
        }
    }

    // Debounced re-scan on window focus: guards against rapid consecutive Activated events.
    private void OnWindowActivated(object? sender, System.EventArgs e)
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (DataContext is ProjectTreeTool tool)
                tool.Refresh();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Double-click → open/activate the selected node ────────────────────────

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool) return;
        if (tool.SelectedItem is not ProjectTreeNodeViewModel node) return;
        node.ActivateCommand.Execute(null);
    }
}
