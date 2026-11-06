using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Nets;

/// <summary>The Nets panel's view (brief-ipx-1). The list selects several rows, so the panel is handed them all.</summary>
public partial class NetsToolView : UserControl
{
    public NetsToolView() => InitializeComponent();

    private NetsTool? _shownTool;
    private bool _applyingSelection;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowFor(DataContext as NetsTool);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ShowFor(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) ShowFor(DataContext as NetsTool);
    }

    private void ShowFor(NetsTool? tool)
    {
        if (ReferenceEquals(tool, _shownTool)) return;
        if (_shownTool is not null)
        {
            _shownTool.Panel.IsShown = false;
            _shownTool.Panel.SelectRowsRequested -= OnSelectRowsRequested;
        }
        _shownTool = tool;
        if (tool is not null)
        {
            tool.Panel.SelectRowsRequested += OnSelectRowsRequested;
            tool.Panel.IsShown = true;
            // A tab brought back to the front makes a new view of a panel that kept its selection.
            OnSelectRowsRequested(tool.Panel.SelectedRows);
        }
    }

    private void OnRowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingSelection) return;
        if (sender is ListBox list && DataContext is NetsTool { Panel: { } panel })
            panel.SetSelectedRows([.. (list.SelectedItems ?? Array.Empty<object>()).OfType<LayoutNetRow>()]);
    }

    private void OnSelectRowsRequested(IReadOnlyList<LayoutNetRow> rows)
    {
        if (RowsList.SelectedItems is not { } selected) return;
        _applyingSelection = true;
        try
        {
            selected.Clear();
            foreach (var row in rows) selected.Add(row);
        }
        finally { _applyingSelection = false; }
    }
}
