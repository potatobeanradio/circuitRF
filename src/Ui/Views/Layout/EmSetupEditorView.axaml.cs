using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Layout;

/// <summary>
/// Code-behind for the <c>.cem</c> EM setup editor. Every staged field commits through one generic
/// dispatcher keyed by the control's <see cref="Control.Tag"/>, mirroring
/// <see cref="TechEditorView"/> — one handler pair rather than one per field.
/// </summary>
public partial class EmSetupEditorView : UserControl
{
    public EmSetupEditorView()
    {
        InitializeComponent();
        EditTechButton.Click += OnEditTechnologyClick;
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitField(sender);
            e.Handled = true;
        }
    }

    private void CommitField(object? sender)
    {
        if (sender is not Control c || c.Tag is not string tag) return;
        if (DataContext is not EmSetupDocument doc) return;
        var vm = doc.ViewModel;

        switch (tag)
        {
            case "Port1Z0": vm.CommitPortZ0(1); break;
            case "Port2Z0": vm.CommitPortZ0(2); break;
            default:        vm.CommitMeshField(tag); break;
        }
    }

    // R-cpl-6 — the per-port list. Its rows are their own DataContext (EmPortZ0Row), so they need
    // their own handler pair rather than the Tag-keyed CommitField dispatcher above: the index the
    // VM commits is the row's position, which only the collection knows.

    private void OnPortRowLostFocus(object? sender, RoutedEventArgs e) => CommitPortRow(sender);

    private void OnPortRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitPortRow(sender);
            e.Handled = true;
        }
    }

    private void CommitPortRow(object? sender)
    {
        if (sender is not Control { DataContext: EmPortZ0Row row }) return;
        if (DataContext is not EmSetupDocument doc) return;
        int index = doc.ViewModel.PortRows.IndexOf(row);
        if (index >= 0) doc.ViewModel.CommitPortRow(index);
    }

    /// <summary>
    /// R-em-12 — the stackup is shown here, edited in the <c>.ctech</c>. Reaches the workspace the
    /// same way the Layout Editor's own Technology ▾ ▸ Edit does (this view's DataContext is an
    /// <see cref="EmSetupDocument"/>, not the workspace), rather than duplicating a stackup editor.
    /// </summary>
    private void OnEditTechnologyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmSetupDocument doc) return;
        if (doc.ViewModel.ResolveLayout?.Invoke(doc.ViewModel.Working.LayoutRef) is not { } source) return;
        if (source.Technology is null) return;

        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var ws = desktop.Windows
            .Select(w => w.DataContext)
            .OfType<WorkspaceViewModel>()
            .FirstOrDefault();
        if (ws?.ResolvedTechPathFor(source.AbsolutePath) is { } techPath)
            ws.OpenTechnologyDocument(techPath);
    }
}
